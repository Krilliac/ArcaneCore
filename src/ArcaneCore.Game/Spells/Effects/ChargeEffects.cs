using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The movement effects of a spell: SPELL_EFFECT_CHARGE (96), SPELL_EFFECT_LEAP (29, Blink) and SPELL_EFFECT_TELEPORT_UNITS_FACE_CASTER (43),
/// after mangoszero <c>Spell::EffectCharge</c>, <c>EffectLeapForward</c> and <c>EffectTeleUnitsFaceCaster</c>
/// (SpellEffectObjectCombat.cpp:944, :1245; SpellEffectSkillEnchantPet.cpp:413). The geometry is <see cref="ForcedMovement"/>.
/// <list type="bullet">
/// <item>Charge: the caster goes to the contact point at the target's edge (<see cref="ForcedMovement.ContactPoint"/>) along one spline at 24 yards
/// per second; a creature target stops moving; a negative spell starts the caster's melee attack on the target. The rage and the stun a
/// warrior's Charge grants are not this effect: the spell's other effects (a trigger spell, an energize) run as every cast's do, after it.</item>
/// <item>Leap: the caster is teleported by <see cref="SpellSystem.Teleports"/> to <see cref="ForcedMovement.LeapDestination"/>, the effect's radius
/// ahead of it, stopping before a collision or an edge. For a player that is a near teleport (the client acknowledges it).</item>
/// <item>Teleport units face caster: the target is teleported to the spell's destination, or in front of the caster by the effect radius, and
/// turned to face the caster.</item>
/// </list>
/// The cast checks are the effect loop of vmangos Spell::CheckCast (SpellChecks.cpp:1124, :1380): a charge fails while the caster is rooted (ROOTED),
/// a leap or face-caster teleport on a taxi (NOT_ON_TAXI) or a transport (NOT_ON_TRANSPORT). The battleground "not before the start" rule
/// (TRY_AGAIN) is the battleground area's.
/// </summary>
public sealed class ChargeEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.Charge, EffectCharge);
        system.RegisterEffect(SpellEffectName.Leap, EffectLeapForward);
        system.RegisterEffect(SpellEffectName.TeleportUnitsFaceCaster, EffectTeleportUnitsFaceCaster);
        system.RegisterCastCheck(new MovementEffectCastCheck());
    }

    private static void EffectCharge(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        Unit target = context.Target;
        if (ReferenceEquals(caster, target) || caster.Map is not { } map || !ReferenceEquals(map, target.Map))
        {
            return;
        }

        Vector3 destination = ForcedMovement.ContactPoint(target, caster, ForcedMovement.ChargeContactGap);
        if (target is Creatures.Creature creature)
        {
            map.FindUpdater<Creatures.CreatureMapSystem>()?.StopMoving(creature);
        }

        ForcedMovement.LaunchSpline(caster, ForcedMovement.ChargePath(caster, destination), ForcedMovement.ChargeSpeed);

        // "not all charge effects used in negative spells"
        if (!context.Spell.IsPositiveSpell(context.System.Store.Get))
        {
            map.Combat.Attack(caster, target);
        }
    }

    private static void EffectLeapForward(SpellEffectContext context)
    {
        Unit target = context.Target;
        if (target.Map is not { } map)
        {
            return;
        }

        Vector3 destination = ForcedMovement.LeapDestination(target, context.Effect.Radius);
        context.System.Teleports.Teleport(target, map.MapId, destination.X, destination.Y, destination.Z, target.Orientation);
    }

    private static void EffectTeleportUnitsFaceCaster(SpellEffectContext context)
    {
        Unit target = context.Target;
        Unit caster = context.Caster;
        if ((target.UnitFlags & UnitFlags.TaxiFlight) != 0 || target.Map is not { } map || !ReferenceEquals(map, caster.Map))
        {
            return;
        }

        Vector3 destination = context.Cast.Targets.HasDest
            ? new Vector3(context.Cast.Targets.Dest.X, context.Cast.Targets.Dest.Y, context.Cast.Targets.Dest.Z)
            : ForcedMovement.PointInFront(caster, target, context.Effect.Radius);
        float facing = caster.Orientation + MathF.PI;
        if (facing >= 2 * MathF.PI)
        {
            facing -= 2 * MathF.PI;
        }

        context.System.Teleports.Teleport(target, map.MapId, destination.X, destination.Y, destination.Z, facing);
    }

    /// <summary>The CheckCast effect loop for the movement effects (strict and landing checks both run it; it reads only the caster's state).</summary>
    private sealed class MovementEffectCastCheck : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Final;

        public int Order => SpellCastCheckOrder.TargetAuraState + 50;

        public SpellCastResult Check(in SpellCastCheckContext context)
        {
            Unit caster = context.Caster;
            foreach (SpellEffectInfo effect in context.Spell.Effects)
            {
                switch (effect.Effect)
                {
                    case SpellEffectName.Charge when IsRooted(caster):
                        return SpellCastResult.Rooted;
                    case SpellEffectName.Leap or SpellEffectName.TeleportUnitsFaceCaster:
                        if ((caster.UnitFlags & UnitFlags.TaxiFlight) != 0)
                        {
                            return SpellCastResult.NotOnTaxi;
                        }

                        // "Blink has leap first and then removing of auras with root effect": only the face-caster teleport needs a free caster.
                        if (effect.Effect == SpellEffectName.TeleportUnitsFaceCaster && IsRooted(caster))
                        {
                            return SpellCastResult.Rooted;
                        }

                        if (caster is Player player && player.Movement.HasFlag(MovementFlags.OnTransport))
                        {
                            return SpellCastResult.NotOnTransport;
                        }

                        break;
                }
            }

            return SpellCastResult.CastOk;
        }

        /// <summary>vmangos hasUnitState(UNIT_STAT_ROOT): a stun or root aura, or the root movement flag.</summary>
        private static bool IsRooted(Unit unit)
            => (unit.UnitFlags & UnitFlags.Stunned) != 0 || unit is Player { IsRooted: true } || unit.Movement.HasFlag(MovementFlags.Root);
    }
}
