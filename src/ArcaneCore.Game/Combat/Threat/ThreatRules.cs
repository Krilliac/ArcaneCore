using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Combat.Threat;

/// <summary>Who may hold threat and when an entry is reachable (vmangos Unit::CanHaveThreatList and HostileReference::updateOnlineStatus).</summary>
public static class ThreatRules
{
    /// <summary>
    /// vmangos Unit::CanHaveThreatList (Objects/Unit.cpp:7377-7405): only a living creature, and not a totem, not a pet of a
    /// player, not a unit charmed by a player, not a NO_THREAT_LIST creature. A player never holds a list. Units that are
    /// neither players nor <see cref="Creature"/> (the test stand-ins of the combat kit) count as plain creatures.
    /// </summary>
    public static bool CanHaveThreatList(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is Player || !unit.IsAlive)
        {
            return false;
        }

        if (unit is not Creature creature)
        {
            return unit.CharmerGuid.IsEmpty || !unit.CharmerGuid.IsPlayer;
        }

        return !creature.IsTotem
            && !(creature.IsPet && creature.OwnerGuid.IsPlayer)
            && !creature.CharmerGuid.IsPlayer
            && (creature.Template.Behaviour & CreatureBehaviourFlags.NoThreatList) == 0;
    }

    /// <summary>
    /// vmangos ThreatManager::addThreat (Threat/ThreatManager.cpp:412-421): assist threat is dropped while the owner is
    /// confused or fleeing. The "stunned by a damage-breakable aura" and "isolated" clauses need aura-holder data the spell
    /// lane does not publish yet and are not applied (documented limit, docs/areas/threat.md).
    /// </summary>
    public static bool IsAssistThreatSuppressed(Unit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return (owner.UnitFlags & (UnitFlags.Confused | UnitFlags.Fleeing)) != 0;
    }

    /// <summary>
    /// vmangos HostileReference::updateOnlineStatus (Threat/ThreatManager.cpp:142-146): a game master target is offline. The
    /// "taxi flying" clause waits for a taxi primitive on <see cref="Player"/> (documented limit).
    /// </summary>
    public static bool IsTargetUnreachable(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is Player { IsGameMaster: true };
    }
}
