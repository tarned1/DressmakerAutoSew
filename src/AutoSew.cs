using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DressmakerAutoSew
{
    /// <summary>
    /// Auto sew: sews a seam by itself, with the animation, and finishes it at 100% quality.
    /// How it works: when a seam opens, Arm reads the seam's path as a list of points. Every frame,
    /// Tick moves a distance along that path, moves the fabric so the needle follows it, draws the
    /// stitches and the progress, and at the end calls the game's own Finish(true).
    /// It is a Harmony prefix on SewingController.Update that returns false while it runs, so the
    /// game's normal sewing code is skipped for that frame.
    /// </summary>
    public static class AutoSew
    {
        /// <summary>
        /// Everything about the seam currently being sewn: the path, how far along it is,
        /// the speed, and the state of the animated sewing hand.
        /// </summary>
        sealed class Run
        {
            public MonoBehaviour Owner;
            /// <summary>
            /// The seam path as points, and the running total length up to each point
            /// (Cumulative[i] = length from the start to point i), used to find a position by distance.
            /// </summary>
            public List<Vector2> Points;
            public float[] Cumulative;
            public float Length;
            /// <summary>
            /// How far along the seam the needle is, in the same units as Length.
            /// </summary>
            public float Distance;
            public float StitchDistance;
            public float Pace;
            public int DrawnCount;
            public float DrawnThrough;
            public Component Hand;
            public Vector3 HandRestPosition;
            public Quaternion HandRestRotation;
            public Vector3 HandLocalPoint;
            public Quaternion HandLocalRotation;
            public object HandWasEngaged;
            public bool HandReady;
            public bool HandFailed;
            public bool HandReturning;
            public int HandFeedFrame = -1;
        }

        /// <summary>
        /// The seam being sewn, or null when nothing is running.
        /// </summary>
        static Run _run;
        /// <summary>
        /// A sewing screen where auto sew failed. It is left to the game so the player can sew by hand.
        /// </summary>
        static MonoBehaviour _failedController;
        /// <summary>
        /// Guard against the hook running inside itself (re-entrancy).
        /// </summary>
        static bool _inTick;
        /// <summary>
        /// Cached handles to the game's SewingController fields and methods. Bind fills them once per type,
        /// so each frame doesn't repeat slow name lookups.
        /// </summary>
        static FieldInfo _finished;
        static FieldInfo _patternLine;
        static FieldInfo _fabric;
        static FieldInfo _hand;
        static FieldInfo _speedControl;
        static FieldInfo _animator;
        static FieldInfo _fabricSpeedMax;
        static FieldInfo _stitchDistance;
        static FieldInfo _stitchLine;
        static FieldInfo _patternRenderer;
        static FieldInfo _currentFabricSpeed;
        static FieldInfo _accuracy;
        static FieldInfo _mistakes;
        static MethodInfo _finish;
        static MethodInfo _restart;
        static MethodInfo _accuracyText;
        static MethodInfo _stitchSounds;
        static PropertyInfo _canSew;

        /// <summary>
        /// Harmony postfix on SewingController.InitializeWith: a new seam was opened, so start sewing it.
        /// __instance is the game's SewingController (Harmony fills this parameter by name).
        /// </summary>
        public static void InitializePostfix(MonoBehaviour __instance)
        {
            try
            {
                if (!Plugin.Enabled)
                    return;
                _failedController = null;
                ReleaseRun();
                Arm(__instance, resetSegment: false);
            }
            catch (Exception ex)
            {
                ReleaseRun();
                _failedController = __instance;
                Plugin.Log.LogError("Could not start auto sew: " + ex);
            }
        }

        /// <summary>
        /// Harmony prefix on SewingController.Update, every frame on the sewing screen.
        /// Returns false to skip the game's own update while auto sew is sewing, true to let the game run.
        /// Any error stops auto sew for this seam and hands it back to the game.
        /// </summary>
        public static bool UpdatePrefix(MonoBehaviour __instance)
        {
            try
            {
                return !Tick(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Auto sew stopped this frame: " + ex);
                ReleaseRun();
                _failedController = __instance;
                return true;
            }
        }

        /// <summary>
        /// One frame of auto sewing. Returns true when auto sew handled the frame.
        /// Steps: checks that sewing is allowed (not paused, no note on screen, not finished), reads the
        /// speed keys and R (restart), moves the needle forward by maxFeed x speed x frame time, then
        /// places the fabric, draws stitches, updates progress and visuals, and finishes at the end.
        /// </summary>
        static bool Tick(MonoBehaviour controller)
        {
            if (_inTick)
                return _run != null;
            _inTick = true;
            try
            {
                Plugin.FlushSpeed(force: false);
                Bind(controller.GetType());
                if (_run != null)
                    _run.HandFeedFrame = -1;

                if (_failedController == controller)
                    return false;
                if (_run != null && !ReferenceEquals(_run.Owner, controller))
                    ReleaseRun();

                if (ShowingNote(controller))
                    return false;

                if (!Plugin.Enabled)
                {
                    ReleaseRun();
                    return false;
                }

                if (Finished(controller))
                {
                    ReleaseRun();
                    return false;
                }
                if (Time.timeScale <= 0f)
                    return false;

                if (_run == null)
                {
                    if (!CanSew(controller))
                        return false;
                    Arm(controller, resetSegment: true);
                    if (_run == null)
                        return true;
                }

                ReadSpeedKeys();

                if (Input.GetKeyDown(KeyCode.R))
                {
                    _restart?.Invoke(controller, null);
                    ClearScore(controller);
                    if (_run == null)
                    {
                        Arm(controller, resetSegment: false);
                        if (_run == null)
                            return false;
                    }
                    _run.Distance = 0f;
                    _run.DrawnCount = 0;
                    _run.DrawnThrough = 0f;
                    _run.HandReady = false;
                }

                float maxFeed = ReadFloat(_fabricSpeedMax, controller, 0.05f);
                if (float.IsNaN(maxFeed) || float.IsInfinity(maxFeed) || maxFeed < 0.0001f)
                    maxFeed = 0.05f;
                float previousDistance = _run.Distance;
                _run.Distance += maxFeed * _run.Pace * Time.deltaTime;
                bool done = _run.Distance >= _run.Length - 0.0001f;
                if (done)
                    _run.Distance = _run.Length;

                Place(controller, _run.Distance);
                DrawStitches(controller, _run);
                SetProgress(controller, _run.Length <= 0f ? 1f : _run.Distance / _run.Length);
                SetFeedVisuals(controller, maxFeed, _run.Pace);
                if (!_run.HandReady)
                    CaptureHand(controller, _run);
                if (_run.Distance > previousDistance + 0.00000001f)
                    _run.HandFeedFrame = Time.frameCount;

                if (done)
                    Complete(controller);

                return true;
            }
            finally
            {
                _inTick = false;
            }
        }

        /// <summary>
        /// Prepares a seam: reads its path, computes the cumulative lengths, clears the score and creates
        /// the Run. A seam without a usable path is marked finished at 100% straight away.
        /// </summary>
        static void Arm(MonoBehaviour controller, bool resetSegment)
        {
            Bind(controller.GetType());
            if (!CanSew(controller))
                return;

            if (resetSegment)
                _restart?.Invoke(controller, null);

            var points = ReadPath(_patternLine.GetValue(controller));
            ClearScore(controller);
            _accuracyText?.Invoke(controller, null);

            if (points.Count < 2)
            {
                Plugin.Log.LogWarning("This seam has no stitch path. Marking it finished at 100%.");
                Complete(controller);
                return;
            }

            var cumulative = new float[points.Count];
            for (int i = 1; i < points.Count; i++)
                cumulative[i] = cumulative[i - 1] + Vector2.Distance(points[i - 1], points[i]);
            if (float.IsNaN(cumulative[cumulative.Length - 1]) || float.IsInfinity(cumulative[cumulative.Length - 1]))
                throw new InvalidOperationException("The seam has an invalid length.");

            float stitch = ReadFloat(_stitchDistance, controller, 0.01f);
            if (float.IsNaN(stitch) || float.IsInfinity(stitch) || stitch < 0.0001f)
                stitch = 0.01f;

            _run = new Run
            {
                Owner = controller,
                Points = points,
                Cumulative = cumulative,
                Length = cumulative[cumulative.Length - 1],
                Distance = 0f,
                StitchDistance = stitch,
                Pace = Mathf.Clamp01(Plugin.Speed)
            };

            if (_run.Length <= 0.0001f)
            {
                Complete(controller);
                return;
            }

            Place(controller, 0f);
            CaptureHand(controller, _run);
            Plugin.Log.LogInfo("Auto sewing a seam (" + _run.Length.ToString("0.00") + " units) at machine speed " + Plugin.Speed.ToString("0.00") + ".");
        }

        /// <summary>
        /// Ends the seam: clears mistakes and calls the game's Finish(true), which records 100% quality.
        /// </summary>
        static void Complete(MonoBehaviour controller)
        {
            ReleaseRun();
            ClearScore(controller);
            _accuracyText?.Invoke(controller, null);
            if (_finish == null)
            {
                _failedController = controller;
                Plugin.Log.LogError("SewingController.Finish is missing.");
                return;
            }

            try
            {
                _finish.Invoke(controller, new object[] { true });
            }
            catch (TargetInvocationException ex)
            {
                _failedController = controller;
                Plugin.Log.LogError("Finishing the seam failed: " + (ex.InnerException ?? ex));
            }
        }

        /// <summary>
        /// W / Up and S / Down change speed while held; the mouse wheel changes it in steps.
        /// </summary>
        static void ReadSpeedKeys()
        {
            if (_run == null)
                return;
            _run.Pace = Mathf.Clamp01(Plugin.Speed);
            float delta = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
                delta += Time.unscaledDeltaTime;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
                delta -= Time.unscaledDeltaTime;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
                delta += wheel * 0.08f;
            if (Mathf.Abs(delta) < 0.0001f)
                return;
            Plugin.SetSpeed(_run.Pace + delta, saveNow: false);
            _run.Pace = Plugin.Speed;
        }

        /// <summary>
        /// Moves and rotates the fabric so the point at this distance along the seam sits under the needle,
        /// with the seam's direction lined up with the machine.
        /// </summary>
        static void Place(MonoBehaviour controller, float distance)
        {
            var fabric = _fabric.GetValue(controller) as Component;
            if (fabric == null)
                return;

            Sample(_run.Points, _run.Cumulative, distance, out Vector2 point, out Vector2 tangent);
            Transform transform = fabric.transform;
            transform.localRotation = Quaternion.identity;
            transform.localPosition = new Vector3(-point.x, 0f, -point.y);
            Vector3 worldPoint = transform.TransformPoint(new Vector3(point.x, 0f, point.y));
            Vector3 worldAhead = transform.TransformPoint(new Vector3(point.x + tangent.x, 0f, point.y + tangent.y));
            float angle = Vector3.SignedAngle(Vector3.forward, worldAhead - worldPoint, Vector3.up);
            transform.RotateAround(Vector3.zero, Vector3.up, 180f - angle);
        }

        /// <summary>
        /// Finds the position and direction at a distance along the path: locates the segment containing
        /// that distance and interpolates (Lerp) between its two end points.
        /// </summary>
        static void Sample(List<Vector2> points, float[] cumulative, float distance, out Vector2 point, out Vector2 tangent)
        {
            float length = cumulative[cumulative.Length - 1];
            if (distance < 0f)
                distance = 0f;
            if (distance > length)
                distance = length;

            // Cumulative is sorted, so the segment is the last index whose
            // prefix length is still at or behind the needle.
            int segment = SegmentIndex(cumulative, distance);
            float span = cumulative[segment + 1] - cumulative[segment];
            float t = span < 0.0000001f ? 0f : (distance - cumulative[segment]) / span;
            point = Vector2.Lerp(points[segment], points[segment + 1], t);
            tangent = points[segment + 1] - points[segment];
            if (tangent.sqrMagnitude < 0.0000001f)
                tangent = Vector2.up;
            else
                tangent.Normalize();
        }

        /// <summary>
        /// Binary search: the last segment whose start is at or before the distance.
        /// Takes log2(n) steps instead of checking every segment.
        /// </summary>
        static int SegmentIndex(float[] cumulative, float distance)
        {
            int lo = 0;
            int hi = cumulative.Length - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (cumulative[mid] <= distance)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        /// <summary>
        /// Draws the stitch line. Stitches sit at fixed intervals along the seam; only the tip follows the
        /// needle. Points already drawn are not recomputed, unless the seam was restarted.
        /// </summary>
        static void DrawStitches(MonoBehaviour controller, Run run)
        {
            var line = _stitchLine.GetValue(controller) as LineRenderer;
            if (line == null || run.Length <= 0f || run.StitchDistance <= 0.0001f)
                return;

            line.enabled = true;

            // Stitches stay at fixed distances. Only the tip moves until the
            // next interval, then that tip is kept and a new one starts.
            int committed = Mathf.FloorToInt(run.Distance / run.StitchDistance);
            int count = committed + 1;
            if (count < 2)
                count = 2;
            if (count > 8192)
                count = 8192;

            bool restarted = run.Distance + 0.0001f < run.DrawnThrough;
            if (restarted)
                run.DrawnCount = 0;

            if (line.positionCount != count)
                line.positionCount = count;

            int firstNew = restarted ? 0 : run.DrawnCount;
            int tip = count - 1;
            for (int i = firstNew; i < tip; i++)
            {
                Sample(run.Points, run.Cumulative, i * run.StitchDistance, out Vector2 point, out _);
                line.SetPosition(i, new Vector3(point.x, point.y, 0f));
            }

            Sample(run.Points, run.Cumulative, Mathf.Min(run.Distance, run.Length), out Vector2 tipPoint, out _);
            line.SetPosition(tip, new Vector3(tipPoint.x, tipPoint.y, 0f));
            run.DrawnCount = tip;
            run.DrawnThrough = run.Distance;
        }

        /// <summary>
        /// Shows how much of the seam is done, through the pattern line's shader value _Progress.
        /// </summary>
        static void SetProgress(MonoBehaviour controller, float progress)
        {
            var renderer = _patternRenderer.GetValue(controller) as Renderer;
            if (renderer == null)
                return;
            renderer.enabled = true;
            renderer.material.SetFloat("_Progress", Mathf.Clamp01(progress));
        }

        /// <summary>
        /// Keeps the visuals in sync with the speed: the feed speed value, the speed lever, the stitching
        /// sounds and the machine's animation speed. These are cosmetic, so their errors are ignored.
        /// </summary>
        static void SetFeedVisuals(MonoBehaviour controller, float maxFeed, float pace)
        {
            _currentFabricSpeed?.SetValue(controller, maxFeed * pace);

            var speedControl = _speedControl?.GetValue(controller);
            var setting = speedControl?.GetType().GetProperty("SpeedSetting");
            try
            {
                setting?.SetValue(speedControl, pace);
            }
            catch (Exception)
            {
                // The lever is only a readout. Sewing continues if its clamps are not ready.
            }

            try
            {
                _stitchSounds?.Invoke(controller, null);
            }
            catch (Exception)
            {
            }

            var animator = _animator?.GetValue(controller) as Animator;
            if (animator != null)
                animator.SetFloat("Speed", pace);
        }

        /// <summary>
        /// Stops auto sew and puts the hand back. Called when the switch is turned off.
        /// </summary>
        public static void Cancel()
        {
            ReleaseRun();
            _failedController = null;
        }

        /// <summary>
        /// Forgets the current seam and restores the hand to where it started.
        /// </summary>
        static void ReleaseRun()
        {
            Run previous = _run;
            _run = null;
            RestoreHand(previous);
        }

        /// <summary>
        /// Puts the sewing hand back to its saved position, rotation and "engaged" state.
        /// </summary>
        static void RestoreHand(Run previous)
        {
            if (previous == null || previous.Hand == null)
                return;
            try
            {
                previous.Hand.transform.position = previous.HandRestPosition;
                previous.Hand.transform.rotation = previous.HandRestRotation;
                if (previous.HandWasEngaged != null)
                    GameAccess.SetMember(previous.Hand, "engaged", previous.HandWasEngaged);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not restore the sewing hand: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Remembers the hand's resting pose and where it holds the fabric (in the fabric's own coordinates),
        /// so LateTick can make it follow the fabric. Skipped if the hand is part of the fabric's hierarchy,
        /// because moving one would move the other.
        /// </summary>
        static void CaptureHand(MonoBehaviour controller, Run run)
        {
            if (run.HandFailed)
                return;
            var hand = _hand?.GetValue(controller) as Component;
            var fabric = _fabric?.GetValue(controller) as Component;
            if (hand == null || fabric == null)
                return;
            // Never move a fabric ancestor or descendant independently.
            if (hand.transform == fabric.transform || hand.transform.IsChildOf(fabric.transform)
                || fabric.transform.IsChildOf(hand.transform))
                return;
            if (run.Hand == null)
            {
                run.Hand = hand;
                run.HandRestPosition = hand.transform.position;
                run.HandRestRotation = hand.transform.rotation;
                run.HandWasEngaged = GameAccess.Member(hand, "engaged");
            }
            run.HandLocalPoint = fabric.transform.InverseTransformPoint(hand.transform.position);
            run.HandLocalRotation = Quaternion.Inverse(fabric.transform.rotation) * hand.transform.rotation;
            run.HandReady = true;
        }

        /// <summary>
        /// Runs after all Update calls each frame. Makes the hand follow the fabric while it feeds, smoothly
        /// (exponential blending). If the hand would reach too far (0.22 units), or feeding stops, the hand
        /// returns to its start and takes a new grip, like a real hand re-gripping cloth.
        /// </summary>
        // Apply after ordinary Update calls, so mouse-driven hand updates cannot
        // overwrite the automated pose during the same frame.
        public static void LateTick()
        {
            Run run = _run;
            if (!Plugin.Enabled || (run != null && (run.Owner == null || !run.Owner.gameObject.activeInHierarchy)))
            {
                ReleaseRun();
                return;
            }
            if (run == null || !run.HandReady || run.Hand == null)
                return;
            var fabric = _fabric?.GetValue(run.Owner) as Component;
            if (fabric == null)
                return;
            try
            {
                if (Time.timeScale <= 0f || Plugin.Speed <= 0.0001f || run.HandFeedFrame != Time.frameCount)
                {
                    ReturnHandToStart(run);
                    return;
                }
                if (run.HandReturning)
                {
                    // Re-grip at the starting pose against the cloth's new pose.
                    run.Hand.transform.position = run.HandRestPosition;
                    run.Hand.transform.rotation = run.HandRestRotation;
                    run.HandLocalPoint = fabric.transform.InverseTransformPoint(run.HandRestPosition);
                    run.HandLocalRotation = Quaternion.Inverse(fabric.transform.rotation) * run.HandRestRotation;
                    run.HandReturning = false;
                    GameAccess.SetMember(run.Hand, "engaged", true);
                    return;
                }
                Vector3 desired = fabric.transform.TransformPoint(run.HandLocalPoint);
                desired.y = run.HandRestPosition.y;
                Vector3 offset = desired - run.HandRestPosition;
                const float reach = 0.22f;
                if (offset.sqrMagnitude > reach * reach)
                {
                    ReturnHandToStart(run);
                    return;
                }
                Quaternion desiredRotation = fabric.transform.rotation * run.HandLocalRotation;
                desiredRotation = Quaternion.RotateTowards(run.HandRestRotation, desiredRotation, 25f);
                float blend = 1f - Mathf.Exp(-12f * Time.deltaTime);
                run.Hand.transform.position = Vector3.Lerp(run.Hand.transform.position, desired, blend);
                run.Hand.transform.rotation = Quaternion.Slerp(run.Hand.transform.rotation, desiredRotation, blend);
                GameAccess.SetMember(run.Hand, "engaged", true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Sewing hand animation stopped: " + ex.GetBaseException().Message);
                RestoreHand(run);
                run.HandReady = false;
                run.HandFailed = true;
            }
        }

        /// <summary>
        /// Moves the hand back to its resting pose and marks it as re-gripping.
        /// </summary>
        static void ReturnHandToStart(Run run)
        {
            run.Hand.transform.position = run.HandRestPosition;
            run.Hand.transform.rotation = run.HandRestRotation;
            GameAccess.SetMember(run.Hand, "engaged", false);
            run.HandReturning = true;
        }

        /// <summary>
        /// Clears the game's accuracy samples and mistake count, so the seam scores 100%.
        /// </summary>
        static void ClearScore(MonoBehaviour controller)
        {
            if (_accuracy?.GetValue(controller) is IList accuracy)
                accuracy.Clear();
            _mistakes?.SetValue(controller, 0);
        }

        /// <summary>
        /// Reads the game's CanSew property. False if it is missing or throws.
        /// </summary>
        static bool CanSew(MonoBehaviour controller)
        {
            if (_canSew == null)
                return false;
            try
            {
                return (bool)_canSew.GetValue(controller);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the game says this seam is already finished.
        /// </summary>
        static bool Finished(MonoBehaviour controller)
        {
            return _finished != null && (bool)_finished.GetValue(controller);
        }

        /// <summary>
        /// True while the game shows a note on the sewing screen. Auto sew waits until it is closed.
        /// This looks the field up by name every frame, and logs a warning in current game versions,
        /// where _isShowingNote no longer exists.
        /// </summary>
        static bool ShowingNote(MonoBehaviour controller)
        {
            var field = AccessTools.Field(controller.GetType(), "_isShowingNote");
            return field != null && (bool)field.GetValue(controller);
        }

        /// <summary>
        /// Reads a cached float field, with a fallback if the field is missing or not a float.
        /// </summary>
        static float ReadFloat(FieldInfo field, object instance, float fallback)
        {
            if (field == null)
                return fallback;
            object value = field.GetValue(instance);
            return value is float number ? number : fallback;
        }

        /// <summary>
        /// Converts the game's seam line into a list of 2D points. Throws on invalid points (NaN, infinity).
        /// </summary>
        static List<Vector2> ReadPath(object raw)
        {
            var points = new List<Vector2>();
            if (raw is IEnumerable items)
            {
                foreach (object item in items)
                {
                    Vector2 point = ReadPoint(item);
                    if (float.IsNaN(point.x) || float.IsInfinity(point.x)
                        || float.IsNaN(point.y) || float.IsInfinity(point.y))
                        throw new InvalidOperationException("The seam contains an invalid point.");
                    points.Add(point);
                }
            }
            return points;
        }

        /// <summary>
        /// Converts one point to 2D. Accepts Vector2, or Vector3 where a flat seam uses x/z instead of x/y.
        /// </summary>
        static Vector2 ReadPoint(object item)
        {
            if (item is Vector2 vector2)
                return vector2;
            if (item is Vector3 vector3)
            {
                if (Mathf.Abs(vector3.y) < 0.0001f)
                    return new Vector2(vector3.x, vector3.z);
                return new Vector2(vector3.x, vector3.y);
            }
            throw new InvalidOperationException("Unexpected seam point type " + (item == null ? "null" : item.GetType().FullName));
        }

        /// <summary>
        /// Looks up all needed SewingController fields and methods once per controller type, and caches them.
        /// </summary>
        static void Bind(Type type)
        {
            if (_finish != null && _finish.DeclaringType == type)
                return;

            _finished = AccessTools.Field(type, "finished");
            _patternLine = AccessTools.Field(type, "patternLine");
            _fabric = AccessTools.Field(type, "fabric");
            _hand = AccessTools.Field(type, "hand");
            _speedControl = AccessTools.Field(type, "speedControl");
            _animator = AccessTools.Field(type, "machineAnimator");
            _fabricSpeedMax = AccessTools.Field(type, "fabricSpeedMax");
            _stitchDistance = AccessTools.Field(type, "stitchDistance");
            _stitchLine = AccessTools.Field(type, "StitchLineRenderer");
            _patternRenderer = AccessTools.Field(type, "patternLineMeshRenderer");
            _currentFabricSpeed = AccessTools.Field(type, "currentFabricSpeed");
            _accuracy = AccessTools.Field(type, "accuracy");
            _mistakes = AccessTools.Field(type, "mistakes");
            _finish = AccessTools.Method(type, "Finish");
            _restart = AccessTools.Method(type, "RestartSegment");
            _accuracyText = AccessTools.Method(type, "UpdateAccuracyText");
            _stitchSounds = AccessTools.Method(type, "ProcessStitchingSounds");
            _canSew = AccessTools.Property(type, "CanSew");
        }
    }
}
