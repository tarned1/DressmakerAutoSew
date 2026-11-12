using System;
using BepInEx;
using UnityEngine;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DressmakerAutoSew
{
    /// <summary>
    /// The mod's entry point. BepInEx creates one Plugin object when the game starts.
    /// It creates the settings, installs the Harmony hooks that connect each feature to the game,
    /// handles the F4 key, and runs a small maintenance check four times a second.
    /// Everything else lives in the feature classes (AutoSew, AutoMeasure, AutoMannequin,
    /// AutoPlace, PlaceBoard, PauseMenuOptions).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("Dressmaker.exe")]
    [DefaultExecutionOrder(10000)]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>
        /// Unique plugin ID. BepInEx, the Mod Manager and Harmony use it to tell mods apart.
        /// Also the name of the settings file in BepInEx/config.
        /// </summary>
        public const string Guid = "com.Venus.dressmaker.autosew";
        public const string Name = "Dressmaker Auto Sew";
        /// <summary>
        /// Shown in the Mod Manager and the log. Bump it for every release.
        /// </summary>
        public const string Version = "1.6.1";

        /// <summary>
        /// Auto sew speed range: 0 is the sewing machine's slowest lever position, 1 its fastest.
        /// </summary>
        public const float SpeedMin = 0f;
        public const float SpeedMax = 1f;

        /// <summary>
        /// The one running Plugin object, so static code can reach it.
        /// </summary>
        public static Plugin Instance { get; private set; }
        /// <summary>
        /// Writes to BepInEx/LogOutput.log. Use LogDebug for detail, LogInfo for normal events,
        /// LogWarning and LogError for problems.
        /// </summary>
        public static ManualLogSource Log { get; private set; }

        /// <summary>
        /// Settings stored in the .cfg file. The Automation window in the pause menu edits the same entries.
        /// </summary>
        public static ConfigEntry<bool> EnabledEntry { get; private set; }
        public static ConfigEntry<float> SpeedEntry { get; private set; }
        public static ConfigEntry<bool> MeasureEntry { get; private set; }
        public static ConfigEntry<bool> MannequinEntry { get; private set; }
        public static ConfigEntry<bool> PlaceEntry { get; private set; }

        /// <summary>
        /// Quick read-only switches: true when that feature is turned on.
        /// </summary>
        public static bool Enabled => EnabledEntry != null && EnabledEntry.Value;
        public static bool MeasureEnabled => MeasureEntry != null && MeasureEntry.Value;
        public static bool MannequinEnabled => MannequinEntry != null && MannequinEntry.Value;
        public static bool PlaceEnabled => PlaceEntry != null && PlaceEntry.Value;

        /// <summary>
        /// The current auto sew speed. Kept in memory and only saved to the .cfg every 0.35 s
        /// (see FlushSpeed), because the mouse wheel can change it many times per second.
        /// </summary>
        static float _speed = 1f;
        static bool _speedDirty;
        static float _nextSpeedSave;
        float _nextMaintenance;

        public static float Speed => _speed;

        /// <summary>
        /// Runs once at startup. Binds every setting (name, default, description), reacts to setting
        /// changes (turning a feature off cancels it right away), and installs the Harmony hooks.
        /// Every game type and method is looked up by name first. If one is missing, only that feature
        /// is skipped with a log message, instead of the whole mod failing.
        /// </summary>
        void Awake()
        {
            Instance = this;
            Log = Logger;
            
            EnabledEntry = Config.Bind(
                "Auto Sew",
                "Enabled",
                true,
                "When this is on, opening a fabric's sewing screen feeds the seam by itself and finishes it at 100% quality. The same switch is in the pause menu's Automation menu.");

            SpeedEntry = Config.Bind(
                "Auto Sew",
                "MachineSpeed",
                1f,
                new ConfigDescription(
                    "Automation-menu speed under Auto sew. 0 is the sewing machine's slowest lever, 1 is its fastest.",
                    new AcceptableValueRange<float>(SpeedMin, SpeedMax)));

            MeasureEntry = Config.Bind(
                "Auto Sew",
                "MeasureEnabled",
                false,
                "When enabled, press F4 in the measuring screen to start or stop. The measuring tape is taken out first, then held on the bust, waist, and hips until the paper records them. It does not press confirm. The same switch is in the pause menu's Automation menu.");
            MannequinEntry = Config.Bind(
                "Auto Sew",
                "MannequinEnabled",
                false,
                "When enabled, press F4 in the mannequin screen to start or stop. The measuring tape is taken out first, then the gears turn until bust, waist, and hips match the measurements. Confirm stays for you. The same switch is in the pause menu's Automation menu.");
            PlaceEntry = Config.Bind(
                "Auto Sew",
                "PlaceEnabled",
                false,
                "When this is on, the cutting room sidebar gets a Layout tab beside Patterns and Fabric. Add the fabrics you want, choose the patterns for each, then press Solve.");

            _speed = ClampSpeed(SpeedEntry.Value);
            EnabledEntry.SettingChanged += (_, __) =>
            {
                if (!Enabled)
                    AutoSew.Cancel();
                PauseMenuOptions.Refresh();
            };
            MeasureEntry.SettingChanged += (_, __) =>
            {
                if (!MeasureEnabled)
                    AutoMeasure.Cancel();
                PauseMenuOptions.Refresh();
            };
            MannequinEntry.SettingChanged += (_, __) =>
            {
                if (!MannequinEnabled)
                    AutoMannequin.Cancel();
                PauseMenuOptions.Refresh();
            };
            PlaceEntry.SettingChanged += (_, __) => PauseMenuOptions.Refresh();
            SpeedEntry.SettingChanged += (_, __) =>
            {
                if (!_speedDirty)
                    _speed = ClampSpeed(SpeedEntry.Value);
                PauseMenuOptions.Refresh();
            };

            var harmony = new Harmony(Guid);
            var sewing = AccessTools.TypeByName("SewingController");
            var update = sewing != null ? AccessTools.Method(sewing, "Update") : null;
            var initialize = sewing != null ? AccessTools.Method(sewing, "InitializeWith") : null;
            if (update != null && initialize != null)
            {
                TryPatch(harmony, update, prefix: new HarmonyMethod(typeof(AutoSew), nameof(AutoSew.UpdatePrefix)));
                TryPatch(harmony, initialize, postfix: new HarmonyMethod(typeof(AutoSew), nameof(AutoSew.InitializePostfix)));
            }
            else
                Log.LogError("SewingController was not found. Auto sew cannot run.");

            var measure = AccessTools.TypeByName("PersonMeasurer");
            var measureUpdate = measure != null ? AccessTools.Method(measure, "Update") : null;
            if (measureUpdate != null)
                TryPatch(harmony, measureUpdate, postfix: new HarmonyMethod(typeof(AutoMeasure), nameof(AutoMeasure.Tick)));
            else
                Log.LogWarning("PersonMeasurer was not found. Auto measure cannot run.");

            var gear = AccessTools.TypeByName("Gear");
            var gearUpdate = gear != null ? AccessTools.Method(gear, "Update") : null;
            if (gearUpdate != null)
                TryPatch(harmony, gearUpdate, postfix: new HarmonyMethod(typeof(AutoMannequin), nameof(AutoMannequin.GearTick)));
            else
                Log.LogWarning("Gear was not found. Auto mannequin cannot run.");

            var cutting = AccessTools.TypeByName("CuttingRoom");
            var cuttingUpdate = cutting != null ? AccessTools.Method(cutting, "Update") : null;
            if (cuttingUpdate != null)
                TryPatch(harmony, cuttingUpdate, postfix: new HarmonyMethod(typeof(AutoPlace), nameof(AutoPlace.Tick)));
            else
                Log.LogWarning("CuttingRoom was not found. Pattern placement cannot run.");

            var sidebar = AccessTools.TypeByName("SidebarInventory");
            var setTab = sidebar != null ? AccessTools.Method(sidebar, "SetTab") : null;
            var setTabs = sidebar != null ? AccessTools.Method(sidebar, "SetAvailableTabs") : null;
            if (setTab != null)
                TryPatch(harmony, setTab, postfix: new HarmonyMethod(typeof(PlaceBoard), nameof(PlaceBoard.TabChanged)));
            if (setTabs != null)
                TryPatch(harmony, setTabs, postfix: new HarmonyMethod(typeof(PlaceBoard), nameof(PlaceBoard.TabsChanged)));

            var cameraControl = AccessTools.TypeByName("MeasurerCameraControl");
            var leaveMode = cameraControl != null ? AccessTools.Method(cameraControl, "LeaveMode") : null;
            if (leaveMode != null)
                TryPatch(harmony, leaveMode, prefix: new HarmonyMethod(typeof(AutoMeasure), nameof(AutoMeasure.LeaveModePrefix)));

            var pause = AccessTools.TypeByName("PauseMenu");
            var onEnable = pause != null ? AccessTools.Method(pause, "OnEnable") : null;
            var hide = pause != null ? AccessTools.Method(pause, "Hide") : null;
            if (onEnable != null)
                TryPatch(harmony, onEnable, postfix: new HarmonyMethod(typeof(PauseMenuOptions), nameof(PauseMenuOptions.OnEnablePostfix)));
            else
                Log.LogWarning("Pause menu was not found. Use BepInEx/config/" + Guid + ".cfg for the switches.");
            if (hide != null)
                TryPatch(harmony, hide, postfix: new HarmonyMethod(typeof(PauseMenuOptions), nameof(PauseMenuOptions.HidePostfix)));

            var languagePopup = AccessTools.TypeByName("LanguageSelectorPopup");
            var languageShow = languagePopup != null ? AccessTools.Method(languagePopup, "Show") : null;
            if (languageShow != null)
                TryPatch(harmony, languageShow, postfix: new HarmonyMethod(typeof(PauseMenuOptions), nameof(PauseMenuOptions.LanguageShown)));

            Log.LogInfo("Auto sew is ready. Open the pause menu for the switches.");
        }

        /// <summary>
        /// True while an F4 pass (auto measure or auto mannequin) is running.
        /// </summary>
        public static bool ManualAutomationActive => AutoMeasure.IsActive || AutoMannequin.IsActive;

        /// <summary>
        /// Stops both F4 features and updates the Automation window.
        /// </summary>
        public static void StopManualAutomation()
        {
            AutoMeasure.Cancel();
            AutoMannequin.Cancel();
            PauseMenuOptions.Refresh();
        }

        /// <summary>
        /// What F4 does: stops a running pass, or starts the right one for the current screen
        /// (measuring a client, or the mannequin). Only starts while the game is unpaused and the
        /// measuring screen is active, and only if that feature is switched on.
        /// </summary>
        public static void ToggleManualAutomation()
        {
            try
            {
                if (ManualAutomationActive)
                {
                    StopManualAutomation();
                    return;
                }
                // Starting is limited to the current, unpaused measuring screen.
                if (Time.timeScale <= 0f)
                    return;
                object scene = GameAccess.Find("MeasuringScene");
                if (!(scene is MonoBehaviour behaviour) || !behaviour.gameObject.activeInHierarchy)
                    return;
                bool mannequin = GameAccess.Bool(scene, "IsMannequinScene");
                var measurer = GameAccess.Member(scene, "ActiveMeasurer") as MonoBehaviour;
                if (mannequin ? MannequinEnabled : MeasureEnabled)
                {
                    StopManualAutomation();
                    bool started = mannequin ? AutoMannequin.Start(scene) : AutoMeasure.Start(measurer);
                    if (started)
                        Log.LogInfo("F4 started auto " + (mannequin ? "mannequin." : "measure."));
                    PauseMenuOptions.Refresh();
                }
            }
            catch (Exception ex)
            {
                StopManualAutomation();
                Log.LogWarning("F4 automation stopped: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Unity calls this every frame. Checks for F4 every frame. Four times a second it also refreshes
        /// the Automation window and lets AutoMeasure and AutoMannequin check that their session is still
        /// valid (Maintain), stopping them cleanly if anything throws.
        /// </summary>
        void Update()
        {
            // Poll every frame, before the slower session maintenance check.
            if (Input.GetKeyDown(KeyCode.F4))
                ToggleManualAutomation();
            if (Time.unscaledTime < _nextMaintenance)
                return;
            _nextMaintenance = Time.unscaledTime + 0.25f;
            PauseMenuOptions.Refresh();
            try { AutoMeasure.Maintain(); }
            catch (Exception ex)
            {
                AutoMeasure.Cancel();
                Log.LogWarning("Auto measure session ended: " + ex.GetBaseException().Message);
            }
            try { AutoMannequin.Maintain(); }
            catch (Exception ex)
            {
                AutoMannequin.Cancel();
                Log.LogWarning("Auto mannequin session ended: " + ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Runs when the plugin is unloaded: stops every feature and restores the game's state.
        /// </summary>
        void OnDestroy()
        {
            AutoSew.Cancel();
            AutoPlace.ClearCutHistory();
            PauseMenuOptions.HidePostfix();
            AutoMeasure.Cancel();
            AutoMannequin.Cancel();
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Unity calls this every frame after all normal Update calls. AutoSew sets the sewing hand's pose
        /// here, so the game's own hand code can't overwrite it in the same frame.
        /// </summary>
        void LateUpdate()
        {
            AutoSew.LateTick();
        }

        /// <summary>
        /// Installs one Harmony hook. A prefix runs before the game's method (and can skip it by returning
        /// false); a postfix runs after it. A failed hook is logged instead of stopping the mod.
        /// </summary>
        static void TryPatch(Harmony harmony, System.Reflection.MethodBase method, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                harmony.Patch(method, prefix, postfix);
            }
            catch (Exception ex)
            {
                Log.LogError("Could not hook " + method.DeclaringType.Name + "." + method.Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Setters used by the Automation window. They only write when the value really changes,
        /// which avoids triggering SettingChanged in a loop.
        /// </summary>
        public static void SetEnabled(bool value)
        {
            if (EnabledEntry != null && EnabledEntry.Value != value)
                EnabledEntry.Value = value;
        }

        /// <summary>
        /// Turns auto measure on or off (from the Automation window).
        /// </summary>
        public static void SetMeasureEnabled(bool value)
        {
            if (MeasureEntry != null && MeasureEntry.Value != value)
                MeasureEntry.Value = value;
        }

        /// <summary>
        /// Turns auto mannequin on or off (from the Automation window).
        /// </summary>
        public static void SetMannequinEnabled(bool value)
        {
            if (MannequinEntry != null && MannequinEntry.Value != value)
                MannequinEntry.Value = value;
        }

        /// <summary>
        /// Turns pattern layout on or off (from the Automation window).
        /// </summary>
        public static void SetPlaceEnabled(bool value)
        {
            if (PlaceEntry != null && PlaceEntry.Value != value)
                PlaceEntry.Value = value;
        }

        /// <summary>
        /// Changes the speed by a small amount (W/S keys, mouse wheel). Saving can be delayed to avoid
        /// writing the .cfg file every frame.
        /// </summary>
        public static void NudgeSpeed(float delta, bool saveNow)
        {
            if (Math.Abs(delta) < 0.0001f)
                return;
            _speed = ClampSpeed(_speed + delta);
            _speedDirty = true;
            if (saveNow)
                FlushSpeed(force: true);
            PauseMenuOptions.Refresh();
        }

        /// <summary>
        /// Sets the speed to an exact value (the slider). Saves now or marks it to be saved soon.
        /// </summary>
        public static void SetSpeed(float value, bool saveNow)
        {
            _speed = ClampSpeed(value);
            _speedDirty = true;
            if (saveNow)
                FlushSpeed(force: true);
            else
                PauseMenuOptions.Refresh();
        }

        /// <summary>
        /// Writes a changed speed to the .cfg file, at most once every 0.35 s unless forced.
        /// </summary>
        public static void FlushSpeed(bool force)
        {
            if (!_speedDirty || SpeedEntry == null)
                return;
            if (!force && UnityEngine.Time.unscaledTime < _nextSpeedSave)
                return;
            _nextSpeedSave = Time.unscaledTime + 0.35f;
            _speedDirty = false;
            if (Math.Abs(SpeedEntry.Value - _speed) > 0.001f)
                SpeedEntry.Value = _speed;
        }

        /// <summary>
        /// Keeps a speed value inside SpeedMin..SpeedMax. Invalid numbers (NaN, infinity) become the maximum.
        /// </summary>
        public static float ClampSpeed(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return SpeedMax;
            if (value < SpeedMin)
                return SpeedMin;
            if (value > SpeedMax)
                return SpeedMax;
            return value;
        }
    }
}
