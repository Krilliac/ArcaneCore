using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_TRIGGER_MISSILE (32): the effect's trigger spell is cast, triggered, at the cast's destination (vmangos
/// Spell::EffectTriggerMissileSpell, SpellEffects.cpp:1577-1594: <c>m_caster-&gt;CastSpell(m_targets.m_destX, …, spellInfo, true, m_CastItem)</c>).
/// vmangos runs it once per cast (a location effect); here the effect handler runs per selected unit, so a cast fires each effect once. A cast
/// with no destination uses its unit target's position, then the caster's.
/// </summary>
public sealed partial class SpellSystem
{
    private readonly ConditionalWeakTable<SpellCast, HashSet<int>> _missilesFired = new();

    private void InstallTriggerMissile() => RegisterEffect(SpellEffectName.TriggerMissile, context =>
    {
        if (Store.Get(context.Effect.TriggerSpell) is not { } missile || !_missilesFired.GetOrCreateValue(context.Cast).Add(context.EffectIndex))
        {
            return;
        }

        (float X, float Y, float Z) dest = context.Cast.Targets.HasDest
            ? context.Cast.Targets.Dest
            : (context.Target.X, context.Target.Y, context.Target.Z);
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = dest };
        CastSpell(context.Caster, missile.Id, targets, triggered: true, triggeringSpell: context.Spell);
    });
}
