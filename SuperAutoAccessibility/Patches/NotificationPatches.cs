using System;
using HarmonyLib;
using MelonLoader;
using Il2CppSpacewood.Unity.MonoBehaviours.Build;
using Il2CppSpacewood.Unity.Views;

namespace SuperAutoAccessibility.Patches
{
    /// <summary>
    /// The game flashes a short message over the board when an action fails ("Not enough gold.",
    /// a full team). A sighted player sees it; without this the action just did nothing.
    /// </summary>
    public static class NotificationPatches
    {
        /// <summary>Messages the game has flashed so far: an action that raised one did not happen.</summary>
        public static int Count { get; private set; }

        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, typeof(HangarMain), "NotifyBoard", nameof(NotifyBoard_Postfix));
            Patch(harmony, typeof(Space), "SetNotice", nameof(SetNotice_Postfix));
        }

        private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string postfix)
        {
            try
            {
                var original = AccessTools.Method(type, method, new[] { typeof(string) });
                var patch = AccessTools.Method(typeof(NotificationPatches), postfix);
                if (original == null || patch == null)
                {
                    MelonLogger.Warning($"Failed to find {type.Name}.{method}");
                    return;
                }
                harmony.Patch(original, postfix: new HarmonyMethod(patch));
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error patching {type.Name}.{method}: {ex.Message}");
            }
        }

        private static void NotifyBoard_Postfix(string __0)
        {
            Count++;
            string message = TextExtractor.CleanTextPublic(__0 ?? "");
            MelonLogger.Msg($"[Board notification] {message}");
            if (!string.IsNullOrWhiteSpace(message)) AccessibilityManager.Announce(message);
        }

        // Only logged for now: what a slot's notice says, and when.
        private static void SetNotice_Postfix(string __0)
        {
            DebugLog.Msg($"[Slot notice] {__0}");
        }
    }
}
