using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using Il2CppSpacewood.Unity;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// The Achievements page: one entry per pet, with a sticker once the pet has reached level 3,
    /// a red ribbon for winning Arena with it at level 3 and a blue one for winning on hard at
    /// level 2. Entries carry a plain Unity Button, not the game's ButtonBase, so they fit no
    /// NavigationSection: this keeps the list itself. Tab moves between the filter row and the
    /// list; Up and Down, Home and End, Page Up and Page Down (ten at a time) move through the
    /// pets. The tree gets the list as a group from SapUiSource.
    /// </summary>
    internal static class AchievementsNavigator
    {
        private static int _index;
        private static float _cacheAt = -1f;
        private static Achievements _cachedPage;

        /// <summary>The Achievements page when it is the open page, or null.</summary>
        public static Achievements Active()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _cacheAt < 0.2f) return _cachedPage;
            _cacheAt = now;
            _cachedPage = null;
            try
            {
                var page = SceneObjects.Find<PageManager>()?.CurrentPage;
                if (page == null || page.gameObject.name != "Achievements") return null;
                var a = page.GetComponentInChildren<Achievements>();
                if (a != null && a.gameObject.activeInHierarchy) _cachedPage = a;
            }
            catch { }
            return _cachedPage;
        }

        /// <summary>The visible entries' buttons, in the page's order (filters hide some).</summary>
        public static List<GameObject> Items(Achievements a)
        {
            var list = new List<GameObject>();
            if (a?.Entries == null) return list;
            try
            {
                for (int i = 0; i < a.Entries.Count; i++)
                {
                    var e = a.Entries[i];
                    if (e != null && e.Button != null && e.gameObject.activeInHierarchy)
                        list.Add(e.Button.gameObject);
                }
            }
            catch { }
            return list;
        }

        private static AchievementsEntry EntryOf(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var e = go.GetComponentInParent<AchievementsEntry>();
                return e != null && e.Button != null && e.Button.gameObject == go ? e : null;
            }
            catch { return null; }
        }

        public static bool IsEntry(GameObject go) => Active() != null && EntryOf(go) != null;

        /// <summary>"Crocodile, sticker, red ribbon" or "Aardvark, no sticker yet".</summary>
        public static string Label(GameObject go)
        {
            var e = EntryOf(go);
            if (e == null) return null;
            try
            {
                // A locked pet shows "???" over a silhouette; its title still holds the name,
                // which the silhouette gives away to sighted players.
                string name = e.Title != null ? TextExtractor.CleanTextPublic(e.Title.text) : null;
                if (string.IsNullOrEmpty(name) || name.Trim('?').Length == 0)
                    name = Gameplay.PetStatsReader.SplitCamelCase(e.Minion.ToString());
                var parts = new List<string> { name };
                bool sticker = e.HeaderRevealed != null && e.HeaderRevealed.gameObject.activeSelf;
                parts.Add(sticker ? "sticker" : "no sticker yet");
                if (Earned(e.Star)) parts.Add("red ribbon");
                if (Earned(e.SuperStar)) parts.Add("blue ribbon");
                return string.Join(", ", parts);
            }
            catch { return null; }
        }

        // An unearned ribbon is drawn black.
        internal static bool Earned(UnityEngine.UI.Image ribbon)
        {
            try
            {
                if (ribbon == null || !ribbon.gameObject.activeInHierarchy) return false;
                var c = ribbon.color;
                return c.r > 0.5f || c.g > 0.5f || c.b > 0.5f;
            }
            catch { return false; }
        }

        /// <summary>"3 of 536", counting the entries the filters leave.</summary>
        public static string Position(GameObject go)
        {
            var items = Items(Active());
            int i = items.IndexOf(go);
            return i >= 0 ? $"{i + 1} of {items.Count}" : null;
        }

        /// <summary>The counters with their meaning, and the keys.</summary>
        public static string Description(Achievements a)
        {
            var parts = new List<string>();
            try
            {
                Counter(parts, a.CounterMaxLevel, "Stickers");
                Counter(parts, a.CounterVictory, "Red ribbons");
                Counter(parts, a.CounterVictoryHard, "Blue ribbons");
                foreach (var guide in new[] { a.Guide1, a.Guide2, a.Guide3 })
                {
                    string g = guide != null ? TextExtractor.CleanTextPublic(guide.text) : null;
                    if (!string.IsNullOrEmpty(g)) parts.Add(g);
                }
            }
            catch { }
            parts.Add("Tab moves between the filters and the list of pets.");
            return TextExtractor.JoinSentences(parts);
        }

        private static void Counter(List<string> parts, AchievementsCounter counter, string what)
        {
            if (counter == null || !counter.gameObject.activeInHierarchy || counter.Label == null) return;
            string value = TextExtractor.CleanTextPublic(counter.Label.text);
            if (string.IsNullOrEmpty(value)) return;
            parts.Add($"{what}: {value.Replace(" / ", " of ")}");
        }

        /// <summary>Keys on the Achievements page. True when the key was used.</summary>
        public static bool HandleInput()
        {
            var a = Active();
            if (a == null || EventSystem.current == null) return false;
            var selected = EventSystem.current.currentSelectedGameObject;
            bool inList = EntryOf(selected) != null;

            if (ModInput.GetKeyDown(KeyCode.Tab))
            {
                if (inList)
                {
                    // Back to the filter row (the page's only section).
                    SectionManager.FocusFirstSection();
                }
                else
                {
                    var items = Items(a);
                    if (items.Count == 0)
                    {
                        AccessibilityManager.Announce("No pets match the filter");
                        return true;
                    }
                    Focus(items, Math.Min(_index, items.Count - 1), "Pets");
                }
                return true;
            }
            if (!inList) return false;

            var list = Items(a);
            int current = list.IndexOf(selected);
            if (current < 0) return false;
            int next = current;
            if (ModInput.GetKeyDown(KeyCode.DownArrow)) next = current + 1;
            else if (ModInput.GetKeyDown(KeyCode.UpArrow)) next = current - 1;
            else if (ModInput.GetKeyDown(KeyCode.PageDown)) next = current + 10;
            else if (ModInput.GetKeyDown(KeyCode.PageUp)) next = current - 10;
            else if (ModInput.GetKeyDown(KeyCode.Home)) next = 0;
            else if (ModInput.GetKeyDown(KeyCode.End)) next = list.Count - 1;
            else return false;
            next = Math.Max(0, Math.Min(next, list.Count - 1));
            if (next == current)
            {
                // Edge: say it again rather than wrap.
                if (!Tree.SapA11y.OwnsSpeech) AccessibilityManager.Announce($"{Label(selected)}, {Position(selected)}");
                return true;
            }
            Focus(list, next, null);
            return true;
        }

        private static void Focus(List<GameObject> items, int index, string prefix)
        {
            _index = index;
            var go = items[index];
            if (!string.IsNullOrEmpty(prefix)) AccessibilityManager.SetCurrentSectionName(prefix);
            SectionManager.MarkModDriven();
            EventSystem.current.SetSelectedGameObject(go);
        }
    }
}
