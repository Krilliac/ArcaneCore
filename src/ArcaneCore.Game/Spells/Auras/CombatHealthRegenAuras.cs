namespace ArcaneCore.Game.Spells;

public sealed class CombatHealthRegenAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterAura(AuraType.ModRegenDuringCombat, new AuraHandler(null, null));
}
