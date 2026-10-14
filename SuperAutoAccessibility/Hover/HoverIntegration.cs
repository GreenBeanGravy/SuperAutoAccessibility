using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SuperAutoAccessibility.Hover
{
    /// <summary>
    /// Handles mouse hover announcements
    /// </summary>
    public static class HoverIntegration
    {
        private static GameObject _lastHoveredElement;
        private static float _lastHoverTime;
        private const float HOVER_THROTTLE = 0.2f;

        /// <summary>
        /// Called when a selectable element is hovered
        /// </summary>
        internal static bool CoversScreen(GameObject element)
        {
            try
            {
                var rt = element.GetComponent<RectTransform>();
                var canvas = element.GetComponentInParent<Canvas>();
                if (rt == null || canvas == null) return false;
                canvas = canvas.rootCanvas;
                var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                UnityA11y.Unity.Compat.GetWorldCorners(rt, out Vector3 bottomLeft, out Vector3 topRight);
                Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, bottomLeft);
                Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, topRight);
                return Mathf.Abs(b.x - a.x) >= Screen.width * 0.9f && Mathf.Abs(b.y - a.y) >= Screen.height * 0.9f;
            }
            catch
            {
                return false;
            }
        }

        public static void OnElementHovered(GameObject element)
        {
            if (element == null)
                return;

            try
            {
                // Throttle rapid hovers
                if (element == _lastHoveredElement &&
                    Time.time - _lastHoverTime < HOVER_THROTTLE)
                {
                    return;
                }

                // A menu's invisible full-screen backdrop (Settings opened during a run closes on a
                // click outside it, through a "Back" button covering the screen) is "hovered"
                // wherever the mouse rests, so every opening said "Back, button".
                if (CoversScreen(element)) return;

                // Announce the element
                AccessibilityManager.AnnounceSelectedElement(element, true);

                _lastHoveredElement = element;
                _lastHoverTime = Time.time;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in hover integration: {ex.Message}");
            }
        }
    }
}
