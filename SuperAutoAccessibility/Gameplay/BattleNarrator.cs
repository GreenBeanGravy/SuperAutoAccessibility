using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;
using Il2CppBoardEvents;
using Il2CppBoardEvents.Interfaces;
using Il2CppSpacewood.Core.Models;
using Il2CppSpacewood.Core.Models.Item;
using Il2CppSpacewood.Core.Models.BoardResolver.BoardEvents.Wrappers;
using Il2CppSpacewood.Core.Actions.Board;
using Il2CppSpacewood.Core.Enums;
using Il2CppSpacewood.Unity.Extensions;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// Converts battle events (IBoardEvent) into screen reader announcements.
    /// Events arrive wrapped in Done&lt;T&gt;/Try&lt;T&gt;/CastEffect containers;
    /// EventUnwrapper peels these layers to reach the actual DamageMinion, BuffMinion, etc.
    /// </summary>
    public static class BattleNarrator
    {
        // Announcement queue with priority and stereo pan (-1=player/left, 1=enemy/right, 0=center)
        private static Queue<(string message, int priority, float pan)> _queue = new Queue<(string, int, float)>();
        private static float _lastAnnouncementTime = 0f;
        private const float MIN_ANNOUNCEMENT_INTERVAL = 0.18f;

        // Last announcement for Space key re-read
        private static string _lastBattleAnnouncement = "";

        // Board model references for name resolution (player + opponent)
        private static BoardModel _playerBoard;
        private static BoardModel _opponentBoard;

        // Track minion lookup cache during battle.
        // Keyed by (side, unique) where side: 0=unknown/world, 1=player, 2=opponent.
        // Shop-phase IDs frequently collide between boards (both player and opponent
        // can have a pet at unique=7 in turn 1) — keying on unique alone caused the
        // second board's PopulateNameCacheFromBoard to overwrite the first's pets,
        // producing "Your Gecko fainted" when the player's Horse died.
        private static Dictionary<(int side, int unique), string> _minionNameCache =
            new Dictionary<(int, int), string>();

        // Track which side (1=Player, 2=Opponent) each battle-resolver ID belongs to.
        // Populated by CacheMinionTradeNames and SummonMinion events.
        // Used by ResolveOwnerPrefix since FindMinion on shop-phase boards can't match battle IDs.
        private static Dictionary<int, int> _minionOwnerCache = new Dictionary<int, int>();

        // Track fainted pets for battle-end survivor announcement (by name, since
        // battle-resolver IDs don't match shop-phase board IDs).
        // Uses count-based tracking to handle duplicate pet names correctly
        // (e.g., two Spiders â€” if one faints, only one should be excluded from survivors).
        private static HashSet<int> _playerFaintedIds = new HashSet<int>();
        private static HashSet<int> _opponentFaintedIds = new HashSet<int>();
        private static Dictionary<string, int> _playerFaintedNameCounts = new Dictionary<string, int>();
        private static Dictionary<string, int> _opponentFaintedNameCounts = new Dictionary<string, int>();

        // Positional name lists: ordered pet names from shop-phase boards.
        // Used by CacheMinionTradeNames to map battle-resolver IDs to names
        // since FindMinion on shop-phase boards can't match battle IDs.
        private static List<string> _playerPositionalNames = new List<string>();
        private static List<string> _opponentPositionalNames = new List<string>();
        private static int _playerPositionIndex = 0;
        private static int _opponentPositionIndex = 0;
        private static bool _firstTradeProcessed = false;

        /// <summary>
        /// Called when battle starts to set up board references.
        /// Accepts an optional board model cached from the shop phase.
        /// </summary>
        public static void OnBattleStart(BoardModel cachedBoard = null)
        {
            _queue.Clear();
            _minionNameCache.Clear();
            _lastBattleAnnouncement = "";
            _opponentBoard = null;
            _opponentBoardLoaded = false;
            _opponentBoardRetryCount = 0;
            _playerFaintedIds.Clear();
            _opponentFaintedIds.Clear();
            _playerFaintedNameCounts.Clear();
            _opponentFaintedNameCounts.Clear();
            _minionOwnerCache.Clear();
            _playerPositionalNames.Clear();
            _opponentPositionalNames.Clear();
            _playerPositionIndex = 0;
            _opponentPositionIndex = 0;
            _firstTradeProcessed = false;

            // Use the cached board from shop phase if available
            if (cachedBoard != null)
            {
                _playerBoard = cachedBoard;
            }
            else
            {
                // Fallback: try to get from HangarMain (may not exist during battle)
                try
                {
                    var hangar = GameplayPhaseDetector.GetHangarMain();
                    if (hangar?.Overlay?.BoardModel != null)
                    {
                        _playerBoard = hangar.Overlay.BoardModel;
                    }
                }
                catch { }
            }

            // Pre-populate name cache from player board (shop-phase IDs)
            PopulateNameCacheFromBoard(_playerBoard, side: 1);

            // Build positional name lists from shop-phase boards.
            // These are used by CacheMinionTradeNames to map battle-resolver IDs
            // to pet names, since FindMinion can't match battle IDs on shop boards.
            BuildPositionalNameList(_playerBoard, _playerPositionalNames, "Player");

            // Try to get opponent board from Memory.Battle
            TryLoadOpponentBoard();

            // Build opponent positional list if we have the board
            BuildPositionalNameList(_opponentBoard, _opponentPositionalNames, "Opponent");

            MelonLogger.Msg($"[BattleNarrator] Battle start: player=[{string.Join(",", _playerPositionalNames)}], opponent=[{string.Join(",", _opponentPositionalNames)}], cache={_minionNameCache.Count} entries");
        }

        /// <summary>
        /// Build an ordered list of pet names from a board's Items, preserving position order.
        /// Used for positional mapping when MinionTrade fires with battle-resolver IDs.
        /// </summary>
        private static void BuildPositionalNameList(BoardModel board, List<string> nameList, string label)
        {
            if (board?.Minions?.Items == null) return;
            try
            {
                for (int i = 0; i < board.Minions.Items.Count; i++)
                {
                    try
                    {
                        var m = board.Minions.Items[i];
                        if (m != null)
                        {
                            string name = PetStatsReader.GetLocalizedName(m);
                            if (!string.IsNullOrEmpty(name) && name != "Unknown pet")
                                nameList.Add(name);
                            else
                                nameList.Add("pet"); // Preserve position even when name is unresolved
                        }
                    }
                    catch { }
                }
                MelonLogger.Msg($"[BattleNarrator] {label} positional names: [{string.Join(", ", nameList)}]");
            }
            catch { }
        }

        /// <summary>
        /// Tries to load the opponent's board model.
        /// Uses Memory.Battle (BattleModel) as primary source, with Memory.DebugBoard as fallback.
        /// Called at battle start and retried during battle if initially null.
        /// </summary>
        private static bool _opponentBoardLoaded = false;
        private static int _opponentBoardRetryCount = 0;
        private const int MAX_OPPONENT_BOARD_RETRIES = 10;

        private static void TryLoadOpponentBoard()
        {
            if (_opponentBoardLoaded) return;
            if (_opponentBoardRetryCount >= MAX_OPPONENT_BOARD_RETRIES) return;
            _opponentBoardRetryCount++;

            // Strategy 1: Memory.Battle.OpponentBoard (primary â€” works in Arena and Versus)
            try
            {
                var battleModel = Il2CppSpacewood.Unity.Memory.Battle;
                if (battleModel != null)
                {
                    var oppBoard = battleModel.OpponentBoard;
                    if (oppBoard != null)
                    {
                        _opponentBoard = oppBoard;
                        _opponentBoardLoaded = true;
                        PopulateNameCacheFromBoard(oppBoard, side: 2);
                        MelonLogger.Msg($"[BattleNarrator] Opponent board loaded from Memory.Battle, cache has {_minionNameCache.Count} entries");
                        return;
                    }

                    // Also try to populate player board from BattleModel if not already set
                    if (_playerBoard == null)
                    {
                        var userBoard = battleModel.UserBoard;
                        if (userBoard != null)
                        {
                            _playerBoard = userBoard;
                            PopulateNameCacheFromBoard(userBoard, side: 1);
                            MelonLogger.Msg($"[BattleNarrator] Player board loaded from Memory.Battle");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"TryLoadOpponentBoard Battle error: {ex.Message}");
            }

            // Note: Memory.DebugBoard was tested and is always null at battle start,
            // so it's not a viable source for opponent board data.
        }

        /// <summary>
        /// Caches opponent pet names from external sources (e.g., VS screen).
        /// Called by GameplayPhaseDetector when opponent data is available.
        /// </summary>
        public static void CacheOpponentNames(List<string> names)
        {
            if (names == null) return;
            // We can't map these to IDs, but they're useful for logging
            MelonLogger.Msg($"[BattleNarrator] Opponent names from VS screen: {string.Join(", ", names)}");
        }

        /// <summary>
        /// Populate name cache from a board model's minions, scoped to `side`
        /// (1 = player, 2 = opponent). Side-keying is required because shop-phase
        /// unique IDs collide across boards (both start at 5, 6, 7…).
        /// </summary>
        private static void PopulateNameCacheFromBoard(BoardModel board, int side)
        {
            if (board == null) return;

            // Direct Items iteration (most reliable for initial board state)
            try
            {
                if (board.Minions?.Items != null)
                {
                    var items = board.Minions.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            var m = items[i];
                            if (m != null)
                            {
                                int key = m.Id.Unique;
                                string name = PetStatsReader.GetLocalizedName(m);
                                MelonLogger.Msg($"[BattleNarrator] Cache from Items: side={side}, unique={key}, name={name}, enum={m.Enum}");
                                if (!string.IsNullOrEmpty(name) && name != "Unknown pet")
                                    _minionNameCache[(side, key)] = name;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BattleNarrator] PopulateNameCache Items error: {ex.Message}");
            }

            // Also try FindMinions as secondary source
            try
            {
                var allMinions = BoardExtensions.FindMinions(board, false, false);
                if (allMinions != null)
                {
                    for (int i = 0; i < allMinions.Count; i++)
                    {
                        try
                        {
                            var m = allMinions[i];
                            if (m != null)
                            {
                                int key = m.Id.Unique;
                                if (!_minionNameCache.ContainsKey((side, key)))
                                {
                                    string name = PetStatsReader.GetLocalizedName(m);
                                    MelonLogger.Msg($"[BattleNarrator] Cache from FindMinions: side={side}, unique={key}, name={name}");
                                    if (!string.IsNullOrEmpty(name) && name != "Unknown pet")
                                        _minionNameCache[(side, key)] = name;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Lookup helper. When sideHint &gt; 0, looks up that side's entry directly —
        /// this is the authoritative path because shop-phase IDs and battle-resolver
        /// IDs share a namespace and the same `unique` can legitimately belong to both
        /// player and opponent at once (one's shop pet, the other's mid-battle pet).
        /// sideHint=0 means "caller doesn't know"; we then consult _minionOwnerCache
        /// and fall back to scanning both sides.
        /// </summary>
        private static string LookupCachedName(int unique, int sideHint = 0)
        {
            if (sideHint > 0)
            {
                if (_minionNameCache.TryGetValue((sideHint, unique), out var sideName))
                    return sideName;
                // No entry for the hinted side — return null rather than the wrong side's
                // name. Caller (ResolveName) will then try FindMinion on that side's board.
                return null;
            }

            if (_minionOwnerCache.TryGetValue(unique, out int side))
            {
                if (_minionNameCache.TryGetValue((side, unique), out var name))
                    return name;
            }
            if (_minionNameCache.TryGetValue((1, unique), out var playerName))
                return playerName;
            if (_minionNameCache.TryGetValue((2, unique), out var opponentName))
                return opponentName;
            return null;
        }

        /// <summary>
        /// Called from OnUpdate to process the announcement queue with pacing.
        /// </summary>
        public static void ProcessQueue()
        {
            if (_queue.Count == 0) return;
            if (Time.time - _lastAnnouncementTime < MIN_ANNOUNCEMENT_INTERVAL) return;

            var (message, priority, pan) = _queue.Dequeue();
            if (!string.IsNullOrEmpty(message))
            {
                // Spatial pan value: reserved for future positional audio cue
                // (not currently played â€” Tolk TTS does not support stereo panning)
                TolkSpeech.Speak(message, false);
                _lastBattleAnnouncement = message;
                _lastAnnouncementTime = Time.time;
                MelonLogger.Msg($"[Battle] {message}");
            }
        }

        /// <summary>
        /// Re-reads the last battle announcement (Space key).
        /// </summary>
        public static string GetLastAnnouncement()
        {
            return _lastBattleAnnouncement;
        }

        /// <summary>
        /// Main entry point: narrate a board event.
        /// Called from the delegate on BoardRenderer.OnStartRenderEvent.
        /// Uses EventUnwrapper to recursively peel CastEffect/Done wrappers.
        /// </summary>
        public static void NarrateBoardEvent(IBoardEvent boardEvent)
        {
            if (boardEvent == null) return;

            try
            {
                EventUnwrapper.ProcessEvent(boardEvent, HandleUnwrappedEvent);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"NarrateBoardEvent error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler called by EventUnwrapper for each unwrapped event.
        /// </summary>
        private static void HandleUnwrappedEvent(IBoardEvent wrapper, string innerTypeName, string source)
        {
            try
            {
                string ownerPrefix = GetOwnerPrefix(wrapper);
                string message = null;

                switch (innerTypeName)
                {
                    case "DamageMinion":
                        message = NarrateDamage(wrapper, ownerPrefix);
                        break;
                    case "DestroyMinion":
                        message = NarrateDestroy(wrapper, ownerPrefix);
                        break;
                    case "SummonMinion":
                        message = NarrateSummon(wrapper, ownerPrefix);
                        break;
                    case "BuffMinion":
                        message = NarrateBuff(wrapper, ownerPrefix);
                        break;
                    case "MarkMinionDead":
                        message = NarrateMarkDead(wrapper, ownerPrefix);
                        break;
                    case "MinionJumpAttack":
                        message = NarrateJumpAttack(wrapper, ownerPrefix);
                        break;
                    case "GiveMinionPerk":
                        message = NarrateGivePerk(wrapper, ownerPrefix);
                        break;
                    case "LoseMinionPerk":
                        message = NarrateLosePerk(wrapper, ownerPrefix);
                        break;
                    case "StealMinionPerk":
                        message = NarrateStealPerk(wrapper, ownerPrefix);
                        break;
                    case "DebuffMinion":
                        message = NarrateDebuff(wrapper, ownerPrefix);
                        break;
                    case "ForceActivatePerk":
                        message = NarrateForceActivatePerk(wrapper, ownerPrefix);
                        break;
                    case "AbilityActivate":
                        message = NarrateAbility(wrapper, ownerPrefix);
                        break;
                    case "MinionAbilityDamage":
                        // Skip â€” DamageMinion always follows with full details
                        // (amount, remaining health, target name)
                        return;
                    case "PhaseStartBattle":
                        // Suppressed: GameplayPhaseDetector.AnnounceBattleStart fires
                        // earlier and carries richer context (VS screen, team names).
                        return;
                    case "MoveMinion":
                    case "SpendGold":
                    case "PlaySpell":
                        // Skip visual/lifecycle events
                        return;
                    case "ChangeMinionMana":
                        message = NarrateChangeMana(wrapper, ownerPrefix);
                        break;
                    case "SpendMinionMana":
                        message = NarrateSpendMana(wrapper, ownerPrefix);
                        break;
                    case "ReleaseMana":
                        // Mana ability fires â€” SpendMinionMana usually follows, skip this one
                        return;
                    case "MinionTrade":
                        CacheMinionTradeNames(wrapper);
                        return;
                    default:
                        // Skip phase/lifecycle events that aren't useful
                        if (innerTypeName.StartsWith("Phase"))
                            return;
                        MelonLogger.Msg($"[BattleNarrator] Unhandled: {innerTypeName} ({source})");
                        return;
                }

                if (!string.IsNullOrEmpty(message))
                {
                    int priority = GetEventPriority(innerTypeName);
                    // Determine pan based on owner: "Your" = left (-0.8), "Enemy" = right (0.8)
                    float pan = 0f;
                    if (ownerPrefix == "Your") pan = -0.8f;
                    else if (ownerPrefix == "Enemy") pan = 0.8f;
                    _queue.Enqueue((message, priority, pan));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"HandleUnwrappedEvent error: {ex.Message}");
            }
        }

        private static string GetOwnerPrefix(IBoardEvent evt)
        {
            try
            {
                // Owner enum: World=0, Player=1, Opponent=2
                int owner = (int)evt.Owner;
                return owner == 1 ? "Your" : "Enemy";
            }
            catch { return ""; }
        }

        private static int GetEventPriority(string typeName)
        {
            return typeName switch
            {
                "PhaseStartBattle" => 10,
                "BattleResult" => 10,
                "DestroyMinion" => 9,
                "MarkMinionDead" => 9,
                "DamageMinion" => 8,
                "MinionAbilityDamage" => 8,
                "MinionJumpAttack" => 8,
                "DebuffMinion" => 7,
                "SummonMinion" => 7,
                "BuffMinion" => 6,
                "AbilityActivate" => 6,
                "ForceActivatePerk" => 6,
                "GiveMinionPerk" => 5,
                "LoseMinionPerk" => 5,
                "StealMinionPerk" => 5,
                "ChangeMinionMana" => 4,
                "SpendMinionMana" => 7,
                _ => 3
            };
        }

        // --- Individual event narrators ---
        // Each method receives the Done<T> wrapper, extracts the inner T via EventUnwrapper.ExtractDone

        private static string NarrateDamage(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var damage = EventUnwrapper.ExtractDone<DamageMinion>(wrapper);
                if (damage == null) return null;

                // wrapper.Owner is the side of the target (defender) in damage events.
                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(damage.TargetId, targetSide);
                int amount = damage.Amount;

                // Resolve source (attacker) name and side
                string sourceName = null;
                string sourcePrefix = null;
                try
                {
                    var sourceId = damage.SourceId;
                    if (sourceId != null && sourceId.HasValue)
                    {
                        // Source is the OPPOSITE side of the target in most damage events.
                        int sourceSide = targetSide == 1 ? 2 : (targetSide == 2 ? 1 : 0);
                        sourceName = ResolveName(sourceId.Value, sourceSide);
                        sourcePrefix = ResolveOwnerPrefix(sourceId.Value);
                    }
                }
                catch { }

                // Health remaining — clamp to 0 and use "lethal" wording for the
                // fatal hit so the user gets a clean "takes 4 damage, lethal" instead
                // of "takes 4 damage, -2 health remaining" (a Faint announcement
                // follows on the next event).
                string healthRemaining = "";
                try
                {
                    var resultHealth = damage.ResultHealth;
                    if (resultHealth != null)
                    {
                        int hp = resultHealth.Total;
                        healthRemaining = hp <= 0 ? ", lethal" : $", {hp} health remaining";
                    }
                }
                catch { }

                // Perk changes on defender (e.g. lost Melon, gained Weak)
                string perkInfo = "";
                try
                {
                    var defenderPerkChange = damage.ResultChangeDefenderPerk;
                    if (defenderPerkChange != null)
                    {
                        var lostPerk = defenderPerkChange.LostPerk;
                        if (lostPerk != null && lostPerk.HasValue)
                        {
                            string perkName = PetStatsReader.GetPerkDisplayName(lostPerk.Value);
                            perkInfo = $", lost {perkName}";
                        }
                        var newPerk = defenderPerkChange.NewPerk;
                        if (newPerk != null && newPerk.HasValue)
                        {
                            string perkName = PetStatsReader.GetPerkDisplayName(newPerk.Value);
                            perkInfo += $", gained {perkName}";
                        }
                    }
                }
                catch { }

                // Build message: "Enemy Dolphin dealt 4 damage to your Spider, lost Melon, 0 health remaining"
                if (!string.IsNullOrEmpty(sourceName) && !string.IsNullOrEmpty(sourcePrefix))
                {
                    string targetPrefix = ownerPrefix.ToLower();
                    return $"{sourcePrefix} {sourceName} dealt {amount} damage to {targetPrefix} {targetName}{perkInfo}{healthRemaining}";
                }

                // Fallback if source unknown (e.g. environmental damage)
                return $"{ownerPrefix} {targetName} takes {amount} damage{perkInfo}{healthRemaining}";
            }
            catch { return null; }
        }

        /// <summary>
        /// Determines whether a pet belongs to the player or opponent.
        /// Uses the owner cache (populated by CacheMinionTradeNames/SummonMinion) first,
        /// since FindMinion on shop-phase boards can't match battle-resolver IDs.
        /// Falls back to FindMinion on shop-phase boards for any IDs that happen to match.
        /// </summary>
        private static string ResolveOwnerPrefix(ItemId id)
        {
            try
            {
                // Primary: check the owner cache (works with battle-resolver IDs)
                int uniqueKey = id.Unique;
                if (_minionOwnerCache.TryGetValue(uniqueKey, out int owner))
                {
                    return owner == 1 ? "Your" : "Enemy";
                }
            }
            catch { }

            // Fallback: try FindMinion on shop-phase boards (works for shop-phase IDs
            // and DebugBoard IDs if they happen to match)
            try
            {
                var battleModel = Il2CppSpacewood.Unity.Memory.Battle;
                if (battleModel != null)
                {
                    var userBoard = battleModel.UserBoard;
                    if (userBoard != null)
                    {
                        var minion = BoardExtensions.FindMinion(userBoard, id, true, true);
                        if (minion != null) return "Your";
                    }
                    var oppBoard = battleModel.OpponentBoard;
                    if (oppBoard != null)
                    {
                        var minion = BoardExtensions.FindMinion(oppBoard, id, true, true);
                        if (minion != null) return "Enemy";
                    }
                }
            }
            catch { }

            return null;
        }

        private static string NarrateDestroy(IBoardEvent wrapper, string ownerPrefix)
        {
            // DestroyMinion fires after MarkMinionDead for the same pet.
            // Skip to avoid duplicate "fainted" announcements.
            return null;
        }

        private static string NarrateSummon(IBoardEvent wrapper, string ownerPrefix)
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
                        // Cache the summoned pet's name and ownership for future lookups
                        // (important for mid-battle summons like Ram from Sheep)
                        try
                        {
                            int key = minion.Id.Unique;
                            int ownerSide = (int)wrapper.Owner;
                            _minionNameCache[(ownerSide, key)] = minionName;
                            _minionOwnerCache[key] = ownerSide;
                        }
                        catch { }
                    }
                }
                catch { }

                return $"{ownerPrefix} {minionName} summoned";
            }
            catch { return null; }
        }

        private static string NarrateBuff(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var buff = EventUnwrapper.ExtractDone<BuffMinion>(wrapper);
                if (buff == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(buff.TargetId, targetSide);
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

                return $"{ownerPrefix} {targetName} gains {buffText}";
            }
            catch { return null; }
        }

        private static string NarrateAbility(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var ability = EventUnwrapper.ExtractDone<AbilityActivate>(wrapper);
                if (ability == null) return null;

                // AbilityActivate.Ability is the ABILITY enum (e.g. Blowfish, GeckoAbility,
                // SkunkAbility). For pets where the enum equals the pet name (Blowfish, Skunk
                // as bare names) it doubles as the pet name; for pets named "*Ability" the
                // suffix has to be stripped so the cache holds "Gecko" not "Gecko Ability".
                string abilityName = "";
                string petNameFromAbility = "";
                try
                {
                    abilityName = PetStatsReader.SplitCamelCase(ability.Ability.ToString());
                    petNameFromAbility = System.Text.RegularExpressions.Regex.Replace(
                        abilityName ?? "", @"\s*Ability$", "").Trim();
                }
                catch { }

                int battleId = ability.MinionId.Unique;
                // Owner IS reliable for AbilityActivate (unlike MinionTrade where Owner=World)
                int abilityOwnerSide = 0;
                try { abilityOwnerSide = (int)wrapper.Owner; } catch { }
                if (abilityOwnerSide > 0 &&
                    !_minionNameCache.ContainsKey((abilityOwnerSide, battleId)) &&
                    !string.IsNullOrEmpty(petNameFromAbility))
                {
                    _minionNameCache[(abilityOwnerSide, battleId)] = petNameFromAbility;
                    _minionOwnerCache[battleId] = abilityOwnerSide;
                    MelonLogger.Msg($"[BattleNarrator] AbilityActivate cached: battleId={battleId} -> {petNameFromAbility} (owner={abilityOwnerSide})");
                }

                string minionName = ResolveName(ability.MinionId, abilityOwnerSide);

                // Avoid "Blowfish's Blowfish triggers" by case-insensitive name compare after
                // stripping leading articles. Use the pet-name (suffix-stripped) for dedup,
                // but spell out the full ability name in the trigger phrasing.
                if (!string.IsNullOrEmpty(abilityName) &&
                    !SameName(petNameFromAbility, minionName) &&
                    !SameName(abilityName, minionName))
                    return $"{ownerPrefix} {minionName}'s {abilityName} triggers";
                else
                    return $"{ownerPrefix} {minionName}'s ability triggers";
            }
            catch { return null; }
        }

        private static string NarrateJumpAttack(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var jumpAttack = EventUnwrapper.ExtractDone<MinionJumpAttack>(wrapper);
                if (jumpAttack == null) return null;

                // Jump-attack: attacker is the event owner, target is the opposite side.
                int attackerSide = 0; try { attackerSide = (int)wrapper.Owner; } catch { }
                int defenderSide = attackerSide == 1 ? 2 : (attackerSide == 2 ? 1 : 0);
                string attackerName = ResolveName(jumpAttack.TargetToThrow, attackerSide);
                string targetName = ResolveName(jumpAttack.TargetToThrowAt, defenderSide);
                return $"{ownerPrefix} {attackerName} attacks {targetName}";
            }
            catch { return null; }
        }

        private static string NarrateAbilityDamage(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var abilityDmg = EventUnwrapper.ExtractDone<MinionAbilityDamage>(wrapper);
                if (abilityDmg == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(abilityDmg.TargetId, targetSide);
                int amount = abilityDmg.Amount;

                if (amount > 0)
                    return $"{ownerPrefix} {targetName} takes {amount} ability damage";
                return $"{ownerPrefix} {targetName} takes ability damage";
            }
            catch { return null; }
        }

        private static string NarrateGivePerk(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var givePerk = EventUnwrapper.ExtractDone<GiveMinionPerk>(wrapper);
                if (givePerk == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(givePerk.TargetId, targetSide);
                string perkName = ReadPerkName(givePerk);
                return $"{ownerPrefix} {targetName} gains {perkName}";
            }
            catch { return null; }
        }

        private static string NarrateLosePerk(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var losePerk = EventUnwrapper.ExtractDone<LoseMinionPerk>(wrapper);
                if (losePerk == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(losePerk.Target, targetSide);
                string perkName = ReadPerkNullable(losePerk.Perk);
                return $"{ownerPrefix} {targetName} loses {perkName}";
            }
            catch { return null; }
        }

        private static string NarrateStealPerk(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var stealPerk = EventUnwrapper.ExtractDone<StealMinionPerk>(wrapper);
                if (stealPerk == null) return null;

                // StealMinionPerk: source is the event owner; target is the opposite side.
                int sourceSide = 0; try { sourceSide = (int)wrapper.Owner; } catch { }
                int targetSide = sourceSide == 1 ? 2 : (sourceSide == 2 ? 1 : 0);
                string sourceName = ResolveName(stealPerk.Source, sourceSide);
                string targetName = ResolveName(stealPerk.Target, targetSide);
                string perkName = ReadPerkNullable(stealPerk.ResultPerk);
                return $"{ownerPrefix} {sourceName} steals {perkName} from {targetName}";
            }
            catch { return null; }
        }

        private static string NarrateDebuff(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var debuff = EventUnwrapper.ExtractDone<DebuffMinion>(wrapper);
                if (debuff == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(debuff.Target, targetSide);
                int attack = debuff.Attack;
                int health = debuff.Health;

                string debuffText;
                if (attack != 0 && health != 0)
                    debuffText = $"-{System.Math.Abs(attack)}/-{System.Math.Abs(health)}";
                else if (attack != 0)
                    debuffText = $"-{System.Math.Abs(attack)} attack";
                else
                    debuffText = $"-{System.Math.Abs(health)} health";

                return $"{ownerPrefix} {targetName} debuffed {debuffText}";
            }
            catch { return null; }
        }

        private static string NarrateMarkDead(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var markDead = EventUnwrapper.ExtractDone<MarkMinionDead>(wrapper);
                if (markDead == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(markDead.TargetId, targetSide);

                // Track fainted pets for survivor announcement â€” use NAMES with COUNTS since
                // battle-resolver IDs don't match shop-phase board IDs.
                // Count-based to handle duplicate pet names (e.g., two Spiders).
                try
                {
                    int idKey = markDead.TargetId.Unique;
                    int owner = (int)wrapper.Owner;
                    MelonLogger.Msg($"[BattleNarrator] MarkDead: {targetName} (unique={idKey}, owner={owner})");
                    if (owner == 1) // Player
                    {
                        _playerFaintedIds.Add(idKey);
                        if (targetName != "pet")
                        {
                            if (_playerFaintedNameCounts.ContainsKey(targetName))
                                _playerFaintedNameCounts[targetName]++;
                            else
                                _playerFaintedNameCounts[targetName] = 1;
                        }
                    }
                    else if (owner == 2) // Opponent
                    {
                        _opponentFaintedIds.Add(idKey);
                        if (targetName != "pet")
                        {
                            if (_opponentFaintedNameCounts.ContainsKey(targetName))
                                _opponentFaintedNameCounts[targetName]++;
                            else
                                _opponentFaintedNameCounts[targetName] = 1;
                        }
                    }
                }
                catch { }

                return $"{ownerPrefix} {targetName} fainted";
            }
            catch { return null; }
        }

        private static string NarrateForceActivatePerk(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var forcePerk = EventUnwrapper.ExtractDone<ForceActivatePerk>(wrapper);
                if (forcePerk == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(forcePerk.TargetId, targetSide);
                string perkName = ReadPerkNullable(forcePerk.Perk);
                return $"{ownerPrefix} {targetName}'s {perkName} activates";
            }
            catch { return null; }
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
                // Try Result.NewPerk first â€” it reflects the actual perk that was applied
                var result = givePerk.Result;
                if (result != null)
                {
                    try
                    {
                        var newPerk = result.NewPerk;
                        if (newPerk != null && newPerk.HasValue)
                        {
                            string name = PetStatsReader.GetPerkDisplayName(newPerk.Value);
                            if (!string.IsNullOrEmpty(name))
                                return name;
                        }
                    }
                    catch { }
                }

                // Try SourceSpell â€” the food item that granted the perk
                try
                {
                    var sourceSpell = givePerk.SourceSpell;
                    if (sourceSpell != null && sourceSpell.HasValue)
                    {
                        var spellAsset = SpellEnumExtensions.ToAsset(sourceSpell.Value);
                        if (spellAsset != null)
                        {
                            string foodName = spellAsset.GetName();
                            if (!string.IsNullOrEmpty(foodName))
                                return foodName;
                        }
                    }
                }
                catch { }

                // Fallback to direct Perk field, but skip Coconut (enum 0 = likely default)
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
        /// Uses the game's own BoardExtensions.FindMinion which properly handles
        /// dead/destroyed minions and all board locations.
        /// Checks name cache first, then live Memory.Battle boards via FindMinion.
        /// </summary>
        private static string ResolveName(ItemId id, int sideHint = 0)
        {
            try
            {
                int uniqueKey = id.Unique;

                // Environmental / world-source events come with uniqueKey == 0;
                // they don't refer to a real pet — skip the diagnostic spam.
                if (uniqueKey == 0) return "pet";

                // Caller-provided side wins because the same `unique` can belong to
                // both sides at once (shop-phase IDs and battle-resolver IDs collide).
                var cached = LookupCachedName(uniqueKey, sideHint);
                if (!string.IsNullOrEmpty(cached)) return cached;

                // Eagerly retry loading opponent board if not yet loaded
                if (!_opponentBoardLoaded && _opponentBoardRetryCount < MAX_OPPONENT_BOARD_RETRIES)
                    TryLoadOpponentBoard();

                // Use the game's own FindMinion which handles dead/destroyed pets
                // Try live boards from Memory.Battle first (most up-to-date)
                try
                {
                    var battleModel = Il2CppSpacewood.Unity.Memory.Battle;
                    if (battleModel != null)
                    {
                        // Try user board
                        var userBoard = battleModel.UserBoard;
                        if (userBoard != null)
                        {
                            var minion = BoardExtensions.FindMinion(userBoard, id, true, true);
                            if (minion != null)
                            {
                                string name = PetStatsReader.GetLocalizedName(minion);
                                if (!string.IsNullOrEmpty(name))
                                {
                                    _minionNameCache[(1, uniqueKey)] = name;
                                    _minionOwnerCache[uniqueKey] = 1;
                                    return name;
                                }
                            }
                        }

                        // Try opponent board
                        var oppBoard = battleModel.OpponentBoard;
                        if (oppBoard != null)
                        {
                            var minion = BoardExtensions.FindMinion(oppBoard, id, true, true);
                            if (minion != null)
                            {
                                string name = PetStatsReader.GetLocalizedName(minion);
                                if (!string.IsNullOrEmpty(name))
                                {
                                    _minionNameCache[(2, uniqueKey)] = name;
                                    _minionOwnerCache[uniqueKey] = 2;
                                    return name;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[BattleNarrator] FindMinion live lookup error: {ex.Message}");
                }

                // Fallback: try cached boards with FindMinion
                if (_playerBoard != null)
                {
                    try
                    {
                        var minion = BoardExtensions.FindMinion(_playerBoard, id, true, true);
                        if (minion != null)
                        {
                            string name = PetStatsReader.GetLocalizedName(minion);
                            if (!string.IsNullOrEmpty(name))
                            {
                                _minionNameCache[(1, uniqueKey)] = name;
                                _minionOwnerCache[uniqueKey] = 1;
                                return name;
                            }
                        }
                    }
                    catch { }
                }

                if (_opponentBoard != null)
                {
                    try
                    {
                        var minion = BoardExtensions.FindMinion(_opponentBoard, id, true, true);
                        if (minion != null)
                        {
                            string name = PetStatsReader.GetLocalizedName(minion);
                            if (!string.IsNullOrEmpty(name))
                            {
                                _minionNameCache[(2, uniqueKey)] = name;
                                _minionOwnerCache[uniqueKey] = 2;
                                return name;
                            }
                        }
                    }
                    catch { }
                }

                // Diagnostic: dump what IDs ARE on the boards
                try
                {
                    var bm = Il2CppSpacewood.Unity.Memory.Battle;
                    if (bm != null)
                    {
                        var ub = bm.UserBoard;
                        var ob = bm.OpponentBoard;
                        string userIds = "null";
                        string oppIds = "null";
                        if (ub?.Minions?.Items != null)
                        {
                            var ids = new List<string>();
                            for (int i = 0; i < ub.Minions.Items.Count; i++)
                            {
                                try { var mm = ub.Minions.Items[i]; if (mm != null) ids.Add($"{mm.Id.Unique}={mm.Enum}"); } catch { }
                            }
                            userIds = string.Join(",", ids);
                        }
                        if (ob?.Minions?.Items != null)
                        {
                            var ids = new List<string>();
                            for (int i = 0; i < ob.Minions.Items.Count; i++)
                            {
                                try { var mm = ob.Minions.Items[i]; if (mm != null) ids.Add($"{mm.Id.Unique}={mm.Enum}"); } catch { }
                            }
                            oppIds = string.Join(",", ids);
                        }
                        MelonLogger.Warning($"[BattleNarrator] MISS unique={uniqueKey} | UserBoard=[{userIds}] | OppBoard=[{oppIds}] | Cache=[{string.Join(",", _minionNameCache.Keys.Select(k => $"{k.side}:{k.unique}"))}]");
                    }
                    else
                    {
                        MelonLogger.Warning($"[BattleNarrator] MISS unique={uniqueKey} | Memory.Battle is null | Cache=[{string.Join(",", _minionNameCache.Keys.Select(k => $"{k.side}:{k.unique}"))}]");
                    }
                }
                catch (Exception diagEx)
                {
                    MelonLogger.Warning($"[BattleNarrator] MISS unique={uniqueKey} (diag error: {diagEx.Message})");
                }
                return "pet";
            }
            catch
            {
                return "pet";
            }
        }

        /// <summary>
        /// Computes and returns a string describing which pets survived the battle.
        /// Uses name-based tracking since battle-resolver IDs don't match shop-phase board IDs.
        /// Compares the initial board pet names against pets that fainted during battle.
        /// </summary>
        /// <summary>
        /// Returns null â€” survivor announcement is disabled because battle-resolver IDs
        /// don't match shop-phase board IDs, making accurate tracking impossible.
        /// The battle outcome (win/loss/draw) is announced by the shop phase transition instead.
        /// </summary>
        public static string GetBattleSurvivors()
        {
            return null;
        }

        /// <summary>
        /// Case-insensitive name compare that ignores leading articles ("the", "a", "an").
        /// Used to dedup pet names against ability names in NarrateAbility, where
        /// localization can introduce "The Gecko" vs "Gecko" mismatches.
        /// </summary>
        private static bool SameName(string a, string b)
        {
            return string.Equals(StripArticle(a), StripArticle(b),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string StripArticle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return System.Text.RegularExpressions.Regex.Replace(
                s.Trim(), @"^(the|a|an)\s+", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Get all pet names from a board model's minions list.
        /// </summary>
        private static List<string> GetBoardPetNames(BoardModel board)
        {
            var names = new List<string>();
            if (board?.Minions?.Items == null) return names;
            try
            {
                for (int i = 0; i < board.Minions.Items.Count; i++)
                {
                    try
                    {
                        var m = board.Minions.Items[i];
                        if (m != null)
                        {
                            string name = PetStatsReader.GetLocalizedName(m);
                            if (!string.IsNullOrEmpty(name) && name != "Unknown pet")
                                names.Add(name);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// When MinionTrade fires (before each combat clash), seed the name cache
        /// using positional name lists built at battle start.
        ///
        /// Key insight: Battle events use NEW sequential IDs (0,1,2...) assigned by the
        /// battle resolver, NOT the shop-phase IDs on the boards (149, 122...).
        /// FindMinion(shopBoard, battleId) will NEVER work. Instead, we use ordered
        /// positional name lists and the first-trade heuristic:
        /// - First trade: player always attacks first in SAP, so attackerId=player, defenderId=opponent
        /// - Subsequent trades: if one ID is already known (from AbilityActivate), use it to
        ///   determine the other ID's side
        /// </summary>
        private static void CacheMinionTradeNames(IBoardEvent wrapper)
        {
            try
            {
                Il2CppBoardEvents.MinionTrade trade = null;
                try { trade = EventUnwrapper.ExtractDone<Il2CppBoardEvents.MinionTrade>(wrapper); } catch { }
                if (trade == null)
                {
                    try { trade = wrapper.TryCast<Il2CppBoardEvents.MinionTrade>(); } catch { }
                }
                if (trade == null) return;

                int attackerKey = trade.attackerId.Unique;
                int defenderKey = trade.defenderId.Unique;

                MelonLogger.Msg($"[BattleNarrator] MinionTrade: attacker={attackerKey}, defender={defenderKey}, firstTrade={!_firstTradeProcessed}");

                // Determine which ID is player and which is opponent.
                int playerKey = -1;
                int opponentKey = -1;

                // Check if either ID is already known from AbilityActivate caching
                bool attackerKnown = _minionOwnerCache.TryGetValue(attackerKey, out int attackerOwner);
                bool defenderKnown = _minionOwnerCache.TryGetValue(defenderKey, out int defenderOwner);

                if (attackerKnown && attackerOwner == 1) // Attacker is player
                {
                    playerKey = attackerKey;
                    opponentKey = defenderKey;
                }
                else if (attackerKnown && attackerOwner == 2) // Attacker is opponent
                {
                    playerKey = defenderKey;
                    opponentKey = attackerKey;
                }
                else if (defenderKnown && defenderOwner == 1) // Defender is player
                {
                    playerKey = defenderKey;
                    opponentKey = attackerKey;
                }
                else if (defenderKnown && defenderOwner == 2) // Defender is opponent
                {
                    playerKey = attackerKey;
                    opponentKey = defenderKey;
                }
                else if (!_firstTradeProcessed)
                {
                    // First trade heuristic: SAP always has player attack first
                    playerKey = attackerKey;
                    opponentKey = defenderKey;
                }
                else
                {
                    // No info â€” try to infer from name cache
                    // If one ID is already in the name cache, the other must be the opposite side
                    bool attackerCached = LookupCachedName(attackerKey) != null;
                    bool defenderCached = LookupCachedName(defenderKey) != null;
                    if (attackerCached && !defenderCached)
                    {
                        // We know attacker name but not side â€” fall through with best guess
                        playerKey = attackerKey;
                        opponentKey = defenderKey;
                    }
                    else if (defenderCached && !attackerCached)
                    {
                        playerKey = defenderKey;
                        opponentKey = attackerKey;
                    }
                    else
                    {
                        // Both unknown or both known â€” default to attacker=player
                        playerKey = attackerKey;
                        opponentKey = defenderKey;
                    }
                }

                _firstTradeProcessed = true;

                // Check if these IDs are FIRST-TIME before we set ownership below.
                // (Same pet wins multiple rounds â†’ same ID in later MinionTrades, don't re-advance.)
                bool playerIsNew = playerKey >= 0 && !_minionOwnerCache.ContainsKey(playerKey);
                bool opponentIsNew = opponentKey >= 0 && !_minionOwnerCache.ContainsKey(opponentKey);

                // Cache ownership
                if (playerKey >= 0) _minionOwnerCache[playerKey] = 1;
                if (opponentKey >= 0) _minionOwnerCache[opponentKey] = 2;

                // Cache player name from positional list.
                // Advance the positional index only for first-time IDs.
                // Also advance when AbilityActivate pre-cached the name â€” that ID still
                // occupies a positional slot that needs to be consumed.
                if (playerIsNew && _playerPositionIndex < _playerPositionalNames.Count)
                {
                    if (!_minionNameCache.ContainsKey((1, playerKey)))
                    {
                        string name = _playerPositionalNames[_playerPositionIndex];
                        _minionNameCache[(1, playerKey)] = name;
                        MelonLogger.Msg($"[BattleNarrator] MinionTrade cached player: battleId={playerKey} -> {name} (pos={_playerPositionIndex})");
                    }
                    _playerPositionIndex++;
                }

                // Cache opponent name from positional list (same logic as player)
                if (opponentIsNew && _opponentPositionIndex < _opponentPositionalNames.Count)
                {
                    if (!_minionNameCache.ContainsKey((2, opponentKey)))
                    {
                        string name = _opponentPositionalNames[_opponentPositionIndex];
                        _minionNameCache[(2, opponentKey)] = name;
                        MelonLogger.Msg($"[BattleNarrator] MinionTrade cached opponent: battleId={opponentKey} -> {name} (pos={_opponentPositionIndex})");
                    }
                    _opponentPositionIndex++;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BattleNarrator] CacheMinionTradeNames error: {ex.Message}");
            }
        }

        private static string NarrateChangeMana(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var changeMana = EventUnwrapper.ExtractDone<ChangeMinionMana>(wrapper);
                if (changeMana == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(changeMana.TargetId, targetSide);
                int amount = 0;
                try { amount = changeMana.ResultAmount; } catch { }
                if (amount == 0)
                {
                    try { amount = changeMana.Amount; } catch { }
                }

                if (amount > 0)
                    return $"{ownerPrefix} {targetName} gained {amount} mana";
                else if (amount < 0)
                    return $"{ownerPrefix} {targetName} lost {System.Math.Abs(amount)} mana";
                return null;
            }
            catch { return null; }
        }

        private static string NarrateSpendMana(IBoardEvent wrapper, string ownerPrefix)
        {
            try
            {
                var spendMana = EventUnwrapper.ExtractDone<SpendMinionMana>(wrapper);
                if (spendMana == null) return null;

                int targetSide = 0; try { targetSide = (int)wrapper.Owner; } catch { }
                string targetName = ResolveName(spendMana.TargetId, targetSide);
                return $"{ownerPrefix} {targetName} mana ability activated";
            }
            catch { return null; }
        }

        /// <summary>
        /// Clear all state (called when leaving battle).
        /// </summary>
        public static void Reset()
        {
            _queue.Clear();
            _minionNameCache.Clear();
            _minionOwnerCache.Clear();
            _lastBattleAnnouncement = "";
            _playerBoard = null;
            _opponentBoard = null;
            _opponentBoardLoaded = false;
            _opponentBoardRetryCount = 0;
            _playerFaintedIds.Clear();
            _opponentFaintedIds.Clear();
            _playerFaintedNameCounts.Clear();
            _opponentFaintedNameCounts.Clear();
            _playerPositionalNames.Clear();
            _opponentPositionalNames.Clear();
            _playerPositionIndex = 0;
            _opponentPositionIndex = 0;
            _firstTradeProcessed = false;
        }
    }
}
