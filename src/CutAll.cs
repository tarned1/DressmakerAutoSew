using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DressmakerAutoSew
{
    public static class CutAll
    {
        static bool _running;
        static GameObject _button;

        public static void Run()
        {
            if (_running)
                return;
            var room = GameAccess.Find("CuttingRoom") as MonoBehaviour;
            if (room == null || !GameAccess.SceneIs("CuttingRoom"))
            {
                Plugin.Log.LogInfo("Cut all: not in the cutting room.");
                return;
            }

            List<object> pieces = ReadyPieces();
            if (pieces.Count == 0)
            {
                Plugin.Log.LogInfo("Cut all: no pieces ready to cut.");
                return;
            }

            _running = true;
            AutoPlace.Runner().StartCoroutine(CutRoutine(room, pieces));
        }

        // Pieces lying on the table that the game's scissors would accept.
        static List<object> ReadyPieces()
        {
            var list = new List<object>();
            object dress = GameAccess.Member(GameAccess.Instance("GameManager"), "activeDress");
            if (!(GameAccess.Member(dress, "PanelPieces") is IEnumerable panels))
                return list;
            foreach (object panel in panels)
            {
                if (GameAccess.Bool(panel, "IsWorld3D")
                    && !GameAccess.Bool(panel, "HasBeenCut")
                    && !GameAccess.Bool(panel, "beingCut")
                    && !GameAccess.Bool(panel, "IsOverlapping"))
                    list.Add(panel);
            }
            return list;
        }

        // Starts the game's own scissors on every piece at once, and sends each piece back to the
        // menu (like a right-click) as soon as its own cut is finished.
        static IEnumerator CutRoutine(MonoBehaviour room, List<object> pieces)
        {
            int done = 0;
            try
            {
                var cut = AccessTools.Method(room.GetType(), "CutOutFabric");
                var send = AccessTools.Method(room.GetType(), "ReturnToInventoryAnimation");
                foreach (object piece in pieces)
                    cut.Invoke(room, new object[] { piece });

                var waiting = new List<object>(pieces);
                while (waiting.Count > 0 && GameAccess.SceneIs("CuttingRoom"))
                {
                    yield return null;
                    for (int i = waiting.Count - 1; i >= 0; i--)
                    {
                        object piece = waiting[i];
                        if (GameAccess.Bool(piece, "beingCut"))
                            continue;
                        waiting.RemoveAt(i);
                        if (!GameAccess.Bool(piece, "HasBeenCut"))
                            continue;
                        done++;
                        room.StartCoroutine((IEnumerator)send.Invoke(room, new object[] { piece }));
                    }
                }
            }
            finally
            {
                _running = false;
            }
            Plugin.Log.LogInfo("Cut all: cut " + done + " of " + pieces.Count + " pieces.");
        }
        
        // Adds a "Cut all" button to the cutting room's top toolbar, once per visit.
        // It's a clone of the game's own Scissors button, so it matches the game's style.
        public static void EnsureButton(MonoBehaviour room)
        {
            if (_button != null)
                return;
            object field = GameAccess.Member(room, "discardFabricButton");
            Transform discard = field is Component component ? component.transform : (field as GameObject)?.transform;
            Transform bar = discard?.parent;
            Transform scissors = bar?.Find("Scissors");
            if (scissors == null)
                return;

            _button = Object.Instantiate(scissors.gameObject, bar, false);
            _button.name = "AutoSewCutAll";

            var button = _button.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Run);

            var tooltip = _button.GetComponent("UiTooltip");
            if (tooltip != null)
                Object.Destroy(tooltip);
            _button.AddComponent<AutoSewTooltip>().Text = "Cut all placed pieces";
            
            DoubleIcon(FindDeep(_button.transform, "SeamsVisible"));
            PlaceAfterLast(bar, _button.transform as RectTransform);
        }
        
        // Like transform.Find, but searches every level inside, not only direct children.
        static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name)
                    return child;
            Plugin.Log.LogWarning("Cut all: couldn't find " + name + " in the button.");
            return null;
        }

        // Two scissors crossed into an X: the original, plus a copy turned 90° on the same spot,
        // drawn behind it, and the whole X tilted a little so both handles sit level.
        static void DoubleIcon(Transform icon)
        {
            var rect = icon as RectTransform;
            if (rect == null)
                return;
            var copy = Object.Instantiate(rect.gameObject, rect.parent, false).transform as RectTransform;
            Vector3 scale = rect.localScale * 0.9f;
            rect.localScale = scale;
            copy.localScale = new Vector3(-scale.x, scale.y, scale.z);
            copy.localEulerAngles = rect.localEulerAngles + new Vector3(0f, 0f, -90f);
            copy.SetSiblingIndex(rect.GetSiblingIndex());
            Vector3 tilt = new Vector3(0f, 0f, 11f);
            rect.localEulerAngles += tilt;
            copy.localEulerAngles += tilt;
            rect.anchoredPosition += new Vector2(0f, 1.5f);
            copy.anchoredPosition += new Vector2(0f, 1.5f);
        }

        // Puts the new button one step to the right of the last game button.
        static void PlaceAfterLast(Transform bar, RectTransform mine)
        {
            int count = bar.childCount - 1;
            if (count < 2 || mine == null)
                return;
            var last = bar.GetChild(count - 1) as RectTransform;
            var before = bar.GetChild(count - 2) as RectTransform;
            mine.anchoredPosition = last.anchoredPosition + (last.anchoredPosition - before.anchoredPosition);
        }
    }
}