using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace SuperAutoAccessibility.Gameplay
{
    public enum GamePhase
    {
        Menu,
        ShopBuild,
        Battle,
        BattleOutcome,
        Unknown
    }

    /// <summary>
    /// Detects the current gameplay phase (menu, shop/build, battle) by polling
    /// for the existence of key game components each frame.
    /// Announces phase transitions to the screen reader.
    /// </summary>
    public static class GameplayPhaseDetector
    {
        private static GamePhase _currentPhase = GamePhase.Menu;
        private static GamePhase _lastAnnouncedPhase = GamePhase.Unknown;

        // Cache references to avoid FindObjectOfType every frame
        private static Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarMain _hangarMain;
        private static int _pollCooldown = 0;
        private const int POLL_INTERVAL = 15; // Check every 15 frames (~0.25s)

        // Track previous losses for battle outcome detection
        private static int _preBattleLosses = 0;
        private static int _preBattleLivesMax = 0;

        // Cache board model from shop phase for battle narration
        private static Il2CppSpacewood.Core.Models.BoardModel _cachedBoardModel;

        // VS screen announcement (delayed to let UIBattleIntro populate, with retries)
        private static bool _pendingVsAnnouncement = false;
        private static int _vsAnnouncementDelay = 0;
        private static int _vsRetryCount = 0;
        private const int VS_MAX_RETRIES = 5;
        private const int VS_RETRY_INTERVAL = 15; // frames between retries

        public static GamePhase CurrentPhase => _currentPhase;

        /// <summary>
        /// Returns the cached HangarMain reference (the central shop controller).
        /// May be null if not in shop phase.
        /// </summary>
        public static Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarMain GetHangarMain()
        {
            return _hangarMain;
        }

        /// <summary>
        /// Called every frame from OnUpdate. Polls game components to detect phase changes.
        /// </summary>
        public static void Update()
        {
            _pollCooldown--;
            if (_pollCooldown > 0) return;
            _pollCooldown = POLL_INTERVAL;

            DetectPhase();

            if (_currentPhase != _lastAnnouncedPhase)
            {
                AnnouncePhaseChange();
                _lastAnnouncedPhase = _currentPhase;
            }
        }

        private static void DetectPhase()
        {
            // Check HangarMain (shop/build phase)
            try
            {
                if (_hangarMain == null)
                {
                    _hangarMain = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarMain>();
                }

                if (_hangarMain != null)
                {
                    try
                    {
                        var _ = _hangarMain.gameObject;
                    }
                    catch
                    {
                        _hangarMain = null;
                    }
                }
            }
            catch
            {
                _hangarMain = null;
            }

            // Check BattleController (battle phase)
            bool battleActive = false;
            try
            {
                var battleController = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Battle.BattleController>();
                if (battleController != null)
                {
                    try
                    {
                        var _ = battleController.gameObject;
                        if (battleController.gameObject.activeInHierarchy)
                            battleActive = true;
                    }
                    catch { }
                }
            }
            catch { }

            // Determine phase
            if (battleActive)
            {
                _currentPhase = GamePhase.Battle;
            }
            else if (_hangarMain != null)
            {
                _currentPhase = GamePhase.ShopBuild;
            }
            else
            {
                _currentPhase = GamePhase.Menu;
            }
        }

        private static void AnnouncePhaseChange()
        {
            try
            {
                switch (_currentPhase)
                {
                    case GamePhase.ShopBuild:
                        SuperAutoAccessibility.Patches.GameplayPatches.ResetBattleSubscription();
                        AnnounceShopPhase();
                        break;
                    case GamePhase.Battle:
                        ShopNavigationManager.Reset();
                        SAPMod.ResetBattleState();
                        AnnounceBattleStart();
                        break;
                    case GamePhase.Menu:
                        ShopNavigationManager.Reset();
                        SuperAutoAccessibility.Patches.GameplayPatches.ResetBattleSubscription();
                        // Don't announce menu phase â€” PageManagerPatches handles menu page names
                        break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Phase announcement error: {ex.Message}");
            }
        }

        private static void AnnounceShopPhase()
        {
            try
            {
                if (_hangarMain == null) return;

                var overlay = _hangarMain.Overlay;
                if (overlay == null) return;

                var boardModel = overlay.BoardModel;
                if (boardModel == null)
                {
                    AccessibilityManager.Announce("Shop phase");
                    return;
                }

                string summary = PetStatsReader.ReadBoardStatus(boardModel);

                // Check for battle outcome from previous round
                string outcomeText = "";
                try
                {
                    var outcome = boardModel.PreviousOutcome;
                    if (outcome != null && outcome.HasValue &&
                        (_lastAnnouncedPhase == GamePhase.Battle || _lastAnnouncedPhase == GamePhase.Unknown))
                    {
                        string outcomeStr = outcome.Value.ToString();
                        if (outcomeStr == "PlayerWon")
                        {
                            // Arena wins are announced by PollTallyArena with trophy info â€” skip here
                            bool isArenaWin = false;
                            try
                            {
                                var tallyArena = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArena>();
                                isArenaWin = tallyArena != null;
                            }
                            catch { }

                            if (!isArenaWin)
                                outcomeText = "You won the battle! ";
                        }
                        else if (outcomeStr == "EnemyWon")
                        {
                            int livesLost = boardModel.Losses - _preBattleLosses;
                            if (livesLost > 0)
                                outcomeText = $"You lost, {livesLost} damage. ";
                            else
                                outcomeText = "You lost. ";
                        }
                        else if (outcomeStr == "Draw")
                        {
                            outcomeText = "Draw! ";
                        }
                        else if (outcomeStr == "TimeoutDraw")
                        {
                            outcomeText = "Timeout draw! ";
                        }
                    }
                }
                catch { }

                AccessibilityManager.Announce($"{outcomeText}Shop phase. {summary}");

                // Set input cooldown to prevent leftover key states from triggering actions
                ShopNavigationManager.SetInputCooldown();

                // Pre-build the perk name cache so first navigation is fast
                DetailLineProvider.EnsurePerkNameCachePublic();

                // Save pre-battle state for next outcome detection
                try
                {
                    _preBattleLosses = boardModel.Losses;
                    _preBattleLivesMax = boardModel.LivesMax;
                    _cachedBoardModel = boardModel;
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Shop phase announce error: {ex.Message}");
                AccessibilityManager.Announce("Shop phase");
            }
        }

        private static void AnnounceBattleStart()
        {
            BattleNarrator.OnBattleStart(_cachedBoardModel);
            AccessibilityManager.Announce("Battle starting");

            // Schedule VS screen reading after a short delay to let UIBattleIntro populate
            _pendingVsAnnouncement = true;
            _vsAnnouncementDelay = 30; // ~0.5 seconds at 60fps
            _vsRetryCount = 0;
        }

        /// <summary>
        /// Force a re-check on next Update (e.g., after a page transition).
        /// </summary>
        public static void InvalidateCache()
        {
            _hangarMain = null;
            _pollCooldown = 0;
        }

        /// <summary>
        /// Returns true if we're in the shop/build phase.
        /// </summary>
        public static bool IsShopPhase()
        {
            return _currentPhase == GamePhase.ShopBuild && _hangarMain != null;
        }

        /// <summary>
        /// Returns true if we're in the battle phase.
        /// </summary>
        public static bool IsBattlePhase()
        {
            return _currentPhase == GamePhase.Battle;
        }

        /// <summary>
        /// Called every frame from OnUpdate to process the delayed VS screen announcement.
        /// Reads the UIBattleIntro component to announce player and opponent info.
        /// </summary>
        public static void ProcessVsPending()
        {
            if (!_pendingVsAnnouncement) return;

            _vsAnnouncementDelay--;
            if (_vsAnnouncementDelay > 0) return;

            try
            {
                var battleIntro = UnityEngine.Object.FindObjectOfType<Il2Cpp.UIBattleIntro>();
                if (battleIntro == null)
                {
                    MelonLogger.Msg($"VS screen: UIBattleIntro not found (attempt {_vsRetryCount + 1}/{VS_MAX_RETRIES + 1})");
                    if (_vsRetryCount < VS_MAX_RETRIES)
                    {
                        _vsRetryCount++;
                        _vsAnnouncementDelay = VS_RETRY_INTERVAL;
                        return;
                    }
                    _pendingVsAnnouncement = false;
                    return;
                }

                var parts = new List<string>();

                // Read player intro
                string playerInfo = ReadBattleIntro(battleIntro.PlayerIntro, "You");
                if (!string.IsNullOrEmpty(playerInfo))
                    parts.Add(playerInfo);

                parts.Add("versus");

                // Read opponent intro
                string opponentInfo = ReadBattleIntro(battleIntro.OpponentIntro, "Opponent");
                if (!string.IsNullOrEmpty(opponentInfo))
                    parts.Add(opponentInfo);

                // Check if we got real data (not just fallback labels)
                bool hasRealData = (playerInfo != "You") || (opponentInfo != "Opponent");

                if (hasRealData && parts.Count > 1)
                {
                    // Success â€” announce and stop retrying
                    string vsAnnouncement = string.Join(". ", parts);
                    AccessibilityManager.Announce(vsAnnouncement, interrupt: false);
                    MelonLogger.Msg($"[VS Screen] {vsAnnouncement}");
                    _pendingVsAnnouncement = false;
                }
                else if (_vsRetryCount < VS_MAX_RETRIES)
                {
                    // Data not populated yet â€” retry
                    _vsRetryCount++;
                    _vsAnnouncementDelay = VS_RETRY_INTERVAL;
                    MelonLogger.Msg($"VS screen: data not populated yet, retry {_vsRetryCount}/{VS_MAX_RETRIES}");
                }
                else
                {
                    // Max retries exhausted â€” announce whatever we have
                    if (parts.Count > 1)
                    {
                        string vsAnnouncement = string.Join(". ", parts);
                        AccessibilityManager.Announce(vsAnnouncement, interrupt: false);
                        MelonLogger.Msg($"[VS Screen] (partial) {vsAnnouncement}");
                    }
                    else
                    {
                        MelonLogger.Msg("VS screen: retries exhausted with no usable data");
                    }
                    _pendingVsAnnouncement = false;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"VS screen read error: {ex.Message}");
                if (_vsRetryCount < VS_MAX_RETRIES)
                {
                    _vsRetryCount++;
                    _vsAnnouncementDelay = VS_RETRY_INTERVAL;
                }
                else
                {
                    _pendingVsAnnouncement = false;
                }
            }
        }

        /// <summary>
        /// Reads a UIBattlePlayerIntroduction component (player or opponent side).
        /// Returns a string like "PlayerName, team: TeamName, 2 wins, 4 lives"
        /// </summary>
        private static string ReadBattleIntro(
            Il2Cpp.UIBattlePlayerIntroduction intro, string fallbackLabel)
        {
            if (intro == null) return fallbackLabel;

            try
            {
                var infoParts = new List<string>();

                // Username
                try
                {
                    if (intro.Username != null && !string.IsNullOrWhiteSpace(intro.Username.text))
                        infoParts.Add(intro.Username.text.Trim());
                    else
                        infoParts.Add(fallbackLabel);
                }
                catch { infoParts.Add(fallbackLabel); }

                // Team name
                try
                {
                    if (intro.TeamName != null && !string.IsNullOrWhiteSpace(intro.TeamName.text))
                        infoParts.Add($"team: {intro.TeamName.text.Trim()}");
                }
                catch { }

                // Victories/wins
                try
                {
                    if (intro.Victories != null && !string.IsNullOrWhiteSpace(intro.Victories.text))
                        infoParts.Add($"{intro.Victories.text.Trim()} wins");
                }
                catch { }

                // Lives
                try
                {
                    if (intro.Lives != null && !string.IsNullOrWhiteSpace(intro.Lives.text))
                        infoParts.Add($"{intro.Lives.text.Trim()} lives");
                }
                catch { }

                return string.Join(", ", infoParts);
            }
            catch
            {
                return fallbackLabel;
            }
        }
    }
}
