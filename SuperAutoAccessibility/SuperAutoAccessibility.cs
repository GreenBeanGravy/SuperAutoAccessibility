using System.Collections.Generic;
using System.Text.RegularExpressions;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using HarmonyLib;
using Il2CppSpacewood.Unity.UI;

[assembly: MelonInfo(typeof(SuperAutoAccessibility.SAPMod), "Super Auto Pets Accessibility Mod", "1.1.0", "GreenBean")]
[assembly: MelonGame("Team Wood", "Super Auto Pets")]

namespace SuperAutoAccessibility
{
    public class SAPMod : MelonMod
    {
        private static HarmonyLib.Harmony _harmony;

        // Bootstrap loading screen text polling
        private static Il2CppSpacewood.Unity.Bootstrap _bootstrap;
        private static string _lastBootstrapText = "";

        // Bootstrap terms/policy page
        private static bool _bootstrapTermsDetected = false;
        private static UnityEngine.UI.Button _bootstrapConsoleButton = null;

        // Bootstrap language picker
        private static bool _lastLanguagePickerOpen = false;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Super Auto Pets Accessibility Mod initializing... (update test build)");

            // Check for updates in background (non-blocking).
            // Skip when a dev-mode marker file exists alongside the mod, so local
            // development builds don't trip the "update available" prompt on every
            // launch. The release build will not have this marker.
            string devMarker = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(SAPMod).Assembly.Location) ?? "",
                "SAPAccess.dev");
            if (!System.IO.File.Exists(devMarker))
                AutoUpdater.CheckForUpdate();
            else
                LoggerInstance.Msg("[AutoUpdater] Skipped (dev marker present)");

            TolkSpeech.Initialize();
            TolkSpeech.Speak("Super Auto Pets Accessibility Mod loaded!", true);

            AccessibilityManager.Initialize();
            SoundManager.Initialize();

            try
            {
                _harmony = new HarmonyLib.Harmony("com.superautopets.accessibility");

                Patches.SelectableBasePatches.Initialize(_harmony);
                Patches.EventSystemPatches.Initialize(_harmony);
                Patches.PageManagerPatches.Initialize(_harmony);
                Patches.PopupManagerPatches.Initialize(_harmony);
                Patches.ModalPatches.Initialize(_harmony);
                Patches.SettingsMenuPatches.Initialize(_harmony);
                Patches.GameplayPatches.Initialize(_harmony);

                LoggerInstance.Msg("Accessibility Mod fully initialized!");
            }
            catch (System.Exception ex)
            {
                LoggerInstance.Error($"Error initializing Harmony patches: {ex.Message}");
                LoggerInstance.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        public override void OnUpdate()
        {
            // Poll Bootstrap loading screen text and announce changes
            PollBootstrapText();

            // Poll Bootstrap UI (terms page and language picker)
            PollBootstrapUI();

            // Poll DesyncAlert early â€” before ProcessPending so it can cancel deferred
            // announcements (modals/FocusFirst) that would interrupt the desync announcement.
            PollDesyncAlert();

            // Flush any pending modal announcements (deferred from Awake for text timing)
            AccessibilityManager.ProcessPending();
            
            // Check for delayed tooltip announcements
            AccessibilityManager.OnUpdate();

            // Process deferred one-liner announcement
            Patches.PageManagerPatches.ProcessOneLinerPending();

            // Process deferred VersusLobby summary announcement
            Patches.PageManagerPatches.ProcessVersusLobbySummaryPending();

            // Process deferred UI element dumps (auto-dump on page change)
            UIElementDumper.ProcessPending();

            // Process deferred replay page focus
            ProcessReplayFocusPending();

            // Track text input changes for backspace announcement
            try
            {
                var inputObj = EventSystem.current?.currentSelectedGameObject;
                if (inputObj != null)
                {
                    var tmpInput = inputObj.GetComponent<Il2CppTMPro.TMP_InputField>();
                    if (tmpInput != null)
                    {
                        string currentText = tmpInput.text ?? "";
                        if (inputObj != _lastInputFieldObject)
                        {
                            // Just focused a new input field â€” capture initial text
                            _lastInputFieldObject = inputObj;
                            _lastInputFieldText = currentText;
                        }
                        else if (currentText != _lastInputFieldText)
                        {
                            // Text changed â€” check for deletion (backspace)
                            if (currentText.Length < _lastInputFieldText.Length &&
                                _lastInputFieldText.Length - currentText.Length == 1)
                            {
                                // Single character deleted â€” find which one
                                for (int i = 0; i < _lastInputFieldText.Length; i++)
                                {
                                    string without = _lastInputFieldText.Remove(i, 1);
                                    if (without == currentText)
                                    {
                                        char deletedChar = _lastInputFieldText[i];
                                        string charName = deletedChar == ' ' ? "space" : deletedChar.ToString();
                                        AccessibilityManager.Announce(charName);
                                        break;
                                    }
                                }
                            }
                            _lastInputFieldText = currentText;
                        }
                    }
                    else
                    {
                        _lastInputFieldObject = null;
                    }
                }
                else
                {
                    _lastInputFieldObject = null;
                }
            }
            catch { }

            // Gameplay phase detection (shop/battle/menu)
            Gameplay.GameplayPhaseDetector.Update();

            // Shop navigation input handling (zone keys, status queries, actions)
            Gameplay.ShopNavigationManager.HandleInput();

            // Battle narration queue processing (paced TTS)
            Gameplay.BattleNarrator.ProcessQueue();

            // Shop narration queue processing (paced TTS for trigger events)
            Gameplay.ShopNarrator.ProcessQueue();

            // Subscribe to battle events when in battle phase (lazy init)
            if (Gameplay.GameplayPhaseDetector.IsBattlePhase())
            {
                Patches.GameplayPatches.TrySubscribeToBoardRenderer();
                ApplyPendingFastForward();
            }

            // Poll for arena rank changes (tally screen)
            Patches.GameplayPatches.PollArenaRankChange();

            // ----- Dictionary overlay: must come BEFORE any other input handler -----
            // While the dictionary is open, EVERY keystroke is consumed by it. Navigation
            // keys (arrows, Tab, Enter, Space, Home/End) never reach the shop nav, the
            // game's EventSystem, or any other mod keybind. The overlay disables the
            // EventSystem on open and re-enables it on close (see DictionaryMenu.OpenBrowse).
            if (DictionaryMenu.IsActive)
            {
                DictionaryMenu.HandleInput();
                return;
            }
            // After the dictionary closes, swallow input until the keys held at close
            // time are released. Without this, the Escape that closed the dictionary
            // immediately propagates to whatever menu was underneath.
            if (DictionaryMenu.ShouldSwallowInput()) return;
            // J: open the dictionary in categorized browse mode. Ctrl+F is only honoured
            // FROM INSIDE the dictionary (see DictionaryMenu.HandleInput) — it is not a
            // global app-wide hotkey.
            if (Input.GetKeyDown(KeyCode.J))
            {
                var sel = EventSystem.current?.currentSelectedGameObject;
                if (sel?.GetComponent<InputFieldBase>() == null)
                {
                    DictionaryMenu.OpenBrowse();
                    return;
                }
            }

            // Bootstrap terms input: R reads terms, Enter/Space accepts
            if (_bootstrapTermsDetected && _bootstrap != null)
            {
                if (Input.GetKeyDown(KeyCode.R))
                {
                    var selected = EventSystem.current?.currentSelectedGameObject;
                    if (selected?.GetComponent<InputFieldBase>() == null)
                    {
                        try
                        {
                            var canvasText = _bootstrap.transform.Find("CanvasText");
                            if (canvasText != null)
                            {
                                string termsText = TextExtractor.GetAllTextFromHierarchy(canvasText.gameObject);
                                if (!string.IsNullOrEmpty(termsText))
                                    AccessibilityManager.Announce(termsText);
                                else
                                    AccessibilityManager.Announce("No terms text found");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            MelonLogger.Warning($"Bootstrap terms read error: {ex.Message}");
                        }
                    }
                }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space))
                {
                    try
                    {
                        if (_bootstrapConsoleButton != null)
                        {
                            _bootstrapConsoleButton.onClick.Invoke();
                            AccessibilityManager.Announce("Accepted");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Bootstrap terms accept error: {ex.Message}");
                    }
                }
            }

            // R key: Read current context (modal, page, or gameplay)
            // Skip if Bootstrap terms already handled R above
            if (Input.GetKeyDown(KeyCode.R) && !(_bootstrapTermsDetected && _bootstrap != null))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null)
                {
                    // In shop phase, read full board status
                    if (Gameplay.GameplayPhaseDetector.IsShopPhase())
                    {
                        try
                        {
                            var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                            var board = hangar?.Overlay?.BoardModel;
                            if (board != null)
                            {
                                AccessibilityManager.Announce(Gameplay.PetStatsReader.ReadBoardStatus(board));
                            }
                            else
                            {
                                AccessibilityManager.ReadCurrentContext();
                            }
                        }
                        catch { AccessibilityManager.ReadCurrentContext(); }
                    }
                    else
                    {
                        AccessibilityManager.ReadCurrentContext();
                    }
                }
            }

            // Space key: Re-read last battle announcement (only during/after battle)
            if (Input.GetKeyDown(KeyCode.Space))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null &&
                    selected?.GetComponent<ButtonBase>() == null)
                {
                    if (Gameplay.GameplayPhaseDetector.IsBattlePhase())
                    {
                        string lastMsg = Gameplay.BattleNarrator.GetLastAnnouncement();
                        if (!string.IsNullOrEmpty(lastMsg))
                        {
                            AccessibilityManager.Announce(lastMsg);
                        }
                    }
                }
            }

            // Battle control keybinds (P=pause/play, G=fast forward, K=skip)
            if (Gameplay.GameplayPhaseDetector.IsBattlePhase())
            {
                if (Input.GetKeyDown(KeyCode.P))
                {
                    try
                    {
                        var uiBattle = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle>();
                        if (uiBattle != null)
                        {
                            if (uiBattle._currentState == Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle.State.BattlePlaying)
                            {
                                uiBattle.Pause();
                                AccessibilityManager.Announce("Battle paused");
                                MelonLogger.Msg("[Battle] Paused via P key");
                            }
                            else if (uiBattle._currentState == Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle.State.BattlePaused)
                            {
                                uiBattle.Play();
                                AccessibilityManager.Announce("Battle resumed");
                                MelonLogger.Msg("[Battle] Resumed via P key");
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Battle pause/play error: {ex.Message}");
                    }
                }
                else if (Input.GetKeyDown(KeyCode.G))
                {
                    try
                    {
                        var uiBattle = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle>();
                        var speedBtn = uiBattle?._UIBattleSpeed?.FastForwardButton?.Button;
                        if (speedBtn != null)
                        {
                            speedBtn.Click();
                            // Read actual state from game after clicking
                            bool isNowFast = Il2CppSpacewood.Unity.MonoBehaviours.Battle.BattleController.IsFastfowarding;
                            _fastForwardActive = isNowFast;
                            string state = isNowFast ? "on" : "off";
                            AccessibilityManager.Announce($"Fast forward {state}");
                            MelonLogger.Msg($"[Battle] Fast forward {state} via G key");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Battle fast forward error: {ex.Message}");
                    }
                }
                else if (Input.GetKeyDown(KeyCode.K))
                {
                    try
                    {
                        var uiBattle = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle>();
                        var skipBtn = uiBattle?._UIBattleSpeed?.SkipButton?.Button;
                        if (skipBtn != null)
                        {
                            skipBtn.Click();
                            AccessibilityManager.Announce("Skipping battle");
                            MelonLogger.Msg("[Battle] Skip via K key");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Battle skip error: {ex.Message}");
                    }
                }
            }

            // F8 key: Dump full scene hierarchy to file (for bug reports)
            if (Input.GetKeyDown(KeyCode.F8))
            {
                SceneHierarchyDumper.DumpAndSave();
            }

            // M key: Toggle mouse tracking mode (announce elements under cursor)
            if (Input.GetKeyDown(KeyCode.M))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null)
                {
                    _mouseTrackingEnabled = !_mouseTrackingEnabled;
                    _lastMouseTrackedObject = null;
                    AccessibilityManager.Announce(_mouseTrackingEnabled ? "Mouse tracking enabled" : "Mouse tracking disabled");
                }
            }

            // N key: Open News page (only from Lobby/main menu)
            if (Input.GetKeyDown(KeyCode.N))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null)
                {
                    try
                    {
                        var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                        if (pageManager?.CurrentPage?.gameObject != null)
                        {
                            string pageName = pageManager.CurrentPage.gameObject.name;
                            // Only allow from Lobby or main menu pages (not during gameplay or other menus)
                            if (pageName.Contains("Lobby") || pageName.Contains("MainMenu") || pageName.Contains("Menu"))
                            {
                                // Find the News page in the page list
                                var pages = pageManager.Pages;
                                if (pages != null)
                                {
                                    for (int i = 0; i < pages.Count; i++)
                                    {
                                        var page = pages[i];
                                        if (page?.gameObject != null && page.gameObject.name == "News")
                                        {
                                            pageManager.Open(page);
                                            MelonLogger.Msg("[Keybind] N pressed â€” opening News page");
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"News keybind error: {ex.Message}");
                    }
                }
            }

            // UI Inspector update (mouse-based element inspection)
            if (UIInspector.IsActive)
            {
                UIInspector.Update();
            }

            // Mouse tracking update (announce elements under cursor when M-toggled)
            if (_mouseTrackingEnabled)
            {
                UpdateMouseTracking();
            }

            // B key: Read bones counter
            if (Input.GetKeyDown(KeyCode.B))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null)
                {
                    ReadBonesCounter();
                }
            }

            // C key: Read turn timer
            if (Input.GetKeyDown(KeyCode.C))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null)
                {
                    ReadTurnTimer();
                }
            }

            // H key: Toggle help overlay
            if (Input.GetKeyDown(KeyCode.H))
            {
                var selected = EventSystem.current?.currentSelectedGameObject;
                if (selected?.GetComponent<InputFieldBase>() == null)
                {
                    if (_helpOverlayActive)
                        CloseHelpOverlay();
                    else
                        OpenHelpOverlay();
                }
            }

            // Replay page navigation (Up/Down/Left/Right/Enter)
            if (_replayPageOpen && HandleReplayNavigation())
            {
                // Input was consumed by replay navigator â€” skip everything else
                return;
            }

            // Escape: context-dependent close/back/menu behavior
            // Skip if help overlay is active â€” it handles its own Escape via the input trap below
            if (Input.GetKeyDown(KeyCode.Escape) && !_helpOverlayActive)
            {
                var currentSelected = EventSystem.current?.currentSelectedGameObject;

                // Check if dropdown is expanded â€” always defer to native dropdown handling
                if (IsAnyDropdownExpanded())
                {
                    LoggerInstance.Msg("Escape: Deferring to native dropdown handler");
                    return;
                }

                // Only defer to native cancel handlers outside gameplay phases
                // During gameplay, we need Escape to open the pause menu
                if (!Gameplay.GameplayPhaseDetector.IsShopPhase() &&
                    !Gameplay.GameplayPhaseDetector.IsBattlePhase() &&
                    HasNativeCancelHandler(currentSelected))
                {
                    LoggerInstance.Msg("Escape: Deferring to native cancel handler");
                    return;
                }

                // Validate exclusive-mode flags against actual UI state
                // Stale flags can block Escape from reaching the pause menu
                ValidateExclusiveModeFlags();

                // Name picker: Escape does nothing â€” user must confirm their name
                if (_namePickerActive)
                {
                    LoggerInstance.Msg("Escape: Blocked during name picker");
                    return;
                }

                // 0.35. Close Tips dialog if active
                if (_tipsActive)
                {
                    try
                    {
                        var tips = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Tips>();
                        if (tips != null) tips.Close();
                    }
                    catch { }
                    LoggerInstance.Msg("Escape: Closed Tips");
                    return;
                }

                // 0.4. Dismiss IconAlert if active
                if (_iconAlertActive)
                {
                    try
                    {
                        var iconAlert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.IconAlert>();
                        if (iconAlert != null) iconAlert.Confirm();
                    }
                    catch { }
                    // Let PollIconAlert handle state cleanup
                    LoggerInstance.Msg("Escape: Dismissed IconAlert");
                    return;
                }

                // 0.45. Cancel Alert2 dialog if active
                if (_alert2Active)
                {
                    try
                    {
                        var alert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Alert2>();
                        if (alert != null) alert.Cancel();
                    }
                    catch { }
                    _alert2Active = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    Gameplay.ShopNavigationManager.ResetTurnEnded();
                    AccessibilityManager.Announce("Cancelled");
                    LoggerInstance.Msg("Escape: Cancelled Alert2");
                    return;
                }

                // 0.5. Cancel confirm popup if active
                if (_confirmPopupActive)
                {
                    _confirmPopupActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    Gameplay.ShopNavigationManager.ResetTurnEnded();
                    DismissDockConfirmButton();
                    AccessibilityManager.Announce("Cancelled");
                    LoggerInstance.Msg("Escape: Cancelled confirm popup");
                    return;
                }

                // 0.6. Dismiss TallyArena (battle result) if active
                if (_tallyArenaActive)
                {
                    try
                    {
                        var tally = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArena>();
                        if (tally != null && tally.Button != null)
                        {
                            tally.HandleSubmit(tally.Button);
                            // No announcement â€” the next screen will announce itself
                        }
                    }
                    catch { }
                    _tallyArenaActive = false;
                    LoggerInstance.Msg("Escape: Dismissed TallyArena");
                    return;
                }

                // 0.61. Dismiss TallyArenaFinale if active
                if (_tallyFinaleActive)
                {
                    try
                    {
                        var finale = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaFinale>();
                        if (finale != null)
                        {
                            if (finale.ClaimButton != null)
                                finale.HandleSubmit(finale.ClaimButton);
                            else if (finale.Button != null)
                                finale.HandleSubmit(finale.Button);
                        }
                    }
                    catch { }
                    _tallyFinaleActive = false;
                    LoggerInstance.Msg("Escape: Dismissed TallyArenaFinale");
                    return;
                }

                // 0.62. Dismiss TallyArenaReward if active
                if (_tallyRewardActive)
                {
                    try
                    {
                        var reward = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaReward>();
                        if (reward != null && reward.Button != null)
                            reward.HandleSubmit(reward.Button);
                    }
                    catch { }
                    _tallyRewardActive = false;
                    LoggerInstance.Msg("Escape: Dismissed TallyArenaReward");
                    return;
                }

                // 0.63. Handle TallyArenaMenu â€” Escape returns to main menu
                if (_tallyMenuActive)
                {
                    try
                    {
                        var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaMenu>();
                        if (menu != null)
                            menu.Return();
                    }
                    catch { }
                    _tallyMenuActive = false;
                    AccessibilityManager.Announce("Return to menu");
                    LoggerInstance.Msg("Escape: TallyArenaMenu â†’ Return to menu");
                    return;
                }

                // 0.65. Close Scoreboard overlay if open
                if (_scoreboardOpen)
                {
                    try
                    {
                        var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                        hangar?.CloseScoreboards();
                    }
                    catch { }
                    _scoreboardOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce("Scoreboard closed");
                    LoggerInstance.Msg("Escape: Closed Scoreboard");
                    return;
                }

                // 0.7. Close DeckViewer overlay if open
                if (_deckViewerOpen)
                {
                    try
                    {
                        var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                        if (hangar?.DeckViewer?.CloseButton != null)
                        {
                            hangar.DeckViewer.CloseButton.Click();
                        }
                    }
                    catch { }
                    _deckViewerOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce("Deck viewer closed");
                    LoggerInstance.Msg("Escape: Closed DeckViewer");
                    return;
                }

                // 0.8. Close LastBattleMenu overlay if open
                if (_lastBattleMenuOpen)
                {
                    try
                    {
                        var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                        hangar?.LastBattleMenu?.Close();
                    }
                    catch { }
                    _lastBattleMenuOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce("Battle menu closed");
                    LoggerInstance.Msg("Escape: Closed LastBattleMenu");
                    return;
                }

                // 1. Close Picker popup if open
                try
                {
                    var picker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.UI.Picker>();
                    if (picker != null && picker.Container != null &&
                        picker.Container.gameObject.activeInHierarchy)
                    {
                        picker.Close();
                        SectionManager.InvalidateCache();
                        AccessibilityManager.Announce("Picker closed");
                        LoggerInstance.Msg("Escape: Closed picker popup");
                        return;
                    }
                }
                catch (System.Exception ex)
                {
                    LoggerInstance.Warning($"Escape picker close error: {ex.Message}");
                }

                // 2. Close active modal
                var modal = AccessibilityManager.GetActiveModal();
                if (modal != null && modal.BackdropButton != null)
                {
                    modal.BackdropButton.onClick.Invoke();
                    AccessibilityManager.PopModal();
                    return;
                }

                // 3. Close sidebar if open
                if (IsSidebarOpen())
                {
                    CloseSidebar("Escape");
                    return;
                }

                // 4. GAMEPLAY: During shop phase â€” exit zone first, then open menu
                if (Gameplay.GameplayPhaseDetector.IsShopPhase())
                {
                    if (Gameplay.ShopNavigationManager.IsActivelyNavigating())
                    {
                        // If in SpellFocus (food targeting) or ArmyMinionFocus, cancel it properly first
                        if (Gameplay.ShopNavigationManager.CancelCurrentAction())
                            return;

                        // Otherwise exit current zone â€” user presses Escape again to open menu
                        Gameplay.ShopNavigationManager.Reset();
                        AccessibilityManager.Announce("Exited zone");
                        LoggerInstance.Msg("Escape: Exited shop zone");
                        return;
                    }

                    // Not in a zone â€” toggle sidebar
                    if (IsSidebarOpen())
                        CloseSidebar("shop phase");
                    else
                        OpenPauseMenu("shop phase");
                    return;
                }

                // 5. GAMEPLAY: During battle phase â€” toggle sidebar/menu
                if (Gameplay.GameplayPhaseDetector.IsBattlePhase())
                {
                    if (IsSidebarOpen())
                        CloseSidebar("battle phase");
                    else
                        OpenPauseMenu("battle phase");
                    return;
                }

                // 6. Non-gameplay: Try Page.Back() on the current page
                try
                {
                    var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                    if (pageManager != null && pageManager.CurrentPage != null && pageManager.CurrentPage.CanBack)
                    {
                        pageManager.CurrentPage.Back();
                        return;
                    }
                }
                catch (System.Exception ex)
                {
                    LoggerInstance.Warning($"Escape back error: {ex.Message}");
                }

                // 7. Nothing to close/back â€” toggle sidebar (fallback)
                if (IsSidebarOpen())
                    CloseSidebar("fallback");
                else
                    OpenPauseMenu("fallback");
            }

            // Process deferred sidebar focus (wait for sidebar UI to fully build)
            if (_pendingSidebarFocusFrames > 0)
            {
                _pendingSidebarFocusFrames--;
                if (_pendingSidebarFocusFrames == 0)
                {
                    SectionManager.InvalidateCache();

                    // Build sidebar navigation items and announce
                    if (_pendingSidebarResumeButton)
                    {
                        _pendingSidebarResumeButton = false;
                        BuildSidebarItems();
                    }
                    else
                    {
                        AccessibilityManager.RequestFocusFirst();
                    }
                }
            }

            // Handle sidebar keyboard navigation when active â€” trap all input
            if (_sidebarActive)
            {
                try
                {
                    HandleSidebarNavigation();
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning($"Sidebar navigation error, releasing focus trap: {ex.Message}");
                    _sidebarActive = false;
                    _sidebarItems.Clear();
                }
                return; // Trap focus: no other input while sidebar is open
            }

            // Handle help overlay input when active â€” trap all input
            if (_helpOverlayActive)
            {
                // Auto-close if phase changed (e.g., battle started while help was open)
                if (Gameplay.GameplayPhaseDetector.CurrentPhase != _helpOpenedInPhase)
                {
                    _helpOverlayActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                }

                if (_helpOverlayActive)
                {
                    HandleHelpOverlayInput();
                    return; // Trap focus: no other input while help overlay is open
                }
            }

            // Process deferred slider value announcements
            if (_pendingSliderAnnounce != null)
            {
                _sliderAnnounceDelay--;
                if (_sliderAnnounceDelay <= 0)
                {
                    try
                    {
                        string newValue = AccessibilityManager.GetControlValue(_pendingSliderAnnounce);
                        if (!string.IsNullOrEmpty(newValue))
                        {
                            AccessibilityManager.Announce(newValue);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Slider announce error: {ex.Message}");
                    }
                    _pendingSliderAnnounce = null;
                }
            }

            // Page transition cooldown â€” prevents Enter key carry-over
            if (_pageTransitionCooldown > 0) _pageTransitionCooldown--;

            // Detect Enter/Space on ButtonBase to track value changes
            if (_pageTransitionCooldown <= 0 && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)))
            {
                var go = EventSystem.current?.currentSelectedGameObject;
                if (go != null)
                {
                    // Skip if in an input field
                    if (go.GetComponent<InputFieldBase>() == null)
                    {
                        var btn = go.GetComponent<ButtonBase>();
                        if (btn != null)
                        {
                            // Check if this is a PackProduct button â€” give pack-specific feedback
                            var packProduct = go.GetComponentInParent<Il2CppSpacewood.Unity.PackProduct>();
                            if (packProduct != null)
                            {
                                string packName = GetPackProductName(packProduct);

                                if (packProduct.IsNewbieLocked)
                                {
                                    // Locked pack â€” let Unity's native Submit handler click
                                    // the button (same as mouse). Don't intercept â€” just ensure
                                    // the button is selected so InputModule's Submit fires on it.
                                    if (EventSystem.current != null)
                                        EventSystem.current.SetSelectedGameObject(go);
                                }
                                else if (!packProduct.IsOwned)
                                {
                                    string price = "";
                                    try
                                    {
                                        if (packProduct.Price != null && !string.IsNullOrWhiteSpace(packProduct.Price.text))
                                            price = $", {packProduct.Price.text.Trim()}";
                                    }
                                    catch { }
                                    AccessibilityManager.Announce($"{packName}, not owned{price}");
                                }
                                else
                                {
                                    // Pack is owned â€” click will select it, announce after delay
                                    _pendingPackName = packName;
                                    _packCheckDelay = 5;
                                }
                            }
                            else
                            {
                                // Non-pack button â€” use generic value change tracking
                                _preClickValue = AccessibilityManager.GetControlValue(go) ?? "";
                                _pendingValueCheck = go;
                                _valueCheckDelay = 5; // frames
                            }
                        }
                    }
                }
            }

            // Process deferred pack selection announcements
            if (_pendingPackName != null)
            {
                _packCheckDelay--;
                if (_packCheckDelay <= 0)
                {
                    AccessibilityManager.Announce($"{_pendingPackName} selected");
                    _pendingPackName = null;
                }
            }

            // Process deferred button value change announcements
            if (_pendingValueCheck != null)
            {
                _valueCheckDelay--;
                if (_valueCheckDelay <= 0)
                {
                    try
                    {
                        string newValue = AccessibilityManager.GetControlValue(_pendingValueCheck) ?? "";
                        if (newValue != _preClickValue && !string.IsNullOrEmpty(newValue))
                        {
                            AccessibilityManager.Announce(newValue);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Button value announce error: {ex.Message}");
                    }
                    _pendingValueCheck = null;
                }
            }

            // Detect when Picker popup opens â€” announce title and focus first option
            try
            {
                bool pickerOpen = false;
                var picker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.UI.Picker>();
                if (picker != null && picker.Container != null &&
                    picker.Container.gameObject.activeInHierarchy)
                {
                    pickerOpen = true;
                }

                if (pickerOpen && !_lastPickerOpen)
                {
                    // Picker just opened â€” announce title and focus first option
                    SectionManager.InvalidateCache();
                    string title = "";
                    try
                    {
                        if (picker.Title != null && !string.IsNullOrWhiteSpace(picker.Title.text))
                            title = picker.Title.text.Trim();
                    }
                    catch { }
                    string announcement = string.IsNullOrEmpty(title) ? "Picker" : title;
                    AccessibilityManager.Announce(announcement);
                    AccessibilityManager.RequestFocusFirst();
                    LoggerInstance.Msg($"Picker opened: {announcement}");
                }
                else if (!pickerOpen && _lastPickerOpen)
                {
                    // Picker just closed â€” invalidate cache
                    SectionManager.InvalidateCache();
                }
                _lastPickerOpen = pickerOpen;
            }
            catch { }

            // Poll News dialog overlay
            PollNewsDialog();

            // Poll DeckNamer popup (prefix/suffix team name editor)
            PollDeckNamer();

            // Poll Dock name picker (adjective/noun team name selector)
            PollDockNamePicker();

            // Poll Tips dialog (multi-page tutorial/help)
            PollTips();

            // Poll Tutorial prompt
            PollTutorialPrompt();

            // Poll IconAlert popups (turn milestones, tier unlocks, life gain/loss)
            PollIconAlert();

            // Poll end-turn confirmation popup
            PollEndTurnConfirm();

            // Poll Alert2 confirmation dialogs (food replacement, etc.)
            PollAlert2();

            // Poll TallyArena (battle result screen)
            PollTallyArena();

            // Poll post-arena screens (end-of-run summary, rewards, menu)
            PollTallyArenaFinale();
            PollTallyArenaReward();
            PollTallyArenaMenu();

            // Poll Daily-mode (BullyRush) moustache-score screen
            PollTallyBullyScore();

            // Poll tier upgrade overlay
            PollTierOverlay();

            // Poll LastBattleMenu overlay
            PollLastBattleMenu();

            // Poll DeckViewer overlay
            PollDeckViewer();

            // Poll Scoreboard overlay
            PollScoreboard();

            // Poll VersusLobby for player joins/leaves
            PollVersusLobby();

            // Poll for food targeting state changes (SpellFocus)
            if (Gameplay.GameplayPhaseDetector.IsShopPhase())
            {
                var hangarForSpellFocus = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangarForSpellFocus != null)
                    Gameplay.ShopNavigationManager.PollSpellFocusState(hangarForSpellFocus);
            }

            // Process VS screen announcement (delayed after battle start)
            Gameplay.GameplayPhaseDetector.ProcessVsPending();

            // Handle Tips dialog input â€” exclusive when active
            if (_tipsActive)
            {
                try
                {
                    var tips = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Tips>();
                    if (tips != null)
                    {
                        if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.Return) ||
                            Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space))
                        {
                            tips.Next();
                            // Page change will be detected and announced by PollTips on next frame
                        }
                        else if (Input.GetKeyDown(KeyCode.LeftArrow))
                        {
                            tips.Prev();
                        }
                        else if (Input.GetKeyDown(KeyCode.Escape))
                        {
                            tips.Close();
                        }
                        else if (Input.GetKeyDown(KeyCode.R))
                        {
                            // Re-read current page
                            string pageText = ReadTipsPage(tips);
                            if (!string.IsNullOrEmpty(pageText))
                                AccessibilityManager.Announce(pageText);
                        }
                    }
                }
                catch { }
                return;
            }

            // Handle tier overlay input â€” dismiss with Enter/Space/Escape
            if (_tierOverlayActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape))
                {
                    try
                    {
                        var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                        if (hangar?.Overlay != null)
                        {
                            hangar.Overlay.CloseTooltip(true);
                            MelonLogger.Msg("Tier overlay dismissed via keyboard");
                        }
                    }
                    catch { }
                    _tierOverlayActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    AccessibilityManager.Announce("Dismissed");
                }
                return;
            }

            // Handle name picker input â€” exclusive when active (skips all other input)
            if (_namePickerActive)
            {
                HandleNamePickerInput();
                return;
            }

            // Handle IconAlert input â€” dismiss with Enter/Space/Escape, R to re-read
            if (_iconAlertActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape))
                {
                    try
                    {
                        var iconAlert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.IconAlert>();
                        if (iconAlert != null) iconAlert.Confirm();
                    }
                    catch { }
                    // Don't clear _iconAlertActive here â€” let PollIconAlert detect close
                    // so that if another IconAlert appears immediately, it's caught
                }
                else if (Input.GetKeyDown(KeyCode.R))
                {
                    if (!string.IsNullOrEmpty(_lastIconAlertText))
                        AccessibilityManager.Announce(_lastIconAlertText);
                }
                return;
            }

            // Handle confirm popup input â€” navigable with arrow/tab between Confirm and Cancel
            if (_confirmPopupActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (_confirmFocusOnConfirm)
                    {
                        // Confirm action
                        try
                        {
                            var dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
                            if (dock?.ConfirmButton != null)
                            {
                                dock.HandleConfirm(dock.ConfirmButton);
                                AccessibilityManager.Announce("Turn ended");
                            }
                        }
                        catch { }
                        _confirmPopupActive = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    }
                    else
                    {
                        // Cancel action (focused on Cancel button)
                        _confirmPopupActive = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        Gameplay.ShopNavigationManager.ResetTurnEnded();
                        DismissDockConfirmButton();
                        AccessibilityManager.Announce("Cancelled");
                    }
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    // Escape always cancels regardless of focus
                    _confirmPopupActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    Gameplay.ShopNavigationManager.ResetTurnEnded();
                    DismissDockConfirmButton();
                    AccessibilityManager.Announce("Cancelled");
                }
                else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow) ||
                         Input.GetKeyDown(KeyCode.Tab))
                {
                    // Navigate between Confirm and Cancel
                    _confirmFocusOnConfirm = !_confirmFocusOnConfirm;
                    string focusLabel = _confirmFocusOnConfirm ? "Confirm" : "Cancel";
                    AccessibilityManager.Announce(focusLabel);
                }
                return;
            }

            // Handle TallyArena (battle result) input â€” exclusive when active
            if (_tallyArenaActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape))
                {
                    try
                    {
                        var tally = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArena>();
                        if (tally != null && tally.Button != null)
                        {
                            tally.HandleSubmit(tally.Button);
                            // No announcement â€” the next screen (Finale/Reward/Menu/Shop) will announce itself
                        }
                    }
                    catch { }
                    _tallyArenaActive = false;
                }
                else if (Input.GetKeyDown(KeyCode.R))
                {
                    // Re-read the result
                    try
                    {
                        var tally = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArena>();
                        if (tally != null)
                        {
                            string resultText = ReadTallyArenaDetails(tally);
                            if (!string.IsNullOrEmpty(resultText))
                                AccessibilityManager.Announce(resultText);
                        }
                    }
                    catch { }
                }
                return;
            }

            // Handle TallyArenaFinale (end-of-run summary) input â€” exclusive when active
            if (_tallyFinaleActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space))
                {
                    try
                    {
                        var finale = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaFinale>();
                        if (finale != null)
                        {
                            // Click the claim button or use HandleSubmit
                            if (finale.ClaimButton != null)
                                finale.HandleSubmit(finale.ClaimButton);
                            else if (finale.Button != null)
                                finale.HandleSubmit(finale.Button);
                        }
                    }
                    catch { }
                    _tallyFinaleActive = false;
                }
                else if (Input.GetKeyDown(KeyCode.R))
                {
                    // Re-read summary
                    try
                    {
                        var finale = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaFinale>();
                        string text = ReadTallyArenaFinaleDetails(finale);
                        if (!string.IsNullOrEmpty(text))
                            AccessibilityManager.Announce(text);
                    }
                    catch { }
                }
                return;
            }

            // Handle TallyArenaReward (unlocked item) input â€” exclusive when active
            if (_tallyRewardActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space))
                {
                    try
                    {
                        var reward = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaReward>();
                        if (reward != null && reward.Button != null)
                            reward.HandleSubmit(reward.Button);
                    }
                    catch { }
                    _tallyRewardActive = false;
                }
                else if (Input.GetKeyDown(KeyCode.R))
                {
                    // Re-read reward
                    try
                    {
                        var reward = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaReward>();
                        string text = ReadTallyArenaRewardDetails(reward);
                        if (!string.IsNullOrEmpty(text))
                            AccessibilityManager.Announce(text);
                    }
                    catch { }
                }
                return;
            }

            // Handle TallyArenaMenu (play again / return to menu) input â€” exclusive when active
            if (_tallyMenuActive)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    // Activate focused button
                    if (_tallyMenuButtons.Count > 0 && _tallyMenuFocusIndex < _tallyMenuButtons.Count)
                    {
                        var (label, action) = _tallyMenuButtons[_tallyMenuFocusIndex];
                        try { action?.Invoke(); } catch { }
                        AccessibilityManager.Announce(label);
                        _tallyMenuActive = false;
                    }
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    // Escape â†’ return to menu
                    try
                    {
                        var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaMenu>();
                        if (menu != null)
                            menu.Return();
                    }
                    catch { }
                    AccessibilityManager.Announce("Return to menu");
                    _tallyMenuActive = false;
                }
                else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.Tab))
                {
                    // Navigate down
                    if (_tallyMenuButtons.Count > 0)
                    {
                        _tallyMenuFocusIndex = (_tallyMenuFocusIndex + 1) % _tallyMenuButtons.Count;
                        AccessibilityManager.Announce($"{_tallyMenuButtons[_tallyMenuFocusIndex].label}, {_tallyMenuFocusIndex + 1} of {_tallyMenuButtons.Count}");
                    }
                }
                else if (Input.GetKeyDown(KeyCode.UpArrow))
                {
                    // Navigate up
                    if (_tallyMenuButtons.Count > 0)
                    {
                        _tallyMenuFocusIndex = (_tallyMenuFocusIndex - 1 + _tallyMenuButtons.Count) % _tallyMenuButtons.Count;
                        AccessibilityManager.Announce($"{_tallyMenuButtons[_tallyMenuFocusIndex].label}, {_tallyMenuFocusIndex + 1} of {_tallyMenuButtons.Count}");
                    }
                }
                return;
            }

            // Handle Alert2 popup input â€” navigable with arrow/tab, Enter activates selected button
            if (_alert2Active)
            {
                // Cooldown after dialog opens â€” prevents Enter carry-over from the button that opened it
                if (_alert2InputCooldown > 0)
                {
                    _alert2InputCooldown--;
                    // Suppress Unity's InputModule Submit by deselecting while Enter is held
                    if (Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter))
                    {
                        if (EventSystem.current != null)
                            EventSystem.current.SetSelectedGameObject(null);
                        _alert2InputCooldown = 2; // extend while still held
                    }
                    return; // consume all input during cooldown
                }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    try
                    {
                        var alert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Alert2>();
                        if (alert != null)
                        {
                            // Check which button is currently selected
                            var currentGo = EventSystem.current?.currentSelectedGameObject;
                            bool onCancel = currentGo != null && currentGo == alert.CancelButton?.gameObject;
                            if (onCancel)
                            {
                                alert.Cancel();
                                Gameplay.ShopNavigationManager.ResetTurnEnded();
                                AccessibilityManager.Announce("Cancelled");
                            }
                            else
                            {
                                alert.Confirm();
                                // No extra announcement â€” the game's response speaks for itself
                            }
                        }
                    }
                    catch { }
                    _alert2Active = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    try
                    {
                        var alert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Alert2>();
                        if (alert != null) alert.Cancel();
                    }
                    catch { }
                    _alert2Active = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    Gameplay.ShopNavigationManager.ResetTurnEnded();
                    AccessibilityManager.Announce("Cancelled");
                }
                else if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow))
                {
                    // Navigate between cancel and confirm buttons with position
                    try
                    {
                        var alert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Alert2>();
                        if (alert != null && EventSystem.current != null)
                        {
                            var current = EventSystem.current.currentSelectedGameObject;
                            bool onCancel = current == alert.CancelButton?.gameObject;
                            if (onCancel && alert.ConfirmButton?.gameObject != null)
                            {
                                EventSystem.current.SetSelectedGameObject(alert.ConfirmButton.gameObject);
                                AccessibilityManager.Announce("Confirm, button, 2 of 2");
                            }
                            else if (alert.CancelButton?.gameObject != null)
                            {
                                EventSystem.current.SetSelectedGameObject(alert.CancelButton.gameObject);
                                AccessibilityManager.Announce("Cancel, button, 1 of 2");
                            }
                        }
                    }
                    catch { }
                }
                return;
            }

            // Handle Scoreboard input â€” custom navigation with Left/Right for opponents, Up/Down for detail lines
            if (_scoreboardOpen)
            {
                // Skip O-key close on the same frame the scoreboard opened
                if (_scoreboardJustOpened)
                {
                    _scoreboardJustOpened = false;
                }
                else if (Input.GetKeyDown(KeyCode.O))
                {
                    // O key closes scoreboard (toggle)
                    try
                    {
                        var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                        hangar?.CloseScoreboards();
                    }
                    catch { }
                    _scoreboardOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce("Scoreboard closed");
                    LoggerInstance.Msg("O key: Closed Scoreboard");
                    return;
                }

                // Left/Right: browse between opponents
                if (Input.GetKeyDown(KeyCode.LeftArrow))  { MoveScoreboardOpponent(-1); return; }
                if (Input.GetKeyDown(KeyCode.RightArrow)) { MoveScoreboardOpponent(1); return; }

                // Up/Down: browse detail lines for current opponent
                if (Input.GetKeyDown(KeyCode.UpArrow))    { MoveScoreboardDetailLine(-1); return; }
                if (Input.GetKeyDown(KeyCode.DownArrow))  { MoveScoreboardDetailLine(1); return; }

                // Space: re-read current detail line
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    if (_scoreboardDetailLines.Count > 0 && _scoreboardDetailLineIndex < _scoreboardDetailLines.Count)
                        AccessibilityManager.Announce(_scoreboardDetailLines[_scoreboardDetailLineIndex]);
                    return;
                }

                // Consume all other non-escape input to prevent fallthrough
                if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.PageUp) ||
                    Input.GetKeyDown(KeyCode.PageDown))
                    return;
            }

            // Handle deferred news enrichment (wait for codec to populate after Tab to a new item)
            if (_newsEnrichPending > 0)
            {
                _newsEnrichPending--;
                if (_newsEnrichPending == 0)
                {
                    try
                    {
                        var news3 = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                        var ann3 = news3?.Announcement;
                        var codec3 = ann3?.Codec;
                        var cg3 = ann3?.CodecGroup;
                        if (codec3 != null && cg3 != null && cg3.alpha > 0)
                        {
                            BuildNewsCodecDetailLines(codec3);
                            if (_newsCodecDetailLines.Count > 0)
                            {
                                // Build enriched announcement: base + tier + description + index
                                string stripped = _newsEnrichBaseAnnouncement;
                                if (stripped.EndsWith(", button"))
                                    stripped = stripped.Substring(0, stripped.Length - ", button".Length);
                                var parts = new List<string> { stripped };

                                try
                                {
                                    var tier = codec3.Tier;
                                    if (tier != null && tier.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(tier.text))
                                    {
                                        string t = TextExtractor.CleanTextPublic(tier.text);
                                        if (!stripped.Contains(t)) parts.Add(t);
                                    }
                                }
                                catch { }

                                try
                                {
                                    var body = codec3.Body;
                                    if (body != null && !string.IsNullOrWhiteSpace(body.text))
                                    {
                                        var split = Regex.Split(body.text, @"<sprite name=""Level\d+"">");
                                        string pre = TextExtractor.CleanTextPublic(split[0]);
                                        if (!string.IsNullOrWhiteSpace(pre))
                                        {
                                            if (pre.Length > 80) pre = pre.Substring(0, 77) + "...";
                                            parts.Add(pre);
                                        }
                                    }
                                }
                                catch { }

                                parts.Add($"1 of {_newsCodecDetailLines.Count}");
                                string enriched = string.Join(", ", parts);
                                AccessibilityManager.Announce(enriched);
                                MelonLogger.Msg($"[News Enrich] {enriched}");
                            }
                        }
                    }
                    catch { }
                    _newsEnrichBaseAnnouncement = "";
                }
            }

            // Handle deferred page flip rebuild (wait 2 frames for codec to update)
            if (_newsPageFlipPending > 0)
            {
                _newsPageFlipPending--;
                if (_newsPageFlipPending == 0)
                {
                    try
                    {
                        var news2 = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                        var ann2 = news2?.Announcement;
                        var codec2 = ann2?.Codec;
                        if (codec2 != null)
                        {
                            _lastNewsCodecHeader = ""; // invalidate cache
                            BuildNewsCodecDetailLines(codec2);
                            if (_newsPageFlipDirection > 0)
                                _newsCodecDetailLineIndex = 0; // forward: start at first line
                            else
                                _newsCodecDetailLineIndex = System.Math.Max(0, _newsCodecDetailLines.Count - 1); // backward: last line
                            AnnounceNewsCodecLine();
                        }
                    }
                    catch { }
                }
            }

            // Handle News detail line navigation â€” Left/Right arrows browse codec details or change lines
            if (_lastNewsOpen)
            {
                bool leftArrow = Input.GetKeyDown(KeyCode.LeftArrow);
                bool rightArrow = Input.GetKeyDown(KeyCode.RightArrow);
                bool space = Input.GetKeyDown(KeyCode.Space);

                if (leftArrow || rightArrow || space)
                {
                    // Check if we're in the "Changes" section â€” navigate changelog lines
                    try
                    {
                        var currentSection = SectionManager.GetCurrentSection();
                        if (currentSection != null && currentSection.Name == "Changes" && !string.IsNullOrEmpty(currentSection.Tag))
                        {
                            // Build change lines on demand
                            if (_newsCodecDetailLines.Count == 0 || _lastNewsCodecHeader != "__changes__")
                            {
                                _newsCodecDetailLines.Clear();
                                _newsCodecDetailLineIndex = 0;
                                _lastNewsCodecHeader = "__changes__";
                                foreach (var line in currentSection.Tag.Split('\n'))
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                        _newsCodecDetailLines.Add(line.Trim());
                                }
                            }

                            if (_newsCodecDetailLines.Count > 0)
                            {
                                if (leftArrow) { MoveNewsCodecDetailLine(-1); return; }
                                if (rightArrow) { MoveNewsCodecDetailLine(1); return; }
                                if (space) { AnnounceNewsCodecLine(); return; }
                            }
                        }
                    }
                    catch { }

                    // Otherwise, navigate codec details for the focused pinboard item
                    try
                    {
                        var news = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                        var announcement = news?.Announcement;
                        var codec = announcement?.Codec;
                        var codecGroup = announcement?.CodecGroup;
                        if (codec != null && codecGroup != null && codecGroup.alpha > 0)
                        {
                            // Check if we need to (re)build detail lines for this item
                            string currentHeader = "";
                            int currentPage = 0;
                            try { currentHeader = codec.Header?.text ?? ""; } catch { }
                            try { currentPage = codec.CurrentPage; } catch { }
                            string cacheKey = $"{currentHeader}__p{currentPage}";
                            if (cacheKey != _lastNewsCodecHeader || _newsCodecDetailLines.Count == 0)
                            {
                                BuildNewsCodecDetailLines(codec);
                            }

                            if (_newsCodecDetailLines.Count > 0)
                            {
                                if (rightArrow)
                                {
                                    // If at last detail line and multi-page, flip to next page
                                    if (_newsCodecDetailLineIndex >= _newsCodecDetailLines.Count - 1 && codec.Pages > 1)
                                    {
                                        FlipNewsCodecPage(codec);
                                        _newsPageFlipPending = 5; // wait 5 frames for codec to update
                                        _newsPageFlipDirection = 1;
                                    }
                                    else
                                    {
                                        MoveNewsCodecDetailLine(1);
                                    }
                                    return;
                                }
                                if (leftArrow)
                                {
                                    // If at first detail line and multi-page, flip to previous page
                                    if (_newsCodecDetailLineIndex <= 0 && codec.Pages > 1)
                                    {
                                        // Go backward: flip forward Pages-1 times to wrap around
                                        int flips = codec.Pages - 1;
                                        for (int i = 0; i < flips; i++)
                                            FlipNewsCodecPage(codec);
                                        _newsPageFlipPending = 5;
                                        _newsPageFlipDirection = -1;
                                    }
                                    else
                                    {
                                        MoveNewsCodecDetailLine(-1);
                                    }
                                    return;
                                }
                                if (space) { AnnounceNewsCodecLine(); return; }
                            }
                        }
                    }
                    catch { }
                }
            }

            HandleNavigation();

            // Clear Unity EventSystem selection at end of frame during shop phase.
            // This prevents Unity's native Submit handler (Enter/Space) from clicking
            // whatever button the game happens to have focused. Our mod handles all
            // input through its own keybinds and SectionManager navigation.
            if (Gameplay.GameplayPhaseDetector.IsShopPhase() && EventSystem.current != null)
            {
                var selected = EventSystem.current.currentSelectedGameObject;
                if (selected != null)
                {
                    // Don't clear if a text input field is focused
                    if (selected.GetComponent<InputFieldBase>() == null)
                    {
                        EventSystem.current.SetSelectedGameObject(null);
                    }
                }
            }
        }

        /// <summary>
        /// Mouse tracking mode â€” when enabled (M key toggle), announces UI elements under the cursor.
        /// Uses EventSystem.RaycastAll to find the topmost interactive element.
        /// </summary>
        private static void UpdateMouseTracking()
        {
            try
            {
                if (EventSystem.current == null) return;

                var pointerData = new PointerEventData(EventSystem.current)
                {
                    position = Input.mousePosition
                };

                var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                EventSystem.current.RaycastAll(pointerData, results);

                GameObject hitObject = null;
                foreach (var result in results)
                {
                    if (result.gameObject == null) continue;
                    // Look for the nearest parent with a ButtonBase or SelectableBase
                    var go = result.gameObject;
                    var btn = go.GetComponentInParent<ButtonBase>();
                    if (btn != null)
                    {
                        hitObject = btn.gameObject;
                        break;
                    }
                    var selectable = go.GetComponentInParent<SelectableBase>();
                    if (selectable != null)
                    {
                        hitObject = selectable.gameObject;
                        break;
                    }
                }

                if (hitObject != null && hitObject != _lastMouseTrackedObject)
                {
                    _lastMouseTrackedObject = hitObject;
                    AccessibilityManager.AnnounceSelectedElement(hitObject, true);
                }
                else if (hitObject == null)
                {
                    _lastMouseTrackedObject = null;
                }
            }
            catch { }
        }

        private static void PollBootstrapText()
        {
            // Try to find Bootstrap once; it only exists during the loading screen scene
            if (_bootstrap == null)
            {
                _bootstrap = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Bootstrap>();
                if (_bootstrap == null) return;
            }

            // Check if Bootstrap is still alive (destroyed when scene changes)
            try { var _ = _bootstrap.gameObject; }
            catch { _bootstrap = null; return; }

            // Read the Text component and announce if it changed
            try
            {
                var textComponent = _bootstrap.Text;
                if (textComponent == null) return;
                string current = textComponent.text;
                if (!string.IsNullOrEmpty(current) && current != _lastBootstrapText)
                {
                    _lastBootstrapText = current;
                    AccessibilityManager.Announce(current);
                }
            }
            catch
            {
                // Text component failed â€” check if Bootstrap itself is still alive
                // (don't null _bootstrap here; the terms page may still be showing)
                try { var _ = _bootstrap.gameObject; }
                catch
                {
                    _bootstrap = null;
                    _bootstrapTermsDetected = false;
                    _bootstrapConsoleButton = null;
                    _lastLanguagePickerOpen = false;
                }
            }
        }

        /// <summary>
        /// Polls for Bootstrap UI elements: terms/policy page and language picker.
        /// These exist in the Bootstrap scene which has no PageManager.
        /// </summary>
        private static void PollBootstrapUI()
        {
            if (_bootstrap == null) return;

            // Check if Bootstrap is still alive
            try { var _ = _bootstrap.gameObject; }
            catch { _bootstrap = null; _bootstrapTermsDetected = false; _bootstrapConsoleButton = null; _lastLanguagePickerOpen = false; return; }

            // --- Language Picker detection ---
            try
            {
                bool langPickerOpen = false;
                var langPicker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.LanguagePicker>();
                if (langPicker != null)
                {
                    var canvas = langPicker.GetComponentInChildren<Canvas>();
                    if (canvas != null && canvas.gameObject.activeInHierarchy)
                        langPickerOpen = true;
                }

                if (langPickerOpen && !_lastLanguagePickerOpen)
                {
                    // Language picker just opened
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce("Language picker. Use Up and Down arrows to choose a language, then press Enter to confirm.");
                    AccessibilityManager.RequestFocusFirst();
                }
                else if (!langPickerOpen && _lastLanguagePickerOpen)
                {
                    // Language picker just closed
                    SectionManager.InvalidateCache();
                }
                _lastLanguagePickerOpen = langPickerOpen;
            }
            catch { }

            // --- Terms/Policy page detection ---
            // The terms/privacy buttons (Accept, Privacy policy, Terms of service) are ButtonBase
            // elements that appear in the Bootstrap scene but may NOT be children of CanvasText.
            // Scan all active ButtonBase in the scene â€” during Bootstrap there's no PageManager,
            // so any ButtonBase found must be part of the terms/consent UI.
            try
            {
                bool termsPageVisible = false;

                if (!_lastLanguagePickerOpen) // Don't confuse language picker buttons with terms
                {
                    var allButtons = UnityEngine.Object.FindObjectsOfType<ButtonBase>();
                    if (allButtons != null)
                    {
                        foreach (var btn in allButtons)
                        {
                            if (btn != null && btn.gameObject != null && btn.gameObject.activeInHierarchy
                                && btn.GetInteractable())
                            {
                                termsPageVisible = true;
                                break;
                            }
                        }
                    }
                }

                if (termsPageVisible && !_bootstrapTermsDetected)
                {
                    _bootstrapTermsDetected = true;

                    // Read all visible text from the entire Bootstrap scene
                    // (terms text may be in CanvasText or elsewhere)
                    var parts = new System.Collections.Generic.List<string>();
                    try
                    {
                        var allTmp = UnityEngine.Object.FindObjectsOfType<Il2CppTMPro.TextMeshProUGUI>();
                        foreach (var tmp in allTmp)
                        {
                            if (tmp == null || !tmp.gameObject.activeInHierarchy) continue;
                            string text = tmp.text?.Trim();
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            // Skip "Cmd..." loading indicator and very short text
                            if (text.StartsWith("Cmd")) continue;
                            if (text.Length <= 2) continue;
                            if (!parts.Contains(text))
                                parts.Add(text);
                        }
                    }
                    catch { }

                    string allText = parts.Count > 0 ? string.Join(". ", parts) : "";

                    // Cache the ConsoleButton (some versions use Unity Button for Accept)
                    try
                    {
                        var canvasText = _bootstrap.transform.Find("CanvasText");
                        if (canvasText != null)
                        {
                            var consoleBtnTransform = canvasText.Find("NotchPadding/ConsoleButton");
                            if (consoleBtnTransform != null)
                                _bootstrapConsoleButton = consoleBtnTransform.GetComponent<UnityEngine.UI.Button>();
                        }
                    }
                    catch { }

                    string announcement = !string.IsNullOrWhiteSpace(allText)
                        ? $"{allText}. Use Tab to navigate. Press Enter to accept."
                        : "Privacy policy and terms of service. Use Tab to navigate. Press Enter to accept.";
                    AccessibilityManager.Announce(announcement);
                    MelonLogger.Msg($"[Bootstrap] Terms/policy page detected. Text: {allText}");

                    // Focus the first element so navigation starts
                    SectionManager.InvalidateCache();
                    AccessibilityManager.RequestFocusFirst();
                }
                else if (!termsPageVisible && _bootstrapTermsDetected)
                {
                    // Terms page was dismissed â€” announce and reset state
                    AccessibilityManager.Announce("Accepted");
                    _bootstrapTermsDetected = false;
                    _bootstrapConsoleButton = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Polls for the News/Announcement dialog overlay.
        /// News is a standalone component under Menu, not managed by PageManager.
        /// Visibility is controlled by CanvasGroup alpha on the Announcement's Canvas.
        /// </summary>
        private static void PollNewsDialog()
        {
            try
            {
                bool isOpen = false;
                try
                {
                    var news = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                    if (news != null)
                    {
                        // Check CanvasGroup alpha on the Announcement's Canvas to determine visibility
                        var canvasGroup = news.GetComponentInChildren<UnityEngine.CanvasGroup>();
                        if (canvasGroup != null && canvasGroup.alpha > 0 && canvasGroup.gameObject.activeInHierarchy)
                            isOpen = true;
                    }
                }
                catch { }

                if (isOpen && !_lastNewsOpen)
                {
                    // News dialog just opened â€” announce structured summary and enable navigation
                    SectionManager.InvalidateCache();

                    // Build a concise summary from Announcement title only
                    // (changelog text is in its own "Changes" section now)
                    string summary = "";
                    try
                    {
                        var news = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                        if (news != null)
                        {
                            var announcement = news.Announcement;
                            if (announcement != null)
                            {
                                try
                                {
                                    var title = announcement.GenericTitleMesh;
                                    if (title != null && !string.IsNullOrWhiteSpace(title.text))
                                        summary = TextExtractor.CleanTextPublic(title.text);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(summary))
                        AccessibilityManager.Announce(summary + ". Use left and right arrows on items to read details.");
                    else
                        AccessibilityManager.Announce("News. Use left and right arrows on items to read details.");

                    _newsCodecDetailLines.Clear();
                    _newsCodecDetailLineIndex = 0;
                    _lastNewsCodecHeader = "";

                    AccessibilityManager.RequestFocusFirst();
                    MelonLogger.Msg("News dialog opened");
                }
                else if (!isOpen && _lastNewsOpen)
                {
                    // News dialog closed â€” clean up
                    _newsCodecDetailLines.Clear();
                    _newsCodecDetailLineIndex = 0;
                    _lastNewsCodecHeader = "";
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("News dialog closed");
                }
                _lastNewsOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Builds detail lines from the News codec popup for navigation with Left/Right arrows.
        /// Line 0 = summary (name, tier, stats). Lines 1+ = individual level abilities.
        /// Called when a news item gets focus (tab navigation selects it and codec becomes visible).
        /// </summary>
        internal static void BuildNewsCodecDetailLinesPublic(Il2CppSpacewood.Scripts.Codec.CodecEntry codec)
            => BuildNewsCodecDetailLines(codec);

        /// <summary>
        /// Flips the codec page by re-opening with flipPage=true using the stored sprite name.
        /// codec.UpdatePage(true) doesn't work after programmatic Open(), so we re-Open instead.
        /// </summary>
        private static void FlipNewsCodecPage(Il2CppSpacewood.Scripts.Codec.CodecEntry codec)
        {
            string sprite = _lastNewsCodecSpriteName;
            if (string.IsNullOrEmpty(sprite))
            {
                // Fallback to UpdatePage if we don't have a sprite name
                codec.UpdatePage(true);
                return;
            }

            try
            {
                // Try MinionEnum
                if (System.Enum.TryParse<Il2CppSpacewood.Core.Enums.MinionEnum>(sprite, true, out var minionEnum))
                {
                    var templates = Il2CppSpacewood.Core.Enums.MinionConstants.Minions;
                    if (templates != null && templates.ContainsKey(minionEnum))
                    {
                        codec.Open(templates[minionEnum], true, true); // flipPage=true
                        return;
                    }
                }
                // Try SpellEnum
                if (System.Enum.TryParse<Il2CppSpacewood.Core.Enums.SpellEnum>(sprite, true, out var spellEnum))
                {
                    var spells = Il2CppSpacewood.Core.Enums.SpellConstants.Spells;
                    if (spells != null && spells.ContainsKey(spellEnum))
                    {
                        codec.Open(spells[spellEnum], true, true);
                        return;
                    }
                }
                // Try Perk
                if (System.Enum.TryParse<Il2CppSpacewood.Core.Enums.Perk>(sprite, true, out var perkEnum))
                {
                    var perks = Il2CppSpacewood.Core.Enums.PerkConstants.Perks;
                    if (perks != null && perks.ContainsKey(perkEnum))
                    {
                        codec.Open(perks[perkEnum], true, true);
                        return;
                    }
                }
            }
            catch { }

            // Fallback
            codec.UpdatePage(true);
        }

        /// <summary>
        /// Called by AccessibilityManager to trigger deferred codec enrichment
        /// after a Tab to a news item. The codec won't be populated for a few frames.
        /// </summary>
        internal static void RequestNewsEnrichment(string baseAnnouncement)
        {
            _newsEnrichPending = 5; // wait 5 frames for game to populate codec
            _newsEnrichBaseAnnouncement = baseAnnouncement;
        }

        private static void BuildNewsCodecDetailLines(Il2CppSpacewood.Scripts.Codec.CodecEntry codec)
        {
            _newsCodecDetailLines.Clear();
            _newsCodecDetailLineIndex = 0;

            try
            {
                string name = "";
                string tierText = "";
                string statsText = "";
                string pageText = "";

                // Name
                try
                {
                    var header = codec.Header;
                    if (header != null && !string.IsNullOrWhiteSpace(header.text))
                        name = TextExtractor.CleanTextPublic(header.text);
                }
                catch { }

                int currentPage = 0;
                try { currentPage = codec.CurrentPage; } catch { }
                // Set cache key after reading both name and page (avoids empty key on partial failure)
                _lastNewsCodecHeader = $"{name}__p{currentPage}";

                // Tier
                try
                {
                    var tier = codec.Tier;
                    if (tier != null && tier.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(tier.text))
                        tierText = TextExtractor.CleanTextPublic(tier.text);
                }
                catch { }

                // Stats (attack/health)
                try
                {
                    var atk = codec.StatsAttack;
                    var hp = codec.StatsHealth;
                    if (atk != null && hp != null &&
                        atk.gameObject.activeInHierarchy && hp.gameObject.activeInHierarchy &&
                        !string.IsNullOrWhiteSpace(atk.text) && !string.IsNullOrWhiteSpace(hp.text))
                    {
                        statsText = $"{atk.text} attack, {hp.text} health";
                    }
                }
                catch { }

                // Page indicator
                try
                {
                    if (codec.Pages > 1)
                    {
                        var page = codec.Page;
                        if (page != null && page.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(page.text))
                            pageText = TextExtractor.CleanTextPublic(page.text);
                        else
                            pageText = $"Page {codec.CurrentPage + 1} of {codec.Pages}";
                    }
                }
                catch { }

                // Build summary line (line 0)
                var summaryParts = new List<string>();
                if (!string.IsNullOrEmpty(name)) summaryParts.Add(name);
                if (!string.IsNullOrEmpty(tierText)) summaryParts.Add(tierText);
                if (!string.IsNullOrEmpty(statsText)) summaryParts.Add(statsText);
                if (!string.IsNullOrEmpty(pageText)) summaryParts.Add(pageText);
                _newsCodecDetailLines.Add(string.Join(". ", summaryParts));

                // Parse body text into individual level lines
                try
                {
                    var body = codec.Body;
                    if (body != null && !string.IsNullOrWhiteSpace(body.text))
                    {
                        string rawBody = body.text;

                        // Split on <sprite name="Level1">, <sprite name="Level2">, etc.
                        var levelParts = Regex.Split(rawBody, @"<sprite name=""Level(\d+)"">");

                        // levelParts[0] = text before first Level sprite (usually empty or perk description)
                        // levelParts[1] = "1" (captured group), levelParts[2] = ability text for Lvl 1
                        // levelParts[3] = "2", levelParts[4] = ability text for Lvl 2, etc.

                        // If there was text before the first level marker (e.g. perk/food description)
                        string preLevelText = TextExtractor.CleanTextPublic(levelParts[0]);
                        if (!string.IsNullOrWhiteSpace(preLevelText))
                        {
                            _newsCodecDetailLines.Add(preLevelText);
                        }

                        // Process matched level groups
                        for (int i = 1; i + 1 < levelParts.Length; i += 2)
                        {
                            string levelNum = levelParts[i];
                            string abilityRaw = levelParts[i + 1];
                            string ability = TextExtractor.CleanTextPublic(abilityRaw);
                            if (!string.IsNullOrWhiteSpace(ability))
                            {
                                _newsCodecDetailLines.Add($"Lvl {levelNum}: {ability}");
                            }
                        }

                        // If no level markers were found and no pre-level text was added,
                        // add the whole body as a single line
                        if (levelParts.Length <= 1 && string.IsNullOrWhiteSpace(preLevelText))
                        {
                            string fullBody = TextExtractor.CleanTextPublic(rawBody);
                            if (!string.IsNullOrWhiteSpace(fullBody))
                                _newsCodecDetailLines.Add(fullBody);
                        }
                    }
                }
                catch { }

                MelonLogger.Msg($"[News Codec] Built {_newsCodecDetailLines.Count} detail lines for {name}");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"BuildNewsCodecDetailLines error: {ex.Message}");
            }
        }

        /// <summary>
        /// Moves through news codec detail lines. direction: -1 = left, +1 = right.
        /// </summary>
        private static void MoveNewsCodecDetailLine(int direction)
        {
            if (_newsCodecDetailLines.Count == 0) return;

            int newIndex = _newsCodecDetailLineIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _newsCodecDetailLines.Count) newIndex = _newsCodecDetailLines.Count - 1;

            // If at boundary and trying to go further, re-read current
            _newsCodecDetailLineIndex = newIndex;

            AnnounceNewsCodecLine();
        }

        /// <summary>
        /// Announces the current news codec detail line with its index.
        /// </summary>
        private static void AnnounceNewsCodecLine()
        {
            if (_newsCodecDetailLines.Count == 0) return;
            if (_newsCodecDetailLineIndex < 0 || _newsCodecDetailLineIndex >= _newsCodecDetailLines.Count) return;
            string line = _newsCodecDetailLines[_newsCodecDetailLineIndex];
            string posInfo = $"{_newsCodecDetailLineIndex + 1} of {_newsCodecDetailLines.Count}";
            string announcement = $"{line}, {posInfo}";
            AccessibilityManager.Announce(announcement);
            MelonLogger.Msg($"[News Codec] {announcement}");
        }

        /// <summary>
        /// Polls for the DeckNamer popup (team name prefix/suffix editor).
        /// DeckNamer is a MonoBehaviour, not a Modal or Page, so existing patches don't detect it.
        /// </summary>
        private static void PollDeckNamer()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Scripts.Codec.DeckNamer deckNamer = null;
                try
                {
                    deckNamer = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Scripts.Codec.DeckNamer>();
                    if (deckNamer != null && deckNamer.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastDeckNamerOpen)
                {
                    // DeckNamer just opened â€” announce and focus
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce("Team name editor. Type a name and press Enter to save, or Escape to cancel.");

                    // Focus the name input field
                    try
                    {
                        if (deckNamer.NameField?.gameObject != null && EventSystem.current != null)
                        {
                            EventSystem.current.SetSelectedGameObject(deckNamer.NameField.gameObject);
                        }
                    }
                    catch { }
                }
                else if (!isOpen && _lastDeckNamerOpen)
                {
                    SectionManager.InvalidateCache();
                    AccessibilityManager.RequestFocusFirst();
                }
                _lastDeckNamerOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Polls for the Tips dialog (multi-page tutorial/help shown on first game or from sidebar).
        /// Tips is a MonoBehaviour with a Canvas to check visibility, pages navigated via Next/Prev.
        /// </summary>
        private static void PollTips()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.MonoBehaviours.Build.Tips tips = null;
                try
                {
                    tips = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Tips>();
                    if (tips != null && tips.Canvas != null && tips.Canvas.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastTipsOpen)
                {
                    // Tips just opened
                    _tipsActive = true;
                    AccessibilityManager.CancelAllPending();
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;

                    int pageIndex = 0;
                    try { pageIndex = tips.CurrentIndex; } catch { }
                    _lastTipsPageIndex = pageIndex;

                    string pageText = ReadTipsPage(tips);
                    string announcement = !string.IsNullOrEmpty(pageText)
                        ? $"Tips. {pageText}. Left and Right to navigate pages. Escape to close."
                        : "Tips opened. Left and Right to navigate pages. Escape to close.";
                    AccessibilityManager.Announce(announcement);
                    MelonLogger.Msg($"Tips opened at page {pageIndex}");
                }
                else if (isOpen && _lastTipsOpen)
                {
                    // Tips still open â€” check for page change
                    try
                    {
                        int pageIndex = tips.CurrentIndex;
                        if (pageIndex != _lastTipsPageIndex)
                        {
                            _lastTipsPageIndex = pageIndex;
                            string pageText = ReadTipsPage(tips);
                            if (!string.IsNullOrEmpty(pageText))
                                AccessibilityManager.Announce(pageText);
                            MelonLogger.Msg($"Tips page changed to {pageIndex}");
                        }
                    }
                    catch { }
                }
                else if (!isOpen && _lastTipsOpen)
                {
                    // Tips closed
                    _tipsActive = false;
                    _lastTipsPageIndex = -1;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("Tips closed");
                }
                _lastTipsOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Reads the current Tips page content: counter, page name, and text.
        /// </summary>
        private static string ReadTipsPage(Il2CppSpacewood.Unity.MonoBehaviours.Build.Tips tips)
        {
            if (tips == null) return null;
            try
            {
                var parts = new System.Collections.Generic.List<string>();

                // Counter label (e.g., "1/14")
                try
                {
                    if (tips.Label != null && !string.IsNullOrWhiteSpace(tips.Label.text))
                        parts.Add($"Page {tips.Label.text.Trim()}");
                }
                catch { }

                // Main page text from Tips.Text property
                try
                {
                    if (tips.Text != null && !string.IsNullOrWhiteSpace(tips.Text.text))
                        parts.Add(tips.Text.text.Trim());
                }
                catch { }

                // Read additional text from the active page's children
                // (e.g., cost labels, stat names like "Attack"/"Health")
                try
                {
                    int idx = tips.CurrentIndex;
                    if (tips.Pages != null && idx >= 0 && idx < tips.Pages.Count)
                    {
                        var pageTransform = tips.Pages[idx];
                        if (pageTransform != null)
                        {
                            var childTexts = pageTransform.GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>();
                            if (childTexts != null)
                            {
                                string mainText = tips.Text != null ? tips.Text.text?.Trim() : "";
                                for (int i = 0; i < childTexts.Count; i++)
                                {
                                    try
                                    {
                                        var ct = childTexts[i];
                                        if (ct == null || !ct.gameObject.activeInHierarchy) continue;
                                        string t = ct.text?.Trim();
                                        if (string.IsNullOrWhiteSpace(t)) continue;
                                        // Skip the main text (already added above)
                                        if (t == mainText) continue;
                                        parts.Add(t);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Polls for the HangarTutorialPrompt (tutorial popup during shop phase).
        /// HangarTutorialPrompt is a MonoBehaviour with a CanvasGroup (Box.alpha) to check visibility.
        /// </summary>
        private static void PollTutorialPrompt()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarTutorialPrompt prompt = null;
                try
                {
                    prompt = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.HangarTutorialPrompt>();
                    if (prompt != null && prompt.gameObject.activeInHierarchy &&
                        prompt.Button != null && prompt.Button.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastTutorialPromptOpen)
                {
                    // Tutorial prompt just appeared â€” auto-dismiss it silently
                    try
                    {
                        prompt.Close();
                        MelonLogger.Msg("Tutorial prompt auto-dismissed");
                    }
                    catch { }
                }
                else if (!isOpen && _lastTutorialPromptOpen)
                {
                    SectionManager.InvalidateCache();
                }
                _lastTutorialPromptOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Finds the SideBar component reliably. Menu may be null during shop/battle phase,
        /// so we fall back to FindObjectOfType and then GameObject.Find.
        /// </summary>
        private static Il2CppSpacewood.Unity.SideBar FindSideBar()
        {
            // Per-frame cache to avoid repeated FindObjectOfType calls
            if (_cachedSideBar != null && _sidebarCacheFrame == Time.frameCount)
                return _cachedSideBar;

            _sidebarCacheFrame = Time.frameCount;
            _cachedSideBar = null;

            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Menu>();
                if (menu?.SideBar != null) { _cachedSideBar = menu.SideBar; return _cachedSideBar; }
            }
            catch { }
            try
            {
                var sidebar = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.SideBar>();
                if (sidebar != null) { _cachedSideBar = sidebar; return _cachedSideBar; }
            }
            catch { }
            try
            {
                var go = GameObject.Find("Build/SideBar");
                if (go != null) { _cachedSideBar = go.GetComponent<Il2CppSpacewood.Unity.SideBar>(); return _cachedSideBar; }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Returns true if the sidebar/pause menu is currently open.
        /// </summary>
        private static bool IsSidebarOpen()
        {
            try
            {
                var sidebar = FindSideBar();
                if (sidebar?.Container != null)
                    return sidebar.Container.gameObject.activeInHierarchy;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Closes the sidebar/pause menu using the native Close() method.
        /// </summary>
        private static void CloseSidebar(string context)
        {
            try
            {
                var sidebar = FindSideBar();
                if (sidebar != null)
                {
                    sidebar.Close();
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"CloseSidebar error ({context}): {ex.Message}");
            }
            _sidebarActive = false;
            _sidebarItems.Clear();
            _cachedSideBar = null;
            _sidebarCacheFrame = -1;
            SectionManager.InvalidateCache();
            AccessibilityManager.Announce("Pause menu closed");
            MelonLogger.Msg($"Escape: Closed sidebar ({context})");
        }

        /// <summary>
        /// Opens the sidebar/pause menu with deferred focus to allow UI to build.
        /// </summary>
        private static void OpenPauseMenu(string context)
        {
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Menu>();
                if (menu != null)
                {
                    // Try OpenSideBar first (the game's method)
                    try
                    {
                        menu.OpenSideBar();
                        MelonLogger.Msg($"Escape: Called menu.OpenSideBar() ({context})");
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"menu.OpenSideBar() failed: {ex.Message}");
                        // Fallback: try calling SideBar.Open() directly
                        try
                        {
                            if (menu.SideBar != null)
                            {
                                menu.SideBar.Open();
                                MelonLogger.Msg($"Escape: Called SideBar.Open() fallback ({context})");
                            }
                        }
                        catch (System.Exception ex2)
                        {
                            MelonLogger.Warning($"SideBar.Open() fallback failed: {ex2.Message}");
                        }
                    }
                }
                else
                {
                    // Menu component not found â€” try finding SideBar directly
                    MelonLogger.Warning($"OpenPauseMenu: Menu not found, trying SideBar directly ({context})");
                    var sidebar = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.SideBar>();
                    if (sidebar != null)
                    {
                        sidebar.Open();
                        MelonLogger.Msg($"Escape: Called SideBar.Open() direct ({context})");
                    }
                    else
                    {
                        // Last resort: try finding the SideBar GameObject by path
                        var sidebarGO = GameObject.Find("Build/SideBar");
                        if (sidebarGO != null)
                        {
                            var sidebarComp = sidebarGO.GetComponent<Il2CppSpacewood.Unity.SideBar>();
                            if (sidebarComp != null)
                            {
                                sidebarComp.Open();
                                MelonLogger.Msg($"Escape: Called SideBar.Open() via path ({context})");
                            }
                            else
                            {
                                MelonLogger.Warning($"OpenPauseMenu: SideBar component not found on Build/SideBar ({context})");
                                return;
                            }
                        }
                        else
                        {
                            MelonLogger.Warning($"OpenPauseMenu: No Menu or SideBar found ({context})");
                            return;
                        }
                    }
                }

                SectionManager.InvalidateCache();
                // Defer focus by 2 frames to let the sidebar UI build
                _pendingSidebarFocusFrames = 2;
                _pendingSidebarResumeButton = true;
                MelonLogger.Msg($"Escape: Opened pause menu ({context})");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"OpenPauseMenu error ({context}): {ex.Message}");
            }
        }

        /// <summary>
        /// Hides the Dock ConfirmButton if it's still visible.
        /// </summary>
        private static void DismissDockConfirmButton()
        {
            try
            {
                var dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
                if (dock?.ConfirmButton?.gameObject != null)
                    dock.ConfirmButton.gameObject.SetActive(false);
            }
            catch { }

            // Also reset hangar state machine to cancel any pending game action (e.g., end turn)
            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar?.StateMachine != null)
                    hangar.StateMachine.SetStateDefault();
            }
            catch { }
        }

        /// <summary>
        /// Validates exclusive-mode flags against actual UI state.
        /// Clears stale flags that would block Escape from reaching the pause menu.
        /// </summary>
        private static void ValidateExclusiveModeFlags()
        {
            // Validate _tipsActive
            if (_tipsActive)
            {
                try
                {
                    var tips = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Tips>();
                    if (tips == null || tips.Canvas == null || !tips.Canvas.gameObject.activeInHierarchy)
                    {
                        _tipsActive = false;
                        _lastTipsPageIndex = -1;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _tipsActive");
                    }
                }
                catch { _tipsActive = false; _lastTipsPageIndex = -1; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _iconAlertActive
            if (_iconAlertActive)
            {
                try
                {
                    var iconAlert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.IconAlert>();
                    if (iconAlert == null || !iconAlert.gameObject.activeInHierarchy)
                    {
                        _iconAlertActive = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _iconAlertActive");
                    }
                }
                catch { _iconAlertActive = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _confirmPopupActive
            if (_confirmPopupActive)
            {
                try
                {
                    var dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
                    if (dock?.ConfirmButton?.gameObject == null || !dock.ConfirmButton.gameObject.activeInHierarchy)
                    {
                        _confirmPopupActive = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _confirmPopupActive");
                    }
                }
                catch { _confirmPopupActive = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _alert2Active
            if (_alert2Active)
            {
                try
                {
                    var alert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Alert2>();
                    if (alert == null || !alert.IsOpen)
                    {
                        _alert2Active = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _alert2Active");
                    }
                }
                catch { _alert2Active = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _namePickerActive
            if (_namePickerActive)
            {
                try
                {
                    var dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
                    bool stillOpen = false;
                    if (dock?.Adjectives != null && dock.Adjectives.Count > 0)
                    {
                        var adjContainer = dock.AdjectiveContainer;
                        if (adjContainer != null && adjContainer.gameObject.activeInHierarchy)
                            stillOpen = true;
                    }
                    if (!stillOpen)
                    {
                        _namePickerActive = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _namePickerActive");
                    }
                }
                catch { _namePickerActive = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _lastBattleMenuOpen
            if (_lastBattleMenuOpen)
            {
                try
                {
                    var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                    bool stillOpen = false;
                    if (hangar?.LastBattleMenu?.Container?.gameObject != null)
                        stillOpen = hangar.LastBattleMenu.Container.gameObject.activeInHierarchy;
                    if (!stillOpen)
                    {
                        _lastBattleMenuOpen = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _lastBattleMenuOpen");
                    }
                }
                catch { _lastBattleMenuOpen = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _deckViewerOpen
            if (_deckViewerOpen)
            {
                try
                {
                    var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                    bool stillOpen = false;
                    if (hangar?.DeckViewer?.gameObject != null)
                        stillOpen = hangar.DeckViewer.gameObject.activeInHierarchy;
                    if (!stillOpen)
                    {
                        _deckViewerOpen = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _deckViewerOpen");
                    }
                }
                catch { _deckViewerOpen = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }

            // Validate _scoreboardOpen
            if (_scoreboardOpen)
            {
                try
                {
                    var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                    bool stillOpen = false;
                    var sbGo = hangar?.GetScoreboard();
                    if (sbGo != null) stillOpen = sbGo.activeInHierarchy;
                    if (!stillOpen)
                    {
                        _scoreboardOpen = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        MelonLogger.Msg("ValidateFlags: Cleared stale _scoreboardOpen");
                    }
                }
                catch { _scoreboardOpen = false; Gameplay.ShopNavigationManager.IsInputSuppressed = false; }
            }
        }

        // Whether to try focusing the Resume button specifically on sidebar open
        private static bool _pendingSidebarResumeButton = false;

        private static void HandleNavigation()
        {
            // If sidebar is active, it handles its own navigation
            if (_sidebarActive) return;
            // If ShopNavigationManager is actively navigating, it handles Left/Right/Up/Down
            if (Gameplay.ShopNavigationManager.IsActivelyNavigating()) return;

            bool shiftDown = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool tab      = Input.GetKeyDown(KeyCode.Tab);
            bool up       = Input.GetKeyDown(KeyCode.UpArrow);
            bool down     = Input.GetKeyDown(KeyCode.DownArrow);
            bool left     = Input.GetKeyDown(KeyCode.LeftArrow);
            bool right    = Input.GetKeyDown(KeyCode.RightArrow);
            bool pageUp   = Input.GetKeyDown(KeyCode.PageUp);
            bool pageDown = Input.GetKeyDown(KeyCode.PageDown);

            if (!tab && !up && !down && !left && !right && !pageUp && !pageDown) return;

            // Tab/Shift+Tab or Page Down/Page Up: move between sections
            if (pageDown || (tab && !shiftDown))    SectionManager.MoveToNextSection();
            else if (pageUp || (tab && shiftDown))  SectionManager.MoveToPrevSection();
            // Up/Down: move within current section
            else if (down)              SectionManager.MoveToNextElement();
            else if (up)                SectionManager.MoveToPrevElement();
            // Left/Right: adjust sliders (no-op on non-slider elements)
            else if (left || right)     HandleSliderAdjust(right);
        }

        /// <summary>
        /// Adjusts a slider's value when Left/Right arrow is pressed on a SliderBase element.
        /// Does nothing if the focused element is not a slider.
        /// </summary>
        /// <summary>
        /// Gets a PackProduct's display name by finding the Name/Label child in its hierarchy.
        /// Falls back to the GameObject name if label text isn't found.
        /// Scene hierarchy: PackProduct â†’ BuyButton â†’ Tween â†’ Name â†’ Label (TMP_Text with pack name)
        /// </summary>
        private static string GetPackProductName(Il2CppSpacewood.Unity.PackProduct packProduct)
        {
            string packName = "Pack";
            if (packProduct == null) return packName;
            try
            {
                // Navigate the known hierarchy: BuyButton â†’ Tween â†’ Name â†’ Label
                var button = packProduct.Button;
                if (button != null)
                {
                    var nameTransform = button.transform.Find("Tween/Name/Label");
                    if (nameTransform != null)
                    {
                        var txt = nameTransform.GetComponent<Il2CppTMPro.TMP_Text>();
                        if (txt != null && !string.IsNullOrWhiteSpace(txt.text))
                            return txt.text.Trim();
                    }
                    // Try without Tween level (in case hierarchy varies)
                    nameTransform = button.transform.Find("Tween/Name");
                    if (nameTransform != null)
                    {
                        var txt = nameTransform.GetComponentInChildren<Il2CppTMPro.TMP_Text>();
                        if (txt != null && !string.IsNullOrWhiteSpace(txt.text))
                            return txt.text.Trim();
                    }
                }

                // Fallback: use the PackProduct's GameObject name (e.g., "Turtle", "Challenge")
                string goName = packProduct.gameObject.name;
                if (!string.IsNullOrWhiteSpace(goName) && goName != "PackProduct" && !goName.StartsWith("PackProduct(Clone)"))
                    return goName;
            }
            catch { }
            return packName;
        }

        /// <summary>
        /// Checks if a PackProduct is currently selected by looking for an active "Activator" child.
        /// In the game's UI, the Activator child is active when the pack is selected.
        /// Scene hierarchy: PackProduct â†’ BuyButton â†’ Tween â†’ Activator
        /// </summary>
        private static bool IsPackProductSelected(Il2CppSpacewood.Unity.PackProduct packProduct)
        {
            if (packProduct == null) return false;
            try
            {
                var button = packProduct.Button;
                if (button == null) return false;
                var activator = button.transform.Find("Tween/Activator");
                if (activator != null && activator.gameObject.activeInHierarchy)
                    return true;
            }
            catch { }
            return false;
        }

        private static void HandleSliderAdjust(bool increase)
        {
            var go = EventSystem.current?.currentSelectedGameObject;
            if (go == null) return;

            var sliderBase = go.GetComponent<SliderBase>();
            if (sliderBase == null) return;

            try
            {
                var slider = sliderBase.Slider;
                if (slider == null) return;

                float range = slider.maxValue - slider.minValue;
                float step = range * 0.05f; // 5% per press
                if (step < 0.01f) step = 0.01f;

                float newValue = slider.value + (increase ? step : -step);
                newValue = Mathf.Clamp(newValue, slider.minValue, slider.maxValue);
                slider.value = newValue;

                // Announce the new value after a short delay for the game's label to update
                _pendingSliderAnnounce = go;
                _sliderAnnounceDelay = 3; // frames
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"HandleSliderAdjust error: {ex.Message}");
            }
        }

        // Deferred sidebar focus (wait for sidebar UI to build)
        private static int _pendingSidebarFocusFrames = 0;

        // Deferred slider value announcement (wait for game to update LabelRight text)
        private static GameObject _pendingSliderAnnounce = null;
        private static int _sliderAnnounceDelay = 0;

        // Deferred button value change detection
        private static GameObject _pendingValueCheck = null;
        private static string _preClickValue = "";
        private static int _valueCheckDelay = 0;

        // Deferred locked-pack click â€” defer onClick by 1 frame so Enter key release
        // doesn't carry over to the dialog's Cancel button via Unity's InputModule
        private static UnityEngine.UI.Button _pendingLockedPackClick = null;
        private static GameObject _pendingLockedPackGo = null;
        private static int _pendingLockedPackFrames = 0;

        // Deferred pack selection announcement
        private static string _pendingPackName = null;
        private static int _packCheckDelay = 0;

        // Page transition cooldown â€” prevents Enter key carry-over from triggering pack selection
        // on the first frames after a page transition (the Enter that opened the new page)
        private static int _pageTransitionCooldown = 0;

        // Picker state tracking â€” detect when picker opens to announce and focus
        private static bool _lastPickerOpen = false;

        // DeckNamer popup tracking (prefix/suffix team name editor)
        private static bool _lastDeckNamerOpen = false;

        // Mouse tracking mode (M key toggle)
        private static bool _mouseTrackingEnabled = true;
        private static GameObject _lastMouseTrackedObject = null;

        // News dialog tracking
        internal static bool _lastNewsOpen = false;
        private static List<string> _newsCodecDetailLines = new List<string>();
        private static int _newsCodecDetailLineIndex = 0;
        private static string _lastNewsCodecHeader = ""; // track which item's details are loaded
        internal static int NewsCodecDetailLineCount => _newsCodecDetailLines.Count;
        private static int _newsPageFlipPending = 0; // >0 = frames remaining before rebuild after page flip
        private static int _newsPageFlipDirection = 0; // 1 = forward, -1 = backward
        private static int _newsEnrichPending = 0; // >0 = frames remaining before deferred codec enrichment
        private static string _newsEnrichBaseAnnouncement = ""; // base announcement to enrich
        internal static string _lastNewsCodecSpriteName = ""; // sprite name of current codec item for page flips

        // Tips dialog tracking (multi-page tutorial/help)
        private static bool _lastTipsOpen = false;
        private static bool _tipsActive = false;
        private static int _lastTipsPageIndex = -1;

        // Tutorial prompt tracking
        private static bool _lastTutorialPromptOpen = false;

        // End-turn confirm popup tracking
        private static bool _lastConfirmVisible = false;
        private static bool _confirmPopupActive = false;
        private static bool _confirmFocusOnConfirm = true; // Track which virtual button is focused

        // IconAlert popup tracking (turn milestones, tier unlocks, life gain/loss)
        private static bool _lastIconAlertOpen = false;
        private static bool _iconAlertActive = false;
        private static string _lastIconAlertText = ""; // For R key re-read

        // Alert2 popup tracking (food replacement confirmations, etc.)
        private static bool _lastAlert2Open = false;
        private static int _pendingAlert2FocusFrames = 0;
        private static int _alert2InputCooldown = 0;
        private static bool _alert2Active = false;

        // TallyArena (battle result) tracking
        private static bool _lastTallyArenaOpen = false;

        // TallyBullyScore (Daily-mode moustache score) tracking
        private static bool _lastTallyBullyScoreOpen = false;
        private static bool _tallyArenaActive = false;

        // TallyArenaFinale (end-of-run EXP/bones summary) tracking
        private static bool _lastTallyFinaleOpen = false;
        private static bool _tallyFinaleActive = false;

        // TallyArenaReward (unlocked item display) tracking
        private static bool _lastTallyRewardOpen = false;
        private static bool _tallyRewardActive = false;

        // DesyncAlert tracking (desync error during match)
        private static bool _lastDesyncAlertOpen = false;

        // TallyArenaMenu (play again / return to menu) tracking
        private static bool _lastTallyMenuOpen = false;
        private static bool _tallyMenuActive = false;
        private static int _tallyMenuFocusIndex = 0;
        private static System.Collections.Generic.List<(string label, System.Action action)> _tallyMenuButtons =
            new System.Collections.Generic.List<(string, System.Action)>();

        // Tier upgrade overlay tracking
        private static int _lastAnnouncedTier = 0;

        // Team name picker (Dock adjective/noun) tracking
        private static bool _lastNamePickerOpen = false;
        private static bool _namePickerActive = false;
        private static int _namePickerAdjectiveIndex = 0;
        private static int _namePickerNounIndex = 0;
        private static bool _namePickerOnAdjectives = true; // true = adjective row, false = noun row
        private static bool _namePickerAdjectiveSelected = false;
        private static bool _namePickerNounSelected = false;
        private static bool _namePickerOnConfirm = false; // true = confirm button focused, arrows go back to editing

        // Text input tracking for backspace reading
        private static string _lastInputFieldText = "";
        private static GameObject _lastInputFieldObject = null;

        // LastBattleMenu overlay tracking
        private static bool _lastBattleMenuOpen = false;

        // DeckViewer overlay tracking
        private static bool _deckViewerOpen = false;

        // Scoreboard (opponent list) overlay tracking
        private static bool _scoreboardOpen = false;
        private static bool _scoreboardJustOpened = false; // Skip O-key close on the frame it opens

        // Scoreboard detail line navigation
        private static List<Il2CppSpacewood.Core.Models.UserVersusOpponent> _scoreboardOpponents =
            new List<Il2CppSpacewood.Core.Models.UserVersusOpponent>();
        private static List<Il2CppSpacewood.Unity.UI.SelectableBase> _scoreboardButtons =
            new List<Il2CppSpacewood.Unity.UI.SelectableBase>();
        private static List<string> _scoreboardDetailLines = new List<string>();
        private static int _scoreboardDetailLineIndex = 0;
        private static int _scoreboardOpponentIndex = 0;

        // Replay page navigation tracking
        private static bool _replayPageOpen = false;
        private static int _replayFocusIndex = 0;
        private static int _replayPetIndex = 0;
        private static int _pendingReplayFocusFrames = 0;
        private const int REPLAY_FOCUS_DELAY = 5;

        /// <summary>
        /// Called by PageManagerPatches when the Replay page is opened.
        /// </summary>
        public static void NotifyReplayPageOpened()
        {
            _replayPageOpen = true;
            _replayFocusIndex = 0;
            _replayPetIndex = 0;
            _pendingReplayFocusFrames = REPLAY_FOCUS_DELAY;
            MelonLogger.Msg("[Replay] Page opened, deferring focus");
        }

        /// <summary>
        /// Called by PageManagerPatches when navigating away from the Replay page.
        /// </summary>
        public static void NotifyReplayPageClosed()
        {
            if (_replayPageOpen)
            {
                _replayPageOpen = false;
                _pendingReplayFocusFrames = 0;
                MelonLogger.Msg("[Replay] Page closed");
            }
        }

        /// <summary>
        /// Process deferred replay focus (called from OnUpdate).
        /// Waits for items to populate, then announces the first replay.
        /// </summary>
        private static void ProcessReplayFocusPending()
        {
            if (_pendingReplayFocusFrames <= 0) return;

            _pendingReplayFocusFrames--;
            if (_pendingReplayFocusFrames > 0) return;

            try
            {
                var replayPage = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Replay>();
                if (replayPage == null || replayPage.Items == null)
                {
                    AccessibilityManager.Announce("Replays");
                    return;
                }

                int count = replayPage.Items.Count;
                if (count == 0)
                {
                    AccessibilityManager.Announce("Replays, no entries");
                    return;
                }

                _replayFocusIndex = 0;
                _replayPetIndex = 0;
                string summary = BuildReplaySummary(replayPage.Items[0], 0, count);
                AccessibilityManager.Announce($"Replays, {count} entries. {summary}");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[Replay] Focus error: {ex.Message}");
                AccessibilityManager.Announce("Replays");
            }
        }

        /// <summary>
        /// Handles keyboard input when the Replay page is active.
        /// Returns true if input was consumed.
        /// </summary>
        private static bool HandleReplayNavigation()
        {
            if (!_replayPageOpen) return false;

            try
            {
                var replayPage = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Replay>();
                if (replayPage == null || replayPage.Items == null || replayPage.Items.Count == 0)
                    return false;

                var items = replayPage.Items;
                int count = items.Count;

                bool up = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.UpArrow);
                bool down = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.DownArrow);
                bool left = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.LeftArrow);
                bool right = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.RightArrow);
                bool enter = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Return)
                          || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.KeypadEnter);
                bool home = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Home);
                bool end = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.End);

                if (up || down)
                {
                    // Navigate replay list
                    if (up)
                    {
                        _replayFocusIndex--;
                        if (_replayFocusIndex < 0) _replayFocusIndex = count - 1;
                    }
                    else
                    {
                        _replayFocusIndex++;
                        if (_replayFocusIndex >= count) _replayFocusIndex = 0;
                    }
                    _replayPetIndex = 0; // Reset pet selection when changing replay

                    string summary = BuildReplaySummary(items[_replayFocusIndex], _replayFocusIndex, count);
                    AccessibilityManager.Announce(summary);
                    return true;
                }

                if (home)
                {
                    _replayFocusIndex = 0;
                    _replayPetIndex = 0;
                    string summary = BuildReplaySummary(items[_replayFocusIndex], _replayFocusIndex, count);
                    AccessibilityManager.Announce(summary);
                    return true;
                }

                if (end)
                {
                    _replayFocusIndex = count - 1;
                    _replayPetIndex = 0;
                    string summary = BuildReplaySummary(items[_replayFocusIndex], _replayFocusIndex, count);
                    AccessibilityManager.Announce(summary);
                    return true;
                }

                if (left || right)
                {
                    // Navigate pets within the focused replay entry
                    var item = items[_replayFocusIndex];
                    string petInfo = NavigateReplayPets(item, right);
                    if (!string.IsNullOrEmpty(petInfo))
                        AccessibilityManager.Announce(petInfo);
                    else
                        AccessibilityManager.Announce("No pets");
                    return true;
                }

                if (enter)
                {
                    // Watch the selected replay
                    var item = items[_replayFocusIndex];
                    if (item?.Button != null)
                    {
                        item.Button.Click();
                        AccessibilityManager.Announce("Loading replay");
                        _replayPageOpen = false;
                        MelonLogger.Msg($"[Replay] Loading replay {_replayFocusIndex + 1}");
                    }
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[Replay] Navigation error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Builds a verbose summary string for a replay item.
        /// Format: "{date}, {boardName}, {victories} wins, {lives} lives, turn {turn}, {result}. {pet names}. {index} of {total}"
        /// </summary>
        private static string BuildReplaySummary(Il2CppSpacewood.Unity.ReplayItem item, int index, int total)
        {
            try
            {
                var parts = new List<string>();

                // Date
                try
                {
                    string date = item.DateText?.text;
                    if (!string.IsNullOrWhiteSpace(date)) parts.Add(date.Trim());
                }
                catch { }

                // Board name
                try
                {
                    string boardName = item.BoardNameText?.text;
                    if (!string.IsNullOrWhiteSpace(boardName)) parts.Add(boardName.Trim());
                }
                catch { }

                // Victories
                try
                {
                    string victories = item.VictoryText?.text;
                    if (!string.IsNullOrWhiteSpace(victories)) parts.Add($"{victories.Trim()} wins");
                }
                catch { }

                // Lives
                try
                {
                    string lives = item.LivesText?.text;
                    if (!string.IsNullOrWhiteSpace(lives)) parts.Add($"{lives.Trim()} lives");
                }
                catch { }

                // Turn
                try
                {
                    string turn = item.TurnText?.text;
                    if (!string.IsNullOrWhiteSpace(turn)) parts.Add($"turn {turn.Trim()}");
                }
                catch { }

                // Result (Victory or Defeat)
                try
                {
                    if (item.Victory != null && item.Victory.gameObject.activeInHierarchy)
                        parts.Add("Victory");
                    else
                        parts.Add("Defeat");
                }
                catch { parts.Add("Defeat"); }

                string mainInfo = string.Join(", ", parts);

                // Pet team names
                string petNames = GetReplayPetNames(item);
                if (!string.IsNullOrEmpty(petNames))
                    mainInfo += $". {petNames}";

                return $"{mainInfo}. {index + 1} of {total}";
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[Replay] BuildSummary error: {ex.Message}");
                return $"Replay {index + 1} of {total}";
            }
        }

        /// <summary>
        /// Gets the pet names from a replay item's MinionContainer.
        /// </summary>
        /// <summary>
        /// Finds the Layout transform that holds MinionCanvasView children.
        /// Hierarchy: MinionContainer â†’ Layout â†’ MinionCanvasView(Clone) x5
        /// Falls back to searching GetComponentsInChildren if not found by name.
        /// </summary>
        private static UnityEngine.Transform FindMinionLayout(UnityEngine.RectTransform container)
        {
            if (container == null) return null;

            // Direct child named "Layout"
            var layout = container.Find("Layout");
            if (layout != null) return layout;

            // If MinionContainer itself has MinionCanvasView children, use it directly
            if (container.childCount > 0)
            {
                var firstChild = container.GetChild(0);
                if (firstChild != null && firstChild.GetComponent<Il2CppSpacewood.Unity.Views.MinionCanvasView>() != null)
                    return container.transform;
            }

            // Fallback: search one level deeper for any child with a "Layout" child
            for (int i = 0; i < container.childCount; i++)
            {
                var child = container.GetChild(i);
                if (child == null) continue;
                var nested = child.Find("Layout");
                if (nested != null) return nested;
            }

            return container.transform; // Last resort: use container itself
        }

        private static string GetReplayPetNames(Il2CppSpacewood.Unity.ReplayItem item)
        {
            try
            {
                var container = item.MinionContainer;
                if (container == null) return null;

                var layout = FindMinionLayout(container);

                var names = new List<string>();
                for (int i = 0; i < layout.childCount; i++)
                {
                    try
                    {
                        var child = layout.GetChild(i);
                        if (child == null || !child.gameObject.activeInHierarchy) continue;
                        var minionView = child.GetComponent<Il2CppSpacewood.Unity.Views.MinionCanvasView>();
                        if (minionView?.Model != null)
                        {
                            string name = Gameplay.PetStatsReader.GetLocalizedName(minionView.Model);
                            if (!string.IsNullOrEmpty(name) && name != "Unknown pet")
                                names.Add(name);
                        }
                    }
                    catch { }
                }

                return names.Count > 0 ? string.Join(", ", names) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Navigates through pets in a replay entry's lineup using Left/Right.
        /// Returns the pet info string to announce.
        /// </summary>
        private static string NavigateReplayPets(Il2CppSpacewood.Unity.ReplayItem item, bool forward)
        {
            try
            {
                var container = item.MinionContainer;
                if (container == null) return null;

                var layout = FindMinionLayout(container);
                if (layout == null) return null;

                // Collect active MinionCanvasViews
                var views = new List<Il2CppSpacewood.Unity.Views.MinionCanvasView>();
                for (int i = 0; i < layout.childCount; i++)
                {
                    try
                    {
                        var child = layout.GetChild(i);
                        if (child == null || !child.gameObject.activeInHierarchy) continue;
                        var minionView = child.GetComponent<Il2CppSpacewood.Unity.Views.MinionCanvasView>();
                        if (minionView != null)
                            views.Add(minionView);
                    }
                    catch { }
                }

                if (views.Count == 0)
                {
                    _replayPetIndex = 0;
                    return "No pets";
                }

                // Clamp index if it's out of range for this replay's pet count
                if (_replayPetIndex >= views.Count) _replayPetIndex = 0;

                // Navigate
                if (forward)
                {
                    _replayPetIndex++;
                    if (_replayPetIndex >= views.Count) _replayPetIndex = 0;
                }
                else
                {
                    _replayPetIndex--;
                    if (_replayPetIndex < 0) _replayPetIndex = views.Count - 1;
                }

                var view = views[_replayPetIndex];
                var model = view.Model;
                if (model == null) return $"Empty, pet {_replayPetIndex + 1} of {views.Count}";

                string petName = Gameplay.PetStatsReader.GetLocalizedName(model);

                // Get stats
                int attack = 0, health = 0, level = 1;
                try { attack = model.Attack.Total; } catch { }
                try { health = model.Health.Total; } catch { }
                try { level = model.Level; } catch { }

                string info = $"{petName}, {attack} attack, {health} health, level {level}";

                // Perk
                try
                {
                    if (Gameplay.PetStatsReader.HasRealPerk(model, out var perkVal))
                    {
                        string perkName = Gameplay.PetStatsReader.GetPerkDisplayName(perkVal);
                        if (!string.IsNullOrEmpty(perkName))
                            info += $", {perkName}";
                    }
                }
                catch { }

                return $"{info}. Pet {_replayPetIndex + 1} of {views.Count}";
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[Replay] Pet navigation error: {ex.Message}");
                return null;
            }
        }

        // Battle fast forward state tracking
        private static bool _fastForwardActive = false;
        private static int _pendingFastForwardFrames = 0;
        private const int FAST_FORWARD_APPLY_DELAY = 10; // Wait for battle UI to be ready

        /// <summary>
        /// Resets battle-phase UI state. Called when entering a new battle.
        /// Fast forward state is preserved across battles â€” if it was on, we re-apply it.
        /// </summary>
        public static void ResetBattleState()
        {
            if (_fastForwardActive)
                _pendingFastForwardFrames = FAST_FORWARD_APPLY_DELAY;
        }

        /// <summary>
        /// Re-applies fast forward state after entering a new battle.
        /// Called from OnUpdate() during battle phase.
        /// Reads the actual game state to avoid toggling in the wrong direction.
        /// </summary>
        private static void ApplyPendingFastForward()
        {
            if (_pendingFastForwardFrames <= 0) return;

            _pendingFastForwardFrames--;
            if (_pendingFastForwardFrames > 0) return;

            try
            {
                // Check actual game state â€” only click if it's NOT already fast forwarding
                bool alreadyFast = Il2CppSpacewood.Unity.MonoBehaviours.Battle.BattleController.IsFastfowarding;
                if (alreadyFast)
                {
                    MelonLogger.Msg("[Battle] Fast forward already active in new battle, no re-apply needed");
                    return;
                }

                var uiBattle = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle>();
                var speedBtn = uiBattle?._UIBattleSpeed?.FastForwardButton?.Button;
                if (speedBtn != null)
                {
                    speedBtn.Click();
                    MelonLogger.Msg("[Battle] Re-applied fast forward from previous battle");
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"Fast forward re-apply error: {ex.Message}");
            }
        }

        // Sidebar/pause menu keyboard navigation
        private static Il2CppSpacewood.Unity.SideBar _cachedSideBar = null;
        private static int _sidebarCacheFrame = -1;
        private static bool _sidebarActive = false;
        private static int _sidebarFocusIndex = 0;
        private static readonly List<(string label, System.Action<Il2CppSpacewood.Unity.SideBar> handler)> _sidebarItems =
            new List<(string, System.Action<Il2CppSpacewood.Unity.SideBar>)>();

        /// <summary>
        /// Builds the list of visible sidebar buttons for keyboard navigation.
        /// Called after deferred frames when sidebar UI is fully built.
        /// </summary>
        private static void BuildSidebarItems()
        {
            _sidebarItems.Clear();
            _sidebarFocusIndex = 0;

            try
            {
                var sidebar = FindSideBar();
                if (sidebar == null)
                {
                    _sidebarActive = false;
                    AccessibilityManager.RequestFocusFirst();
                    return;
                }

                // Add buttons in display order, checking visibility
                TryAddSidebarItem(sidebar, "Resume", sidebar.Resume, (sb) => sb.HandleResume(sb.Resume));
                TryAddSidebarItem(sidebar, "Versus games", sidebar.List, (sb) => sb.HandleList(sb.List));
                TryAddSidebarItem(sidebar, "Settings", sidebar.Settings, (sb) => sb.HandleSettings(sb.Settings));
                TryAddSidebarItem(sidebar, "Account", sidebar.Account, (sb) => ClickSidebarButton(sb.Account));
                TryAddSidebarItem(sidebar, "Subscription", sidebar.Premium, (sb) => ClickSidebarButton(sb.Premium));
                TryAddSidebarItem(sidebar, "Feedback", sidebar.Feedback, (sb) => ClickSidebarButton(sb.Feedback));
                TryAddSidebarItem(sidebar, "Credits", sidebar.Credits, (sb) => ClickSidebarButton(sb.Credits));
                TryAddSidebarItem(sidebar, "Game tips", sidebar.Tips, (sb) => sb.HandleTips(sb.Tips));
                TryAddSidebarItem(sidebar, "Return to menu", sidebar.Return, (sb) => sb.HandleReturn(sb.Return));
                TryAddSidebarItem(sidebar, "Log out", sidebar.LogOut, (sb) => ClickSidebarButton(sb.LogOut));
                TryAddSidebarItem(sidebar, "Abandon", sidebar.Abandon, (sb) => sb.HandleAbandon(sb.Abandon));
                TryAddSidebarItem(sidebar, "Quit to desktop", sidebar.Quit, (sb) => sb.HandleQuit(sb.Quit));

                if (_sidebarItems.Count > 0)
                {
                    _sidebarActive = true;
                    AccessibilityManager.Announce(
                        $"Pause menu, {_sidebarItems[0].label}, 1 of {_sidebarItems.Count}");
                    MelonLogger.Msg($"Sidebar: Built {_sidebarItems.Count} items");
                }
                else
                {
                    _sidebarActive = false;
                    AccessibilityManager.Announce("Pause menu opened");
                    MelonLogger.Warning("Sidebar: No visible buttons found");
                }
            }
            catch (System.Exception ex)
            {
                _sidebarActive = false;
                MelonLogger.Warning($"BuildSidebarItems error: {ex.Message}");
                AccessibilityManager.Announce("Pause menu opened");
            }
        }

        private static void TryAddSidebarItem(Il2CppSpacewood.Unity.SideBar sidebar,
            string label, Il2CppSpacewood.Unity.UI.ButtonBase button,
            System.Action<Il2CppSpacewood.Unity.SideBar> handler)
        {
            try
            {
                if (button?.gameObject != null && button.gameObject.activeInHierarchy)
                {
                    _sidebarItems.Add((label, handler));
                }
            }
            catch { }
        }

        /// <summary>
        /// Generic sidebar button click â€” triggers the Unity Button onClick event.
        /// Used for sidebar items where we don't have the exact HandleXxx method name.
        /// </summary>
        private static void ClickSidebarButton(Il2CppSpacewood.Unity.UI.ButtonBase btn)
        {
            if (btn == null || btn.gameObject == null) return;
            try
            {
                var unityButton = btn.GetComponent<UnityEngine.UI.Button>();
                if (unityButton != null)
                    unityButton.onClick.Invoke();
                else
                    ExecuteEvents.Execute(btn.gameObject,
                        new PointerEventData(EventSystem.current),
                        ExecuteEvents.submitHandler);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"ClickSidebarButton error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles keyboard input when the sidebar/pause menu is active.
        /// Up/Down to navigate items, Enter to activate, Escape handled elsewhere.
        /// </summary>
        private static void HandleSidebarNavigation()
        {
            // Verify sidebar is still open
            try
            {
                var sidebar = FindSideBar();
                if (sidebar?.Container == null || !sidebar.Container.gameObject.activeInHierarchy)
                {
                    _sidebarActive = false;
                    _sidebarItems.Clear();
                    return;
                }
            }
            catch
            {
                _sidebarActive = false;
                _sidebarItems.Clear();
                return;
            }

            if (_sidebarItems.Count == 0) return;

            bool up = Input.GetKeyDown(KeyCode.UpArrow);
            bool down = Input.GetKeyDown(KeyCode.DownArrow);
            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            bool home = Input.GetKeyDown(KeyCode.Home);
            bool end = Input.GetKeyDown(KeyCode.End);

            if (up)
            {
                _sidebarFocusIndex--;
                if (_sidebarFocusIndex < 0) _sidebarFocusIndex = _sidebarItems.Count - 1;
                AccessibilityManager.Announce($"{_sidebarItems[_sidebarFocusIndex].label}. {_sidebarFocusIndex + 1} of {_sidebarItems.Count}");
            }
            else if (down)
            {
                _sidebarFocusIndex++;
                if (_sidebarFocusIndex >= _sidebarItems.Count) _sidebarFocusIndex = 0;
                AccessibilityManager.Announce($"{_sidebarItems[_sidebarFocusIndex].label}. {_sidebarFocusIndex + 1} of {_sidebarItems.Count}");
            }
            else if (home)
            {
                _sidebarFocusIndex = 0;
                AccessibilityManager.Announce($"{_sidebarItems[_sidebarFocusIndex].label}. {_sidebarFocusIndex + 1} of {_sidebarItems.Count}");
            }
            else if (end)
            {
                _sidebarFocusIndex = _sidebarItems.Count - 1;
                AccessibilityManager.Announce($"{_sidebarItems[_sidebarFocusIndex].label}. {_sidebarFocusIndex + 1} of {_sidebarItems.Count}");
            }
            else if (enter)
            {
                try
                {
                    var sidebar = FindSideBar();
                    if (sidebar != null)
                    {
                        var (label, handler) = _sidebarItems[_sidebarFocusIndex];
                        MelonLogger.Msg($"Sidebar: Activating '{label}'");
                        handler(sidebar);
                        _sidebarActive = false;
                        _sidebarItems.Clear();
                        SectionManager.InvalidateCache();
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning($"Sidebar activate error: {ex.Message}");
                }
            }
        }

        private static void ReadBonesCounter()
        {
            try
            {
                var allTMP = UnityEngine.Object.FindObjectsOfType<Il2CppTMPro.TextMeshProUGUI>();
                foreach (var tmp in allTMP)
                {
                    if (tmp == null || !tmp.gameObject.activeInHierarchy) continue;
                    string text = tmp.text;
                    if (string.IsNullOrEmpty(text)) continue;

                    // Match bones format: digits / digits (e.g., "0 / 5" or "124 / 500")
                    if (Regex.IsMatch(text.Trim(), @"^\d+\s*/\s*\d+$"))
                    {
                        AccessibilityManager.Announce($"Bones: {text.Trim()}");
                        return;
                    }
                }
                AccessibilityManager.Announce("Bones counter not found");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"ReadBonesCounter error: {ex.Message}");
            }
        }

        // --- Help overlay (navigable TTS overlay with categories) ---

        private static bool _helpOverlayActive = false;
        private static bool _helpOverlaySkipFrame = false;
        private static int _helpCategoryIndex = 0;
        private static int _helpItemIndex = 0;
        private static Gameplay.GamePhase _helpOpenedInPhase = Gameplay.GamePhase.Unknown;
        private static List<(string category, List<string> items)> _helpCategories =
            new List<(string, List<string>)>();

        /// <summary>
        /// Keyword dictionary entries split into four sub-categories so the user can
        /// Tab between Triggers / Food perks / Statuses / Mechanics rather than
        /// scrolling through one long list.
        /// </summary>
        private static readonly List<string> _keywordDictionaryTriggers = new List<string>
        {
            "Start of battle: Triggers once when combat begins, before any pet attacks.",
            "Start of turn: Triggers at the start of each shop phase.",
            "End of turn: Triggers when you press End Turn, before the battle starts.",
            "Faint: Triggers when this pet dies in battle.",
            "Hurt: Triggers when this pet takes damage but survives.",
            "Friend hurt: Triggers when an allied pet takes damage.",
            "Friend faints: Triggers when an allied pet dies.",
            "Friend ahead faints: Triggers when the ally directly in front of this pet dies.",
            "Friend ahead attacks: Triggers when the ally directly in front of this pet attacks.",
            "Friend attacks: Triggers when any allied pet attacks.",
            "Friend summoned: Triggers when a new allied pet is summoned during battle.",
            "Friend bought: Triggers when you buy a pet from the shop.",
            "Friend sold: Triggers when you sell an allied pet.",
            "Friend ate food: Triggers when an allied pet consumes food.",
            "Eat food: Triggers when this pet itself eats food.",
            "Summoned: Triggers when this pet enters the team, whether bought or summoned during battle.",
            "Sell: Triggers when this pet is sold.",
            "Buy: Triggers when this pet is purchased from the shop.",
            "Level up: Triggers when this pet reaches a new level by combining.",
            "Friend leveled up: Triggers when an allied pet levels up.",
            "Knockout: Triggers when this pet defeats an enemy in a single attack.",
            "Before attack: Triggers in the instant before this pet attacks.",
            "After attack: Triggers immediately after this pet attacks.",
            "Shop rolled: Triggers each time the shop is rolled.",
            "Break: Triggers when this pet's toy or relic breaks.",
            "Toy summoned: Triggers when a toy is granted to this pet.",
            "Gain perk: Triggers when this pet gains a perk like Honey or Melon.",
            "Lose perk: Triggers when this pet loses a perk it was holding.",
            "Friend transformed: Triggers when an allied pet changes into a different pet.",
            "Friend jumped: Triggers when an allied pet performs a jump attack.",
            "Friend gained attack: Triggers when an allied pet's attack is permanently increased.",
            "Friend gained health: Triggers when an allied pet's health is permanently increased.",
            "Spend gold: Triggers when you spend the listed amount of gold this turn.",
            "Anyone attacks: Triggers when any pet on the board attacks.",
            "Enemy hurt: Triggers when an enemy pet takes damage.",
            "Enemy faints: Triggers when an enemy pet dies.",
        };

        private static readonly List<string> _keywordDictionaryFoodPerks = new List<string>
        {
            "Apple: Permanent plus 1 attack, plus 1 health.",
            "Pear: Permanent plus 2 attack, plus 2 health.",
            "Cupcake: Plus 3 attack, plus 3 health for the next battle only.",
            "Croissant: The carrier gains plus 1 attack at the end of each turn.",
            "Salad Bowl: Plus 1 attack, plus 1 health to two random allies.",
            "Canned Food: Plus 1 attack, plus 1 health to all current and future shop pets.",
            "Sleeping Pill: Makes one pet faint, triggering its faint ability. Always on sale at 1 gold.",
            "Honey: When this pet faints, summons a 1 attack, 1 health Bee.",
            "Mushroom: Revives this pet once at 1 attack, 1 health when it faints.",
            "Garlic: The carrier takes 2 less damage from each hit, but never less than 2.",
            "Melon: Blocks the next 20 damage taken, then breaks.",
            "Coconut: Blocks the first instance of damage taken, then breaks.",
            "Steak: First attack deals plus 20 damage, then breaks.",
            "Meat Bone: The carrier's attack deals plus 3 damage.",
            "Chili: On attack, also deals 5 damage to the second enemy.",
            "Pepper: The carrier's health cannot drop below 1; removed after taking damage.",
            "Pineapple: The carrier's ability damage is increased by 2.",
            "Strawberry: When the carrier faints, gives the back-most friend plus 1 attack, plus 1 health.",
            "Cake: The carrier increases sell value by 1 gold at the end of each turn.",
            "Egg: Before the carrier attacks, deals 2 damage to the target, once.",
            "Cheese: The carrier's next attack deals at least 15 damage, then breaks.",
            "Lemon: The carrier takes 7 less damage, twice.",
            "Popcorn: When this pet faints, summons a random tier pet of the same tier.",
            "Cucumber: The carrier gains plus 1 health at the end of each turn.",
            "Carrot: Gains plus 1 attack, plus 1 health at the end of each turn.",
            "Grapes: Earn plus 1 gold at the start of each turn.",
            "Banana: When the carrier faints, summons a 4 attack, 4 health Monkey.",
            "Bread: The carrier gains plus 7 health until next turn, at the end of each turn.",
            "Onion: Before the carrier attacks, it moves to the back of the team, once.",
            "Sushi: Plus 1 attack, plus 1 health to three random friends.",
            "Fortune Cookie: The carrier's attacks have a 50 percent chance to deal double damage.",
            "Skewer: On attack, also deals 3 damage to the second and third enemies.",
            "Tomato: Before the carrier attacks, deals 10 damage to the last enemy, once.",
            "Pancakes: Before battle, gives all friends plus 2 attack, plus 2 health.",
            "Doughnut: The carrier is prioritized as the target of friendly random abilities.",
            "Eggplant: Before battle, pushes the opposite enemy 1 space forward.",
            "Pie: Before battle, the carrier gains plus 4 attack, plus 4 health.",
            "Magic Beans: At the start of next turn, the carrier gains the Golden Egg perk and sell value increases by 4 gold.",
            "Fairy Dust: When the front space is empty, the carrier jumps to the front and gains 2 mana, once.",
            "Golden Egg: Before the carrier attacks, deals 6 damage to the target, once.",
            "Easter Egg: When the carrier faints, summons a 3 attack, 3 health Bunny that attacks for double damage.",
        };

        private static readonly List<string> _keywordDictionaryStatuses = new List<string>
        {
            "Weak: The afflicted pet takes plus 3 damage from incoming attacks.",
            "Confused: Before the afflicted pet's first attack, it transforms into a random pet one tier below, once.",
            "Cursed: When the afflicted pet faints, makes one random friend Cursed.",
            "Silly: The afflicted pet's ability has random targets in battle.",
            "Sleepy: Halves damage dealt by the afflicted pet, once.",
        };

        private static readonly List<string> _keywordDictionaryMechanics = new List<string>
        {
            "Tier: A pet's tier determines how soon it appears in the shop. Tier 1 is earliest.",
            "Roll: Replaces all unfrozen pets and food in the shop for 1 gold.",
            "Freeze: Locks a shop pet or food so it stays through rolls and into the next turn. Free.",
            "Combine: Place two of the same pet on top of each other to merge. Two of the same pet make a level 2; three more make level 3.",
            "Level: Each pet levels from 1 to 3. Higher level usually means stronger abilities.",
            "Toy: A special item earned at certain shop tiers that grants a permanent perk.",
            "Relic: A special bonus item in Daily mode, displayed beside the bully team.",
            "Mana: Resource used by certain pets to trigger powerful abilities.",
            "Trumpets: An accumulating buff that summons a Golden Retriever with attack and health equal to its count when the team is nearly wiped.",
            "Moustache Score: Daily-mode score, earned by collecting moustaches across battles.",
            "Bullies: The pre-built enemy teams faced in Daily mode.",
        };

        /// <summary>
        /// Builds context-dependent help categories based on current game phase.
        /// </summary>
        private static void BuildHelpCategories()
        {
            _helpCategories.Clear();
            _helpCategoryIndex = 0;
            _helpItemIndex = 0;

            if (Gameplay.GameplayPhaseDetector.IsShopPhase())
            {
                _helpCategories.Add(("Navigation", new List<string>
                {
                    "S: Pet shop",
                    "F: Food shop",
                    "T: Team",
                    "D: Action buttons",
                    "Left and Right arrows: Navigate slots",
                    "Home: First slot",
                    "End: Last slot",
                    "Up and Down arrows: Browse detail lines",
                    "Space: Re-read current detail line"
                }));
                _helpCategories.Add(("Actions", new List<string>
                {
                    "Enter: Buy or select",
                    "X: Sell",
                    "Q: Roll shop",
                    "Z: Freeze or unfreeze",
                    "E: End turn"
                }));
                _helpCategories.Add(("Information", new List<string>
                {
                    "I: Detailed info",
                    "A: Gold",
                    "L: Lives",
                    "N: Turn and tier",
                    "W: Wins",
                    "C: Timer",
                    "R: Full board status",
                    "B: Bones",
                    "O: Scoreboard"
                }));
                _helpCategories.Add(("General", new List<string>
                {
                    "Escape: Pause menu or close overlay",
                    "H: Open or close help"
                }));
                _helpCategories.Add(("Dictionary: Triggers",
                    new List<string>(_keywordDictionaryTriggers)));
                _helpCategories.Add(("Dictionary: Food and Perks",
                    new List<string>(_keywordDictionaryFoodPerks)));
                _helpCategories.Add(("Dictionary: Statuses",
                    new List<string>(_keywordDictionaryStatuses)));
                _helpCategories.Add(("Dictionary: Game Mechanics",
                    new List<string>(_keywordDictionaryMechanics)));
            }
            else if (Gameplay.GameplayPhaseDetector.IsBattlePhase())
            {
                _helpCategories.Add(("Battle Controls", new List<string>
                {
                    "P: Pause or resume",
                    "G: Fast forward on or off",
                    "K: Skip battle",
                    "Space: Repeat last announcement"
                }));
                _helpCategories.Add(("General", new List<string>
                {
                    "R: Read context",
                    "Escape: Pause menu",
                    "H: Open or close help"
                }));
                _helpCategories.Add(("Dictionary: Triggers",
                    new List<string>(_keywordDictionaryTriggers)));
                _helpCategories.Add(("Dictionary: Food and Perks",
                    new List<string>(_keywordDictionaryFoodPerks)));
                _helpCategories.Add(("Dictionary: Statuses",
                    new List<string>(_keywordDictionaryStatuses)));
                _helpCategories.Add(("Dictionary: Game Mechanics",
                    new List<string>(_keywordDictionaryMechanics)));
            }
            else
            {
                _helpCategories.Add(("Menu Navigation", new List<string>
                {
                    "Tab or Page Down: Next section",
                    "Shift Tab or Page Up: Previous section",
                    "Up and Down arrows: Navigate items",
                    "Enter: Activate",
                    "Escape: Back or menu"
                }));
                _helpCategories.Add(("Information", new List<string>
                {
                    "R: Read current context",
                    "B: Bones"
                }));
                _helpCategories.Add(("General", new List<string>
                {
                    "H: Open or close help"
                }));
                _helpCategories.Add(("Dictionary: Triggers",
                    new List<string>(_keywordDictionaryTriggers)));
                _helpCategories.Add(("Dictionary: Food and Perks",
                    new List<string>(_keywordDictionaryFoodPerks)));
                _helpCategories.Add(("Dictionary: Statuses",
                    new List<string>(_keywordDictionaryStatuses)));
                _helpCategories.Add(("Dictionary: Game Mechanics",
                    new List<string>(_keywordDictionaryMechanics)));
            }
        }

        /// <summary>
        /// Opens the help overlay. Builds categories and announces the first item.
        /// </summary>
        private static void OpenHelpOverlay()
        {
            BuildHelpCategories();
            if (_helpCategories.Count == 0) return;

            _helpOverlayActive = true;
            _helpOverlaySkipFrame = true; // Skip one frame to avoid H key closing immediately
            _helpOpenedInPhase = Gameplay.GameplayPhaseDetector.CurrentPhase;
            Gameplay.ShopNavigationManager.IsInputSuppressed = true;

            var cat = _helpCategories[0];
            string firstItem = cat.items.Count > 0 ? cat.items[0] : "";
            AccessibilityManager.Announce(
                $"Help. {cat.category}. {firstItem}. " +
                $"Tab or Page Down to switch categories, Shift Tab or Page Up for previous, Up and Down to browse, Escape to close.");
            MelonLogger.Msg($"[Help] Opened with {_helpCategories.Count} categories");
        }

        /// <summary>
        /// Closes the help overlay and restores normal input.
        /// </summary>
        private static void CloseHelpOverlay()
        {
            _helpOverlayActive = false;
            Gameplay.ShopNavigationManager.IsInputSuppressed = false;
            AccessibilityManager.Announce("Help closed");
            MelonLogger.Msg("[Help] Closed");
        }

        /// <summary>
        /// Handles input when the help overlay is active.
        /// Tab/Shift+Tab = switch category, Up/Down = navigate items, Space = re-read, Escape/H = close.
        /// </summary>
        private static void HandleHelpOverlayInput()
        {
            // Skip one frame after opening to prevent H key from immediately closing
            if (_helpOverlaySkipFrame)
            {
                _helpOverlaySkipFrame = false;
                return;
            }

            if (_helpCategories.Count == 0)
            {
                CloseHelpOverlay();
                return;
            }

            // Close with Escape or H
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.H))
            {
                CloseHelpOverlay();
                return;
            }

            // Switch category with Tab / Shift+Tab / PageDown / PageUp
            bool tab = Input.GetKeyDown(KeyCode.Tab);
            bool pageDown = Input.GetKeyDown(KeyCode.PageDown);
            bool pageUp = Input.GetKeyDown(KeyCode.PageUp);
            if (tab || pageDown || pageUp)
            {
                bool shift = pageUp || (tab && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)));
                if (shift)
                    _helpCategoryIndex--;
                else
                    _helpCategoryIndex++;

                // Wrap around
                if (_helpCategoryIndex < 0) _helpCategoryIndex = _helpCategories.Count - 1;
                if (_helpCategoryIndex >= _helpCategories.Count) _helpCategoryIndex = 0;

                _helpItemIndex = 0;
                var cat = _helpCategories[_helpCategoryIndex];
                string firstItem = cat.items.Count > 0 ? cat.items[0] : "";
                string pos = $"category {_helpCategoryIndex + 1} of {_helpCategories.Count}";
                AccessibilityManager.Announce($"{cat.category}. {firstItem}. {pos}");
                return;
            }

            // Navigate items with Up/Down
            var currentCat = _helpCategories[_helpCategoryIndex];
            if (currentCat.items.Count == 0) return;

            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                _helpItemIndex++;
                if (_helpItemIndex >= currentCat.items.Count)
                    _helpItemIndex = currentCat.items.Count - 1;

                string item = currentCat.items[_helpItemIndex];
                string pos = $"{_helpItemIndex + 1} of {currentCat.items.Count}";
                AccessibilityManager.Announce($"{item}, {pos}");
            }
            else if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                _helpItemIndex--;
                if (_helpItemIndex < 0) _helpItemIndex = 0;

                string item = currentCat.items[_helpItemIndex];
                string pos = $"{_helpItemIndex + 1} of {currentCat.items.Count}";
                AccessibilityManager.Announce($"{item}, {pos}");
            }
            else if (Input.GetKeyDown(KeyCode.Space))
            {
                // Re-read current item
                if (_helpItemIndex < currentCat.items.Count)
                    AccessibilityManager.Announce(currentCat.items[_helpItemIndex]);
            }
        }

        /// <summary>
        /// Reads the turn timer and announces time remaining.
        /// </summary>
        private static void ReadTurnTimer()
        {
            if (!Gameplay.GameplayPhaseDetector.IsShopPhase())
            {
                AccessibilityManager.Announce("Timer not available outside shop phase");
                return;
            }

            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar == null)
                {
                    AccessibilityManager.Announce("Timer not available");
                    return;
                }

                var timer = hangar.Overlay?.Timer;
                if (timer == null || !timer.IsCounting)
                {
                    AccessibilityManager.Announce("No active timer");
                    return;
                }

                int totalSeconds = timer.SecondsDelta;
                if (totalSeconds <= 0)
                {
                    AccessibilityManager.Announce("Timer expired");
                    return;
                }

                int minutes = totalSeconds / 60;
                int seconds = totalSeconds % 60;

                string timeStr;
                if (minutes > 0)
                    timeStr = $"{minutes} minute{(minutes != 1 ? "s" : "")} {seconds} second{(seconds != 1 ? "s" : "")} remaining";
                else
                    timeStr = $"{seconds} second{(seconds != 1 ? "s" : "")} remaining";

                AccessibilityManager.Announce(timeStr);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"ReadTurnTimer error: {ex.Message}");
                AccessibilityManager.Announce("Timer not available");
            }
        }

        /// <summary>
        /// Polls for IconAlert popups (turn milestones, tier unlocks, life gain/loss).
        /// These can appear back-to-back, so we don't force focus elsewhere on close.
        /// </summary>
        private static void PollIconAlert()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.IconAlert iconAlert = null;
                try
                {
                    iconAlert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.IconAlert>();
                    if (iconAlert != null && iconAlert.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastIconAlertOpen)
                {
                    // IconAlert just appeared â€” read its text and take exclusive focus
                    // Clear competing exclusive flags to prevent input routing conflicts
                    _iconAlertActive = true;
                    AccessibilityManager.CancelAllPending();
                    _alert2Active = false;
                    _tierOverlayActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;

                    string message = "";
                    try
                    {
                        string above = "";
                        string below = "";
                        try
                        {
                            if (iconAlert.TextMeshAbove != null && !string.IsNullOrWhiteSpace(iconAlert.TextMeshAbove.text))
                                above = iconAlert.TextMeshAbove.text.Trim();
                        }
                        catch { }
                        try
                        {
                            if (iconAlert.TextMeshBelow != null && !string.IsNullOrWhiteSpace(iconAlert.TextMeshBelow.text))
                                below = iconAlert.TextMeshBelow.text.Trim();
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(above) && !string.IsNullOrEmpty(below))
                            message = $"{above} {below}";
                        else if (!string.IsNullOrEmpty(above))
                            message = above;
                        else if (!string.IsNullOrEmpty(below))
                            message = below;
                    }
                    catch { }

                    if (string.IsNullOrEmpty(message))
                        message = "Alert";

                    _lastIconAlertText = message;
                    string announcement = $"{message}. Press Enter to continue.";
                    AccessibilityManager.Announce(announcement);
                    MelonLogger.Msg($"IconAlert opened: {message}");
                }
                else if (!isOpen && _lastIconAlertOpen)
                {
                    // IconAlert closed â€” restore input but don't force focus
                    // (another IconAlert may appear immediately)
                    _iconAlertActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    MelonLogger.Msg("IconAlert closed");
                }
                _lastIconAlertOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Polls for end-turn confirmation popup (Dock.ConfirmButton visibility).
        /// Distinguishes from name picker mode (dock.Adjectives populated).
        /// When detected, announces the prompt text and suppresses shop input.
        /// </summary>
        private static void PollEndTurnConfirm()
        {
            try
            {
                bool confirmVisible = false;
                Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock dock = null;

                try
                {
                    dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
                    if (dock != null && dock.ConfirmButton != null &&
                        dock.ConfirmButton.gameObject != null &&
                        dock.ConfirmButton.gameObject.activeInHierarchy)
                    {
                        // Only treat as confirm popup if NOT in name picker mode
                        bool isNamePicker = false;
                        try
                        {
                            if (dock.Adjectives != null && dock.Adjectives.Count > 0)
                            {
                                var adjContainer = dock.AdjectiveContainer;
                                if (adjContainer != null && adjContainer.gameObject.activeInHierarchy)
                                    isNamePicker = true;
                            }
                        }
                        catch { }

                        if (!isNamePicker)
                            confirmVisible = true;
                    }
                }
                catch { }

                if (confirmVisible && !_lastConfirmVisible)
                {
                    // Confirm button just appeared â€” read text and suppress shop input
                    _confirmPopupActive = true;
                    _confirmFocusOnConfirm = true;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;

                    string message = "Confirm? Confirm button focused. Arrow keys to switch, Enter to activate, Escape to cancel.";
                    try
                    {
                        if (dock.CtaTextMesh != null && !string.IsNullOrWhiteSpace(dock.CtaTextMesh.text))
                        {
                            string txt = dock.CtaTextMesh.text.Trim();
                            if (txt.Length > 1)
                                message = $"{txt}. Confirm button focused. Arrow keys to switch, Enter to activate, Escape to cancel.";
                        }
                    }
                    catch { }

                    AccessibilityManager.Announce(message);

                    // Focus the confirm button so screen readers can discover it
                    try
                    {
                        if (dock.ConfirmButton?.gameObject != null && EventSystem.current != null)
                            EventSystem.current.SetSelectedGameObject(dock.ConfirmButton.gameObject);
                    }
                    catch { }
                }
                else if (!confirmVisible && _lastConfirmVisible)
                {
                    // Confirm disappeared â€” restore shop input
                    _confirmPopupActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    Gameplay.ShopNavigationManager.ResetTurnEnded(); // Reset in case popup was dismissed without confirming
                }
                _lastConfirmVisible = confirmVisible;
            }
            catch { }
        }

        /// <summary>
        /// Polls for Alert2 confirmation dialogs (e.g., "replace held food with same food?").
        /// Alert2 is a separate MonoBehaviour from Modal, with its own IsOpen/Confirm/Cancel API.
        /// </summary>
        private static void PollAlert2()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.Alert2 alert = null;
                try
                {
                    alert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Alert2>();
                    if (alert != null && alert.IsOpen)
                        isOpen = true;
                }
                catch { }

                // Process deferred Alert2 focus â€” wait for Enter to release before focusing Cancel
                if (_pendingAlert2FocusFrames > 0 && isOpen)
                {
                    _pendingAlert2FocusFrames--;
                    if (_pendingAlert2FocusFrames == 0)
                    {
                        try
                        {
                            if (alert?.CancelButton?.gameObject != null && EventSystem.current != null)
                                EventSystem.current.SetSelectedGameObject(alert.CancelButton.gameObject);
                        }
                        catch { }
                    }
                }

                if (isOpen && !_lastAlert2Open)
                {
                    // Alert just opened â€” announce dialog text, then auto-focus Cancel with position
                    _alert2Active = true;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;
                    AccessibilityManager.CancelPendingTooltip();

                    string dialogText = "Dialog";
                    try
                    {
                        // Try reading the TextMesh first (rendered text)
                        if (alert.TextMesh != null && !string.IsNullOrWhiteSpace(alert.TextMesh.text))
                        {
                            string txt = alert.TextMesh.text.Trim();
                            if (txt.Length > 1)
                                dialogText = $"{txt}. Dialog";
                        }
                        // Fallback to model text
                        else if (alert.Model != null && !string.IsNullOrEmpty(alert.Model.Text))
                        {
                            string txt = alert.Model.Text.Trim();
                            if (txt.Length > 1)
                                dialogText = $"{txt}. Dialog";
                        }
                    }
                    catch { }

                    // Announce dialog text, then auto-focused button with position
                    AccessibilityManager.Announce($"{dialogText}. Cancel, button, 1 of 2");

                    // Set input cooldown â€” prevents Enter carry-over from the button
                    // that opened this dialog. The cooldown handler deselects while
                    // Enter is held, blocking Unity's InputModule Submit.
                    _alert2InputCooldown = 5;

                    // Defer focus to Cancel button after Enter has time to release
                    _pendingAlert2FocusFrames = 8;
                }
                else if (!isOpen && _lastAlert2Open)
                {
                    // Alert closed â€” restore shop input
                    _alert2Active = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                }
                _lastAlert2Open = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Polls for the DesyncAlert overlay (desync error during a match).
        /// DesyncAlert is a MonoSingleton shown when a hash mismatch is detected.
        /// Announces the error and focuses the Reload button for keyboard navigation.
        /// </summary>
        private static void PollDesyncAlert()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.DesyncAlert desyncAlert = null;
                try
                {
                    desyncAlert = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.DesyncAlert>();
                    if (desyncAlert != null && desyncAlert.IsActive)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastDesyncAlertOpen)
                {
                    // DesyncAlert just opened â€” announce and suppress shop input
                    AccessibilityManager.CancelAllPending();
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;

                    // Read any visible text from child TMP elements
                    string alertText = "Desync error";
                    try
                    {
                        var tmpTexts = desyncAlert.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false);
                        if (tmpTexts != null)
                        {
                            var textParts = new System.Collections.Generic.List<string>();
                            for (int i = 0; i < tmpTexts.Count; i++)
                            {
                                var tmp = tmpTexts[i];
                                if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                                {
                                    string txt = tmp.text.Trim();
                                    // Skip button labels (Reload/Menu/Abandon) â€” they'll be in sections
                                    if (txt != "Reload" && txt != "Menu" && txt != "Abandon" && txt.Length > 1)
                                        textParts.Add(txt);
                                }
                            }
                            if (textParts.Count > 0)
                                alertText = string.Join(". ", textParts);
                        }
                    }
                    catch { }

                    AccessibilityManager.Announce($"{alertText}. Reload, button, 1 of 3");
                    SectionManager.InvalidateCache();

                    // Focus the Reload button
                    try
                    {
                        if (desyncAlert.ReloadButton?.gameObject != null && EventSystem.current != null)
                            EventSystem.current.SetSelectedGameObject(desyncAlert.ReloadButton.gameObject);
                    }
                    catch { }

                    MelonLogger.Msg("[DesyncAlert] Desync alert opened and announced");
                }
                else if (!isOpen && _lastDesyncAlertOpen)
                {
                    // DesyncAlert closed â€” restore input
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("[DesyncAlert] Desync alert closed");
                }
                _lastDesyncAlertOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Polls for the TallyArena screen (battle result: win/loss/draw with trophies and hearts).
        /// TallyArena is a MonoBehaviour shown after each battle in arena mode.
        /// </summary>
        private static void PollTallyArena()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.TallyArena tally = null;
                try
                {
                    tally = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArena>();
                    if (tally != null && tally.gameObject.activeInHierarchy)
                    {
                        // Check if it's waiting for user to continue
                        try
                        {
                            if (tally.WaitingForContinue)
                                isOpen = true;
                        }
                        catch
                        {
                            // Fallback: check if the button is active
                            try
                            {
                                if (tally.Button != null && tally.Button.gameObject.activeInHierarchy)
                                    isOpen = true;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (isOpen && !_lastTallyArenaOpen)
                {
                    // TallyArena just appeared â€” announce surviving pets first, then result
                    _tallyArenaActive = true;
                    AccessibilityManager.CancelAllPending();

                    // Announce surviving pets from the battle
                    string survivors = Gameplay.BattleNarrator.GetBattleSurvivors();
                    if (!string.IsNullOrEmpty(survivors))
                        AccessibilityManager.Announce(survivors);

                    string resultText = ReadTallyArenaDetails(tally);
                    string announcement = !string.IsNullOrEmpty(resultText)
                        ? $"{resultText}. Press Enter or Space to continue."
                        : "Battle result. Press Enter or Space to continue.";

                    AccessibilityManager.Announce(announcement, interrupt: false);
                    MelonLogger.Msg($"TallyArena opened: {resultText}");

                    // Focus the button if available
                    try
                    {
                        if (tally.Button?.gameObject != null && EventSystem.current != null)
                            EventSystem.current.SetSelectedGameObject(tally.Button.gameObject);
                    }
                    catch { }
                }
                else if (!isOpen && _lastTallyArenaOpen)
                {
                    _tallyArenaActive = false;
                    SectionManager.InvalidateCache();
                }
                _lastTallyArenaOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Reads TallyArena details â€” outcome, trophies, and lives.
        /// </summary>
        private static string ReadTallyArenaDetails(Il2CppSpacewood.Unity.TallyArena tally)
        {
            if (tally == null) return null;

            try
            {
                var parts = new System.Collections.Generic.List<string>();
                string tallyOutcomeStr = "";

                // Read outcome
                try
                {
                    var outcome = tally.Outcome;
                    tallyOutcomeStr = outcome.ToString();
                    if (tallyOutcomeStr == "PlayerWon")
                        parts.Add("Victory");
                    else if (tallyOutcomeStr == "EnemyWon")
                        parts.Add("Defeat");
                    else if (tallyOutcomeStr == "Draw")
                        parts.Add("Draw");
                    else if (tallyOutcomeStr == "TimeoutDraw")
                        parts.Add("Timeout draw");
                    else
                        parts.Add(tallyOutcomeStr);
                }
                catch
                {
                    // Fallback: try reading the Status text
                    try
                    {
                        if (tally.Status != null && !string.IsNullOrWhiteSpace(tally.Status.text))
                            parts.Add(tally.Status.text.Trim());
                        else
                            parts.Add("Battle result");
                    }
                    catch { parts.Add("Battle result"); }
                }

                // Read model for trophies and lives
                try
                {
                    var model = tally.Model;
                    if (model != null)
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

                            if (isBullyRush)
                            {
                                // Daily mode (BullyRush) scores by moustaches, not trophies.
                                // The HUD shows MoustachesCollected as a running total. The
                                // model.Victories/VictoriesMax fields don't map to anything
                                // the user sees on the screen.
                                int moustaches = 0;
                                try { moustaches = model.Board?.MoustachesCollected ?? 0; }
                                catch { }
                                if (moustaches > 0)
                                    parts.Add(moustaches == 1
                                        ? "1 moustache"
                                        : $"{moustaches} moustaches");
                            }
                            else
                            {
                                int victories = model.Victories;
                                int victoriesMax = model.VictoriesMax;
                                // Victories hasn't been incremented yet when TallyArena appears after a win
                                if (tallyOutcomeStr == "PlayerWon") victories++;
                                if (victoriesMax > 0)
                                    parts.Add($"{victories} of {victoriesMax} trophies");
                                else
                                    parts.Add($"{victories} trophies");
                            }
                        }
                        catch { }

                        try
                        {
                            int lives = model.Lives;
                            int livesMax = model.LivesMax;
                            // Lives haven't been decremented yet when TallyArena appears after a loss
                            if (tallyOutcomeStr == "EnemyWon") lives = System.Math.Max(0, lives - 1);
                            parts.Add($"{lives} of {livesMax} lives");
                        }
                        catch { }

                        // Read enemy team if available
                        try
                        {
                            var enemies = model.Enemies;
                            if (enemies != null && enemies.Count > 0)
                            {
                                var enemyNames = new System.Collections.Generic.List<string>();
                                for (int i = 0; i < enemies.Count; i++)
                                {
                                    try
                                    {
                                        string name = Gameplay.PetStatsReader.SplitCamelCase(enemies[i].ToString());
                                        if (!string.IsNullOrEmpty(name))
                                            enemyNames.Add(name);
                                    }
                                    catch { }
                                }
                                if (enemyNames.Count > 0)
                                    parts.Add($"Enemy remaining team: {string.Join(", ", enemyNames)}");
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch { return null; }
        }

        // =====================================================================
        // DAILY-MODE (BullyRush) MOUSTACHE-SCORE SCREEN: TallyBullyScore
        // =====================================================================

        /// <summary>
        /// Polls for the TallyBullyScore screen (post-battle moustache score with optional
        /// win-bonus). Reads the live TextMeshProUGUI fields the game already populates
        /// (FinalScoreLabel / ScoreAmount / PersonalBestLabel) so we announce whatever the
        /// game itself displays — e.g. "Moustache Score. 13. Plus 5 win bonus."
        /// </summary>
        private static void PollTallyBullyScore()
        {
            try
            {
                var tbs = UnityEngine.Object.FindObjectOfType<
                    Il2CppSpacewood.Unity.TallyBullyScore>();
                bool isOpen = tbs != null && tbs.gameObject.activeInHierarchy;

                if (isOpen && !_lastTallyBullyScoreOpen)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    try
                    {
                        string label = tbs.FinalScoreLabel?.text?.Trim();
                        if (!string.IsNullOrEmpty(label)) parts.Add(label);
                    }
                    catch { }
                    try
                    {
                        string amount = tbs.ScoreAmount?.text?.Trim();
                        if (!string.IsNullOrEmpty(amount)) parts.Add(amount);
                    }
                    catch { }
                    try
                    {
                        string bonus = tbs.PersonalBestLabel?.text?.Trim();
                        if (!string.IsNullOrEmpty(bonus))
                        {
                            // "+5 Win Bonus" reads better as "plus 5 win bonus" through TTS.
                            string spoken = bonus.StartsWith("+")
                                ? "plus " + bonus.Substring(1).Trim()
                                : bonus;
                            parts.Add(spoken);
                        }
                    }
                    catch { }

                    if (parts.Count > 0)
                    {
                        string announcement = string.Join(". ", parts) + ".";
                        AccessibilityManager.Announce(announcement);
                        MelonLogger.Msg($"[TallyBullyScore] Announced: {announcement}");
                    }
                }

                _lastTallyBullyScoreOpen = isOpen;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"PollTallyBullyScore error: {ex.Message}");
            }
        }

        // =====================================================================
        // POST-ARENA SCREENS: TallyArenaFinale, TallyArenaReward, TallyArenaMenu
        // =====================================================================

        /// <summary>
        /// Polls for the TallyArenaFinale screen (end-of-run EXP/bones summary + "Claim reward").
        /// Shown after the final TallyArena result when an arena run ends.
        /// </summary>
        private static void PollTallyArenaFinale()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.TallyArenaFinale finale = null;
                try
                {
                    finale = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaFinale>();
                    if (finale != null && finale.Canvas != null && finale.Canvas.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastTallyFinaleOpen)
                {
                    _tallyFinaleActive = true;
                    AccessibilityManager.CancelAllPending();
                    string announcement = ReadTallyArenaFinaleDetails(finale);
                    if (string.IsNullOrEmpty(announcement))
                        announcement = "Arena run complete.";
                    // Only say "claim reward" if at or past goal
                    bool hasReward = false;
                    try
                    {
                        var snackbar = finale.Snackbar;
                        if (snackbar?.Message != null && snackbar.Message.gameObject.activeInHierarchy &&
                            !string.IsNullOrWhiteSpace(snackbar.Message.text))
                            hasReward = true;
                    }
                    catch { }
                    if (hasReward)
                        announcement += " Press Enter to claim reward.";
                    else
                        announcement += " Press Enter to continue.";
                    AccessibilityManager.Announce(announcement);
                    MelonLogger.Msg($"[TallyArenaFinale] {announcement}");
                }
                else if (!isOpen && _lastTallyFinaleOpen)
                {
                    _tallyFinaleActive = false;
                }
                _lastTallyFinaleOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Reads the TallyArenaFinale summary â€” points earned, progress toward next reward.
        /// </summary>
        private static string ReadTallyArenaFinaleDetails(Il2CppSpacewood.Unity.TallyArenaFinale finale)
        {
            if (finale == null) return null;
            try
            {
                var parts = new System.Collections.Generic.List<string>();

                // Read victories and points
                try
                {
                    int victories = finale.Victories;
                    int points = finale.PointsGained;
                    if (victories > 0)
                        parts.Add($"{victories} wins");
                    if (points > 0)
                        parts.Add($"{points} bones earned");
                }
                catch { }

                // Read snackbar details (EXP bar, sum, goal)
                try
                {
                    var snackbar = finale.Snackbar;
                    if (snackbar != null)
                    {
                        // Sum text (current total)
                        try
                        {
                            if (snackbar.Sum != null && !string.IsNullOrWhiteSpace(snackbar.Sum.text))
                                parts.Add(snackbar.Sum.text.Trim());
                        }
                        catch { }

                        // Goal text (next milestone)
                        try
                        {
                            if (snackbar.Goal != null && !string.IsNullOrWhiteSpace(snackbar.Goal.text))
                                parts.Add($"goal: {snackbar.Goal.text.Trim()}");
                        }
                        catch { }

                        // Message text (e.g., level up message)
                        try
                        {
                            if (snackbar.Message != null && snackbar.Message.gameObject.activeInHierarchy &&
                                !string.IsNullOrWhiteSpace(snackbar.Message.text))
                                parts.Add(snackbar.Message.text.Trim());
                        }
                        catch { }
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Polls for the TallyArenaReward screen (unlocked item: "You unlocked: Lava Cave").
        /// May appear multiple times in sequence for each reward earned.
        /// </summary>
        private static void PollTallyArenaReward()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.TallyArenaReward reward = null;
                try
                {
                    reward = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaReward>();
                    if (reward != null && reward.Canvas != null && reward.Canvas.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastTallyRewardOpen)
                {
                    _tallyRewardActive = true;
                    // Cancel any pending deferred announcements (FocusFirst, modals, pages)
                    // from PageManager.Open or Modal.Awake â€” our custom announcement takes
                    // priority and the deferred focus would interrupt it by announcing
                    // "Continue, button" on a later frame.
                    AccessibilityManager.CancelAllPending();
                    string announcement = ReadTallyArenaRewardDetails(reward);
                    if (string.IsNullOrEmpty(announcement))
                        announcement = "Reward unlocked.";
                    announcement += " Press Enter to continue.";
                    AccessibilityManager.Announce(announcement);
                    MelonLogger.Msg($"[TallyArenaReward] {announcement}");
                }
                else if (!isOpen && _lastTallyRewardOpen)
                {
                    _tallyRewardActive = false;
                }
                _lastTallyRewardOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Reads the TallyArenaReward details â€” reward name and description.
        /// </summary>
        private static string ReadTallyArenaRewardDetails(Il2CppSpacewood.Unity.TallyArenaReward reward)
        {
            if (reward == null) return null;
            try
            {
                var parts = new System.Collections.Generic.List<string>();

                // Read reward label (e.g., "You unlocked: Lava Cave")
                try
                {
                    if (reward.RewardLabel != null && !string.IsNullOrWhiteSpace(reward.RewardLabel.text))
                        parts.Add(reward.RewardLabel.text.Trim());
                }
                catch { }

                // Read product label (footer text, e.g., "Remaining unlockables: 123")
                try
                {
                    if (reward.ProductLabel != null && reward.ProductLabel.gameObject.activeInHierarchy &&
                        !string.IsNullOrWhiteSpace(reward.ProductLabel.text))
                        parts.Add(reward.ProductLabel.text.Trim());
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Polls for the TallyArenaMenu screen (Play again / Return to menu).
        /// Shown after all rewards have been claimed.
        /// </summary>
        private static void PollTallyArenaMenu()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.TallyArenaMenu menu = null;
                try
                {
                    menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.TallyArenaMenu>();
                    if (menu != null && menu.Canvas != null && menu.Canvas.gameObject.activeInHierarchy)
                        isOpen = true;
                }
                catch { }

                if (isOpen && !_lastTallyMenuOpen)
                {
                    _tallyMenuActive = true;
                    AccessibilityManager.CancelAllPending();
                    BuildTallyMenuButtonList(menu);
                    _tallyMenuFocusIndex = 0;

                    // Announce first item with position
                    if (_tallyMenuButtons.Count > 0)
                    {
                        string firstLabel = _tallyMenuButtons[0].label;
                        AccessibilityManager.Announce($"{firstLabel}, {1} of {_tallyMenuButtons.Count}");
                    }
                    else
                    {
                        AccessibilityManager.Announce("Menu");
                    }
                    MelonLogger.Msg($"[TallyArenaMenu] Opened with {_tallyMenuButtons.Count} options");
                }
                else if (!isOpen && _lastTallyMenuOpen)
                {
                    _tallyMenuActive = false;
                    _tallyMenuButtons.Clear();
                }
                _lastTallyMenuOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Builds the list of visible/interactable buttons on TallyArenaMenu.
        /// </summary>
        private static void BuildTallyMenuButtonList(Il2CppSpacewood.Unity.TallyArenaMenu menu)
        {
            _tallyMenuButtons.Clear();
            if (menu == null) return;

            try
            {
                // Check each button: add to list if it exists and its GameObject is active
                TryAddMenuButton(menu.StartNewGameButton, "Play again", () => menu.StartNewGame());
                TryAddMenuButton(menu.ReturnToMenuButton, "Return to menu", () => menu.Return());

                // Difficulty button â€” read its current text for the label
                try
                {
                    var diffBtn = menu.DifficultButton;
                    if (diffBtn != null && diffBtn.gameObject.activeInHierarchy)
                    {
                        string diffLabel = "Difficulty";
                        try
                        {
                            var tmp = diffBtn.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>();
                            if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                                diffLabel = $"Difficulty: {tmp.text.Trim()}";
                        }
                        catch { }
                        _tallyMenuButtons.Add((diffLabel, () => menu.GotoDifficulty()));
                    }
                }
                catch { }

                TryAddMenuButton(menu.SpectateMatchButton, "Spectate match", () => menu.SpectateMatch());
                TryAddMenuButton(menu.WatchBattleButton, "Watch battle", () => menu.WatchBattle());
                TryAddMenuButton(menu.PlaybackOpponentButton, "Playback opponent", () => menu.PlaybackOpponent());
            }
            catch { }
        }

        private static void TryAddMenuButton(SelectableBase button, string label, System.Action action)
        {
            try
            {
                if (button != null && button.gameObject.activeInHierarchy)
                    _tallyMenuButtons.Add((label, action));
            }
            catch { }
        }

        // =====================================================================
        // END POST-ARENA SCREENS
        // =====================================================================

        // Tier overlay tracking
        private static bool _lastLockedTooltip = false;
        private static bool _tierOverlayActive = false;

        /// <summary>
        /// Polls for tier upgrade overlay ("You reached turn X. Tier Y pets unlocked!").
        /// The overlay is shown when HangarOverlay.LockedTooltip becomes true.
        /// </summary>
        private static void PollTierOverlay()
        {
            if (!Gameplay.GameplayPhaseDetector.IsShopPhase()) return;

            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar?.Overlay == null) return;

                bool lockedTooltip = false;
                try { lockedTooltip = hangar.Overlay.LockedTooltip; } catch { }

                if (lockedTooltip && !_lastLockedTooltip)
                {
                    // Tooltip just locked â€” likely tier upgrade overlay
                    _tierOverlayActive = true;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;

                    // Read the board tier for the announcement
                    int tier = 0;
                    try { tier = hangar.Overlay.BoardModel.Tier; } catch { }

                    string message = tier > 0
                        ? $"Tier {tier} pets unlocked! Press Enter to dismiss."
                        : "New tier unlocked! Press Enter to dismiss.";
                    AccessibilityManager.Announce(message);
                    MelonLogger.Msg($"Tier overlay locked tooltip detected: tier {tier}");
                }
                else if (!lockedTooltip && _lastLockedTooltip)
                {
                    // Tooltip unlocked â€” overlay dismissed
                    _tierOverlayActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                }
                _lastLockedTooltip = lockedTooltip;

                // Track tier for reference
                try { _lastAnnouncedTier = hangar.Overlay.BoardModel.Tier; } catch { }
            }
            catch { }
        }

        /// <summary>
        /// Polls for LastBattleMenu overlay opening/closing.
        /// When open, suppresses shop input and provides section-based navigation.
        /// </summary>
        private static void PollLastBattleMenu()
        {
            if (!Gameplay.GameplayPhaseDetector.IsShopPhase()) return;

            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar == null) return;

                bool isOpen = false;
                try
                {
                    if (hangar.LastBattleMenu?.Container?.gameObject != null)
                        isOpen = hangar.LastBattleMenu.Container.gameObject.activeInHierarchy;
                }
                catch { }

                if (isOpen && !_lastBattleMenuOpen)
                {
                    // LastBattleMenu just opened
                    _lastBattleMenuOpen = true;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce(
                        "Battle menu. Use arrows to choose Battle or Playback. Escape to close.");
                    SectionManager.FocusFirstSection();
                    MelonLogger.Msg("LastBattleMenu opened");
                }
                else if (!isOpen && _lastBattleMenuOpen)
                {
                    // LastBattleMenu closed
                    _lastBattleMenuOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("LastBattleMenu closed");
                }
            }
            catch { }
        }

        /// <summary>
        /// Polls for DeckViewer overlay opening/closing.
        /// When open, suppresses shop input and provides tier-based section navigation.
        /// </summary>
        private static void PollDeckViewer()
        {
            if (!Gameplay.GameplayPhaseDetector.IsShopPhase()) return;

            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar == null) return;

                bool isOpen = false;
                try
                {
                    if (hangar.DeckViewer?.gameObject != null)
                        isOpen = hangar.DeckViewer.gameObject.activeInHierarchy;
                }
                catch { }

                if (isOpen && !_deckViewerOpen)
                {
                    // DeckViewer just opened
                    _deckViewerOpen = true;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;
                    SectionManager.InvalidateCache();
                    AccessibilityManager.Announce(
                        "Deck viewer. Tab between tiers, arrows to browse items. Escape to close.");
                    SectionManager.FocusFirstSection();
                    MelonLogger.Msg("DeckViewer opened");
                }
                else if (!isOpen && _deckViewerOpen)
                {
                    // DeckViewer closed
                    _deckViewerOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("DeckViewer closed");
                }
            }
            catch { }
        }

        /// <summary>
        /// Polls for Scoreboard overlay (opponent list) opening/closing.
        /// When open, suppresses shop input and provides section-based navigation.
        /// </summary>
        private static void PollScoreboard()
        {
            if (!Gameplay.GameplayPhaseDetector.IsShopPhase()) return;

            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar == null) return;

                bool isOpen = false;
                try
                {
                    var sbGo = hangar.GetScoreboard();
                    if (sbGo != null)
                        isOpen = sbGo.activeInHierarchy;
                }
                catch { }

                if (isOpen && !_scoreboardOpen)
                {
                    // Scoreboard just opened
                    _scoreboardOpen = true;
                    _scoreboardJustOpened = true; // Prevent O key from closing on same frame
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;
                    SectionManager.InvalidateCache();

                    // Build opponents list for detail line navigation
                    _scoreboardOpponents.Clear();
                    _scoreboardButtons.Clear();
                    _scoreboardOpponentIndex = 0;
                    _scoreboardDetailLines.Clear();
                    _scoreboardDetailLineIndex = 0;

                    try
                    {
                        var entries = hangar.Scoreboard?.Entries;
                        if (entries != null)
                        {
                            for (int i = 0; i < entries.Count; i++)
                            {
                                try
                                {
                                    var e = entries[i];
                                    if (e == null || e.Self) continue;
                                    var opp = e.Opponent;
                                    _scoreboardOpponents.Add(opp); // may be null, handled later
                                    _scoreboardButtons.Add(e.Button);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    if (_scoreboardOpponents.Count == 0)
                    {
                        // No opponents â€” close scoreboard and announce
                        try
                        {
                            var sbGo2 = hangar.GetScoreboard();
                            if (sbGo2 != null) sbGo2.SetActive(false);
                        }
                        catch { }
                        _scoreboardOpen = false;
                        Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                        AccessibilityManager.Announce("No opponents");
                        MelonLogger.Msg("Scoreboard: No opponents, closed immediately");
                    }
                    else
                    {
                        // Build detail lines for first opponent
                        BuildScoreboardDetailLines();

                        int oppCount = _scoreboardOpponents.Count;
                        string firstLine = _scoreboardDetailLines.Count > 0 ? _scoreboardDetailLines[0] : "Unknown";
                        AccessibilityManager.Announce(
                            $"Scoreboard. {oppCount} opponent{(oppCount != 1 ? "s" : "")}. " +
                            $"{firstLine}. " +
                            "Left/Right to browse opponents, Up/Down for details. Escape to close.");

                        // Focus the first opponent's button for visual highlight
                        if (_scoreboardButtons.Count > 0 && _scoreboardButtons[0] != null &&
                            EventSystem.current != null)
                        {
                            EventSystem.current.SetSelectedGameObject(_scoreboardButtons[0].gameObject);
                        }

                        MelonLogger.Msg("Scoreboard opened");
                    }
                }
                else if (!isOpen && _scoreboardOpen)
                {
                    // Scoreboard closed
                    _scoreboardOpen = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("Scoreboard closed");
                }
            }
            catch { }
        }

        /// <summary>
        /// Builds detail lines for the currently selected scoreboard opponent.
        /// Uses DetailLineProvider.BuildOpponentDetailLines for full pet-by-pet breakdown.
        /// </summary>
        private static void BuildScoreboardDetailLines()
        {
            _scoreboardDetailLines.Clear();
            _scoreboardDetailLineIndex = 0;

            if (_scoreboardOpponentIndex < 0 || _scoreboardOpponentIndex >= _scoreboardOpponents.Count)
                return;

            var opponent = _scoreboardOpponents[_scoreboardOpponentIndex];
            _scoreboardDetailLines = Gameplay.DetailLineProvider.BuildOpponentDetailLines(opponent);
        }

        /// <summary>
        /// Navigates between opponents in the scoreboard (Left/Right).
        /// Rebuilds detail lines and announces the first line (opponent name).
        /// </summary>
        private static void MoveScoreboardOpponent(int direction)
        {
            if (_scoreboardOpponents.Count == 0) return;

            int newIndex = _scoreboardOpponentIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _scoreboardOpponents.Count) newIndex = _scoreboardOpponents.Count - 1;

            if (newIndex == _scoreboardOpponentIndex && direction != 0)
            {
                // At boundary â€” re-read current opponent's first line
                if (_scoreboardDetailLines.Count > 0)
                {
                    string posInfo = $"{_scoreboardOpponentIndex + 1} of {_scoreboardOpponents.Count}. ";
                    AccessibilityManager.Announce(posInfo + _scoreboardDetailLines[0]);
                }
                return;
            }

            _scoreboardOpponentIndex = newIndex;
            BuildScoreboardDetailLines();

            // Update visual focus on the opponent's button
            if (_scoreboardButtons.Count > newIndex && _scoreboardButtons[newIndex] != null &&
                EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(_scoreboardButtons[newIndex].gameObject);
            }

            // Announce first line with position context
            if (_scoreboardDetailLines.Count > 0)
            {
                string posInfo = $"{_scoreboardOpponentIndex + 1} of {_scoreboardOpponents.Count}. ";
                AccessibilityManager.Announce(posInfo + _scoreboardDetailLines[0]);
            }
        }

        /// <summary>
        /// Navigates detail lines for the current scoreboard opponent (Up/Down).
        /// </summary>
        private static void MoveScoreboardDetailLine(int direction)
        {
            if (_scoreboardDetailLines.Count == 0) return;

            int newIndex = _scoreboardDetailLineIndex + direction;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= _scoreboardDetailLines.Count) newIndex = _scoreboardDetailLines.Count - 1;

            if (newIndex == _scoreboardDetailLineIndex && direction != 0)
            {
                // At boundary â€” re-read current line
                if (_scoreboardDetailLineIndex < _scoreboardDetailLines.Count)
                    AccessibilityManager.Announce(_scoreboardDetailLines[_scoreboardDetailLineIndex]);
                return;
            }

            _scoreboardDetailLineIndex = newIndex;
            AccessibilityManager.Announce(_scoreboardDetailLines[_scoreboardDetailLineIndex]);
        }

        // Track VersusLobby player count for polling
        private static int _versusLobbyLastPlayerCount = -1;
        private static bool _versusLobbyActive = false;

        /// <summary>
        /// Polls for VersusLobby player list changes (joins and leaves).
        /// Periodically checks the player count and triggers section rebuild + announcement on change.
        /// </summary>
        private static void PollVersusLobby()
        {
            try
            {
                var versusLobby = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.VersusLobby>();
                if (versusLobby == null || versusLobby.gameObject == null || !versusLobby.gameObject.activeInHierarchy)
                {
                    if (_versusLobbyActive)
                    {
                        _versusLobbyActive = false;
                        _versusLobbyLastPlayerCount = -1;
                        SectionManager._lastLobbyPlayerCount = 0;
                    }
                    return;
                }

                if (!_versusLobbyActive)
                {
                    _versusLobbyActive = true;
                }

                // Count active players
                int currentCount = 0;
                try
                {
                    var items = versusLobby.Items;
                    if (items != null)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            try
                            {
                                var item = items[i];
                                if (item?.backgroundButton?.gameObject != null &&
                                    item.backgroundButton.gameObject.activeInHierarchy)
                                    currentCount++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (_versusLobbyLastPlayerCount >= 0 && currentCount != _versusLobbyLastPlayerCount)
                {
                    // Player count changed â€” invalidate sections so they rebuild with new player list
                    SectionManager.InvalidateCache();

                    if (currentCount > _versusLobbyLastPlayerCount)
                    {
                        // Try to find the new player's name
                        string newPlayerName = "";
                        try
                        {
                            var items = versusLobby.Items;
                            if (items != null && currentCount > 0)
                            {
                                var lastItem = items[currentCount - 1];
                                if (lastItem != null)
                                {
                                    var nameText = lastItem.GetComponentInChildren<Il2CppTMPro.TMP_Text>();
                                    if (nameText != null && !string.IsNullOrEmpty(nameText.text))
                                        newPlayerName = nameText.text.Trim();
                                }
                            }
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(newPlayerName))
                            AccessibilityManager.Announce($"{newPlayerName} joined. {currentCount} players in lobby.", interrupt: false);
                        else
                            AccessibilityManager.Announce($"Player joined. {currentCount} players in lobby.", interrupt: false);
                    }
                    else
                    {
                        AccessibilityManager.Announce($"Player left. {currentCount} players in lobby.", interrupt: false);
                    }

                    MelonLogger.Msg($"[VersusLobby] Player count changed: {_versusLobbyLastPlayerCount} -> {currentCount}");
                }

                _versusLobbyLastPlayerCount = currentCount;
                // Keep SectionManager in sync
                SectionManager._lastLobbyPlayerCount = currentCount;
            }
            catch { }
        }

        /// <summary>
        /// Polls for the Dock name picker (adjective/noun team name selector).
        /// The Dock component is dual-purpose: it shows both the end-turn confirm button
        /// AND the name picker. When the name picker is active, dock.Adjectives and dock.Nouns
        /// are populated and their containers are visible.
        /// </summary>
        private static void PollDockNamePicker()
        {
            try
            {
                bool isOpen = false;
                Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock dock = null;
                try
                {
                    dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
                    if (dock != null && dock.Adjectives != null && dock.Adjectives.Count > 0
                        && dock.Nouns != null && dock.Nouns.Count > 0)
                    {
                        var adjContainer = dock.AdjectiveContainer;
                        if (adjContainer != null && adjContainer.gameObject.activeInHierarchy)
                            isOpen = true;
                    }
                }
                catch { }

                if (isOpen && !_lastNamePickerOpen)
                {
                    // Name picker just opened
                    _namePickerActive = true;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = true;
                    _namePickerAdjectiveIndex = 0;
                    _namePickerNounIndex = 0;
                    _namePickerOnAdjectives = true;
                    _namePickerAdjectiveSelected = false;
                    _namePickerNounSelected = false;
                    _namePickerOnConfirm = false;

                    string currentAdj = GetDockBoxText(dock.Adjective);
                    string currentNoun = GetDockBoxText(dock.Noun);

                    AccessibilityManager.Announce(
                        $"Team name picker. Current name: {currentAdj} {currentNoun}. " +
                        "Left and Right to browse, Up and Down to switch between adjectives and nouns, " +
                        "Enter to select.");

                    // Announce first item
                    AnnounceCurrentDockBox(dock);
                    MelonLogger.Msg("Name picker opened");
                }
                else if (!isOpen && _lastNamePickerOpen)
                {
                    // Name picker closed
                    _namePickerActive = false;
                    _confirmPopupActive = false;
                    Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                    SectionManager.InvalidateCache();
                    MelonLogger.Msg("Name picker closed");
                }
                _lastNamePickerOpen = isOpen;
            }
            catch { }
        }

        /// <summary>
        /// Handles keyboard input for the name picker (adjective/noun DockBox navigation).
        /// </summary>
        private static void HandleNamePickerInput()
        {
            var dock = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock>();
            if (dock == null || dock.Adjectives == null || dock.Nouns == null)
            {
                _namePickerActive = false;
                _namePickerOnConfirm = false;
                Gameplay.ShopNavigationManager.IsInputSuppressed = false;
                return;
            }

            // When on confirm button: Enter confirms, arrows/Tab go back to editing
            if (_namePickerOnConfirm)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    // Confirm the name
                    try
                    {
                        var confirmBtn = dock.ConfirmButton;
                        if (confirmBtn != null)
                        {
                            MelonLogger.Msg($"[NamePicker] Confirming. Button active={confirmBtn.gameObject?.activeInHierarchy}, interactable={confirmBtn.Interactable}");
                            dock.HandleConfirm(confirmBtn);
                            AccessibilityManager.Announce("Name confirmed");
                        }
                        else
                        {
                            MelonLogger.Warning("[NamePicker] ConfirmButton is null");
                            AccessibilityManager.Announce("Cannot confirm. Button not found.");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning($"Name picker confirm error: {ex.Message}");
                    }
                }
                else if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow) ||
                         Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow) ||
                         Input.GetKeyDown(KeyCode.Tab))
                {
                    // Go back to editing
                    _namePickerOnConfirm = false;
                    AnnounceCurrentDockBox(dock);
                }
                else
                {
                    return; // Trap other input while on confirm
                }
                return;
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                if (_namePickerOnAdjectives)
                {
                    int count = dock.Adjectives.Count;
                    if (count > 0)
                        _namePickerAdjectiveIndex = (_namePickerAdjectiveIndex - 1 + count) % count;
                }
                else
                {
                    int count = dock.Nouns.Count;
                    if (count > 0)
                        _namePickerNounIndex = (_namePickerNounIndex - 1 + count) % count;
                }
                AnnounceCurrentDockBox(dock);
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                if (_namePickerOnAdjectives)
                {
                    int count = dock.Adjectives.Count;
                    if (count > 0)
                        _namePickerAdjectiveIndex = (_namePickerAdjectiveIndex + 1) % count;
                }
                else
                {
                    int count = dock.Nouns.Count;
                    if (count > 0)
                        _namePickerNounIndex = (_namePickerNounIndex + 1) % count;
                }
                AnnounceCurrentDockBox(dock);
            }
            else if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow))
            {
                _namePickerOnAdjectives = !_namePickerOnAdjectives;
                AnnounceCurrentDockBox(dock);
            }
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                // Select the currently highlighted DockBox
                try
                {
                    if (_namePickerOnAdjectives && _namePickerAdjectiveIndex < dock.Adjectives.Count)
                    {
                        var box = dock.Adjectives[_namePickerAdjectiveIndex];
                        dock.HandleAdjective(box);
                        string text = GetDockBoxText(box);
                        _namePickerAdjectiveSelected = true;
                        AccessibilityManager.Announce($"Selected adjective: {text}");
                    }
                    else if (!_namePickerOnAdjectives && _namePickerNounIndex < dock.Nouns.Count)
                    {
                        var box = dock.Nouns[_namePickerNounIndex];
                        dock.HandleNoun(box);
                        string text = GetDockBoxText(box);
                        _namePickerNounSelected = true;
                        AccessibilityManager.Announce($"Selected noun: {text}");
                    }

                    // After both are selected, move to confirm button
                    if (_namePickerAdjectiveSelected && _namePickerNounSelected)
                    {
                        string currentAdj = GetDockBoxText(dock.Adjective);
                        string currentNoun = GetDockBoxText(dock.Noun);
                        _namePickerOnConfirm = true;
                        AccessibilityManager.Announce(
                            $"Team name: {currentAdj} {currentNoun}. Confirm, button",
                            interrupt: false);
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning($"Name picker select error: {ex.Message}");
                }
            }
            // Escape does nothing in name picker â€” user must confirm their name
        }

        /// <summary>
        /// Gets the display text from a DockBox element.
        /// </summary>
        private static string GetDockBoxText(Il2CppSpacewood.Unity.MonoBehaviours.Build.DockBox box)
        {
            if (box == null) return "unknown";
            try
            {
                string text = box.TextMesh?.text?.Trim();
                if (!string.IsNullOrEmpty(text)) return text;
                string model = box.Model;
                if (!string.IsNullOrEmpty(model)) return model;
            }
            catch { }
            return "unknown";
        }

        /// <summary>
        /// Announces the currently browsed DockBox in the name picker.
        /// </summary>
        private static void AnnounceCurrentDockBox(Il2CppSpacewood.Unity.MonoBehaviours.Build.Dock dock)
        {
            try
            {
                string row = _namePickerOnAdjectives ? "Adjective" : "Noun";
                var list = _namePickerOnAdjectives ? dock.Adjectives : dock.Nouns;
                int index = _namePickerOnAdjectives ? _namePickerAdjectiveIndex : _namePickerNounIndex;

                if (list == null || index >= list.Count) return;

                string text = GetDockBoxText(list[index]);
                int pos = index + 1;
                int total = list.Count;
                AccessibilityManager.Announce($"{row}: {text}, {pos} of {total}");
            }
            catch { }
        }

        /// <summary>
        /// Gets all focusable elements in hierarchy order (depth-first traversal).
        /// This preserves the developer's intended layout order (sibling index).
        /// Used as fallback by SectionManager for non-mapped pages.
        /// </summary>
        internal static List<SelectableBase> GetFocusableElements()
        {
            var result = new List<SelectableBase>();

            // Find the current page's root to limit the search scope
            GameObject searchRoot = null;
            try
            {
                var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                if (pageManager?.CurrentPage?.gameObject != null)
                    searchRoot = pageManager.CurrentPage.gameObject;
            }
            catch { }

            if (searchRoot != null)
            {
                // Depth-first traversal of the page hierarchy
                CollectFocusableElements(searchRoot.transform, result);
            }
            else
            {
                // Absolute fallback: use FindObjectsOfType (unordered) and sort by screen position
                var all = UnityEngine.Object.FindObjectsOfType<SelectableBase>();
                for (int i = 0; i < all.Length; i++)
                {
                    var s = all[i];
                    if (s == null || s.gameObject == null || !s.gameObject.activeInHierarchy || !s.GetInteractable())
                        continue;
                    if (string.IsNullOrEmpty(TextExtractor.GetElementText(s.gameObject)))
                        continue;
                    // Skip elements inside Picker popup
                    try
                    {
                        if (s.GetComponentInParent<Il2CppSpacewood.Unity.UI.Picker>() != null) continue;
                        if (s.GetComponentInParent<Il2CppSpacewood.Unity.UI.PickerButton>() != null) continue;
                    }
                    catch { }
                    // Skip ShowcaseButtons (icon-only preview buttons on product shop items)
                    try
                    {
                        var parentShopItem = s.GetComponentInParent<Il2CppSpacewood.Unity.ProductShopItemShared>();
                        if (parentShopItem != null && parentShopItem.ShowcaseButton != null &&
                            s.gameObject == parentShopItem.ShowcaseButton.gameObject)
                            continue;
                    }
                    catch { }
                    result.Add(s);
                }
                result.Sort((a, b) =>
                {
                    var posA = GetScreenPos(a);
                    var posB = GetScreenPos(b);
                    int cmp = posB.y.CompareTo(posA.y);
                    if (cmp != 0) return cmp;
                    return posA.x.CompareTo(posB.x);
                });
            }

            return result;
        }

        /// <summary>
        /// Depth-first traversal to collect focusable elements in hierarchy order.
        /// Skips elements that belong to the Picker popup (PickerButton children).
        /// </summary>
        private static void CollectFocusableElements(Transform parent, List<SelectableBase> result)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy) continue;

                // Skip Picker container and its children (they pollute fallback sections)
                try
                {
                    if (child.GetComponent<Il2CppSpacewood.Unity.UI.Picker>() != null) continue;
                    if (child.GetComponent<Il2CppSpacewood.Unity.UI.PickerButton>() != null) continue;
                }
                catch { }

                // Skip ShowcaseButtons (icon-only preview buttons on product shop items)
                try
                {
                    var shopItem = child.GetComponent<Il2CppSpacewood.Unity.ProductShopItemShared>();
                    if (shopItem != null && shopItem.ShowcaseButton != null &&
                        child.gameObject == shopItem.ShowcaseButton.gameObject)
                        continue;
                }
                catch { }

                var selectable = child.GetComponent<SelectableBase>();
                if (selectable != null && selectable.GetInteractable())
                {
                    // Skip elements with no text (invisible/icon-only buttons like modal backdrop)
                    if (!string.IsNullOrEmpty(TextExtractor.GetElementText(selectable.gameObject)))
                    {
                        result.Add(selectable);
                    }
                }

                // Recurse into children
                CollectFocusableElements(child, result);
            }
        }

        /// <summary>
        /// Sets a cooldown after page transitions to prevent Enter key carry-over.
        /// Called from PageManagerPatches.Open_Postfix.
        /// </summary>
        internal static void SetPageTransitionCooldown()
        {
            _pageTransitionCooldown = 10; // ~10 frames cooldown
        }

        internal static Vector2 GetScreenPos(SelectableBase sb)
        {
            var rt = sb.GetComponent<RectTransform>();
            if (rt == null) return Vector2.zero;
            if (Camera.main != null)
            {
                Vector3 screenPos = Camera.main.WorldToScreenPoint(rt.position);
                return new Vector2(screenPos.x, screenPos.y);
            }
            return new Vector2(rt.position.x, rt.position.y);
        }

        public override void OnApplicationQuit()
        {
            LoggerInstance.Msg("Shutting down accessibility mod...");
            TolkSpeech.Shutdown();
        }

        /// <summary>
        /// Checks if any dropdown is currently expanded (has a Blocker or Dropdown List visible)
        /// </summary>
        private static bool IsAnyDropdownExpanded()
        {
            // Unity Dropdown creates "Blocker" and "Dropdown List" GameObjects when expanded
            var blocker = GameObject.Find("Blocker");
            var dropdownList = GameObject.Find("Dropdown List");
            return (blocker != null && blocker.activeInHierarchy) ||
                   (dropdownList != null && dropdownList.activeInHierarchy);
        }

        /// <summary>
        /// Checks if the given GameObject has a native cancel handler that should process Escape
        /// </summary>
        private static bool HasNativeCancelHandler(GameObject go)
        {
            if (go == null) return false;

            // DropdownBase handles cancel natively
            if (go.GetComponent<DropdownBase>() != null) return true;

            // Check if SelectableBase has OnCancel callback assigned
            var selectable = go.GetComponent<SelectableBase>();
            if (selectable != null)
            {
                try
                {
                    return selectable.OnCancel != null;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
