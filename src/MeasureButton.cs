using UnityEngine;
using UnityEngine.UI;

namespace DressmakerAutoSew
{
    // A button above the measuring card's Confirm. It starts or stops Auto measure / Auto mannequin, while one runs, it says Stop.
    public static class MeasureButton
    {
        const string ButtonName = "AutoSewMeasureButton";
        static bool _failed;
        
        static GameObject _labeled;
        static string _labeledText;

        // Runs every frame while a measuring card is on screen (hooked in Plugin.cs).
        public static void Tick(MonoBehaviour __instance)
        {
            if (_failed)
                return;
            try
            {
                Ensure(__instance);
            }
            catch (System.Exception ex)
            {
                _failed = true;
                Plugin.Log.LogWarning("Measure button: couldn't add it: " + ex.Message);
            }
        }

        static void Ensure(MonoBehaviour measurer)
        {
            object paper = GameAccess.Member(measurer, "paper");
            var confirm = GameAccess.Member(paper, "confirmButton") as Button;
            if (confirm == null)
                return;
            Transform card = confirm.transform.parent;

            Transform existing = card.Find(ButtonName);
            GameObject made = existing != null ? existing.gameObject : Make(confirm, card);

            bool mannequin = GameAccess.Bool(paper, "IsMannequinScene");
            string text = Plugin.ManualAutomationActive ? "Stop" : mannequin ? "Auto mannequin" : "Auto measure";
            if (made != _labeled || text != _labeledText)
            {
                PauseMenuOptions.ForceLabel(made, text, null);
                _labeled = made;
                _labeledText = text;
            }
        }

        static GameObject Make(Button confirm, Transform card)
        {
            GameObject made = Object.Instantiate(confirm.gameObject, card, false);
            made.name = ButtonName;
            made.transform.SetSiblingIndex(confirm.transform.GetSiblingIndex());

            var button = made.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Plugin.ToggleManualAutomation);
            button.interactable = true;
            foreach (Image image in made.GetComponentsInChildren<Image>(true))
                image.enabled = true;
            return made;
        }
    }
}