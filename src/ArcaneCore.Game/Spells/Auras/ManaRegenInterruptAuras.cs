namespace ArcaneCore.Game.Spells;

/// <summary>Registers SPELL_AURA_MOD_MANA_REGEN_INTERRUPT (134), whose percentage is consumed by player mana regeneration.</summary>
public sealed class ManaRegenInterruptAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterAura(AuraType.ModManaRegenInterrupt, new AuraHandler(null, null));
}
