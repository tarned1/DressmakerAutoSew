using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DressmakerAutoSew
{
    public static class PlaceAll
    {
        const float TotalTime = 3f;   // seconds for the whole dress
        const float MaxDelay = 0.3f;  // longest wait between two pieces
        const float SoundGap = 0.2f;  // shortest time between two drop sounds
        const string IconSprite = "mannequin";  // which game sprite the button shows

        static bool _running;
        
        static GameObject _button;
        static bool _buttonFailed;

        // A button: puts every cut piece that isn't on the mannequin yet onto it, one after another.
        public static void Run()
        {
            if (_running)
                return;
            var scene = GameAccess.Find("MannequinScene") as MonoBehaviour;
            if (scene == null || !GameAccess.SceneIs("Mannequin"))
            {
                Plugin.Log.LogInfo("Place all: not in the mannequin scene.");
                return;
            }

            List<object> pieces = ReadyPieces();
            if (pieces.Count == 0)
            {
                Plugin.Log.LogInfo("Place all: no pieces ready to place.");
                return;
            }

            _running = true;
            AutoPlace.Runner().StartCoroutine(PlaceRoutine(scene, pieces));
        }
        
        // Runs every frame on the mannequin screen (hooked in Plugin.cs). Adds the button once per visit.
        public static void Tick(MonoBehaviour __instance)
        {
            if (_button != null || _buttonFailed)
                return;
            try
            {
                MakeButton(__instance);
            }
            catch (System.Exception ex)
            {
                _buttonFailed = true;
                Plugin.Log.LogWarning("Place all: couldn't add the button: " + ex.Message);
            }
        }

        // A clone of the game's mannequin-color button, placed to its left.
        static void MakeButton(MonoBehaviour scene)
        {
            var picker = GameAccess.Member(scene, "mannequinColorPickerButton") as GameObject;
            if (picker == null)
                return;

            _button = Object.Instantiate(picker, picker.transform.parent, false);
            _button.name = "AutoSewPlaceAll";
            _button.SetActive(true);

            var button = _button.GetComponent<Button>();
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(Run);
            }
            var tooltip = _button.GetComponent("UiTooltip");
            if (tooltip != null)
                Object.Destroy(tooltip);
            _button.AddComponent<AutoSewTooltip>().Text = "Place all cut pieces";

            var mine = _button.transform as RectTransform;
            var theirs = picker.transform as RectTransform;
            var seamRip = picker.transform.parent.Find("SeamRip") as RectTransform;
            Vector2 step = seamRip != null ? seamRip.anchoredPosition - theirs.anchoredPosition : new Vector2(125f, 0f);
            mine.anchoredPosition = theirs.anchoredPosition - step;

            SetIcon(FindDeep(_button.transform, "icon"));
        }
        
        // Swaps the cloned dropper picture for one of the game's own sprites.
        static void SetIcon(Transform icon)
        {
            var image = icon != null ? icon.GetComponent<Image>() : null;
            if (image == null)
                return;
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite.name == IconSprite)
                {
                    image.sprite = sprite;
                    image.preserveAspect = true;
                    image.transform.localScale *= 1.15f;
                    return;
                }
            Plugin.Log.LogWarning("Place all: no sprite named " + IconSprite + ".");
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name)
                    return child;
            return null;
        }

        static IEnumerator PlaceRoutine(MonoBehaviour scene, List<object> pieces)
        {
            float delay = Mathf.Min(TotalTime / pieces.Count, MaxDelay);
            float nextSound = 0f;
            int done = 0;
            try
            {
                foreach (object piece in pieces)
                {
                    if (!GameAccess.SceneIs("Mannequin"))
                        break;
                    if (GameAccess.Bool(piece, "PlacedOnMannequin"))
                        continue;

                    bool withSound = Time.time >= nextSound;
                    if (withSound)
                        nextSound = Time.time + SoundGap;
                    PlaceOne(scene, piece, withSound);
                    done++;
                    yield return new WaitForSeconds(delay);
                }

                if (done > 0 && GameAccess.SceneIs("Mannequin"))
                    AfterPlacing(scene);
            }
            finally
            {
                _running = false;
            }
            Plugin.Log.LogInfo("Place all: placed " + done + " of " + pieces.Count + " pieces.");
        }

        // Pieces that are cut but not on the mannequin yet.
        static List<object> ReadyPieces()
        {
            var ready = new List<object>();
            object dress = GameAccess.Member(GameAccess.Instance("GameManager"), "activeDress");
            if (!(GameAccess.Member(dress, "PanelPieces") is IEnumerable panels))
                return ready;
            foreach (object panel in panels)
                if (GameAccess.Bool(panel, "HasBeenCut") && !GameAccess.Bool(panel, "PlacedOnMannequin"))
                    ready.Add(panel);
            return ready;
        }

        // What the game does to a piece when you drop it on the mannequin: take it out of the menu,
        // animate it from there onto the body, and play the sound.
        static void PlaceOne(MonoBehaviour scene, object piece, bool withSound)
        {
            var button = GameAccess.Member(piece, "InventoryButton") as Component;
            var flat = GameAccess.Member(piece, "WorldObj") as GameObject;
            if (button != null && flat != null)
            {
                flat.transform.position = MenuToWorld(scene, button.transform as RectTransform, flat.transform.position);
                button.gameObject.SetActive(false);
            }

            AccessTools.Method(piece.GetType(), "ShowRepresentationMannequin")
                ?.Invoke(piece, new object[] { true });
            if (!withSound)
                return;

            object sound = GameAccess.Member(GameAccess.Instance("SoundController"), "placeFabricOnMannequin");
            var play = AccessTools.FirstMethod(AccessTools.TypeByName("EffectSoundBankExtenstions"),
                method => method.Name == "Play" && method.GetParameters().Length == 1);
            if (sound != null && play != null)
                play.Invoke(null, new[] { sound });
        }
        
        // The point in the 3D room that's behind the menu button on screen, at the mannequin's distance.
        static Vector3 MenuToWorld(MonoBehaviour scene, RectTransform button, Vector3 fallback)
        {
            var camera = GameAccess.Member(GameAccess.Instance("GameManager"), "mainCamera") as Camera;
            var mannequin = GameAccess.Member(scene, "mannequin") as Component;
            if (camera == null || mannequin == null || button == null)
                return fallback;

            Canvas canvas = button.GetComponentInParent<Canvas>()?.rootCanvas;
            Camera uiCamera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(uiCamera, button.position);

            Vector3 toMannequin = mannequin.transform.position - camera.transform.position;
            float depth = Vector3.Dot(toMannequin, camera.transform.forward);
            return camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth));
        }

        // What the game does after a drop: scores, the "dress finished?" check, and the sew buttons.
        static void AfterPlacing(MonoBehaviour scene)
        {
            AccessTools.Method(scene.GetType(), "UpdateScores")?.Invoke(scene, null);

            object game = GameAccess.Instance("GameManager");
            if (!SewingComplete(game))
            {
                AccessTools.Method(game.GetType(), "CheckDressCompletion")?.Invoke(game, null);
                if (SewingComplete(game))
                    AccessTools.Method(scene.GetType(), "Show")?.Invoke(scene, null);
            }

            AccessTools.Method(scene.GetType(), "UpdateSewButtons")?.Invoke(scene, new object[] { true });
        }

        static bool SewingComplete(object game)
        {
            object state = GameAccess.Member(GameAccess.Member(game, "activeDress"), "state");
            return GameAccess.Bool(state, "sewingComplete");
        }
    }
}