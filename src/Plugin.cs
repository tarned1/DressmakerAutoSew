using System;
using BepInEx;
using UnityEngine;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DressmakerAutoSew
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("Dressmaker.exe")]
    [DefaultExecutionOrder(10000)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.Venus.dressmaker.autosew";
        public const string Name = "Dressmaker Auto Sew";
        public const string Version = "2.0.0";

        public const float SpeedMin = 0f;
        public const float SpeedMax = 1f;

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<bool> EnabledEntry { get; private set; }
        public static ConfigEntry<float> SpeedEntry { get; private set; }

        public static bool Enabled => EnabledEntry != null && EnabledEntry.Value;

        static float _speed = 1f;
        static bool _speedDirty;
        static float _nextSpeedSave;
        float _nextMaintenance;

        public static float Speed => _speed;

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

            _speed = ClampSpeed(SpeedEntry.Value);
            EnabledEntry.SettingChanged += (_, __) =>
            {
                if (!Enabled)
                    AutoSew.Cancel();
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
            {
                TryPatch(harmony, measureUpdate, postfix: new HarmonyMethod(typeof(AutoMeasure), nameof(AutoMeasure.Tick)));
                TryPatch(harmony, measureUpdate, postfix: new HarmonyMethod(typeof(MeasureButton), nameof(MeasureButton.Tick)));
            }
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
            
            var mannequinScene = AccessTools.TypeByName("MannequinScene");
            var mannequinUpdate = mannequinScene != null ? AccessTools.Method(mannequinScene, "Update") : null;
            if (mannequinUpdate != null)
                TryPatch(harmony, mannequinUpdate, postfix: new HarmonyMethod(typeof(PlaceAll), nameof(PlaceAll.Tick)));
            else
                Log.LogWarning("MannequinScene was not found. Place all cannot run.");
            
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

        public static bool ManualAutomationActive => AutoMeasure.IsActive || AutoMannequin.IsActive;

        public static void StopManualAutomation()
        {
            AutoMeasure.Cancel();
            AutoMannequin.Cancel();
            PauseMenuOptions.Refresh();
        }

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
                StopManualAutomation();
                bool started = mannequin ? AutoMannequin.Start(scene) : AutoMeasure.Start(measurer);
                if (started)
                    Log.LogInfo("Started auto " + (mannequin ? "mannequin." : "measure."));
                PauseMenuOptions.Refresh();
            }
            catch (Exception ex)
            {
                StopManualAutomation();
                Log.LogWarning("Auto measure/mannequin stopped: " + ex.GetBaseException().Message);
            }
        }

        void Update()
        {
            // Poll every frame, before the slower session maintenance check.
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

        void LateUpdate()
        {
            AutoSew.LateTick();
        }

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

        public static void SetEnabled(bool value)
        {
            if (EnabledEntry != null && EnabledEntry.Value != value)
                EnabledEntry.Value = value;
        }

        public static void SetSpeed(float value, bool saveNow)
        {
            _speed = ClampSpeed(value);
            _speedDirty = true;
            if (saveNow)
                FlushSpeed(force: true);
            else
                PauseMenuOptions.Refresh();
        }

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
