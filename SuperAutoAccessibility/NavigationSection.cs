using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using Il2CppSpacewood.Unity;
using Il2CppSpacewood.Unity.UI;
using Il2CppSpacewood.Unity.MonoBehaviours.Build;
using Il2CppSpacewood.Scripts.MonoBehaviours.Build.Hangar;
using Il2CppSpacewood.Core.Enums;

namespace SuperAutoAccessibility
{
    public class NavigationSection
    {
        public string Name { get; set; }
        public List<SelectableBase> Elements { get; set; }
        /// <summary>Optional metadata string (e.g. changelog lines for news Changes section)</summary>
        public string Tag { get; set; }

        public NavigationSection(string name)
        {
            Name = name;
            Elements = new List<SelectableBase>();
            Tag = null;
        }
    }

    public static class SectionManager
    {
        private static List<NavigationSection> _sections = new List<NavigationSection>();
        private static int _currentSectionIndex = 0;
        private static int _currentElementIndex = 0;
        private static string _cachedContextKey = "";

        // Label overrides for icon-only buttons (keyed by GameObject instance)
        private static Dictionary<GameObject, string> _labelOverrides = new Dictionary<GameObject, string>();

        // Flag to distinguish mod-driven selections from mouse/game-driven ones
        private static bool _modDrivenSelection = false;

        // Pending initial focus override set by section builders (e.g., PackShop auto-focuses selected pack)
        // FocusFirstSection uses this instead of defaulting to 0,0 when set
        private static (int section, int element)? _pendingInitialFocus = null;

        /// <summary>
        /// Returns true if the current selection change was triggered by the mod (keyboard nav).
        /// Consumed on read (resets to false).
        /// </summary>
        public static bool ConsumeModDrivenFlag()
        {
            bool val = _modDrivenSelection;
            _modDrivenSelection = false;
            return val;
        }

        public static void InvalidateCache()
        {
            _cachedContextKey = "";
        }

        /// <summary>
        /// Returns the current NavigationSection (or null if no sections exist).
        /// </summary>
        public static NavigationSection GetCurrentSection()
        {
            if (_sections.Count == 0) return null;
            if (_currentSectionIndex >= _sections.Count) return null;
            return _sections[_currentSectionIndex];
        }

        /// <summary>
        /// Validates current section elements, removing any that have been destroyed by the game.
        /// Returns false if no valid elements remain in the current section.
        /// </summary>
        private static bool ValidateSection()
        {
            if (_sections.Count == 0) return false;
            if (_currentSectionIndex >= _sections.Count) _currentSectionIndex = 0;

            var section = _sections[_currentSectionIndex];
            // Remove destroyed Il2Cpp elements (try-catch handles destroyed native objects)
            section.Elements.RemoveAll(e =>
            {
                try { return e == null || e.gameObject == null; }
                catch { return true; }
            });

            if (section.Elements.Count == 0) return false;
            if (_currentElementIndex >= section.Elements.Count)
                _currentElementIndex = 0;
            return true;
        }

        /// <summary>
        /// Get the label override for a GameObject, if any.
        /// Used by AccessibilityManager.BuildAnnouncement() for icon-only buttons.
        /// </summary>
        public static string GetLabelOverride(GameObject go)
        {
            if (go == null) return null;
            _labelOverrides.TryGetValue(go, out string label);
            return label;
        }

        public static List<NavigationSection> GetSections()
        {
            string contextKey = GetContextKey();
            if (contextKey != _cachedContextKey || _sections.Count == 0)
            {
                RebuildSections();
                _cachedContextKey = contextKey;
            }
            return _sections;
        }

        /// <summary>
        /// Context key includes page name, sidebar state, active settings tab, and picker state
        /// so cache invalidates properly on any context change.
        /// </summary>
        private static string GetContextKey()
        {
            string pageName = "";
            bool sidebarOpen = false;
            string activeTab = "";
            bool pickerOpen = false;

            try
            {
                var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                if (pageManager?.CurrentPage?.gameObject != null)
                    pageName = pageManager.CurrentPage.gameObject.name;
            }
            catch { }

            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Menu>();
                if (menu?.SideBar?.Container?.gameObject != null)
                    sidebarOpen = menu.SideBar.Container.gameObject.activeInHierarchy;
            }
            catch { }

            // Check if Picker popup is open
            try
            {
                var picker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.UI.Picker>();
                if (picker?.Container?.gameObject != null)
                    pickerOpen = picker.Container.gameObject.activeInHierarchy;
            }
            catch { }

            // Include active SettingsMenu tab in context key
            try
            {
                if (pageName.Contains("Settings") || IsSettingsMenuVisible())
                {
                    var settingsMenu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu>();
                    if (settingsMenu?.Groups != null)
                    {
                        for (int i = 0; i < settingsMenu.Groups.Count; i++)
                        {
                            var group = settingsMenu.Groups[i];
                            if (group?.Container != null &&
                                group.Container.gameObject.activeInHierarchy)
                            {
                                activeTab = group.Button?.gameObject?.name ?? $"tab{i}";
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // Detect active sub-page for pages like "Customize" that have child pages
            string subPage = "";
            try
            {
                if (pageName == "Customize" || pageName == "History")
                {
                    // Check which sub-page component is active
                    var petCustomizer = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PetCustomizer>();
                    if (petCustomizer != null && petCustomizer.gameObject != null && petCustomizer.gameObject.activeInHierarchy)
                        subPage = "PetCustomizer";
                    else if (HasActiveProductShopItems())
                        subPage = "ProductShop";
                    else
                    {
                        var browseShops = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.BrowseShops>();
                        if (browseShops != null && browseShops.gameObject != null && browseShops.gameObject.activeInHierarchy)
                            subPage = "BrowseShops";
                    }

                    // History sub-pages
                    var statsSummary = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.StatsSummary>();
                    if (statsSummary != null && statsSummary.gameObject != null && statsSummary.gameObject.activeInHierarchy)
                        subPage = "StatsSummary";
                    var achievements = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Achievements>();
                    if (achievements != null && achievements.gameObject != null && achievements.gameObject.activeInHierarchy)
                        subPage = "Achievements";
                    var replay = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Replay>();
                    if (replay != null && replay.gameObject != null && replay.gameObject.activeInHierarchy)
                        subPage = "Replay";
                    var spectate = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Spectate>();
                    if (spectate != null && spectate.gameObject != null && spectate.gameObject.activeInHierarchy)
                        subPage = "Spectate";
                }
            }
            catch { }

            // Track VersusListItem count on ModeMenu so sections rebuild when matches appear
            int versusItemCount = 0;
            try
            {
                if (pageName.Contains("ModeMenu"))
                {
                    var versusItems = UnityEngine.Object.FindObjectsOfType<Il2CppSpacewood.Unity.VersusListItem>();
                    foreach (var item in versusItems)
                    {
                        if (item != null && item.gameObject != null && item.gameObject.activeInHierarchy)
                            versusItemCount++;
                    }
                }
            }
            catch { }

            // Detect LastBattleMenu, DeckViewer, and Scoreboard overlay states
            bool lastBattleMenuOpen = false;
            bool deckViewerOpen = false;
            bool scoreboardOpen = false;
            try
            {
                var hangar = Gameplay.GameplayPhaseDetector.GetHangarMain();
                if (hangar != null)
                {
                    try
                    {
                        if (hangar.LastBattleMenu?.Container?.gameObject != null)
                            lastBattleMenuOpen = hangar.LastBattleMenu.Container.gameObject.activeInHierarchy;
                    }
                    catch { }
                    try
                    {
                        if (hangar.DeckViewer?.gameObject != null)
                            deckViewerOpen = hangar.DeckViewer.gameObject.activeInHierarchy;
                    }
                    catch { }
                    try
                    {
                        var sbGo = hangar.GetScoreboard();
                        if (sbGo != null)
                            scoreboardOpen = sbGo.activeInHierarchy;
                    }
                    catch { }
                }
            }
            catch { }

            // Check if LanguagePicker is visible (Bootstrap scene)
            bool langPickerOpen = false;
            try
            {
                var langPicker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.LanguagePicker>();
                if (langPicker != null)
                {
                    var canvas = langPicker.GetComponentInChildren<Canvas>();
                    if (canvas != null && canvas.gameObject.activeInHierarchy)
                        langPickerOpen = true;
                }
            }
            catch { }

            return $"{pageName}|sidebar:{sidebarOpen}|tab:{activeTab}|picker:{pickerOpen}|sub:{subPage}|versus:{versusItemCount}|lbm:{lastBattleMenuOpen}|dv:{deckViewerOpen}|sb:{scoreboardOpen}|lang:{langPickerOpen}";
        }

        private static void RebuildSections()
        {
            _sections.Clear();
            _labelOverrides.Clear();

            try
            {
                // Check if Picker popup is open FIRST â€” it overlays everything
                bool pickerOpen = false;
                try
                {
                    var picker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.UI.Picker>();
                    if (picker?.Container?.gameObject != null)
                        pickerOpen = picker.Container.gameObject.activeInHierarchy;
                    if (pickerOpen)
                    {
                        BuildPickerSections(picker);
                        _currentSectionIndex = 0;
                        _currentElementIndex = 0;
                        return;
                    }
                }
                catch { }

                // Check if LanguagePicker is open (Bootstrap scene â€” before main menu loads)
                try
                {
                    var langPicker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.LanguagePicker>();
                    if (langPicker != null)
                    {
                        var canvas = langPicker.GetComponentInChildren<Canvas>();
                        if (canvas != null && canvas.gameObject.activeInHierarchy)
                        {
                            BuildLanguagePickerSections(langPicker);
                            _currentSectionIndex = 0;
                            _currentElementIndex = 0;
                            return;
                        }
                    }
                }
                catch { }

                // Check if DesyncAlert is open â€” it overlays everything (desync error during match)
                try
                {
                    var desyncAlert = UnityEngine.Object.FindObjectOfType<DesyncAlert>();
                    if (desyncAlert != null && desyncAlert.IsActive)
                    {
                        BuildDesyncAlertSections(desyncAlert);
                        _currentSectionIndex = 0;
                        _currentElementIndex = 0;
                        return;
                    }
                }
                catch { }

                var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Menu>();

                // Check sidebar FIRST â€” if open, build sidebar sections
                bool sidebarOpen = false;
                try
                {
                    if (menu?.SideBar?.Container?.gameObject != null)
                        sidebarOpen = menu.SideBar.Container.gameObject.activeInHierarchy;
                }
                catch { }

                if (sidebarOpen && menu?.SideBar != null)
                {
                    BuildSidebarSections(menu.SideBar);
                }
                else
                {
                    // Get current page name for dispatch
                    string currentPageName = "";
                    try
                    {
                        var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                        if (pageManager?.CurrentPage?.gameObject != null)
                            currentPageName = pageManager.CurrentPage.gameObject.name;
                    }
                    catch { }

                    // Check for News overlay first (not a Page, has its own canvas)
                    bool newsHandled = false;
                    try
                    {
                        var news = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                        if (news != null)
                        {
                            var canvasGroup = news.GetComponentInChildren<UnityEngine.CanvasGroup>();
                            if (canvasGroup != null && canvasGroup.alpha > 0 && canvasGroup.gameObject.activeInHierarchy)
                            {
                                BuildNewsDialogSections(news);
                                newsHandled = true;
                            }
                        }
                    }
                    catch { }

                    if (newsHandled)
                    {
                        // News overlay takes priority â€” skip page dispatch
                    }
                    // Hybrid dispatch: page-name for unique pages, component-based for parent pages with sub-pages.
                    // Pure component-based dispatch breaks navigation because singleton components (e.g. SettingsMenu)
                    // can have activeInHierarchy=true even when their page isn't showing.
                    else if (currentPageName.Contains("Settings"))
                    {
                        TryBuildByActiveComponent<Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu>(BuildSettingsMenuSections);
                    }
                    else if (currentPageName.Contains("PackShop"))
                    {
                        TryBuildByActiveComponent<Il2CppSpacewood.Unity.PackShop>(BuildPackShopSections);
                    }
                    else if (currentPageName.Contains("VersusCreator"))
                    {
                        TryBuildByActiveComponent<Il2CppSpacewood.Unity.VersusCreator>(BuildVersusCreatorSections);
                    }
                    else if (currentPageName.Contains("ModeMenu"))
                    {
                        TryBuildByActiveComponent<Il2CppSpacewood.Unity.ModeMenu>(BuildModeMenuSections);
                    }
                    else if (currentPageName.Contains("VersusLobby"))
                    {
                        TryBuildByActiveComponent<Il2CppSpacewood.Unity.VersusLobby>(BuildVersusLobbySections);
                    }
                    else if (currentPageName.Contains("Customize"))
                    {
                        // "Customize" is the parent page for BrowseShops, PetCustomizer, and all cosmetic shops.
                        // Use component detection to determine which sub-page is active.
                        if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.PetCustomizer>(BuildPetCustomizerSections)) { }
                        else if (HasActiveProductShopItems()) { BuildProductShopSections(currentPageName); }
                        else if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.BrowseShops>(BuildBrowseShopsSections)) { }
                        else { BuildFallbackSections(); }
                    }
                    else if (currentPageName.Contains("History"))
                    {
                        // "History" is the parent page for StatsSummary, Achievements, Replay, Spectate, and HistoryMenu.
                        // Use component detection to determine which sub-page is active.
                        if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.StatsSummary>(BuildStatsSummarySections)) { }
                        else if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.Achievements>(BuildAchievementsSections)) { }
                        else if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.Replay>(BuildReplaySections)) { }
                        else if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.Spectate>(BuildSpectateSections)) { }
                        else if (TryBuildByActiveComponent<Il2CppSpacewood.Unity.HistoryMenu>(BuildHistoryMenuSections)) { }
                        else { BuildFallbackSections(); }
                    }
                    else if (IsSettingsMenuVisible())
                    {
                        // Settings opened from in-game sidebar (page name is still "Build")
                        TryBuildByActiveComponent<Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu>(BuildSettingsMenuSections);
                    }
                    else if (Patches.PageManagerPatches.IsDialogPageName(currentPageName))
                    {
                        // Dialog/consent page â€” scan for buttons so Tab navigation works
                        BuildDialogPageSections(currentPageName);
                    }
                    else
                    {
                        // Check for gameplay hangar (shop/build phase)
                        var hangarMain = Gameplay.GameplayPhaseDetector.GetHangarMain();
                        if (hangarMain != null)
                        {
                            // Check overlay menus FIRST â€” they overlay the shop
                            bool builtOverlay = false;

                            // Scoreboard (opponent list)
                            if (!builtOverlay)
                            {
                                try
                                {
                                    var sbGo = hangarMain.GetScoreboard();
                                    if (sbGo != null && sbGo.activeInHierarchy)
                                    {
                                        BuildScoreboardSections(hangarMain);
                                        builtOverlay = true;
                                    }
                                }
                                catch { }
                            }

                            // LastBattleMenu
                            if (!builtOverlay)
                            {
                                try
                                {
                                    if (hangarMain.LastBattleMenu?.Container?.gameObject != null &&
                                        hangarMain.LastBattleMenu.Container.gameObject.activeInHierarchy)
                                    {
                                        BuildLastBattleMenuSections(hangarMain.LastBattleMenu);
                                        builtOverlay = true;
                                    }
                                }
                                catch { }
                            }

                            // DeckViewer
                            if (!builtOverlay)
                            {
                                try
                                {
                                    if (hangarMain.DeckViewer?.gameObject != null &&
                                        hangarMain.DeckViewer.gameObject.activeInHierarchy)
                                    {
                                        BuildDeckViewerSections(hangarMain.DeckViewer);
                                        builtOverlay = true;
                                    }
                                }
                                catch { }
                            }

                            // Chooser screen (food stock / trinket / cursed trinket selection)
                            if (!builtOverlay)
                            {
                                try
                                {
                                    var chooser = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Scripts.MonoBehaviours.Build.Hangar.Chooser>();
                                    if (chooser != null && chooser.gameObject.activeInHierarchy)
                                    {
                                        BuildChooserSections(chooser);
                                        builtOverlay = true;
                                    }
                                }
                                catch { }
                            }

                            if (!builtOverlay)
                            {
                                BuildHangarSections(hangarMain);
                            }
                        }
                        else
                        {
                            // Lobby or unknown page
                            var lobby = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Lobby>();
                            if (lobby != null)
                            {
                                BuildLobbySections(lobby, menu);
                            }
                            else
                            {
                                BuildFallbackSections();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"RebuildSections error: {ex.Message}");
                BuildFallbackSections();
            }

            _currentSectionIndex = 0;
            _currentElementIndex = 0;
        }

        /// <summary>
        /// Tries to find an active component of type T and build sections using it.
        /// Returns true if the component was found and active.
        /// </summary>
        private static bool TryBuildByActiveComponent<T>(System.Action<T> builder) where T : UnityEngine.Component
        {
            try
            {
                var component = UnityEngine.Object.FindObjectOfType<T>();
                if (component != null && component.gameObject != null && component.gameObject.activeInHierarchy)
                {
                    builder(component);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks if any active ProductShopItemShared exists (indicates a cosmetic shop sub-page is open).
        /// </summary>
        private static bool HasActiveProductShopItems()
        {
            try
            {
                var items = UnityEngine.Object.FindObjectsOfType<Il2CppSpacewood.Unity.ProductShopItemShared>();
                foreach (var item in items)
                {
                    if (item != null && item.gameObject != null && item.gameObject.activeInHierarchy)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks if the SettingsMenu is currently visible (has at least one active group container).
        /// Used to detect settings opened from the in-game sidebar, where the page name remains "Build".
        /// </summary>
        private static bool IsSettingsMenuVisible()
        {
            try
            {
                var sm = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu>();
                if (sm?.Groups == null) return false;
                for (int i = 0; i < sm.Groups.Count; i++)
                {
                    var g = sm.Groups[i];
                    if (g?.Container?.gameObject != null && g.Container.gameObject.activeInHierarchy)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void BuildSidebarSections(Il2CppSpacewood.Unity.SideBar sidebar)
        {
            var section = new NavigationSection("Sidebar");
            TryAddWithLabel(section, sidebar.Resume, "Resume");
            TryAddWithLabel(section, sidebar.Settings, "Settings");
            TryAddWithLabel(section, sidebar.Account, "Account");
            TryAddWithLabel(section, sidebar.Premium, "Subscription");
            TryAddWithLabel(section, sidebar.Feedback, "Feedback");
            TryAddWithLabel(section, sidebar.Credits, "Credits");
            TryAddWithLabel(section, sidebar.Tips, "Tips");
            TryAddWithLabel(section, sidebar.List, "List");
            TryAddWithLabel(section, sidebar.Return, "Return");
            TryAddWithLabel(section, sidebar.LogOut, "Log out");
            TryAddWithLabel(section, sidebar.Abandon, "Abandon");
            TryAddWithLabel(section, sidebar.Quit, "Quit to desktop");
            if (section.Elements.Count > 0) _sections.Add(section);
        }

        private static void BuildLanguagePickerSections(Il2CppSpacewood.Unity.LanguagePicker langPicker)
        {
            var section = new NavigationSection("Languages");
            try
            {
                // Collect all LanguagePickerItem ButtonBase elements
                var items = langPicker.GetComponentsInChildren<Il2CppSpacewood.Unity.LanguagePickerItem>(false);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    try
                    {
                        var btn = item.GetComponentInChildren<ButtonBase>(false);
                        if (btn == null || btn.gameObject == null) continue;
                        if (!btn.gameObject.activeInHierarchy) continue;
                        if (!btn.GetInteractable()) continue;
                        section.Elements.Add(btn);
                    }
                    catch { }
                }

                // Add the MoreButton if active (shows additional languages)
                try
                {
                    var moreBtn = langPicker.GetComponentInChildren<Canvas>()
                        ?.transform.Find("NotchPadding/Layout/Content/MoreButton");
                    if (moreBtn != null)
                    {
                        var moreBtnBase = moreBtn.GetComponent<ButtonBase>();
                        if (moreBtnBase != null && moreBtnBase.gameObject.activeInHierarchy && moreBtnBase.GetInteractable())
                            section.Elements.Add(moreBtnBase);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildLanguagePickerSections error: {ex.Message}");
            }
            if (section.Elements.Count > 0) _sections.Add(section);
        }

        private static void BuildSettingsMenuSections(Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu settingsMenu)
        {
            // Section 1: Tabs â€” in correct left-to-right order
            var tabSection = new NavigationSection("Tabs");
            TryAdd(tabSection, settingsMenu.GeneralTab);
            TryAdd(tabSection, settingsMenu.GameplayTab);
            TryAdd(tabSection, settingsMenu.AudioTab);
            TryAdd(tabSection, settingsMenu.DisplayTab);
            TryAdd(tabSection, settingsMenu.CustomizeTab);
            TryAdd(tabSection, settingsMenu.PrivacyTab);
            if (tabSection.Elements.Count > 0) _sections.Add(tabSection);

            // Build a set of tab buttons for exclusion
            var tabButtons = new HashSet<SelectableBase>();
            if (settingsMenu.GeneralTab != null) tabButtons.Add(settingsMenu.GeneralTab);
            if (settingsMenu.GameplayTab != null) tabButtons.Add(settingsMenu.GameplayTab);
            if (settingsMenu.AudioTab != null) tabButtons.Add(settingsMenu.AudioTab);
            if (settingsMenu.DisplayTab != null) tabButtons.Add(settingsMenu.DisplayTab);
            if (settingsMenu.CustomizeTab != null) tabButtons.Add(settingsMenu.CustomizeTab);
            if (settingsMenu.PrivacyTab != null) tabButtons.Add(settingsMenu.PrivacyTab);

            // Section 2: Content â€” elements inside the active tab's container
            var contentSection = new NavigationSection("Content");
            try
            {
                var groups = settingsMenu.Groups;
                if (groups != null)
                {
                    for (int i = 0; i < groups.Count; i++)
                    {
                        var group = groups[i];
                        if (group?.Container != null &&
                            group.Container.gameObject.activeInHierarchy)
                        {
                            // Collect all active SelectableBase in this container (include disabled ones)
                            var selectables = group.Container.GetComponentsInChildren<SelectableBase>(false);
                            var sorted = new List<SelectableBase>();
                            foreach (var s in selectables)
                            {
                                if (s == null || s.gameObject == null) continue;
                                if (!s.gameObject.activeInHierarchy) continue;
                                // Exclude the tab buttons themselves (they're in the Tabs section)
                                if (tabButtons.Contains(s)) continue;
                                sorted.Add(s);
                            }
                            // Sort by screen position (top-to-bottom, left-to-right)
                            sorted.Sort((a, b) =>
                            {
                                var posA = SAPMod.GetScreenPos(a);
                                var posB = SAPMod.GetScreenPos(b);
                                int cmp = posB.y.CompareTo(posA.y);
                                if (cmp != 0) return cmp;
                                return posA.x.CompareTo(posB.x);
                            });

                            // Extract labels from parent row for each settings element
                            foreach (var s in sorted)
                            {
                                string rowLabel = ExtractSettingsRowLabel(s);
                                if (!string.IsNullOrEmpty(rowLabel))
                                    _labelOverrides[s.gameObject] = rowLabel;
                            }

                            contentSection.Elements.AddRange(sorted);
                            break; // Only one container should be active
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildSettingsMenuSections content error: {ex.Message}");
            }
            if (contentSection.Elements.Count > 0) _sections.Add(contentSection);

            // Section 3: Navigation â€” Back button
            var navSection = new NavigationSection("Navigation");
            TryAddWithLabel(navSection, settingsMenu.ExitButton, "Back");
            if (navSection.Elements.Count > 0) _sections.Add(navSection);
        }

        /// <summary>
        /// Extracts the setting label from a parent row container.
        /// Settings items typically have a parent row (VerticalLayoutGroup) containing a Label sibling
        /// and a Content child that holds the interactive control.
        /// </summary>
        private static string ExtractSettingsRowLabel(SelectableBase s)
        {
            try
            {
                if (s == null || s.gameObject == null) return null;
                Transform t = s.transform;

                // Walk up to find the parent "row" container (up to 4 levels)
                for (int level = 0; level < 4; level++)
                {
                    Transform parent = t.parent;
                    if (parent == null) break;

                    // Pass 1: Prefer siblings explicitly named "Label" or "Title"
                    for (int ci = 0; ci < parent.childCount; ci++)
                    {
                        Transform sibling = parent.GetChild(ci);
                        if (sibling == t) continue;
                        if (sibling == null || !sibling.gameObject.activeInHierarchy) continue;

                        if (sibling.name == "Label" || sibling.name == "Title")
                        {
                            var labelTmp = sibling.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                            if (labelTmp != null && !string.IsNullOrWhiteSpace(labelTmp.text))
                                return TextExtractor.CleanTextPublic(labelTmp.text);
                        }

                        // Check sibling's children for a "Label" child
                        var labelChild = sibling.Find("Label");
                        if (labelChild != null && labelChild.gameObject.activeInHierarchy)
                        {
                            var childTmp = labelChild.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                            if (childTmp != null && !string.IsNullOrWhiteSpace(childTmp.text))
                                return TextExtractor.CleanTextPublic(childTmp.text);
                        }
                    }

                    // Pass 2: Fallback â€” first sibling with non-value TextMeshProUGUI
                    for (int ci = 0; ci < parent.childCount; ci++)
                    {
                        Transform sibling = parent.GetChild(ci);
                        if (sibling == t) continue;
                        if (sibling == null || !sibling.gameObject.activeInHierarchy) continue;

                        var tmp = sibling.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                        if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                        {
                            string text = tmp.text.Trim();
                            if (IsSettingsValueText(text)) continue;
                            return TextExtractor.CleanTextPublic(text);
                        }
                    }

                    // Move up one level and try again
                    t = parent;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ExtractSettingsRowLabel error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Returns true if the text looks like a value/percentage rather than a setting label.
        /// </summary>
        private static bool IsSettingsValueText(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            // Pure numbers or percentages
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+%?$")) return true;
            // Very short single-char text (checkmarks, icons)
            if (text.Length <= 1) return true;
            // Common toggle/value words that aren't setting labels
            string lower = text.ToLowerInvariant();
            if (lower == "on" || lower == "off" || lower == "yes" || lower == "no" ||
                lower == "low" || lower == "medium" || lower == "high" ||
                lower == "enabled" || lower == "disabled" ||
                lower == "windowed" || lower == "fullscreen" || lower == "borderless")
                return true;
            return false;
        }

        private static void BuildPackShopSections(Il2CppSpacewood.Unity.PackShop packShop)
        {
            // Section 1: Standard Packs (named properties â€” stable references)
            var standardSection = new NavigationSection("Standard Packs");
            TryAddPackProduct(standardSection, packShop.TurtlePack);
            TryAddPackProduct(standardSection, packShop.Bundle);
            // Scan RegularPackLayout for any additional standard packs not covered above
            try
            {
                if (packShop.RegularPackLayout != null)
                {
                    ScanPackContainer(standardSection, packShop.RegularPackLayout, packShop);
                }
            }
            catch { }
            if (standardSection.Elements.Count > 0) _sections.Add(standardSection);

            // Section 2: DLC/Weekly Packs (dynamically scanned from containers)
            var dlcSection = new NavigationSection("Packs");
            try
            {
                // Weekly pack â€” lives under Pack/ container
                // Find it by searching the page for PackProduct instances not already added
                var allProducts = packShop.GetComponentsInChildren<PackProduct>(false);
                var addedButtons = new HashSet<SelectableBase>();
                foreach (var elem in standardSection.Elements)
                    addedButtons.Add(elem);

                foreach (var product in allProducts)
                {
                    if (product == null) continue;
                    try
                    {
                        if (product.Button == null) continue;
                        if (!product.Button.gameObject.activeInHierarchy) continue;
                        if (!product.Button.GetInteractable()) continue;
                        // Skip if already in standard section
                        if (addedButtons.Contains(product.Button)) continue;
                        // Skip named special packs (they'll go in their own section)
                        if (product == packShop.ChallengePack ||
                            product == packShop.WackyPack ||
                            product == packShop.PlusPack)
                            continue;
                        dlcSection.Elements.Add(product.Button);
                        addedButtons.Add(product.Button);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildPackShopSections DLC scan error: {ex.Message}");
            }
            if (dlcSection.Elements.Count > 0) _sections.Add(dlcSection);

            // Section 3: Special Packs (Challenge, Wacky, Plus)
            var specialSection = new NavigationSection("Special Packs");
            TryAddPackProduct(specialSection, packShop.ChallengePack);
            TryAddPackProduct(specialSection, packShop.WackyPack);
            TryAddPackProduct(specialSection, packShop.PlusPack);
            if (specialSection.Elements.Count > 0) _sections.Add(specialSection);

            // Section 4: Pack Inspect â€” ShowcaseButtons for viewing pack contents
            var inspectSection = new NavigationSection("Pack Inspect");
            try
            {
                var allShared = packShop.GetComponentsInChildren<Il2CppSpacewood.Unity.ProductShopItemShared>(false);
                foreach (var shared in allShared)
                {
                    if (shared == null) continue;
                    try
                    {
                        var showcaseBtn = shared.ShowcaseButton;
                        if (showcaseBtn == null || showcaseBtn.gameObject == null) continue;
                        if (!showcaseBtn.gameObject.activeInHierarchy) continue;
                        // Try to get a label from the parent PackProduct or sibling text
                        string label = "View Pack";
                        try
                        {
                            var packProduct = shared.GetComponent<PackProduct>();
                            if (packProduct?.Button != null)
                            {
                                var txt = packProduct.Button.GetComponentInChildren<Il2CppTMPro.TMP_Text>();
                                if (txt != null && !string.IsNullOrEmpty(txt.text))
                                    label = $"Inspect {txt.text}";
                            }
                        }
                        catch { }
                        TryAddWithLabel(inspectSection, showcaseBtn, label);
                    }
                    catch { }
                }
            }
            catch { }
            if (inspectSection.Elements.Count > 0) _sections.Add(inspectSection);

            // Section 5: Match Settings (PlayBar buttons)
            var settingsSection = new NavigationSection("Match Settings");
            // Difficulty button â€” find by name since PackShop doesn't expose a direct property
            try
            {
                var allButtons = packShop.GetComponentsInChildren<ButtonBase>(false);
                foreach (var btn in allButtons)
                {
                    if (btn == null || btn.gameObject == null) continue;
                    if (!btn.gameObject.activeInHierarchy) continue;
                    if (btn.gameObject.name == "Difficulty")
                    {
                        TryAddWithLabel(settingsSection, btn, "Difficulty");
                        break;
                    }
                }
            }
            catch { }
            TryAddWithLabel(settingsSection, packShop.TimeButton, "Time Mode");
            TryAddWithLabel(settingsSection, packShop.MirrorButton, "Mirror Mode");
            try
            {
                var versusRank = packShop.VersusRank;
                if (versusRank?.Button != null)
                    TryAddWithLabel(settingsSection, versusRank.Button, "Ranked Mode");
            }
            catch { }
            if (settingsSection.Elements.Count > 0) _sections.Add(settingsSection);

            // Section 6: Actions (Start, Create Custom, Back)
            var actionsSection = new NavigationSection("Actions");
            TryAddWithLabel(actionsSection, packShop.ContinueButton, "Start");
            TryAddWithLabel(actionsSection, packShop.CreateCustomButton, "Create Custom Pack");
            TryAddWithLabel(actionsSection, packShop.PlayersButton, "Players");
            // Find Back button by searching for it under NotchPadding
            try
            {
                var allButtons = packShop.GetComponentsInChildren<ButtonBase>(false);
                foreach (var btn in allButtons)
                {
                    if (btn == null || btn.gameObject == null) continue;
                    if (!btn.gameObject.activeInHierarchy) continue;
                    if (btn.gameObject.name == "Back" || btn.gameObject.name == "BackButton")
                    {
                        TryAddWithLabel(actionsSection, btn, "Back");
                        break;
                    }
                }
            }
            catch { }
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);

            // Auto-focus the currently selected pack (the one with active Activator child)
            // Sets _pendingInitialFocus so FocusFirstSection uses these indices instead of 0,0
            try
            {
                for (int si = 0; si < _sections.Count; si++)
                {
                    var section = _sections[si];
                    for (int ei = 0; ei < section.Elements.Count; ei++)
                    {
                        var elem = section.Elements[ei];
                        if (elem == null || elem.gameObject == null) continue;
                        var pp = elem.gameObject.GetComponentInParent<PackProduct>();
                        if (pp == null) continue;
                        // Check Activator child: BuyButton â†’ Tween â†’ Activator
                        var activator = elem.transform.Find("Tween/Activator");
                        if (activator != null && activator.gameObject.activeInHierarchy)
                        {
                            _pendingInitialFocus = (si, ei);
                            MelonLogger.Msg($"PackShop: Will auto-focus selected pack at section {si}, element {ei}");
                            return; // Found the selected pack
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Adds a PackProduct's Button to a section if valid.
        /// </summary>
        private static void TryAddPackProduct(NavigationSection section, PackProduct product)
        {
            if (product == null) return;
            try
            {
                if (product.Button == null) return;
                if (product.Button.gameObject == null) return;
                if (!product.Button.gameObject.activeInHierarchy) return;
                if (!product.Button.GetInteractable()) return;
                section.Elements.Add(product.Button);
            }
            catch { }
        }

        /// <summary>
        /// Scans a container for PackProduct children and adds their buttons to a section.
        /// Skips products already in the section (avoids duplicates from named properties).
        /// </summary>
        private static void ScanPackContainer(NavigationSection section, RectTransform container,
            Il2CppSpacewood.Unity.PackShop packShop)
        {
            if (container == null) return;
            var products = container.GetComponentsInChildren<PackProduct>(false);
            foreach (var product in products)
            {
                if (product == null) continue;
                try
                {
                    if (product.Button == null) continue;
                    if (product.Button.gameObject == null) continue;
                    if (!product.Button.gameObject.activeInHierarchy) continue;
                    if (!product.Button.GetInteractable()) continue;
                    // Skip if already in section (from named properties like TurtlePack, Bundle)
                    if (section.Elements.Contains(product.Button)) continue;
                    section.Elements.Add(product.Button);
                }
                catch { }
            }
        }

        private static void BuildLobbySections(Il2CppSpacewood.Unity.Lobby lobby, Il2CppSpacewood.Unity.Menu menu)
        {
            // Main section
            var mainSection = new NavigationSection("Main");
            TryAdd(mainSection, lobby.PlayButton);
            TryAdd(mainSection, lobby.PackButton);
            TryAdd(mainSection, lobby.CustomizeButton);
            TryAdd(mainSection, lobby.StatsButton);
            TryAdd(mainSection, lobby.ContinueButton);
            if (mainSection.Elements.Count > 0) _sections.Add(mainSection);

            // Social section â€” find buttons by name under "Socials" parent
            var socialSection = new NavigationSection("Social");
            try
            {
                var allButtons = UnityEngine.Object.FindObjectsOfType<ButtonBase>();
                // Collect social buttons first, then sort to ensure consistent order
                var socialButtons = new List<(ButtonBase btn, string label)>();
                foreach (var btn in allButtons)
                {
                    if (btn == null || btn.gameObject == null) continue;
                    if (!btn.gameObject.activeInHierarchy) continue;
                    if (!btn.GetInteractable()) continue;

                    var parent = btn.gameObject.transform.parent;
                    if (parent != null && parent.name == "Socials")
                    {
                        string btnName = btn.gameObject.name;
                        socialButtons.Add((btn, btnName));
                    }
                }
                // Sort by desired order: Website, Discord, Twitter
                string[] socialOrder = { "Website", "Discord", "Twitter" };
                foreach (var name in socialOrder)
                {
                    foreach (var (btn, label) in socialButtons)
                    {
                        if (label == name)
                        {
                            TryAddWithLabel(socialSection, btn, label);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Social buttons search error: {ex.Message}");
            }
            if (socialSection.Elements.Count > 0) _sections.Add(socialSection);

            // Settings section
            if (menu?.SettingsMenuButton != null)
            {
                var settingsSection = new NavigationSection("Settings");
                TryAddWithLabel(settingsSection, menu.SettingsMenuButton, "Settings");
                if (settingsSection.Elements.Count > 0) _sections.Add(settingsSection);
            }
        }

        /// <summary>
        /// Builds sections for the DesyncAlert overlay (desync error during a match).
        /// Provides access to Reload, Menu, and Abandon buttons.
        /// </summary>
        private static void BuildDesyncAlertSections(DesyncAlert desyncAlert)
        {
            var section = new NavigationSection("Desync Alert");

            try
            {
                TryAddWithLabel(section, desyncAlert.ReloadButton, "Reload");
            }
            catch { }
            try
            {
                TryAddWithLabel(section, desyncAlert.MenuButton, "Menu");
            }
            catch { }
            try
            {
                TryAddWithLabel(section, desyncAlert.AbandonButton, "Abandon");
            }
            catch { }

            if (section.Elements.Count > 0) _sections.Add(section);
        }

        /// <summary>
        /// Builds sections when the Picker popup is open (timer, packs, spectator mode dropdowns).
        /// The Picker creates PickerButton clones with Primary/Secondary ButtonBase children.
        /// </summary>
        private static void BuildPickerSections(Il2CppSpacewood.Unity.UI.Picker picker)
        {
            var section = new NavigationSection("Picker");

            try
            {
                // Get title for context
                string title = "";
                try
                {
                    if (picker.Title != null && !string.IsNullOrWhiteSpace(picker.Title.text))
                        title = picker.Title.text.Trim();
                }
                catch { }
                if (!string.IsNullOrEmpty(title))
                    section.Name = title;

                // Collect all Primary buttons from the PickerButton items in the ItemContainer
                var itemContainer = picker.ItemContainer;
                if (itemContainer != null)
                {
                    var pickerButtons = itemContainer.GetComponentsInChildren<Il2CppSpacewood.Unity.UI.PickerButton>(false);
                    foreach (var pb in pickerButtons)
                    {
                        if (pb == null) continue;
                        try
                        {
                            if (pb.Primary == null) continue;
                            if (pb.Primary.gameObject == null) continue;
                            if (!pb.Primary.gameObject.activeInHierarchy) continue;
                            if (!pb.Primary.GetInteractable()) continue;
                            section.Elements.Add(pb.Primary);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildPickerSections error: {ex.Message}");
            }

            if (section.Elements.Count > 0) _sections.Add(section);
        }

        /// <summary>
        /// Builds sections for VersusCreator (match settings page).
        /// Sections: Match Settings (timer, packs, spectator), Game Name, Actions (Create, Back)
        /// </summary>
        private static void BuildVersusCreatorSections(Il2CppSpacewood.Unity.VersusCreator versusCreator)
        {
            // Section 1: Match Settings (picker buttons for match configuration)
            var settingsSection = new NavigationSection("Match Settings");
            TryAddWithLabel(settingsSection, versusCreator.TurnDurationButton, "Turn Duration");
            TryAddWithLabel(settingsSection, versusCreator.PackModeButton, "Pack Mode");
            TryAddWithLabel(settingsSection, versusCreator.SpectatorModeButton, "Spectator Mode");
            // Find MaxPlayers input field by name (not a named property on VersusCreator)
            try
            {
                var allInputs = versusCreator.GetComponentsInChildren<InputFieldBase>(false);
                foreach (var inp in allInputs)
                {
                    if (inp == null || inp.gameObject == null) continue;
                    if (!inp.gameObject.activeInHierarchy) continue;
                    if (inp.gameObject.name == "MaxPlayers")
                    {
                        TryAddWithLabel(settingsSection, inp, "Max Players");
                        break;
                    }
                }
            }
            catch { }
            if (settingsSection.Elements.Count > 0) _sections.Add(settingsSection);

            // Section 2: Game Name input
            var nameSection = new NavigationSection("Game Name");
            TryAdd(nameSection, versusCreator.GameNameField);
            if (nameSection.Elements.Count > 0) _sections.Add(nameSection);

            // Section 3: Advanced settings toggle
            var advancedSection = new NavigationSection("Advanced");
            TryAddWithLabel(advancedSection, versusCreator.AdvancedToggle, "Advanced Settings");

            // Check if advanced view is active and add its elements
            try
            {
                if (versusCreator.Advanced != null && versusCreator.Advanced.gameObject != null &&
                    versusCreator.Advanced.gameObject.activeInHierarchy)
                {
                    // Add advanced rows (each row has PrimaryButton and optional SecondaryButton/SecondaryInput)
                    var rows = versusCreator.Advanced.Rows;
                    if (rows != null)
                    {
                        for (int i = 0; i < rows.Count; i++)
                        {
                            try
                            {
                                var row = rows[i];
                                if (row == null) continue;
                                TryAdd(advancedSection, row.PrimaryButton);
                                if (row.SecondaryEnabled)
                                {
                                    TryAdd(advancedSection, row.SecondaryButton);
                                    TryAdd(advancedSection, row.SecondaryInput);
                                }
                            }
                            catch { }
                        }
                    }

                    // Add wacky toy button if visible
                    TryAdd(advancedSection, versusCreator.Advanced.WackyToyButton);
                    TryAdd(advancedSection, versusCreator.Advanced.WackyParameterButton);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"VersusCreator advanced error: {ex.Message}");
            }
            if (advancedSection.Elements.Count > 0) _sections.Add(advancedSection);

            // Section 4: Actions (Create match, Back)
            var actionsSection = new NavigationSection("Actions");
            TryAddWithLabel(actionsSection, versusCreator.CreateButton, "Create Match");
            // Find Back button
            try
            {
                var allButtons = versusCreator.GetComponentsInChildren<ButtonBase>(false);
                foreach (var btn in allButtons)
                {
                    if (btn == null || btn.gameObject == null) continue;
                    if (!btn.gameObject.activeInHierarchy) continue;
                    if (btn.gameObject.name == "Back" || btn.gameObject.name == "BackButton")
                    {
                        TryAddWithLabel(actionsSection, btn, "Back");
                        break;
                    }
                }
            }
            catch { }
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for ModeMenu (game mode selection).
        /// Single section with all game mode buttons.
        /// </summary>
        private static void BuildModeMenuSections(Il2CppSpacewood.Unity.ModeMenu modeMenu)
        {
            var section = new NavigationSection("Game Modes");
            TryAddWithLabel(section, modeMenu.ArenaButton, "Arena");
            TryAddWithLabel(section, modeMenu.BullyButton, "Bully");
            TryAddWithLabel(section, modeMenu.ListButton, "List");
            TryAddWithLabel(section, modeMenu.VersusButton, "Versus");
            TryAddWithLabel(section, modeMenu.PrivateButton, "Private");
            if (section.Elements.Count > 0) _sections.Add(section);

            // Section 2: Active versus matches (VersusListItem clones â€” dynamically created)
            var matchSection = new NavigationSection("Matches");
            try
            {
                var versusItems = UnityEngine.Object.FindObjectsOfType<Il2CppSpacewood.Unity.VersusListItem>();
                foreach (var item in versusItems)
                {
                    if (item == null) continue;
                    try
                    {
                        if (item.Button == null) continue;
                        if (item.Button.gameObject == null) continue;
                        if (!item.Button.gameObject.activeInHierarchy) continue;
                        if (!item.Button.GetInteractable()) continue;

                        // Build a label from the item's text fields
                        string label = "Match";
                        try
                        {
                            var labelParts = new List<string>();
                            if (item.GameNameLabel != null && !string.IsNullOrWhiteSpace(item.GameNameLabel.text))
                                labelParts.Add(item.GameNameLabel.text.Trim());
                            if (item.StatusLabel != null && !string.IsNullOrWhiteSpace(item.StatusLabel.text))
                                labelParts.Add(item.StatusLabel.text.Trim());
                            if (item.TimeLeftLabel != null && !string.IsNullOrWhiteSpace(item.TimeLeftLabel.text))
                                labelParts.Add(item.TimeLeftLabel.text.Trim());
                            if (labelParts.Count > 0)
                                label = string.Join(", ", labelParts);
                        }
                        catch { }

                        TryAddWithLabel(matchSection, item.Button, label);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildModeMenuSections versus items error: {ex.Message}");
            }
            if (matchSection.Elements.Count > 0) _sections.Add(matchSection);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, modeMenu.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for HistoryMenu (achievements, replays, stats, spectate).
        /// </summary>
        private static void BuildHistoryMenuSections(Il2CppSpacewood.Unity.HistoryMenu historyMenu)
        {
            var section = new NavigationSection("History");
            TryAddWithLabel(section, historyMenu.AchievementsButton, "Achievements");
            TryAddWithLabel(section, historyMenu.ReplaysButton, "Replays");
            TryAddWithLabel(section, historyMenu.StatsButton, "Stats");
            TryAddWithLabel(section, historyMenu.SpectateButton, "Spectate");
            if (section.Elements.Count > 0) _sections.Add(section);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, historyMenu.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for Replay page.
        /// </summary>
        private static void BuildReplaySections(Il2CppSpacewood.Unity.Replay replay)
        {
            var section = new NavigationSection("Replay");
            TryAddWithLabel(section, replay.SharedClipboardPlayButton, "Paste from Clipboard");
            TryAddWithLabel(section, replay.SharedManualPlayButton, "Play Replay");
            TryAdd(section, replay.SharedManualPlayInput);
            if (section.Elements.Count > 0) _sections.Add(section);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, replay.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for Spectate page (list of spectatable matches).
        /// </summary>
        private static void BuildSpectateSections(Il2CppSpacewood.Unity.Spectate spectate)
        {
            var section = new NavigationSection("Spectate");
            try
            {
                if (spectate.ItemContainer != null)
                {
                    var items = spectate.ItemContainer.GetComponentsInChildren<Il2CppSpacewood.Unity.SpectateItem>(false);
                    foreach (var item in items)
                    {
                        if (item == null) continue;
                        try
                        {
                            if (item.Button == null) continue;
                            if (item.Button.gameObject == null) continue;
                            if (!item.Button.gameObject.activeInHierarchy) continue;
                            if (!item.Button.GetInteractable()) continue;
                            section.Elements.Add(item.Button);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildSpectateSections error: {ex.Message}");
            }
            if (section.Elements.Count > 0) _sections.Add(section);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, spectate.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for BrowseShops (Customize page with category buttons).
        /// </summary>
        private static void BuildBrowseShopsSections(Il2CppSpacewood.Unity.BrowseShops browseShops)
        {
            var section = new NavigationSection("Categories");
            TryAddWithLabel(section, browseShops.PetButton, "Pets");
            TryAddWithLabel(section, browseShops.CosmeticButton, "Cosmetics");
            TryAddWithLabel(section, browseShops.BackgroundButton, "Backgrounds");
            TryAddWithLabel(section, browseShops.MascotButton, "Mascots");
            TryAddWithLabel(section, browseShops.EntranceButton, "Entrances");
            TryAddWithLabel(section, browseShops.AwardButton, "Awards");
            if (section.Elements.Count > 0) _sections.Add(section);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, browseShops.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for any cosmetic/product shop page (Hats, Backgrounds, Mascots, Entrances, Awards).
        /// All use the ProductShopItemShared pattern. Scans for items and adds their main Button.
        /// </summary>
        private static void BuildProductShopSections(string pageName)
        {
            var itemSection = new NavigationSection("Items");
            try
            {
                // Find all active ProductShopItemShared on the page
                var allItems = UnityEngine.Object.FindObjectsOfType<Il2CppSpacewood.Unity.ProductShopItemShared>();
                // Sort by screen position for consistent ordering
                var sortedItems = new List<Il2CppSpacewood.Unity.ProductShopItemShared>();
                foreach (var item in allItems)
                {
                    if (item == null) continue;
                    try
                    {
                        if (item.gameObject == null || !item.gameObject.activeInHierarchy) continue;
                        if (item.Button == null) continue;
                        if (item.Button.gameObject == null || !item.Button.gameObject.activeInHierarchy) continue;
                        if (!item.Button.GetInteractable()) continue;
                        sortedItems.Add(item);
                    }
                    catch { }
                }
                sortedItems.Sort((a, b) =>
                {
                    var posA = SAPMod.GetScreenPos(a.Button);
                    var posB = SAPMod.GetScreenPos(b.Button);
                    int cmp = posB.y.CompareTo(posA.y); // Top to bottom
                    if (cmp != 0) return cmp;
                    return posA.x.CompareTo(posB.x); // Left to right
                });
                foreach (var item in sortedItems)
                {
                    itemSection.Elements.Add(item.Button);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildProductShopSections error: {ex.Message}");
            }
            if (itemSection.Elements.Count > 0) _sections.Add(itemSection);

            // Back button â€” search the current page
            var actionsSection = new NavigationSection("Actions");
            try
            {
                var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                if (pageManager?.CurrentPage?.gameObject != null)
                {
                    FindAndAddBackButton(actionsSection, pageManager.CurrentPage.gameObject);
                }
            }
            catch { }
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);

            // If no items found, fall back
            if (_sections.Count == 0) BuildFallbackSections();
        }

        /// <summary>
        /// Builds sections for StatsSummary page (filters + stat entries).
        /// </summary>
        private static void BuildStatsSummarySections(Il2CppSpacewood.Unity.StatsSummary statsSummary)
        {
            // Section 1: Filters
            var filterSection = new NavigationSection("Filters");
            TryAddWithLabel(filterSection, statsSummary.ModeFilterButton, "Mode Filter");
            TryAddWithLabel(filterSection, statsSummary.PackFilterButton, "Pack Filter");
            if (filterSection.Elements.Count > 0) _sections.Add(filterSection);

            // Section 2: Stats items
            var statsSection = new NavigationSection("Stats");
            try
            {
                var items = statsSummary.Items;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var item = items[i];
                            if (item == null) continue;
                            if (item.Details == null) continue;
                            if (item.Details.gameObject == null) continue;
                            if (!item.Details.gameObject.activeInHierarchy) continue;
                            if (!item.Details.GetInteractable()) continue;
                            statsSection.Elements.Add(item.Details);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildStatsSummarySections items error: {ex.Message}");
            }
            if (statsSection.Elements.Count > 0) _sections.Add(statsSection);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, statsSummary.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for PetCustomizer page (tabs, controls, search, item grid).
        /// </summary>
        private static void BuildPetCustomizerSections(Il2CppSpacewood.Unity.PetCustomizer petCustomizer)
        {
            // Section 1: Tabs
            var tabSection = new NavigationSection("Tabs");
            TryAddWithLabel(tabSection, petCustomizer.SkinTab, "Skins");
            TryAddWithLabel(tabSection, petCustomizer.HatTab, "Hats");
            if (tabSection.Elements.Count > 0) _sections.Add(tabSection);

            // Section 2: Controls
            var controlSection = new NavigationSection("Controls");
            TryAddWithLabel(controlSection, petCustomizer.ChangeAllButton, "Change All");
            TryAddWithLabel(controlSection, petCustomizer.ResetAllButton, "Reset All");
            TryAdd(controlSection, petCustomizer.SearchField);
            if (controlSection.Elements.Count > 0) _sections.Add(controlSection);

            // Section 3: Items
            var itemSection = new NavigationSection("Items");
            try
            {
                var items = petCustomizer.Items;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var item = items[i];
                            if (item == null) continue;
                            if (item.Button == null) continue;
                            if (item.Button.gameObject == null) continue;
                            if (!item.Button.gameObject.activeInHierarchy) continue;
                            if (!item.Button.GetInteractable()) continue;
                            itemSection.Elements.Add(item.Button);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildPetCustomizerSections items error: {ex.Message}");
            }
            if (itemSection.Elements.Count > 0) _sections.Add(itemSection);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, petCustomizer.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for Achievements page (filters, achievement entries, actions).
        /// </summary>
        private static void BuildAchievementsSections(Il2CppSpacewood.Unity.Achievements achievements)
        {
            // Section 1: Filters
            var filterSection = new NavigationSection("Filters");
            TryAddWithLabel(filterSection, achievements.FilterButton, "Filter");
            TryAddWithLabel(filterSection, achievements.SortButton, "Sort");
            if (filterSection.Elements.Count > 0) _sections.Add(filterSection);

            // Section 2: Achievement entries â€” scan EntryContainer for ButtonBase children
            var achieveSection = new NavigationSection("Achievements");
            try
            {
                if (achievements.EntryContainer != null)
                {
                    var buttons = achievements.EntryContainer.GetComponentsInChildren<ButtonBase>(false);
                    foreach (var btn in buttons)
                    {
                        if (btn == null) continue;
                        try
                        {
                            if (btn.gameObject == null) continue;
                            if (!btn.gameObject.activeInHierarchy) continue;
                            if (!btn.GetInteractable()) continue;
                            achieveSection.Elements.Add(btn);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildAchievementsSections entries error: {ex.Message}");
            }
            if (achieveSection.Elements.Count > 0) _sections.Add(achieveSection);

            // Back button
            var actionsSection = new NavigationSection("Actions");
            FindAndAddBackButton(actionsSection, achievements.gameObject);
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for VersusLobby (waiting room after creating/joining a private match).
        /// Sections: Match Info (read-only summary), Players (player list), Actions (Start, Leave)
        /// </summary>
        /// <summary>
        /// Builds sections for the Chooser overlay (food stock / trinket / cursed trinket selection).
        /// </summary>
        private static void BuildChooserSections(Chooser chooser)
        {
            // Section 1: Choices
            var choiceSection = new NavigationSection("Choices");
            try
            {
                var items = chooser.Items;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var item = items[i];
                            if (item?.Button == null) continue;
                            if (!item.Button.gameObject.activeInHierarchy) continue;
                            choiceSection.Elements.Add(item.Button);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildChooserSections choices error: {ex.Message}");
            }
            if (choiceSection.Elements.Count > 0) _sections.Add(choiceSection);

            // Section 2: Actions â€” View, Skip
            var actionsSection = new NavigationSection("Actions");
            TryAddWithLabel(actionsSection, chooser.VisibleButton, "View");
            TryAddWithLabel(actionsSection, chooser.SkipButton, "Skip");
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        // Track lobby player count for join/leave announcements
        public static int _lastLobbyPlayerCount = 0;

        private static void BuildVersusLobbySections(Il2CppSpacewood.Unity.VersusLobby versusLobby)
        {
            // Section 1: Match Info â€” announce match settings via info pairs
            // Info pairs are text-only (Label + Value), but RulesButton is interactive
            var infoSection = new NavigationSection("Match Info");
            TryAddWithLabel(infoSection, versusLobby.RulesButton, "Custom Rules");
            if (infoSection.Elements.Count > 0) _sections.Add(infoSection);

            // Section 2: Players â€” list of joined players
            var playerSection = new NavigationSection("Players");
            int currentPlayerCount = 0;
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
                            if (item == null) continue;
                            // Use backgroundButton as the focusable element
                            if (item.backgroundButton == null) continue;
                            if (item.backgroundButton.gameObject == null) continue;
                            if (!item.backgroundButton.gameObject.activeInHierarchy) continue;
                            playerSection.Elements.Add(item.backgroundButton);
                            currentPlayerCount++;

                            // Try to get player name for label
                            try
                            {
                                var nameText = item.GetComponentInChildren<Il2CppTMPro.TMP_Text>();
                                if (nameText != null && !string.IsNullOrEmpty(nameText.text))
                                    _labelOverrides[item.backgroundButton.gameObject] = nameText.text;
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildVersusLobbySections players error: {ex.Message}");
            }

            // Player count tracking â€” announcements are handled by PollVersusLobby() in SuperAutoAccessibility.cs
            _lastLobbyPlayerCount = currentPlayerCount;

            if (playerSection.Elements.Count > 0) _sections.Add(playerSection);

            // Section 3: Actions â€” Start, Leave
            var actionsSection = new NavigationSection("Actions");
            TryAddWithLabel(actionsSection, versusLobby.StartButton, "Start");
            TryAddWithLabel(actionsSection, versusLobby.LeaveButton, "Leave");
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);
        }

        /// <summary>
        /// Builds sections for the Hangar/Build phase (shop, team, actions).
        /// Provides Tab/Arrow-based navigation as an alternative to zone keys (S/F/T/D).
        /// </summary>
        private static void BuildHangarSections(HangarMain hangarMain)
        {
            // Section 1: Pet Shop
            var petShopSection = new NavigationSection("Pet Shop");
            try
            {
                var minionShop = hangarMain.MinionShop;
                if (minionShop?.Spaces != null)
                {
                    var spaces = minionShop.Spaces;
                    for (int i = 0; i < spaces.Count; i++)
                    {
                        try
                        {
                            var space = spaces[i];
                            if (space?.gameObject == null) continue;
                            // Look for any SelectableBase on the space for focus
                            var selectable = space.GetComponent<SelectableBase>();
                            if (selectable != null && selectable.gameObject.activeInHierarchy)
                            {
                                string label = "Empty";
                                try
                                {
                                    if (space.MinionModel != null)
                                        label = Gameplay.PetStatsReader.ReadMinionBrief(space.MinionModel);
                                }
                                catch { }
                                TryAddWithLabel(petShopSection, selectable, $"Slot {i + 1}: {label}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildHangarSections pet shop error: {ex.Message}");
            }
            if (petShopSection.Elements.Count > 0) _sections.Add(petShopSection);

            // Section 2: Food Shop
            var foodShopSection = new NavigationSection("Food Shop");
            try
            {
                var spellShop = hangarMain.SpellShop;
                if (spellShop?.Spaces != null)
                {
                    var spaces = spellShop.Spaces;
                    for (int i = 0; i < spaces.Count; i++)
                    {
                        try
                        {
                            var space = spaces[i];
                            if (space?.gameObject == null) continue;
                            var selectable = space.GetComponent<SelectableBase>();
                            if (selectable != null && selectable.gameObject.activeInHierarchy)
                            {
                                string label = "Empty";
                                try
                                {
                                    if (space.SpellModel != null)
                                        label = Gameplay.PetStatsReader.ReadSpell(space.SpellModel);
                                }
                                catch { }
                                TryAddWithLabel(foodShopSection, selectable, $"Slot {i + 1}: {label}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildHangarSections food shop error: {ex.Message}");
            }
            if (foodShopSection.Elements.Count > 0) _sections.Add(foodShopSection);

            // Section 3: Team
            var teamSection = new NavigationSection("Team");
            try
            {
                var army = hangarMain.MinionArmy;
                if (army?.Spaces != null)
                {
                    var items = army.Spaces.Items;
                    if (items != null)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            try
                            {
                                var space = items[i];
                                if (space?.gameObject == null) continue;
                                var selectable = space.GetComponent<SelectableBase>();
                                if (selectable != null && selectable.gameObject.activeInHierarchy)
                                {
                                    string label = "Empty";
                                    try
                                    {
                                        if (space.MinionModel != null)
                                            label = Gameplay.PetStatsReader.ReadMinion(space.MinionModel);
                                    }
                                    catch { }
                                    TryAddWithLabel(teamSection, selectable, $"Slot {i + 1}: {label}");
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildHangarSections team error: {ex.Message}");
            }
            if (teamSection.Elements.Count > 0) _sections.Add(teamSection);

            // Section 4: Actions
            var actionsSection = new NavigationSection("Actions");
            try
            {
                var overlay = hangarMain.Overlay;
                if (overlay != null)
                {
                    TryAddWithLabel(actionsSection, overlay.Roll?.Button, "Roll");
                    TryAddWithLabel(actionsSection, overlay.Freeze?.Button, "Freeze");
                    // End Turn excluded â€” use E keybind to prevent accidental native clicks
                    TryAddWithLabel(actionsSection, overlay.SellButton, "Sell");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildHangarSections actions error: {ex.Message}");
            }
            if (actionsSection.Elements.Count > 0) _sections.Add(actionsSection);

            // Section 5: Top-right options (Opponent, Battle, DeckViewer)
            var optionsSection = new NavigationSection("Options");
            try
            {
                var hangarMenu = hangarMain.Overlay?.Menu;
                if (hangarMenu != null)
                {
                    TryAddWithLabel(optionsSection, hangarMenu.OpponentButton, "View Opponent");
                    TryAddWithLabel(optionsSection, hangarMenu.BattleButton, "Battle");
                    TryAddWithLabel(optionsSection, hangarMenu.DeckViewerButton, "Deck Viewer");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildHangarSections options error: {ex.Message}");
            }
            if (optionsSection.Elements.Count > 0) _sections.Add(optionsSection);

            // If no sections were built, fall back
            if (_sections.Count == 0)
            {
                BuildFallbackSections();
            }
        }

        /// <summary>
        /// Builds sections for the LastBattleMenu overlay (Battle / Playback buttons).
        /// </summary>
        private static void BuildLastBattleMenuSections(LastBattleMenu menu)
        {
            var section = new NavigationSection("Battle Menu");
            try
            {
                TryAddWithLabel(section, menu.BattleButton, "Battle");
                TryAddWithLabel(section, menu.PlaybackButton, "Playback");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildLastBattleMenuSections error: {ex.Message}");
            }
            if (section.Elements.Count > 0)
                _sections.Add(section);
            else
                BuildFallbackSections();
        }

        /// <summary>
        /// Builds sections for the Scoreboard overlay (opponent list).
        /// Each opponent becomes a navigable entry with their team info read out.
        /// </summary>
        private static void BuildScoreboardSections(HangarMain hangarMain)
        {
            try
            {
                var scoreboard = hangarMain.Scoreboard;
                if (scoreboard == null) { BuildFallbackSections(); return; }

                var entries = scoreboard.Entries;
                if (entries == null || entries.Count == 0) { BuildFallbackSections(); return; }

                var opponentsSection = new NavigationSection("Opponents");

                for (int i = 0; i < entries.Count; i++)
                {
                    try
                    {
                        var entry = entries[i];
                        if (entry?.gameObject == null || !entry.gameObject.activeInHierarchy) continue;

                        var btn = entry.Button;
                        if (btn == null) continue;

                        // Build label from entry data
                        string label = $"Opponent {i + 1}";
                        try
                        {
                            // Check if this is the player's own entry
                            bool isSelf = false;
                            try { isSelf = entry.Self; } catch { }
                            if (isSelf) continue; // Skip own entry

                            string displayName = "";
                            try { displayName = entry.DisplayName; } catch { }

                            int lives = 0;
                            try { lives = entry.Lives; } catch { }

                            bool isNext = false;
                            try { isNext = entry.Next; } catch { }

                            // Brief label â€” full detail is now handled by scoreboard detail line navigation
                            if (!string.IsNullOrWhiteSpace(displayName))
                                label = (isNext ? "Next: " : "") + $"{displayName}, {lives} lives";
                            else
                                label = (isNext ? "Next: " : "") + $"Opponent, {lives} lives";
                        }
                        catch { }

                        TryAddWithLabel(opponentsSection, btn, label);
                    }
                    catch { }
                }

                // Add close button
                try
                {
                    TryAddWithLabel(opponentsSection, scoreboard.CloseLeftButton, "Close");
                }
                catch { }
                try
                {
                    if (opponentsSection.Elements.Count == 0 ||
                        (scoreboard.CloseLeftButton?.gameObject == null || !scoreboard.CloseLeftButton.gameObject.activeInHierarchy))
                        TryAddWithLabel(opponentsSection, scoreboard.CloseRightButton, "Close");
                }
                catch { }

                if (opponentsSection.Elements.Count > 0)
                    _sections.Add(opponentsSection);
                else
                    BuildFallbackSections();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildScoreboardSections error: {ex.Message}");
                BuildFallbackSections();
            }
        }

        /// <summary>
        /// Builds sections for the DeckViewer overlay (Controls + tier rows with pets/foods).
        /// </summary>
        private static void BuildDeckViewerSections(DeckViewer deckViewer)
        {
            try
            {
                var itemRows = deckViewer.ItemRows;
                if (itemRows == null || itemRows.Count == 0)
                {
                    BuildFallbackSections();
                    return;
                }

                // Section 1: Controls (row 0 typically has Achievements/Custom/Wacky toggles + Close)
                var controlsSection = new NavigationSection("Controls");
                try
                {
                    TryAddWithLabel(controlsSection, deckViewer.InformationButton, "Information");
                    TryAddWithLabel(controlsSection, deckViewer.CloseButton, "Close");

                    // Also scan row 0 for any toggle buttons
                    if (itemRows.Count > 0)
                    {
                        var row0 = itemRows[0];
                        if (row0?.gameObject != null && row0.gameObject.activeInHierarchy)
                        {
                            // Get all ButtonBase children from the row
                            var buttons = row0.GetComponentsInChildren<ButtonBase>(false);
                            if (buttons != null)
                            {
                                foreach (var btn in buttons)
                                {
                                    try
                                    {
                                        if (btn == null || btn.gameObject == null) continue;
                                        if (!btn.gameObject.activeInHierarchy) continue;
                                        // Skip if already added (Information/Close)
                                        if (btn == deckViewer.InformationButton || btn == deckViewer.CloseButton) continue;
                                        TryAddWithLabel(controlsSection, btn, btn.gameObject.name);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
                if (controlsSection.Elements.Count > 0) _sections.Add(controlsSection);

                // Tier sections (rows 1+)
                for (int rowIdx = 1; rowIdx < itemRows.Count; rowIdx++)
                {
                    try
                    {
                        var row = itemRows[rowIdx];
                        if (row?.gameObject == null || !row.gameObject.activeInHierarchy) continue;

                        // Build section name from row text
                        string tierName = $"Tier {rowIdx}";
                        try
                        {
                            var textComp = row.Text;
                            if (textComp != null)
                            {
                                string rowText = textComp.text;
                                if (!string.IsNullOrWhiteSpace(rowText))
                                    tierName = rowText;
                            }
                        }
                        catch { }

                        // Check if tier is locked (Conceal overlay is active)
                        bool locked = false;
                        try
                        {
                            if (row.Conceal?.gameObject != null && row.Conceal.gameObject.activeInHierarchy)
                                locked = true;
                        }
                        catch { }
                        if (locked) tierName += " (locked)";

                        var tierSection = new NavigationSection(tierName);

                        // Scan PetContainer for DeckViewerItems
                        try
                        {
                            if (row.PetContainer != null)
                            {
                                var petItems = row.PetContainer.GetComponentsInChildren<DeckViewerItem>(false);
                                if (petItems != null)
                                {
                                    foreach (var item in petItems)
                                    {
                                        AddDeckViewerItem(tierSection, item);
                                    }
                                }
                            }
                        }
                        catch { }

                        // Scan FoodContainer for DeckViewerItems
                        try
                        {
                            if (row.FoodContainer != null)
                            {
                                var foodItems = row.FoodContainer.GetComponentsInChildren<DeckViewerItem>(false);
                                if (foodItems != null)
                                {
                                    foreach (var item in foodItems)
                                    {
                                        AddDeckViewerItem(tierSection, item);
                                    }
                                }
                            }
                        }
                        catch { }

                        if (tierSection.Elements.Count > 0)
                            _sections.Add(tierSection);
                    }
                    catch { }
                }

                if (_sections.Count == 0) BuildFallbackSections();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildDeckViewerSections error: {ex.Message}");
                BuildFallbackSections();
            }
        }

        /// <summary>
        /// Adds a DeckViewerItem to a section, labeling it using ButtonBase.GetTitle()
        /// with enum-based fallback. Uses TryAddEvenIfDisabled to include locked items.
        /// </summary>
        private static void AddDeckViewerItem(NavigationSection section, DeckViewerItem item)
        {
            if (item == null) return;
            try
            {
                var btn = item.Button;
                if (btn == null || btn.gameObject == null || !btn.gameObject.activeInHierarchy) return;

                string label = "";

                // Primary: use ButtonBase.GetTitle() â€” the game's own labeling
                try
                {
                    label = btn.GetTitle();
                }
                catch { }

                // Fallback: try enum-based names if GetTitle() returned nothing
                if (string.IsNullOrWhiteSpace(label))
                {
                    // Try Minion
                    try
                    {
                        var minionNullable = item.Minion;
                        try
                        {
                            var minion = minionNullable.Value;
                            label = Gameplay.PetStatsReader.SplitCamelCase(minion.ToString());
                        }
                        catch { /* Nullable has no value */ }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    // Try Spell (food item)
                    try
                    {
                        var spellNullable = item.Spell;
                        try
                        {
                            var spell = spellNullable.Value;
                            label = Gameplay.PetStatsReader.SplitCamelCase(spell.ToString());
                        }
                        catch { /* Nullable has no value */ }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    // Try Perk
                    try
                    {
                        var perkNullable = item.Perk;
                        try
                        {
                            var perk = perkNullable.Value;
                            label = "Perk: " + Gameplay.PetStatsReader.GetPerkDisplayName(perk);
                        }
                        catch { /* Nullable has no value */ }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(label)) label = "Unknown item";

                // Use TryAddEvenIfDisabled so locked tier items still appear
                TryAddEvenIfDisabled(section, btn, label);
            }
            catch { }
        }

        /// <summary>
        /// Like TryAddWithLabel, but does NOT check GetInteractable().
        /// Used for DeckViewer items where locked tier items should still be listed.
        /// </summary>
        private static void TryAddEvenIfDisabled(NavigationSection section, SelectableBase button, string label)
        {
            if (button == null) return;
            try
            {
                if (button.gameObject == null) return;
                if (!button.gameObject.activeInHierarchy) return;
                section.Elements.Add(button);
                _labelOverrides[button.gameObject] = label;
            }
            catch { }
        }

        /// <summary>
        /// Helper: finds a Back button in a page's hierarchy and adds it with a label override.
        /// </summary>
        private static void FindAndAddBackButton(NavigationSection section, GameObject pageRoot)
        {
            if (pageRoot == null) return;
            try
            {
                var allButtons = pageRoot.GetComponentsInChildren<ButtonBase>(false);
                foreach (var btn in allButtons)
                {
                    if (btn == null || btn.gameObject == null) continue;
                    if (!btn.gameObject.activeInHierarchy) continue;
                    if (btn.gameObject.name == "Back" || btn.gameObject.name == "BackButton")
                    {
                        TryAddWithLabel(section, btn, "Back");
                        return;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Builds sections for the News/Announcement overlay dialog.
        /// Creates labeled sections for pinboard items (Major/Minor/Free) and actions (Dismiss/Link).
        /// </summary>
        private static void BuildNewsDialogSections(Il2CppSpacewood.Unity.News news)
        {
            try
            {
                var announcement = news.Announcement;
                if (announcement == null)
                {
                    BuildFallbackSections();
                    return;
                }

                var pinboard = announcement.Pinboard;

                // Build pinboard item sections by scanning containers
                if (pinboard != null)
                {
                    AddPinboardSection(pinboard, "MajorItems", "Major Changes");
                    AddPinboardSection(pinboard, "MinorItems", "Minor Changes");
                    AddPinboardSection(pinboard, "FreeItems", "Free Items");
                }

                // Changes section: the changelog text at the bottom, split by line
                // Changes section: the changelog text at the bottom, split by individual items.
                // Uses the scrollbar as anchor (a Selectable that won't trigger dialog actions).
                try
                {
                    var textMesh = announcement.TextMesh;
                    if (textMesh != null && !string.IsNullOrWhiteSpace(textMesh.text))
                    {
                        string cleaned = TextExtractor.CleanTextPublic(textMesh.text);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                        {
                            // CleanTextPublic collapses newlines to spaces, so format is:
                            // "- Change1. - Change2. - Change3."
                            // Use regex to split on " - " or leading "- "
                            var changeLines = new System.Collections.Generic.List<string>();
                            var matches = System.Text.RegularExpressions.Regex.Matches(cleaned, @"(?:^|\s)-\s+([^-](?:[^-]|-(?!\s))*?)(?=\s-\s|$)");
                            if (matches.Count > 0)
                            {
                                foreach (System.Text.RegularExpressions.Match m in matches)
                                {
                                    string line = m.Groups[1].Value.Trim().TrimEnd('.');
                                    if (!string.IsNullOrWhiteSpace(line))
                                        changeLines.Add(line);
                                }
                            }

                            // Fallback: if regex didn't match, just split on " - "
                            if (changeLines.Count == 0)
                            {
                                foreach (var rawLine in cleaned.Split(new[] { " - ", "- " }, System.StringSplitOptions.RemoveEmptyEntries))
                                {
                                    string line = rawLine.TrimStart('-', ' ').Trim().TrimEnd('.');
                                    if (!string.IsNullOrWhiteSpace(line))
                                        changeLines.Add(line);
                                }
                            }

                            if (changeLines.Count > 0)
                            {
                                var changesSection = new NavigationSection("Changes");
                                changesSection.Tag = string.Join("\n", changeLines);

                                // Use the scrollbar as anchor â€” it's a Selectable that won't
                                // trigger dialog actions like DismissButton would
                                UnityEngine.UI.Scrollbar scrollbar = null;
                                try
                                {
                                    scrollbar = announcement.GetComponentInChildren<UnityEngine.UI.Scrollbar>(false);
                                }
                                catch { }

                                if (scrollbar != null)
                                {
                                    var selectableBase = scrollbar.GetComponent<SelectableBase>();
                                    if (selectableBase == null)
                                    {
                                        // Scrollbar is a Unity Selectable but not a SelectableBase.
                                        // Use DismissButton as fallback but with a clear label.
                                        if (announcement.DismissButton?.gameObject != null)
                                        {
                                            TryAddWithLabel(changesSection, announcement.DismissButton,
                                                $"{changeLines.Count} patch notes. Use left and right arrows to read.");
                                        }
                                    }
                                    else
                                    {
                                        TryAddWithLabel(changesSection, selectableBase,
                                            $"{changeLines.Count} patch notes. Use left and right arrows to read.");
                                    }
                                }
                                else if (announcement.DismissButton?.gameObject != null)
                                {
                                    TryAddWithLabel(changesSection, announcement.DismissButton,
                                        $"{changeLines.Count} patch notes. Use left and right arrows to read.");
                                }

                                if (changesSection.Elements.Count > 0)
                                    _sections.Add(changesSection);
                            }
                        }
                    }
                }
                catch { }

                // Actions section: Dismiss and optional Link
                var actionsSection = new NavigationSection("Actions");
                try
                {
                    if (announcement.LinkButton?.gameObject != null &&
                        announcement.LinkButton.gameObject.activeInHierarchy)
                        TryAddWithLabel(actionsSection, announcement.LinkButton, "Link");
                }
                catch { }
                TryAddWithLabel(actionsSection, announcement.DismissButton, "Dismiss");
                if (actionsSection.Elements.Count > 0)
                    _sections.Add(actionsSection);

                // Back button (NotchPadding/Layout/Back)
                try
                {
                    var backBtns = news.GetComponentsInChildren<ButtonBase>(false);
                    if (backBtns != null)
                    {
                        foreach (var btn in backBtns)
                        {
                            if (btn == null || btn.gameObject == null) continue;
                            if (!btn.gameObject.activeInHierarchy) continue;
                            if (btn.gameObject.name == "Back" || btn.gameObject.name == "BackButton")
                            {
                                var navSection = new NavigationSection("Navigation");
                                TryAddWithLabel(navSection, btn, "Back");
                                if (navSection.Elements.Count > 0) _sections.Add(navSection);
                                break;
                            }
                        }
                    }
                }
                catch { }

                if (_sections.Count == 0)
                    BuildFallbackSections();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildNewsDialogSections error: {ex.Message}");
                BuildFallbackSections();
            }
        }

        /// <summary>
        /// Finds a named container under a Pinboard and adds its DeckViewerItems as a labeled section.
        /// </summary>
        private static void AddPinboardSection(Il2CppSpacewood.Unity.MonoBehaviours.Build.Pinboard pinboard, string containerName, string sectionName)
        {
            try
            {
                // Find the container by name (e.g. "MajorItems", "MinorItems", "FreeItems")
                Transform container = null;
                for (int i = 0; i < pinboard.transform.childCount; i++)
                {
                    var child = pinboard.transform.GetChild(i);
                    // Check direct children and one level deeper (Padding/MajorItems)
                    if (child.name == containerName)
                    {
                        container = child;
                        break;
                    }
                    for (int j = 0; j < child.childCount; j++)
                    {
                        var grandchild = child.GetChild(j);
                        if (grandchild.name == containerName)
                        {
                            container = grandchild;
                            break;
                        }
                    }
                    if (container != null) break;
                }

                if (container == null || !container.gameObject.activeInHierarchy) return;

                var section = new NavigationSection(sectionName);
                var items = container.GetComponentsInChildren<DeckViewerItem>(false);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        AddNewsPinboardItem(section, item);
                    }
                }

                if (section.Elements.Count > 0)
                    _sections.Add(section);
            }
            catch { }
        }

        /// <summary>
        /// Adds a News pinboard DeckViewerItem using Icon sprite name.
        /// Il2Cpp Nullable enums return default (Ant=0) for all items, and GetTitle()
        /// returns stale template text. The Icon sprite name is the only reliable source.
        /// </summary>
        private static void AddNewsPinboardItem(NavigationSection section, DeckViewerItem item)
        {
            if (item == null) return;
            try
            {
                var btn = item.Button;
                if (btn == null || btn.gameObject == null || !btn.gameObject.activeInHierarchy) return;

                string label = "";

                // Read the Icon sprite name â€” this is set per-item and matches the pet/food/perk name
                try
                {
                    var icon = item.Icon;
                    if (icon != null && icon.sprite != null)
                    {
                        string spriteName = icon.sprite.name;
                        if (!string.IsNullOrWhiteSpace(spriteName))
                        {
                            // Strip resolution suffixes like "_2x", "_4x"
                            spriteName = System.Text.RegularExpressions.Regex.Replace(spriteName, @"_\d+x$", "");
                            label = Gameplay.PetStatsReader.SplitCamelCase(spriteName);
                        }
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(label)) label = "Unknown item";

                TryAddWithLabel(section, btn, label);
            }
            catch { }
        }

        /// <summary>
        /// Builds sections for dialog/consent pages (privacy terms, EULA, etc.)
        /// by scanning for all interactive buttons so Tab navigation works.
        /// </summary>
        private static void BuildDialogPageSections(string pageName)
        {
            try
            {
                var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                var page = pageManager?.CurrentPage;
                if (page == null) { BuildFallbackSections(); return; }

                var dialogSection = new NavigationSection("Dialog");
                var buttons = page.GetComponentsInChildren<ButtonBase>(false);
                foreach (var btn in buttons)
                {
                    if (btn == null) continue;
                    try
                    {
                        if (btn.gameObject == null) continue;
                        if (!btn.gameObject.activeInHierarchy) continue;
                        if (!btn.GetInteractable()) continue;
                        dialogSection.Elements.Add(btn);
                    }
                    catch { }
                }

                if (dialogSection.Elements.Count > 0)
                    _sections.Add(dialogSection);
                else
                    BuildFallbackSections(); // No buttons found, try generic scan
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"BuildDialogPageSections error: {ex.Message}");
                BuildFallbackSections();
            }
        }

        private static void BuildFallbackSections()
        {
            var elements = SAPMod.GetFocusableElements();
            if (elements.Count > 0)
            {
                var section = new NavigationSection("Content");
                section.Elements.AddRange(elements);
                _sections.Add(section);
            }
        }

        private static void TryAdd(NavigationSection section, SelectableBase button)
        {
            if (button == null) return;
            try
            {
                if (button.gameObject == null) return;
                if (!button.gameObject.activeInHierarchy) return;
                if (!button.GetInteractable()) return;
                section.Elements.Add(button);
            }
            catch { }
        }

        /// <summary>
        /// TryAdd with a label override for icon-only buttons.
        /// </summary>
        private static void TryAddWithLabel(NavigationSection section, SelectableBase button, string label)
        {
            if (button == null) return;
            try
            {
                if (button.gameObject == null) return;
                if (!button.gameObject.activeInHierarchy) return;
                if (!button.GetInteractable()) return;
                section.Elements.Add(button);

                // Register label override (used if TextExtractor returns empty)
                _labelOverrides[button.gameObject] = label;
            }
            catch { }
        }

        public static void MoveToNextSection()
        {
            var sections = GetSections();
            if (sections.Count == 0) return;
            _currentSectionIndex = (_currentSectionIndex + 1) % sections.Count;
            _currentElementIndex = 0;
            FocusCurrentAndAnnounceSection();
        }

        public static void MoveToPrevSection()
        {
            var sections = GetSections();
            if (sections.Count == 0) return;
            _currentSectionIndex = (_currentSectionIndex - 1 + sections.Count) % sections.Count;
            _currentElementIndex = 0;
            FocusCurrentAndAnnounceSection();
        }

        public static void MoveToNextElement()
        {
            var sections = GetSections();
            if (sections.Count == 0) return;
            if (!ValidateSection()) return;
            var section = sections[_currentSectionIndex];
            _currentElementIndex = (_currentElementIndex + 1) % section.Elements.Count;
            FocusCurrent();
        }

        public static void MoveToPrevElement()
        {
            var sections = GetSections();
            if (sections.Count == 0) return;
            if (!ValidateSection()) return;
            var section = sections[_currentSectionIndex];
            _currentElementIndex = (_currentElementIndex - 1 + section.Elements.Count) % section.Elements.Count;
            FocusCurrent();
        }

        private static void FocusCurrentAndAnnounceSection()
        {
            var sections = GetSections();
            if (sections.Count == 0) return;
            if (!ValidateSection()) return;
            var section = sections[_currentSectionIndex];

            if (sections.Count > 1)
            {
                AccessibilityManager.SetCurrentSectionName(section.Name);
            }

            if (EventSystem.current != null)
            {
                _modDrivenSelection = true;
                EventSystem.current.SetSelectedGameObject(section.Elements[_currentElementIndex].gameObject);
            }
        }

        private static void FocusCurrent()
        {
            var sections = GetSections();
            if (sections.Count == 0) return;
            if (!ValidateSection()) return;
            var section = sections[_currentSectionIndex];

            AccessibilityManager.ClearCurrentSectionName();

            if (EventSystem.current != null)
            {
                _modDrivenSelection = true;
                EventSystem.current.SetSelectedGameObject(section.Elements[_currentElementIndex].gameObject);
            }
        }

        /// <summary>
        /// Sync internal indices from current EventSystem selection (mouse clicks, game-driven focus)
        /// </summary>
        public static void SyncFromEventSystem()
        {
            var currentGO = EventSystem.current?.currentSelectedGameObject;
            if (currentGO == null) return;

            var sections = GetSections();
            for (int s = 0; s < sections.Count; s++)
            {
                for (int e = 0; e < sections[s].Elements.Count; e++)
                {
                    try
                    {
                        if (sections[s].Elements[e].gameObject == currentGO)
                        {
                            _currentSectionIndex = s;
                            _currentElementIndex = e;
                            return;
                        }
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Focus the first element of the first section. Called after page/modal transitions.
        /// </summary>
        public static void FocusFirstSection()
        {
            InvalidateCache();
            var sections = GetSections();
            if (sections.Count == 0) return;

            // Check if a section builder requested a specific initial focus (e.g., PackShop selected pack)
            if (_pendingInitialFocus.HasValue)
            {
                var focus = _pendingInitialFocus.Value;
                _pendingInitialFocus = null;
                if (focus.section < sections.Count && focus.element < sections[focus.section].Elements.Count)
                {
                    _currentSectionIndex = focus.section;
                    _currentElementIndex = focus.element;

                    if (!ValidateSection()) return;

                    if (sections.Count > 1)
                    {
                        AccessibilityManager.SetCurrentSectionName(sections[_currentSectionIndex].Name);
                    }

                    if (EventSystem.current != null)
                    {
                        _modDrivenSelection = true;
                        EventSystem.current.SetSelectedGameObject(
                            sections[_currentSectionIndex].Elements[_currentElementIndex].gameObject);
                    }
                    return;
                }
            }

            _currentSectionIndex = 0;
            _currentElementIndex = 0;

            if (!ValidateSection()) return;

            if (sections.Count > 1)
            {
                AccessibilityManager.SetCurrentSectionName(sections[0].Name);
            }

            if (EventSystem.current != null)
            {
                _modDrivenSelection = true;
                EventSystem.current.SetSelectedGameObject(sections[0].Elements[0].gameObject);
            }
        }
    }
}
