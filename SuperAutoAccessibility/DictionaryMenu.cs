using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// In-game dictionary overlay with two modes:
    ///
    /// Browse mode (opened with J): categorized like the Help menu. Tab / PageDown
    /// switches categories (Pets, Food, Ailments, Triggers, Mechanics, etc.); Up/Down
    /// browses entries inside the current category.
    ///
    /// Search mode (opened with Ctrl+F): the user types a query; pressing Enter
    /// commits the search and announces the first match. Up/Down then walks through
    /// matches across all categories. Backspace edits the query. Ctrl+F again toggles
    /// back to browse mode.
    ///
    /// While the overlay is active, ALL other keybinds are suppressed by an early
    /// return in SAPMod.OnUpdate — typed letters never trigger shop nav or status
    /// queries, and Escape always closes the overlay.
    ///
    /// Source data: combines the four hand-curated lists in SAPMod with an optional
    /// wiki-scraped JSON file at Mods/SAPAccess.dictionary.json next to the mod DLL.
    /// The file is loaded once at first open; missing file = curated entries only.
    /// </summary>
    public static class DictionaryMenu
    {
        private struct Entry
        {
            public string Category;
            public string Name;
            public string Description;
        }

        private enum Mode { Browse, Search }

        private static bool _active;
        private static Mode _mode;
        private static bool _skipFrame;
        // EventSystem state restored on Close so the game's UI input resumes correctly.
        // Default to true so a fresh build doesn't restore-as-disabled if something odd
        // happens during Open (e.g. EventSystem not yet present).
        private static bool _eventSystemWasEnabled = true;
        private static bool _eventSystemSendNav = true;

        // Keys recorded as held at the moment the dictionary closes. We swallow all
        // input from outer handlers (SAPMod.OnUpdate + game EventSystem) until every
        // one of these is released. Prevents the Escape press that closed the dictionary
        // from immediately closing the next menu underneath.
        private static readonly HashSet<KeyCode> _swallowUntilRelease = new HashSet<KeyCode>();

        // Browse-mode state
        private static readonly List<string> _categories = new List<string>();
        private static int _categoryIndex;
        private static int _itemIndex;

        // Search-mode state
        private static string _query = "";
        private static readonly List<int> _searchResults = new List<int>();
        private static int _searchIndex;
        private static bool _searchCommitted; // false while typing, true after Enter

        // Data
        private static readonly List<Entry> _all = new List<Entry>();
        // categoryName -> list of indices into _all, sorted by Name
        private static readonly Dictionary<string, List<int>> _byCategory =
            new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static bool IsActive => _active;

        // ------------------------------------------------------------ public entry points

        public static void OpenBrowse()
        {
            EnsureLoaded();
            if (_categories.Count == 0) { _active = false; return; }
            _mode = Mode.Browse;
            _categoryIndex = Math.Max(0, Math.Min(_categoryIndex, _categories.Count - 1));
            _itemIndex = 0;
            SuppressGameInput();
            _active = true;
            _skipFrame = true;
            // Match the Help menu's "{category}. {first item}." format on open so the
            // user immediately knows which category they're starting in.
            AnnounceCurrentBrowse(includeCategory: true);
        }

        // Internal-only — switches mode without re-suppressing or re-announcing. Used by
        // Ctrl+F inside the active overlay.
        private static void SwitchToBrowseInternal()
        {
            if (_categories.Count == 0) return;
            _mode = Mode.Browse;
            _categoryIndex = Math.Max(0, Math.Min(_categoryIndex, _categories.Count - 1));
            _itemIndex = 0;
            AnnounceCurrentBrowse();
        }

        private static void SwitchToSearchInternal()
        {
            _mode = Mode.Search;
            _query = "";
            _searchCommitted = false;
            _searchResults.Clear();
            _searchIndex = 0;
            AccessibilityManager.Announce("Search.");
        }

        public static void Close()
        {
            if (!_active) return;
            _active = false;
            // Record any keys held right now (especially the Escape that just closed us)
            // so SAPMod.OnUpdate can keep swallowing them until released.
            _swallowUntilRelease.Clear();
            foreach (var k in _watchedKeys)
            {
                try { if (Input.GetKey(k)) _swallowUntilRelease.Add(k); }
                catch { }
            }
            RestoreGameInput();
            AccessibilityManager.Announce("Dictionary closed");
        }

        /// <summary>
        /// Keys we track for the close-time swallow. Covers everything realistically
        /// usable as a "close" / "submit" / nav key. Letter/digit keys aren't included
        /// because they don't trigger global behaviors on press-release.
        /// </summary>
        private static readonly KeyCode[] _watchedKeys = new[]
        {
            KeyCode.Escape, KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Space,
            KeyCode.Tab, KeyCode.Backspace,
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
            KeyCode.LeftControl, KeyCode.RightControl, KeyCode.F,
        };

        /// <summary>
        /// SAPMod.OnUpdate calls this BEFORE its normal input handling. Returns true
        /// if any key that was held at close-time is still being held — meaning the
        /// caller should swallow input this frame (return from OnUpdate). Once every
        /// recorded key is released, the swallow set clears and normal input resumes.
        /// </summary>
        public static bool ShouldSwallowInput()
        {
            if (_swallowUntilRelease.Count == 0) return false;
            // Drop keys that have been released. Keep the still-held ones.
            var stillHeld = new List<KeyCode>();
            foreach (var k in _swallowUntilRelease)
            {
                try { if (Input.GetKey(k)) stillHeld.Add(k); } catch { }
            }
            _swallowUntilRelease.Clear();
            foreach (var k in stillHeld) _swallowUntilRelease.Add(k);
            return _swallowUntilRelease.Count > 0;
        }

        /// <summary>
        /// Find the live EventSystem. EventSystem.current can be stale across scene
        /// transitions (it's a static cache); FindObjectOfType reflects the current
        /// scene's actual instance.
        /// </summary>
        private static UnityEngine.EventSystems.EventSystem GetLiveEventSystem()
        {
            try
            {
                return UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
            }
            catch { return null; }
        }

        /// <summary>
        /// Block the game from receiving any navigation/click input while the overlay
        /// is open. Disables the EventSystem entirely so arrow keys, Tab, Enter, Space,
        /// mouse — nothing fires UI events on the underlying game UI. Also suppresses
        /// the mod's own shop nav handler.
        /// </summary>
        private static void SuppressGameInput()
        {
            try { Gameplay.ShopNavigationManager.IsInputSuppressed = true; } catch { }
            try
            {
                var es = GetLiveEventSystem();
                if (es != null)
                {
                    _eventSystemWasEnabled = es.enabled;
                    _eventSystemSendNav = es.sendNavigationEvents;
                    es.sendNavigationEvents = false;
                    es.enabled = false;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] SuppressGameInput error: {ex.Message}");
            }
        }

        private static void RestoreGameInput()
        {
            try { Gameplay.ShopNavigationManager.IsInputSuppressed = false; } catch { }
            try
            {
                var es = GetLiveEventSystem();
                if (es != null)
                {
                    es.sendNavigationEvents = _eventSystemSendNav;
                    es.enabled = _eventSystemWasEnabled;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] RestoreGameInput error: {ex.Message}");
            }
        }

        // ------------------------------------------------------------ input dispatch

        public static void HandleInput()
        {
            if (_skipFrame) { _skipFrame = false; return; }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }

            // Ctrl+F toggles between Browse and Search mode WITHIN the open overlay.
            // (Ctrl+F never opens the overlay from scratch — that's intentional, so
            // typing Ctrl+F in the regular game doesn't pop up the dictionary.)
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.F))
            {
                if (_mode == Mode.Search) SwitchToBrowseInternal();
                else SwitchToSearchInternal();
                return;
            }

            if (_mode == Mode.Browse) HandleBrowseInput();
            else HandleSearchInput();
        }

        // ------------------------------------------------------------ browse mode

        private static void HandleBrowseInput()
        {
            // Tab / PageDown: next category. Shift+Tab / PageUp: previous category.
            bool tabNext = (Input.GetKeyDown(KeyCode.Tab) &&
                            !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                           || Input.GetKeyDown(KeyCode.PageDown);
            bool tabPrev = (Input.GetKeyDown(KeyCode.Tab) &&
                            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                           || Input.GetKeyDown(KeyCode.PageUp);

            if (tabNext)
            {
                if (_categories.Count == 0) return;
                _categoryIndex = (_categoryIndex + 1) % _categories.Count;
                _itemIndex = 0;
                AnnounceCurrentBrowse(includeCategory: true);
                return;
            }
            if (tabPrev)
            {
                if (_categories.Count == 0) return;
                _categoryIndex = (_categoryIndex - 1 + _categories.Count) % _categories.Count;
                _itemIndex = 0;
                AnnounceCurrentBrowse(includeCategory: true);
                return;
            }

            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                var list = CurrentCategoryItems();
                if (list == null || list.Count == 0) return;
                _itemIndex = (_itemIndex + 1) % list.Count;
                AnnounceCurrentBrowse();
                return;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                var list = CurrentCategoryItems();
                if (list == null || list.Count == 0) return;
                _itemIndex = (_itemIndex - 1 + list.Count) % list.Count;
                AnnounceCurrentBrowse();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Home))
            {
                var list = CurrentCategoryItems();
                if (list == null || list.Count == 0) return;
                _itemIndex = 0;
                AnnounceCurrentBrowse();
                return;
            }
            if (Input.GetKeyDown(KeyCode.End))
            {
                var list = CurrentCategoryItems();
                if (list == null || list.Count == 0) return;
                _itemIndex = list.Count - 1;
                AnnounceCurrentBrowse();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
                || Input.GetKeyDown(KeyCode.Space))
            {
                AnnounceCurrentBrowse();
                return;
            }

            // All other keys are deliberately swallowed (we returned in OnUpdate).
        }

        private static List<int> CurrentCategoryItems()
        {
            if (_categoryIndex < 0 || _categoryIndex >= _categories.Count) return null;
            string cat = _categories[_categoryIndex];
            return _byCategory.TryGetValue(cat, out var list) ? list : null;
        }

        private static void AnnounceCurrentBrowse(bool includeCategory = false)
        {
            var list = CurrentCategoryItems();
            if (list == null || list.Count == 0)
            {
                AccessibilityManager.Announce(
                    (_categoryIndex < _categories.Count ? _categories[_categoryIndex] : "Empty")
                    + ". No entries.");
                return;
            }
            if (_itemIndex < 0 || _itemIndex >= list.Count) return;
            var e = _all[list[_itemIndex]];
            if (includeCategory)
            {
                // Match the Help menu's category-tab announce: "{Category}. {firstItem}.
                // category {n} of {totalCategories}." — so the user knows where they
                // are in the category list, not just inside the current category.
                string catPos = $"category {_categoryIndex + 1} of {_categories.Count}";
                AccessibilityManager.Announce(
                    $"{_categories[_categoryIndex]}. {e.Name}. {e.Description}. {catPos}.");
            }
            else
            {
                // Within-category browse: report item position.
                string itemPos = $"{_itemIndex + 1} of {list.Count}";
                AccessibilityManager.Announce(
                    $"{e.Name}. {e.Description}. {itemPos}.");
            }
        }

        // ------------------------------------------------------------ search mode

        private static void HandleSearchInput()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                CommitSearch();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                if (_query.Length > 0)
                {
                    _query = _query.Substring(0, _query.Length - 1);
                    _searchCommitted = false;
                    AccessibilityManager.Announce(string.IsNullOrEmpty(_query) ? "Search cleared" : _query);
                }
                return;
            }

            // After a search is committed, arrow keys navigate results.
            if (_searchCommitted)
            {
                if (Input.GetKeyDown(KeyCode.DownArrow))
                {
                    if (_searchResults.Count == 0) return;
                    _searchIndex = (_searchIndex + 1) % _searchResults.Count;
                    AnnounceCurrentSearchResult();
                    return;
                }
                if (Input.GetKeyDown(KeyCode.UpArrow))
                {
                    if (_searchResults.Count == 0) return;
                    _searchIndex = (_searchIndex - 1 + _searchResults.Count) % _searchResults.Count;
                    AnnounceCurrentSearchResult();
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Home))
                {
                    if (_searchResults.Count == 0) return;
                    _searchIndex = 0; AnnounceCurrentSearchResult(); return;
                }
                if (Input.GetKeyDown(KeyCode.End))
                {
                    if (_searchResults.Count == 0) return;
                    _searchIndex = _searchResults.Count - 1; AnnounceCurrentSearchResult(); return;
                }
            }

            // Append typed characters to the query but DON'T filter yet.
            // Suppress when Ctrl is held — that means the user is composing a Ctrl+X
            // shortcut, not typing.
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return;

            string typed = Input.inputString;
            if (!string.IsNullOrEmpty(typed))
            {
                bool changed = false;
                for (int i = 0; i < typed.Length; i++)
                {
                    char ch = typed[i];
                    if (ch < 32 || ch == 127) continue; // control / DEL
                    _query += ch;
                    changed = true;
                }
                if (changed)
                {
                    _searchCommitted = false; // editing — needs new commit
                    // Speak just the appended character so the user has feedback they're typing,
                    // but nothing else. (Some screen readers also echo via their own typing-echo.)
                    AccessibilityManager.Announce(typed);
                }
            }
        }

        private static void CommitSearch()
        {
            _searchResults.Clear();
            _searchIndex = 0;
            if (string.IsNullOrEmpty(_query))
            {
                _searchCommitted = false;
                AccessibilityManager.Announce("Empty search");
                return;
            }
            string q = _query.ToLowerInvariant();
            // Rank: prefix on Name first, then infix on Name, then infix on Description.
            var prefix = new List<int>();
            var infix = new List<int>();
            var descMatch = new List<int>();
            for (int i = 0; i < _all.Count; i++)
            {
                var e = _all[i];
                string n = e.Name?.ToLowerInvariant() ?? "";
                if (n.StartsWith(q)) prefix.Add(i);
                else if (n.Contains(q)) infix.Add(i);
                else if ((e.Description ?? "").ToLowerInvariant().Contains(q)) descMatch.Add(i);
            }
            _searchResults.AddRange(prefix);
            _searchResults.AddRange(infix);
            _searchResults.AddRange(descMatch);
            _searchCommitted = true;

            if (_searchResults.Count == 0)
            {
                AccessibilityManager.Announce($"No results for {_query}.");
                return;
            }
            AnnounceCurrentSearchResult();
        }

        private static void AnnounceCurrentSearchResult()
        {
            if (_searchResults.Count == 0) return;
            if (_searchIndex < 0 || _searchIndex >= _searchResults.Count) return;
            var e = _all[_searchResults[_searchIndex]];
            string cat = string.IsNullOrEmpty(e.Category) ? "" : $"{e.Category}: ";
            AccessibilityManager.Announce(
                $"{cat}{e.Name}. {e.Description}. Result {_searchIndex + 1} of {_searchResults.Count}.");
        }

        // ------------------------------------------------------------ data loading

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _all.Clear();
            _byCategory.Clear();
            _categories.Clear();

            // Wiki-scraped entries are loaded FIRST so they own each (category, name) slot.
            // Curated entries fill in only the gaps.
            // Order:
            //   1. Disk override at <SAP>/Mods/SAPAccess.dictionary.json — for testing
            //      and community-edited dictionaries without rebuilding the mod.
            //   2. Otherwise, the JSON embedded in the DLL at build time.
            try
            {
                bool loadedFromDisk = false;
                string modLoc = typeof(DictionaryMenu).Assembly.Location;
                if (!string.IsNullOrEmpty(modLoc))
                {
                    string path = Path.Combine(
                        Path.GetDirectoryName(modLoc), "SAPAccess.dictionary.json");
                    if (File.Exists(path))
                    {
                        int added = LoadJsonFile(path);
                        MelonLogger.Msg($"[Dictionary] Loaded {added} entries from override file {path}");
                        loadedFromDisk = true;
                    }
                }
                if (!loadedFromDisk)
                {
                    int added = LoadEmbeddedResource();
                    MelonLogger.Msg($"[Dictionary] Loaded {added} entries from embedded resource");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] LoadEntries error: {ex.Message}");
            }

            AddCurated();
            FilterPlaceholders();
            BuildCategoryIndex();
            _loaded = true;
            MelonLogger.Msg(
                $"[Dictionary] Total entries: {_all.Count}, categories: {_categories.Count}");
        }

        /// <summary>
        /// Drop entries whose name is a placeholder like "???" or is empty after trim.
        /// These appear on the wiki as unrevealed/teaser pets and don't help the user.
        /// </summary>
        private static void FilterPlaceholders()
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                string n = (_all[i].Name ?? "").Trim();
                if (n.Length == 0 || n == "???" || n.Replace("?", "").Trim().Length == 0)
                    _all.RemoveAt(i);
            }
        }

        /// <summary>
        /// Pull the four already-verified _keywordDictionary* lists out of SAPMod
        /// via reflection so we don't duplicate the data.
        /// </summary>
        private static void AddCurated()
        {
            var t = typeof(SAPMod);
            var bf = BindingFlags.NonPublic | BindingFlags.Static;
            AddCuratedList(t.GetField("_keywordDictionaryTriggers", bf), "Trigger");
            AddCuratedList(t.GetField("_keywordDictionaryFoodPerks", bf), "Food");
            AddCuratedList(t.GetField("_keywordDictionaryStatuses", bf), "Ailment");
            AddCuratedList(t.GetField("_keywordDictionaryMechanics", bf), "Mechanic");
        }

        private static void AddCuratedList(FieldInfo fld, string category)
        {
            if (fld == null) return;
            try
            {
                if (!(fld.GetValue(null) is List<string> list)) return;
                // Build a set of (category, name) already present from the wiki load so
                // curated entries only fill gaps. The wiki is authoritative.
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in _all)
                {
                    if (string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
                        existing.Add(e.Name?.Trim() ?? "");
                }
                foreach (var line in list)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int sep = line.IndexOf(':');
                    string name;
                    string desc;
                    if (sep < 0) { name = line.Trim(); desc = ""; }
                    else { name = line.Substring(0, sep).Trim(); desc = line.Substring(sep + 1).Trim(); }
                    if (existing.Contains(name)) continue; // wiki wins
                    _all.Add(new Entry { Category = category, Name = name, Description = desc });
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] AddCuratedList error: {ex.Message}");
            }
        }

        /// <summary>
        /// Build the category index from _all. Categories are emitted in a fixed order
        /// (Pet, Food, Toy, Relic, Ailment, Trigger, Mechanic, then anything else
        /// alphabetically) so the user gets a predictable Tab walk.
        /// </summary>
        private static void BuildCategoryIndex()
        {
            _byCategory.Clear();
            _categories.Clear();
            for (int i = 0; i < _all.Count; i++)
            {
                string cat = string.IsNullOrEmpty(_all[i].Category) ? "Other" : _all[i].Category;
                if (!_byCategory.TryGetValue(cat, out var list))
                {
                    list = new List<int>();
                    _byCategory[cat] = list;
                }
                list.Add(i);
            }
            foreach (var kv in _byCategory)
                kv.Value.Sort((a, b) => string.CompareOrdinal(_all[a].Name ?? "", _all[b].Name ?? ""));

            var preferred = new[] { "Pet", "Food", "Toy", "Relic", "Ailment", "Trigger", "Mechanic" };
            foreach (var p in preferred)
                if (_byCategory.ContainsKey(p)) _categories.Add(p);
            // Append anything else not in the preferred list.
            var rest = new List<string>();
            foreach (var k in _byCategory.Keys)
            {
                bool inPreferred = false;
                foreach (var p in preferred) if (string.Equals(p, k, StringComparison.OrdinalIgnoreCase)) { inPreferred = true; break; }
                if (!inPreferred) rest.Add(k);
            }
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            _categories.AddRange(rest);
        }

        // ------------------------------------------------------------ JSON parsing

        /// <summary>
        /// Read the wiki dictionary that's embedded into this DLL at build time.
        /// Returns 0 if the resource isn't found (e.g. mod was built before the
        /// dictionary was added).
        /// </summary>
        private static int LoadEmbeddedResource()
        {
            try
            {
                var asm = typeof(DictionaryMenu).Assembly;
                using var stream = asm.GetManifestResourceStream(
                    "SuperAutoAccessibility.Generated.WikiDictionary.json");
                if (stream == null)
                {
                    MelonLogger.Warning("[Dictionary] Embedded WikiDictionary.json not found");
                    return 0;
                }
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                return LoadJsonString(json);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] LoadEmbeddedResource error: {ex.Message}");
                return 0;
            }
        }

        private static int LoadJsonFile(string path)
        {
            try { return LoadJsonString(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] LoadJsonFile error: {ex.Message}");
                return 0;
            }
        }

        private static int LoadJsonString(string json)
        {
            int added = 0;
            try
            {
                int i = 0;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != '[') return 0;
                i++;
                while (i < json.Length)
                {
                    while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                    if (i >= json.Length || json[i] == ']') break;
                    if (json[i] != '{')
                    {
                        while (i < json.Length && json[i] != '{' && json[i] != ']') i++;
                        if (i >= json.Length || json[i] == ']') break;
                    }
                    string cat = null, name = null, desc = null;
                    int objEnd = FindMatchingBrace(json, i);
                    if (objEnd < 0) break;
                    ParseObjectFields(json, i + 1, objEnd, ref cat, ref name, ref desc);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _all.Add(new Entry
                        {
                            Category = cat ?? "",
                            Name = name.Trim(),
                            Description = (desc ?? "").Trim(),
                        });
                        added++;
                    }
                    i = objEnd + 1;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Dictionary] LoadJsonFile error: {ex.Message}");
            }
            return added;
        }

        private static int FindMatchingBrace(string s, int open)
        {
            int depth = 0;
            bool inStr = false;
            bool escape = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inStr) { escape = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static void ParseObjectFields(string s, int start, int end,
            ref string cat, ref string name, ref string desc)
        {
            int i = start;
            while (i < end)
            {
                while (i < end && s[i] != '"') i++;
                if (i >= end) return;
                int keyStart = i + 1;
                int keyEnd = FindStringEnd(s, keyStart);
                if (keyEnd < 0 || keyEnd >= end) return;
                string key = s.Substring(keyStart, keyEnd - keyStart);
                i = keyEnd + 1;
                while (i < end && (char.IsWhiteSpace(s[i]) || s[i] == ':')) i++;
                if (i >= end || s[i] != '"') return;
                int valStart = i + 1;
                int valEnd = FindStringEnd(s, valStart);
                if (valEnd < 0 || valEnd >= end) return;
                string val = UnescapeJson(s.Substring(valStart, valEnd - valStart));
                if (key.Equals("category", StringComparison.OrdinalIgnoreCase)) cat = val;
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = val;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) desc = val;
                i = valEnd + 1;
            }
        }

        private static int FindStringEnd(string s, int start)
        {
            bool escape = false;
            for (int i = start; i < s.Length; i++)
            {
                if (escape) { escape = false; continue; }
                if (s[i] == '\\') { escape = true; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        private static string UnescapeJson(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(n); break;
                    }
                    i++;
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
