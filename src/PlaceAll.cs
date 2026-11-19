using System.Collections;
using HarmonyLib;

namespace DressmakerAutoSew
{
    public static class PlaceAll
    {
        public static void Run()
        {
            if (!GameAccess.SceneIs("Mannequin"))
            {
                Plugin.Log.LogInfo("Place all: not in the mannequin scene");
                return;
            }

            object dress = GameAccess.Member(GameAccess.Instance("GameManager"), "activeDress");
            if (!(GameAccess.Member(dress, "PanelPieces") is IEnumerable panels))
            {
                Plugin.Log.LogWarning("Place all: no active dress.");
                return;
            }
            
            int ready = 0;
            foreach (object panel in panels)
            {
                bool cut = GameAccess.Bool(panel, "HasBeenCut");
                bool onMannequin = GameAccess.Bool(panel, "VisibleOnMannequin");
                bool stitched = GameAccess.Bool(panel, "HasBeenStitched");
                if (cut && !onMannequin)
                    ready++;
                Plugin.Log.LogInfo("Place all: " + AutoPlace.NameOf(panel)
                                                 + " | cut " + cut + " | on mannequin " + onMannequin + " | stitched " + stitched);   
            }

            Plugin.Log.LogInfo("Place all: " + ready + " pieces ready to place.");
        }
    }
}