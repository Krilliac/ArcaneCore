namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Registers the paladin scripts that are not <see cref="Scripts.ISpellScript"/>s (discovered <see cref="ISpellHandlerModule"/>, so every spell
/// system has them): the Consecration tick (<see cref="ConsecrationScript"/>).
/// </summary>
public sealed class PaladinScriptsModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var consecration = new ConsecrationScript();
        foreach (uint rank in ConsecrationScript.Ranks)
        {
            system.RegisterPeriodicDamageScript(rank, consecration);
        }
    }
}
