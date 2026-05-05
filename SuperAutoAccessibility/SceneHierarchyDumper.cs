using System;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Dumps the full scene hierarchy in a condensed tree format.
    /// Triggered by F8. Saves to file and copies to clipboard.
    /// </summary>
    public static class SceneHierarchyDumper
    {
        private const int MAX_DEPTH = 15;
        private const string DUMP_PATH = "Mods/SceneDump.txt";

        public static void DumpAndSave()
        {
            try
            {
                var sb = new StringBuilder();
                var scene = SceneManager.GetActiveScene();
                sb.AppendLine($"=== Scene Dump: {scene.name} | Frame {Time.frameCount} ===");
                sb.AppendLine();

                var rootObjects = scene.GetRootGameObjects();
                foreach (var root in rootObjects)
                {
                    if (root == null) continue;
                    DumpGameObject(sb, root.transform, 0);
                }

                string dump = sb.ToString();

                // Save to file
                try
                {
                    System.IO.File.WriteAllText(DUMP_PATH, dump);
                    MelonLogger.Msg($"[Scene Dump] Saved to {DUMP_PATH} ({dump.Length} chars)");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Scene Dump] File write error: {ex.Message}");
                }

                AccessibilityManager.Announce($"Scene dumped. {rootObjects.Length} root objects.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Scene Dump] Error: {ex.Message}");
                AccessibilityManager.Announce("Scene dump failed");
            }
        }

        private static void DumpGameObject(StringBuilder sb, Transform transform, int depth)
        {
            if (transform == null || depth > MAX_DEPTH) return;

            var go = transform.gameObject;
            if (go == null) return;

            string indent = new string(' ', depth * 2);
            string active = go.activeInHierarchy ? "" : " [OFF]";
            int childCount = transform.childCount;

            // Build component list (condensed â€” just type names, skip Transform)
            string components = "";
            try
            {
                var comps = go.GetComponents<Component>();
                var compNames = new StringBuilder();
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetIl2CppType()?.Name ?? "?";
                    if (typeName == "Transform" || typeName == "RectTransform") continue;
                    if (compNames.Length > 0) compNames.Append(", ");
                    compNames.Append(typeName);
                }
                if (compNames.Length > 0)
                    components = $" <{compNames}>";
            }
            catch { }

            sb.AppendLine($"{indent}{go.name}{active}{components}");

            // Recurse children
            for (int i = 0; i < childCount; i++)
            {
                try
                {
                    var child = transform.GetChild(i);
                    DumpGameObject(sb, child, depth + 1);
                }
                catch { }
            }
        }
    }
}
