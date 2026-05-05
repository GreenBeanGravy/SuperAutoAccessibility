using System;
using HarmonyLib;
using MelonLoader;

namespace SuperAutoAccessibility.Patches
{
    public static class PopupManagerPatches
    {
        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            try
            {
                var original = AccessTools.Method(
                    typeof(Il2CppSpacewood.Unity.PopupManager),
                    "AddMessage",
                    new[] { typeof(string), typeof(float) }
                );

                var postfix = AccessTools.Method(
                    typeof(PopupManagerPatches),
                    nameof(AddMessage_Postfix)
                );

                if (original != null && postfix != null)
                {
                    harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                    MelonLogger.Msg("PopupManager.AddMessage patched successfully");
                }
                else
                {
                    MelonLogger.Warning("Failed to find PopupManager.AddMessage");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error applying PopupManager patch: {ex.Message}");
            }
        }

        private static void AddMessage_Postfix(string message, float time)
        {
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                AccessibilityManager.Announce(message, interrupt: false);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in PopupManager.AddMessage postfix: {ex.Message}");
            }
        }
    }
}
