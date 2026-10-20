using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DressmakerAutoSew
{
    public static class AutoMeasure
    {
        const float ZoneSeconds = 8f;
        static readonly string[] Slots = { "bustValue", "waistValue", "hipsValue" };
        static readonly string[] Boxes = { "bustHitbox", "waistHitbox", "hipsHitbox" };
        static readonly string[] Markers = { "bustMarker", "waistMarker", "hipsMarker" };
        static readonly string[] Setters = { "SetBustMeasurement", "SetWaistMeasurement", "SetHipsMeasurement" };

        static bool _requested;
        public static bool IsActive => _requested && !_halted && !_completed;

        public static bool Start(MonoBehaviour measurer)
        {
            if (!Plugin.MeasureEnabled || measurer == null || GameAccess.Member(measurer, "paper") == null)
                return false;
            Cancel();
            _measurer = measurer;
            _paper = GameAccess.Member(measurer, "paper");
            _requested = true;
            return true;
        }

        static int _zone;
        static bool _armed;
        static bool _running;
        static bool _halted;
        static bool _completed;
        static bool _tapePlaced;
        static bool _wrote;
        static bool _logged;
        static bool _tapeNoted;
        static bool _bandNoted;
        static bool _blockMeasure;
        static bool _blockMannequin;
        static bool _allowLeave;
        static float _heldSince;
        static float _heldCm;
        static float _zoneSince;
        static MonoBehaviour _measurer;
        static object _paper;

        public static void Tick(MonoBehaviour __instance)
        {
            if (Time.timeScale <= 0f && Plugin.MeasureEnabled)
                return;
            try
            {
                TickSafe(__instance);
            }
            catch (Exception ex)
            {
                if (!_logged)
                    Plugin.Log.LogWarning("Auto measure stopped: " + ex.GetBaseException().Message);
                _logged = true;
                Halt(_measurer != null ? _measurer : __instance, null);
            }
        }

        static void TickSafe(MonoBehaviour measurer)
        {
            if (!Plugin.MeasureEnabled)
            {
                Cancel();
                return;
            }

            if (!_requested)
                return;

            object scene = GameAccess.Find("MeasuringScene");
            if (scene == null || GameAccess.Member(scene, "ActiveMeasurer") != (object)measurer)
                return;
            if (GameAccess.Bool(scene, "IsMannequinScene"))
                return;

            object paper = GameAccess.Member(measurer, "paper");
            if (paper == null)
                return;
            if (!ReferenceEquals(_measurer, measurer) || !ReferenceEquals(_paper, paper))
            {
                Cancel();
                return;
            }
            if (_halted || _completed)
                return;

            // The tape icon has to be taken out, and the measure camera zoomed in,
            // before any number is written. Doing it the other way leaves the icon
            // dragging and the tape hidden.
            _measurer = measurer;
            _running = true;
            _blockMeasure = true;
            if (_zoneSince <= 0f)
                _zoneSince = Time.time;
            if (!EnsureTapeOut(measurer))
            {
                _tapePlaced = false;
                _heldSince = 0f;
                Stall(measurer, "the measuring tape did not come out");
                return;
            }
            bool settled = TapeSettled(measurer);
            if (settled && !_tapeNoted)
            {
                _tapeNoted = true;
                Plugin.Log.LogInfo("Measuring tape is out.");
            }

            if (!_armed)
            {
                _armed = true;
                _zone = 0;
                _tapePlaced = false;
                _wrote = false;
                _zoneSince = Time.time;
                Plugin.Log.LogInfo("Auto measure will read the bust, then the waist, then the hips.");
            }
            if (_zone >= 3)
                return;

            string[] slots = Slots;
            string[] boxes = Boxes;
            string[] markers = Markers;
            string[] setters = Setters;
            if (Has(paper, slots[_zone]) && Finite(GameAccess.Float(GameAccess.Member(paper, slots[_zone]), "Value", 0f))
                && GameAccess.Float(GameAccess.Member(paper, slots[_zone]), "Value", 0f) > 1f)
            {
                Plugin.Log.LogInfo("Auto measure recorded " + slots[_zone].Replace("Value", "") + ".");
                _zone++;
                _tapePlaced = false;
                _wrote = false;
                _bandNoted = false;
                _zoneSince = Time.time;
                if (_zone >= 3)
                {
                    _completed = true;
                    _running = false;
                    _blockMeasure = false;
                }
                return;
            }
            if (_zoneSince <= 0f)
                _zoneSince = Time.time;

            var zone = GameAccess.Member(measurer, boxes[_zone]) as RectTransform;
            var marker = GameAccess.Member(measurer, markers[_zone]) as Transform;
            float along = boxes[_zone] == "bustHitbox" ? 0.71f : boxes[_zone] == "hipsHitbox" ? 0.66f : 0.5f;
            float preferredY = marker != null ? marker.position.y : 0f;
            if (!_tapePlaced)
            {
                if (!settled)
                {
                    Stall(measurer, "the measuring camera did not settle");
                    return;
                }
                if (!FindBandPoint(measurer, preferredY, zone, along, out Vector3 point) || !MoveTape(measurer, point, out _heldCm))
                {
                    if (settled && !_bandNoted && Time.time - _zoneSince > 1.5f)
                    {
                        _bandNoted = true;
                        Plugin.Log.LogWarning("Auto measure could not read the " + slots[_zone].Replace("Value", "") + " band.");
                    }
                    Stall(measurer, "the " + slots[_zone].Replace("Value", "") + " was not recorded");
                    return;
                }
                _bandNoted = false;
                _tapePlaced = true;
                _wrote = false;
                _heldSince = 0f;
                return;
            }

            // Leave the tape still. Moving it again keeps the game from accepting the hold.
            if (!settled)
            {
                _heldSince = 0f;
                Stall(measurer, "the measuring camera did not settle");
                return;
            }
            if (_heldSince <= 0f)
                _heldSince = Time.time;
            if (Time.time - _heldSince < 0.6f)
                return;
            if (_wrote)
            {
                Stall(measurer, "the " + slots[_zone].Replace("Value", "") + " was not recorded");
                return;
            }

            var end = GameAccess.Member(measurer, "MeasuringTapeEnd") as Component;
            float shown = end == null ? 0f : GameAccess.Float(end, "value", 0f);
            if (!Finite(shown) || shown <= 1f || Mathf.Abs(shown - _heldCm) > 1.5f)
            {
                Halt(measurer, "Auto measure stopped: the tape reading changed during the hold.");
                return;
            }
            float cm = shown;
            var method = AccessTools.Method(paper.GetType(), setters[_zone]);
            if (method == null || cm <= 1f)
            {
                Halt(measurer, "Auto measure stopped: the " + slots[_zone].Replace("Value", "") + " was not recorded.");
                return;
            }
            method.Invoke(paper, new object[] { cm });
            _wrote = true;
        }

        static bool Stall(MonoBehaviour measurer, string reason)
        {
            if (_zoneSince <= 0f || Time.time - _zoneSince < ZoneSeconds)
                return false;
            Halt(measurer, "Auto measure stopped: " + reason + ".");
            return true;
        }

        static void Halt(MonoBehaviour measurer, string reason)
        {
            Release(measurer, false);
            if (!string.IsNullOrEmpty(reason))
                Plugin.Log.LogWarning(reason);
            _halted = true;
            _running = false;
            _armed = false;
            _blockMeasure = false;
        }

        static void Clear()
        {
            _zone = 0;
            _armed = false;
            _running = false;
            _halted = false;
            _completed = false;
            _tapePlaced = false;
            _wrote = false;
            _logged = false;
            _tapeNoted = false;
            _bandNoted = false;
            _blockMeasure = false;
            _heldSince = 0f;
            _heldCm = 0f;
            _zoneSince = 0f;
            _measurer = null;
            _paper = null;
        }

        public static void Cancel()
        {
            _requested = false;
            bool wasRunning = _running;
            Release(_measurer, false);
            Clear();
            if (wasRunning)
                Plugin.Log.LogInfo("Auto measure stopped.");
        }

        // Called by the plugin even when the game's measurer no longer updates.
        public static void Maintain()
        {
            if (!Plugin.MeasureEnabled)
            {
                Cancel();
                return;
            }
            if (_paper == null)
                return;
            object scene = GameAccess.Find("MeasuringScene");
            if (_measurer == null || scene == null || GameAccess.Bool(scene, "IsMannequinScene")
                || !ReferenceEquals(GameAccess.Member(scene, "ActiveMeasurer"), _measurer)
                || !ReferenceEquals(GameAccess.Member(_measurer, "paper"), _paper))
                Cancel();
        }

        static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool LeaveModePrefix(object __instance)
        {
            if (_allowLeave)
                return true;
            // Only block the camera belonging to this attempt.
            return !((_blockMeasure && _measurer != null
                && ReferenceEquals(GameAccess.Member(_measurer, "measurerCamControl"), __instance))
                || (_blockMannequin && AutoMannequin.OwnsCamera(__instance)));
        }

        public static void BlockMannequin(bool block)
        {
            _blockMannequin = block;
        }

        public static void Release(MonoBehaviour measurer, bool mannequin)
        {
            if (mannequin)
                _blockMannequin = false;
            else
                _blockMeasure = false;
            if (measurer == null)
                return;
            bool previousAllowLeave = _allowLeave;
            _allowLeave = true;
            try
            {
                CleanupStep("remove tape", () => InvokeRequired(measurer, "RemoveTape"));
                CleanupStep("clear tape state", () => GameAccess.SetMember(measurer, "_isTapeOnBody", false));
                CleanupStep("cancel tape movement", () => SetDragging(measurer, false));
                CleanupStep("cancel icon drag", () =>
                {
                    var icon = GameAccess.Member(measurer, "TapeIcon") as Component;
                    if (icon != null)
                        InvokeRequired(icon, "CancelDrag", true);
                });
                CleanupStep("leave measuring camera", () =>
                {
                    object camControl = GameAccess.Member(measurer, "measurerCamControl");
                    if (camControl != null)
                        InvokeRequired(camControl, "LeaveMode", true);
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not put the measuring tape away: " + ex.Message);
            }
            finally
            {
                _allowLeave = previousAllowLeave;
            }
        }

        static void CleanupStep(string action, Action cleanup)
        {
            try { cleanup(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not " + action + ": " + ex.GetBaseException().Message);
            }
        }

        public static object InvokeRequired(object target, string name, params object[] args)
        {
            if (target == null)
                throw new InvalidOperationException("Missing target for " + name + ".");
            MethodInfo method = AccessTools.Method(target.GetType(), name);
            if (method == null)
                throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, args);
        }

        public static bool EnsureTapeOut(MonoBehaviour measurer)
        {
            if (measurer == null)
                return false;
            var start = GameAccess.Member(measurer, "MeasuringTapeStart") as Component;
            var end = GameAccess.Member(measurer, "MeasuringTapeEnd") as Component;
            var icon = GameAccess.Member(measurer, "TapeIcon") as Component;
            object camControl = GameAccess.Member(measurer, "measurerCamControl");
            if (start == null || end == null)
                return false;

            bool tapes = start.gameObject.activeInHierarchy && end.gameObject.activeInHierarchy;
            bool iconAway = icon == null || !icon.gameObject.activeInHierarchy;
            bool onBody = GameAccess.Bool(measurer, "_isTapeOnBody");
            bool camOn = camControl != null && GameAccess.Bool(camControl, "IsEnabled");
            SetDragging(measurer, false);
            if (tapes && iconAway && onBody && camOn)
                return true;

            if (icon != null)
            {
                AccessTools.Method(icon.GetType(), "CancelDrag")?.Invoke(icon, new object[] { true });
                icon.gameObject.SetActive(false);
            }
            start.gameObject.SetActive(true);
            end.gameObject.SetActive(true);
            GameAccess.SetMember(measurer, "_isTapeOnBody", true);
            SetDragging(measurer, false);
            if (camControl != null && !camOn)
                AccessTools.Method(camControl.GetType(), "EnterMode")?.Invoke(camControl, null);
            return false;
        }

        public static bool TapeSettled(MonoBehaviour measurer)
        {
            var cam = GameAccess.Member(measurer, "cam") as Camera;
            if (cam == null)
                return false;
            return cam.orthographicSize <= 0.32f;
        }

        public static bool Has(object owner, string name)
        {
            object value = GameAccess.Member(owner, name);
            return value != null && GameAccess.Bool(value, "HasBeenSet");
        }

        public static bool FindBandPoint(MonoBehaviour measurer, float preferredY, RectTransform zone, float along, out Vector3 point)
        {
            point = default;
            var body = GameAccess.Member(measurer, "body") as Component;
            var cam = GameAccess.Member(measurer, "cam") as Camera;
            if (body == null)
                return false;
            int mask = LayerMask.GetMask("Mannequin");

            if (zone != null && cam != null)
            {
                Vector3[] corners = new Vector3[4];
                zone.GetWorldCorners(corners);
                Vector3 left = Vector3.Lerp(corners[0], corners[1], along);
                Vector3 right = Vector3.Lerp(corners[3], corners[2], along);
                Vector3 mid = (left + right) * 0.5f;
                if (RayToBody(cam, mid, mask, out point) && Sample(measurer, point, out _))
                    return true;
                Vector2 screen = ToScreen(cam, mid);
                for (float dy = -80f; dy <= 80f; dy += 16f)
                {
                    Ray ray = cam.ScreenPointToRay(screen + new Vector2(0f, dy));
                    if (Physics.Raycast(ray, out RaycastHit hit, 100f, mask) && Sample(measurer, hit.point, out _))
                    {
                        point = hit.point;
                        return true;
                    }
                }
            }

            if (preferredY > 0.01f && ScanHeight(measurer, preferredY, out point))
                return true;
            return false;
        }

        public static bool MoveTape(MonoBehaviour measurer, Vector3 worldPoint, out float cm)
        {
            cm = 0f;
            var start = GameAccess.Member(measurer, "MeasuringTapeStart") as Component;
            var end = GameAccess.Member(measurer, "MeasuringTapeEnd") as Component;
            if (start == null || !Sample(measurer, worldPoint, out float sampled))
                return false;

            var body = GameAccess.Member(measurer, "body") as Component;
            float before = end == null ? 0f : GameAccess.Float(end, "value", 0f);
            Vector3 position = start.transform.position;
            position.x = body != null ? body.transform.position.x : worldPoint.x;
            position.y = worldPoint.y;
            start.transform.position = position;
            start.gameObject.SetActive(true);
            if (end != null)
                end.gameObject.SetActive(true);
            GameAccess.SetMember(measurer, "_isTapeOnBody", true);
            SetDragging(measurer, false);
            AccessTools.Method(measurer.GetType(), "UpdateTape")?.Invoke(measurer, new object[] { true });
            float shown = end == null ? 0f : GameAccess.Float(end, "value", 0f);
            bool fresh = Mathf.Abs(shown - sampled) <= 1.5f;
            bool changed = Mathf.Abs(shown - before) > 0.05f;
            if (!Finite(shown) || !Finite(sampled) || shown <= 1f || (!fresh && !changed))
                return false;
            cm = fresh ? shown : sampled;
            return true;
        }

        public static bool Sample(MonoBehaviour measurer, Vector3 around, out float cm)
        {
            cm = 0f;
            var body = GameAccess.Member(measurer, "body") as Component;
            MethodInfo method = AccessTools.Method(measurer.GetType(), "TrySampleCircumferenceAt");
            if (body == null || method == null)
                return false;

            Vector3 world = around;
            world.x = body.transform.position.x;
            world.z = body.transform.position.z;
            object[] args = { world, 0f, default(RaycastHit), default(RaycastHit) };
            if (!(method.Invoke(measurer, args) is bool ok) || !ok || !(args[1] is float number) || !Finite(number) || number <= 1f)
                return false;
            cm = number;
            return true;
        }

        static bool ScanHeight(MonoBehaviour measurer, float preferredY, out Vector3 point)
        {
            point = default;
            var body = GameAccess.Member(measurer, "body") as Component;
            if (body == null)
                return false;
            Vector3 guess = body.transform.position;
            for (float dy = -0.18f; dy <= 0.18f; dy += 0.02f)
            {
                guess.y = preferredY + dy;
                if (!Sample(measurer, guess, out _))
                    continue;
                point = guess;
                return true;
            }
            return false;
        }

        static bool RayToBody(Camera cam, Vector3 mid, int mask, out Vector3 point)
        {
            point = default;
            Vector3 projected = cam.WorldToScreenPoint(mid);
            if (projected.z > 0f && Physics.Raycast(cam.ScreenPointToRay(projected), out RaycastHit hit, 100f, mask))
            {
                point = hit.point;
                return true;
            }
            if ((Mathf.Abs(mid.x) > 8f || Mathf.Abs(mid.y) > 8f)
                && Physics.Raycast(cam.ScreenPointToRay(new Vector2(mid.x, mid.y)), out RaycastHit direct, 100f, mask))
            {
                point = direct.point;
                return true;
            }
            return false;
        }

        static Vector2 ToScreen(Camera cam, Vector3 mid)
        {
            if (Mathf.Abs(mid.x) > 8f || Mathf.Abs(mid.y) > 8f)
                return new Vector2(mid.x, mid.y);
            return cam.WorldToScreenPoint(mid);
        }

        static void SetDragging(object measurer, bool value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = measurer.GetType().GetProperty("IsDraggingTape", flags);
            MethodInfo setter = property?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(measurer, new object[] { value });
                return;
            }
            FieldInfo field = measurer.GetType().GetField("<IsDraggingTape>k__BackingField", flags);
            field?.SetValue(measurer, value);
        }
    }
}
