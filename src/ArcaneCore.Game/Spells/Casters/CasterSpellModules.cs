namespace ArcaneCore.Game.Spells.Casters;

/// <summary>
/// The mage / priest / warlock rules that plug into a <see cref="SpellSystem"/> through its public registration
/// seams (<see cref="SpellSystem.RegisterEffect"/>, <see cref="SpellSystem.RegisterAura"/>). The world daemon runs
/// <see cref="Register"/> from its caster feature; tests call it on a bare system. Each caster slice that adds
/// handlers appends one line here.
/// </summary>
public static class CasterSpellModules
{
    public static void Register(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        PowerCostAuras.Register(spells);
    }
}
