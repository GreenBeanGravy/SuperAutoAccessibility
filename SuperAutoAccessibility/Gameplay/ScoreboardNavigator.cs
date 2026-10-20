using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using Il2CppSpacewood.Core.Models;
using Il2CppSpacewood.Unity.UI;
using Il2CppSpacewood.Unity.MonoBehaviours.Build;
using Il2CppSpacewood.Unity.Views;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// The game's scoreboard (HangarScoreboard): the other players and their teams. It opens with
    /// O in the shop, as the last opponent from the last-battle menu, and after a Versus battle as
    /// the battle's tally. Every pet on it is a button (MinionCanvasView), so each opponent is a
    /// group and each pet a control in the accessibility tree, and the same keys drive the mod's
    /// own speech: Tab and Shift+Tab (or Page Down and Page Up) between opponents, Left and Right
    /// between pets, Up and Down through the pet's details, Enter for all of them at once.
    /// </summary>
    internal static class ScoreboardNavigator
    {
        internal const string Hint =
            "Tab and Shift Tab move between opponents, Left and Right between their pets, " +
            "Up and Down read a pet's details, Enter reads them all. Escape closes.";

        /// <summary>After a Versus battle the board shares the screen with the result, whose Enter continues.</summary>
        internal const string TallyHint =
            "Tab and Shift Tab move between opponents, Left and Right between their pets, " +
            "Up and Down read a pet's details. Enter continues.";

        /// <summary>The keys for the board on screen.</summary>
        internal static string CurrentHint => GameplayPhaseDetector.IsBattlePhase() ? TallyHint : Hint;

        // After a battle the board opens with the result screen, which the player hears first:
        // the board waits for a key before it reads anything.
        private static bool _waitingForEntry;

        internal sealed class Pet
        {
            public SelectableBase Button;   // null when the scoreboard shows no pet views
            public List<string> Lines;      // [0] summary, then details
        }

        internal sealed class Opponent
        {
            public HangarScoreboardEntry Entry;
            public string Header;           // name, board name, lives, next, outcome
            public string Details;          // pack, relics
            public List<Pet> Pets = new List<Pet>();
            public string GroupLabel => string.IsNullOrEmpty(Details) ? Header : Header + ", " + Details;
        }

        private static HangarScoreboard _board;
        private static List<Opponent> _opponents = new List<Opponent>();
        private static int _opp, _pet, _line;
        private static int _openedFrame = -1;
        private static float _nextPoll;
        private static float _hintAt = -1f;
        private static Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle _uiBattle;
        private static readonly Dictionary<int, List<string>> _linesByView = new Dictionary<int, List<string>>();

        public static bool IsOpen => _board != null;

        /// <summary>The scoreboard on screen, or null.</summary>
        public static HangarScoreboard Active()
        {
            try
            {
                var hangar = GameplayPhaseDetector.GetHangarMain();
                if (hangar != null)
                {
                    var go = hangar.GetScoreboard();
                    if (go != null && go.activeInHierarchy)
                    {
                        var board = go.GetComponent<HangarScoreboard>();
                        if (board == null) board = go.GetComponentInChildren<HangarScoreboard>();
                        if (board == null && hangar.Scoreboard != null && hangar.Scoreboard.gameObject.activeInHierarchy)
                            board = hangar.Scoreboard;
                        if (board != null) return board;
                    }
                }

                if (GameplayPhaseDetector.IsBattlePhase())
                {
                    if (_uiBattle == null)
                        _uiBattle = SceneObjects.Find<Il2CppSpacewood.Unity.MonoBehaviours.Battle.UIBattle>();
                    var tally = _uiBattle != null ? _uiBattle.TallyScoreboard : null;
                    if (tally != null && tally.gameObject.activeInHierarchy &&
                        tally.Scoreboard != null && tally.Scoreboard.gameObject.activeInHierarchy)
                        return tally.Scoreboard;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Changes when entries or pets come and go, so sections are rebuilt.</summary>
        public static string Signature(HangarScoreboard board)
        {
            if (board == null) return "none";
            try
            {
                int entries = board.Entries != null ? board.Entries.Count : 0;
                int pets = board.GetComponentsInChildren<MinionCanvasView>(false).Length;
                return board.GetInstanceID() + "/" + entries + "/" + pets;
            }
            catch { return "?"; }
        }

        // ---- model ---------------------------------------------------------------------------

        /// <summary>The opponents on a scoreboard, in its order, leaving out the player's own entry.</summary>
        public static List<Opponent> Read(HangarScoreboard board)
        {
            var list = new List<Opponent>();
            if (board == null) return list;
            try
            {
                var entries = board.Entries;
                if (entries == null) return list;
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    try
                    {
                        if (entry == null || !entry.gameObject.activeInHierarchy) continue;
                        bool self = false;
                        try { self = entry.Self; } catch { }
                        if (self) continue;

                        var opp = new Opponent { Entry = entry };
                        UserVersusOpponent model = null;
                        try { model = entry.Opponent; } catch { }
                        opp.Header = Header(entry, model);
                        opp.Details = Details(model);

                        var views = entry.MinionContainer != null
                            ? entry.MinionContainer.GetComponentsInChildren<MinionCanvasView>(false)
                            : null;
                        if (views != null)
                        {
                            foreach (var view in views)
                            {
                                if (view == null || view.Model == null || view.Button == null) continue;
                                opp.Pets.Add(new Pet { Button = view.Button, Lines = LinesFor(view) });
                            }
                        }

                        // A scoreboard set to hide the pets still has them in its model.
                        if (opp.Pets.Count == 0 && model?.Minions != null)
                        {
                            for (int m = 0; m < model.Minions.Count; m++)
                            {
                                var minion = model.Minions[m];
                                if (minion != null)
                                    opp.Pets.Add(new Pet { Lines = DetailLineProvider.BuildOpponentMinionLines(minion) });
                            }
                        }
                        list.Add(opp);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Scoreboard] Skipped an entry: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Scoreboard] Read error: {ex.Message}");
            }
            return list;
        }

        private static List<string> LinesFor(MinionCanvasView view)
        {
            int key = view.GetInstanceID();
            if (_linesByView.TryGetValue(key, out var lines)) return lines;
            lines = DetailLineProvider.BuildOpponentMinionLines(view.Model);
            _linesByView[key] = lines;
            return lines;
        }

        private static string Header(HangarScoreboardEntry entry, UserVersusOpponent model)
        {
            var parts = new List<string>();
            string name = null;
            try { name = entry.DisplayName; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = TextOf(entry.PlayerName);
            if (string.IsNullOrWhiteSpace(name)) name = model?.DisplayName;
            parts.Add(string.IsNullOrWhiteSpace(name) ? "Opponent" : name.Trim());

            string board = null;
            try
            {
                string adj = model?.BoardAdjective, noun = model?.BoardNoun;
                if (!string.IsNullOrEmpty(adj) && !string.IsNullOrEmpty(noun)) board = $"The {adj} {noun}";
                else if (!string.IsNullOrEmpty(noun)) board = noun;
            }
            catch { }
            if (string.IsNullOrEmpty(board)) board = TextOf(entry.BoardNameLabel);
            if (!string.IsNullOrEmpty(board)) parts.Add(board);

            try
            {
                int lives = entry.Lives;
                parts.Add(lives == 1 ? "1 life" : $"{lives} lives");
            }
            catch { }

            try { if (entry.Next) parts.Add("next opponent"); } catch { }

            try
            {
                if (model != null && model.Outcome.HasValue)
                    parts.Add(PetStatsReader.SplitCamelCase(model.Outcome.Value.ToString()).ToLowerInvariant());
            }
            catch { }

            return string.Join(", ", parts);
        }

        private static string Details(UserVersusOpponent model)
        {
            if (model == null) return null;
            var parts = new List<string>();
            try
            {
                string pack = PackName(model.Pack);
                if (!string.IsNullOrEmpty(pack))
                    parts.Add((model.WildPack ? "wild " : "") + pack + " pack");
            }
            catch { }
            try
            {
                var relics = model.Relics;
                if (relics != null && relics.Count > 0)
                {
                    var names = new List<string>();
                    for (int i = 0; i < relics.Count; i++)
                        if (relics[i] != null) names.Add(PetStatsReader.SplitCamelCase(relics[i].Enum.ToString()));
                    if (names.Count > 0) parts.Add("relics: " + string.Join(", ", names));
                }
            }
            catch { }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        /// <summary>"Turtle", "Golden": the enum says Pack1, Pack4.</summary>
        internal static string PackName(Il2CppSpacewood.Core.Enums.Pack pack)
        {
            try
            {
                string name = Il2CppSpacewood.Core.Enums.PackExtensions.PrettyName(pack);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return PetStatsReader.SplitCamelCase(pack.ToString());
        }

        private static string TextOf(Il2CppTMPro.TMP_Text t)
        {
            try
            {
                if (t == null || !t.gameObject.activeInHierarchy) return null;
                string s = TextExtractor.CleanTextPublic(t.text);
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
        }

        // ---- for the tree and the mod's announcements ----------------------------------------

        /// <summary>Whether this is a pet's button on an open scoreboard.</summary>
        public static bool IsPetButton(GameObject go)
        {
            if (go == null || _board == null) return false;
            try
            {
                var view = go.GetComponentInParent<MinionCanvasView>();
                return view != null && view.Button != null && view.Button.gameObject == go &&
                       go.GetComponentInParent<HangarScoreboard>() != null;
            }
            catch { return false; }
        }

        /// <summary>A pet button's summary and details, or false when it is not one.</summary>
        public static bool TryDescribe(GameObject go, out string label, out string description)
        {
            label = description = null;
            if (!IsPetButton(go)) return false;
            try
            {
                var lines = LinesFor(go.GetComponentInParent<MinionCanvasView>());
                if (lines == null || lines.Count == 0) return false;
                label = lines[0];
                if (lines.Count > 1) description = TextExtractor.JoinSentences(lines.GetRange(1, lines.Count - 1));
                return true;
            }
            catch { return false; }
        }

        // ---- open, close, keys ---------------------------------------------------------------

        public static void Poll()
        {
            float now = Time.realtimeSinceStartup;
            if (_hintAt > 0f && now >= _hintAt)
            {
                _hintAt = -1f;
                if (_board != null && !Tree.SapA11y.OwnsSpeech) AccessibilityManager.Announce(Hint, interrupt: false);
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 0.1f;

            var board = Active();
            if (board != null && board != _board) Open(board);
            else if (board == null && _board != null) Closed();
            else if (_board != null && _emptySince >= 0f) WaitForOpponents(now);
        }

        // The board can open a frame before its entries are spawned; "no opponents" waits a bit.
        private static float _emptySince = -1f;
        private const float EmptyGraceSeconds = 0.5f;

        private static void Open(HangarScoreboard board)
        {
            _board = board;
            _openedFrame = Time.frameCount;
            _linesByView.Clear();
            _opponents = Read(board);
            if (GameplayPhaseDetector.IsShopPhase()) ShopNavigationManager.IsInputSuppressed = true;
            SectionManager.InvalidateCache();

            _emptySince = -1f;
            if (_opponents.Count == 0)
            {
                _emptySince = Time.realtimeSinceStartup;
                return;
            }
            Introduce();
        }

        private static void WaitForOpponents(float now)
        {
            _opponents = Read(_board);
            if (_opponents.Count > 0)
            {
                _emptySince = -1f;
                SectionManager.InvalidateCache();
                Introduce();
                return;
            }
            if (now - _emptySince < EmptyGraceSeconds) return;
            _emptySince = -1f;
            // Arena has no other players: an empty board over the shop would only hold the keys,
            // so it closes again, as it always has.
            if (GameplayPhaseDetector.IsShopPhase())
            {
                try { GameplayPhaseDetector.GetHangarMain()?.CloseScoreboards(); } catch { }
                Closed();
                AccessibilityManager.Announce("No opponents");
            }
            else
            {
                AccessibilityManager.Announce("Scoreboard. No opponents.");
            }
            MelonLogger.Msg("[Scoreboard] Opened with no opponents");
        }

        private static void Introduce()
        {
            // Start on the next opponent when the game marks one.
            _opp = 0;
            for (int i = 0; i < _opponents.Count; i++)
            {
                try { if (_opponents[i].Entry.Next) { _opp = i; break; } } catch { }
            }
            _pet = 0;
            _line = 0;
            SectionManager.GetSections(); // label overrides in place before the focus is announced
            MelonLogger.Msg($"[Scoreboard] Opened: {_opponents.Count} opponents");
            // After a Versus battle the result screen opens with the board and says "Tab reads
            // the scoreboard". Reading the board here only had the result talk over it, and the
            // result's keys left the board unreachable.
            if (GameplayPhaseDetector.IsBattlePhase())
            {
                _waitingForEntry = true;
                return;
            }
            _waitingForEntry = false;
            Focus($"Scoreboard, {CountText()}. {OpponentIntro()}");
            _hintAt = Time.realtimeSinceStartup + 1.5f;
        }

        private static string CountText() =>
            _opponents.Count == 1 ? "1 opponent" : $"{_opponents.Count} opponents";

        /// <summary>
        /// Added to the battle result while the board is up beside it: "Tab reads the
        /// scoreboard, 2 opponents." Null when there is no board.
        /// </summary>
        public static string TallyLine() =>
            _board != null && _waitingForEntry && _opponents.Count > 0
                ? $"Tab reads the scoreboard, {CountText()}."
                : null;

        /// <summary>A key that moves around the board (not Enter or Space, which the result keeps).</summary>
        public static bool NavigationKeyDown() =>
            ModInput.GetKeyDown(KeyCode.Tab) || ModInput.GetKeyDown(KeyCode.PageDown) || ModInput.GetKeyDown(KeyCode.PageUp) ||
            ModInput.GetKeyDown(KeyCode.LeftArrow) || ModInput.GetKeyDown(KeyCode.RightArrow) ||
            ModInput.GetKeyDown(KeyCode.UpArrow) || ModInput.GetKeyDown(KeyCode.DownArrow) ||
            ModInput.GetKeyDown(KeyCode.Home) || ModInput.GetKeyDown(KeyCode.End);

        private static void Closed()
        {
            _board = null;
            _waitingForEntry = false;
            _emptySince = -1f;
            _opponents.Clear();
            _linesByView.Clear();
            _hintAt = -1f;
            ShopNavigationManager.IsInputSuppressed = false;
            SectionManager.InvalidateCache();
            MelonLogger.Msg("[Scoreboard] Closed");
        }

        /// <summary>Closes the scoreboard the way its own close button does.</summary>
        public static void Close()
        {
            var board = _board;
            try
            {
                var hangar = GameplayPhaseDetector.GetHangarMain();
                if (hangar != null && board != null && (board == hangar.Scoreboard || board == hangar.ScoreboardMini))
                    hangar.CloseScoreboards();
                else
                    board?.HandleClose();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Scoreboard] Close error: {ex.Message}");
            }
            Closed();
            if (!Tree.SapA11y.OwnsSpeech) AccessibilityManager.Announce("Scoreboard closed");
        }

        /// <summary>Handles a key while the scoreboard is open. True when the key was used.</summary>
        public static bool HandleInput()
        {
            if (_board == null) return false;
            if (_opponents.Count == 0)
            {
                if (ModInput.GetKeyDown(KeyCode.Escape) || (ModInput.GetKeyDown(KeyCode.O) && Time.frameCount != _openedFrame))
                {
                    Close();
                    return true;
                }
                return false;
            }

            bool shift = ModInput.GetKey(KeyCode.LeftShift) || ModInput.GetKey(KeyCode.RightShift);
            if (_waitingForEntry)
            {
                // The first move enters the board on its first opponent, whatever the key.
                if (!NavigationKeyDown()) return false;
                _waitingForEntry = false;
                Refresh();
                if (_opponents.Count == 0) return true;
                Focus($"Scoreboard, {CountText()}. {OpponentIntro()}");
                return true;
            }
            if (ModInput.GetKeyDown(KeyCode.O) && Time.frameCount != _openedFrame) { Close(); return true; }
            if (ModInput.GetKeyDown(KeyCode.Tab)) { MoveOpponent(shift ? -1 : 1); return true; }
            if (ModInput.GetKeyDown(KeyCode.PageDown)) { MoveOpponent(1); return true; }
            if (ModInput.GetKeyDown(KeyCode.PageUp)) { MoveOpponent(-1); return true; }
            if (ModInput.GetKeyDown(KeyCode.RightArrow)) { MovePet(_pet + 1); return true; }
            if (ModInput.GetKeyDown(KeyCode.LeftArrow)) { MovePet(_pet - 1); return true; }
            if (ModInput.GetKeyDown(KeyCode.Home)) { MovePet(0); return true; }
            if (ModInput.GetKeyDown(KeyCode.End)) { MovePet(int.MaxValue); return true; }
            if (ModInput.GetKeyDown(KeyCode.DownArrow)) { MoveLine(1); return true; }
            if (ModInput.GetKeyDown(KeyCode.UpArrow)) { MoveLine(-1); return true; }
            if (ModInput.GetKeyDown(KeyCode.Space)) { SpeakLine(); return true; }
            if (ModInput.GetKeyDown(KeyCode.Return) || ModInput.GetKeyDown(KeyCode.KeypadEnter)) { SpeakAll(); return true; }
            return false;
        }

        private static void Refresh()
        {
            // Versus updates the board while it is open (a player is knocked out, a team arrives).
            var current = CurrentPet()?.Button;
            var entry = _opp < _opponents.Count ? _opponents[_opp].Entry : null;
            _opponents = Read(_board);
            if (_opponents.Count == 0) return;
            int oi = _opponents.FindIndex(o => o.Entry == entry);
            _opp = oi >= 0 ? oi : Math.Min(_opp, _opponents.Count - 1);
            var pets = _opponents[_opp].Pets;
            int pi = current != null ? pets.FindIndex(p => p.Button == current) : -1;
            _pet = pi >= 0 ? pi : Math.Max(0, Math.Min(_pet, pets.Count - 1));
        }

        private static Pet CurrentPet()
        {
            if (_opp < 0 || _opp >= _opponents.Count) return null;
            var pets = _opponents[_opp].Pets;
            return _pet >= 0 && _pet < pets.Count ? pets[_pet] : null;
        }

        private static string OpponentIntro()
        {
            var o = _opponents[_opp];
            string intro = $"Opponent {_opp + 1} of {_opponents.Count}: {o.GroupLabel}";
            if (o.Pets.Count == 0) intro += ", no pets";
            return intro;
        }

        private static void MoveOpponent(int direction)
        {
            Refresh();
            if (_opponents.Count == 0) return;
            int next = _opp + direction;
            if (next < 0 || next >= _opponents.Count)
            {
                // Edge: say where we are again rather than wrap.
                Speak(OpponentIntro());
                return;
            }
            _opp = next;
            _pet = 0;
            _line = 0;
            Focus(OpponentIntro());
        }

        private static void MovePet(int index)
        {
            Refresh();
            var pets = _opponents[_opp].Pets;
            if (pets.Count == 0)
            {
                Speak(OpponentIntro());
                return;
            }
            int next = Math.Max(0, Math.Min(index, pets.Count - 1));
            if (next == _pet && index != 0 && index != int.MaxValue)
            {
                Speak(PetLine(pets[_pet]));
                return;
            }
            _pet = next;
            _line = 0;
            Focus(null);
        }

        /// <summary>
        /// Puts the selection on the current pet. The mod's focus announcement (or the screen
        /// reader, from the tree) then reads it; a pet without a button is spoken directly.
        /// </summary>
        private static void Focus(string prefix)
        {
            var pet = CurrentPet();
            var button = pet?.Button;
            if (button != null && button.gameObject.activeInHierarchy && EventSystem.current != null)
            {
                if (!string.IsNullOrEmpty(prefix)) AccessibilityManager.SetCurrentSectionName(prefix);
                if (EventSystem.current.currentSelectedGameObject == button.gameObject)
                    EventSystem.current.SetSelectedGameObject(null);
                SectionManager.MarkModDriven();
                EventSystem.current.SetSelectedGameObject(button.gameObject);
                return;
            }
            string text = pet != null ? PetLine(pet) : null;
            if (!string.IsNullOrEmpty(prefix)) text = string.IsNullOrEmpty(text) ? prefix : prefix + ". " + text;
            if (!string.IsNullOrEmpty(text)) AccessibilityManager.Announce(text);
        }

        private static string PetLine(Pet pet)
        {
            var pets = _opponents[_opp].Pets;
            return $"{pet.Lines[0]}, {pets.IndexOf(pet) + 1} of {pets.Count}";
        }

        private static void MoveLine(int direction)
        {
            var pet = CurrentPet();
            if (pet == null || pet.Lines.Count == 0)
            {
                Speak(OpponentIntro());
                return;
            }
            _line = Math.Max(0, Math.Min(_line + direction, pet.Lines.Count - 1));
            AccessibilityManager.Announce(pet.Lines[_line]);
        }

        private static void SpeakLine()
        {
            var pet = CurrentPet();
            if (pet == null || pet.Lines.Count == 0) { Speak(OpponentIntro()); return; }
            AccessibilityManager.Announce(pet.Lines[Math.Min(_line, pet.Lines.Count - 1)]);
        }

        private static void SpeakAll()
        {
            var pet = CurrentPet();
            if (pet == null) { Speak(OpponentIntro()); return; }
            AccessibilityManager.Announce(TextExtractor.JoinSentences(pet.Lines));
        }

        /// <summary>Speech that is not a focus change, so the screen reader would not say it.</summary>
        private static void Speak(string text)
        {
            if (!string.IsNullOrEmpty(text)) AccessibilityManager.Announce(text);
        }
    }
}
