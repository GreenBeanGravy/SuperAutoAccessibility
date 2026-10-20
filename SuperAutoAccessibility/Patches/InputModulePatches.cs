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
            // The stick also moves the game's own selection through these axes, by a lighter push
            // than the mod's, and that move is silent. With a pad connected the mod does all the
            // moving (the D-pad has no native route at all).
            Patch(harmony, "GetAxisRaw", nameof(Axis_Postfix));
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

        private static void Axis_Postfix(string axisName, ref float __result)
        {
            if (__result != 0f && Gamepad.Connected && (axisName == "Horizontal" || axisName == "Vertical"))
                __result = 0f;
        }

        private static void Down_Postfix(string buttonName, ref bool __result)
        {
            if (!__result) __result = Gamepad.ModuleSubmitOrCancel(buttonName);
        }
    }
}
