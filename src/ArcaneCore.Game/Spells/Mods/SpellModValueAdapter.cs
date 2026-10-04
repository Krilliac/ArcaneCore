namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Feeds the spell system's value seam from the engine: <see cref="SpellValueKind.CastTime"/> is SPELLMOD_CASTING_TIME
/// (vmangos SpellEntry::GetCastTime, SpellEntry.cpp:486-494, never asked for 0) and <see cref="SpellValueKind.Duration"/> is
/// SPELLMOD_DURATION (SpellEntry::CalculateDuration, :723-751). The seam is pure, so this only reads; the other kinds are
/// wired where vmangos orders them (cost inside the cost formula, effect value after the combo-point scaling).
/// </summary>
internal sealed class SpellModValueAdapter(ISpellModEngine engine) : ISpellValueModifier
{
    public int Modify(SpellValueKind kind, in SpellValueContext context, int value) => kind switch
    {
        SpellValueKind.CastTime => engine.Apply(context.Caster, context.Spell, SpellModOp.CastingTime, value),
        SpellValueKind.Duration => engine.Apply(context.Caster, context.Spell, SpellModOp.Duration, value),
        _ => value,
    };
}
