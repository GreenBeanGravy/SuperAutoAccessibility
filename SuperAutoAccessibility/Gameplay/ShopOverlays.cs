using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using Il2CppSpacewood.Core.Enums;
using Il2CppSpacewood.Unity;
using Il2CppSpacewood.Unity.Extensions;
using Il2CppSpacewood.Unity.MonoBehaviours.Build;
using Il2CppSpacewood.Scripts.MonoBehaviours.Build.Hangar;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// The shop's own overlays: the deck viewer (every pet and food of the pack, by tier), the
    /// last battle menu (watch or play back the last battle) and the chooser (a reward to pick
    /// after a pet levels up, a toy, stock food). They are menus over the run: the shop's keys
    /// pause while one is open, the game keeps its selection so Enter presses the focused
    /// control, and the accessibility tree publishes them.
    /// </summary>
    internal static class ShopOverlays
    {
        public static DeckViewer OpenDeckViewer()
        {
            try
            {
                var dv = GameplayPhaseDetector.GetHangarMain()?.DeckViewer;
                return dv != null && dv.gameObject.activeInHierarchy ? dv : null;
            }
            catch { return null; }
        }

        public static LastBattleMenu OpenLastBattleMenu()
        {
            try
            {
                var menu = GameplayPhaseDetector.GetHangarMain()?.LastBattleMenu;
                return menu?.Container != null && menu.Container.gameObject.activeInHierarchy ? menu : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The chooser while it is up. Its object stays active all run; only its canvas shows.
        /// </summary>
        public static Chooser OpenChooser()
        {
            try
            {
                var chooser = GameplayPhaseDetector.GetHangarMain()?.Chooser;
                return chooser != null && IsOpen(chooser) ? chooser : null;
            }
            catch { return null; }
        }

        public static bool IsOpen(Chooser chooser)
        {
            try
            {
                var canvas = chooser != null ? chooser.transform.Find("Canvas") : null;
                return canvas != null && canvas.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        /// <summary>
        /// False after View, which hides the choices so a sighted player can look at the board.
        /// </summary>
        public static bool ChoicesShown(Chooser chooser)
        {
            try { return chooser?.Visible != null && chooser.Visible.gameObject.activeInHierarchy; }
            catch { return false; }
        }

        public static bool AnyOpen() =>
            GameplayPhaseDetector.IsShopPhase() &&
            (OpenDeckViewer() != null || OpenLastBattleMenu() != null || OpenChooser() != null);

        // ---- chooser -------------------------------------------------------------------------

        /// <summary>"You increased a pet level. Choose shop reward": the label's two lines.</summary>
        public static string ChooserTitle(Chooser chooser)
        {
            try
            {
                string raw = chooser?.Label != null ? chooser.Label.text : null;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var lines = new List<string>();
                    foreach (var line in raw.Split('\n'))
                        lines.Add(TextExtractor.CleanTextPublic(line));
                    string title = TextExtractor.JoinSentences(lines);
                    if (!string.IsNullOrEmpty(title)) return title;
                }
            }
            catch { }
            return "Choose a reward";
        }

        /// <summary>The guide under the choices, when the chooser shows one.</summary>
        public static string ChooserGuide(Chooser chooser)
        {
            try
            {
                if (chooser?.Guide == null || !chooser.Guide.gameObject.activeInHierarchy) return null;
                string guide = TextExtractor.CleanTextPublic(chooser.Guide.text);
                return string.IsNullOrEmpty(guide) ? null : guide;
            }
            catch { return null; }
        }

        /// <summary>The guide, how many choices there are and the keys.</summary>
        public static string ChooserHint(Chooser chooser)
        {
            var parts = new List<string>();
            string guide = ChooserGuide(chooser);
            if (guide != null) parts.Add(guide);
            if (!ChoicesShown(chooser))
            {
                parts.Add("The choices are hidden. Show choices brings them back.");
            }
            else
            {
                int count = Choices(chooser).Count;
                parts.Add(count == 1 ? "1 choice." : $"{count} choices.");
                parts.Add("Up and Down move through them, Enter takes one.");
            }
            bool skip = false;
            try { skip = chooser.SkipButton != null && chooser.SkipButton.gameObject.activeInHierarchy; } catch { }
            if (skip) parts.Add("Tab reaches Skip.");
            parts.Add("T reads your team, R your gold and lives.");
            return TextExtractor.JoinSentences(parts);
        }

        /// <summary>"Team: Level 2 Ant, 3/4. Fish, 2/3." For deciding on a reward.</summary>
        public static string TeamSummary()
        {
            try
            {
                var items = GameplayPhaseDetector.GetHangarMain()?.Overlay?.BoardModel?.Minions?.Items;
                var pets = new List<string>();
                for (int i = 0; items != null && i < items.Count; i++)
                {
                    var m = items[i];
                    if (m == null) continue;
                    string pet = PetStatsReader.ReadMinionNameAndStats(m);
                    try { if (m.Level > 1) pet = $"Level {m.Level} {pet}"; } catch { }
                    pets.Add(pet);
                }
                return pets.Count == 0 ? "Your team is empty" : "Team: " + string.Join(". ", pets);
            }
            catch { return "Team unknown"; }
        }

        public static List<GameObject> Choices(Chooser chooser)
        {
            var list = new List<GameObject>();
            try
            {
                var items = chooser?.Items;
                if (items == null) return list;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item?.Button != null && item.Button.gameObject.activeInHierarchy)
                        list.Add(item.Button.gameObject);
                }
            }
            catch { }
            return list;
        }

        private static ChooserItem ChoiceOf(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var item = go.GetComponentInParent<ChooserItem>();
                return item?.Button != null && item.Button.gameObject == go ? item : null;
            }
            catch { return null; }
        }

        public static bool IsChoice(GameObject go) => ChoiceOf(go) != null;

        /// <summary>
        /// A choice as name and stats ("Rabbit, 1 attack, 2 health"), with its ability, perk and
        /// tier as the description. Its own texts are bare numbers ("1, 2, Rabbit, 3, ...").
        /// </summary>
        public static bool DescribeChoice(GameObject go, out string label, out string description)
        {
            label = null;
            description = null;
            var item = ChoiceOf(go);
            if (item == null) return false;
            try
            {
                List<string> lines = null;
                if (item.MinionView != null && item.MinionView.gameObject.activeInHierarchy && item.MinionView.Model != null)
                {
                    lines = DetailLineProvider.BuildMinionShopLines(item.MinionView.Model);
                    // Name, then stats: one label.
                    if (lines.Count >= 2)
                    {
                        lines[0] = lines[0] + ", " + lines[1];
                        lines.RemoveAt(1);
                    }
                }
                else if (item.SpellView != null && item.SpellView.gameObject.activeInHierarchy && item.SpellView.Model != null)
                {
                    lines = DetailLineProvider.BuildSpellShopLines(item.SpellView.Model);
                }

                if (lines != null && lines.Count > 0)
                {
                    // A reward costs nothing: the shop price and the "Pet" type line say nothing here.
                    lines.RemoveAll(l => l == "Pet" || l.EndsWith(" gold"));
                    label = lines[0];
                    lines.RemoveAt(0);
                    description = lines.Count > 0 ? TextExtractor.JoinSentences(lines) : null;
                    return true;
                }

                // An ability card (a toy's or a transformation's): its text is all there is.
                string text = item.Ability != null && item.Ability.gameObject.activeInHierarchy
                    ? TextExtractor.GetElementText(item.Ability.gameObject)
                    : TextExtractor.GetElementText(go);
                label = string.IsNullOrEmpty(text) ? "Choice" : text;
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"DescribeChoice error: {ex.Message}");
                label = "Choice";
                return true;
            }
        }

        // ---- deck viewer ---------------------------------------------------------------------

        /// <summary>
        /// "Tier 2", or "Tier 4, from turn 7" while the run has not reached it. The game labels
        /// each row only with the turn its tier opens ("Turn 3").
        /// </summary>
        public static string RowName(DeckViewerRow row, int index)
        {
            string name = $"Tier {index + 1}";
            try
            {
                bool locked = row.Conceal != null && row.Conceal.gameObject.activeInHierarchy;
                string turn = row.Text != null ? TextExtractor.CleanTextPublic(row.Text.text) : null;
                if (locked && !string.IsNullOrEmpty(turn))
                    name += ", from " + char.ToLowerInvariant(turn[0]) + turn.Substring(1);
            }
            catch { }
            return name;
        }

        /// <summary>"Turtle pack. Tab moves between the tiers, ..."</summary>
        public static string DeckViewerHint(DeckViewer dv)
        {
            var parts = new List<string>();
            try
            {
                string pack = null;
                if (dv.Custom && dv.PackNameCustom != null && dv.PackNameCustom.gameObject.activeInHierarchy)
                    pack = TextExtractor.CleanTextPublic(dv.PackNameCustom.text);
                if (string.IsNullOrEmpty(pack)) pack = ScoreboardNavigator.PackName(dv.Pack);
                if (!string.IsNullOrEmpty(pack))
                    parts.Add(pack.EndsWith(" pack", StringComparison.OrdinalIgnoreCase) ? pack : pack + " pack");
            }
            catch { }
            parts.Add("Tab moves between the tiers, Up and Down through their pets and foods. Escape closes.");
            return TextExtractor.JoinSentences(parts);
        }

        private static DeckViewerItem DeckItemOf(GameObject go)
        {
            if (go == null || OpenDeckViewer() == null) return null;
            try
            {
                var item = go.GetComponentInParent<DeckViewerItem>();
                return item?.Button != null && item.Button.gameObject == go ? item : null;
            }
            catch { return null; }
        }

        public static bool IsDeckItem(GameObject go) => DeckItemOf(go) != null;

        private static readonly Dictionary<int, (string label, string brief, string description)> _deckItemCache =
            new Dictionary<int, (string, string, string)>();

        /// <summary>
        /// A deck viewer pet as "Snail, 2 attack, 3 health" with its ability at each level as the
        /// description; a food as its name with what it does. The item's minion and spell fields
        /// are Il2Cpp nullables that always read the first enum value (every pet was "Ant"), so
        /// the item is identified by its icon's sprite, as the news screen does.
        /// </summary>
        public static bool DescribeDeckItem(GameObject go, out string label, out string brief, out string description)
        {
            label = brief = description = null;
            var item = DeckItemOf(go);
            if (item == null) return false;
            string sprite = null;
            try { sprite = item.Icon?.sprite?.name; } catch { }
            if (string.IsNullOrEmpty(sprite)) return false;
            if (sprite.EndsWith("_2x")) sprite = sprite.Substring(0, sprite.Length - 3);

            string ribbons = Ribbons(item);
            int key = go.GetInstanceID() ^ sprite.GetHashCode() ^ (ribbons ?? "").GetHashCode();
            if (_deckItemCache.TryGetValue(key, out var cached))
            {
                (label, brief, description) = cached;
                return true;
            }

            bool food = IsFoodItem(item);
            if (!(food ? TrySpell(sprite, ref label, ref description) || TryMinion(sprite, ref label, ref brief, ref description)
                       : TryMinion(sprite, ref label, ref brief, ref description) || TrySpell(sprite, ref label, ref description)))
                label = PetStatsReader.SplitCamelCase(sprite);
            if (ribbons != null) label += ", " + ribbons;
            if (brief == null) brief = description;
            _deckItemCache[key] = (label, brief, description);
            return true;
        }

        /// <summary>
        /// With the Achievements switch on, each pet shows its two ribbons, black until earned
        /// (red: won Arena with it at level 3; blue: the same on hard).
        /// </summary>
        private static string Ribbons(DeckViewerItem item)
        {
            try
            {
                if (item.AchievementsContainer == null || !item.AchievementsContainer.gameObject.activeInHierarchy) return null;
                var parts = new List<string>();
                if (AchievementsNavigator.Earned(item.Star)) parts.Add("red ribbon");
                if (AchievementsNavigator.Earned(item.SuperStar)) parts.Add("blue ribbon");
                return parts.Count > 0 ? string.Join(", ", parts) : "no ribbons yet";
            }
            catch { return null; }
        }

        private static bool IsFoodItem(DeckViewerItem item)
        {
            try
            {
                var row = item.GetComponentInParent<DeckViewerRow>();
                return row?.FoodContainer != null && item.transform.IsChildOf(row.FoodContainer);
            }
            catch { return false; }
        }

        private static bool TryMinion(string sprite, ref string label, ref string brief, ref string description)
        {
            try
            {
                if (!Enum.TryParse(sprite, true, out MinionEnum minion)) return false;
                var templates = MinionConstants.Minions;
                if (templates == null || !templates.ContainsKey(minion)) return false;
                var template = templates[minion];
                string name = null;
                try { name = LanguageLibrary.GetMinionName(minion); } catch { }
                if (string.IsNullOrEmpty(name)) name = PetStatsReader.SplitCamelCase(minion.ToString());
                label = $"{name}, {template.Attack} attack, {template.Health} health";

                var levels = new List<string>();
                var abilities = template.AbilityEnums;
                for (int lvl = 1; lvl <= 3 && abilities != null && abilities.Count > 0; lvl++)
                {
                    var parts = new List<string>();
                    for (int i = 0; i < abilities.Count; i++)
                    {
                        try
                        {
                            string about = AbilityEnumExtensions.ToAsset(abilities[i])?.GetAbout(lvl, false, null);
                            if (!string.IsNullOrEmpty(about)) parts.Add(PetStatsReader.StripRichTextTags(about).Trim());
                        }
                        catch { }
                    }
                    if (parts.Count == 0) break;
                    string text = TextExtractor.JoinSentences(parts);
                    if (lvl == 1) brief = text;
                    levels.Add($"Level {lvl}: {text}");
                }
                description = levels.Count > 0 ? TextExtractor.JoinSentences(levels) : "No ability";
                if (brief == null) brief = "No ability";
                return true;
            }
            catch { return false; }
        }

        private static bool TrySpell(string sprite, ref string label, ref string description)
        {
            try
            {
                if (!Enum.TryParse(sprite, true, out SpellEnum spell)) return false;
                var asset = SpellEnumExtensions.ToAsset(spell);
                if (asset == null) return false;
                string name = asset.GetName();
                label = string.IsNullOrEmpty(name) ? PetStatsReader.SplitCamelCase(spell.ToString()) : name;
                var parts = new List<string>();
                string about = asset.GetAbility(false);
                if (!string.IsNullOrEmpty(about)) parts.Add(PetStatsReader.StripRichTextTags(about).Trim());
                // A food that gives a perk ("Give one pet the Honey perk."): what the perk does.
                try
                {
                    string perk = asset.GetPerkAbility(false, DetailLineProvider.GetPerkLibrary());
                    if (!string.IsNullOrEmpty(perk)) parts.Add("Perk: " + PetStatsReader.StripRichTextTags(perk).Trim());
                }
                catch { }
                description = parts.Count > 0 ? TextExtractor.JoinSentences(parts) : null;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether one of the deck viewer's switches (achievements, custom) is on: the game swaps
        /// the button's sprite.
        /// </summary>
        public static string SwitchState(DeckViewer dv, Il2CppSpacewood.Unity.UI.ButtonBase button)
        {
            try
            {
                if (dv == null || button == null || dv.SpriteOn == null) return null;
                foreach (var img in button.GetComponentsInChildren<UnityEngine.UI.Image>(false))
                {
                    if (img.sprite == null) continue;
                    if (img.sprite.name == dv.SpriteOn.name) return "on";
                    if (dv.SpriteOff != null && img.sprite.name == dv.SpriteOff.name) return "off";
                }
            }
            catch { }
            return null;
        }
    }
}
