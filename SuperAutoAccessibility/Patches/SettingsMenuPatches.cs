using System;
using HarmonyLib;
using MelonLoader;
using Il2CppSpacewood.Unity.UI;

namespace SuperAutoAccessibility.Patches
{
    public static class SettingsMenuPatches
    {
        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            string[] tabMethodNames = {
                "HandleTabGeneral",
                "HandleTabGameplay",
                "HandleTabAudio",
                "HandleTabDisplay",
                "HandleTabCustomize",
                "HandleTabPrivacy"
            };

            var postfix = AccessTools.Method(
                typeof(SettingsMenuPatches),
                nameof(HandleTab_Postfix)
            );

            if (postfix == null)
            {
                MelonLogger.Warning("Failed to find SettingsMenuPatches.HandleTab_Postfix");
                return;
            }

            int patchedCount = 0;
            foreach (var methodName in tabMethodNames)
            {
                try
                {
                    var original = AccessTools.Method(
                        typeof(Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu),
                        methodName,
                        new[] { typeof(SelectableBase) }
                    );

                    if (original != null)
                    {
                        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                        patchedCount++;
                        MelonLogger.Msg($"SettingsMenu.{methodName} patched successfully");
                    }
                    else
                    {
                        MelonLogger.Warning($"Failed to find SettingsMenu.{methodName}");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"Error patching SettingsMenu.{methodName}: {ex.Message}");
                }
            }

            MelonLogger.Msg($"SettingsMenu tab patches: {patchedCount}/6 applied");
        }

        private static void HandleTab_Postfix(
            Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu __instance)
        {
            if (__instance == null) return;

            try
            {
                // Determine which tab is now active
                string activeTabName = "Settings";
                try
                {
                    var groups = __instance.Groups;
                    if (groups != null)
                    {
                        for (int i = 0; i < groups.Count; i++)
                        {
                            var group = groups[i];
                            if (group?.Container != null &&
                                group.Container.gameObject.activeInHierarchy)
                            {
                                if (group.Button?.gameObject != null)
                                    activeTabName = group.Button.gameObject.name;
                                break;
                            }
                        }
                    }
                }
                catch { }

                // Invalidate cache so sections rebuild with new tab content
                SectionManager.InvalidateCache();

                // Announce the tab change
                AccessibilityManager.Announce($"{activeTabName} tab");

                // Request focus on first content element of the new tab
                AccessibilityManager.RequestFocusFirst();

                // Queue a UI dump for debugging
                UIElementDumper.QueueDump();

                MelonLogger.Msg($"[SettingsMenu] Tab switched to: {activeTabName}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in SettingsMenu HandleTab postfix: {ex.Message}");
            }
        }
    }
}
