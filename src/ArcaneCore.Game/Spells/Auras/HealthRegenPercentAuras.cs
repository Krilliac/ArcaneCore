namespace ArcaneCore.Game.Spells;

/// <summary>Registers SPELL_AURA_MOD_HEALTH_REGEN_PERCENT (88), consumed by MapCombat's out-of-combat health tick.</summary>
public sealed class HealthRegenPercentAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterAura(AuraType.ModHealthRegenPercent, new AuraHandler(null, null));
}
