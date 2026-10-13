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
    /// vmangos HostileReference::addThreat (Threat/ThreatManager.cpp:117-121): a pet's owner is added only when
    /// <c>pVictimOwner->IsTargetableBy(getSource()->getOwner())</c>. That check's flag and immunity half: a spawning,
    /// taxi, non-attackable or non-selectable owner is not a target, an owner immune to the attacker's side is not, and an
    /// attacker immune to the owner's side is not. Feign death and stealth detection are not read here.
    /// </summary>
    public static bool CanPullPetOwner(Unit owner, Unit attacker)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(attacker);
        if (!owner.IsInWorld || !owner.IsAlive || owner is Player { IsGameMaster: true })
        {
            return false;
        }

        const UnitFlags notAttackable = UnitFlags.NonAttackable2 | UnitFlags.Spawning | UnitFlags.TaxiFlight
            | UnitFlags.NotAttackable1 | UnitFlags.NotSelectable;
        if ((owner.UnitFlags & notAttackable) != 0)
        {
            return false;
        }

        if (attacker.IsCharmerOrOwnerPlayerOrPlayerItself)
        {
            if ((owner.UnitFlags & UnitFlags.ImmuneToPlayer) != 0)
            {
                return false;
            }
        }
        else if ((owner.UnitFlags & UnitFlags.ImmuneToNpc) != 0)
        {
            return false;
        }

        if (owner.IsCharmerOrOwnerPlayerOrPlayerItself)
        {
            if ((attacker.UnitFlags & UnitFlags.ImmuneToPlayer) != 0)
            {
                return false;
            }
        }
        else if ((attacker.UnitFlags & UnitFlags.ImmuneToNpc) != 0)
        {
            return false;
        }

        return true;
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
