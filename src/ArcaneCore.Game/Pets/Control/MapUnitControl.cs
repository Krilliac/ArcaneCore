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

    private sealed record Link(CharmService Service, ObjectGuid Controller);

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

    internal void Track(CharmService service, Unit controller, Unit target) => _links[target] = new Link(service, controller.Guid);

    internal void Untrack(Unit target) => _links.Remove(target);

    public void Update(Map map, uint diffMs)
    {
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

    /// <summary>vmangos Creature::SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0): a random entry of the controller's threat list.</summary>
    private static Unit? RandomThreatTarget(Unit controller)
    {
        IReadOnlyList<ThreatEntry> entries = controller.Combat.Threat.Entries;
        return entries.Count == 0 ? controller.Combat.Victim : entries[Random.Shared.Next(entries.Count)].Target;
    }
}
