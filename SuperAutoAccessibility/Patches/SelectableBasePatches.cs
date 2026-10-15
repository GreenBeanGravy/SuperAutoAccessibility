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

                // The interop generator names explicit interface implementations with
                // underscores (Il2CppInterop 1.5, MelonLoader 0.7.3); older generators used dots.
                // After the game moved to Unity 6 only the underscore name exists, and hover
                // announcements were silently off.
                var originalMethod = AccessTools.Method(
                    typeof(Il2CppSpacewood.Unity.UI.SelectableBase),
                    "UnityEngine_EventSystems_IPointerEnterHandler_OnPointerEnter"
                );

                if (originalMethod == null)
                {
                    originalMethod = AccessTools.Method(
                        typeof(Il2CppSpacewood.Unity.UI.SelectableBase),
                        "UnityEngine.EventSystems.IPointerEnterHandler.OnPointerEnter"
                    );
                }

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
                var prefixMethod = AccessTools.Method(
                    typeof(SelectableBasePatches),
                    nameof(PointerEnter_Prefix)
                );

                if (originalMethod != null && postfixMethod != null)
                {
                    harmony.Patch(originalMethod, prefix: new HarmonyMethod(prefixMethod), postfix: new HarmonyMethod(postfixMethod));
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

        private static UnityEngine.Vector3 _lastMousePosition;
        private static float _lastMouseMoveAt;

        /// <summary>Called every frame: notes when the mouse last moved.</summary>
        public static void TrackMouse()
        {
            try
            {
                var pos = UnityEngine.Input.mousePosition;
                if (pos != _lastMousePosition)
                {
                    _lastMousePosition = pos;
                    _lastMouseMoveAt = UnityEngine.Time.realtimeSinceStartup;
                }
            }
            catch { }
        }

        /// <summary>
        /// Set while the mod sends its own pointer enter to the focused control, which makes the
        /// game show that control's tooltip. Blocking it with the hovers left every tooltip stale.
        /// </summary>
        internal static bool Synthetic;

        /// <summary>The mouse has not moved for a while: a pointer enter is not a hover.</summary>
        public static bool MouseParked() =>
            UnityEngine.Time.realtimeSinceStartup - _lastMouseMoveAt > 0.5f;

        /// <summary>
        /// A control that appears under a mouse cursor left still (a blind player's mouse rests
        /// wherever it was) took the game's selection, as if hovered: the chooser's third choice
        /// as the choices slid in, the Danger pack when the Pack Shop opened. Focus jumped away
        /// from where the keys had put it. A pointer enter without the mouse moving is ignored.
        /// </summary>
        private static bool PointerEnter_Prefix() => Synthetic || !MouseParked();

        /// <summary>
        /// Postfix patch for SelectableBase.PointerEnter
        /// Called when mouse hovers over an element
        /// </summary>
        private static void PointerEnter_Postfix(Il2CppSpacewood.Unity.UI.SelectableBase __instance)
        {
            if (__instance == null || __instance.gameObject == null)
                return;
            // Ignored above; reading it would announce a control nobody hovered.
            if (MouseParked())
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
