using System;
using System.Collections.Generic;
using MelonLoader;
using Il2CppSpacewood.Core.Models;
using Il2CppSpacewood.Unity;
using Il2CppSpacewood.Unity.Extensions;
using Il2CppSpacewood.Unity.Views;
using MinionView = Il2CppSpacewood.Unity.Views.MinionView;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// Builds ordered detail line lists for pets and food items,
    /// following the Hearthstone Battlegrounds accessibility reading order.
    /// Lines are navigated with Up/Down arrows in the shop.
    /// </summary>
    public static class DetailLineProvider
    {
        // Cached PerkLibrary reference (avoids expensive FindObjectOfType every call)
        private static PerkLibrary _cachedPerkLib;
        // Cached perk name -> (enum value, description) map for fast ability text scanning
        private static Dictionary<string, (Il2CppSpacewood.Core.Enums.Perk perk, string description)> _perkNameCache;

        private static PerkLibrary GetPerkLibrary()
        {
            if (_cachedPerkLib == null)
                _cachedPerkLib = UnityEngine.Object.FindObjectOfType<PerkLibrary>();
            return _cachedPerkLib;
        }

        private static void EnsurePerkNameCache()
        {
            if (_perkNameCache != null) return;
            _perkNameCache = new Dictionary<string, (Il2CppSpacewood.Core.Enums.Perk, string)>();
            var perkLib = GetPerkLibrary();
            if (perkLib == null) return;
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var perk = (Il2CppSpacewood.Core.Enums.Perk)i;
                    string perkName = PetStatsReader.GetPerkDisplayName(perk);
                    if (string.IsNullOrEmpty(perkName)) continue;
                    string key = perkName.ToLowerInvariant();
                    if (!_perkNameCache.ContainsKey(key))
                    {
                        string desc = perkLib.GetAbility(perk, false);
                        if (!string.IsNullOrEmpty(desc))
                            desc = PetStatsReader.StripRichTextTags(desc).Trim();
                        _perkNameCache[key] = (perk, desc ?? "");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Invalidates the PerkLibrary and perk name caches.
        /// Call when the shop refreshes (start of turn, reroll) to avoid stale data.
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedPerkLib = null;
            _perkNameCache = null;
        }

        /// <summary>
        /// Public wrapper for EnsurePerkNameCache, called on shop entry for eager init.
        /// </summary>
        public static void EnsurePerkNameCachePublic()
        {
            try { EnsurePerkNameCache(); } catch { }
        }

        /// <summary>
        /// Builds detail lines for a pet in the shop.
        /// Line 1: Name (with "Level X" prefix if level > 1)
        /// Line 2: Attack and Health + perk keyword
        /// Line 3: Perk description (if perk present)
        /// Line 4: Ability description
        /// Line 5: Tribe
        /// Line 6: Tier
        /// Line 7: Mana (if > 0)
        /// Line 8: Cost
        /// Line 9: "Pet"
        /// </summary>
        public static List<string> BuildMinionShopLines(MinionModel minion, MinionView minionView = null)
        {
            var lines = new List<string>();
            if (minion == null) { lines.Add("Empty"); return lines; }

            // Line 1: Name
            try
            {
                string name = PetStatsReader.GetLocalizedName(minion);
                try
                {
                    int level = minion.Level;
                    if (level > 1) name = $"Level {level} {name}";
                }
                catch { }
                lines.Add(name);
            }
            catch { lines.Add("Unknown pet"); }

            // Line 2: Attack/Health + perk keyword (uses native GetPerkTitleLocalized)
            try
            {
                int attack = minion.Attack != null ? minion.Attack.Total : 0;
                int health = minion.Health != null ? minion.Health.Total : 0;
                string statsLine = $"{attack} attack, {health} health";
                try
                {
                    string perkTitle = MinionModelExtensions.GetPerkTitleLocalized(minion);
                    if (!string.IsNullOrEmpty(perkTitle))
                        statsLine += $", {perkTitle}";
                }
                catch { }
                lines.Add(statsLine);
            }
            catch { lines.Add("Stats unknown"); }

            // Line 3: Perk description (uses native GetPerkAbilityLocalized)
            try
            {
                string perkAbility = MinionModelExtensions.GetPerkAbilityLocalized(minion);
                if (!string.IsNullOrEmpty(perkAbility))
                {
                    perkAbility = PetStatsReader.StripRichTextTags(perkAbility).Trim();
                    lines.Add($"Perk: {perkAbility}");
                }
            }
            catch { }

            // Line 4: Ability description
            string abilityText = null;
            try
            {
                abilityText = PetStatsReader.ReadMinionAbility(minion);
                lines.Add(string.IsNullOrEmpty(abilityText) ? "No ability" : abilityText);
            }
            catch { lines.Add("No ability"); }

            // Line 4b: Perk descriptions referenced in ability text
            AppendReferencedPerkDescriptions(lines, abilityText, null);

            // Line 5: Tier
            try { lines.Add($"Tier {minion.Tier}"); }
            catch { lines.Add("Tier unknown"); }

            // Line 6: Mana (if > 0)
            try
            {
                int mana = minion.Mana;
                if (mana > 0)
                    lines.Add($"{mana} mana");
            }
            catch { }

            // Line 8: Cost
            try { lines.Add($"{minion.Price} gold"); }
            catch { }

            // Line 9: Type
            lines.Add("Pet");

            return lines;
        }

        /// <summary>
        /// Builds detail lines for a pet on the team.
        /// Same structure as shop but with sell value + experience instead of cost.
        /// </summary>
        public static List<string> BuildMinionTeamLines(MinionModel minion, MinionView minionView = null)
        {
            var lines = new List<string>();
            if (minion == null) { lines.Add("Empty slot"); return lines; }

            // Line 1: Name with level
            try
            {
                string name = PetStatsReader.GetLocalizedName(minion);
                try
                {
                    int level = minion.Level;
                    if (level > 1) name = $"Level {level} {name}";
                }
                catch { }
                lines.Add(name);
            }
            catch { lines.Add("Unknown pet"); }

            // Line 2: Attack/Health + perk (uses native GetPerkTitleLocalized)
            try
            {
                int attack = minion.Attack != null ? minion.Attack.Total : 0;
                int health = minion.Health != null ? minion.Health.Total : 0;
                string statsLine = $"{attack} attack, {health} health";
                try
                {
                    string perkTitle = MinionModelExtensions.GetPerkTitleLocalized(minion);
                    if (!string.IsNullOrEmpty(perkTitle))
                        statsLine += $", {perkTitle}";
                }
                catch { }
                lines.Add(statsLine);
            }
            catch { lines.Add("Stats unknown"); }

            // Line 3: Perk description (uses native GetPerkAbilityLocalized)
            try
            {
                string perkAbility = MinionModelExtensions.GetPerkAbilityLocalized(minion);
                if (!string.IsNullOrEmpty(perkAbility))
                {
                    perkAbility = PetStatsReader.StripRichTextTags(perkAbility).Trim();
                    lines.Add($"Perk: {perkAbility}");
                }
            }
            catch { }

            // Line 4: Ability
            string abilityText = null;
            try
            {
                abilityText = PetStatsReader.ReadMinionAbility(minion);
                lines.Add(string.IsNullOrEmpty(abilityText) ? "No ability" : abilityText);
            }
            catch { lines.Add("No ability"); }

            // Line 4b: Perk descriptions referenced in ability text
            Il2CppSystem.Nullable<Il2CppSpacewood.Core.Enums.Perk> teamRealPerk = null;
            try { /* pass null â€” perk exclusion not needed with native title approach */ }
            catch { }
            AppendReferencedPerkDescriptions(lines, abilityText, teamRealPerk);

            // Line 5: Tier
            try { lines.Add($"Tier {minion.Tier}"); }
            catch { lines.Add("Tier unknown"); }

            // Line 6: Level + Experience (with max)
            try
            {
                int level = minion.Level;
                int exp = minion.Exp;
                if (level < 3)
                {
                    int expNeeded = GetExpForNextLevel(level);
                    if (expNeeded > 0)
                        lines.Add($"Level {level}, {exp} of {expNeeded} experience");
                    else
                        lines.Add($"Level {level}, {exp} experience");
                }
                else
                    lines.Add($"Level {level}");
            }
            catch { }

            // Line 8: Mana (if > 0)
            try
            {
                int mana = minion.Mana;
                if (mana > 0)
                    lines.Add($"{mana} mana");
            }
            catch { }

            // Line 9: Sell value
            try
            {
                var sv = minion.SellValue;
                if (sv.HasValue)
                    lines.Add($"Sell for {sv.Value} gold");
            }
            catch { }

            // Line 10: Type
            lines.Add("Pet");

            return lines;
        }

        /// <summary>
        /// Builds detail lines for a food item in the shop.
        /// Line 1: Name
        /// Line 2: Description (includes perk info if food grants a perk)
        /// Line 3: Tier
        /// Line 4: Cost
        /// </summary>
        public static List<string> BuildSpellShopLines(SpellModel spell)
        {
            var lines = new List<string>();
            if (spell == null) { lines.Add("Empty"); return lines; }

            // Line 1: Name
            string name = PetStatsReader.SplitCamelCase(spell.Enum.ToString());
            SpellAsset spellAsset = null;
            try
            {
                spellAsset = SpellEnumExtensions.ToAsset(spell.Enum);
                if (spellAsset != null)
                {
                    string localized = spellAsset.GetName();
                    if (!string.IsNullOrEmpty(localized)) name = localized;
                }
            }
            catch { }
            lines.Add(name);

            // Line 2: Description
            try
            {
                if (spellAsset != null)
                {
                    string ability = spellAsset.GetAbility(false);
                    if (!string.IsNullOrEmpty(ability))
                        lines.Add(PetStatsReader.StripRichTextTags(ability).Trim());
                    else
                        lines.Add("No description");
                }
                else
                {
                    lines.Add("No description");
                }
            }
            catch { lines.Add("No description"); }

            // Line 3: Perk granted (if food gives a perk, describe what it does)
            try
            {
                if (spellAsset != null)
                {
                    var perkLib = GetPerkLibrary();
                    string perkAbility = spellAsset.GetPerkAbility(false, perkLib);
                    if (!string.IsNullOrEmpty(perkAbility))
                    {
                        perkAbility = PetStatsReader.StripRichTextTags(perkAbility).Trim();
                        lines.Add($"Gives perk: {perkAbility}");
                    }
                }
            }
            catch { }

            // Line 4: Tier
            try { lines.Add($"Tier {spell.Tier}"); }
            catch { lines.Add("Tier unknown"); }

            // Line 5: Cost
            try { lines.Add($"{spell.Price} gold"); }
            catch { }

            return lines;
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

        /// <summary>
        /// Gets a perk's description text from PerkLibrary.
        /// </summary>
        private static string GetPerkDescription(Il2CppSpacewood.Core.Enums.Perk perk)
        {
            try
            {
                var perkLib = GetPerkLibrary();
                if (perkLib != null)
                {
                    string desc = perkLib.GetAbility(perk, false);
                    if (!string.IsNullOrEmpty(desc))
                        return PetStatsReader.StripRichTextTags(desc).Trim();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Scans ability text for perk name references and appends description lines
        /// for each referenced perk (e.g., "Peanut perk: Deal 5 damage to attacker").
        /// Skips the pet's own current perk since that's already shown in Line 3.
        /// </summary>
        private static void AppendReferencedPerkDescriptions(
            List<string> lines,
            string abilityText,
            Il2CppSystem.Nullable<Il2CppSpacewood.Core.Enums.Perk> currentPerk)
        {
            if (string.IsNullOrEmpty(abilityText)) return;

            try
            {
                EnsurePerkNameCache();
                if (_perkNameCache == null || _perkNameCache.Count == 0) return;

                string abilityLower = abilityText.ToLowerInvariant();

                // Get the current perk's display name to skip it (already shown as Line 3)
                string currentPerkKey = null;
                if (currentPerk != null && currentPerk.HasValue)
                {
                    try { currentPerkKey = PetStatsReader.GetPerkDisplayName(currentPerk.Value)?.ToLowerInvariant(); }
                    catch { }
                }

                foreach (var kvp in _perkNameCache)
                {
                    // Skip the pet's own current perk
                    if (currentPerkKey != null && kvp.Key == currentPerkKey)
                        continue;

                    // Check if ability text mentions this perk name
                    if (abilityLower.Contains(kvp.Key))
                    {
                        string desc = kvp.Value.description;
                        if (!string.IsNullOrEmpty(desc))
                        {
                            // Reconstruct display name with original casing from GetPerkDisplayName
                            string displayName = PetStatsReader.GetPerkDisplayName(kvp.Value.perk);
                            lines.Add($"{displayName} perk: {desc}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"AppendReferencedPerkDescriptions error: {ex.Message}");
            }
        }
        /// <summary>
        /// Lines for an opponent's pet on the scoreboard. Line 0 is the summary read on focus
        /// ("Level 2 Ant, 3 attack, 4 health, Honey"); then perk, ability, the perks the ability
        /// names, tier, experience and mana, as for the player's own team.
        /// </summary>
        public static List<string> BuildOpponentMinionLines(MinionModel minion)
        {
            var lines = new List<string>();
            if (minion == null) { lines.Add("Empty"); return lines; }

            var summary = new List<string>();
            try
            {
                string name = PetStatsReader.GetLocalizedName(minion);
                try
                {
                    int level = minion.Level;
                    if (level > 1) name = $"Level {level} {name}";
                }
                catch { }
                summary.Add(name);
            }
            catch { summary.Add("Unknown pet"); }
            try
            {
                int attack = minion.Attack != null ? minion.Attack.Total : 0;
                int health = minion.Health != null ? minion.Health.Total : 0;
                summary.Add($"{attack} attack, {health} health");
            }
            catch { }
            try
            {
                string perkTitle = MinionModelExtensions.GetPerkTitleLocalized(minion);
                if (!string.IsNullOrEmpty(perkTitle)) summary.Add(perkTitle);
            }
            catch { }
            lines.Add(string.Join(", ", summary));

            try
            {
                string perkAbility = MinionModelExtensions.GetPerkAbilityLocalized(minion);
                if (!string.IsNullOrEmpty(perkAbility))
                    lines.Add($"Perk: {PetStatsReader.StripRichTextTags(perkAbility).Trim()}");
            }
            catch { }

            string abilityText = null;
            try
            {
                abilityText = PetStatsReader.ReadMinionAbility(minion);
                lines.Add(string.IsNullOrEmpty(abilityText) ? "No ability" : abilityText);
            }
            catch { lines.Add("No ability"); }
            AppendReferencedPerkDescriptions(lines, abilityText, null);

            try { lines.Add($"Tier {minion.Tier}"); }
            catch { }

            try
            {
                int level = minion.Level;
                int exp = minion.Exp;
                int expNeeded = level < 3 ? GetExpForNextLevel(level) : 0;
                lines.Add(expNeeded > 0 ? $"Level {level}, {exp} of {expNeeded} experience" : $"Level {level}");
            }
            catch { }

            try
            {
                int mana = minion.Mana;
                if (mana > 0) lines.Add($"{mana} mana");
            }
            catch { }

            return lines;
        }
    }
}
