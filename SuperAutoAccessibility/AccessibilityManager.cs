using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using Il2CppSpacewood.Unity.UI;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Core accessibility manager that coordinates all UI announcements
    /// </summary>
    public static class AccessibilityManager
    {
        // State tracking
        private static GameObject _lastAnnouncedObject = null;
        private static string _lastAnnouncedText = "";
        private static float _lastAnnouncementTime = 0f;
        private const float ANNOUNCEMENT_THROTTLE = 0.1f;

        // Tooltip delay tracking
        private static GameObject _currentSelectedObject = null;
        private static float _selectionTime = 0f;
        private static bool _tooltipAnnounced = false;
        private const float TOOLTIP_DELAY = 0.5f;

        // Section name announcement (set by SectionManager when entering a new section)
        private static string _pendingSectionName = null;

        // Page prefix â€” combined with the first element announcement after a page transition
        private static string _pendingPagePrefix = null;

        // Track visible texts at selection time to detect newly visible tooltip text
        private static HashSet<string> _textsAtSelectionTime = new HashSet<string>();

        // Modal stack for Escape handling
        private static Stack<Il2CppSpacewood.Unity.UI.Modal> _modalStack = new Stack<Il2CppSpacewood.Unity.UI.Modal>();

        // Pending modals to announce after a delay (Awake fires before text is populated)
        private static Queue<(Il2CppSpacewood.Unity.UI.Modal modal, int frameDelay)> _pendingModalAnnouncements = new Queue<(Il2CppSpacewood.Unity.UI.Modal, int)>();
        private const int MODAL_ANNOUNCE_FRAME_DELAY = 30; // Wait more frames for text to populate

        // Pending pages to announce after a delay (Open fires before text is populated)
        private static Queue<(Il2CppSpacewood.Unity.Page page, int frameDelay)> _pendingPageAnnouncements = new Queue<(Il2CppSpacewood.Unity.Page, int)>();
        private const int PAGE_ANNOUNCE_FRAME_DELAY = 10; // Wait for text to populate

        // Deferred FocusFirst â€” set by PageManager patch, executed next frame when elements are ready
        private static bool _pendingFocusFirst = false;

        public static void RequestFocusFirst()
        {
            _pendingFocusFirst = true;
        }

        public static void CancelPendingFocusFirst()
        {
            _pendingFocusFirst = false;
        }

        /// <summary>
        /// Cancel all pending deferred announcements (modals, pages, focus).
        /// Used by exclusive overlay polls (TallyArenaReward, IconAlert, etc.) to prevent
        /// deferred announcements from interrupting their custom announcements.
        /// </summary>
        public static void CancelAllPending()
        {
            _pendingFocusFirst = false;
            _pendingModalAnnouncements.Clear();
            _pendingPageAnnouncements.Clear();
        }

        public static void SetCurrentSectionName(string name)
        {
            _pendingSectionName = name;
        }

        public static void ClearCurrentSectionName()
        {
            _pendingSectionName = null;
        }

        public static void SetPendingPagePrefix(string prefix)
        {
            _pendingPagePrefix = prefix;
        }

        public static void Initialize()
        {
            MelonLogger.Msg("AccessibilityManager initialized");
        }

        public static void OnUpdate()
        {
            // Check for delayed tooltip announcement
            if (_currentSelectedObject != null && !_tooltipAnnounced)
            {
                if (Time.time - _selectionTime > TOOLTIP_DELAY)
                {
                    _tooltipAnnounced = true; // Set true immediately to avoid repeated attempts
                    AnnounceTooltip(_currentSelectedObject);
                }
            }
        }

        // --- Modal stack ---

        public static void PushModal(Il2CppSpacewood.Unity.UI.Modal modal)
        {
            _modalStack.Push(modal);
        }

        public static void PopModal()
        {
            if (_modalStack.Count > 0)
                _modalStack.Pop();
        }

        public static Il2CppSpacewood.Unity.UI.Modal GetActiveModal()
        {
            return _modalStack.Count > 0 ? _modalStack.Peek() : null;
        }

        /// <summary>
        /// Queue a modal for announcement after a delay (text isn't populated yet in Awake)
        /// </summary>
        public static void QueueModalAnnouncement(Il2CppSpacewood.Unity.UI.Modal modal)
        {
            _pendingModalAnnouncements.Enqueue((modal, MODAL_ANNOUNCE_FRAME_DELAY));
        }

        /// <summary>
        /// Queue a page for content announcement after a delay (text isn't fully populated in Open postfix)
        /// </summary>
        public static void QueuePageAnnouncement(Il2CppSpacewood.Unity.Page page)
        {
            _pendingPageAnnouncements.Enqueue((page, PAGE_ANNOUNCE_FRAME_DELAY));
        }

        /// <summary>
        /// Called from OnUpdate â€” flushes any pending modal announcements now that text is populated
        /// </summary>
        public static void ProcessPending()
        {
            // Flush deferred FocusFirst from PageManager.Open (elements not ready in patch itself)
            if (_pendingFocusFirst)
            {
                _pendingFocusFirst = false;
                FocusFirst();
            }

            // Process modal announcements with frame delay
            var stillPending = new Queue<(Il2CppSpacewood.Unity.UI.Modal modal, int frameDelay)>();
            while (_pendingModalAnnouncements.Count > 0)
            {
                var (modal, frameDelay) = _pendingModalAnnouncements.Dequeue();
                if (modal == null || modal.gameObject == null) continue;

                if (frameDelay > 0)
                {
                    // Not ready yet, re-queue with decremented delay
                    stillPending.Enqueue((modal, frameDelay - 1));
                }
                else
                {
                    // Ready to announce - get ALL text from modal hierarchy
                    MelonLogger.Msg($"[Modal Debug] Modal GO name: {modal.gameObject.name}");

                    // Try the modal's own hierarchy first
                    string allText = TextExtractor.GetAllTextFromHierarchy(modal.gameObject);
                    MelonLogger.Msg($"[Modal Debug] Direct hierarchy text: '{allText}'");

                    // If no text found, try searching up to find the full modal container
                    if (string.IsNullOrEmpty(allText))
                    {
                        // Search parents for a larger container that might have the text
                        Transform parent = modal.transform.parent;
                        while (parent != null && string.IsNullOrEmpty(allText))
                        {
                            MelonLogger.Msg($"[Modal Debug] Trying parent: {parent.name}");
                            allText = TextExtractor.GetAllTextFromHierarchy(parent.gameObject);
                            if (!string.IsNullOrEmpty(allText))
                            {
                                MelonLogger.Msg($"[Modal Debug] Found text in parent: '{allText}'");
                            }
                            parent = parent.parent;
                        }
                    }

                    string announcement = string.IsNullOrEmpty(allText) ? "dialog" : allText;
                    Announce(announcement);

                    // Focus first interactive element in the modal so Tab starts from there
                    FocusFirst();
                }
            }
            // Re-add items that still need to wait
            while (stillPending.Count > 0)
            {
                _pendingModalAnnouncements.Enqueue(stillPending.Dequeue());
            }

            // Process page announcements with frame delay
            var stillPendingPages = new Queue<(Il2CppSpacewood.Unity.Page page, int frameDelay)>();
            while (_pendingPageAnnouncements.Count > 0)
            {
                var (page, frameDelay) = _pendingPageAnnouncements.Dequeue();
                if (page == null || page.gameObject == null) continue;

                if (frameDelay > 0)
                {
                    // Not ready yet, re-queue with decremented delay
                    stillPendingPages.Enqueue((page, frameDelay - 1));
                }
                else
                {
                    // Ready to announce - get ALL text from page hierarchy
                    MelonLogger.Msg($"[Page Debug] Reading content for page: {page.gameObject.name}");

                    string allText = TextExtractor.GetAllTextFromHierarchy(page.gameObject);
                    MelonLogger.Msg($"[Page Debug] Page text: '{allText}'");

                    if (!string.IsNullOrEmpty(allText))
                    {
                        Announce(allText);
                    }
                    else
                    {
                        // Fallback to page name if no text found
                        string title = Patches.PageManagerPatches.SplitCamelCase(page.gameObject.name);
                        if (!string.IsNullOrEmpty(title))
                        {
                            Announce(title);
                        }
                    }
                }
            }
            // Re-add items that still need to wait
            while (stillPendingPages.Count > 0)
            {
                _pendingPageAnnouncements.Enqueue(stillPendingPages.Dequeue());
            }
        }

        /// <summary>
        /// Scans all focusable elements, selects the first one (top-left in reading order).
        /// Called after page/modal transitions to ensure focus starts at the top.
        /// </summary>
        public static void FocusFirst()
        {
            if (EventSystem.current == null) return;

            try
            {
                SectionManager.FocusFirstSection();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"FocusFirst error: {ex.Message}");
            }

            // If page prefix is still pending (no element was focused to consume it), announce it standalone
            if (_pendingPagePrefix != null)
            {
                string prefix = _pendingPagePrefix;
                _pendingPagePrefix = null;
                Announce(prefix);
            }
        }

        private static Vector2 GetPos(SelectableBase sb)
        {
            var rt = sb.GetComponent<RectTransform>();
            if (rt == null) return Vector2.zero;
            if (Camera.main != null)
            {
                Vector3 sp = Camera.main.WorldToScreenPoint(rt.position);
                return new Vector2(sp.x, sp.y);
            }
            return new Vector2(rt.position.x, rt.position.y);
        }

        // --- Announcements ---

        /// <summary>
        /// Main announcement function called when UI elements are selected
        /// </summary>
        private static bool _isAnnouncing = false;

        /// <summary>
        /// Main announcement function called when UI elements are selected
        /// </summary>
        public static void AnnounceSelectedElement(GameObject selected, bool fromHover = false)
        {
            if (selected == null) return;
            if (_isAnnouncing) return;

            _isAnnouncing = true;
            try
            {
                // Sync section tracking with current EventSystem state
                SectionManager.SyncFromEventSystem();

                // Throttle rapid duplicate announcements
                if (selected == _lastAnnouncedObject &&
                    Time.time - _lastAnnouncementTime < ANNOUNCEMENT_THROTTLE)
                {
                    // If it's a hover following a selection (or vice versa) of the same object, 
                    // we generally still want to ensure pointer events are handled if needed,
                    // but for announcement throttling we skip.
                    return;
                }

                // Force PointerEnter for keyboard navigation to update tooltips
                if (!fromHover)
                {
                    try
                    {
                        var pointerData = new PointerEventData(EventSystem.current);
                        
                        // Exit previous
                        if (_currentSelectedObject != null && _currentSelectedObject != selected)
                        {
                            ExecuteEvents.Execute(_currentSelectedObject, pointerData, ExecuteEvents.pointerExitHandler);
                        }

                        // Enter new
                        ExecuteEvents.Execute(selected, pointerData, ExecuteEvents.pointerEnterHandler);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"Error forcing pointer events: {ex.Message}");
                    }
                }

                try
                {
                    string announcement = BuildAnnouncement(selected);

                    // Enrich news item announcements with codec info
                    announcement = EnrichNewsItemAnnouncement(selected, announcement);

                    if (!string.IsNullOrEmpty(announcement))
                    {
                        if (announcement != _lastAnnouncedText ||
                            Time.time - _lastAnnouncementTime > 2.0f)
                        {
                            TolkSpeech.Speak(announcement, true);

                            _lastAnnouncedObject = selected;
                            _lastAnnouncedText = announcement;
                            _lastAnnouncementTime = Time.time;

                            // Start tracking for tooltip delay
                            _currentSelectedObject = selected;
                            _selectionTime = Time.time;
                            _tooltipAnnounced = false;

                            // Capture current visible texts to detect new tooltip text later
                            CaptureTextStateAtSelection(selected);

                            MelonLogger.Msg($"Announced: {announcement}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"Error announcing element: {ex.Message}");
                }
            }
            finally
            {
                _isAnnouncing = false;
            }
        }

        private static void AnnounceTooltip(GameObject go)
        {
            if (go == null) return;

            try
            {
                string tooltipText = null;
                // Dedup against what was ACTUALLY spoken for the element. Re-computing
                // elementText here is timing-dependent (tooltip TMPs may have activated
                // between the element announcement and now) and produced both flavours
                // of bug: re-computing always → double-announce; re-computing too late →
                // wrongly suppress the tooltip. Comparing against the literal spoken
                // string is unambiguous.
                string elementText = _lastAnnouncedText ?? "";

                // Strategy 1: Check ButtonBase.Tooltip
                var buttonBase = go.GetComponent<ButtonBase>();
                if (buttonBase != null)
                {
                    var tooltip = buttonBase.Tooltip;
                    if (tooltip != null)
                    {
                        MelonLogger.Msg($"[Tooltip Debug] Found ButtonTooltip component");

                        // Try TextMesh first
                        if (tooltip.TextMesh != null)
                        {
                            MelonLogger.Msg($"[Tooltip Debug] TextMesh exists, text='{tooltip.TextMesh.text}'");
                            if (!string.IsNullOrWhiteSpace(tooltip.TextMesh.text))
                            {
                                tooltipText = tooltip.TextMesh.text.Trim();
                            }
                        }

                        // Try LocalizeTextMesh if TextMesh didn't have text
                        if (string.IsNullOrEmpty(tooltipText) && tooltip.LocalizeTextMesh != null)
                        {
                            MelonLogger.Msg($"[Tooltip Debug] Checking LocalizeTextMesh");
                            // LocalizeTextMesh may have a different way to get text
                            var localizeGO = tooltip.LocalizeTextMesh.gameObject;
                            if (localizeGO != null)
                            {
                                var tmpText = localizeGO.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                                if (tmpText != null && !string.IsNullOrWhiteSpace(tmpText.text))
                                {
                                    MelonLogger.Msg($"[Tooltip Debug] LocalizeTextMesh text='{tmpText.text}'");
                                    tooltipText = tmpText.text.Trim();
                                }
                            }
                        }

                        // Also check if tooltip GameObject itself has text children
                        if (string.IsNullOrEmpty(tooltipText) && tooltip.gameObject != null)
                        {
                            string tooltipChildText = TextExtractor.GetElementText(tooltip.gameObject);
                            MelonLogger.Msg($"[Tooltip Debug] Tooltip child text='{tooltipChildText}'");
                            if (!string.IsNullOrWhiteSpace(tooltipChildText))
                            {
                                tooltipText = tooltipChildText;
                            }
                        }
                    }
                    else
                    {
                        MelonLogger.Msg($"[Tooltip Debug] ButtonBase found but Tooltip is null");
                    }
                }

                // Strategy 2: Search for tooltip-named children
                if (string.IsNullOrEmpty(tooltipText))
                {
                    tooltipText = FindTooltipInHierarchy(go.transform);
                    if (!string.IsNullOrEmpty(tooltipText))
                    {
                        MelonLogger.Msg($"[Tooltip Debug] Found via hierarchy search: '{tooltipText}'");
                    }
                }

                // Strategy 3: Find newly visible text since selection
                if (string.IsNullOrEmpty(tooltipText))
                {
                    tooltipText = FindNewlyVisibleText(go);
                    if (!string.IsNullOrEmpty(tooltipText))
                    {
                        MelonLogger.Msg($"[Tooltip Debug] Found newly visible text: '{tooltipText}'");
                    }
                }

                // Announce if found and different from the element label
                if (!string.IsNullOrEmpty(tooltipText))
                {
                    MelonLogger.Msg($"[Tooltip Debug] tooltipText='{tooltipText}', elementText='{elementText}'");
                    MelonLogger.Msg($"[Tooltip Debug] elementText.Contains check: {elementText.Contains(tooltipText)}");

                    if (!elementText.Contains(tooltipText))
                    {
                        MelonLogger.Msg($"[Tooltip Debug] Speaking tooltip now...");
                        TolkSpeech.Speak(tooltipText, false); // Don't interrupt â€” queue after current speech
                        MelonLogger.Msg($"Announced Tooltip: {tooltipText}");
                    }
                    else
                    {
                        MelonLogger.Msg($"[Tooltip Debug] Skipped - tooltip text is contained in element text");
                    }
                }
                else
                {
                    MelonLogger.Msg($"[Tooltip Debug] No tooltip text found for element");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Error announcing tooltip: {ex.Message}");
            }
        }

        /// <summary>
        /// Searches for tooltip-like children by name patterns
        /// </summary>
        private static string FindTooltipInHierarchy(Transform t)
        {
            string[] tooltipNames = { "Tooltip", "tooltip", "Help", "help", "Description", "description", "Info", "info", "Hint", "hint" };

            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);

                // Check if child name contains tooltip patterns
                foreach (var name in tooltipNames)
                {
                    if (child.name.Contains(name))
                    {
                        // Check for TextMeshProUGUI
                        var tmp = child.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                        if (tmp != null && child.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(tmp.text))
                        {
                            return CleanTooltipText(tmp.text);
                        }

                        // Check for Unity UI Text
                        var uiText = child.GetComponent<UnityEngine.UI.Text>();
                        if (uiText != null && child.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(uiText.text))
                        {
                            return CleanTooltipText(uiText.text);
                        }
                    }
                }

                // Recurse into children
                var found = FindTooltipInHierarchy(child);
                if (!string.IsNullOrEmpty(found)) return found;
            }

            return null;
        }

        /// <summary>
        /// Captures visible texts at selection time for comparison later
        /// </summary>
        private static void CaptureTextStateAtSelection(GameObject go)
        {
            _textsAtSelectionTime.Clear();
            if (go == null) return;

            CollectVisibleTexts(go.transform, _textsAtSelectionTime);
        }

        /// <summary>
        /// Finds text that became visible after selection (new tooltip text)
        /// </summary>
        private static string FindNewlyVisibleText(GameObject go)
        {
            if (go == null) return null;

            var currentTexts = new HashSet<string>();
            CollectVisibleTexts(go.transform, currentTexts);

            // Find texts that weren't visible at selection time
            foreach (var text in currentTexts)
            {
                if (!_textsAtSelectionTime.Contains(text))
                {
                    return text;
                }
            }

            return null;
        }

        /// <summary>
        /// Collects all visible text from a transform hierarchy
        /// </summary>
        private static void CollectVisibleTexts(Transform t, HashSet<string> texts)
        {
            var tmp = t.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
            if (tmp != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(tmp.text))
            {
                texts.Add(CleanTooltipText(tmp.text));
            }

            var uiText = t.GetComponent<UnityEngine.UI.Text>();
            if (uiText != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(uiText.text))
            {
                texts.Add(CleanTooltipText(uiText.text));
            }

            for (int i = 0; i < t.childCount; i++)
            {
                CollectVisibleTexts(t.GetChild(i), texts);
            }
        }

        /// <summary>
        /// Cleans tooltip text for comparison and announcement
        /// </summary>
        private static string CleanTooltipText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            // Remove rich text tags and collapse whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        /// <summary>
        /// Gets just the direct label text of a button, not including tooltip children.
        /// Used to compare against tooltip text to avoid announcing duplicates.
        /// </summary>
        private static string GetDirectLabelText(GameObject go)
        {
            if (go == null) return "";

            // For ButtonBase, try to get Label property first
            var buttonBase = go.GetComponent<ButtonBase>();
            if (buttonBase != null)
            {
                try
                {
                    var label = buttonBase.Label;
                    if (label != null && !string.IsNullOrWhiteSpace(label.text))
                    {
                        return CleanTooltipText(label.text);
                    }
                }
                catch { }
            }

            // Fallback: get first TextMeshPro that's a direct child (not in Tooltip)
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                // Skip tooltip children
                if (child.name.ToLower().Contains("tooltip")) continue;

                var tmp = child.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                {
                    return CleanTooltipText(tmp.text);
                }
            }

            return "";
        }

        /// <summary>
        /// Builds the announcement text for a GameObject
        /// </summary>
        private static string BuildAnnouncement(GameObject go)
        {
            StringBuilder sb = new StringBuilder();

            // Extract text content (with label override for context or fallback)
            string elementText = TextExtractor.GetElementText(go);
            string labelOverride = SectionManager.GetLabelOverride(go);
            if (!string.IsNullOrEmpty(labelOverride))
            {
                if (string.IsNullOrEmpty(elementText))
                {
                    // Icon-only button: use label override as the text
                    elementText = labelOverride;
                }
                else if (!elementText.Contains(labelOverride))
                {
                    // Button has text but label override adds context (e.g., "Turn Duration, 1 hour")
                    elementText = $"{labelOverride}, {elementText}";
                }
            }

            // Prepend page prefix if we just transitioned to a new page
            if (_pendingPagePrefix != null)
            {
                string pagePrefix = _pendingPagePrefix;
                _pendingPagePrefix = null;

                // Only add if different from element text (avoid "Lobby, Lobby")
                if (!string.Equals(pagePrefix, elementText, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(pagePrefix);
                    sb.Append(", ");
                }
            }

            // Prepend section name if we just entered a new section
            if (_pendingSectionName != null)
            {
                string sectionName = _pendingSectionName;
                _pendingSectionName = null;

                // Only add section name if it's different from element text (avoid "Settings, Settings")
                if (!string.Equals(sectionName, elementText, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(sectionName);
                    sb.Append(", ");
                }
            }

            if (!string.IsNullOrEmpty(elementText))
            {
                sb.Append(elementText);
            }

            // REMOVED Tooltip logic from here - moved to AnnounceTooltip

            // Add role based on component type
            string role = GetRole(go);
            if (!string.IsNullOrEmpty(role))
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(role);
            }

            // Add current value for interactive controls (slider %, button state, etc.)
            string valueInfo = GetControlValue(go);
            if (!string.IsNullOrEmpty(valueInfo))
            {
                // Skip if the value is already present in the announcement text
                string currentText = sb.ToString();
                if (!currentText.Contains(valueInfo))
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(valueInfo);
                }
            }

            // Add position context if in a list
            string positionInfo = GetPositionContext(go);
            if (!string.IsNullOrEmpty(positionInfo))
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(positionInfo);
            }

            // Add disabled state
            var selectable = go.GetComponent<SelectableBase>();
            if (selectable != null && !selectable.GetInteractable())
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("disabled");
            }

            // Guard against degenerate "button" / "slider" / etc announcements:
            // when an icon-only control has no element text AND no page/section
            // context, the StringBuilder ends up with just the role word. Suppress
            // those — they tell the user nothing. Callers should add a label
            // override (TryAddWithLabel) for any control they want surfaced.
            string final = sb.ToString().Trim();
            if (final == "button" || final == "slider" ||
                final == "dropdown" || final == "edit box")
            {
                return "";
            }

            return final;
        }

        /// <summary>
        /// If the selected element is a news pinboard item (DeckViewerItem), request a
        /// deferred enrichment that will fire after the codec populates (a few frames later).
        /// Returns the base announcement unchanged â€” the enriched version will be spoken later.
        /// </summary>
        private static string EnrichNewsItemAnnouncement(GameObject selected, string baseAnnouncement)
        {
            try
            {
                // Only enrich if we're on the News page
                if (!SAPMod._lastNewsOpen) return baseAnnouncement;

                // Check if the selected element is inside a DeckViewerItem
                var deckItem = selected.GetComponentInParent<Il2CppSpacewood.Unity.MonoBehaviours.Build.DeckViewerItem>();
                if (deckItem == null) return baseAnnouncement;

                // Programmatically open the codec for this item using sprite name lookup
                // (Il2Cpp Nullable<MinionEnum>.Value always returns default=Ant, so we use the sprite instead)
                try
                {
                    var news = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.News>();
                    var announcement = news?.Announcement;
                    var codec = announcement?.Codec;
                    if (codec != null)
                    {
                        // Get the sprite name from the item's Icon
                        string spriteName = "";
                        try
                        {
                            var images = deckItem.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                            if (images != null)
                            {
                                foreach (var img in images)
                                {
                                    if (img?.sprite != null && img.sprite.name != "Background" &&
                                        img.sprite.name != "UiBlank" && img.sprite.name != "Unity UI")
                                    {
                                        spriteName = img.sprite.name;
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }

                        // Remove _2x suffix
                        if (spriteName.EndsWith("_2x"))
                            spriteName = spriteName.Substring(0, spriteName.Length - 3);

                        if (!string.IsNullOrEmpty(spriteName))
                        {
                            bool opened = false;

                            // Try MinionEnum
                            if (!opened)
                            {
                                try
                                {
                                    if (System.Enum.TryParse<Il2CppSpacewood.Core.Enums.MinionEnum>(spriteName, true, out var minionEnum))
                                    {
                                        var templates = Il2CppSpacewood.Core.Enums.MinionConstants.Minions;
                                        if (templates != null && templates.ContainsKey(minionEnum))
                                        {
                                            codec.Open(templates[minionEnum], false, true);
                                            opened = true;
                                        }
                                    }
                                }
                                catch { }
                            }

                            // Try SpellEnum
                            if (!opened)
                            {
                                try
                                {
                                    if (System.Enum.TryParse<Il2CppSpacewood.Core.Enums.SpellEnum>(spriteName, true, out var spellEnum))
                                    {
                                        var spells = Il2CppSpacewood.Core.Enums.SpellConstants.Spells;
                                        if (spells != null && spells.ContainsKey(spellEnum))
                                        {
                                            codec.Open(spells[spellEnum], false, true);
                                            opened = true;
                                        }
                                    }
                                }
                                catch { }
                            }

                            // Try Perk
                            if (!opened)
                            {
                                try
                                {
                                    if (System.Enum.TryParse<Il2CppSpacewood.Core.Enums.Perk>(spriteName, true, out var perkEnum))
                                    {
                                        var perks = Il2CppSpacewood.Core.Enums.PerkConstants.Perks;
                                        if (perks != null && perks.ContainsKey(perkEnum))
                                        {
                                            codec.Open(perks[perkEnum], false, true);
                                            opened = true;
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (opened)
                                SAPMod._lastNewsCodecSpriteName = spriteName;
                            else
                                MelonLoader.MelonLogger.Warning($"[News Codec] Could not open codec for sprite '{spriteName}'");
                        }
                    }
                }
                catch { }

                // Request deferred enrichment â€” codec needs a frame to render after Open
                // Return null to suppress the initial "Name, button" announcement
                // The enriched version will speak in a few frames
                SAPMod.RequestNewsEnrichment(baseAnnouncement);
                return null;
            }
            catch { }
            return baseAnnouncement;
        }

        private static string GetRole(GameObject go)
        {
            if (go.GetComponent<ButtonBase>() != null) return "button";
            if (go.GetComponent<SliderBase>() != null) return "slider";
            if (go.GetComponent<DropdownBase>() != null) return "dropdown";
            if (go.GetComponent<InputFieldBase>() != null) return "edit box";
            return "";
        }

        /// <summary>
        /// Gets the current value of an interactive control (slider percentage, button state, dropdown selection).
        /// Public so SAPMod can use it for deferred value-change announcements.
        /// </summary>
        public static string GetControlValue(GameObject go)
        {
            if (go == null) return null;

            // Slider: show current value or percentage
            var slider = go.GetComponent<SliderBase>();
            if (slider != null)
            {
                try
                {
                    // Prefer LabelRight text (game-formatted value like "75%")
                    if (slider.LabelRight != null && !string.IsNullOrWhiteSpace(slider.LabelRight.text))
                        return slider.LabelRight.text.Trim();
                    // Fallback: compute percentage from Slider component
                    if (slider.Slider != null)
                    {
                        float pct = (slider.Slider.value - slider.Slider.minValue) /
                                    (slider.Slider.maxValue - slider.Slider.minValue) * 100f;
                        return $"{Mathf.RoundToInt(pct)}%";
                    }
                }
                catch { }
                return null;
            }

            // PackProduct: show owned/locked/selected status (check before ButtonBase since BuyButton is a ButtonBase)
            try
            {
                var packProduct = go.GetComponentInParent<Il2CppSpacewood.Unity.PackProduct>();
                if (packProduct != null)
                {
                    var parts = new List<string>();
                    if (packProduct.IsOwned) parts.Add("owned");
                    if (packProduct.IsNewbieLocked) parts.Add("locked");
                    // Check selected state via Activator child
                    try
                    {
                        var activator = go.transform.Find("Tween/Activator");
                        if (activator != null && activator.gameObject.activeInHierarchy)
                            parts.Add("selected");
                    }
                    catch { }
                    // Show price if not owned
                    if (!packProduct.IsOwned && packProduct.Price != null && !string.IsNullOrWhiteSpace(packProduct.Price.text))
                        parts.Add(packProduct.Price.text.Trim());
                    return parts.Count > 0 ? string.Join(", ", parts) : null;
                }
            }
            catch { }

            // ProductShopItemShared: show status only (locked/excluded/new) â€” name and price
            // are already in the element text from TextExtractor, so skip those to avoid duplication
            try
            {
                var shopItem = go.GetComponentInParent<Il2CppSpacewood.Unity.ProductShopItemShared>();
                if (shopItem != null)
                {
                    var parts = new List<string>();
                    try { if (shopItem.Lock != null && shopItem.Lock.gameObject.activeInHierarchy) parts.Add("locked"); } catch { }
                    try { if (shopItem.Excluded != null && shopItem.Excluded.gameObject.activeInHierarchy) parts.Add("excluded"); } catch { }
                    try { if (shopItem.New != null && shopItem.New.gameObject.activeInHierarchy) parts.Add("new"); } catch { }
                    if (parts.Count > 0) return string.Join(", ", parts);
                    return null; // Prevent falling through to ButtonBase value check
                }
            }
            catch { }

            // StatsSummaryItem: show "Label: Value" for stat entries
            try
            {
                var statItem = go.GetComponentInParent<Il2CppSpacewood.Unity.StatsSummaryItem>();
                if (statItem != null)
                {
                    string label = "";
                    string value = "";
                    try { label = statItem.Label?.text ?? ""; } catch { }
                    try { value = statItem.Value?.text ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(label)) return $"{label}: {value}".Trim();
                }
            }
            catch { }

            // PetCustomizerItem: show skin/hat name
            try
            {
                var petItem = go.GetComponentInParent<Il2CppSpacewood.Unity.PetCustomizerItem>();
                if (petItem != null)
                {
                    string label = "";
                    try { label = petItem.Label?.text ?? ""; } catch { }
                    if (string.IsNullOrEmpty(label))
                    {
                        try { label = petItem.SkinLabel?.text ?? ""; } catch { }
                    }
                    if (!string.IsNullOrEmpty(label)) return label.Trim();
                }
            }
            catch { }

            // VersusLobbyItem: show player name and rank
            try
            {
                var lobbyItem = go.GetComponentInParent<Il2CppSpacewood.Unity.VersusLobbyItem>();
                if (lobbyItem != null)
                {
                    var parts = new List<string>();
                    try { if (lobbyItem.PlayerName != null && !string.IsNullOrWhiteSpace(lobbyItem.PlayerName.text)) parts.Add(lobbyItem.PlayerName.text.Trim()); } catch { }
                    try { if (lobbyItem.RankText != null && !string.IsNullOrWhiteSpace(lobbyItem.RankText.text)) parts.Add($"rank {lobbyItem.RankText.text.Trim()}"); } catch { }
                    if (parts.Count > 0) return string.Join(", ", parts);
                }
            }
            catch { }

            // SpectateItem: show player name
            try
            {
                var spectateItem = go.GetComponentInParent<Il2CppSpacewood.Unity.SpectateItem>();
                if (spectateItem != null)
                {
                    string name = "";
                    try { name = spectateItem.NameText?.text ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(name)) return name.Trim();
                }
            }
            catch { }

            // Button with picker value or toggle state: show current selection
            var button = go.GetComponent<ButtonBase>();
            if (button != null)
            {
                try
                {
                    string val = button.GetValue();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        // Skip if the value is already contained in the element text
                        string elementText = TextExtractor.GetElementText(go);
                        if (!string.IsNullOrEmpty(elementText) && elementText.Contains(val))
                            return null;
                        return val;
                    }
                }
                catch { }
                return null;
            }

            // Dropdown: show selected option text
            var dropdown = go.GetComponent<DropdownBase>();
            if (dropdown != null)
            {
                try
                {
                    if (dropdown.Label != null && !string.IsNullOrWhiteSpace(dropdown.Label.text))
                        return dropdown.Label.text.Trim();
                }
                catch { }
                return null;
            }

            return null;
        }

        /// <summary>
        /// Gets position context (X of Y) for elements in lists
        /// </summary>
        private static string GetPositionContext(GameObject go)
        {
            try
            {
                var selectable = go.GetComponent<SelectableBase>();
                if (selectable == null) return "";

                Transform parent = go.transform.parent;
                if (parent == null) return "";

                List<SelectableBase> siblings = new List<SelectableBase>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    var childSelectable = child.GetComponent<SelectableBase>();

                    if (childSelectable != null &&
                        childSelectable.GetInteractable() &&
                        child.gameObject.activeInHierarchy)
                    {
                        siblings.Add(childSelectable);
                    }
                }

                if (siblings.Count > 1)
                {
                    int index = siblings.IndexOf(selectable);
                    if (index != -1)
                    {
                        return $"{index + 1} of {siblings.Count}";
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Error getting position context: {ex.Message}");
            }

            return "";
        }

        /// <summary>
        /// Announces arbitrary text (useful for game state changes)
        /// </summary>
        /// <summary>
        /// Cancels any pending tooltip announcement. Call when an overlay/dialog opens
        /// to prevent the tooltip from interrupting the dialog's announcement.
        /// </summary>
        public static void CancelPendingTooltip()
        {
            _tooltipAnnounced = true;
            _currentSelectedObject = null;
        }

        public static void Announce(string message, bool interrupt = true)
        {
            if (string.IsNullOrEmpty(message)) return;

            TolkSpeech.Speak(message, interrupt);
            MelonLogger.Msg($"Announced: {message}");
        }

        /// <summary>
        /// Reads all text content from the current context (active modal or current page).
        /// Called when user presses R key.
        /// </summary>
        public static void ReadCurrentContext()
        {
            // Priority: Active modal > Current page
            var modal = GetActiveModal();
            if (modal != null && modal.gameObject != null)
            {
                string allText = TextExtractor.GetAllTextFromHierarchy(modal.gameObject);
                if (!string.IsNullOrEmpty(allText))
                {
                    Announce(allText);
                }
                else
                {
                    Announce("dialog");
                }
                return;
            }

            // Fall back to current page
            try
            {
                var pageManager = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PageManager>();
                if (pageManager?.CurrentPage?.gameObject != null)
                {
                    string pageText = TextExtractor.GetAllTextFromHierarchy(pageManager.CurrentPage.gameObject);
                    if (!string.IsNullOrEmpty(pageText))
                    {
                        Announce(pageText);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadCurrentContext error: {ex.Message}");
            }
        }
    }
}
