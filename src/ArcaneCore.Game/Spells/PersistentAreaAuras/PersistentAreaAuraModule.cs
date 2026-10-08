namespace ArcaneCore.Game.Spells.PersistentAreaAuras;

/// <summary>
/// Installs SPELL_EFFECT_PERSISTENT_AREA_AURA (discovered <see cref="ISpellHandlerModule"/>): the per-unit effect does nothing (the cast puts the
/// ground object down once, <see cref="SpellSystem"/>.HandleGroundEffects) and a channel that ends removes its objects.
/// </summary>
public sealed class PersistentAreaAuraModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.PersistentAreaAura, static _ => { });
        system.RegisterObserver(new ChannelEndObserver(system));
    }

    /// <summary>vmangos Spell::cancel / Spell::finish for a channel: <c>m_caster-&gt;RemoveDynObject(m_spellInfo-&gt;Id)</c> (Spell.cpp:3595, :4794).</summary>
    private sealed class ChannelEndObserver(SpellSystem system) : ISpellCastObserver
    {
        public void OnFinished(SpellCast cast, bool completed)
        {
            if (cast.Spell.IsChanneled && cast.Spell.HasEffect(SpellEffectName.PersistentAreaAura))
            {
                system.RemoveDynamicObjects(cast.Caster, cast.Spell.Id);
            }
        }
    }
}
