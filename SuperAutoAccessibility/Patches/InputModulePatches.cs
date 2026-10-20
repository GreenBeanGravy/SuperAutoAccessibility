using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SuperAutoAccessibility.Patches
{
    /// <summary>
    /// The game's own input module presses the focused control when "Submit" is pressed and
    /// cancels on "Cancel", reading them through <see cref="BaseInput"/>. Those are the buttons
    /// the mod's menus rely on (the mod does not press menu buttons itself), so the controller's
    /// A and B count as them: the same press the Enter and Escape keys make.
    /// </summary>
    public static class InputModulePatches
    {
        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, "GetButtonDown", nameof(Down_Postfix));
            Patch(harmony, "GetButton", nameof(Held_Postfix));
            Patch(harmony, "GetButtonUp", nameof(Up_Postfix));
        }

        private static void Patch(HarmonyLib.Harmony harmony, string method, string postfix)
        {
            try
            {
                var original = AccessTools.Method(typeof(BaseInput), method, new[] { typeof(string) });
                var patch = AccessTools.Method(typeof(InputModulePatches), postfix);
                if (original == null || patch == null)
                {
                    MelonLogger.Warning($"Failed to find BaseInput.{method}");
                    return;
                }
                harmony.Patch(original, postfix: new HarmonyMethod(patch));
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error patching BaseInput.{method}: {ex.Message}");
            }
        }

        private static void Down_Postfix(string buttonName, ref bool __result)
        {
            if (!__result) __result = Gamepad.ModuleButton(buttonName, Gamepad.Phase.Down);
        }

        private static void Held_Postfix(string buttonName, ref bool __result)
        {
            if (!__result) __result = Gamepad.ModuleButton(buttonName, Gamepad.Phase.Held);
        }

        private static void Up_Postfix(string buttonName, ref bool __result)
        {
            if (!__result) __result = Gamepad.ModuleButton(buttonName, Gamepad.Phase.Up);
        }
    }
}
