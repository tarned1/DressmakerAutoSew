using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace DressmakerAutoSew
{
    public static class MlData
    {
        public static void Run()
        {
            Plugin.Log.LogInfo("MlData.Run was called");
            CheckGenerator();
        }

        // Part 4: your catalog code, unchanged, now in its own method.
        static void LogCatalog()
        {
            object all = GameAccess.Static("GarmentComponent", "AllGarmentComponents");
            if (!(all is IEnumerable components))
            {
                Plugin.Log.LogWarning("Could not find the garment catalog!");
                return;
            }

            int count = 0;
            foreach (object component in components)
            {
                if (component == null)
                    continue;
                count++;

                string type = GameAccess.Member(component, "GarmentComponentTypeName") as string;
                string name = GameAccess.Member(component, "prettyName") as string;
                string id = component is UnityEngine.Object unity ? unity.name : "?" ;

                int variations = 0;
                int panels = 0;
                if (GameAccess.Member(component, "variations") is IList variationList)
                {
                    variations = variationList.Count;
                    foreach (object variation in variationList)
                    {
                        if (GameAccess.Member(variation, "panels") is IList panelList)
                            panels += panelList.Count;
                    }
                }
                
                Plugin.Log.LogInfo(type + " [" + id + "]: " + name + ", " + variations + " variations, " + panels + " panels");
            }
            
            Plugin.Log.LogInfo("Catalog: " + count + "  garment parts.");
        }

        // Part 5: the new sanity check.
        static void CheckGenerator()
        {
            object dress = GameAccess.Member(GameAccess.Instance("GameManager"), "activeDress");
            if (dress == null)
            {
                Plugin.Log.LogWarning("No active dress. Start a dress for an order first.");
                return;
            }
            if (!(GameAccess.Member(dress, "PanelPieces") is IEnumerable panels))
            {
                Plugin.Log.LogWarning("The dress has no pattern pieces.");
                return;
            }
            object measurements = GameAccess.Member(GameAccess.Member(dress, "state"), "measurements");
            if (measurements == null)
            {
                Plugin.Log.LogWarning("The dress has no measurements.");
                return;
            }

            var generate = AccessTools.Method(AccessTools.TypeByName("MeshHelpers"), "GeneratePatternMesh");
            if (generate == null)
            {
                Plugin.Log.LogWarning("Could not find MeshHelpers.GeneratePatternMesh.");
                return;
            }

            int tested = 0;
            int matched = 0;
            foreach (object panel in panels)
            {
                var real = GameAccess.Member(panel, "PatternMesh") as Mesh;
                object definition = GameAccess.Member(panel, "definition");
                object component = GameAccess.Member(panel, "garmentComponent");
                var source = GameAccess.Member(definition, "mannequinMesh") as Mesh;
                object type = GameAccess.Member(component, "type");
                if (real == null || source == null || type == null)
                    continue;

                var made = generate.Invoke(null, new object[] { source, measurements, type }) as Mesh;
                if (made == null)
                    continue;

                float difference = MaxDifference(real.vertices, made.vertices);
                tested++;
                if (difference < 0.0001f)
                    matched++;
                Plugin.Log.LogInfo(AutoPlace.NameOf(panel) + ": " + real.vertexCount + " vs "
                    + made.vertexCount + " points, max difference " + difference);

                UnityEngine.Object.Destroy(made);
            }

            Plugin.Log.LogInfo("Generator check: " + matched + " of " + tested + " pieces match.");
        }

        // Largest distance between matching points of two shapes. 0 means identical.
        static float MaxDifference(Vector3[] a, Vector3[] b)
        {
            if (a.Length != b.Length)
                return float.PositiveInfinity;
            float max = 0f;
            for (int i = 0; i < a.Length; i++)
                max = Mathf.Max(max, Vector3.Distance(a[i], b[i]));
            return max;
        }
    }
}