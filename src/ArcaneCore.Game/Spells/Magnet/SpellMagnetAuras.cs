namespace ArcaneCore.Game.Spells;

/// <summary>Aura 96 has no immediate field effect; enemy target selection reads its holder.</summary>
public sealed class SpellMagnetAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.SpellMagnet, new AuraHandler(null, null));
    }
}
