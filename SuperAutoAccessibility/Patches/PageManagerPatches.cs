using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;

namespace SuperAutoAccessibility.Patches
{
    public static class PageManagerPatches
    {
        // One-liner deferred announcement
        private static bool _pendingOneLinerCheck = false;
        private static int _oneLinerCheckFrame = 0;
        private const int ONE_LINER_FRAME_DELAY = 15;

        // VersusLobby deferred summary announcement
        private static bool _pendingVersusLobbySummary = false;
        private static int _versusLobbySummaryFrame = 0;
        private const int VERSUS_LOBBY_FRAME_DELAY = 20;

        public static void Initialize(HarmonyLib.Harmony harmony)
        {
            try
            {
                var original = AccessTools.Method(
                    typeof(Il2CppSpacewood.Unity.PageManager),
                    "Open",
                    new[] { typeof(Il2CppSpacewood.Unity.Page) }
                );

                var postfix = AccessTools.Method(
                    typeof(PageManagerPatches),
                    nameof(Open_Postfix)
                );

                if (original != null && postfix != null)
                {
                    harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                    MelonLogger.Msg("PageManager.Open patched successfully");
                }
                else
                {
                    MelonLogger.Warning("Failed to find PageManager.Open");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error applying PageManager patch: {ex.Message}");
            }
        }

        // Page names that should have their full content read (dialog-like pages)
        private static readonly string[] DialogPageNames = {
            "EmailVerificationNotice",
            "Notice",
            "Confirm",
            "Alert",
            "Warning",
            "Error",
            "Message",
            "Popup",
            "Dialog",
            "Privacy",
            "Consent",
            "Terms",
            "GDPR",
            "Agreement",
            "Accept",
            "Policy",
            "Legal",
            "EULA"
        };

        /// <summary>
        /// Returns true if the given page name matches a dialog page pattern.
        /// Used by NavigationSection to build dialog button sections.
        /// </summary>
        public static bool IsDialogPageName(string pageName)
        {
            if (string.IsNullOrEmpty(pageName)) return false;
            foreach (var dialogName in DialogPageNames)
            {
                if (pageName.Contains(dialogName))
                    return true;
            }
            return false;
        }

        private static void Open_Postfix(Il2CppSpacewood.Unity.Page page)
        {
            if (page == null || page.gameObject == null) return;

            try
            {
                string pageName = page.gameObject.name;
                MelonLogger.Msg($"[Page Debug] PageManager.Open postfix for: {pageName}");

                // Invalidate section cache on page change
                SectionManager.InvalidateCache();

                // Set page transition cooldown to prevent Enter key carry-over
                SAPMod.SetPageTransitionCooldown();

                // Check if this is a dialog-like page that should have content read
                bool isDialogPage = false;
                foreach (var dialogName in DialogPageNames)
                {
                    if (pageName.Contains(dialogName))
                    {
                        isDialogPage = true;
                        break;
                    }
                }

                if (isDialogPage)
                {
                    // Queue page for full content announcement
                    AccessibilityManager.QueuePageAnnouncement(page);
                }
                else
                {
                    // Store page name as prefix for the first element announcement
                    string title = SplitCamelCase(pageName);
                    if (!string.IsNullOrEmpty(title))
                    {
                        AccessibilityManager.SetPendingPagePrefix(title);
                    }

                    // If this is the main menu / Lobby page, queue one-liner announcement
                    if (pageName.Contains("Lobby") || pageName.Contains("MainMenu") || pageName.Contains("Menu"))
                    {
                        _pendingOneLinerCheck = true;
                        _oneLinerCheckFrame = ONE_LINER_FRAME_DELAY;
                    }

                    // If this is VersusLobby, queue match summary announcement
                    if (pageName.Contains("VersusLobby"))
                    {
                        _pendingVersusLobbySummary = true;
                        _versusLobbySummaryFrame = VERSUS_LOBBY_FRAME_DELAY;
                    }
                }

                // Replay page detection â€” notify the main mod to start replay navigation
                if (pageName == "Replay")
                {
                    SAPMod.NotifyReplayPageOpened();
                }
                else
                {
                    // Any other page means we've left the replay page
                    SAPMod.NotifyReplayPageClosed();
                }

                // Defer focus to first element â€” elements may not be ready yet in this postfix
                AccessibilityManager.RequestFocusFirst();

                // Auto-dump all UI elements for debugging
                UIElementDumper.QueueDump();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error in PageManager.Open postfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from SAPMod.OnUpdate() to tick down the one-liner announcement delay
        /// </summary>
        public static void ProcessOneLinerPending()
        {
            if (!_pendingOneLinerCheck) return;

            _oneLinerCheckFrame--;
            if (_oneLinerCheckFrame > 0) return;

            _pendingOneLinerCheck = false;

            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.Menu>();
                if (menu != null && menu.OneLiner != null)
                {
                    string oneLinerText = menu.OneLiner.text;
                    if (!string.IsNullOrWhiteSpace(oneLinerText))
                    {
                        AccessibilityManager.Announce(oneLinerText.Trim(), interrupt: false);
                        MelonLogger.Msg($"[OneLiner] Announced: {oneLinerText.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"OneLiner announcement error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from SAPMod.OnUpdate() to tick down the VersusLobby summary announcement delay
        /// </summary>
        public static void ProcessVersusLobbySummaryPending()
        {
            if (!_pendingVersusLobbySummary) return;

            _versusLobbySummaryFrame--;
            if (_versusLobbySummaryFrame > 0) return;

            _pendingVersusLobbySummary = false;

            try
            {
                var versusLobby = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.VersusLobby>();
                if (versusLobby == null) return;

                var parts = new System.Collections.Generic.List<string>();

                // Game name
                try
                {
                    if (versusLobby.InfoPairGameName?.Value != null &&
                        !string.IsNullOrWhiteSpace(versusLobby.InfoPairGameName.Value.text))
                        parts.Add(versusLobby.InfoPairGameName.Value.text.Trim());
                }
                catch { }

                // Player count
                try
                {
                    if (versusLobby.InfoPairPlayerCount?.Value != null &&
                        !string.IsNullOrWhiteSpace(versusLobby.InfoPairPlayerCount.Value.text))
                        parts.Add($"Players: {versusLobby.InfoPairPlayerCount.Value.text.Trim()}");
                }
                catch { }

                // Turn duration
                try
                {
                    if (versusLobby.InfoPairTurnDuration?.Value != null &&
                        !string.IsNullOrWhiteSpace(versusLobby.InfoPairTurnDuration.Value.text))
                        parts.Add($"Duration: {versusLobby.InfoPairTurnDuration.Value.text.Trim()}");
                }
                catch { }

                // Pack mode
                try
                {
                    if (versusLobby.InfoPairPackMode?.Value != null &&
                        !string.IsNullOrWhiteSpace(versusLobby.InfoPairPackMode.Value.text))
                        parts.Add($"Packs: {versusLobby.InfoPairPackMode.Value.text.Trim()}");
                }
                catch { }

                // Spectator mode
                try
                {
                    if (versusLobby.InfoPairSpectator?.Value != null &&
                        !string.IsNullOrWhiteSpace(versusLobby.InfoPairSpectator.Value.text))
                        parts.Add($"Spectators: {versusLobby.InfoPairSpectator.Value.text.Trim()}");
                }
                catch { }

                if (parts.Count > 0)
                {
                    string summary = string.Join(", ", parts);
                    AccessibilityManager.Announce(summary, interrupt: false);
                    MelonLogger.Msg($"[VersusLobby] Announced summary: {summary}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"VersusLobby summary error: {ex.Message}");
            }
        }

        // "MainMenuPage" â†’ "Main Menu Page"
        public static string SplitCamelCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return Regex.Replace(input, @"([a-z])([A-Z])", "$1 $2").Trim();
        }
    }
}
