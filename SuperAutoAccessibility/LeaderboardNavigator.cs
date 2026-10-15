using System;
using System.Collections.Generic;
using Il2CppSpacewood.Unity;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// The leaderboard the Pack Shop's rank button opens (Daily has the same kind). It is a modal
    /// whose players are plain text rows, with one button that picks the board (Duel, Party,
    /// Weekly, Custom). Nothing handled it: the Pack Shop's sections stayed in charge, so the
    /// arrow keys moved through the packs hidden behind it, and the opening line read every row
    /// as "1.. Tokieda_Yukari. 1820". Up and Down now read one player at a time, R (the mod's
    /// read-the-screen key, see AccessibilityManager.ReadCurrentContext) reads the summary, and
    /// Escape returns to the rank button.
    /// </summary>
    internal static class LeaderboardNavigator
    {
        internal const string Hint =
            "Up and Down read the players, Home and End go to either end, R reads your rank. Escape closes.";

        private static VersusLeaderboard _open;
        private static int _row = -1;
        private static GameObject _returnTo;
        private static float _cacheAt = -1f;
        private static VersusLeaderboard _cached;

        /// <summary>The open leaderboard, or null.</summary>
        public static VersusLeaderboard Active()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _cacheAt < 0.1f) return _cached;
            _cacheAt = now;
            _cached = null;
            try
            {
                foreach (var lb in UnityEngine.Object.FindObjectsOfType<VersusLeaderboard>())
                {
                    if (lb?.Modal != null && lb.Column != null && lb.Modal.gameObject.activeInHierarchy)
                    {
                        _cached = lb;
                        break;
                    }
                }
            }
            catch { }
            return _cached;
        }

        /// <summary>Whether this object is part of an open leaderboard.</summary>
        public static bool Contains(GameObject go)
        {
            var lb = Active();
            try { return lb != null && go != null && go.transform.IsChildOf(lb.transform); }
            catch { return false; }
        }

        /// <summary>
        /// "Party leaderboard. 1474 Points. You are almost top 50%. Season reset: 16 days."
        /// </summary>
        public static string Summary(VersusLeaderboard lb)
        {
            if (lb?.Column == null) return "Leaderboard";
            var col = lb.Column;
            var parts = new List<string>();
            string board = Text(col.RankButton != null && col.RankButton.gameObject.activeInHierarchy ? col.RankButton.Label : null);
            parts.Add(board != null ? board + " leaderboard" : "Leaderboard");
            parts.Add(Text(col.Rank));
            parts.Add(Text(col.YourPercentile));
            parts.Add(Countdown(col.SeasonCountdownContainer, col.SeasonCountdown));
            parts.Add(Countdown(col.ChallengeCountdownContainer, col.ChallengeCountdown));
            parts.Add(Text(col.InactivePlayersNote));
            return TextExtractor.JoinSentences(parts);
        }

        /// <summary>"1. Tokieda_Yukari, 1820", one per player shown, in order.</summary>
        public static List<string> Rows(VersusLeaderboard lb)
        {
            var rows = new List<string>();
            try
            {
                var container = lb?.Column?.PlayerContainer;
                if (container == null) return rows;
                for (int i = 0; i < container.childCount; i++)
                {
                    var player = container.GetChild(i).GetComponent<VersusLeaderboardPlayer>();
                    if (player == null || !player.gameObject.activeInHierarchy) continue;
                    string number = Text(player.NumberLabel)?.TrimEnd('.');
                    string name = Text(player.NameLabel);
                    string rank = Text(player.RankLabel);
                    if (name == null) continue;
                    string row = number != null ? $"{number}. {name}" : name;
                    rows.Add(rank != null ? $"{row}, {rank}" : row);
                }
            }
            catch { }
            return rows;
        }

        /// <summary>
        /// Called every frame: notices the leaderboard opening and closing. On close, focus goes
        /// back to the control that opened it; the button left selected inside the hidden modal
        /// would leave the arrow keys dead.
        /// </summary>
        public static void Poll()
        {
            var lb = Active();
            if (lb != null && _open != lb)
            {
                _open = lb;
                _row = -1;
                if (_returnTo == null)
                {
                    var selected = EventSystem.current?.currentSelectedGameObject;
                    if (selected != null && !selected.transform.IsChildOf(lb.transform)) _returnTo = selected;
                }
                SectionManager.InvalidateCache();
            }
            else if (lb == null && _open != null)
            {
                _open = null;
                _row = -1;
                SectionManager.InvalidateCache();
                ReturnFocus();
            }
        }

        private static void ReturnFocus()
        {
            var target = _returnTo;
            _returnTo = null;
            try
            {
                if (target == null || !target.activeInHierarchy)
                {
                    var rank = UnityEngine.Object.FindObjectOfType<PackShop>()?.VersusRank?.Button;
                    target = rank != null && rank.gameObject.activeInHierarchy ? rank.gameObject : null;
                }
                if (target == null || EventSystem.current == null) return;
                var current = EventSystem.current.currentSelectedGameObject;
                if (current != null && current.activeInHierarchy && current != target) return;
                SectionManager.MarkModDriven();
                if (current == target) EventSystem.current.SetSelectedGameObject(null);
                EventSystem.current.SetSelectedGameObject(target);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Leaderboard] Return focus error: {ex.Message}");
            }
        }

        /// <summary>Keys while the leaderboard is open. True when the key was used.</summary>
        public static bool HandleInput()
        {
            var lb = Active();
            if (lb == null || PickerOpen()) return false;

            int move = 0;
            if (Input.GetKeyDown(KeyCode.DownArrow)) move = 1;
            else if (Input.GetKeyDown(KeyCode.UpArrow)) move = -1;
            else if (Input.GetKeyDown(KeyCode.PageDown)) move = 10;
            else if (Input.GetKeyDown(KeyCode.PageUp)) move = -10;
            bool home = Input.GetKeyDown(KeyCode.Home), end = Input.GetKeyDown(KeyCode.End);
            if (move == 0 && !home && !end) return false;

            var rows = Rows(lb);
            if (rows.Count == 0)
            {
                AccessibilityManager.Announce("No players listed");
                return true;
            }
            if (home) _row = 0;
            else if (end) _row = rows.Count - 1;
            else _row = Math.Max(0, Math.Min(rows.Count - 1, (_row < 0 && move > 0 ? -1 : _row) + move));
            AccessibilityManager.Announce($"{rows[_row]}, {_row + 1} of {rows.Count}");
            return true;
        }

        private static bool PickerOpen()
        {
            try
            {
                var picker = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.UI.Picker>();
                return picker?.Container != null && picker.Container.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        private static string Countdown(Transform container, Il2CppTMPro.TMP_Text value)
        {
            try
            {
                if (container == null || !container.gameObject.activeInHierarchy) return null;
                string v = Text(value);
                if (v == null) return null;
                // The label beside the value: "Season reset: " + "16 days".
                foreach (var t in container.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false))
                {
                    if (t == value) continue;
                    string label = Text(t);
                    if (label != null) return $"{label.TrimEnd(':', ' ')}: {v}";
                }
                return v;
            }
            catch { return null; }
        }

        private static string Text(Il2CppTMPro.TMP_Text t)
        {
            try
            {
                if (t == null || !t.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(t.text)) return null;
                string s = TextExtractor.CleanTextPublic(t.text);
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
        }
    }
}
