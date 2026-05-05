using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2CppTMPro;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Utility class for extracting text content from UI GameObjects
    /// </summary>
    public static class TextExtractor
    {
        /// <summary>
        /// Extracts all visible text from a GameObject and its children
        /// </summary>
        public static string GetElementText(GameObject go)
        {
            if (go == null) return "";

            List<string> textParts = new List<string>();

            try
            {
                // Get Unity UI Text components
                var uiTexts = go.GetComponentsInChildren<Text>(true);
                foreach (var text in uiTexts)
                {
                    if (IsTextVisible(text.gameObject) && !string.IsNullOrWhiteSpace(text.text))
                    {
                        textParts.Add(CleanText(text.text));
                    }
                }

                // Get TextMeshPro UGUI components
                var tmpTexts = go.GetComponentsInChildren<TextMeshProUGUI>(true);
                foreach (var text in tmpTexts)
                {
                    if (IsTextVisible(text.gameObject) && !string.IsNullOrWhiteSpace(text.text))
                    {
                        textParts.Add(CleanText(text.text));
                    }
                }

                // Get TextMeshPro 3D components (just in case)
                var tmp3DTexts = go.GetComponentsInChildren<TextMeshPro>(true);
                foreach (var text in tmp3DTexts)
                {
                    if (IsTextVisible(text.gameObject) && !string.IsNullOrWhiteSpace(text.text))
                    {
                        textParts.Add(CleanText(text.text));
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Error extracting text: {ex.Message}");
            }

            // Remove duplicates and join
            return string.Join(", ", textParts.Distinct().Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        /// <summary>
        /// Checks if a text component is actually visible
        /// </summary>
        private static bool IsTextVisible(GameObject go)
        {
            // Check if GameObject is active
            if (!go.activeInHierarchy)
                return false;

            return true;
        }

        /// <summary>
        /// Public wrapper for CleanText â€” used by callers outside TextExtractor.
        /// </summary>
        public static string CleanTextPublic(string text) => CleanText(text);

        /// <summary>
        /// Cleans up text for speech (removes extra whitespace, special chars, etc.)
        /// </summary>
        private static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // Remove rich text tags first (they may contain newlines)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");

            // Collapse all whitespace (newlines, tabs, multiple spaces) into single space
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            // Trim leading/trailing
            text = text.Trim();

            return text;
        }

        /// <summary>
        /// Gets the GameObject hierarchy path (useful for debugging)
        /// </summary>
        public static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return "null";

            string path = go.name;
            Transform current = go.transform;

            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }

            return path;
        }

        /// <summary>
        /// Gets ALL text from a hierarchy in reading order (top-to-bottom, left-to-right).
        /// Used for reading full dialog/page content.
        /// </summary>
        public static string GetAllTextFromHierarchy(GameObject root)
        {
            if (root == null) return "";

            var textElements = new List<(float y, float x, string text)>();

            try
            {
                CollectAllTextElements(root.transform, textElements);

                // Sort by reading order: top-to-bottom (high Y first), left-to-right (low X first)
                textElements.Sort((a, b) =>
                {
                    int cmp = b.y.CompareTo(a.y); // Higher Y = top
                    if (cmp != 0) return cmp;
                    return a.x.CompareTo(b.x);    // Lower X = left
                });

                // Deduplicate and join
                var seen = new HashSet<string>();
                var result = new List<string>();
                foreach (var (_, _, text) in textElements)
                {
                    if (!seen.Contains(text) && !string.IsNullOrWhiteSpace(text))
                    {
                        seen.Add(text);
                        result.Add(text);
                    }
                }

                return string.Join(". ", result);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Error getting all text from hierarchy: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Recursively collects all text elements with their screen positions
        /// </summary>
        private static void CollectAllTextElements(Transform t, List<(float y, float x, string text)> elements)
        {
            // Check for TextMeshProUGUI
            var tmp = t.GetComponent<TextMeshProUGUI>();
            if (tmp != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(tmp.text))
            {
                var pos = GetScreenPosition(t);
                elements.Add((pos.y, pos.x, CleanText(tmp.text)));
            }

            // Check for Unity UI Text
            var uiText = t.GetComponent<Text>();
            if (uiText != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(uiText.text))
            {
                var pos = GetScreenPosition(t);
                elements.Add((pos.y, pos.x, CleanText(uiText.text)));
            }

            // Recurse to children
            for (int i = 0; i < t.childCount; i++)
            {
                CollectAllTextElements(t.GetChild(i), elements);
            }
        }

        /// <summary>
        /// Gets the screen position of a transform for reading order sorting
        /// </summary>
        private static Vector2 GetScreenPosition(Transform t)
        {
            var rt = t.GetComponent<RectTransform>();
            if (rt != null && Camera.main != null)
            {
                Vector3 sp = Camera.main.WorldToScreenPoint(rt.position);
                return new Vector2(sp.x, sp.y);
            }
            return new Vector2(t.position.x, t.position.y);
        }
    }
}
