namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_PERIODIC_LEECH (53, Drain Life / Siphon Life) and SPELL_AURA_PERIODIC_MANA_LEECH (64, Drain Mana),
/// after vmangos <c>Aura::PeriodicTick</c> (SpellAuras.cpp:5927 and :6116). This built-in module is the single registration
/// point; the tick logic is the casters lane's <see cref="Casters.Drain.DrainAuras"/> (spell power, Improved Drain Mana), which
/// replaced the spell-breadth lane's duplicate ticks at wave-2 integration.
/// </summary>
public sealed class LeechAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        Casters.Drain.DrainAuras.Register(system);
    }
}
