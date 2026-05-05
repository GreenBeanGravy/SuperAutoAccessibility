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
