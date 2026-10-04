using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
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
/// tap is dropped when the creature is back at full health (what an evade or a respawn does, CreatureAI.cpp:344, Creature.cpp:2306), and the tapper or
/// every living member of its group in range is rewarded, whoever landed the killing blow. A creature nobody tapped (killed by one hit that is its
/// first damage, or by non-player damage) rewards the killer's controlling player, which is the original's pPlayerTap fallback. Documented limits:
/// the tap is a snapshot of the tapper's group taken at the first hit (the tapper's current group is preferred while it still contains the tapper),
/// "IsLootAllowedDueToDamageOrigin" (the share of damage done by players) and the pet to owner tap swap have no data here, and a creature healed
/// to full health mid-fight loses its tap; the "xp_multiplier 0" and "UNIT_STATE_NO_KILL_REWARD" terms of IsHonorOrXPTarget have no data either.
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

    private sealed record Tap(ObjectGuid Player, RewardGroup? Group);

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

        Player? owner = DuelRules.ControllingPlayer(attacker);
        state.Ledger.Record(owner?.Guid.Value ?? 0, damage, _honor.Clock.UnixMilliseconds);
    }

    /// <summary>Unit::DealDamage (Unit.cpp:804-807): a creature that is not a pet is tapped by the first player to damage it.</summary>
    private void RecordTap(Unit attacker, Creature creature)
    {
        if (creature.IsPet || DuelRules.ControllingPlayer(attacker) is not { } owner)
        {
            return;
        }

        // Damage dealt at full health starts a new fight: an evade (CreatureAI.cpp:344) or a respawn (Creature.cpp:2306) cleared the old tap.
        if (creature.Health >= creature.MaxHealth)
        {
            _taps.Remove(creature);
        }

        if (!_taps.TryGetValue(creature, out _))
        {
            _taps.Add(creature, new Tap(owner.Guid, _groups(owner)));
        }
    }

    /// <summary>React to a death: honor for a player victim from its damage history, for a creature victim from the killer.</summary>
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
            tap = held;
            _taps.Remove(creature);
        }

        Player? tapper = tap is null
            ? (killer is null ? null : DuelRules.ControllingPlayer(killer))
            : map.FindPlayer(tap.Player);
        RewardCreatureKill(tapper, tap?.Group, creature);
    }

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
    /// The honor of a creature kill for <paramref name="tapper"/> (the player the creature's honor belongs to): the player alone, or every
    /// living member of its group within reach (Group::RewardGroupAtKill_helper calls RewardHonor for living members).
    /// </summary>
    public void RewardCreatureKill(Player tapper, Creature victim)
    {
        ArgumentNullException.ThrowIfNull(tapper);
        ArgumentNullException.ThrowIfNull(victim);
        RewardCreatureKill(tapper, null, victim);
    }

    // tapper null: the tapping player is gone, only the tap-time group is left to reward.
    private void RewardCreatureKill(Player? tapper, RewardGroup? tapGroup, Creature victim)
    {
        if (victim.Map is not { } map || (tapper is not null && !ReferenceEquals(tapper.Map, map)))
        {
            return;
        }

        RewardGroup? current = tapper is null ? null : _groups(tapper);
        RewardGroup? group = tapper is not null && current is not null && current.Members.Contains(tapper.Guid) ? current : tapGroup;
        if (group is null)
        {
            if (tapper is not null)
            {
                RewardHonor(tapper, victim);
            }

            return;
        }

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
