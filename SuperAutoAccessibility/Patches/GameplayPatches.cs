using System;
using HarmonyLib;
using MelonLoader;
using SuperAutoAccessibility.Gameplay;

namespace SuperAutoAccessibility.Patches
{
    /// <summary>
    /// Harmony patches for gameplay events:
    /// - Battle events (BoardRenderer.RenderEvent)
    /// - Shop events (gold changes, life changes, turn changes, free rolls)
    /// </summary>
    public static class GameplayPatches
    {
        // Track last announced values to avoid duplicate announcements
        private static int _lastAnnouncedGold = -1;
        private static int _lastAnnouncedTurn = -1;
        private static int _lastAnnouncedLives = -1;
        private static int _lastAnnouncedTrophies = -1;

        // Whether battle narration patch succeeded
        private static bool _battleNarrationEnabled = false;

        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Patch shop events FIRST (safe, non-virtual methods)
            PatchShopEvents(harmony);

            // Then try battle events (may fail on some Il2Cpp methods)
            PatchBattleEvents(harmony);

            // Patch BoardSystem.Add for shop-phase trigger event narration
            PatchShopTriggerEvents(harmony);
        }

        // =====================================================================
        // BATTLE EVENT SUBSCRIPTION
        // =====================================================================

        // Whether we've subscribed to the current BoardRenderer's OnStartRenderEvent
        private static bool _subscribedToRenderEvent = false;

        private static void PatchBattleEvents(HarmonyLib.Harmony harmony)
        {
            // Battle events use runtime delegate subscription instead of Harmony patching.
            // The game's RenderEvent methods are async UniTask methods that cannot be safely
            // patched via Harmony/Dobby. Instead, we subscribe to BoardRenderer.OnStartRenderEvent
            // at runtime when battle phase is detected.
            MelonLogger.Msg("Battle narration: using OnStartRenderEvent delegate subscription (lazy init)");
        }

        /// <summary>
        /// Called each frame during battle to subscribe to BoardRenderer.OnStartRenderEvent.
        /// Uses BoardController to access the BoardRenderer instance.
        /// </summary>
        public static void TrySubscribeToBoardRenderer()
        {
            if (_subscribedToRenderEvent) return;

            try
            {
                var boardController = UnityEngine.Object.FindObjectOfType<
                    Il2CppSpacewood.Unity.MonoBehaviours.Board.BoardController>();
                if (boardController == null) return;

                Il2CppSpacewood.Unity.MonoBehaviours.Board.BoardRenderer boardRenderer = null;
                try
                {
                    boardRenderer = boardController.__boardRenderer_k__BackingField;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"BoardRenderer backing field access error: {ex.Message}");
                    return;
                }

                if (boardRenderer == null)
                {
                    return;
                }

                // Create our handler and convert to Il2Cpp delegate.
                // Il2CppInterop provides DelegateSupport.ConvertDelegate to properly wrap
                // managed delegates as Il2Cpp delegates with correct calling conventions.
                var managedHandler = new System.Action<Il2CppBoardEvents.Interfaces.IBoardEvent>(OnBoardEventRendered);
                Il2CppSystem.Action<Il2CppBoardEvents.Interfaces.IBoardEvent> il2cppHandler;
                try
                {
                    il2cppHandler = Il2CppInterop.Runtime.DelegateSupport
                        .ConvertDelegate<Il2CppSystem.Action<Il2CppBoardEvents.Interfaces.IBoardEvent>>(managedHandler);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"DelegateSupport.ConvertDelegate failed: {ex.Message}");
                    // Fallback: try implicit cast (may work in newer Il2CppInterop versions)
                    il2cppHandler = managedHandler;
                }

                var existing = boardRenderer.OnStartRenderEvent;
                if (existing != null)
                {
                    // Append our handler to the existing delegate chain
                    var combined = Il2CppSystem.Delegate.Combine(existing, il2cppHandler);
                    if (combined != null)
                    {
                        boardRenderer.OnStartRenderEvent = combined
                            .Cast<Il2CppSystem.Action<Il2CppBoardEvents.Interfaces.IBoardEvent>>();
                    }
                    else
                    {
                        boardRenderer.OnStartRenderEvent = il2cppHandler;
                    }
                }
                else
                {
                    boardRenderer.OnStartRenderEvent = il2cppHandler;
                }

                _subscribedToRenderEvent = true;
                _battleNarrationEnabled = true;
                MelonLogger.Msg("Subscribed to BoardRenderer.OnStartRenderEvent â€” battle narration enabled");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"TrySubscribeToBoardRenderer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for BoardRenderer.OnStartRenderEvent â€” narrates each board event.
        /// </summary>
        private static void OnBoardEventRendered(Il2CppBoardEvents.Interfaces.IBoardEvent boardEvent)
        {
            try
            {
                if (boardEvent == null) return;

                try
                {
                    var il2cppObj = boardEvent.TryCast<Il2CppSystem.Object>();
                    if (il2cppObj != null)
                    {
                        string typeName = il2cppObj.GetIl2CppType().Name;
                        MelonLogger.Msg($"[BattleEvent] Type={typeName}, Owner={boardEvent.Owner}");
                    }
                }
                catch { }

                BattleNarrator.NarrateBoardEvent(boardEvent);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"OnBoardEventRendered error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset subscription flag so we re-subscribe for the next battle.
        /// Called when leaving battle phase.
        /// </summary>
        public static void ResetBattleSubscription()
        {
            _subscribedToRenderEvent = false;
        }

        // =====================================================================
        // SHOP TRIGGER EVENT PATCHES (BoardSystem.Add)
        // =====================================================================

        /// <summary>
        /// Patches BoardSystem.Add(IBoardEvent) to intercept trigger events during shop phase.
        /// BoardSystem.Add is a non-virtual concrete method â€” safe to patch via Harmony/Dobby.
        /// </summary>
        private static void PatchShopTriggerEvents(HarmonyLib.Harmony harmony)
        {
            PatchMethod(harmony,
                typeof(Il2CppSpacewood.Core.Actions.Board.Systems.BoardSystem),
                "Add",
                new[] { typeof(Il2CppBoardEvents.Interfaces.IBoardEvent) },
                nameof(BoardSystemAdd_Postfix),
                isPostfix: true,
                label: "BoardSystem.Add (shop trigger events)");
        }

        /// <summary>
        /// Postfix for BoardSystem.Add â€” narrates shop-phase trigger events.
        /// Only fires during IsShopPhase() to avoid double-announcing battle events.
        /// </summary>
        private static void BoardSystemAdd_Postfix(Il2CppBoardEvents.Interfaces.IBoardEvent boardEventType)
        {
            try
            {
                if (boardEventType == null) return;
                if (!GameplayPhaseDetector.IsShopPhase()) return;

                ShopNarrator.NarrateShopEvent(boardEventType);
            }
            catch { }
        }

        // =====================================================================
        // SHOP EVENT PATCHES
        // =====================================================================

        // Last known arena rank for change detection
        private static int _lastArenaRank = -1;

        private static void PatchShopEvents(HarmonyLib.Harmony harmony)
        {
            // Patch HangarGold.SetGold(int) â€” announce gold changes
            PatchMethod(harmony,
                typeof(Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarGold),
                "SetGold",
                new[] { typeof(int) },
                nameof(SetGold_Postfix),
                isPostfix: true,
                label: "HangarGold.SetGold");

            // Patch HangarLives.Set(int, int) â€” announce life changes
            PatchMethod(harmony,
                typeof(Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarLives),
                "Set",
                new[] { typeof(int), typeof(int) },
                nameof(SetLives_Postfix),
                isPostfix: true,
                label: "HangarLives.Set");

            // Patch HangarTurns.Set(int) â€” announce new turns
            PatchMethod(harmony,
                typeof(Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarTurns),
                "Set",
                new[] { typeof(int) },
                nameof(SetTurn_Postfix),
                isPostfix: true,
                label: "HangarTurns.Set");

            // Patch HangarVictories.Set(int, int) â€” announce trophy changes
            PatchMethod(harmony,
                typeof(Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarVictories),
                "Set",
                new[] { typeof(int), typeof(int) },
                nameof(SetVictories_Postfix),
                isPostfix: true,
                label: "HangarVictories.Set");
        }

        /// <summary>
        /// Helper to patch a method with error handling.
        /// Validates native pointer before patching to prevent Dobby crashes.
        /// </summary>
        private static void PatchMethod(HarmonyLib.Harmony harmony, Type targetType, string methodName,
            Type[] paramTypes, string patchMethodName, bool isPostfix, string label)
        {
            try
            {
                var original = AccessTools.Method(targetType, methodName, paramTypes);
                var patch = AccessTools.Method(typeof(GameplayPatches), patchMethodName);

                if (original == null || patch == null)
                {
                    MelonLogger.Warning($"Failed to find {label} (original={original != null}, patch={patch != null})");
                    return;
                }

                // Validate native pointer to avoid AccessViolationException in Dobby
                try
                {
                    var ptr = original.MethodHandle.GetFunctionPointer();
                    if (ptr == IntPtr.Zero)
                    {
                        MelonLogger.Warning($"{label}: native pointer is null, skipping");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"{label}: cannot validate pointer ({ex.Message}), skipping");
                    return;
                }

                if (isPostfix)
                    harmony.Patch(original, postfix: new HarmonyMethod(patch));
                else
                    harmony.Patch(original, prefix: new HarmonyMethod(patch));
                MelonLogger.Msg($"{label} patched successfully");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Error patching {label}: {ex.Message}");
            }
        }

        // --- Shop event postfixes ---

        private static void SetGold_Postfix(int gold)
        {
            try
            {
                if (!GameplayPhaseDetector.IsShopPhase()) return;
                if (gold == _lastAnnouncedGold) return;
                _lastAnnouncedGold = gold;

                // Non-interrupting announcement so it doesn't cut off other speech
                AccessibilityManager.Announce($"{gold} gold", interrupt: false);
            }
            catch { }
        }

        private static void SetLives_Postfix(int current, int max)
        {
            try
            {
                // Allow during shop and battle-to-shop transition, block during menu
                if (GameplayPhaseDetector.CurrentPhase == GamePhase.Menu) return;
                if (current == _lastAnnouncedLives) return;
                _lastAnnouncedLives = current;

                AccessibilityManager.Announce($"{current} lives", interrupt: false);
            }
            catch { }
        }

        private static void SetTurn_Postfix(int turn)
        {
            try
            {
                if (!GameplayPhaseDetector.IsShopPhase()) return;
                if (turn == _lastAnnouncedTurn) return;
                _lastAnnouncedTurn = turn;

                AccessibilityManager.Announce($"Turn {turn}", interrupt: false);
            }
            catch { }
        }

        private static void SetVictories_Postfix(int amount, int max)
        {
            try
            {
                // Allow during shop and battle-to-shop transition, block during menu
                if (GameplayPhaseDetector.CurrentPhase == GamePhase.Menu) return;
                if (amount == _lastAnnouncedTrophies) return;
                _lastAnnouncedTrophies = amount;

                AccessibilityManager.Announce($"{amount} trophies", interrupt: false);
            }
            catch { }
        }

        /// <summary>
        /// Polls for TallyArenaRank to detect arena rank changes after a match.
        /// Called from OnUpdate when the tally screen is visible.
        /// </summary>
        public static void PollArenaRankChange()
        {
            try
            {
                var tally = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaRank>();
                if (tally == null || !tally.gameObject.activeInHierarchy) return;

                int rank = tally.ViewRank;
                if (rank <= 0 || rank == _lastArenaRank) return;

                // Read gain/loss labels from the UI for context
                string gainText = "";
                string loseText = "";
                try
                {
                    if (tally.GainLabel != null && !string.IsNullOrEmpty(tally.GainLabel.text))
                        gainText = tally.GainLabel.text.Trim();
                    if (tally.LoseLabel != null && !string.IsNullOrEmpty(tally.LoseLabel.text))
                        loseText = tally.LoseLabel.text.Trim();
                }
                catch { }

                string announcement;
                if (!string.IsNullOrEmpty(gainText))
                    announcement = $"Rank {rank}. {gainText}";
                else if (!string.IsNullOrEmpty(loseText))
                    announcement = $"Rank {rank}. {loseText}";
                else
                    announcement = $"Rank {rank}";

                AccessibilityManager.Announce(announcement, interrupt: false);
                _lastArenaRank = rank;
            }
            catch { }
        }

        /// <summary>
        /// Reset tracked values when leaving gameplay.
        /// </summary>
        public static void ResetTracking()
        {
            _lastAnnouncedGold = -1;
            _lastAnnouncedTurn = -1;
            _lastAnnouncedLives = -1;
            _lastAnnouncedTrophies = -1;
            _lastArenaRank = -1;
        }
    }
}
