using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Pets.Control;

/// <summary>
/// The charms and possessions of one map (docs/areas/unit-control.md): which unit controls which, the cleanup vmangos does in
/// <c>Unit::RemoveFromWorld</c> (<c>Uncharm</c>, Unit.cpp:8276-8282) and <c>Player::TeleportTo</c> (<c>RemoveCharmAuras</c>,
/// Player.cpp:1819 and :2061) when a controller or a controlled unit leaves the map, and vmangos <c>PlayerControlledAI</c>
/// (AI/PlayerAI.cpp:173-330) for a charmed player. A link is added by <see cref="CharmService"/> when a control aura lands and removed
/// when it ends.
/// <para>Thread affinity: world thread.</para>
/// </summary>
[DefaultMapUpdater(Order = 160)]
public sealed class MapUnitControl : IMapUpdater
{
    private readonly Map _map;
    private readonly Dictionary<Unit, Link> _links = new(ReferenceEqualityComparer.Instance);
    private readonly List<Pending> _pending = [];
    private readonly List<Pending> _due = [];
    private bool _combatSubscribed;

    /// <summary>
    /// The scheduled actions of players between two maps (a far teleport): vmangos keeps them on the unit (<c>m_Events</c>, which only
    /// CleanupsBeforeDelete clears, Unit.cpp:8305), so they go with the player and resume on the map it enters. A player that never enters
    /// another map (logout in transit) takes them with it, as vmangos deletes the unit's queue with the unit.
    /// </summary>
    private static readonly ConditionalWeakTable<Player, List<Pending>> s_inTransit = new();

    private sealed record Link(CharmService Service, ObjectGuid Controller);

    private sealed class Pending(Unit unit, uint delayMs, Action action)
    {
        public Unit Unit { get; } = unit;

        public uint RemainingMs { get; set; } = delayMs;

        public Action Action { get; } = action;
    }

    internal MapUnitControl(Map map, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(world);
        _map = map;
    }

    /// <summary>How many units of this map are charmed or possessed.</summary>
    public int Count => _links.Count;

    /// <summary>The charmed or possessed units of this map.</summary>
    public IReadOnlyCollection<Unit> Controlled => _links.Keys;

    /// <summary>The GUID of the unit that controls <paramref name="unit"/>, or empty.</summary>
    public ObjectGuid ControllerOf(Unit unit) => _links.TryGetValue(unit, out Link? link) ? link.Controller : default;

    internal void Track(CharmService service, Unit controller, Unit target)
    {
        _links[target] = new Link(service, controller.Guid);
        SubscribeCombat();
    }

    /// <summary>
    /// vmangos PetAI::OwnerAttacked / OwnerAttackedBy for charmed creatures (the pets of the summon service hear them through
    /// <see cref="PetMapSystem"/>): this map's damage event, subscribed once at the first charm. The subscription is per map (the map's
    /// combat and this registry live and die together), so nothing world-wide keeps an unloaded instance map alive.
    /// </summary>
    private void SubscribeCombat()
    {
        if (!_combatSubscribed && _map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.DamageDealt += OnDamageDealt;
            _combatSubscribed = true;
        }
    }

    private void OnDamageDealt(Unit attacker, Unit victim, uint damage, bool direct, bool meleeDamage)
    {
        if (_links.Count == 0)
        {
            return;
        }

        foreach (Unit controlled in _links.Keys.ToArray())
        {
            if (controlled is not Creature { AI: PetAI ai, Summon: null } charmed || !charmed.IsAlive)
            {
                continue;
            }

            if (charmed.CharmerGuid == victim.Guid)
            {
                ai.OwnerAttackedBy(attacker);
            }
            else if (charmed.CharmerGuid == attacker.Guid)
            {
                ai.OwnerAttacked(victim);
            }
        }
    }

    internal void Untrack(Unit target) => _links.Remove(target);

    /// <summary>
    /// vmangos <c>m_Events.AddLambdaEventAtOffset</c> for a unit of this map: <paramref name="action"/> runs in the map update once
    /// <paramref name="delayMs"/> have passed (Spirit of Redemption's follow-up steps). Like vmangos's queue, which belongs to the unit, the
    /// action of a player follows it to the next map it enters (<see cref="OnPlayerRemoved"/>, <see cref="OnPlayerAdding"/>); the
    /// action of another unit is dropped when the unit has left the map (despawned or unloaded).
    /// </summary>
    public void Schedule(Unit unit, uint delayMs, Action action)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(action);
        _pending.Add(new Pending(unit, delayMs, action));
    }

    public void Update(Map map, uint diffMs)
    {
        RunDue(diffMs);
        if (_links.Count == 0)
        {
            return;
        }

        foreach ((Unit controlled, Link link) in _links.ToArray())
        {
            if (!_links.ContainsKey(controlled))
            {
                continue; // released by an earlier link this tick
            }

            // The controlled unit left the map (despawned, unloaded, teleported): vmangos removes its auras with AURA_REMOVE_BY_DELETE,
            // which only does the controller's half.
            if (!ReferenceEquals(controlled.Map, _map))
            {
                _links.Remove(controlled);
                if (_map.FindObject(link.Controller) is Player controllerPlayer && controllerPlayer.CharmGuid == controlled.Guid)
                {
                    link.Service.ReleaseController(controllerPlayer, controlled);
                }

                continue;
            }

            // The controller left the map: vmangos Uncharm in its RemoveFromWorld releases the target.
            if (_map.FindObject(link.Controller) is not Unit controller)
            {
                link.Service.RemoveCharmAuras(controlled);
                link.Service.Spells?.RemoveAurasByType(controlled, Spells.AuraType.ModPossessPet);
                if (_links.ContainsKey(controlled))
                {
                    _links.Remove(controlled);
                    link.Service.ForceRelease(controlled);
                }

                continue;
            }

            if (controlled is Player charmedPlayer && !charmedPlayer.IsPossessedState)
            {
                UpdateCharmedPlayer(link.Service, charmedPlayer, controller);
            }
        }
    }

    /// <summary>
    /// The player leaves the map (logout, far teleport): what it charms is released (vmangos Unit::RemoveFromWorld → Uncharm) and a charm on
    /// it ends (vmangos Player::TeleportTo → RemoveCharmAuras).
    /// </summary>
    public void OnPlayerRemoved(Map map, Player player)
    {
        CarryPending(player);
        if (_links.Count == 0)
        {
            return;
        }

        foreach ((Unit controlled, Link link) in _links.ToArray())
        {
            if (ReferenceEquals(controlled, player))
            {
                link.Service.RemoveCharmAuras(player);
                if (_links.Remove(player))
                {
                    link.Service.ForceRelease(player);
                }
            }
            else if (link.Controller == player.Guid)
            {
                link.Service.Uncharm(player);
                if (_links.Remove(controlled))
                {
                    link.Service.ReleaseController(player, controlled);
                    link.Service.ForceRelease(controlled);
                }
            }
        }
    }

    /// <summary>The player enters this map: what it carried from the map it left resumes here, with the time it still had.</summary>
    public void OnPlayerAdding(Map map, Player player)
    {
        if (s_inTransit.TryGetValue(player, out List<Pending>? carried))
        {
            s_inTransit.Remove(player);
            _pending.AddRange(carried);
        }
    }

    /// <summary>Move <paramref name="player"/>'s scheduled actions off this map, onto the player, until it enters a map again.</summary>
    private void CarryPending(Player player)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        List<Pending>? carried = null;
        foreach (Pending pending in _pending)
        {
            if (ReferenceEquals(pending.Unit, player))
            {
                (carried ??= s_inTransit.GetValue(player, static _ => [])).Add(pending);
            }
        }

        if (carried is not null)
        {
            _pending.RemoveAll(p => ReferenceEquals(p.Unit, player));
        }
    }

    /// <summary>
    /// A due action of a player that is no longer on this map without having been removed through it: it runs on the player's map
    /// (next update there), or waits on the player while it is between maps.
    /// </summary>
    private static void Redirect(Player player, Pending pending)
    {
        pending.RemainingMs = 0;
        if (player.Map?.FindUpdater<MapUnitControl>() is { } control)
        {
            control._pending.Add(pending);
        }
        else
        {
            s_inTransit.GetValue(player, static _ => []).Add(pending);
        }
    }

    /// <summary>
    /// vmangos PlayerControlledAI::UpdateAI (AI/PlayerAI.cpp:249-330) and UpdateTarget (:173-215), for what this server can do with a
    /// player it does not move: the charm ends when a player controller is dead or a creature controller is dead or out of combat; the
    /// victim is the charmed player's own (a passive charm) or else its controller's (a player controller) or one of the creature
    /// controller's threat targets; a hostile victim is attacked in melee and both sides enter combat. The charmed player is not moved
    /// towards its victim (vmangos MoveChase: players have no server movement generator here) and casts no spells (the usable-spell list).
    /// </summary>
    private void UpdateCharmedPlayer(CharmService service, Player me, Unit controller)
    {
        if (!me.IsAlive)
        {
            return;
        }

        bool controllerLost = controller is Player ? !controller.IsAlive : !controller.IsAlive || !controller.Combat.IsInCombat;
        if (controllerLost)
        {
            service.RemoveCharmAuras(me);
            service.RemoveCharmAuras(controller);
            return;
        }

        MapCombat combat = _map.Combat;
        Unit? victim;
        if (controller is Player)
        {
            CharmInfo? info = me.GetCharmInfo();
            victim = me.Combat.Victim;
            if (info?.ReactState != ReactState.Passive && (victim is null || ReferenceEquals(victim, controller)))
            {
                victim = controller.Combat.Victim;
            }
        }
        else
        {
            victim = me.Combat.Victim is { } own && combat.Hooks.CanAttack(me, own) ? own : RandomThreatTarget(controller);
        }

        if (victim is null || ReferenceEquals(victim, me) || !victim.IsAlive || !combat.Hooks.IsHostileTo(me, victim))
        {
            return;
        }

        // UpdateTarget: never against a unit with the same charmer, nor while feared or polymorphed.
        if ((victim.IsCharmed && victim.CharmerGuid == me.CharmerGuid) || (me.UnitFlags & (UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            combat.AttackStop(me);
            service.Spells?.InterruptNonMeleeSpells(me);
            return;
        }

        if (!ReferenceEquals(me.Combat.Victim, victim))
        {
            combat.Attack(me, victim, melee: true);
        }

        combat.SetInCombatState(me, 0);
        combat.SetInCombatState(victim, 0);
        combat.SetInCombatState(controller, 0);
    }

    private void RunDue(uint diffMs)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (Pending pending in _pending)
        {
            pending.RemainingMs = pending.RemainingMs > diffMs ? pending.RemainingMs - diffMs : 0;
            if (pending.RemainingMs == 0)
            {
                _due.Add(pending);
            }
        }

        _pending.RemoveAll(static p => p.RemainingMs == 0);
        foreach (Pending pending in _due)
        {
            if (ReferenceEquals(pending.Unit.Map, _map))
            {
                pending.Action();
            }
            else if (pending.Unit is Player player)
            {
                Redirect(player, pending);
            }
        }

        _due.Clear();
    }

    /// <summary>vmangos Creature::SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0): a random entry of the controller's threat list.</summary>
    private static Unit? RandomThreatTarget(Unit controller)
    {
        IReadOnlyList<ThreatEntry> entries = controller.Combat.Threat.Entries;
        return entries.Count == 0 ? controller.Combat.Victim : entries[Random.Shared.Next(entries.Count)].Target;
    }
}
