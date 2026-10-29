using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The two pieces of the generic DUMMY aura cases (<c>Generic/GenericDummyAuraCases.cs</c>) that need the spell system's private helpers: the
/// creature feign death of Permanent Feign Death and the leash distance of the Alterac Valley ram and wolf.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Unit::SetFeignDeath(apply) with the default arguments (SpellAuras.cpp:2103-2112 calls it for a creature target of 29266 Permanent
    /// Feign Death, Unit.cpp:9228-9278): on apply the unit stops, spells aimed at it are interrupted, combat stops, stealth and invisibility end, the
    /// hostile threat references go and the dead dynamic flag is set; on remove the flag goes. The state is not an aura of the unit's own
    /// (the unit keeps no resist roll: <c>success</c> is true), so <see cref="IsFeigningDeath"/> does not report it.
    /// </summary>
    internal void SetCreatureFeignDeath(Unit target, bool apply)
    {
        if (!apply)
        {
            target.RemoveFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);
            return;
        }

        MovementInfo movement = target.Movement;
        (movement.X, movement.Y, movement.Z, movement.Orientation) = (target.X, target.Y, target.Z, target.Orientation);
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.TurnLeft | MovementFlags.TurnRight);
        target.ApplyMovement(movement, NowMs);
        if (target is Creature creature)
        {
            creature.System?.StopMoving(creature);
        }

        InterruptSpellsCastedOn(target);
        target.Map?.Combat.CombatStop(target);
        RemoveHoldersWithAuraInterrupt(target, StealthInvisCancelsFlag);
        foreach (Unit attacker in target.Combat.ThreatenedBy.ToArray())
        {
            attacker.Combat.Threat.Remove(target); // HostileRefManager::deleteReferences
        }

        target.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);

        // InterruptNonMeleeSpells(true): the unit's own cast ends too.
        if (GetState(target.Guid)?.CurrentCast is { } current)
        {
            Cancel(current);
        }
    }

    /// <summary>vmangos <c>pRam->IsWithinDistInMap(pPlayer, 50.0f)</c> of the Frostwolf and Alterac Ram auras' tick (SpellAuras.cpp:6471-6480).</summary>
    internal static bool IsWithinLeash(Unit creature, Unit player, float distance) => IsWithinDistInMap(creature, player, distance);
}
