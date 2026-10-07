namespace ArcaneCore.Game.Spells;

/// <summary>Registers SPELL_AURA_MOD_HEALTH_REGEN_IN_COMBAT (161), whose value is consumed by MapCombat's two-second health tick.</summary>
public sealed class CombatFlatHealthRegenAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterAura(AuraType.ModHealthRegenInCombat, new AuraHandler(null, null));
}
