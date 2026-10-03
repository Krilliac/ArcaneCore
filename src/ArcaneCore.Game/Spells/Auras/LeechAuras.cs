namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_PERIODIC_LEECH (53, Drain Life / Siphon Life) and SPELL_AURA_PERIODIC_MANA_LEECH (64, Drain Mana),
/// after vmangos <c>Aura::PeriodicTick</c> (SpellAuras.cpp:5927 and :6116). The ticks live in
/// <c>SpellSystem.Leech.cs</c> because they use the system's private damage, resolution and cancel helpers.
/// </summary>
public sealed class LeechAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.PeriodicLeech, new AuraHandler(null, static (s, h, a) => s.TickPeriodicLeech(h, a)));
        system.RegisterAura(AuraType.PeriodicManaLeech, new AuraHandler(null, static (s, h, a) => s.TickPeriodicManaLeech(h, a)));
    }
}
