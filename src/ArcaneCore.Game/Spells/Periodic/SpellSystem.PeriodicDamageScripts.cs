using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The per-spell seam of a SPELL_AURA_PERIODIC_DAMAGE tick (vmangos <c>AuraScript::OnPeriodicCalculateAmount</c> plus the hard-coded spell
/// cases of <c>Aura::PeriodicTick</c>, SpellAuras.cpp:5861-5925). Class scripts register one per aura spell with
/// <see cref="SpellSystem.RegisterPeriodicDamageScript"/>.
/// </summary>
public interface IPeriodicDamageScript
{
    /// <summary>
    /// The tick's amount before the target side and the tick-index ramp, or null to keep the stored amount (vmangos replaces
    /// <c>fdamage</c> here: Consecration recalculates it from the caster's current bonuses, SpellAuras.cpp:5873-5874).
    /// </summary>
    float? CalculateTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura, Unit caster, uint storedAmount) => null;

    /// <summary>After the tick dealt <paramref name="dealt"/> damage (vmangos runs the Curse of Doom summon right after DealDamage, :5921-5924).</summary>
    void AfterTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura, Unit caster, uint dealt)
    {
    }
}

public sealed partial class SpellSystem
{
    private readonly Dictionary<uint, IPeriodicDamageScript> _periodicDamageScripts = [];

    /// <summary>Register the periodic damage script of <paramref name="spellId"/>; a second script for one spell is a startup error.</summary>
    public void RegisterPeriodicDamageScript(uint spellId, IPeriodicDamageScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!_periodicDamageScripts.TryAdd(spellId, script))
        {
            throw new InvalidOperationException($"spell {spellId} already has a periodic damage script");
        }
    }

    /// <summary>The periodic damage script of <paramref name="spellId"/>, or null.</summary>
    public IPeriodicDamageScript? FindPeriodicDamageScript(uint spellId) => _periodicDamageScripts.GetValueOrDefault(spellId);
}
