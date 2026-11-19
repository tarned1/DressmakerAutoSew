using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DressmakerAutoSew
{
    // Shows a text in the game's hover label, like the game's own buttons. Used by AutoSew's buttons.
    public class AutoSewTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string Text;

        public void OnPointerEnter(PointerEventData eventData)
        {
            object label = GameAccess.Member(GameAccess.Instance("GameManager"), "hoverLabel");
            if (label != null)
                AccessTools.Method(label.GetType(), "ShowLabel")
                    ?.Invoke(label, new object[] { gameObject, Text });
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            object label = GameAccess.Member(GameAccess.Instance("GameManager"), "hoverLabel");
            if (label != null)
                AccessTools.Method(label.GetType(), "HideLabel", new[] { typeof(GameObject) })
                    ?.Invoke(label, new object[] { gameObject });
        }
    }
}