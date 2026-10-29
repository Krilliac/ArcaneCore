namespace ArcaneCore.Game.Spells;

/// <summary>
/// The effects vmangos routes to a handler with no body, so that casting one is the silent no-op it is there instead of a "not implemented" report:
/// <list type="bullet">
/// <item>SPELL_EFFECT_EVADE (21; vmangos table row 21, <c>EffectEmpty</c> "one spell: Evade (DND)", SpellEffects.cpp:81; the body at :194-197 says "not need any
/// implementation code, effect possible used as marker or client inform"). The 1.12 Spell.dbc has one user, Evade (DND) (108).</item>
/// <item>SPELL_EFFECT_NOSTALRIUS (131; vmangos <c>EffectNostalrius</c>, SpellEffects.cpp:5916-5919, which only writes a debug log line). No 1.12 Spell.dbc
/// spell uses it.</item>
/// </list>
/// SPELL_EFFECT_126 and 127 are <c>EffectUnused</c> and 132 and 133 are past the end of vmangos's table, so they stay unregistered.
/// </summary>
public sealed class MarkerEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.Evade, static _ => { });
        system.RegisterEffect(SpellEffectName.Nostalrius, static _ => { });
    }
}
