using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using Il2CppBoardEvents;
using Il2CppBoardEvents.Interfaces;
using Il2CppSpacewood.Core.Models;
using Il2CppSpacewood.Core.Models.Item;
using Il2CppSpacewood.Core.Enums;
using Il2CppSpacewood.Core.Models.BoardResolver.BoardEvents.Wrappers;
using Il2CppSpacewood.Unity.Extensions;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// Converts shop-phase board events (from BoardSystem.Add) into screen reader announcements.
    /// Events arrive in the same Done&lt;T&gt;/Try&lt;T&gt;/CastEffect wrapper pattern as battle events.
    /// Uses EventUnwrapper to peel layers and access the actual BuffMinion, SellMinion, etc.
    /// </summary>
    public static class ShopNarrator
    {
        // Announcement queue with pacing to avoid overwhelming the screen reader
        private static Queue<string> _queue = new Queue<string>();
        private static float _lastAnnouncementTime = 0f;
        private const float MIN_ANNOUNCEMENT_INTERVAL = 0.24f;

        // Last announcement for re-read
        private static string _lastShopAnnouncement = "";

        // Debounce: avoid announcing same event repeatedly within a short window
        private static string _lastMessage = "";
        private static float _lastMessageTime = 0f;
        private const float DEBOUNCE_INTERVAL = 0.1f;

        // Extended dedup: track recent messages to catch duplicates from double-fired game events
        // (e.g. game fires two SellMinion events, each triggering identical ability chains ~0.75s apart)
        private static Dictionary<string, float> _recentMessages = new Dictionary<string, float>();
        private const float DEDUP_WINDOW = 1.5f;

        // Name cache: maps ItemId.ToString() -> pet name
        // Populated from board model when first needed each shop phase.
        // Needed because pets are removed from the board before Done events fire (sell, stack source).
        private static Dictionary<string, string> _nameCache = new Dictionary<string, string>();
        private static bool _nameCacheInitialized = false;

        /// <summary>
        /// Stores the last food name used in SpellFocus mode.
        /// Set by ShopNavigationManager when entering SpellFocus, consumed by GiveMinionPerk narration.
        /// This bypasses broken Il2Cpp Nullable&lt;SpellEnum&gt; which always reads as enum value 0.
        /// </summary>
        public static string LastAppliedFoodName { get; set; }

        /// <summary>
        /// Called from OnUpdate to process the announcement queue with pacing.
        /// </summary>
        public static void ProcessQueue()
        {
            if (_queue.Count == 0) return;
            if (Time.time - _lastAnnouncementTime < MIN_ANNOUNCEMENT_INTERVAL) return;

            string message = _queue.Dequeue();
            if (!string.IsNullOrEmpty(message))
            {
                TolkSpeech.Speak(message, false);
                _lastShopAnnouncement = message;
                _lastAnnouncementTime = Time.time;
                MelonLogger.Msg($"[Shop] {message}");
            }
        }

        /// <summary>
        /// Returns the last shop announcement (for re-read).
        /// </summary>
        public static string GetLastAnnouncement()
        {
            return _lastShopAnnouncement;
        }

        /// <summary>
        /// Main entry point: narrate a board event during shop phase.
        /// Called from the Harmony postfix on BoardSystem.Add(IBoardEvent).
        /// </summary>
        public static void NarrateShopEvent(IBoardEvent boardEvent)
        {
            if (boardEvent == null) return;

            try
            {
                // Use EventUnwrapper to peel containers and dispatch
                EventUnwrapper.ProcessEvent(boardEvent, HandleUnwrappedEvent);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"NarrateShopEvent error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler called by EventUnwrapper for each unwrapped event.
        /// </summary>
        private static void HandleUnwrappedEvent(IBoardEvent wrapper, string innerTypeName, string source)
        {
            try
            {
                // Only announce player-side events
                // Owner enum: World=0, Player=1, Opponent=2
                try
                {
                    int owner = (int)wrapper.Owner;
                    if (owner == 2) return; // Skip opponent events
                }
                catch { }

                // Only process Done-wrapped events (source="Done")
                // Direct events are internal lifecycle markers that duplicate the Done events.
                // Exception: RollShop events may arrive as Do/Try wrappers for non-keybind rerolls
                // (pet ability free rolls, UI clicks) â€” allow them through regardless of wrapper type.
                if (source != "Done" && innerTypeName != "RollShop")
                    return;

                string message = null;

                switch (innerTypeName)
                {
                    case "BuffMinion":
                        message = NarrateBuff(wrapper);
                        break;
                    case "SellMinion":
                        return; // Handled by ShopNavigationManager directly
                    case "SummonMinion":
                        message = NarrateSummon(wrapper);
                        break;
                    case "PlayedSpellOn":
                        message = NarratePlayedSpellOn(wrapper);
                        break;
                    case "GiveMinionPerk":
                        message = NarrateGivePerk(wrapper);
                        break;
                    case "LoseMinionPerk":
                        message = NarrateLosePerk(wrapper);
                        break;
                    case "MarkMinionDead":
                        message = NarrateMarkDead(wrapper);
                        break;
                    case "AbilityActivate":
                        message = NarrateAbility(wrapper);
                        break;
                    case "DestroyMinion":
                        message = NarrateDestroy(wrapper);
                        break;
                    case "DamageMinion":
                        message = NarrateDamage(wrapper);
                        break;
                    case "StackMinion":
                        message = NarrateStack(wrapper);
                        break;
                    case "GiveMinionExp":
                        message = NarrateGiveExp(wrapper);
                        break;
                    case "UpgradeTier":
                        message = NarrateUpgradeTier();
                        break;
                    case "ReorderMinion":
                        message = NarrateReorder(wrapper);
                        break;
                    case "RollShop":
                        message = NarrateRollShop(wrapper);
                        break;
                    case "ChangeMinionMana":
                        message = NarrateChangeMana(wrapper);
                        break;
                    case "SpendMinionMana":
                        message = NarrateSpendMana(wrapper);
                        break;

                    // Skip lifecycle/internal events even if wrapped in Done
                    case "SpendGold":
                    case "PlaySpell":
                    case "StartTurn":
                    case "EndTurn":
                    case "AddShopMinions":
                    case "MoveMinion":
                    case "MinionTrade":
                    case "GainGold":
                    case "Escape":
                    case "AbilityTriggered":
                    case "AddSellValue":
                        return;

                    default:
                        MelonLogger.Msg($"[ShopNarrator] Unhandled Done: {innerTypeName}");
                        return;
                }

                if (!string.IsNullOrEmpty(message))
                {
                    // Debounce: skip exact duplicate messages within short window
                    if (message == _lastMessage && (Time.time - _lastMessageTime) < DEBOUNCE_INTERVAL)
                        return;

                    // Extended dedup: game sometimes fires duplicate event chains
                    // (e.g. two SellMinion events each trigger identical ability/buff chains)
                    float now = Time.time;
                    if (_recentMessages.TryGetValue(message, out float lastTime) && (now - lastTime) < DEDUP_WINDOW)
                        return;

                    // Prune old entries periodically
                    if (_recentMessages.Count > 20)
                    {
                        var stale = new List<string>();
                        foreach (var kv in _recentMessages)
                            if (now - kv.Value > DEDUP_WINDOW) stale.Add(kv.Key);
                        foreach (var key in stale) _recentMessages.Remove(key);
                    }

                    _lastMessage = message;
                    _lastMessageTime = now;
                    _recentMessages[message] = now;

                    _queue.Enqueue(message);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"ShopNarrator.HandleUnwrappedEvent error: {ex.Message}");
            }
        }

        // --- Name cache ---

        /// <summary>
        /// Populate the name cache from the current board model.
        /// Called lazily on first name resolution each shop phase.
        /// </summary>
        private static void EnsureNameCache()
        {
            if (_nameCacheInitialized) return;
            _nameCacheInitialized = true;

            try
            {
                var hangar = GameplayPhaseDetector.GetHangarMain();
                var board = hangar?.Overlay?.BoardModel;
                if (board?.Minions?.Items != null)
                {
                    var items = board.Minions.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var m = items[i];
                            if (m != null)
                            {
                                string key = m.Id.ToString();
                                string name = PetStatsReader.GetLocalizedName(m);
                                if (!string.IsNullOrEmpty(name) && name != "Unknown pet")
                                    _nameCache[key] = name;
                            }
                        }
                        catch { }
                    }
                    MelonLogger.Msg($"[ShopNarrator] Name cache populated with {_nameCache.Count} entries");
                }
            }
            catch { }
        }

        /// <summary>
        /// Add a name to the cache (called when we see SummonMinion events).
        /// </summary>
        private static void CacheName(ItemId id, string name)
        {
            if (!string.IsNullOrEmpty(name) && name != "pet" && name != "Unknown")
            {
                _nameCache[id.ToString()] = name;
            }
        }

        // --- Individual event narrators ---

        private static string NarrateBuff(IBoardEvent wrapper)
        {
            try
            {
                var buff = EventUnwrapper.ExtractDone<BuffMinion>(wrapper);
                if (buff == null) return null;

                string targetName = ResolveName(buff.TargetId);

                int attack = buff.ResultAttackGained;
                int health = buff.ResultHealthGained;

                if (attack == 0 && health == 0)
                {
                    try
                    {
                        attack = buff.Attack;
                        health = buff.Health;
                    }
                    catch { }
                }

                if (attack == 0 && health == 0) return null;

                string buffText;
                if (attack != 0 && health != 0)
                    buffText = $"+{attack}/+{health}";
                else if (attack != 0)
                    buffText = $"+{attack} attack";
                else
                    buffText = $"+{health} health";

                return $"{targetName} gained {buffText}";
            }
            catch { return null; }
        }

        private static string NarrateSell(IBoardEvent wrapper)
        {
            try
            {
                var sell = EventUnwrapper.ExtractDone<SellMinion>(wrapper);
                if (sell == null) return null;

                string name = ResolveName(sell.MinionId);
                return $"Sold {name}";
            }
            catch { return null; }
        }

        private static string NarrateSummon(IBoardEvent wrapper)
        {
            try
            {
                var summon = EventUnwrapper.ExtractDone<SummonMinion>(wrapper);
                if (summon == null) return null;

                string minionName = "pet";
                try
                {
                    var minion = summon.Minion;
                    if (minion != null)
                    {
                        minionName = PetStatsReader.GetLocalizedName(minion);
                        // Cache the name for later events (sell, stack, etc.)
                        try { CacheName(minion.Id, minionName); } catch { }
                    }
                }
                catch { }

                return $"{minionName} summoned";
            }
            catch { return null; }
        }

        private static string NarratePlayedSpellOn(IBoardEvent wrapper)
        {
            try
            {
                var played = EventUnwrapper.ExtractDone<PlayedSpellOn>(wrapper);
                if (played == null) return null;

                string spellName = "food";
                try
                {
                    var spell = played.Spell;
                    if (spell != null)
                        spellName = PetStatsReader.ReadSpell(spell);
                }
                catch { }

                string targetName = "pet";
                try
                {
                    targetName = ResolveName(played.TargetId);
                }
                catch { }

                return $"Applied {spellName} to {targetName}";
            }
            catch { return null; }
        }

        private static string NarrateGivePerk(IBoardEvent wrapper)
        {
            try
            {
                var givePerk = EventUnwrapper.ExtractDone<GiveMinionPerk>(wrapper);
                if (givePerk == null) return null;

                string targetName = ResolveName(givePerk.TargetId);
                string perkName = ReadPerkName(givePerk);

                // "a perk" means we couldn't identify it (duplicate event) â€” suppress
                if (perkName == "a perk") return null;

                return $"{targetName} gained {perkName} perk";
            }
            catch { return null; }
        }

        private static string NarrateLosePerk(IBoardEvent wrapper)
        {
            try
            {
                var losePerk = EventUnwrapper.ExtractDone<LoseMinionPerk>(wrapper);
                if (losePerk == null) return null;

                string targetName = ResolveName(losePerk.Target);
                string perkName = ReadPerkNullable(losePerk.Perk);

                return $"{targetName} lost {perkName}";
            }
            catch { return null; }
        }

        private static string NarrateMarkDead(IBoardEvent wrapper)
        {
            try
            {
                var markDead = EventUnwrapper.ExtractDone<MarkMinionDead>(wrapper);
                if (markDead == null) return null;

                string targetName = ResolveName(markDead.TargetId);
                return $"{targetName} fainted";
            }
            catch { return null; }
        }

        private static string NarrateAbility(IBoardEvent wrapper)
        {
            try
            {
                var ability = EventUnwrapper.ExtractDone<AbilityActivate>(wrapper);
                if (ability == null) return null;

                string minionName = ResolveName(ability.MinionId);
                string abilityName = "";
                try
                {
                    abilityName = PetStatsReader.SplitCamelCase(ability.Ability.ToString());
                }
                catch { }

                if (!string.IsNullOrEmpty(abilityName))
                    return $"{minionName}'s {abilityName} activated";
                return $"{minionName}'s ability activated";
            }
            catch { return null; }
        }

        private static string NarrateDestroy(IBoardEvent wrapper)
        {
            try
            {
                var destroy = EventUnwrapper.ExtractDone<DestroyMinion>(wrapper);
                if (destroy == null) return null;

                string targetName = ResolveName(destroy.TargetId);
                return $"{targetName} destroyed";
            }
            catch { return null; }
        }

        private static string NarrateDamage(IBoardEvent wrapper)
        {
            try
            {
                var damage = EventUnwrapper.ExtractDone<DamageMinion>(wrapper);
                if (damage == null) return null;

                string targetName = ResolveName(damage.TargetId);
                int amount = damage.Amount;

                if (amount > 0)
                    return $"{targetName} took {amount} damage";
                return $"{targetName} took damage";
            }
            catch { return null; }
        }

        private static string NarrateStack(IBoardEvent wrapper)
        {
            try
            {
                var stack = EventUnwrapper.ExtractDone<StackMinion>(wrapper);
                if (stack == null) return null;

                // The target pet is the one that remains; both source and target
                // are the same pet type (you can only combine identical pets).
                // The source may already be removed from the board, so just use target name.
                string targetName = ResolveName(stack.TargetMinionId);

                return $"{targetName} combined";
            }
            catch { return null; }
        }

        private static string NarrateGiveExp(IBoardEvent wrapper)
        {
            try
            {
                var giveExp = EventUnwrapper.ExtractDone<GiveMinionExp>(wrapper);
                if (giveExp == null) return null;

                string targetName = ResolveName(giveExp.TargetId);
                int amount = giveExp.Amount;

                // Check if this caused a level up
                try
                {
                    var result = giveExp.Result;
                    if (result != null && result.LevelsGained > 0)
                    {
                        int newLevel = result.NewLevel;
                        string msg = $"{targetName} leveled up to level {newLevel}";

                        // Announce stat improvements from leveling up
                        try
                        {
                            var parts = new System.Collections.Generic.List<string>();
                            int atkGain = result.AttackGained;
                            int hpGain = result.HealthGained;
                            if (atkGain > 0) parts.Add($"+{atkGain} attack");
                            if (hpGain > 0) parts.Add($"+{hpGain} health");

                            // Show new total stats
                            try
                            {
                                var newAtk = result.NewAttack;
                                var newHp = result.NewHealth;
                                if (newAtk != null && newHp != null)
                                {
                                    parts.Add($"now {newAtk.Total} attack, {newHp.Total} health");
                                }
                            }
                            catch { }

                            if (parts.Count > 0)
                                msg += ". " + string.Join(", ", parts);
                        }
                        catch { }

                        return msg;
                    }
                }
                catch { }

                // No level up â€” just experience gain
                if (amount > 0)
                {
                    string msg = $"{targetName} gained {amount} experience";

                    // Still announce stat changes even without level up (some exp grants give stats)
                    try
                    {
                        var result = giveExp.Result;
                        if (result != null)
                        {
                            int atkGain = result.AttackGained;
                            int hpGain = result.HealthGained;
                            if (atkGain > 0 || hpGain > 0)
                            {
                                var parts = new System.Collections.Generic.List<string>();
                                if (atkGain > 0) parts.Add($"+{atkGain} attack");
                                if (hpGain > 0) parts.Add($"+{hpGain} health");
                                msg += ". " + string.Join(", ", parts);
                            }
                        }
                    }
                    catch { }

                    return msg;
                }
                return $"{targetName} gained experience";
            }
            catch { return null; }
        }

        private static string NarrateUpgradeTier()
        {
            try
            {
                var hangar = GameplayPhaseDetector.GetHangarMain();
                var board = hangar?.Overlay?.BoardModel;
                if (board != null)
                {
                    int tier = board.Tier;
                    return $"Shop upgraded to tier {tier}";
                }
                return "Shop tier upgraded";
            }
            catch { return "Shop tier upgraded"; }
        }

        private static string NarrateReorder(IBoardEvent wrapper)
        {
            try
            {
                var reorder = EventUnwrapper.ExtractDone<ReorderMinion>(wrapper);
                if (reorder == null) return null;

                // Skip silent reorders (internal repositioning)
                try { if (reorder.Silent) return null; } catch { }

                string minionName = ResolveName(reorder.MinionId);

                // TargetPoint.x gives the slot position (0-indexed, left to right)
                int slot = 0;
                try { slot = reorder.TargetPoint.x + 1; } catch { }

                if (slot > 0)
                    return $"{minionName} moved to slot {slot}";
                return $"{minionName} repositioned";
            }
            catch { return null; }
        }

        private static string NarrateRollShop(IBoardEvent wrapper)
        {
            try
            {
                bool free = false;
                bool startOfTurn = false;

                // Try Done wrapper first (keybind-triggered rerolls)
                var roll = EventUnwrapper.ExtractDone<Il2CppBoardEvents.RollShop>(wrapper);
                if (roll == null)
                {
                    // Fallback: direct cast for non-Done wrappers (Do/Try â€” pet ability free rolls, UI clicks)
                    try { roll = wrapper.TryCast<Il2CppBoardEvents.RollShop>(); } catch { }
                }

                if (roll != null)
                {
                    try { free = roll.Free; } catch { }
                    try { startOfTurn = roll.StartOfTurn; } catch { }
                }

                // Skip auto-roll at start of turn â€” not player-initiated
                if (startOfTurn) return null;
                if (free) return "Free reroll";
                return "Shop rerolled";
            }
            catch { return "Shop rerolled"; }
        }

        /// <summary>
        /// Read perk name from a GiveMinionPerk event.
        /// Tries Result.NewPerk first (authoritative), then SourceSpell (food name),
        /// then Perk field (but guards against Coconut default-value bug).
        /// </summary>
        private static string ReadPerkName(GiveMinionPerk givePerk)
        {
            try
            {
                // Strategy 1: Use cached food name from SpellFocus (set by ShopNavigationManager)
                // This is the most reliable source â€” it reads from SpellModel, not broken Nullable<enum>
                if (!string.IsNullOrEmpty(LastAppliedFoodName))
                {
                    string foodName = LastAppliedFoodName;
                    LastAppliedFoodName = null; // Consume it
                    return foodName;
                }

                // Strategy 2: Result.NewPerk â€” but guard against Coconut default (enum 0)
                var result = givePerk.Result;
                if (result != null)
                {
                    try
                    {
                        var newPerk = result.NewPerk;
                        if (newPerk != null && newPerk.HasValue && newPerk.Value != Perk.Coconut)
                        {
                            string name = PetStatsReader.GetPerkDisplayName(newPerk.Value);
                            if (!string.IsNullOrEmpty(name))
                                return name;
                        }
                    }
                    catch { }
                }

                // Strategy 3: Direct Perk field â€” skip Coconut (enum 0 = likely default)
                var perk = givePerk.Perk;
                if (perk != null && perk.HasValue && perk.Value != Perk.Coconut)
                    return PetStatsReader.GetPerkDisplayName(perk.Value);
            }
            catch { }
            return "a perk";
        }

        /// <summary>
        /// Read perk name from a Nullable&lt;Perk&gt; with safe fallback.
        /// </summary>
        private static string ReadPerkNullable(Il2CppSystem.Nullable<Il2CppSpacewood.Core.Enums.Perk> perk)
        {
            try
            {
                if (perk != null && perk.HasValue)
                    return PetStatsReader.GetPerkDisplayName(perk.Value);
            }
            catch { }
            return "perk";
        }

        /// <summary>
        /// Resolve a minion name from an ItemId.
        /// First checks the name cache (for pets that may have been removed from the board),
        /// then falls back to the live board model.
        /// </summary>
        private static string ResolveName(ItemId id)
        {
            try
            {
                EnsureNameCache();

                string key = id.ToString();

                // Check cache first (has names for pets that were sold/combined)
                if (_nameCache.TryGetValue(key, out string cached))
                    return cached;

                // Try live board model
                var hangar = GameplayPhaseDetector.GetHangarMain();
                var board = hangar?.Overlay?.BoardModel;
                if (board != null)
                {
                    string name = PetStatsReader.ResolveMinionName(id, board);
                    if (name != "Unknown")
                    {
                        _nameCache[key] = name; // Cache for future lookups
                        return name;
                    }
                }

                return "pet";
            }
            catch
            {
                return "pet";
            }
        }

        private static string NarrateChangeMana(IBoardEvent wrapper)
        {
            try
            {
                var changeMana = EventUnwrapper.ExtractDone<ChangeMinionMana>(wrapper);
                if (changeMana == null) return null;

                string targetName = ResolveName(changeMana.TargetId);
                int amount = 0;
                try { amount = changeMana.ResultAmount; } catch { }
                if (amount == 0)
                {
                    try { amount = changeMana.Amount; } catch { }
                }

                if (amount > 0)
                    return $"{targetName} gained {amount} mana";
                else if (amount < 0)
                    return $"{targetName} lost {System.Math.Abs(amount)} mana";
                return null;
            }
            catch { return null; }
        }

        private static string NarrateSpendMana(IBoardEvent wrapper)
        {
            try
            {
                var spendMana = EventUnwrapper.ExtractDone<SpendMinionMana>(wrapper);
                if (spendMana == null) return null;

                string targetName = ResolveName(spendMana.TargetId);
                return $"{targetName} mana ability activated";
            }
            catch { return null; }
        }

        /// <summary>
        /// Clear all state.
        /// </summary>
        public static void Reset()
        {
            _queue.Clear();
            _lastShopAnnouncement = "";
            _lastMessage = "";
            _recentMessages.Clear();
            _nameCache.Clear();
            _nameCacheInitialized = false;
        }
    }
}
