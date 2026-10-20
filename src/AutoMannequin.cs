using System;
using HarmonyLib;
using UnityEngine;

namespace DressmakerAutoSew
{
    public static class AutoMannequin
    {
        static readonly string[] Parts = { "Bust", "Waist", "Hips" };
        static readonly string[] Slots = { "mannequinBustValue", "mannequinWaistValue", "mannequinHipsValue" };
        static readonly string[] Ghosts = { "ghostBust", "ghostWaist", "ghostHips" };
        static readonly string[] Setters = { "SetBust", "SetWaist", "SetHips" };
        static readonly string[] Reads = { "Bust", "Waist", "Hips" };

        const float TapeSeconds = 8f;
        const float PartSeconds = 20f;

        static bool _requested;
        public static bool IsActive => _requested && !_halted && !_done;

        public static bool Start(object scene)
        {
            if (!Plugin.MannequinEnabled || scene == null || !GameAccess.Bool(scene, "IsMannequinScene"))
                return false;
            var gears = GameAccess.Member(scene, "gears") as Array;
            MonoBehaviour gear = null;
            if (gears != null)
                foreach (object candidate in gears)
                    if (candidate is MonoBehaviour behaviour && behaviour.gameObject.activeInHierarchy)
                    { gear = behaviour; break; }
            var measurer = GameAccess.Member(gear, "personMeasurer") as MonoBehaviour;
            object paper = GameAccess.Member(gear, "paper");
            if (measurer == null || paper == null)
                return false;
            Cancel();
            _scene = scene;
            _gear = gear;
            _measurer = measurer;
            _paper = paper;
            _requested = true;
            _running = true;
            _startedAt = Time.time;
            _frame = -1;
            return true;
        }

        static int _frame = -1;
        static int _part = -1;
        static bool _tapeOn;
        static bool _inverted;
        static bool _done;
        static bool _running;
        static bool _halted;
        static string _lastNote;
        static float _prevError;
        static float _bestAbs = 999f;
        static float _bestLattice;
        static float _startedAt;
        static float _partSince;
        static int _flips;
        static MonoBehaviour _measurer;
        static MonoBehaviour _gear;
        static object _paper;
        static object _scene;
        static readonly float[] _targets = new float[3];
        static readonly bool[] _verified = new bool[3];

        public static void GearTick(MonoBehaviour __instance)
        {
            if (Time.timeScale <= 0f)
                return;
            if (Time.frameCount == _frame)
                return;
            _frame = Time.frameCount;
            try
            {
                Tick(__instance);
            }
            catch (Exception ex)
            {
                Halt("Auto mannequin stopped: " + ex.Message);
            }
        }

        static void Tick(MonoBehaviour gear)
        {
            if (!Plugin.MannequinEnabled)
            {
                Cancel();
                return;
            }

            if (!_requested || _done)
                return;

            object paper = GameAccess.Member(gear, "paper");
            object scene = GameAccess.Member(paper, "measuringScene") ?? GameAccess.Find("MeasuringScene");
            object gears = GameAccess.Member(scene, "gears");
            if (paper == null || scene == null || !(gears is Array array) || !GameAccess.Bool(scene, "IsMannequinScene"))
                return;
            var measurer = GameAccess.Member(gear, "personMeasurer") as MonoBehaviour;
            if (!ReferenceEquals(_paper, paper) || !ReferenceEquals(_measurer, measurer) || !ReferenceEquals(_scene, scene))
            {
                Cancel();
                return;
            }
            _gear = gear;
            if (_halted)
                return;
            if (!Targets(scene, out float bust, out float waist, out float hips))
            {
                Note("Auto mannequin has no bust, waist, and hips to match.");
                if (_running && Time.time - _startedAt > TapeSeconds)
                    Halt("Auto mannequin stopped: there were no measurements to match.");
                return;
            }

            if (_targets[0] != bust || _targets[1] != waist || _targets[2] != hips)
            {
                Reset();
                _targets[0] = bust;
                _targets[1] = waist;
                _targets[2] = hips;
            }
            float[] targets = _targets;
            int part = NextPart(paper, targets);
            if (part < 0)
            {
                _running = false;
                AutoMeasure.BlockMannequin(false);
                if (!_done)
                {
                    _done = true;
                    GameAccess.SetMember(paper, "_delayedAccuracy", false);
                    AccessTools.Method(paper.GetType(), "UpdateAccuracy")?.Invoke(paper, null);
                    AccessTools.Method(paper.GetType(), "UpdateConfirmButton")?.Invoke(paper, null);
                    Plugin.Log.LogInfo("Auto mannequin matches the body measurements.");
                }
                return;
            }
            if (_done)
            {
                Reset();
            }
            _running = true;
            AutoMeasure.BlockMannequin(true);
            if (_startedAt <= 0f)
                _startedAt = Time.time;
            if (!AutoMeasure.EnsureTapeOut(measurer) || !AutoMeasure.TapeSettled(measurer))
            {
                if (Time.time - _startedAt > TapeSeconds)
                    Halt("Auto mannequin stopped: the measuring tape did not settle.");
                return;
            }

            if (part != _part)
            {
                _part = part;
                _tapeOn = false;
                _inverted = false;
                _done = false;
                _prevError = 0f;
                _bestAbs = 999f;
                _flips = 0;
                _partSince = Time.time;
            }
            if (_partSince <= 0f)
                _partSince = Time.time;
            float limit = _tapeOn ? PartSeconds : TapeSeconds;
            if (Time.time - _partSince > limit)
            {
                Halt("Auto mannequin stopped: the " + Parts[part] + " did not settle.");
                return;
            }

            object one = GearFor(array, part);
            if (one == null)
                return;
            if (!_tapeOn)
            {
                if (ArmTape(one, paper, part))
                {
                    _tapeOn = true;
                    Plugin.Log.LogInfo("Auto mannequin put the tape on the " + Parts[part] + ".");
                }
                return;
            }

            Turn(one, paper, part, targets[part]);
        }

        static void Halt(string reason)
        {
            if (_halted)
                return;
            _halted = true;
            _running = false;
            AutoMeasure.Release(_measurer, true);
            Plugin.Log.LogWarning(reason);
        }

        static void Reset()
        {
            Array.Clear(_verified, 0, _verified.Length);
            _part = -1;
            _tapeOn = false;
            _inverted = false;
            _done = false;
            _lastNote = null;
            _prevError = 0f;
            _bestAbs = 999f;
            _flips = 0;
            _startedAt = 0f;
            _partSince = 0f;
        }

        public static void Cancel()
        {
            _requested = false;
            AutoMeasure.Release(_measurer, true);
            Reset();
            _running = false;
            _halted = false;
            _measurer = null;
            _gear = null;
            _paper = null;
            _scene = null;
            Array.Clear(_targets, 0, _targets.Length);
        }

        public static void Maintain()
        {
            if (!Plugin.MannequinEnabled)
            {
                Cancel();
                return;
            }
            if (_paper == null)
                return;
            object scene = GameAccess.Find("MeasuringScene");
            if (_measurer == null || _gear == null || !ReferenceEquals(scene, _scene)
                || !GameAccess.Bool(scene, "IsMannequinScene")
                || !ReferenceEquals(GameAccess.Member(_gear, "paper"), _paper)
                || !ReferenceEquals(GameAccess.Member(_gear, "personMeasurer"), _measurer))
                Cancel();
        }

        public static bool OwnsCamera(object camera)
        {
            return _measurer != null
                && ReferenceEquals(GameAccess.Member(_measurer, "measurerCamControl"), camera);
        }

        static int NextPart(object paper, float[] targets)
        {
            for (int i = 0; i < Parts.Length; i++)
            {
                if (!_verified[i] || !Close(paper, Slots[i], targets[i]))
                    return i;
            }
            return -1;
        }

        static bool Close(object paper, string slot, float target)
        {
            object value = GameAccess.Member(paper, slot);
            if (value == null || !GameAccess.Bool(value, "HasBeenSet"))
                return false;
            float number = GameAccess.Float(value, "Value", 0f);
            if (float.IsNaN(number) || float.IsInfinity(number))
                return false;
            return Tenth(number) == Tenth(target) || Mathf.Abs(number - target) <= 0.05f;
        }

        static object GearFor(Array gears, int part)
        {
            foreach (object one in gears)
            {
                if (one != null && GameAccess.Member(one, "bodyPart")?.ToString() == Parts[part])
                    return one;
            }
            return null;
        }

        static bool ArmTape(object gear, object paper, int part)
        {
            var measurer = GameAccess.Member(gear, "personMeasurer") as MonoBehaviour;
            var ghost = GameAccess.Member(paper, Ghosts[part]) as RectTransform;
            var body = GameAccess.Member(measurer, "body") as Component;
            var cam = GameAccess.Member(measurer, "cam") as Camera;
            var start = GameAccess.Member(measurer, "MeasuringTapeStart") as Component;
            var end = GameAccess.Member(measurer, "MeasuringTapeEnd") as Component;
            var sampleHeight = GameAccess.Member(gear, "sampleHeight") as Transform;
            if (measurer == null || (ghost == null && sampleHeight == null) || body == null || cam == null || start == null || end == null)
            {
                Note("Auto mannequin could not find the " + Parts[part] + " tape.");
                return false;
            }

            Vector3 point = body.transform.position;
            bool found;
            if (sampleHeight != null)
            {
                // Use the same physical band as the gear's measurement logic.
                point.y = sampleHeight.position.y;
                found = AutoMeasure.Sample(measurer, point, out _);
            }
            else
            {
                point.y = ghost.position.y;
                found = AutoMeasure.Sample(measurer, point, out _);
            }
            if (!found || !AutoMeasure.MoveTape(measurer, point, out _))
            {
                Note("Auto mannequin could not read the " + Parts[part] + " at its band.");
                return false;
            }
            return AlignTapeBand(measurer, paper, part) && ReadTape(measurer) > 1f;
        }

        // Both references existed in the supplied game's integration code. Compare
        // the displayed tape to the displayed band, rather than assuming the gear
        // sampling height is also the tape's visible height.
        static bool AlignTapeBand(MonoBehaviour measurer, object paper, int part)
        {
            var ghost = GameAccess.Member(paper, Ghosts[part]) as Transform;
            var tape = GameAccess.Member(paper, "mannequinTape");
            var marker = GameAccess.Member(tape, "StartPiece") as Transform;
            var start = GameAccess.Member(measurer, "MeasuringTapeStart") as Component;
            var body = GameAccess.Member(measurer, "body") as Component;
            if (ghost == null || marker == null || start == null || body == null)
            {
                Note("Auto mannequin cannot verify the " + Parts[part] + " stripe and tape markers.");
                return false;
            }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float delta = ghost.position.y - marker.position.y;
                if (float.IsNaN(delta) || float.IsInfinity(delta) || Mathf.Abs(delta) > 0.25f)
                    return false;
                if (Mathf.Abs(delta) <= 0.001f)
                    return true;
                Vector3 point = body.transform.position;
                point.y = start.transform.position.y + delta;
                if (!AutoMeasure.MoveTape(measurer, point, out _))
                    return false;
                // Re-read the markers after UpdateTape; a correction is only
                // accepted if the rendered tape actually reaches the stripe.
            }
            return Mathf.Abs(ghost.position.y - marker.position.y) <= 0.001f;
        }

        static void Turn(object gear, object paper, int part, float target)
        {
            var measurer = GameAccess.Member(gear, "personMeasurer") as MonoBehaviour;
            object lattice = GameAccess.Member(gear, "targetLattice");
            if (measurer == null || lattice == null)
                return;

            if (!AlignTapeBand(measurer, paper, part))
            {
                Note("Auto mannequin is waiting for the " + Parts[part] + " tape to align with its stripe.");
                return;
            }
            float cm = ReadTape(measurer);
            if (float.IsNaN(cm) || float.IsInfinity(cm) || cm <= 1f)
            {
                Note("Auto mannequin could not read the " + Parts[part] + " tape.");
                return;
            }

            float error = target - cm;
            float current = GameAccess.Float(lattice, Reads[part], 0.5f);
            if (float.IsNaN(current) || float.IsInfinity(current) || current < 0f || current > 1f)
            {
                Halt("Auto mannequin stopped: the gear position was invalid.");
                return;
            }
            if (Mathf.Abs(error) < _bestAbs)
            {
                _bestAbs = Mathf.Abs(error);
                _bestLattice = current;
            }
            if (Tenth(cm) == Tenth(target) || Mathf.Abs(error) <= 0.05f)
            {
                Commit(gear, paper, part, measurer, current);
                return;
            }
            if (_prevError != 0f && Mathf.Sign(_prevError) != Mathf.Sign(error) && Mathf.Abs(error) < 0.8f)
                _flips++;
            _prevError = error;
            if (_flips >= 2)
            {
                Apply(gear, lattice, part, _bestLattice);
                AutoMeasure.InvokeRequired(measurer, "UpdateTape", true);
                float closest = ReadTape(measurer);
                if (Tenth(closest) == Tenth(target) || Mathf.Abs(closest - target) <= 0.05f)
                    Commit(gear, paper, part, measurer, _bestLattice);
                else
                    Halt("Auto mannequin stopped: the closest " + Parts[part] + " size did not match the target.");
                return;
            }

            float gain = Mathf.Abs(error) < 0.45f ? 0.0012f : Mathf.Abs(error) < 1.2f ? 0.0025f : 0.005f;
            float step = error * gain;
            if (_inverted)
                step = -step;
            float limit = Mathf.Abs(error) < 0.45f ? 0.0016f : 0.012f;
            step = Mathf.Clamp(step, -limit, limit);
            if (Mathf.Abs(step) < 0.00025f)
                step = Mathf.Sign(error == 0f ? 1f : error) * 0.00025f;
            step *= Mathf.Clamp(Time.deltaTime * 60f, 0f, 3f);
            float next = Mathf.Clamp01(current + step);
            if ((next <= 0.001f || next >= 0.999f) && Mathf.Abs(next - current) < 0.0001f && Mathf.Abs(error) > 0.5f)
                _inverted = !_inverted;
            Apply(gear, lattice, part, next);
            AccessTools.Method(measurer.GetType(), "UpdateTape")?.Invoke(measurer, new object[] { true });
        }

        static void Commit(object gear, object paper, int part, MonoBehaviour measurer, float latticeValue)
        {
            object lattice = GameAccess.Member(gear, "targetLattice");
            Apply(gear, lattice, part, latticeValue);
            AccessTools.Method(measurer.GetType(), "UpdateTape")?.Invoke(measurer, new object[] { true });
            if (!AlignTapeBand(measurer, paper, part))
                throw new InvalidOperationException("The " + Parts[part] + " tape is not aligned with its stripe.");
            float measured = ReadTape(measurer);
            if (float.IsNaN(measured) || float.IsInfinity(measured) || measured <= 1f
                || (Tenth(measured) != Tenth(_targets[part]) && Mathf.Abs(measured - _targets[part]) > 0.05f))
                throw new InvalidOperationException("The aligned " + Parts[part] + " tape does not match the target.");
            object bodyPart = GameAccess.Member(gear, "bodyPart");
            AutoMeasure.InvokeRequired(paper, "SetMannequinMeasurement", bodyPart);
            if (!Close(paper, Slots[part], _targets[part]))
                throw new InvalidOperationException("The " + Parts[part] + " measurement was not recorded at the target size.");
            _verified[part] = true;
            Plugin.Log.LogInfo("Auto mannequin " + Parts[part] + " tape reads " + ReadTape(measurer).ToString("0.0") + " cm.");
        }

        static void Apply(object gear, object lattice, int part, float value)
        {
            AutoMeasure.InvokeRequired(lattice, Setters[part], value);
            AutoMeasure.InvokeRequired(gear, "SetBodyPart", value);
            AutoMeasure.InvokeRequired(gear, "SetGearPosition", value);
        }

        static int SlotIndex(string slot)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (Slots[i] == slot)
                    return i;
            }
            return 0;
        }

        static string Tenth(float value)
        {
            return value.ToString("0.0");
        }

        static float ReadTape(MonoBehaviour measurer)
        {
            var end = GameAccess.Member(measurer, "MeasuringTapeEnd") as Component;
            return end == null ? 0f : GameAccess.Float(end, "value", 0f);
        }

        static bool Targets(object scene, out float bust, out float waist, out float hips)
        {
            bust = waist = hips = 0f;
            // The current paper is the visible target; quest data may be older.
            var person = GameAccess.Member(scene, "personMeasurer") as Component;
            object paper = person != null ? GameAccess.Member(person, "paper") : null;
            if (paper != null && AutoMeasure.Has(paper, "bustValue") && AutoMeasure.Has(paper, "waistValue") && AutoMeasure.Has(paper, "hipsValue"))
            {
                bust = GameAccess.Float(GameAccess.Member(paper, "bustValue"), "Value", 0f);
                waist = GameAccess.Float(GameAccess.Member(paper, "waistValue"), "Value", 0f);
                hips = GameAccess.Float(GameAccess.Member(paper, "hipsValue"), "Value", 0f);
                if (Valid(bust) && Valid(waist) && Valid(hips))
                {
                    hips -= 0.1f;
                    return true;
                }
            }
            if (!Saved(out bust, out waist, out hips))
                return false;
            hips -= 0.1f;
            return Valid(hips);
        }

        static bool Saved(out float bust, out float waist, out float hips)
        {
            bust = waist = hips = 0f;
            object progress = GameAccess.Instance("PlayerProgress");
            object quest = GameAccess.Member(progress, "CurrentQuestDefinition");
            object giver = GameAccess.Member(quest, "questGiverCharacter");
            var method = progress != null ? AccessTools.Method(progress.GetType(), "GetCharacterMeasurements") : null;
            object measurements = giver != null && method != null ? method.Invoke(progress, new[] { giver }) : null;
            if (measurements == null)
                return false;
            bust = GameAccess.Float(measurements, "bust", 0f);
            waist = GameAccess.Float(measurements, "waist", 0f);
            hips = GameAccess.Float(measurements, "hips", 0f);
            return Valid(bust) && Valid(waist) && Valid(hips);
        }

        static bool Valid(float value)
        {
            return value > 1f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static void Note(string message)
        {
            if (_lastNote == message)
                return;
            _lastNote = message;
            Plugin.Log.LogWarning(message);
        }
    }
}
