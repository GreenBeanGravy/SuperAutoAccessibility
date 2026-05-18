using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using Il2CppSpacewood.Unity.UI;
using Il2CppSpacewood.Unity.MonoBehaviours.Build;
using Il2CppSpacewood.Unity.MonoBehaviours.Build.Hangar.States;
using Il2CppSpacewood.Core.Models;
using Il2CppSpacewood.Unity.Extensions;

// Alias to avoid ambiguity with UnityEngine.Space
using GameSpace = Il2CppSpacewood.Unity.Views.Space;
using MinionView = Il2CppSpacewood.Unity.Views.MinionView;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// Zone-based keyboard navigation for the shop/build phase.
    /// Player uses shortcut keys to jump between zones (S/F/T/D),
    /// then Left/Right to navigate within a zone.
    /// </summary>
    public enum ShopZone
    {
        None,
        PetShop,   // S key
        FoodShop,  // F key
        Team,      // T key
        Actions,   // D key
        Bullies    // V key â€” read-only browse of the upcoming Daily-mode opponent team
    }

    public enum CombineStatus
    {
        None,
        CanCombine,
        WillLevelUp
    }

    public static class ShopNavigationManager
    {
        private static ShopZone _currentZone = ShopZone.None;
        private static int _currentIndex = 0;

        // Cached slot data for current zone
        private static List<GameSpace> _currentSlots = new List<GameSpace>();

        // For Actions zone, we store button references
        private static List<(SelectableBase button, string label)> _actionButtons = new List<(SelectableBase, string)>();

        // For Bullies zone, we store MinionModel references (read-only browse of the
        // upcoming Daily-mode opponent team). BullyBoardView exposes pets as MinionViews,
        // not as Spaces, so we can't reuse _currentSlots.
        private static List<Il2CppSpacewood.Core.Models.MinionModel> _currentBullies =
            new List<Il2CppSpacewood.Core.Models.MinionModel>();

        // Track hangar state machine for food targeting detection
        private static HangarState _lastHangarState = HangarState.Default;

        // Input cooldown on shop entry to prevent leftover key states from triggering actions
        private static int _inputCooldownFrames = 0;
        private const int SHOP_ENTRY_COOLDOWN = 15; // ~0.25s at 60fps

        // Track "Waiting for opponents" text to announce updates
        private static string _lastWaitingText = "";

        // Auto-move to board after buying a pet
        private static bool _pendingAutoMoveToBoard = false;
        private static int _autoMoveDelayFrames = 0;

        // Detail line navigation (Hearthstone-style up/down browsing)
        private static List<string> _currentDetailLines = new List<string>();
        private static int _currentDetailLineIndex = 0;

        // Deferred sell operation (wait for selection to register)
        private static bool _pendingSell = false;
        private static int _sellDelayFrames = 0;
        private static string _sellPetName = "";
        private static int _sellValue = 1;

        // Track previous shop state for unchained detection and food stocked announcements
        private static List<string> _previousPetShopEnums = new List<string>();
        private static List<string> _previousFoodShopEnums = new List<string>();

        // Auto-focus combine target after buying
        private static bool _pendingCombineFocus = false;
        private static int _combineFocusDelayFrames = 0;
        private static string _combineTargetEnum = "";

        // Track zone before auto-switch (e.g., FoodShop â†’ Team for food targeting)
        private static ShopZone _previousZone = ShopZone.None;

        /// <summary>
        /// When true, shop input handling is suppressed (e.g., confirm popup or name picker active).
        /// Set by SuperAutoAccessibility.cs when overlay UI needs exclusive input.
        /// </summary>
        public static bool IsInputSuppressed { get; set; } = false;

        /// <summary>
        /// Sets a cooldown to suppress shop input for a short duration.
        /// Called when entering shop phase to prevent leftover key states from triggering actions.
        /// </summary>
        public static void SetInputCooldown()
        {
            _inputCooldownFrames = SHOP_ENTRY_COOLDOWN;
        }

        /// <summary>
        /// Checks if the turn has been ended by looking for the "Waiting for opponents" label.
        /// This is checked live each time â€” we don't cache the state, so if the label goes away
        /// (e.g., user cancels from confirm dialog), the turn is no longer considered ended.
        /// </summary>
        private static bool IsTurnEnded(HangarMain hangar)
        {
            try
            {
                var waitingLabel = hangar?.transform.Find(
                    "Overlay/BottomCanvas/NotchPadding/Waiting/Label/Background");
                if (waitingLabel != null && waitingLabel.gameObject.activeInHierarchy)
                    return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Resets _turnEnded flag. Called when the user cancels an end-turn confirmation dialog.
        /// </summary>
        public static void ResetTurnEnded()
        {
            _lastWaitingText = "";
        }

        /// <summary>
        /// Polls the "Waiting for opponents" label text. Announces when it appears or changes.
        /// Called each frame from HandleInput.
        /// </summary>
        private static void PollWaitingText(HangarMain hangar)
        {
            try
            {
                var waitingLabel = hangar?.transform.Find(
                    "Overlay/BottomCanvas/NotchPadding/Waiting/Label/Background");
                if (waitingLabel != null && waitingLabel.gameObject.activeInHierarchy)
                {
                    // Get the text from any TMP child
                    string text = "";
                    var tmp = waitingLabel.GetComponentInChildren<Il2CppTMPro.TMP_Text>();
                    if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                        text = tmp.text.Trim();

                    if (!string.IsNullOrEmpty(text) && text != _lastWaitingText)
                    {
                        AccessibilityManager.Announce(text, interrupt: false);
                        _lastWaitingText = text;
                    }
                }
                else
                {
                    // Label not visible â€” clear tracked text
                    if (!string.IsNullOrEmpty(_lastWaitingText))
                        _lastWaitingText = "";
                }
            }
            catch { }
        }

        /// <summary>
        /// Called every frame from OnUpdate during shop phase.
        /// Handles all shop-specific keyboard input.
        /// </summary>
        public static void HandleInput()
        {
            if (!GameplayPhaseDetector.IsShopPhase()) return;
            if (IsInputSuppressed) return;

            // Input cooldown on shop entry
            if (_inputCooldownFrames > 0)
            {
                _inputCooldownFrames--;
                return;
            }

            var hangar = GameplayPhaseDetector.GetHangarMain();
            if (hangar == null) return;

            // Poll the "Waiting for opponents" label and announce when text changes
            PollWaitingText(hangar);

            // Process pending auto-move to board (after buying a pet)
            if (_pendingAutoMoveToBoard)
            {
                _autoMoveDelayFrames--;
                if (_autoMoveDelayFrames <= 0)
                {
                    _pendingAutoMoveToBoard = false;
                    AutoMoveToBoard(hangar);
                }
                // Don't block input â€” let user continue interacting while waiting
            }

            // Process pending combine focus (after buying a combinable pet)
            if (_pendingCombineFocus)
            {
                _combineFocusDelayFrames--;
                if (_combineFocusDelayFrames <= 0)
                {
                    _pendingCombineFocus = false;
                    AutoFocusCombineTarget(hangar, _combineTargetEnum);
                }
            }

            // Process deferred sell (wait for selection to register)
            if (_pendingSell)
            {
                _sellDelayFrames--;
                if (_sellDelayFrames <= 0)
                {
                    _pendingSell = false;
                    try
                    {
                        var sellBtn = hangar.Overlay?.SellButton;
                        if (sellBtn != null)
                        {
                            sellBtn.Click();
                            AccessibilityManager.Announce($"Sold {_sellPetName}");
                            AccessibilityManager.Announce($"Gained {_sellValue} gold", interrupt: false);
                            MelonLogger.Msg($"[Shop Action] Sold {_sellPetName} (deferred)");
                            RefreshCurrentZone(hangar);
                        }
                    }
                    catch (Exception ex) { MelonLogger.Warning($"Deferred sell error: {ex.Message}"); }
                }
            }

            // Guard: skip if text input is focused
            var currentObj = EventSystem.current?.currentSelectedGameObject;
            if (currentObj != null && currentObj.GetComponent<InputFieldBase>() != null) return;

            // Check if turn is already ended â€” block action keys but allow status/navigation
            bool turnEnded = IsTurnEnded(hangar);

            // Zone switching keys (always allowed)
            if (Input.GetKeyDown(KeyCode.S)) SwitchToZone(ShopZone.PetShop, hangar);
            else if (Input.GetKeyDown(KeyCode.F)) SwitchToZone(ShopZone.FoodShop, hangar);
            else if (Input.GetKeyDown(KeyCode.T)) SwitchToZone(ShopZone.Team, hangar);
            else if (Input.GetKeyDown(KeyCode.D)) SwitchToZone(ShopZone.Actions, hangar);

            // Status query keys (always allowed)
            else if (Input.GetKeyDown(KeyCode.A)) AnnounceGold(hangar);
            else if (Input.GetKeyDown(KeyCode.L)) AnnounceLives(hangar);
            else if (Input.GetKeyDown(KeyCode.N)) AnnounceTurn(hangar);
            else if (Input.GetKeyDown(KeyCode.W)) AnnounceWins(hangar);
            else if (Input.GetKeyDown(KeyCode.I)) AnnounceDetailedInfo(hangar);
            else if (Input.GetKeyDown(KeyCode.O)) OpenScoreboard(hangar);
            else if (Input.GetKeyDown(KeyCode.V)) SwitchToZone(ShopZone.Bullies, hangar);

            // Global shop action keys â€” blocked if turn ended
            else if (Input.GetKeyDown(KeyCode.Q))
            {
                if (turnEnded) AccessibilityManager.Announce("Turn already ended");
                else PerformRoll(hangar);
            }
            else if (Input.GetKeyDown(KeyCode.Z))
            {
                if (turnEnded) AccessibilityManager.Announce("Turn already ended");
                else PerformFreeze(hangar);
            }
            else if (Input.GetKeyDown(KeyCode.E))
            {
                if (turnEnded) AccessibilityManager.Announce("Turn already ended");
                else PerformEndTurn(hangar);
            }

            // Within-zone navigation (only when in a zone)
            else if (_currentZone != ShopZone.None)
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) MoveInZone(1, hangar);
                else if (Input.GetKeyDown(KeyCode.LeftArrow)) MoveInZone(-1, hangar);
                else if (Input.GetKeyDown(KeyCode.Home)) MoveToStart(hangar);
                else if (Input.GetKeyDown(KeyCode.End)) MoveToEnd(hangar);

                // Detail line navigation (Up/Down = browse lines, Space = re-read current)
                else if (Input.GetKeyDown(KeyCode.DownArrow)) MoveDetailLine(1);
                else if (Input.GetKeyDown(KeyCode.UpArrow)) MoveDetailLine(-1);
                else if (Input.GetKeyDown(KeyCode.Space) && !GameplayPhaseDetector.IsBattlePhase())
                {
                    if (_currentDetailLines.Count > 0 && _currentDetailLineIndex < _currentDetailLines.Count)
                        AccessibilityManager.Announce(_currentDetailLines[_currentDetailLineIndex]);
                }

                // Action keys (require zone selection) â€” blocked if turn ended
                else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (turnEnded) AccessibilityManager.Announce("Turn already ended");
                    else PerformAction(hangar);
                }
                else if (Input.GetKeyDown(KeyCode.X) || Input.GetKeyDown(KeyCode.Delete))
                {
                    if (turnEnded) AccessibilityManager.Announce("Turn already ended");
                    else PerformSell(hangar);
                }

                // Backspace â€” cancel current action (food targeting, pet focus)
                else if (Input.GetKeyDown(KeyCode.Backspace))
                {
                    CancelCurrentAction();
                }
            }
        }

        /// <summary>
        /// Returns true if the ShopNavigationManager is actively handling input
        /// (i.e., we're in a shop zone and should intercept arrow keys).
        /// </summary>
        public static bool IsActivelyNavigating()
        {
            return _currentZone != ShopZone.None && GameplayPhaseDetector.IsShopPhase();
        }

        /// <summary>
        /// Reset when leaving shop phase.
        /// </summary>
        public static void Reset()
        {
            _currentZone = ShopZone.None;
            _currentIndex = 0;
            _currentSlots.Clear();
            _actionButtons.Clear();
            _lastHangarState = HangarState.Default;
            _inputCooldownFrames = 0;
            _pendingAutoMoveToBoard = false;
            _autoMoveDelayFrames = 0;
            _pendingSell = false;
            _currentDetailLines.Clear();
            _currentDetailLineIndex = 0;
            _previousPetShopEnums.Clear();
            _previousFoodShopEnums.Clear();
            _pendingCombineFocus = false;
            _combineTargetEnum = "";
            _lastWaitingText = "";
            _previousZone = ShopZone.None;
        }

        /// <summary>
        /// Cancels the current game action (SpellFocus food targeting, ArmyMinionFocus pet selection).
        /// Returns true if an action was cancelled, false if nothing to cancel.
        /// </summary>
        public static bool CancelCurrentAction()
        {
            var hangar = GameplayPhaseDetector.GetHangarMain();
            if (hangar == null) return false;

            // Check if we're in SpellFocus (food targeting mode)
            if (_lastHangarState == HangarState.SpellFocus)
            {
                try
                {
                    hangar.StateMachine.SetStateDefault();
                }
                catch { }

                // Return to the zone the user was in before targeting started
                if (_previousZone != ShopZone.None)
                {
                    SwitchToZone(_previousZone, hangar);
                    _previousZone = ShopZone.None;
                }

                AccessibilityManager.Announce("Cancelled");
                MelonLogger.Msg("[Shop] Cancelled food targeting");
                return true;
            }

            // Check if we're in ArmyMinionFocus (pet selected on team)
            try
            {
                var stateEnum = hangar.StateMachine?.State?.Enum;
                if (stateEnum == HangarState.ArmyMinionFocus)
                {
                    hangar.StateMachine.SetStateDefault();
                    RefreshCurrentZone(hangar);
                    AccessibilityManager.Announce("Deselected");
                    MelonLogger.Msg("[Shop] Cancelled pet focus");
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Called each frame to detect when the game enters SpellFocus state
        /// (food targeting mode) and announce instructions.
        /// </summary>
        public static void PollSpellFocusState(HangarMain hangar)
        {
            if (hangar == null) return;

            try
            {
                var stateMachine = hangar.StateMachine;
                if (stateMachine == null) return;

                var currentState = stateMachine.State;
                if (currentState == null) return;

                var stateEnum = currentState.Enum;

                if (stateEnum == HangarState.SpellFocus && _lastHangarState != HangarState.SpellFocus)
                {
                    // Just entered SpellFocus â€” announce targeting mode
                    string spellName = "food item";
                    bool isZoneTarget = false;
                    try
                    {
                        var spellFocusState = currentState.TryCast<HangarStateShopSpellFocus>();
                        if (spellFocusState?.Spell != null)
                        {
                            spellName = PetStatsReader.ReadSpell(spellFocusState.Spell);
                            isZoneTarget = IsZoneTargetFood(spellFocusState.Spell);
                        }
                    }
                    catch { }

                    // Save food name for GiveMinionPerk narration
                    ShopNarrator.LastAppliedFoodName = spellName;

                    // Save current zone so Backspace/Escape can return here
                    _previousZone = _currentZone;

                    if (isZoneTarget)
                    {
                        // Zone-target food (Salad Bowl etc.) â€” no need to navigate to a pet
                        // Just set to Team zone silently so Enter can trigger SpellZone click
                        _currentZone = ShopZone.Team;
                        _currentSlots.Clear();
                        _actionButtons.Clear();
                        PopulateTeamSlots(hangar);
                        AccessibilityManager.Announce(
                            $"Press Enter to use {spellName} on your team, Backspace or Escape to cancel.");
                    }
                    else
                    {
                        // Single-target food â€” auto-switch to Team so user can pick a pet
                        SwitchToZone(ShopZone.Team, hangar);
                        AccessibilityManager.Announce(
                            $"Select a team member to use {spellName} on. " +
                            "Press T for team, arrow keys to navigate, Enter to confirm, Backspace or Escape to cancel.");
                    }
                }
                else if (stateEnum != HangarState.SpellFocus && _lastHangarState == HangarState.SpellFocus)
                {
                    // Just exited SpellFocus â€” refresh zone since items may have changed
                    RefreshCurrentZone(hangar);
                }

                _lastHangarState = stateEnum;
            }
            catch { }
        }

        // --- Detail line navigation (Hearthstone-style) ---

        private static void BuildDetailLinesForCurrentSlot(HangarMain hangar)
        {
            _currentDetailLines.Clear();
            _currentDetailLineIndex = 0;

            if (_currentZone == ShopZone.Actions) return;

            // Bullies zone is sourced from _currentBullies (MinionModel list), not _currentSlots.
            if (_currentZone == ShopZone.Bullies)
            {
                if (_currentBullies.Count == 0 || _currentIndex >= _currentBullies.Count) return;
                try
                {
                    var bullyMinion = _currentBullies[_currentIndex];
                    if (bullyMinion == null) return;
                    // Find the matching MinionView from BullyBoardView for richer detail lines.
                    MinionView bullyView = null;
                    try
                    {
                        var minionsContainer = hangar.transform.Find("Board/BullyBoardView/Minions");
                        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<MinionView> views = null;
                        if (minionsContainer != null)
                            views = minionsContainer.GetComponentsInChildren<MinionView>(true);
                        if (views != null && _currentIndex < views.Count)
                            bullyView = views[_currentIndex];
                    }
                    catch { }
                    // Reuse the team detail-line builder. The bully board is structurally a
                    // team-side board (minions with stats/perks/abilities), just owned by World.
                    _currentDetailLines = DetailLineProvider.BuildMinionTeamLines(bullyMinion, bullyView);
                }
                catch { }
                return;
            }

            if (_currentSlots.Count == 0 || _currentIndex >= _currentSlots.Count) return;

            var space = _currentSlots[_currentIndex];
            if (space == null) return;

            try
            {
                switch (_currentZone)
                {
                    case ShopZone.PetShop:
                        try
                        {
                            var shopMinion = space.MinionModel;
                            if (shopMinion == null && hangar != null)
                            {
                                try
                                {
                                    var board = hangar.Overlay?.BoardModel;
                                    if (board?.MinionShop != null && _currentIndex < board.MinionShop.Count)
                                        shopMinion = board.MinionShop[_currentIndex];
                                }
                                catch { }
                            }
                            if (shopMinion != null)
                            {
                                // Find MinionView from MinionShop/Spaces hierarchy
                                MinionView shopView = null;
                                try
                                {
                                    var shopSpaces = hangar.transform.Find("MinionShop/Spaces");
                                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<MinionView> shopViews = null;
                                    if (shopSpaces != null)
                                        shopViews = shopSpaces.GetComponentsInChildren<MinionView>(true);
                                    if (shopViews != null && _currentIndex < shopViews.Count)
                                        shopView = shopViews[_currentIndex];
                                }
                                catch { }
                                _currentDetailLines = DetailLineProvider.BuildMinionShopLines(shopMinion, shopView);
                            }
                        }
                        catch { } // Empty slots may throw on property access
                        break;
                    case ShopZone.FoodShop:
                        try
                        {
                            var spell = space.SpellModel;
                            if (spell == null && hangar != null)
                            {
                                try
                                {
                                    var board = hangar.Overlay?.BoardModel;
                                    if (board?.SpellShop != null && _currentIndex < board.SpellShop.Count)
                                        spell = board.SpellShop[_currentIndex];
                                }
                                catch { }
                            }
                            if (spell != null)
                                _currentDetailLines = DetailLineProvider.BuildSpellShopLines(spell);
                        }
                        catch { } // Empty slots may throw on property access
                        break;
                    case ShopZone.Team:
                        try
                        {
                            var teamMinion = space.MinionModel;
                            if (teamMinion == null && hangar != null)
                            {
                                try
                                {
                                    var board = hangar.Overlay?.BoardModel;
                                    if (board?.Minions?.Items != null && _currentIndex < board.Minions.Items.Count)
                                        teamMinion = board.Minions.Items[_currentIndex];
                                }
                                catch { }
                            }
                            if (teamMinion != null)
                            {
                                // Find MinionView from Board/BoardView/Minions hierarchy
                                MinionView teamView = null;
                                try
                                {
                                    var minionsContainer = hangar.transform.Find("Board/BoardView/Minions");
                                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<MinionView> armyViews = null;
                                    if (minionsContainer != null)
                                        armyViews = minionsContainer.GetComponentsInChildren<MinionView>(true);
                                    if (armyViews != null && _currentIndex < armyViews.Count)
                                        teamView = armyViews[_currentIndex];
                                }
                                catch { }
                                _currentDetailLines = DetailLineProvider.BuildMinionTeamLines(teamMinion, teamView);
                            }
                        }
                        catch { } // Empty slots may throw on property access
                        break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildDetailLines error: {ex.Message}");
            }
        }

        private static void MoveDetailLine(int direction)
        {
            if (_currentDetailLines.Count == 0) return;

            int newIndex = _currentDetailLineIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _currentDetailLines.Count) newIndex = _currentDetailLines.Count - 1;

            if (newIndex == _currentDetailLineIndex && direction != 0)
            {
                // At boundary â€” re-read current line
                if (_currentDetailLineIndex < _currentDetailLines.Count)
                    AccessibilityManager.Announce(_currentDetailLines[_currentDetailLineIndex]);
                return;
            }

            _currentDetailLineIndex = newIndex;
            AccessibilityManager.Announce(_currentDetailLines[_currentDetailLineIndex]);
        }

        // --- Zone switching ---

        private static void SwitchToZone(ShopZone zone, HangarMain hangar)
        {
            _currentZone = zone;
            _currentIndex = 0;
            _currentSlots.Clear();
            _actionButtons.Clear();
            _currentBullies.Clear();
            _pendingSell = false; // Cancel any deferred sell on zone change

            switch (zone)
            {
                case ShopZone.PetShop:
                    PopulatePetShopSlots(hangar);
                    break;
                case ShopZone.FoodShop:
                    PopulateFoodShopSlots(hangar);
                    break;
                case ShopZone.Team:
                    PopulateTeamSlots(hangar);
                    break;
                case ShopZone.Actions:
                    PopulateActionButtons(hangar);
                    break;
                case ShopZone.Bullies:
                    PopulateBullySlots(hangar);
                    break;
            }

            AnnounceCurrentItem(hangar, includeZoneName: true);
        }

        private static void PopulatePetShopSlots(HangarMain hangar)
        {
            try
            {
                var minionShop = hangar.MinionShop;
                if (minionShop?.Spaces == null) return;

                var spaces = minionShop.Spaces;
                for (int i = 0; i < spaces.Count; i++)
                {
                    try
                    {
                        var space = spaces[i];
                        if (space != null)
                            _currentSlots.Add(space);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PopulatePetShopSlots error: {ex.Message}");
            }
        }

        private static void PopulateFoodShopSlots(HangarMain hangar)
        {
            try
            {
                var spellShop = hangar.SpellShop;
                if (spellShop?.Spaces == null) return;

                var spaces = spellShop.Spaces;
                for (int i = 0; i < spaces.Count; i++)
                {
                    try
                    {
                        var space = spaces[i];
                        if (space != null)
                            _currentSlots.Add(space);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PopulateFoodShopSlots error: {ex.Message}");
            }
        }

        private static void PopulateTeamSlots(HangarMain hangar)
        {
            try
            {
                var army = hangar.MinionArmy;
                if (army?.Spaces == null) return;

                // Grid<Space>.Items gives us a List<Space>
                var items = army.Spaces.Items;
                if (items == null) return;

                for (int i = 0; i < items.Count; i++)
                {
                    try
                    {
                        var space = items[i];
                        if (space != null)
                            _currentSlots.Add(space);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PopulateTeamSlots error: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates _currentBullies with the upcoming Daily-mode opponent team
        /// from /Build/Hangar/Board/BullyBoardView. Reads pets first, then any
        /// relics the bully owns (the special-item slot to the right of the team).
        /// Read-only — used by the V keybind to let the user arrow through the
        /// next opponent's lineup exactly like their own team.
        /// </summary>
        private static void PopulateBullySlots(HangarMain hangar)
        {
            try
            {
                var bvGo = UnityEngine.GameObject.Find("/Build/Hangar/Board/BullyBoardView");
                var view = bvGo?.GetComponent<Il2CppSpacewood.Unity.Views.BoardView>();
                var model = view?.Model;
                if (model == null) return;

                var items = model.Minions?.Items;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var m = items[i];
                            if (m != null) _currentBullies.Add(m);
                        }
                        catch { }
                    }
                }

                // Append any non-null relics so V → → → … lands on them after the pets.
                try
                {
                    var relics = model.Relics?.Items;
                    if (relics != null)
                    {
                        for (int i = 0; i < relics.Count; i++)
                        {
                            try
                            {
                                var r = relics[i];
                                if (r != null) _currentBullies.Add(r);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PopulateBullySlots error: {ex.Message}");
            }
        }

        private static void PopulateActionButtons(HangarMain hangar)
        {
            try
            {
                var overlay = hangar.Overlay;
                if (overlay == null) return;

                // Roll button
                try
                {
                    if (overlay.Roll?.Button != null)
                        _actionButtons.Add((overlay.Roll.Button, "Roll"));
                }
                catch { }

                // Freeze button
                try
                {
                    if (overlay.Freeze?.Button != null)
                        _actionButtons.Add((overlay.Freeze.Button, "Freeze"));
                }
                catch { }

                // End Turn button â€” not included here; use E keybind instead
                // to prevent Unity's native Submit handler from accidentally clicking it

                // Sell button
                try
                {
                    if (overlay.SellButton != null)
                        _actionButtons.Add((overlay.SellButton, "Sell"));
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PopulateActionButtons error: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes the current zone's slot data after an action (buy/roll/sell).
        /// Preserves current index if possible.
        /// </summary>
        private static void RefreshCurrentZone(HangarMain hangar)
        {
            if (_currentZone == ShopZone.None) return;

            int savedIndex = _currentIndex;
            _currentSlots.Clear();
            _actionButtons.Clear();
            _currentBullies.Clear();
            _currentDetailLines.Clear();
            _currentDetailLineIndex = 0;

            switch (_currentZone)
            {
                case ShopZone.PetShop:
                    PopulatePetShopSlots(hangar);
                    break;
                case ShopZone.FoodShop:
                    PopulateFoodShopSlots(hangar);
                    break;
                case ShopZone.Team:
                    PopulateTeamSlots(hangar);
                    break;
                case ShopZone.Actions:
                    PopulateActionButtons(hangar);
                    break;
                case ShopZone.Bullies:
                    PopulateBullySlots(hangar);
                    break;
            }

            // Clamp index to new bounds
            int maxIndex = _currentZone switch
            {
                ShopZone.Actions => _actionButtons.Count - 1,
                ShopZone.Bullies => _currentBullies.Count - 1,
                _ => _currentSlots.Count - 1,
            };
            _currentIndex = Math.Min(savedIndex, Math.Max(0, maxIndex));

            // Rebuild detail lines for the (possibly changed) current slot
            BuildDetailLinesForCurrentSlot(hangar);

            // Also invalidate section cache so Tab nav sees updated labels
            SectionManager.InvalidateCache();
        }

        // --- Within-zone navigation ---

        private static void MoveInZone(int direction, HangarMain hangar)
        {
            int total = _currentZone switch
            {
                ShopZone.Actions => _actionButtons.Count,
                ShopZone.Bullies => _currentBullies.Count,
                _ => _currentSlots.Count,
            };
            if (total == 0) return;
            _currentIndex = (_currentIndex + direction + total) % total;
            AnnounceCurrentItem(hangar);
        }

        private static void MoveToStart(HangarMain hangar)
        {
            _currentIndex = 0;
            AnnounceCurrentItem(hangar);
        }

        private static void MoveToEnd(HangarMain hangar)
        {
            int total = _currentZone switch
            {
                ShopZone.Actions => _actionButtons.Count,
                ShopZone.Bullies => _currentBullies.Count,
                _ => _currentSlots.Count,
            };
            _currentIndex = Math.Max(0, total - 1);
            AnnounceCurrentItem(hangar);
        }

        // --- Announcements ---

        private static void AnnounceCurrentItem(HangarMain hangar, bool includeZoneName = false)
        {
            string zonePrefix = "";
            if (includeZoneName)
            {
                string zoneName = _currentZone switch
                {
                    ShopZone.PetShop => "Pet Shop",
                    ShopZone.FoodShop => "Food Shop",
                    ShopZone.Team => "Team",
                    ShopZone.Actions => "Actions",
                    ShopZone.Bullies => "Upcoming Bullies",
                    _ => ""
                };
                zonePrefix = zoneName + ", ";
            }

            if (_currentZone == ShopZone.Bullies)
            {
                if (_currentBullies.Count == 0)
                {
                    AccessibilityManager.Announce($"{zonePrefix}no upcoming bullies");
                    return;
                }
                if (_currentIndex < 0 || _currentIndex >= _currentBullies.Count) return;

                // Build the detail lines for this bully so Up/Down arrow can browse
                // them line-by-line, exactly like the player's own team.
                BuildDetailLinesForCurrentSlot(hangar);

                var current = _currentBullies[_currentIndex];
                string position = $"{_currentIndex + 1} of {_currentBullies.Count}";
                bool isRelic = false;
                try { isRelic = current.Type == Il2CppSpacewood.Core.Enums.MinionType.Relic; }
                catch { }

                if (isRelic)
                {
                    // Relics don't have meaningful Attack/Health — announce as
                    // "Relic: Radio, level 1" so the user knows it's the special item.
                    string relicName = "";
                    int relicLevel = 1;
                    try { relicName = PetStatsReader.GetLocalizedName(current); } catch { }
                    try { relicLevel = current.Level; } catch { }
                    if (string.IsNullOrEmpty(relicName)) relicName = "unknown";
                    AccessibilityManager.Announce(
                        $"{zonePrefix}Relic: {relicName}, level {relicLevel}, {position}");
                }
                else if (_currentDetailLines.Count > 0)
                {
                    // First detail line is "Name, X/Y" — same shape as team announcements.
                    AccessibilityManager.Announce(
                        $"{zonePrefix}{_currentDetailLines[0]}, {position}");
                }
                else
                {
                    string desc = PetStatsReader.ReadMinionNameAndStats(current);
                    AccessibilityManager.Announce($"{zonePrefix}{desc}, {position}");
                }
                return;
            }

            if (_currentZone == ShopZone.Actions)
            {
                if (_actionButtons.Count == 0)
                {
                    AccessibilityManager.Announce($"{zonePrefix}no actions available");
                    return;
                }

                var (button, label) = _actionButtons[_currentIndex];
                string position = $"{_currentIndex + 1} of {_actionButtons.Count}";
                AccessibilityManager.Announce($"{zonePrefix}{label}, {position}");

                // Focus the button in the EventSystem
                try
                {
                    if (button?.gameObject != null && EventSystem.current != null)
                    {
                        EventSystem.current.SetSelectedGameObject(button.gameObject);
                    }
                }
                catch { }
                return;
            }

            if (_currentSlots.Count == 0)
            {
                AccessibilityManager.Announce($"{zonePrefix}empty");
                return;
            }

            var space = _currentSlots[_currentIndex];

            // Build detail lines for the new slot (Hearthstone-style)
            BuildDetailLinesForCurrentSlot(hangar);

            string pos = $"{_currentIndex + 1} of {_currentSlots.Count}";

            if (_currentDetailLines.Count > 0)
            {
                // Check frozen state for shop items
                string frozen = "";
                if ((_currentZone == ShopZone.PetShop || _currentZone == ShopZone.FoodShop)
                    && IsSlotFrozen(space))
                {
                    frozen = "Frozen, ";
                    SoundManager.Play("SAP_IsFrozen");
                }

                // Check combine status for pet shop items
                string combineInfo = "";
                if (_currentZone == ShopZone.PetShop)
                {
                    var shopMinion = GetCurrentShopMinion(hangar);
                    if (shopMinion != null)
                    {
                        var combineStatus = CheckCombineStatus(shopMinion, hangar);
                        if (combineStatus == CombineStatus.WillLevelUp)
                        {
                            combineInfo = "Can combine and level up, ";
                            SoundManager.Play("SAP_CanCombineAndWillLevelUp");
                        }
                        else if (combineStatus == CombineStatus.CanCombine)
                        {
                            combineInfo = "Can combine, ";
                            SoundManager.Play("SAP_CanCombine");
                        }
                    }
                }

                // Check for chained (linked) pets in shop (pets only â€” food doesn't play chained sound)
                string chainedInfo = "";
                if (_currentZone == ShopZone.PetShop)
                {
                    try
                    {
                        var m = GetCurrentShopMinion(hangar);
                        if (m != null)
                        {
                            var links = m.Links;
                            if (links != null && links.Count > 0)
                            {
                                string linkedDesc = GetLinkedDescription(links, hangar, m.Id);
                                if (!string.IsNullOrEmpty(linkedDesc))
                                {
                                    chainedInfo = $"Chained to {linkedDesc}, ";
                                    SoundManager.Play("SAP_Chained");
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Announce line 1 (name) + slot position
                // Order: Name, chained info, combine status, frozen, position
                string name = _currentDetailLines[0];
                AccessibilityManager.Announce($"{zonePrefix}{name}, {chainedInfo}{combineInfo}{frozen}{pos}");
            }
            else
            {
                AccessibilityManager.Announce($"{zonePrefix}Empty, {pos}");
            }
        }

        /// <summary>
        /// Checks if a shop Space's item view has the frozen indicator active.
        /// </summary>
        private static bool IsSlotFrozen(GameSpace space)
        {
            try
            {
                var minionView = space.MinionView;
                if (minionView != null)
                {
                    var frozenContainer = minionView.FrozenContainer;
                    if (frozenContainer != null && frozenContainer.gameObject.activeInHierarchy)
                        return true;
                }

                var spellView = space.SpellView;
                if (spellView != null)
                {
                    var frozenContainer = spellView.FrozenContainer;
                    if (frozenContainer != null && frozenContainer.gameObject.activeInHierarchy)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Resolves linked item IDs to pet/food names for the announcement.
        /// </summary>
        private static string GetLinkedDescription(Il2CppSystem.Collections.Generic.List<Il2CppSpacewood.Core.Models.Item.ItemId> links, HangarMain hangar, Il2CppSpacewood.Core.Models.Item.ItemId? excludeId = null)
        {
            if (links == null || links.Count == 0) return null;

            var names = new List<string>();
            try
            {
                for (int i = 0; i < links.Count; i++)
                {
                    var linkId = links[i];
                    // Skip the current item's own ID
                    if (excludeId.HasValue && linkId == excludeId.Value) continue;
                    string name = FindItemNameById(linkId, hangar);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            catch { }

            if (names.Count == 0) return null;
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return $"{names[0]} and {names[1]}";
            // 3+: "A, B, and C"
            return string.Join(", ", names.GetRange(0, names.Count - 1)) + ", and " + names[names.Count - 1];
        }

        /// <summary>
        /// Finds a pet or food name by ItemId, searching pet shop and food shop slots.
        /// </summary>
        private static string FindItemNameById(Il2CppSpacewood.Core.Models.Item.ItemId id, HangarMain hangar)
        {
            try
            {
                // Search pet shop
                var minionShop = hangar?.MinionShop?.Spaces;
                if (minionShop != null)
                {
                    for (int i = 0; i < minionShop.Count; i++)
                    {
                        try
                        {
                            var sp = minionShop[i];
                            var m = sp?.MinionModel;
                            if (m != null && m.Id == id)
                                return PetStatsReader.GetLocalizedName(m);
                        }
                        catch { }
                    }
                }

                // Search food shop
                var spellShop = hangar?.SpellShop?.Spaces;
                if (spellShop != null)
                {
                    for (int i = 0; i < spellShop.Count; i++)
                    {
                        try
                        {
                            var sp = spellShop[i];
                            var s = sp?.SpellModel;
                            if (s != null && s.Id == id)
                                return PetStatsReader.SplitCamelCase(s.Enum.ToString());
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Checks if a food item is a multi-pet/zone-target food that needs SpellZone confirmation
        /// (e.g., Salad Bowl) rather than clicking a specific pet.
        /// Returns true if template.Aim is null (no per-pet targeting).
        /// </summary>
        private static bool IsZoneTargetFood(SpellModel spell)
        {
            try
            {
                var template = spell.Template;
                if (template == null) return false;
                return template.Aim == null;
            }
            catch { }
            return false;
        }

        private static string GetSlotDescription(GameSpace space, ShopZone zone, HangarMain hangar = null)
        {
            if (space == null) return "Empty";

            try
            {
                // Check frozen state for shop items
                string frozen = "";
                if (zone == ShopZone.PetShop || zone == ShopZone.FoodShop)
                {
                    if (IsSlotFrozen(space))
                        frozen = "Frozen, ";
                }

                switch (zone)
                {
                    case ShopZone.PetShop:
                        var minionModel = space.MinionModel;
                        if (minionModel != null)
                        {
                            string desc = frozen + PetStatsReader.ReadMinionBrief(minionModel);

                            // Check for linked pets
                            try
                            {
                                var links = minionModel.Links;
                                string linkedInfo = GetLinkedDescription(links, hangar, minionModel.Id);
                                if (!string.IsNullOrEmpty(linkedInfo))
                                    desc += $", chained to {linkedInfo}";
                            }
                            catch { }

                            return desc;
                        }
                        return "Empty";

                    case ShopZone.FoodShop:
                        var spellModel = space.SpellModel;
                        if (spellModel != null)
                        {
                            string desc = frozen + PetStatsReader.ReadSpellDetailed(spellModel);
                            return desc;
                        }
                        return "Empty";

                    case ShopZone.Team:
                        var teamMinion = space.MinionModel;
                        // Fallback: Space.MinionModel can return null even when a pet is present.
                        // Try BoardModel.Minions.Items[index] as a backup.
                        if (teamMinion == null && hangar != null)
                        {
                            try
                            {
                                var board = hangar.Overlay?.BoardModel;
                                if (board?.Minions?.Items != null && _currentIndex < board.Minions.Items.Count)
                                {
                                    teamMinion = board.Minions.Items[_currentIndex];
                                }
                            }
                            catch { }
                        }
                        if (teamMinion != null)
                        {
                            return PetStatsReader.ReadMinion(teamMinion);
                        }
                        return "Empty slot";

                    default:
                        return "Unknown";
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"GetSlotDescription error: {ex.Message}");
                return "Error reading slot";
            }
        }

        // --- Status query keys ---

        private static void AnnounceGold(HangarMain hangar)
        {
            try
            {
                var board = hangar.Overlay?.BoardModel;
                if (board != null)
                {
                    AccessibilityManager.Announce(PetStatsReader.ReadGoldStatus(board));
                }
                else
                {
                    // Try reading from the gold text mesh directly
                    try
                    {
                        var goldText = hangar.Overlay?.Gold?.CurrentTextMesh?.text;
                        if (!string.IsNullOrEmpty(goldText))
                            AccessibilityManager.Announce($"{goldText} gold");
                        else
                            AccessibilityManager.Announce("Gold unknown");
                    }
                    catch { AccessibilityManager.Announce("Gold unknown"); }
                }
            }
            catch { AccessibilityManager.Announce("Gold unknown"); }
        }

        private static void AnnounceLives(HangarMain hangar)
        {
            try
            {
                var board = hangar.Overlay?.BoardModel;
                if (board != null)
                {
                    AccessibilityManager.Announce(PetStatsReader.ReadLivesStatus(board));
                }
                else
                {
                    try
                    {
                        var livesText = hangar.Overlay?.Lives?.TextMesh?.text;
                        if (!string.IsNullOrEmpty(livesText))
                            AccessibilityManager.Announce($"{livesText} lives");
                        else
                            AccessibilityManager.Announce("Lives unknown");
                    }
                    catch { AccessibilityManager.Announce("Lives unknown"); }
                }
            }
            catch { AccessibilityManager.Announce("Lives unknown"); }
        }

        private static void AnnounceTurn(HangarMain hangar)
        {
            try
            {
                var board = hangar.Overlay?.BoardModel;
                if (board != null)
                {
                    AccessibilityManager.Announce(PetStatsReader.ReadTurnStatus(board));
                }
                else
                {
                    try
                    {
                        var turnsText = hangar.Overlay?.Turns?.TextMesh?.text;
                        if (!string.IsNullOrEmpty(turnsText))
                            AccessibilityManager.Announce($"Turn {turnsText}");
                        else
                            AccessibilityManager.Announce("Turn unknown");
                    }
                    catch { AccessibilityManager.Announce("Turn unknown"); }
                }
            }
            catch { AccessibilityManager.Announce("Turn unknown"); }
        }

        private static void AnnounceWins(HangarMain hangar)
        {
            try
            {
                var board = hangar.Overlay?.BoardModel;
                if (board != null)
                {
                    AccessibilityManager.Announce(PetStatsReader.ReadWinsStatus(board));
                }
                else
                {
                    AccessibilityManager.Announce("Wins unknown");
                }
            }
            catch { AccessibilityManager.Announce("Wins unknown"); }
        }

        /// <summary>
        /// V key: read the upcoming bully team that's visually shown on the right side
        /// of the Daily-mode shop. Reads BoardView.Model on /Build/Hangar/Board/BullyBoardView
        /// — that's the BoardModel for the next opponent. Announces pet names + stats.
        /// </summary>
        private static void AnnounceUpcomingBullies(HangarMain hangar)
        {
            try
            {
                bool isBullyRush = false;
                try
                {
                    isBullyRush = Il2CppSpacewood.Unity.Memory.Mode ==
                        Il2CppSpacewood.Core.Enums.Mode.BullyRush;
                }
                catch { }
                if (!isBullyRush)
                {
                    AccessibilityManager.Announce("Upcoming bullies only available in Daily mode");
                    return;
                }

                Il2CppSpacewood.Unity.Views.BoardView view = null;
                try
                {
                    var bvGo = UnityEngine.GameObject.Find("/Build/Hangar/Board/BullyBoardView");
                    if (bvGo != null)
                        view = bvGo.GetComponent<Il2CppSpacewood.Unity.Views.BoardView>();
                }
                catch { }
                if (view?.Model?.Minions?.Items == null)
                {
                    AccessibilityManager.Announce("No upcoming bullies");
                    return;
                }

                var items = view.Model.Minions.Items;
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < items.Count; i++)
                {
                    try
                    {
                        var m = items[i];
                        if (m == null) continue;
                        string desc = PetStatsReader.ReadMinionNameAndStats(m);
                        if (!string.IsNullOrEmpty(desc) && desc != "Empty")
                            parts.Add(desc);
                    }
                    catch { }
                }

                if (parts.Count == 0)
                    AccessibilityManager.Announce("No upcoming bullies");
                else
                    AccessibilityManager.Announce(
                        $"Upcoming bullies: {string.Join(", ", parts)}");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"AnnounceUpcomingBullies error: {ex.Message}");
                AccessibilityManager.Announce("Could not read upcoming bullies");
            }
        }

        private static void AnnounceDetailedInfo(HangarMain hangar)
        {
            // I key: detailed info on selected pet
            if (_currentZone == ShopZone.None || _currentZone == ShopZone.Actions)
            {
                AccessibilityManager.Announce("Select a pet first");
                return;
            }

            if (_currentSlots.Count == 0 || _currentIndex >= _currentSlots.Count)
            {
                AccessibilityManager.Announce("No pet selected");
                return;
            }

            var space = _currentSlots[_currentIndex];
            if (space == null)
            {
                AccessibilityManager.Announce("Empty slot");
                return;
            }

            try
            {
                if (_currentZone == ShopZone.FoodShop)
                {
                    var spell = space.SpellModel;
                    if (spell != null)
                        AccessibilityManager.Announce(PetStatsReader.ReadSpellDetailed(spell));
                    else
                        AccessibilityManager.Announce("Empty slot");
                }
                else
                {
                    var minion = space.MinionModel;
                    // Fallback: Space.MinionModel can return null for team slots
                    if (minion == null && _currentZone == ShopZone.Team)
                    {
                        try
                        {
                            var board = hangar.Overlay?.BoardModel;
                            if (board?.Minions?.Items != null && _currentIndex < board.Minions.Items.Count)
                                minion = board.Minions.Items[_currentIndex];
                        }
                        catch { }
                    }
                    if (minion != null)
                        AccessibilityManager.Announce(PetStatsReader.ReadMinionDetailed(minion));
                    else
                        AccessibilityManager.Announce("Empty slot");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"DetailedInfo error: {ex.Message}");
                AccessibilityManager.Announce("Cannot read info");
            }
        }

        /// <summary>
        /// Opens the scoreboard panel (same as clicking the Opponent button).
        /// The scoreboard shows all opponents with their teams.
        /// Polling in SuperAutoAccessibility.cs handles announcements and navigation.
        /// </summary>
        private static void OpenScoreboard(HangarMain hangar)
        {
            try
            {
                hangar.OpenScoreboard();
                MelonLogger.Msg("O key: Opened scoreboard");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"OpenScoreboard error: {ex.Message}");
                AccessibilityManager.Announce("Could not open scoreboard");
            }
        }

        // --- Shop action keys ---

        private static void PerformAction(HangarMain hangar)
        {
            // Enter key: context-dependent action
            if (_currentZone == ShopZone.Actions)
            {
                // Click the currently focused action button
                if (_actionButtons.Count > 0 && _currentIndex < _actionButtons.Count)
                {
                    var (button, label) = _actionButtons[_currentIndex];
                    try
                    {
                        button.Click();
                        MelonLogger.Msg($"[Shop Action] Clicked: {label}");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"Action click error: {ex.Message}");
                    }
                }
                return;
            }

            if (_currentSlots.Count == 0 || _currentIndex >= _currentSlots.Count) return;

            var space = _currentSlots[_currentIndex];
            if (space == null) return;

            try
            {
                if (_currentZone == ShopZone.PetShop)
                {
                    // Snapshot shop state before buy for unchained detection
                    SnapshotShopState(hangar);

                    // Check if this pet can combine with a team pet before buying
                    var shopMinion = space.MinionModel;
                    string shopEnum = "";
                    CombineStatus preStatus = CombineStatus.None;
                    if (shopMinion != null)
                    {
                        try { shopEnum = shopMinion.Enum.ToString(); } catch { }
                        preStatus = CheckCombineStatus(shopMinion, hangar);
                    }

                    // Buy pet from shop â€” invoke the click handler
                    hangar.HandleMinionShopClick(hangar.MinionShop, space);
                    MelonLogger.Msg($"[Shop Action] Bought pet from shop slot {_currentIndex + 1}");

                    // If combinable, auto-focus the team pet to combine with
                    if (preStatus != CombineStatus.None && !string.IsNullOrEmpty(shopEnum))
                    {
                        _pendingCombineFocus = true;
                        _combineFocusDelayFrames = 2;
                        _combineTargetEnum = shopEnum;
                        _pendingAutoMoveToBoard = false;
                    }
                    else
                    {
                        // Schedule auto-move to board after a short delay
                        _pendingAutoMoveToBoard = true;
                        _autoMoveDelayFrames = 2;
                    }
                }
                else if (_currentZone == ShopZone.FoodShop)
                {
                    // Buy food from shop â€” all foods enter SpellFocus targeting mode
                    // The game handles targeting (single-pet or multi-pet) automatically
                    // PollSpellFocusState() will announce targeting instructions
                    hangar.HandleSpellShopClick(hangar.SpellShop, space);
                    MelonLogger.Msg($"[Shop Action] Bought food from shop slot {_currentIndex + 1}");
                }
                else if (_currentZone == ShopZone.Team)
                {
                    // Snapshot shop state before team click â€” placing a bought pet completes
                    // the purchase and may unchained linked pets in the shop
                    SnapshotShopState(hangar);

                    // Check if we're in SpellFocus with a zone-target food (e.g., Salad Bowl)
                    // These foods need SpellZone confirmation, not a pet click
                    bool handledBySpellZone = false;
                    if (_lastHangarState == HangarState.SpellFocus)
                    {
                        try
                        {
                            var spellFocusState = hangar.StateMachine?.State?.TryCast<HangarStateShopSpellFocus>();
                            if (spellFocusState?.Spell != null && IsZoneTargetFood(spellFocusState.Spell))
                            {
                                // Zone-target food â€” click SpellZone to confirm
                                var spellZone = hangar.GetComponentInChildren<Il2CppSpacewood.Unity.SpellZone>();
                                if (spellZone?.PointerCapture != null)
                                {
                                    spellZone.PointerCapture.Click();
                                    string foodName = PetStatsReader.ReadSpell(spellFocusState.Spell);
                                    AccessibilityManager.Announce($"Used {foodName}");
                                    MelonLogger.Msg($"[Shop Action] Used zone-target food via SpellZone click");
                                    handledBySpellZone = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"SpellZone click error: {ex.Message}");
                        }
                    }

                    if (!handledBySpellZone)
                    {
                        // Guard: don't click empty slots during food targeting â€” causes NullReferenceException
                        // Space.MinionModel can return null for occupied slots, so check BoardModel too
                        bool slotOccupied = space.MinionModel != null;
                        if (!slotOccupied)
                        {
                            try
                            {
                                var board = hangar.Overlay?.BoardModel;
                                if (board?.Minions?.Items != null && _currentIndex < board.Minions.Items.Count
                                    && board.Minions.Items[_currentIndex] != null)
                                    slotOccupied = true;
                            }
                            catch { }
                        }

                        if (_lastHangarState == HangarState.SpellFocus && !slotOccupied)
                        {
                            AccessibilityManager.Announce("Empty slot. Select a pet.");
                            MelonLogger.Msg("[Shop Action] Blocked food use on empty slot");
                        }
                        else
                        {
                            // Normal team click (combining, single-target food, etc.)
                            hangar.HandleMinionArmyClick(hangar.MinionArmy, space);
                            MelonLogger.Msg($"[Shop Action] Clicked team slot {_currentIndex + 1}");
                        }
                    }
                }

                // Refresh slots after action (shop state may have changed)
                RefreshCurrentZone(hangar);

                // Check for unchained pets and new food after any action
                // (placing a pet on the board from Team zone completes the buy and unchains others)
                CheckForUnchainedAndFoodStocked(hangar);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PerformAction error: {ex.Message}");
            }
        }

        /// <summary>
        /// After buying a pet, auto-switch to Team zone and focus the next empty slot (right to left).
        /// </summary>
        private static void AutoMoveToBoard(HangarMain hangar)
        {
            if (hangar == null) return;

            try
            {
                // Switch to Team zone WITHOUT announcing (avoid double announcement from SwitchToZone)
                _currentZone = ShopZone.Team;
                _currentIndex = 0;
                _currentSlots.Clear();
                _actionButtons.Clear();
                PopulateTeamSlots(hangar);
                SectionManager.InvalidateCache();

                // Find rightmost empty slot (right to left) using BoardModel fallback
                // Space.MinionModel can return null for occupied slots, so check BoardModel too
                var board = hangar.Overlay?.BoardModel;
                for (int i = _currentSlots.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        bool occupied = false;
                        var space = _currentSlots[i];
                        if (space?.MinionModel != null)
                        {
                            occupied = true;
                        }
                        else if (board?.Minions?.Items != null && i < board.Minions.Items.Count && board.Minions.Items[i] != null)
                        {
                            occupied = true;
                        }

                        if (!occupied)
                        {
                            _currentIndex = i;
                            AnnounceCurrentItem(hangar, includeZoneName: true);
                            MelonLogger.Msg($"[Shop] Auto-moved to team slot {i + 1} (empty)");
                            return;
                        }
                    }
                    catch { }
                }

                // Board full â€” announce current position
                AnnounceCurrentItem(hangar, includeZoneName: true);
                MelonLogger.Msg("[Shop] Auto-moved to team (board full)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"AutoMoveToBoard error: {ex.Message}");
            }
        }

        private static void PerformRoll(HangarMain hangar)
        {
            try
            {
                var rollButton = hangar.Overlay?.Roll?.Button;
                if (rollButton != null)
                {
                    // Snapshot shop state before roll for unchained/food detection
                    SnapshotShopState(hangar);

                    rollButton.Click();
                    MelonLogger.Msg("[Shop Action] Roll");

                    // Announce tier after roll
                    try
                    {
                        var board = hangar.Overlay?.BoardModel;
                        if (board != null)
                        {
                            AccessibilityManager.Announce($"Rolled. Tier {board.Tier}.", interrupt: false);
                        }
                        else
                        {
                            AccessibilityManager.Announce("Rolled", interrupt: false);
                        }
                    }
                    catch { AccessibilityManager.Announce("Rolled", interrupt: false); }

                    // Refresh slots after roll (new pets/food appear)
                    RefreshCurrentZone(hangar);

                    // Check for unchained pets and newly stocked food
                    CheckForUnchainedAndFoodStocked(hangar);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Roll error: {ex.Message}");
            }
        }

        private static void PerformFreeze(HangarMain hangar)
        {
            // Freeze only applies to shop items
            if (_currentZone != ShopZone.PetShop && _currentZone != ShopZone.FoodShop)
            {
                AccessibilityManager.Announce("Select a shop item to freeze");
                return;
            }

            if (_currentSlots.Count == 0 || _currentIndex >= _currentSlots.Count)
            {
                AccessibilityManager.Announce("No item to freeze");
                return;
            }

            var space = _currentSlots[_currentIndex];
            if (space == null)
            {
                AccessibilityManager.Announce("Empty slot");
                return;
            }

            try
            {
                // Get item name before freeze
                string itemName = "item";
                if (_currentZone == ShopZone.PetShop)
                {
                    var minion = space.MinionModel;
                    if (minion != null)
                        itemName = PetStatsReader.GetLocalizedName(minion);
                    else
                    {
                        AccessibilityManager.Announce("Empty slot");
                        return;
                    }
                }
                else // FoodShop
                {
                    var spell = space.SpellModel;
                    if (spell != null)
                    {
                        try
                        {
                            var spellAsset = SpellEnumExtensions.ToAsset(spell.Enum);
                            if (spellAsset != null)
                            {
                                string localized = spellAsset.GetName();
                                if (!string.IsNullOrEmpty(localized))
                                    itemName = localized;
                                else
                                    itemName = PetStatsReader.SplitCamelCase(spell.Enum.ToString());
                            }
                            else
                                itemName = PetStatsReader.SplitCamelCase(spell.Enum.ToString());
                        }
                        catch { itemName = PetStatsReader.SplitCamelCase(spell.Enum.ToString()); }
                    }
                    else
                    {
                        AccessibilityManager.Announce("Empty slot");
                        return;
                    }
                }

                bool wasFrozen = IsSlotFrozen(space);

                // Use game's FreezeItem API directly (MinionModel/SpellModel both inherit from ItemModel)
                try
                {
                    Il2CppSpacewood.Core.Models.Item.ItemModel itemModel = null;
                    if (_currentZone == ShopZone.PetShop)
                        itemModel = space.MinionModel;
                    else if (_currentZone == ShopZone.FoodShop)
                        itemModel = space.SpellModel;

                    if (itemModel != null)
                    {
                        hangar.FreezeItem(itemModel);
                        string action = wasFrozen ? "Unfroze" : "Froze";
                        AccessibilityManager.Announce($"{action} {itemName}");
                        MelonLogger.Msg($"[Shop Action] {action} {itemName}");
                        RefreshCurrentZone(hangar);
                    }
                    else
                    {
                        AccessibilityManager.Announce("Cannot freeze empty slot");
                    }
                }
                catch (Exception freezeEx) { MelonLogger.Warning($"FreezeItem error: {freezeEx.Message}"); }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Freeze error: {ex.Message}");
            }
        }

        private static void PerformEndTurn(HangarMain hangar)
        {
            try
            {
                var doneButton = hangar.Overlay?.DoneButton;
                if (doneButton != null)
                {
                    doneButton.Click();
                    MelonLogger.Msg("[Shop Action] End turn");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"End turn error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the MinionModel for the current shop slot, with board model fallback.
        /// Space.MinionModel can be null even when the board model has data.
        /// </summary>
        private static MinionModel GetCurrentShopMinion(HangarMain hangar)
        {
            if (_currentSlots.Count == 0 || _currentIndex >= _currentSlots.Count) return null;
            try
            {
                var m = _currentSlots[_currentIndex].MinionModel;
                if (m != null) return m;
            }
            catch { }
            // Board model fallback
            try
            {
                var board = hangar?.Overlay?.BoardModel;
                if (board?.MinionShop != null && _currentIndex < board.MinionShop.Count)
                    return board.MinionShop[_currentIndex];
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Checks if a shop pet can combine with any team pet.
        /// Returns WillLevelUp if combining would cause a level up, CanCombine if it would combine without leveling,
        /// or None if no combine target exists.
        /// </summary>
        private static CombineStatus CheckCombineStatus(MinionModel shopMinion, HangarMain hangar)
        {
            if (shopMinion == null || hangar == null) return CombineStatus.None;

            try
            {
                // If the shop pet itself is max level, it can't combine
                try { if (shopMinion.Level >= 3) return CombineStatus.None; } catch { }

                var shopEnum = shopMinion.Enum;
                var board = hangar.Overlay?.BoardModel;
                if (board?.Minions?.Items == null) return CombineStatus.None;

                var teamMinions = board.Minions.Items;
                for (int i = 0; i < teamMinions.Count; i++)
                {
                    try
                    {
                        var teamPet = teamMinions[i];
                        if (teamPet == null) continue;
                        if (teamPet.Enum != shopEnum) continue;

                        // Same pet type found on team â€” check if combining would level up
                        int teamLevel = teamPet.Level;
                        int teamExp = teamPet.Exp;
                        MelonLogger.Msg($"[Combine] Checking team slot {i}: level={teamLevel}, exp={teamExp}, enum={teamPet.Enum}");

                        if (teamLevel >= 3) continue; // Already max level

                        // Also check if this pet has max XP for its level (about to level up naturally)
                        int expNeeded = 0;
                        try
                        {
                            var reqs = Il2CppSpacewood.Core.Models.BoardConstants.LevelRequirements;
                            if (reqs != null && teamLevel < reqs.Count)
                                expNeeded = reqs[teamLevel];
                        }
                        catch { }

                        // Combining adds 1 exp (the bought pet itself)
                        if (expNeeded > 0 && teamExp + 1 >= expNeeded)
                            return CombineStatus.WillLevelUp;
                        return CombineStatus.CanCombine;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Combine] Error checking team slot {i}: {ex.Message}");
                    }
                }
            }
            catch { }

            return CombineStatus.None;
        }

        /// <summary>
        /// After buying a combinable pet, auto-focus the team pet with the same enum for easy combining.
        /// </summary>
        private static void AutoFocusCombineTarget(HangarMain hangar, string targetEnum)
        {
            if (hangar == null || string.IsNullOrEmpty(targetEnum)) return;

            try
            {
                _currentZone = ShopZone.Team;
                _currentIndex = 0;
                _currentSlots.Clear();
                _actionButtons.Clear();
                PopulateTeamSlots(hangar);
                SectionManager.InvalidateCache();

                var board = hangar.Overlay?.BoardModel;
                if (board?.Minions?.Items != null)
                {
                    // Find the rightmost team pet matching the target enum
                    for (int i = board.Minions.Items.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            var pet = board.Minions.Items[i];
                            if (pet != null && pet.Enum.ToString() == targetEnum)
                            {
                                _currentIndex = i;
                                AccessibilityManager.Announce($"Team, combine with {PetStatsReader.GetLocalizedName(pet)}, {i + 1} of {_currentSlots.Count}");
                                MelonLogger.Msg($"[Shop] Auto-focused combine target at slot {i + 1}");
                                BuildDetailLinesForCurrentSlot(hangar);
                                return;
                            }
                        }
                        catch { }
                    }
                }

                // Fallback: no combine target found, just go to team
                AnnounceCurrentItem(hangar, includeZoneName: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"AutoFocusCombineTarget error: {ex.Message}");
            }
        }

        /// <summary>
        /// Takes a snapshot of the current shop state (pet and food enums).
        /// Called before roll/refresh to detect unchained pets and new food.
        /// </summary>
        public static void SnapshotShopState(HangarMain hangar)
        {
            _previousPetShopEnums.Clear();
            _previousFoodShopEnums.Clear();
            if (hangar == null) return;

            try
            {
                var minionShop = hangar.MinionShop?.Spaces;
                if (minionShop != null)
                {
                    for (int i = 0; i < minionShop.Count; i++)
                    {
                        try
                        {
                            var m = minionShop[i]?.MinionModel;
                            _previousPetShopEnums.Add(m != null ? m.Enum.ToString() : "");
                        }
                        catch { _previousPetShopEnums.Add(""); }
                    }
                }

                var spellShop = hangar.SpellShop?.Spaces;
                if (spellShop != null)
                {
                    for (int i = 0; i < spellShop.Count; i++)
                    {
                        try
                        {
                            var s = spellShop[i]?.SpellModel;
                            _previousFoodShopEnums.Add(s != null ? s.Enum.ToString() : "");
                        }
                        catch { _previousFoodShopEnums.Add(""); }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Checks for unchained pets (disappeared from shop) and newly stocked food after a shop change.
        /// Call after roll, buy, or shop refresh.
        /// </summary>
        public static void CheckForUnchainedAndFoodStocked(HangarMain hangar)
        {
            if (hangar == null) return;

            // Check for unchained pets (pets that disappeared without being bought)
            try
            {
                var minionShop = hangar.MinionShop?.Spaces;
                if (minionShop != null && _previousPetShopEnums.Count > 0)
                {
                    for (int i = 0; i < _previousPetShopEnums.Count && i < minionShop.Count; i++)
                    {
                        string prev = _previousPetShopEnums[i];
                        if (string.IsNullOrEmpty(prev)) continue;

                        try
                        {
                            var current = minionShop[i]?.MinionModel;
                            string currentEnum = current != null ? current.Enum.ToString() : "";

                            // Pet was there before but is now gone or different â€” unchained
                            if (currentEnum != prev && string.IsNullOrEmpty(currentEnum))
                            {
                                SoundManager.Play("SAP_Unchained");
                                string petName = PetStatsReader.SplitCamelCase(prev);
                                AccessibilityManager.Announce($"{petName} unchained", interrupt: false);
                                MelonLogger.Msg($"[Shop] Unchained: {petName} at slot {i + 1}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Check for newly stocked food
            try
            {
                var spellShop = hangar.SpellShop?.Spaces;
                if (spellShop != null && _previousFoodShopEnums.Count > 0)
                {
                    for (int i = 0; i < spellShop.Count; i++)
                    {
                        try
                        {
                            var current = spellShop[i]?.SpellModel;
                            if (current == null) continue;

                            string currentEnum = current.Enum.ToString();
                            string prevEnum = i < _previousFoodShopEnums.Count ? _previousFoodShopEnums[i] : "";

                            // Slot was empty, now has food
                            if (string.IsNullOrEmpty(prevEnum) && !string.IsNullOrEmpty(currentEnum))
                            {
                                string foodName = PetStatsReader.SplitCamelCase(currentEnum);
                                try
                                {
                                    var asset = SpellEnumExtensions.ToAsset(current.Enum);
                                    if (asset != null)
                                    {
                                        string loc = asset.GetName();
                                        if (!string.IsNullOrEmpty(loc)) foodName = loc;
                                    }
                                }
                                catch { }
                                AccessibilityManager.Announce($"Food stocked: {foodName}", interrupt: false);
                                MelonLogger.Msg($"[Shop] Food stocked: {foodName} at slot {i + 1}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void PerformSell(HangarMain hangar)
        {
            // Sell: only valid in Team zone
            if (_currentZone != ShopZone.Team)
            {
                AccessibilityManager.Announce("Select a team pet to sell");
                return;
            }

            if (_currentSlots.Count == 0 || _currentIndex >= _currentSlots.Count) return;

            var space = _currentSlots[_currentIndex];
            MinionModel sellMinion = space?.MinionModel;
            // Fallback to BoardModel if Space.MinionModel is null
            if (sellMinion == null)
            {
                try
                {
                    var board = hangar.Overlay?.BoardModel;
                    if (board?.Minions?.Items != null && _currentIndex < board.Minions.Items.Count)
                        sellMinion = board.Minions.Items[_currentIndex];
                }
                catch { }
            }
            if (sellMinion == null)
            {
                AccessibilityManager.Announce("Empty slot, nothing to sell");
                return;
            }

            try
            {
                string petName = PetStatsReader.GetLocalizedName(sellMinion);
                int sellValue = 1;
                try
                {
                    var sv = sellMinion.SellValue;
                    if (sv.HasValue)
                        sellValue = sv.Value;
                }
                catch { }

                var sellButton = hangar.Overlay?.SellButton;
                if (sellButton == null) return;

                // Check if pet is already selected in the game
                bool alreadySelected = false;
                try
                {
                    var stateMachine = hangar.StateMachine;
                    if (stateMachine?.State?.Enum == HangarState.ArmyMinionFocus)
                        alreadySelected = true;
                }
                catch { }

                if (alreadySelected)
                {
                    // Already selected â€” sell immediately
                    sellButton.Click();
                    AccessibilityManager.Announce($"Sold {petName}");
                    AccessibilityManager.Announce($"Gained {sellValue} gold", interrupt: false);
                    MelonLogger.Msg($"[Shop Action] Sold {petName} (pre-selected)");
                    RefreshCurrentZone(hangar);
                }
                else
                {
                    // Select the pet first, then defer sell by a few frames
                    hangar.HandleMinionArmyClick(hangar.MinionArmy, space);
                    _pendingSell = true;
                    _sellDelayFrames = 3;
                    _sellPetName = petName;
                    _sellValue = sellValue;
                    MelonLogger.Msg($"[Shop Action] Deferred sell for {petName}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Sell error: {ex.Message}");
            }
        }
    }
}
