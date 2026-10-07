using ArcaneCore.Game.Creatures;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_DISTRACT (69), after vmangos <c>Spell::EffectDistract</c> (Spells/SpellEffects.cpp:2632-2649): a creature target that is
/// not in combat and can react turns to the spell's destination and stands for the effect value in seconds (MoveDistract), then turns
/// back to its spawn facing (<see cref="CreatureMapSystem.Distract"/>). Rogue Distract (1725) is the classic user: 10 s, every enemy
/// within 10 yd of the destination, cast with NO_THREAT and NO_INITIAL_AGGRO so it does not pull. A player target only turns in the
/// reference (the client owns its facing); nothing is done for one here. A cast without a destination faces the caster.
/// </summary>
public sealed class DistractEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.Distract, EffectDistract);
    }

    private static void EffectDistract(SpellEffectContext context)
    {
        if (context.Target is not Creature creature || creature.System is not { } creatures || context.Value <= 0)
        {
            return;
        }

        SpellCastTargets targets = context.Cast.Targets;
        (float x, float y) = targets.HasDest ? (targets.Dest.X, targets.Dest.Y) : (context.Caster.X, context.Caster.Y);
        float angle = MathF.Atan2(y - creature.Y, x - creature.X);
        creatures.Distract(creature, angle, (uint)context.Value * 1000u);
    }
}
