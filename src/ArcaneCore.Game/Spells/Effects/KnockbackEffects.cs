using ArcaneCore.Game.Locomotion;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_KNOCK_BACK (98) and SPELL_EFFECT_PLAYER_PULL (124), after vmangos <c>Spell::EffectKnockBack</c> (SpellEffects.cpp:5474-5484)
/// and <c>Spell::EffectPlayerPull</c> (:5495-5505). 133 classic spells have the first effect (51 of them are cast by creatures in
/// dungeons and raids), 7 the second.
/// <list type="bullet">
/// <item>Knock back: a target that is taxi flying is skipped; the Dream Fog sleep (24778) is removed so the target can be launched; the
/// horizontal speed is <c>misc value / 10</c> and the vertical speed is <c>value / 10</c> with vmangos'' integer division of the effect
/// value (mangos-classic divides as a float: 57 gives 5 in vmangos and 5.7 there; vmangos is followed and the difference is an
/// open question).</item>
/// <item>Player pull: toward the caster, by the target''s 2D distance to it capped at the effect value, as the negative horizontal speed, with
/// <c>misc value / 10</c> as the vertical speed. vmangos itself notes the implementation "seems very wrong".</item>
/// </list>
/// Both go through <see cref="KnockbackService"/>.
/// </summary>
public sealed class KnockbackEffects : ISpellHandlerModule
{
    /// <summary>The Dream Fog sleep that would hold the target (vmangos removes it "to let target be launched").</summary>
    public const uint DreamFogSleep = 24778;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.KnockBack, EffectKnockBack);
        system.RegisterEffect(SpellEffectName.PlayerPull, EffectPlayerPull);
    }

    private static void EffectKnockBack(SpellEffectContext context)
    {
        if ((context.Target.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            return;
        }

        context.System.RemoveAuras(context.Target, DreamFogSleep);
        KnockbackService.KnockBackFrom(context.System, context.Target, context.Caster, context.Effect.MiscValue / 10.0f, context.Value / 10);
    }

    private static void EffectPlayerPull(SpellEffectContext context)
    {
        float distance = Distance2d(context.Target, context.Caster);
        if (context.Value != 0 && distance > context.Value)
        {
            distance = context.Value;
        }

        KnockbackService.KnockBackFrom(context.System, context.Target, context.Caster, -distance, context.Effect.MiscValue / 10.0f);
    }

    private static float Distance2d(Entities.Unit a, Entities.Unit b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
