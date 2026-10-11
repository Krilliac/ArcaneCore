using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.Honor;
using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// Honor for kills. Behavioural port of vmangos <c>Player::RewardHonorOnDeath</c> (player victims, Player.cpp:21848-21919),
/// <c>Player::RewardHonor</c> (civilians and racial leaders, :21810-21846), <c>Player::IsHonorOrXPTarget</c>
/// (:19943-19957) and the honor lines of <c>RewardGroupAtKill_helper</c> (Group.cpp:2295-2300). No reference code is copied.
/// <para>
/// A player's damage history (<see cref="PvpDamageLedger"/>) is filled from <see cref="MapCombat.DamageTaken"/>; when a
/// player dies the honor is shared out by damage share: lone attackers must be alive, at group reward distance and of
/// the other team; a group's damage is pooled and split evenly over its qualifying members with the group rate. Every
/// recipient needs the victim above its gray level. Creature kills reward the TAPPER, as vmangos does (Unit::Kill takes the creature's loot recipient and its group, Unit.cpp:988-1001, 1076-1079):
/// the first player (or the owner of the first pet) to damage a creature that is not a pet taps it (Unit::DealDamage, Unit.cpp:804-807), the
/// tap is dropped on an explicit reset (evade or respawn, CreatureAI.cpp:344, Creature.cpp:2306), and the tapper or
/// every living member of its group in range is rewarded, whoever landed the killing blow. A creature players damaged 35% or less
/// (Creature::IsLootAllowedDueToDamageOrigin, settled at the kill) rewards the killing blow's player ALONE (pGroupTap stays null,
/// Unit.cpp:1007-1008); a creature nobody tapped rewards the killer and his current group (:998-999).
/// A respawn is a reset too (Creature.cpp:2306, via Creature.ClearLootRecipient);
/// an owner's active pet holds its tap until the owner or the tap's group hits. Without that player tapper, group rewards are also gated
/// (Unit.cpp:1020). Documented limits: honor keeps its own snapshot of the tapper's group at the first hit; the "xp_multiplier 0" and
/// "UNIT_STATE_NO_KILL_REWARD" terms of IsHonorOrXPTarget have no data either.
/// </para>
/// </summary>
public sealed class HonorKillRewards
{
    private readonly HonorService _honor;
    private readonly Func<Player, RewardGroup?> _groups;
    private readonly GroupRewardOptions _range;
    private readonly Func<Unit, bool> _noPvpCredit;
    private readonly HashSet<MapCombat> _attached = [];
    private readonly ConditionalWeakTable<Creature, Tap> _taps = new();

    private sealed record Tap(ObjectGuid Player, RewardGroup? Group, ulong Revision);

    /// <param name="honor">The honor owner.</param>
    /// <param name="groups">A player's reward group (null for solo).</param>
    /// <param name="range">The group reward distance options.</param>
    /// <param name="noPvpCredit">Whether a unit carries the Honorless Target aura (SPELL_AURA_NO_PVP_CREDIT); null means never.</param>
    public HonorKillRewards(HonorService honor, Func<Player, RewardGroup?> groups, GroupRewardOptions? range = null, Func<Unit, bool>? noPvpCredit = null)
    {
        _honor = honor ?? throw new ArgumentNullException(nameof(honor));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _range = range ?? new GroupRewardOptions();
        _noPvpCredit = noPvpCredit ?? (_ => false);
    }

    /// <summary>Subscribe to a map's damage and kill events.</summary>
    public void Attach(MapCombat combat)
    {
        ArgumentNullException.ThrowIfNull(combat);
        if (_attached.Add(combat))
        {
            combat.DamageTaken += RecordDamage;
            combat.UnitKilled += OnUnitKilled;
        }
    }

    /// <summary>Unsubscribe from a map.</summary>
    public void Detach(MapCombat combat)
    {
        ArgumentNullException.ThrowIfNull(combat);
        if (_attached.Remove(combat))
        {
            combat.DamageTaken -= RecordDamage;
            combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>Record damage a player took, under the controlling player of the attacker (Unit::UnitDamaged).</summary>
    public void RecordDamage(Unit attacker, Unit victim, uint damage)
    {
        if (victim is Creature tapped)
        {
            RecordTap(attacker, tapped);
            return;
        }

        if (victim is not Player player || ReferenceEquals(attacker, victim) || _honor.For(player) is not { } state)
        {
            return;
        }

        // Unit.cpp:791-794: UnitDamaged records the attacker's affecting player (a pet's owner; a charmed creature's own owner, if any).
        Player? owner = attacker.GetAffectingPlayer() ?? DuelRules.ControllingPlayer(attacker);
        state.Ledger.Record(owner?.Guid.Value ?? 0, damage, _honor.Clock.UnixMilliseconds);
    }

    /// <summary>
    /// Unit::DealDamage (Unit.cpp:800-819): a creature that is not a pet is tapped by the first player (or what a player owns or charms)
    /// to damage it; an owner's active pet holds the tap provisionally (<see cref="KillRewards.TapGuid"/>) until the owner or a member of
    /// the tap's group hits.
    /// </summary>
    private void RecordTap(Unit attacker, Creature creature)
    {
        if (creature.IsPet || KillRewards.CreditedPlayer(attacker) is not { } owner)
        {
            return;
        }

        // CreatureAI::EnterEvadeMode clears the recipient without requiring full health.
        // A heal to full during the same fight is not a tap reset.
        if (_taps.TryGetValue(creature, out Tap? previous) && previous.Revision != creature.LootTapRevision)
        {
            _taps.Remove(creature);
        }

        if (!_taps.TryGetValue(creature, out Tap? held))
        {
            _taps.Add(creature, new Tap(KillRewards.TapGuid(attacker, owner), _groups(owner), creature.LootTapRevision));
        }
        else if (KillRewards.TakesOverPetTap(held.Player, attacker, held.Group?.Members.Contains(owner.Guid) == true))
        {
            _taps.AddOrUpdate(creature, new Tap(owner.Guid, _groups(owner) ?? held.Group, creature.LootTapRevision));
        }
    }

    /// <summary>React to a death: honor for a player victim from its damage history, for a creature victim from the tap (Unit::Kill).</summary>
    public void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is Player deadPlayer)
        {
            RewardHonorOnDeath(deadPlayer);
            return;
        }

        if (victim is not Creature creature || creature.Map is not { } map)
        {
            return;
        }

        Tap? tap = null;
        if (_taps.TryGetValue(creature, out Tap? held))
        {
            tap = held.Revision == creature.LootTapRevision ? held : null; // a reset (evade, respawn) since the tap: stale
            _taps.Remove(creature);
        }

        Player? tapper;
        RewardGroup? group = null;
        if (!creature.KillCreditAllowed)
        {
            // Unit.cpp:1007-1008: the tap was dropped and pGroupTap is never set; RewardSinglePlayerAtKill for the killing blow's player.
            tapper = KillRewards.CreditedPlayer(killer);
        }
        else if (tap is null)
        {
            // Unit.cpp:984, 998-999: nobody tapped; pPlayerTap is the killer's player and pGroupTap his current group.
            tapper = KillRewards.CreditedPlayer(killer);
            group = CurrentGroupOf(tapper);
        }
        else if (!tap.Player.IsPlayer)
        {
            return; // a provisional pet tap: pPlayerTap = nullptr gates loot and both reward paths (Unit.cpp:995-997, 1020)
        }
        else
        {
            // Unit.cpp:989-999: the original recipient (the killer's player when he left the map), the tap's group while it has
            // members, else his current group.
            tapper = map.FindPlayer(tap.Player) ?? KillRewards.CreditedPlayer(killer);
            group = tap.Group is { Members.Count: > 0 } ? tap.Group : CurrentGroupOf(tapper);
        }

        if (tapper is null || !ReferenceEquals(tapper.Map, map))
        {
            return;
        }

        RewardCreatureKill(tapper, group, creature);
    }

    private RewardGroup? CurrentGroupOf(Player? player)
        => player is not null && _groups(player) is { } current && current.Members.Contains(player.Guid) ? current : null;

    /// <summary>Player::IsHonorOrXPTarget: the victim is above the gray level and is no totem or pet.</summary>
    public static bool IsHonorOrXpTarget(Player rewarded, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(rewarded);
        ArgumentNullException.ThrowIfNull(victim);
        if (victim.Level <= ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel(rewarded.Level))
        {
            return false;
        }

        return victim is not Creature creature || (!TotemQuery.IsTotem(creature) && !creature.IsPet);
    }

    /// <summary>Player::RewardHonorOnDeath: share the honor of a dead player over the players who damaged it.</summary>
    public void RewardHonorOnDeath(Player victim)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (_honor.For(victim) is not { } state || _noPvpCredit(victim) || victim.Map is not { } map)
        {
            return; // Honorless Target: the history is kept, as vmangos returns before clearing it
        }

        IReadOnlyDictionary<ulong, uint> history = state.Ledger.Snapshot(_honor.Clock.UnixMilliseconds);
        uint totalDamage = 0;
        var perGroup = new Dictionary<ulong, (RewardGroup Group, uint Damage)>();
        var perLoner = new Dictionary<Player, uint>();
        foreach ((ulong guid, uint damage) in history.OrderBy(h => h.Key))
        {
            totalDamage += damage;
            if (guid == 0 || map.FindPlayer(new ObjectGuid(guid)) is not { } attacker)
            {
                continue;
            }

            if (_groups(attacker) is { } group && group.Members.Count > 0)
            {
                // A group is identified by its smallest member (the registry exposes no group id).
                ulong key = group.Members.Min(m => m.Value);
                perGroup[key] = (group, (perGroup.TryGetValue(key, out var held) ? held.Damage : 0) + damage);
            }
            else if (GroupRewardRange.IsAtGroupRewardDistance(attacker, victim, _range) && attacker.IsAlive && attacker.Team != victim.Team)
            {
                perLoner[attacker] = perLoner.GetValueOrDefault(attacker) + damage;
            }
        }

        if (totalDamage == 0)
        {
            state.Ledger.Clear();
            return;
        }

        foreach ((RewardGroup group, uint groupDamage) in perGroup.Values)
        {
            var rewarded = new List<Player>();
            foreach (ObjectGuid member in group.Members)
            {
                if (map.FindPlayer(member) is { } player
                    && GroupRewardRange.IsAtGroupRewardDistance(player, victim, _range) && player.IsAlive && player.Team != victim.Team)
                {
                    rewarded.Add(player);
                }
            }

            if (rewarded.Count == 0)
            {
                continue;
            }

            // The original's float chain: damage * group rate / total damage / members.
            float honorRate = groupDamage;
            honorRate *= ExperienceFormulas.GroupRate(rewarded.Count);
            honorRate /= totalDamage;
            honorRate /= rewarded.Count;
            foreach (Player player in rewarded)
            {
                if (IsHonorOrXpTarget(player, victim))
                {
                    Award(player, victim, _honor.KillPoints(player, victim, 1) * honorRate);
                }
            }
        }

        foreach ((Player player, uint damage) in perLoner)
        {
            if (IsHonorOrXpTarget(player, victim))
            {
                Award(player, victim, _honor.KillPoints(player, victim, 1) * damage / (float)totalDamage);
            }
        }

        state.Ledger.Clear();
    }

    /// <summary>
    /// Group::RewardGroupAtKill_helper (Group.cpp:2295-2300) for a creature victim: RewardHonor for every living member of
    /// <paramref name="group"/> at reward distance; without a group, Player::RewardSinglePlayerAtKill (Player.cpp:19965) for the tapper alone.
    /// </summary>
    private void RewardCreatureKill(Player tapper, RewardGroup? group, Creature victim)
    {
        if (group is null)
        {
            RewardHonor(tapper, victim);
            return;
        }

        Map map = victim.Map!;
        foreach (ObjectGuid guid in group.Members)
        {
            if (map.FindPlayer(guid) is { IsAlive: true } member && GroupRewardRange.IsAtGroupRewardDistance(member, victim, _range))
            {
                RewardHonor(member, victim);
            }
        }
    }

    /// <summary>Player::RewardHonor for a creature victim: dishonor for a civilian below gray, honor for a racial leader.</summary>
    public void RewardHonor(Player member, Creature victim)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(victim);
        if (_noPvpCredit(victim))
        {
            return;
        }

        if (victim.Template.Civilian && !IsHonorOrXpTarget(member, victim))
        {
            if (_honor.Options.DishonorableKills)
            {
                _honor.Add(member, HonorKillPoints.Dishonorable(member.Level), HonorKind.Dishonorable, victim);
            }

            return;
        }

        if (_honor.IsRacialLeader(victim))
        {
            _honor.Add(member, HonorKillPoints.RacialLeaderHonor, HonorKind.Honorable, victim);
        }
    }

    // uint32(points): nothing is added for a share that truncates to zero.
    private void Award(Player player, Player victim, float points)
    {
        uint whole = float.IsFinite(points) && points > 0f ? (uint)points : 0u;
        if (whole != 0)
        {
            _honor.Add(player, whole, HonorKind.Honorable, victim);
        }
    }
}
