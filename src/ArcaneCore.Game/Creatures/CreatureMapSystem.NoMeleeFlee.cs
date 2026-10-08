using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The panic flight of a NO_MELEE_FLEE creature (static flag 0x00100000): cmangos Unit::SetInCombatWithVictim
/// (Entities/Unit.cpp:7993-7998) sends a creature with the flag running for 30 s (DoFlee, SetInPanic) when a player or a player's
/// pet engages it, and CreatureAI::TimedFleeingEnded (AI/BaseAI/CreatureAI.cpp:254-258) evades it when the flight is over.
/// Switch: <see cref="CreatureOptions.NoMeleeFleeOnAggro"/>, off by default (vmangos only takes the melee away).
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// cmangos Unit.cpp:7994: the flag, not created by a spell (a summon), not rooted (or sessile), not already in panic or feared, not
    /// casting, and the enemy is player controlled. Called once per fight, after the aggro hook (a spell cast on aggro wins).
    /// </summary>
    private void TryStartNoMeleeFlee(Creature creature, Unit enemy)
    {
        CreatureBehaviourFlags behaviour = creature.Template.Behaviour;
        if (!_options.NoMeleeFleeOnAggro || (behaviour & CreatureBehaviourFlags.NoMeleeFlee) == 0 || creature.Summon is not null
            || (behaviour & CreatureBehaviourFlags.Sessile) != 0 || creature.Movement.HasFlag(MovementFlags.Root)
            || (creature.UnitFlags & UnitFlags.Fleeing) != 0 || (_ai.Spells?.IsCasting(creature) ?? false) || !IsPlayerControlled(enemy))
        {
            return;
        }

        creature.Motion.MoveFleeing(null, _options.NoMeleeFleeMs); // SetInPanic: SetFleeing with the creature itself as the source
        creature.InNoMeleePanic = true;
    }

    /// <summary>cmangos ORDER_CRITTER_FLEE: when the panic flight is over the creature evades (TimedFleeingEnded).</summary>
    private void CheckNoMeleePanicEnded(Creature creature)
    {
        if (!creature.InNoMeleePanic || creature.Motion.ActiveTypes.Contains(MovementGeneratorType.Fleeing))
        {
            return;
        }

        creature.InNoMeleePanic = false;
        if (creature.IsAlive && creature.Combat.IsInCombat)
        {
            EnterEvadeMode(creature);
        }
    }

    /// <summary>cmangos Unit::IsPlayerControlled: a player, or a unit a player owns or charms.</summary>
    private static bool IsPlayerControlled(Unit unit) => unit.IsCharmerOrOwnerPlayerOrPlayerItself;
}
