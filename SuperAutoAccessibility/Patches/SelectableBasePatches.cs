using System;
using HarmonyLib;
using MelonLoader;
using SuperAutoAccessibility.Hover;

namespace SuperAutoAccessibility.Patches
{
    /// <summary>
    /// Patches for game's SelectableBase class (hover events)
    /// </summary>
    public static class SelectableBasePatches
    {
        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            try
            {
                MelonLogger.Msg("Applying SelectableBase patches...");

                // Try to patch the IPointerEnterHandler interface implementation instead
                var originalMethod = AccessTools.Method(
                    typeof(Il2CppSpacewood.Unity.UI.SelectableBase),
                    "UnityEngine.EventSystems.IPointerEnterHandler.OnPointerEnter"
                );

                // If that doesn't work, try the public OnPointerEnter from the interface
                if (originalMethod == null)
                {
                    originalMethod = AccessTools.Method(
                        typeof(Il2CppSpacewood.Unity.UI.SelectableBase),
                        "OnPointerEnter"
                    );
                }

                var postfixMethod = AccessTools.Method(
                    typeof(SelectableBasePatches),
                    nameof(PointerEnter_Postfix)
                );

                if (originalMethod != null && postfixMethod != null)
                {
                    harmony.Patch(originalMethod, postfix: new HarmonyMethod(postfixMethod));
                    MelonLogger.Msg($"SelectableBase pointer enter patched successfully: {originalMethod.Name}");
                }
                else
                {
                    MelonLogger.Warning("Failed to find SelectableBase pointer enter method - hover announcements disabled for safety");
                    MelonLogger.Msg("Hover announcements will be disabled, but keyboard navigation will work fine");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error applying SelectableBase patches: {ex.Message}");
                MelonLogger.Msg("Hover announcements will be disabled, but keyboard navigation will work fine");
            }
        }

        /// <summary>
        /// Postfix patch for SelectableBase.PointerEnter
        /// Called when mouse hovers over an element
        /// </summary>
        private static void PointerEnter_Postfix(Il2CppSpacewood.Unity.UI.SelectableBase __instance)
        {
            if (__instance == null || __instance.gameObject == null)
                return;

            try
            {
                HoverIntegration.OnElementHovered(__instance.gameObject);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in PointerEnter_Postfix: {ex.Message}");
            }
        }
    }
}
