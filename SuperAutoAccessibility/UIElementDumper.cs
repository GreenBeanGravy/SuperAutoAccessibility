using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;
using Il2CppSpacewood.Unity.UI;
using Il2CppTMPro;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Dumps all UI elements in the scene to console and clipboard for debugging.
    /// Triggered by F6 or automatically on page change.
    /// </summary>
    public static class UIElementDumper
    {
        // Deferred dump (page change triggers dump after frame delay)
        private static bool _pendingDump = false;
        private static int _dumpFrameDelay = 0;
        private const int DUMP_FRAME_DELAY = 15;

        public static void QueueDump()
        {
            _pendingDump = true;
            _dumpFrameDelay = DUMP_FRAME_DELAY;
        }

        public static void ProcessPending()
        {
            if (!_pendingDump) return;

            _dumpFrameDelay--;
            if (_dumpFrameDelay > 0) return;

            _pendingDump = false;
            DumpAndCopy();
        }

        /// <summary>
        /// Dump all UI elements to console and copy to clipboard.
        /// </summary>
        public static void DumpAndCopy()
        {
            try
            {
                string dump = DumpAllElements();
                MelonLogger.Msg(dump);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"UI Dump error: {ex.Message}");
            }
        }

        public static string DumpAllElements()
        {
            StringBuilder sb = new StringBuilder();

            // Get page name
            string pageName = "Unknown";
            try
            {
                var pm = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                if (pm?.CurrentPage?.gameObject != null)
                    pageName = pm.CurrentPage.gameObject.name;
            }
            catch { }

            // Check sidebar state
            string sidebarState = "Closed";
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Menu>();
                if (menu?.SideBar?.Container?.gameObject?.activeInHierarchy == true)
                    sidebarState = "Open";
            }
            catch { }

            sb.AppendLine($"=== UI DUMP: {pageName} | Sidebar: {sidebarState} ===");

            // Dump all SelectableBase elements
            var selectables = UnityEngine.Object.FindObjectsOfType<SelectableBase>();
            // Sort by hierarchy for readability
            var sortedSelectables = new List<SelectableBase>();
            foreach (var s in selectables)
            {
                if (s?.gameObject != null) sortedSelectables.Add(s);
            }
            sortedSelectables.Sort((a, b) =>
            {
                string pathA = GetShortPath(a.gameObject);
                string pathB = GetShortPath(b.gameObject);
                return string.Compare(pathA, pathB, StringComparison.Ordinal);
            });

            foreach (var s in sortedSelectables)
            {
                try
                {
                    string path = GetShortPath(s.gameObject);
                    string text = TextExtractor.GetElementText(s.gameObject);
                    string type = GetComponentType(s.gameObject);
                    bool active = s.gameObject.activeInHierarchy;
                    bool interactable = false;
                    try { interactable = s.GetInteractable(); } catch { }
                    sb.AppendLine($"[Sel] {path} | \"{text}\" | {type} | {(active ? "A" : "I")} | {(interactable ? "E" : "D")}");
                }
                catch { }
            }

            // Dump standalone text elements (not under a SelectableBase)
            var texts = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();
            foreach (var t in texts)
            {
                try
                {
                    if (t?.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (string.IsNullOrWhiteSpace(t.text)) continue;
                    // Skip if under a SelectableBase (already covered above)
                    if (t.GetComponentInParent<SelectableBase>() != null) continue;

                    string path = GetShortPath(t.gameObject);
                    string cleanText = t.text.Trim().Replace("\n", " ");
                    if (cleanText.Length > 80) cleanText = cleanText.Substring(0, 80) + "...";
                    sb.AppendLine($"[Txt] {path} | \"{cleanText}\"");
                }
                catch { }
            }

            sb.AppendLine("===");
            return sb.ToString();
        }

        /// <summary>
        /// Gets a short path: parent/name (2 levels max)
        /// </summary>
        private static string GetShortPath(GameObject go)
        {
            if (go == null) return "null";

            string path = go.name;
            Transform parent = go.transform.parent;
            if (parent != null)
            {
                path = parent.name + "/" + path;
                Transform grandparent = parent.parent;
                if (grandparent != null)
                {
                    path = grandparent.name + "/" + path;
                }
            }
            return path;
        }

        /// <summary>
        /// Gets the specific UI component type name
        /// </summary>
        private static string GetComponentType(GameObject go)
        {
            if (go.GetComponent<ButtonBase>() != null) return "Btn";
            if (go.GetComponent<SliderBase>() != null) return "Sld";
            if (go.GetComponent<DropdownBase>() != null) return "Drp";
            if (go.GetComponent<InputFieldBase>() != null) return "Inp";
            return "Sel";
        }
    }
}
