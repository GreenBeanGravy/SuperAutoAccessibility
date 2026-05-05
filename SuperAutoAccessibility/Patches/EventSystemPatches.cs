using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SuperAutoAccessibility.Patches
{
    public static class EventSystemPatches
    {
        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            try
            {
                var original = AccessTools.Method(
                    typeof(EventSystem),
                    "SetSelectedGameObject",
                    new[] { typeof(GameObject), typeof(BaseEventData) }
                );

                var postfix = AccessTools.Method(
                    typeof(EventSystemPatches),
                    nameof(SetSelectedGameObject_Postfix)
                );

                if (original != null && postfix != null)
                {
                    harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                    MelonLogger.Msg("EventSystem.SetSelectedGameObject patched successfully");
                }
                else
                {
                    MelonLogger.Warning("Failed to find EventSystem.SetSelectedGameObject");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error applying EventSystem patch: {ex.Message}");
            }
        }

        private static void SetSelectedGameObject_Postfix(GameObject selected)
        {
            if (selected == null) return;

            // Only announce if this selection was driven by our mod (keyboard nav).
            // Mouse clicks and game-driven selections are ignored so they don't
            // interfere with keyboard navigation position tracking.
            bool modDriven = SectionManager.ConsumeModDrivenFlag();
            if (!modDriven) return;

            try
            {
                AccessibilityManager.AnnounceSelectedElement(selected);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in SetSelectedGameObject postfix: {ex.Message}");
            }
        }
    }
}
