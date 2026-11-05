using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using ArcaneCore.World.Bots;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Tag contention of <see cref="PlayerbotBrain"/>: whose fight a mob belongs to (tap, group, threat list), the fruitless/unreachable/invincible target
/// marks, the fight-progress watch and leaving or healing a tagger. Split from PlayerbotBrain.cs without changing any member.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>
    /// Whether <paramref name="creature"/> belongs to another player's fight: tapped by someone outside the bot's group (the
    /// client's grey name: UNIT_DYNFLAG_TAPPED without TAPPED_BY_PLAYER as the bot sees it, LootService's viewer filter over
    /// Creature.LootTapper), or fighting a player who is neither the bot nor in its group (its victim, or any player on
    /// its threat list). vmangos bots take such targets only from their leader's fight (PartyBotAI::SelectAttackTarget).
    /// </summary>
    internal static bool IsSomeoneElses(Player player, Creature creature, GroupManager? groups)
    {
        uint seen = creature.GetValueFor(UpdateFields.UnitDynamicFlags, player);
        if ((seen & Game.Loot.LootService.UnitDynFlagTapped) != 0 && (seen & Game.Loot.LootService.UnitDynFlagTappedByPlayer) == 0)
            return true;
        if (creature.Combat.Victim is Player victim && IsStranger(player, victim, groups))
            return true;
        if (!creature.Combat.HasThreatList) return false;
        IReadOnlyList<ThreatEntry> entries = creature.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++) // indexed: an enumerator over the interface would be boxed for every candidate
        {
            if (entries[index].Target is Player other && IsStranger(player, other, groups))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The player behind a creature's tag or fight: the player who tapped it (or the owner of the pet that did), else the stranger it
    /// fights or holds on its threat list. Null when nobody can be found.
    /// </summary>
    internal static Player? TaggerOf(Player player, Creature creature, GroupManager? groups)
    {
        if (creature.Map is not { } map) return null;
        ObjectGuid tap = creature.LootTapper;
        if (!tap.IsEmpty && TapperOf(map, tap) is { } tapper) return tapper;

        if (creature.Combat.Victim is Player victim && IsStranger(player, victim, groups)) return victim;
        if (!creature.Combat.HasThreatList) return null;
        IReadOnlyList<ThreatEntry> entries = creature.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++)
            if (entries[index].Target is Player other && IsStranger(player, other, groups)) return other;
        return null;
    }

    /// <summary>The player a tap belongs to: the tapper itself, or the owner of the pet that tapped. Null when neither is on the map.</summary>
    private static Player? TapperOf(Map map, ObjectGuid tap)
    {
        if (map.FindPlayer(tap) is { } tapper) return tapper;
        if (map.FindObject(tap) is Unit pet && pet.CharmerOrOwnerGuid.IsPlayer && map.FindPlayer(pet.CharmerOrOwnerGuid) is { } owner) return owner;
        return null;
    }

    /// <summary>
    /// The kill-steal rule (spec 12.3: only when the other side started second). Tracks the mob the bot is engaged with (on its threat list while nobody
    /// outside the bot's group holds its tap) in <see cref="_engagedMob"/>, and records the steal when an outsider takes the tag while the bot is still on the list.
    /// Tag and group are sampled once per think: a groupmate who tags and leaves the group (or is kicked) within one think is seen first as a stranger holding the tag,
    /// which differs from the stored tapper (none yet), and so can be blamed for a steal.
    /// </summary>
    private void NoteEngagement(Player player, Creature held, bool onThreatList, GroupManager? groups)
    {
        ulong mob = held.Guid.Value;
        if (!onThreatList)
        {
            // Not (or no longer) on its threat list: the bot was walking up, or the mob evaded or reset. Whatever tags it next took nothing from the bot.
            if (_engagedMob == mob) ClearEngagement();
            return;
        }

        ObjectGuid tap = held.LootTapper;
        Player? tapper = tap.IsEmpty || held.Map is not { } map ? null : TapperOf(map, tap);
        if (tap.IsEmpty || tapper is not null && !IsStranger(player, tapper, groups))
        {
            // Untagged, or tagged by the bot or its group: the bot has it. The tapper is stored as it is now: a tap that is unchanged when the group
            // changes under it (the player leaves, kicks the bot, the group disbands) turns that tapper into a stranger, but takes nothing from the bot.
            _engagedMob = mob;
            _engagedTapper = tapper?.Guid.Value ?? 0;
            return;
        }

        // An outsider holds the tag. It is a steal only if the tag CHANGED to them since the bot engaged (from untagged or from the own side):
        // the bot engaged first and the other side started second.
        if (tapper is not null && _engagedMob == mob && tapper.Guid.Value != _engagedTapper)
        {
            ClearEngagement(); // once per engagement
            Social?.Note(player.Guid, tapper.Guid, BotSocial.PlayerbotSocialEvent.KillSteal, tapper.Team != player.Team);
        }
    }

    /// <summary>
    /// Whether the corpse of <paramref name="creature"/> is the bot's to loot and count: the viewer's own tapped flag (the tap is the
    /// bot's or its group's), and not a tap that is another's (<see cref="Game.Loot.LootService.UnitDynFlagTapped"/> without
    /// <see cref="Game.Loot.LootService.UnitDynFlagTappedByPlayer"/> as the bot sees it) or a kill that earns nobody credit. A corpse with
    /// no tap on record is left to the loot request, as before.
    /// </summary>
    internal static bool HasLootRights(Player player, Creature creature)
    {
        if (!creature.KillCreditAllowed) return false;
        uint seen = creature.GetValueFor(UpdateFields.UnitDynamicFlags, player);
        if ((seen & Game.Loot.LootService.UnitDynFlagTappedByPlayer) != 0) return true;
        return (seen & Game.Loot.LootService.UnitDynFlagTapped) == 0;
    }

    private static bool IsStranger(Player player, Player other, GroupManager? groups)
        => other.Guid != player.Guid && groups?.AreInSameGroup(player.Guid, other.Guid) != true;

    /// <summary>The creature a bot in combat without a victim of its own fights back (internal for the allocation test).</summary>
    internal Creature? FindDefensiveAttacker(Player player, bool defend = false)
    {
        if (player.Map is not { } map) return null;
        // A creature swinging at the bot comes first; if none is, any living creature that still holds the bot on its
        // threat list (fighting a pet, a party/camp fellow, or held at range) is the fight keeping Combat.IsInCombat on.
        return FirstDefensiveCandidate(player, map, player.Combat.Attackers, defend)
            ?? FirstDefensiveCandidate(player, map, player.Combat.ThreatenedBy, defend);
    }

    /// <summary>
    /// The defensive target among <paramref name="units"/>: a living, attackable creature on the bot's map, reachable ones first, then the
    /// bot's current target, then the nearest; of equals the first met wins. One pass with no allocation: it runs at every think of a
    /// fighting bot, and the LINQ chain it replaced (OfType, Where, OrderBy, two ThenBy) allocated about 140 MB in 40 s at 400 bots
    /// (perf-tick 2026-10-20). The live sets are hash sets, walked with their own enumerator.
    /// </summary>
    private Creature? FirstDefensiveCandidate(Player player, Map map, IReadOnlySet<Unit> units, bool defend)
    {
        DefensiveCandidate best = default;
        if (units is HashSet<Unit> set)
        {
            foreach (Unit unit in set)
                best = BetterDefensiveCandidate(player, map, unit, best, defend);
        }
        else
        {
            foreach (Unit unit in units)
                best = BetterDefensiveCandidate(player, map, unit, best, defend);
        }

        return best.Creature;
    }

    private DefensiveCandidate BetterDefensiveCandidate(Player player, Map map, Unit unit, DefensiveCandidate best, bool defend)
    {
        if (unit is not Creature creature || !creature.IsInWorld || !creature.IsAlive || creature.IsInEvadeMode || IsFruitless(creature)
            || !ReferenceEquals(creature.Map, map)
            || !map.Combat.Hooks.CanAttack(player, creature) || (defend && DefendSkips(player, creature))) return best;
        var candidate = new DefensiveCandidate(creature, IsUnreachable(creature), ReferenceEquals(creature, _target) ? 0 : 1, Distance(player, creature));
        if (best.Creature is null) return candidate;
        int order = candidate.Unreachable.CompareTo(best.Unreachable);
        if (order == 0) order = candidate.TargetRank.CompareTo(best.TargetRank);
        if (order == 0) order = candidate.Distance.CompareTo(best.Distance);
        return order < 0 ? candidate : best;
    }

    /// <summary>One candidate of <see cref="FirstDefensiveCandidate"/> with its sort keys.</summary>
    private readonly record struct DefensiveCandidate(Creature? Creature, bool Unreachable, int TargetRank, float Distance);

    private void MarkUnreachable(Creature creature, uint forMs = UnreachableTargetMs)
    {
        uint now = _session.World.NowMs;
        foreach (ObjectGuid expired in _unreachable.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _unreachable.Remove(expired);
        if (_unreachable.Count < 64 || _unreachable.ContainsKey(creature.Guid)) _unreachable[creature.Guid] = unchecked(now + forMs);
    }

    /// <summary>
    /// How long a target the bot stalled on, or fought without a blow or a step for <see cref="FightNoProgressMs"/>, is skipped: about a
    /// respawn, not the 30 seconds of a refused chase (<see cref="UnreachableTargetMs"/>), after which the same creature was chosen again
    /// and held the bot for another stall (bot QA #697, #700, #862: the same target entry stalled again within a minute or two).
    /// </summary>
    internal const uint StalledTargetMs = 300_000;

    /// <summary>The longest a bot holds a living target without its health falling and without moving <see cref="FightProgressYards"/> (<see cref="FightGoesNowhere"/>).</summary>
    internal const uint FightNoProgressMs = 20_000;

    /// <summary>A step this long (yards) at a target counts as progress of the fight (a chase, a reposition).</summary>
    internal const float FightProgressYards = 3f;

    private ObjectGuid _fightWatchTarget;
    private uint _fightWatchHealth;
    private System.Numerics.Vector3 _fightWatchAt;
    private uint _fightWatchSince;
    private uint _fightWatchSeen;

    /// <summary>A fight not looked at for this long (world ms: the bot rested, travelled or did something else) is watched afresh.</summary>
    private const uint FightWatchGapMs = 15_000;

    /// <summary>
    /// The fight with <paramref name="target"/> makes no progress: since the bot took it (or since its last progress) its health has not
    /// fallen and the bot has not moved <see cref="FightProgressYards"/>, for <see cref="FightNoProgressMs"/>. A bot that chases, swings
    /// or casts at a target it can reach makes progress well within that; one that stands under a target on a ledge, or re-plans a
    /// chase that ends where it stands, does not (bot QA #697, #700, #862: <c>goal=Combat</c> stalls with motion moving:no dest:none,
    /// phase combat). True once, then the watch starts over.
    /// </summary>
    private bool FightGoesNowhere(Player player, Creature target)
    {
        uint now = _session.World.NowMs;
        var at = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        bool gap = unchecked(now - _fightWatchSeen) > FightWatchGapMs;
        _fightWatchSeen = now;
        if (gap || target.Guid != _fightWatchTarget || target.Health < _fightWatchHealth
            || System.Numerics.Vector3.Distance(at, _fightWatchAt) >= FightProgressYards)
        {
            (_fightWatchTarget, _fightWatchHealth, _fightWatchAt, _fightWatchSince) = (target.Guid, target.Health, at, now);
            return false;
        }

        _fightWatchHealth = target.Health; // a target healing back up is no progress, and none taken back either
        if (unchecked(now - _fightWatchSince) < FightNoProgressMs) return false;
        _fightWatchTarget = default;
        return true;
    }

    /// <summary>
    /// The target's health has been held at its death-prevention threshold for <c>Contention.InvincibleTargetAbandonSeconds</c> (a creature
    /// an EventAI script keeps at 1 health, quest 2681's servants): the bot cannot kill it. True once, with the anomaly reported; the caller
    /// sets the creature aside the way it does a fight that goes nowhere (<see cref="Combat.PlayerbotInvincibleWatch"/>).
    /// </summary>
    private bool FightsInvincibleTarget(Player player, Creature target)
        => _invincibleWatch.Observe(player, target, _session.World.NowMs,
            (uint)Math.Clamp(_options.Contention.InvincibleTargetAbandonSeconds, 1, 600) * 1000u);

    /// <summary>
    /// Whether the creature's EventAI holds it invincible from spawn until a particular spell hits it
    /// (<see cref="ArcaneCore.Kernel.WorldData.Creatures.CreatureAiContent.IsInvincibleUntilSpellHit"/>): no target for a bot's grind or hunt.
    /// </summary>
    private bool IsHeldInvincibleEntry(Creature creature)
    {
        if (!_deferFeaturesLooked)
        {
            _deferCreatures = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>();
            _deferQuests = _session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>();
            _deferFeaturesLooked = true;
        }

        return _deferCreatures?.Content?.Ai.IsInvincibleUntilSpellHit(creature.Entry) == true;
    }

    private bool IsUnreachable(Creature creature)
        => _unreachable.TryGetValue(creature.Guid, out uint until) && unchecked((int)(until - _session.World.NowMs)) > 0;

    /// <summary>
    /// Creatures the bot fought without progress (<see cref="FightGoesNowhere"/>) until when (world ms): no defensive target either while
    /// the mark lasts (<see cref="StalledTargetMs"/>). An attacker is otherwise always the bot's fight, unreachable or not, so the one the bot
    /// had just set aside, still swinging at it, was taken back on the next think and held it for the next 20 seconds.
    /// </summary>
    private readonly Dictionary<ObjectGuid, uint> _fruitless = [];

    // The mob the bot is on the threat list of while nobody outside its group holds the tap (NoteEngagement): the only mob another player's tag can steal.
    // Cleared when the bot's target changes or dies (UpdateCore), and when the bot is off the mob's threat list (evade, reset, walking up).
    private ulong _engagedMob;

    // The player behind the tap of _engagedMob when the engagement was last noted (pets resolved to their owner), 0 when the mob was untagged.
    // A KillSteal needs the current tapper to be a stranger who differs from it.
    private ulong _engagedTapper;

    private void ClearEngagement()
    {
        _engagedMob = 0;
        _engagedTapper = 0;
    }

    private void MarkFruitless(Creature creature)
    {
        uint now = _session.World.NowMs;
        foreach (ObjectGuid expired in _fruitless.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _fruitless.Remove(expired);
        if (_fruitless.Count < 16 || _fruitless.ContainsKey(creature.Guid)) _fruitless[creature.Guid] = unchecked(now + StalledTargetMs);
    }

    private bool IsFruitless(Creature creature)
        => _fruitless.Count != 0 && _fruitless.TryGetValue(creature.Guid, out uint until) && unchecked((int)(until - _session.World.NowMs)) > 0;

    /// <summary>Whether a live creature in sight is no target for the bot (the grind filter's rules; <see cref="PlayerbotWorldDestinations.Untakeable"/>).</summary>
    internal bool IsUntakeable(Player player, Creature creature)
        => _quests.PreferredItemUseId != 0 && creature.Entry == _quests.PreferredCreatureEntry
            ? IsUnreachable(creature) || _suspensions.IsEntrySuspended(creature.Entry, _session.World.NowMs)
                || _contention.IsEntrySuspended(creature.Entry, _session.World.NowMs)
                || _home is { } home && creature.MapId == home.MapId && !home.Contains(creature)
                || _quests.IsUnavailableItemTarget(creature, _session.World.NowMs)
            : IsSkipped(creature) || _contention.IsSetAside(creature.Guid.Value, _session.World.NowMs)
                || IsSomeoneElses(player, creature, GroupsOf(player))
                || player.Map is { } map && !map.Combat.Hooks.CanAttack(player, creature);

    /// <summary>
    /// A creature the goals do not choose now: unreachable, of an entry set aside (stall, death loop), or outside the bot's home area
    /// (outside the circle or, for a zone-clipped home, out of the home's zone: <see cref="PlayerbotHomeArea.Contains(Unit)"/>; a
    /// creature on another map than the home is not judged: the bot is off-map and behaves as one with no home). This holds wherever
    /// the bot is on the home map, so a bot away on an errand (a vendor in Stormwind) picks no fight on the road (fail closed).
    /// The defensive-attacker path does not use this: a bot always fights back.
    /// </summary>
    private bool IsSkipped(Creature creature)
        => IsUnreachable(creature) || IsHeldInvincibleEntry(creature) || _suspensions.IsEntrySuspended(creature.Entry, _session.World.NowMs)
            || _contention.IsEntrySuspended(creature.Entry, _session.World.NowMs)
            || _quests.PreferredItemUseId != 0 && creature.Entry == _quests.PreferredCreatureEntry
            || _home is { } home && creature.MapId == home.MapId && !home.Contains(creature);

    /// <summary>
    /// The held mob turned out to be another player's (a tag or a fight the bot is not part of, <see cref="IsSomeoneElses"/>; the bot is
    /// not on its threat list). Leave it alone for <see cref="PlayerbotContentionOptions.SomeoneElsesMemorySeconds"/> and let the caller
    /// choose again, unless the bot decides to help its tagger (<see cref="PlayerbotContention.Decide"/>, at most once per mob per memory
    /// window): then it keeps the mob as an assist-only target, no credit and no loot, until the mob dies or the tagger stops fighting.
    /// </summary>
    private void DropIfSomeoneElses(Player player, Creature held)
    {
        uint now = _session.World.NowMs;
        ulong mob = held.Guid.Value;
        bool mine = player.Combat.ThreatenedBy.Contains(held) || ReferenceEquals(held.Combat.Victim, player);
        GroupManager? groups = GroupsOf(player);
        // Before the early exit below: a bot still on the threat list is "mine", and that is exactly when a tag taken from it is a steal.
        // (_attacking says only that a swing was sent; it can be stale.)
        NoteEngagement(player, held, mine, groups);

        if (_contention.IsAssisting(held.Guid))
        {
            // The mob evaded and the bot tagged it afresh: it is the bot's own now, kill and loot included.
            if (mine && held.LootTapper == player.Guid) { _contention.EndAssist(); return; }
            if (player.Map?.FindPlayer(_contention.AssistTagger) is { IsAlive: true } helped && helped.Combat.IsInCombat) return;
            _contention.EndAssist();
            if (!mine) LeaveSomeoneElses(player, held, now);
            return;
        }

        if (mine) return;
        if (_contention.IsSetAside(mob, now)) { LeaveSomeoneElses(player, held, now); return; }
        if (!IsSomeoneElses(player, held, groups)) return;

        // Only a tagged mob has a tagger to help: an untagged one that merely fights a stranger is left alone (helping would take its tag).
        Player? tagger = held.LootTapper.IsEmpty ? null : TaggerOf(player, held, groups);
        bool shares = tagger is not null && _quests.SharesKillQuest(player, tagger, held.Entry);
        var facts = new PlayerbotHelpFacts(tagger is not null,
            tagger is null ? float.PositiveInfinity : Distance(player, tagger),
            player.MaxHealth == 0 ? 0f : player.Health * 100f / player.MaxHealth,
            _quests.ReturnQuest != 0 || Risk.Retreat.Active || Risk.Waiting,
            tagger is not null && TaggerIsLosing(tagger),
            tagger?.Session is WorldSession { IsManaged: false },
            shares);
        if (tagger is not null && shares) RaiseContentionSignal(player, tagger, held, groups, now);
        if (_contention.Decide(facts, player.Guid.Value, mob, now) == PlayerbotHelpDecision.Help && tagger is not null)
        {
            _contention.BeginAssist(held.Guid, tagger.Guid);
            Social?.NoteHelp(player, tagger);
            _contention.ReleaseClaim();
            return;
        }

        LeaveSomeoneElses(player, held, now);
    }

    private void LeaveSomeoneElses(Player player, Creature mob, uint now)
    {
        _contention.MarkSomeoneElses(mob.Guid.Value, now);
        _contention.NoteLostMob(mob.Entry);
        _contention.ReleaseClaim();
        if (_attacking || ReferenceEquals(player.Combat.Victim, mob)) _session.TryManagedAction(WorldOpcode.CmsgAttackstop, []);
        _target = null;
        AbandonRoute();
        _attacking = false;
        _lootOpened = false;
        TargetEntry = 0;
    }

    /// <summary>The tagger is under <see cref="PlayerbotContention.LosingHealthPct"/> of its health, or has <see cref="PlayerbotContention.LosingEnemies"/> enemies on it.</summary>
    private static bool TaggerIsLosing(Player tagger)
        => tagger.MaxHealth > 0 && tagger.Health * 100f < PlayerbotContention.LosingHealthPct * tagger.MaxHealth
            || Math.Max(tagger.Combat.Attackers.Count, tagger.Combat.ThreatenedBy.Count) >= PlayerbotContention.LosingEnemies;

    /// <summary>
    /// Both the bot and the tagger are ungrouped and have the creature as an unfinished kill objective of the same quest: ask for a
    /// group (source "contention"; the coordinator matches by objective, and with <see cref="PlayerbotGroupOptions.InvitePlayers"/> a
    /// real player nearby is invited, PlayerbotGroupCoordinator.MatchWithPlayer).
    /// </summary>
    private void RaiseContentionSignal(Player player, Player tagger, Creature mob, GroupManager? groups, uint now)
    {
        if (!_options.Groups.Enabled || groups is null || Distance(player, tagger) > PlayerbotContention.HelpYards) return;
        if (groups.GetGroup(player.Guid) is not null || groups.GetGroup(tagger.Guid) is not null) return;
        Risk.RaiseGroupSignal(new PlayerbotGroupSignal(mob.Entry, 2, player.MapId, new System.Numerics.Vector3(mob.X, mob.Y, mob.Z), now,
            PlayerbotGroupSignal.ContentionSource));
    }

    /// <summary>The bot gained no credit on its quest creature <paramref name="entry"/> and no other objective is left: ask for a group.</summary>
    private void RaiseContentionExhausted(uint entry, uint quest)
    {
        if (!_options.Groups.Enabled || _session.Player is not { } player) return;
        Risk.RaiseGroupSignal(new PlayerbotGroupSignal(entry, 2, player.MapId, new System.Numerics.Vector3(player.X, player.Y, player.Z),
            _session.World.NowMs, PlayerbotGroupSignal.ContentionSource, quest));
    }

    private static bool IsPvpFlagged(Player player) => (player.UnitFlags & UnitFlags.Pvp) != 0 || (player.Flags & PlayerFlags.FfaPvp) != 0;

    /// <summary>
    /// Whether the bot may cast a heal at <paramref name="tagger"/>: alive, on the bot's map, of the bot's team and friendly (never
    /// hostile), not flagged for PvP unless the bot is too (a heal on a flagged player flags the healer), and in line of sight. Range
    /// and the rest of castability are the rotation's own check. A server refusal would also bar that spell from the bot's own rotation
    /// for <see cref="PlayerbotCombatSpells.RefusalBackoffMs"/>, so a cast that would be refused is never sent.
    /// </summary>
    internal static bool IsSafeHealTarget(Player player, Player tagger)
    {
        if (!tagger.IsAlive || !tagger.IsInWorld || !ReferenceEquals(tagger.Map, player.Map) || player.Map is not { } map) return false;
        if (tagger.Team != player.Team || !map.Combat.Hooks.IsFriendly(player, tagger) || map.Combat.Hooks.IsHostileTo(player, tagger)) return false;
        if (IsPvpFlagged(tagger) && !IsPvpFlagged(player)) return false;
        return player.IsWithinLineOfSight(tagger);
    }

    /// <summary>
    /// A healer class with a direct heal tops the tagger it helps up (below 85% health) before it fights the mob, when
    /// <see cref="IsSafeHealTarget"/> holds. The combat view is built once for the heals (<see cref="PlayerbotCombatSpells.FirstCastable"/>).
    /// True: a heal was accepted (or a cast is under way) and the think is spent.
    /// </summary>
    internal bool TryHealTagger(Player player)
    {
        if (player.Class is not (Game.Class.Priest or Game.Class.Paladin or Game.Class.Druid or Game.Class.Shaman)) return false;
        if (player.Map?.FindPlayer(_contention.AssistTagger) is not { } tagger || !IsSafeHealTarget(player, tagger)
            || tagger.MaxHealth == 0 || tagger.Health * 100f >= 85f * tagger.MaxHealth) return false;
        // A self-only heal such as Desperate Prayer would heal the bot, not its tagger; a scripted (Holy Light) or chain (Healing Wave) heal lands on the target.
        PlayerbotAbilities abilities = _combatSpells.Abilities(player);
        bool healed = _combatSpells.FirstCastable(player, abilities.DirectHeals, tagger, abilities.HealsAnotherUnit) is { } heal && _combatSpells.CastAt(player, heal, tagger);
        if (healed) Social?.NoteHelp(player, tagger);
        return healed;
    }
}
