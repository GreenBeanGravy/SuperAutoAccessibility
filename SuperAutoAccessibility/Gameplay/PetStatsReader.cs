using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MelonLoader;
using UnityEngine;
using Il2CppSpacewood.Core.Models;
using Il2CppSpacewood.Core.Enums;
using Il2CppSpacewood.Unity.Extensions;
using Il2CppSpacewood.Core.Models.Abilities;
using Il2CppSpacewood.Unity;
using Il2CppSpacewood.Unity.Views;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// Utility class for extracting readable text descriptions from game models.
    /// Converts MinionModel, SpellModel, and BoardModel into screen-reader-friendly strings.
    /// </summary>
    public static class PetStatsReader
    {
        /// <summary>
        /// Reads a minion/pet and returns a human-readable description.
        /// e.g. "Beaver, 3 attack, 2 health, level 1, tier 1, perk: Honey"
        /// </summary>
        public static string ReadMinion(MinionModel minion)
        {
            if (minion == null) return "Empty";

            try
            {
                var parts = new List<string>();

                // Pet name from localization system
                try
                {
                    string name = GetLocalizedName(minion);
                    parts.Add(name);
                }
                catch
                {
                    parts.Add("Unknown pet");
                }

                // Attack
                try
                {
                    int attack = minion.Attack != null ? minion.Attack.Total : 0;
                    parts.Add($"{attack} attack");
                }
                catch { }

                // Health
                try
                {
                    int health = minion.Health != null ? minion.Health.Total : 0;
                    parts.Add($"{health} health");
                }
                catch { }

                // Level
                try
                {
                    int level = minion.Level;
                    if (level > 0)
                    {
                        parts.Add($"level {level}");
                    }
                }
                catch { }

                // Tier
                try { parts.Add($"tier {minion.Tier}"); } catch { }

                // Mana
                try
                {
                    int mana = minion.Mana;
                    if (mana > 0)
                        parts.Add($"{mana} mana");
                }
                catch { }

                // Perk
                try
                {
                    if (HasRealPerk(minion, out var perkVal))
                        parts.Add($"perk: {GetPerkDisplayName(perkVal)}");
                }
                catch { }

                return string.Join(", ", parts);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadMinion error: {ex.Message}");
                return "Unknown pet";
            }
        }

        /// <summary>
        /// Reads a minion with brief format for shop browsing.
        /// e.g. "Beaver, 3/2" (attack/health shorthand)
        /// </summary>
        public static string ReadMinionBrief(MinionModel minion)
        {
            if (minion == null) return "Empty";

            try
            {
                string name = GetLocalizedName(minion);
                int attack = minion.Attack != null ? minion.Attack.Total : 0;
                int health = minion.Health != null ? minion.Health.Total : 0;
                int tier = 0;
                try { tier = minion.Tier; } catch { }
                int price = 3;
                try { price = minion.Price; } catch { }
                return $"{name}, {attack}/{health}, tier {tier}, {price} gold";
            }
            catch
            {
                return "Unknown pet";
            }
        }

        /// <summary>
        /// Reads a minion with full detail (for I key info).
        /// Includes experience, sell value, tribe info.
        /// </summary>
        public static string ReadMinionDetailed(MinionModel minion)
        {
            if (minion == null) return "Empty slot";

            try
            {
                var parts = new List<string>();

                // Pet name
                try
                {
                    string name = GetLocalizedName(minion);
                    parts.Add(name);
                }
                catch { parts.Add("Unknown pet"); }

                // Attack / Health
                try
                {
                    int attack = minion.Attack != null ? minion.Attack.Total : 0;
                    int health = minion.Health != null ? minion.Health.Total : 0;
                    parts.Add($"{attack} attack, {health} health");
                }
                catch { }

                // Level and Experience
                try
                {
                    int level = minion.Level;
                    int exp = minion.Exp;
                    if (level > 0)
                    {
                        if (level < 3)
                        {
                            int expNeeded = GetExpForNextLevel(level);
                            if (expNeeded > 0)
                                parts.Add($"level {level}, {exp} of {expNeeded} experience");
                            else
                                parts.Add($"level {level}, {exp} experience");
                        }
                        else
                            parts.Add($"level {level}");
                    }
                }
                catch { }

                // Mana
                try
                {
                    int mana = minion.Mana;
                    if (mana > 0)
                        parts.Add($"{mana} mana");
                }
                catch { }

                // Tier
                try { parts.Add($"tier {minion.Tier}"); } catch { }

                // Perk with description from PerkLibrary
                try
                {
                    if (HasRealPerk(minion, out var perkValue))
                    {
                        string perkName = GetPerkDisplayName(perkValue);
                        try
                        {
                            var perkLib = UnityEngine.Object.FindObjectOfType<PerkLibrary>();
                            if (perkLib != null)
                            {
                                string perkAbility = perkLib.GetAbility(perkValue, false);
                                if (!string.IsNullOrEmpty(perkAbility))
                                {
                                    perkAbility = StripRichTextTags(perkAbility);
                                    parts.Add($"perk: {perkName}, {perkAbility.Trim()}");
                                }
                                else
                                {
                                    parts.Add($"perk: {perkName}");
                                }
                            }
                            else
                            {
                                parts.Add($"perk: {perkName}");
                            }
                        }
                        catch { parts.Add($"perk: {perkName}"); }
                    }
                }
                catch { }

                // Sell value
                try
                {
                    var sellValue = minion.SellValue;
                    if (sellValue.HasValue)
                        parts.Add($"sell for {sellValue.Value} gold");
                }
                catch { }

                // Tribe
                try
                {
                    var tribe = minion.Tribe;
                    if (tribe != null && tribe.HasValue)
                    {
                        string tribeName = SplitCamelCase(tribe.Value.ToString());
                        parts.Add($"tribe: {tribeName}");
                    }
                }
                catch { }

                // Ability description
                try
                {
                    string abilityText = ReadMinionAbility(minion);
                    if (!string.IsNullOrEmpty(abilityText))
                    {
                        parts.Add($"ability: {abilityText}");
                    }
                }
                catch { }

                return string.Join(", ", parts);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadMinionDetailed error: {ex.Message}");
                return "Unknown pet";
            }
        }

        /// <summary>
        /// Reads a spell/food item.
        /// e.g. "Apple", "Canned Food"
        /// </summary>
        public static string ReadSpell(SpellModel spell)
        {
            if (spell == null) return "Empty";

            try
            {
                string name = SplitCamelCase(spell.Enum.ToString());
                return name;
            }
            catch
            {
                return "Unknown food";
            }
        }

        /// <summary>
        /// Reads a spell with tier info for shop display.
        /// </summary>
        public static string ReadSpellForShop(SpellModel spell)
        {
            if (spell == null) return "Empty";

            try
            {
                string name = SplitCamelCase(spell.Enum.ToString());
                int tier = spell.Tier;
                int price = 3;
                try { price = spell.Price; } catch { }
                return $"{name}, tier {tier}, {price} gold";
            }
            catch
            {
                return ReadSpell(spell);
            }
        }

        /// <summary>
        /// Reads the board status (gold, turn, tier, lives, wins/losses).
        /// Daily (BullyRush) also appends the moustache score.
        /// e.g. Arena → "Turn 3, 10 gold, tier 2, 5 lives, 2 wins, 0 losses"
        ///      Daily → "Turn 3, 10 gold, tier 2, 5 lives, 2 wins, 0 losses, 13 moustaches"
        /// </summary>
        public static string ReadBoardStatus(BoardModel board)
        {
            if (board == null) return "";

            try
            {
                var parts = new List<string>();

                try { parts.Add($"Turn {board.Turn}"); } catch { }
                try { parts.Add($"{board.Gold} gold"); } catch { }
                try
                {
                    int freeRolls = board.FreeRolls;
                    if (freeRolls > 0)
                        parts.Add($"{freeRolls} free rolls");
                }
                catch { }
                try { parts.Add($"tier {board.Tier}"); } catch { }
                try { parts.Add($"{board.Lives} of {board.LivesMax} lives"); } catch { }
                try { parts.Add($"{board.Victories} wins"); } catch { }
                try { parts.Add($"{board.Losses} losses"); } catch { }

                bool isBullyRush = false;
                try
                {
                    isBullyRush = Il2CppSpacewood.Unity.Memory.Mode ==
                        Il2CppSpacewood.Core.Enums.Mode.BullyRush;
                }
                catch { }
                if (isBullyRush)
                {
                    try
                    {
                        int moustaches = board.MoustachesCollected;
                        parts.Add(moustaches == 1 ? "1 moustache" : $"{moustaches} moustaches");
                    }
                    catch { }
                }

                return string.Join(", ", parts);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadBoardStatus error: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Reads just the gold status for the A key query.
        /// </summary>
        public static string ReadGoldStatus(BoardModel board)
        {
            if (board == null) return "Gold unknown";
            try
            {
                string result = $"{board.Gold} gold";
                int freeRolls = board.FreeRolls;
                if (freeRolls > 0)
                    result += $", {freeRolls} free rolls";
                return result;
            }
            catch { return "Gold unknown"; }
        }

        /// <summary>
        /// Reads just the lives status for the L key query.
        /// </summary>
        public static string ReadLivesStatus(BoardModel board)
        {
            if (board == null) return "Lives unknown";
            try
            {
                return $"{board.Lives} of {board.LivesMax} lives";
            }
            catch { return "Lives unknown"; }
        }

        /// <summary>
        /// Reads turn and tier for the N key query.
        /// </summary>
        public static string ReadTurnStatus(BoardModel board)
        {
            if (board == null) return "Turn unknown";
            try
            {
                return $"Turn {board.Turn}, tier {board.Tier}";
            }
            catch { return "Turn unknown"; }
        }

        /// <summary>
        /// Reads wins/losses for the W key query. In Daily mode (BullyRush) also
        /// appends the moustache score.
        /// </summary>
        public static string ReadWinsStatus(BoardModel board)
        {
            if (board == null) return "Wins unknown";
            try
            {
                string baseStr = $"{board.Victories} wins, {board.Losses} losses";
                bool isBullyRush = false;
                try
                {
                    isBullyRush = Il2CppSpacewood.Unity.Memory.Mode ==
                        Il2CppSpacewood.Core.Enums.Mode.BullyRush;
                }
                catch { }
                if (isBullyRush)
                {
                    int moustaches = board.MoustachesCollected;
                    string mLabel = moustaches == 1 ? "1 moustache" : $"{moustaches} moustaches";
                    return $"{baseStr}, {mLabel}";
                }
                return baseStr;
            }
            catch { return "Wins unknown"; }
        }

        /// <summary>
        /// Reads a minion with just name and stats, no cost/tier.
        /// Used for opponent team readout where cost is irrelevant.
        /// e.g. "Beaver, 3/2"
        /// </summary>
        public static string ReadMinionNameAndStats(MinionModel minion)
        {
            if (minion == null) return "Empty";
            try
            {
                string name = GetLocalizedName(minion);
                int attack = minion.Attack != null ? minion.Attack.Total : 0;
                int health = minion.Health != null ? minion.Health.Total : 0;
                return $"{name}, {attack}/{health}";
            }
            catch { return "Unknown pet"; }
        }

        /// <summary>
        /// Reads opponent data into a human-readable announcement.
        /// e.g. "PlayerName, The Brave Otter, 8 lives, Turtle Pack. Team: Beaver 3/2, Fish 2/3"
        /// </summary>
        public static string ReadOpponent(Il2CppSpacewood.Core.Models.UserVersusOpponent opponent)
        {
            if (opponent == null) return "No opponent data";

            try
            {
                var parts = new List<string>();

                // Name
                try
                {
                    string name = opponent.DisplayName;
                    if (!string.IsNullOrWhiteSpace(name))
                        parts.Add(name);
                }
                catch { }

                // Board description (adjective + noun)
                try
                {
                    string adj = opponent.BoardAdjective;
                    string noun = opponent.BoardNoun;
                    if (!string.IsNullOrWhiteSpace(adj) && !string.IsNullOrWhiteSpace(noun))
                        parts.Add($"The {adj} {noun}");
                    else if (!string.IsNullOrWhiteSpace(noun))
                        parts.Add(noun);
                }
                catch { }

                // Lives
                try
                {
                    parts.Add($"{opponent.Lives} lives");
                }
                catch { }

                // Pack
                try
                {
                    string packName = SplitCamelCase(opponent.Pack.ToString());
                    if (!string.IsNullOrWhiteSpace(packName))
                        parts.Add($"{packName} pack");
                }
                catch { }

                // Team pets
                try
                {
                    var minions = opponent.Minions;
                    if (minions != null && minions.Count > 0)
                    {
                        var petDescriptions = new List<string>();
                        for (int i = 0; i < minions.Count; i++)
                        {
                            try
                            {
                                var m = minions[i];
                                if (m != null)
                                    petDescriptions.Add(ReadMinionNameAndStats(m));
                            }
                            catch { }
                        }
                        if (petDescriptions.Count > 0)
                            parts.Add("Team: " + string.Join(", ", petDescriptions));
                    }
                    else
                    {
                        parts.Add("No team");
                    }
                }
                catch { }

                // Relics
                try
                {
                    var relics = opponent.Relics;
                    if (relics != null && relics.Count > 0)
                    {
                        var relicDescriptions = new List<string>();
                        for (int i = 0; i < relics.Count; i++)
                        {
                            try
                            {
                                var r = relics[i];
                                if (r != null)
                                {
                                    string relicName = SplitCamelCase(r.Enum.ToString()); // Relics don't have localized names yet
                                    relicDescriptions.Add(relicName);
                                }
                            }
                            catch { }
                        }
                        if (relicDescriptions.Count > 0)
                            parts.Add("Relics: " + string.Join(", ", relicDescriptions));
                    }
                }
                catch { }

                return parts.Count > 0 ? string.Join(". ", parts) : "Opponent data unavailable";
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadOpponent error: {ex.Message}");
                return "Could not read opponent info";
            }
        }

        /// <summary>
        /// Resolves a minion name from an ItemId by searching the board model.
        /// Returns "Unknown" if not found.
        /// </summary>
        public static string ResolveMinionName(Il2CppSpacewood.Core.Models.Item.ItemId id, BoardModel board)
        {
            if (board == null) return "Unknown";

            try
            {
                // Minions is a Grid<MinionModel>, access via .Items list
                var minions = board.Minions;
                if (minions?.Items != null)
                {
                    var items = minions.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var m = items[i];
                            if (m != null && m.Id == id)
                            {
                                return GetLocalizedName(m);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return "Unknown";
        }

        /// <summary>
        /// Reads a minion's ability descriptions.
        /// Uses the Il2Cpp AbilityAsset chain: MinionModel.Abilities â†’ AbilityEnumExtensions.ToAsset â†’ GetAbout
        /// </summary>
        public static string ReadMinionAbility(MinionModel minion)
        {
            if (minion == null) return "";

            try
            {
                var abilities = minion.Abilities;
                if (abilities == null || abilities.Count == 0) return "";

                var abilityParts = new List<string>();
                for (int i = 0; i < abilities.Count; i++)
                {
                    try
                    {
                        var abilityModel = abilities[i];
                        if (abilityModel == null) continue;

                        var abilityEnum = abilityModel.Enum;
                        var abilityAsset = AbilityEnumExtensions.ToAsset(abilityEnum);
                        if (abilityAsset == null) continue;

                        int level = 1;
                        try { level = abilityModel.Level; } catch { }
                        if (level < 1) level = 1;

                        string about = abilityAsset.GetAbout(level, false, minion);
                        if (!string.IsNullOrEmpty(about))
                        {
                            // Strip rich text tags like <color>, <b>, etc.
                            about = StripRichTextTags(about);
                            abilityParts.Add(about.Trim());
                        }
                    }
                    catch { }
                }

                if (abilityParts.Count == 0) return "";
                return string.Join(". ", abilityParts);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadMinionAbility error: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Reads a spell/food item with detailed description.
        /// Uses the Il2Cpp SpellAsset chain: SpellEnumExtensions.ToAsset â†’ GetName/GetAbility
        /// </summary>
        public static string ReadSpellDetailed(SpellModel spell)
        {
            if (spell == null) return "Empty";

            try
            {
                var parts = new List<string>();

                // Get SpellAsset for localized name and ability text
                string name = SplitCamelCase(spell.Enum.ToString());
                Il2CppSpacewood.Unity.SpellAsset spellAsset = null;
                try
                {
                    spellAsset = SpellEnumExtensions.ToAsset(spell.Enum);
                    if (spellAsset != null)
                    {
                        string localizedName = spellAsset.GetName();
                        if (!string.IsNullOrEmpty(localizedName))
                            name = localizedName;
                    }
                }
                catch { }
                parts.Add(name);

                // Tier
                try { parts.Add($"tier {spell.Tier}"); } catch { }

                // Cost
                try { parts.Add($"{spell.Price} gold"); } catch { }

                // Get ability/effect description from SpellAsset
                try
                {
                    if (spellAsset != null)
                    {
                        string abilityText = spellAsset.GetAbility(false);
                        if (!string.IsNullOrEmpty(abilityText))
                        {
                            abilityText = StripRichTextTags(abilityText);
                            parts.Add(abilityText.Trim());
                        }
                    }
                }
                catch { }

                // Perk ability (for food items that grant perks)
                try
                {
                    if (spellAsset != null)
                    {
                        var perkLib = UnityEngine.Object.FindObjectOfType<Il2CppSpacewood.Unity.PerkLibrary>();

                        string perkAbility = spellAsset.GetPerkAbility(false, perkLib);
                        if (!string.IsNullOrEmpty(perkAbility))
                        {
                            perkAbility = StripRichTextTags(perkAbility);
                            parts.Add($"perk effect: {perkAbility.Trim()}");
                        }

                        string perkFinePrint = spellAsset.GetPerkFinePrint(perkLib);
                        if (!string.IsNullOrEmpty(perkFinePrint))
                        {
                            perkFinePrint = StripRichTextTags(perkFinePrint);
                            parts.Add(perkFinePrint.Trim());
                        }
                    }
                }
                catch { }

                return string.Join(", ", parts);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ReadSpellDetailed error: {ex.Message}");
                return ReadSpell(spell);
            }
        }

        /// <summary>
        /// Strips Unity rich text tags (e.g., &lt;color&gt;, &lt;b&gt;, &lt;size&gt;) from a string.
        /// </summary>
        public static string StripRichTextTags(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return Regex.Replace(input, @"<[^>]+>", "");
        }

        /// <summary>
        /// Gets the localized display name for a minion using the game's own localization system.
        /// Falls back to SplitCamelCase of the enum name if localization fails.
        /// </summary>
        public static string GetLocalizedName(MinionModel minion)
        {
            if (minion == null) return "Unknown pet";
            try
            {
                string localized = MinionModelExtensions.GetNameLocalized(minion);
                if (!string.IsNullOrEmpty(localized))
                    return localized;
            }
            catch { }

            // Fallback to SplitCamelCase
            try
            {
                return SplitCamelCase(minion.Enum.ToString());
            }
            catch { return "Unknown pet"; }
        }

        /// <summary>
        /// Checks if a minion actually has a perk by reading from the MinionView visual hierarchy.
        /// Il2Cpp Nullable&lt;Perk&gt; on MinionModel is completely broken (always reads enum 0 = Coconut).
        /// Uses PerkFX active state + perk icon sprite name from the view as primary source.
        /// Falls back to model for non-Coconut perks only.
        /// </summary>
        public static bool HasRealPerk(MinionModel minion, out Perk perkValue,
            MinionView minionView = null)
        {
            perkValue = default;

            // Strategy 1: Use MinionModelExtensions.GetPerkTitleLocalized() â€” native C++ method
            // that returns the perk name as a string, completely bypassing broken Nullable<Perk>.
            if (minion != null)
            {
                try
                {
                    string perkTitle = MinionModelExtensions.GetPerkTitleLocalized(minion);
                    if (!string.IsNullOrEmpty(perkTitle))
                    {
                        // We have a perk name â€” try to match it to a Perk enum value
                        string titleLower = perkTitle.ToLowerInvariant();
                        foreach (var name in System.Enum.GetNames(typeof(Perk)))
                        {
                            if (titleLower == name.ToLowerInvariant() ||
                                titleLower.Replace(" ", "") == name.ToLowerInvariant())
                            {
                                perkValue = (Perk)System.Enum.Parse(typeof(Perk), name);
                                return true;
                            }
                        }
                        // Couldn't match to enum but DO have a perk title â€” store name for display
                        // Use Coconut as placeholder enum but the title is what matters for display
                        _lastPerkTitle = perkTitle;
                        perkValue = Perk.Coconut;
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        // Cached perk title from GetPerkTitleLocalized when enum match fails
        private static string _lastPerkTitle;

        /// <summary>
        /// Gets the perk display name for a minion, using the native GetPerkTitleLocalized first,
        /// then falling back to PerkLibrary lookup.
        /// </summary>
        public static string GetPerkDisplayForMinion(MinionModel minion, Perk perkValue)
        {
            // If we have a cached title from the last HasRealPerk call, use it
            if (!string.IsNullOrEmpty(_lastPerkTitle))
            {
                string title = _lastPerkTitle;
                _lastPerkTitle = null;
                return title;
            }
            return GetPerkDisplayName(perkValue);
        }

        /// <summary>
        /// Gets the localized display name for a perk using PerkLibrary.
        /// Falls back to SplitCamelCase of the enum name if lookup fails.
        /// </summary>
        public static string GetPerkDisplayName(Perk perk)
        {
            try
            {
                var perkLib = UnityEngine.Object.FindObjectOfType<PerkLibrary>();
                if (perkLib != null)
                {
                    string locName = perkLib.GetName(perk);
                    if (!string.IsNullOrEmpty(locName))
                        return locName;
                }
            }
            catch { }

            // Fallback
            return SplitCamelCase(perk.ToString());
        }

        /// <summary>
        /// Gets the localized display name for a nullable perk.
        /// Returns "perk" if the nullable has no value.
        /// </summary>
        public static string GetPerkDisplayNameNullable(Il2CppSystem.Nullable<Perk> perk)
        {
            try
            {
                if (perk != null && perk.HasValue)
                    return GetPerkDisplayName(perk.Value);
            }
            catch { }
            return "perk";
        }

        /// <summary>
        /// Converts "CamelCase" to "Camel Case".
        /// e.g. "MainMenuPage" -> "Main Menu Page"
        /// </summary>
        public static string SplitCamelCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return Regex.Replace(input, @"([a-z])([A-Z])", "$1 $2").Trim();
        }

        /// <summary>
        /// Gets the owner prefix for announcements.
        /// Returns "Your" for player, "Enemy" for opponent.
        /// </summary>
        public static string GetOwnerPrefix(int ownerValue)
        {
            // Owner enum: 0 = Player, 1 = Enemy (typically)
            return ownerValue == 0 ? "Your" : "Enemy";
        }

        /// <summary>
        /// Gets the XP needed to reach the next level from BoardConstants.LevelRequirements.
        /// Returns 0 if unable to determine.
        /// </summary>
        private static int GetExpForNextLevel(int currentLevel)
        {
            try
            {
                var requirements = Il2CppSpacewood.Core.Models.BoardConstants.LevelRequirements;
                if (requirements != null && currentLevel < requirements.Count)
                    return requirements[currentLevel];
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"GetExpForNextLevel error: {ex.Message}");
            }
            return 0;
        }
    }
}
