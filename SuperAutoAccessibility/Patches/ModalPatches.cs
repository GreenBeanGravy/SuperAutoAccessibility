using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SuperAutoAccessibility.Patches
{
    public static class ModalPatches
    {
        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            try
            {
                // Patch Awake for modal stack tracking
                var awakeOriginal = AccessTools.Method(
                    typeof(Il2CppSpacewood.Unity.UI.Modal),
                    "Awake"
                );

                var awakePostfix = AccessTools.Method(
                    typeof(ModalPatches),
                    nameof(Awake_Postfix)
                );

                if (awakeOriginal != null && awakePostfix != null)
                {
                    harmony.Patch(awakeOriginal, postfix: new HarmonyMethod(awakePostfix));
                    MelonLogger.Msg("Modal.Awake patched successfully");
                }
                else
                {
                    MelonLogger.Warning("Failed to find Modal.Awake");
                }

                // Patch OnEnable for announcement when modal becomes visible
                // OnEnable is called when the GameObject is activated - content should be ready
                var onEnableOriginal = AccessTools.Method(
                    typeof(MonoBehaviour),
                    "OnEnable"
                );

                // Since Modal doesn't override OnEnable, we use a different approach:
                // We'll register for announcement in Awake and use a coroutine or
                // check in the next frame via ProcessPending with a very short delay
                MelonLogger.Msg("Modal patches initialized - using deferred announcement");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error applying Modal patch: {ex.Message}");
            }
        }

        private static void Awake_Postfix(Il2CppSpacewood.Unity.UI.Modal __instance)
        {
            if (__instance == null || __instance.gameObject == null) return;

            try
            {
                MelonLogger.Msg($"[Modal] Awake fired for: {__instance.gameObject.name}");
                AccessibilityManager.PushModal(__instance);
                // Queue for announcement - we'll use a coroutine to wait for content
                AccessibilityManager.QueueModalAnnouncement(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in Modal.Awake postfix: {ex.Message}");
            }
        }
    }
}
