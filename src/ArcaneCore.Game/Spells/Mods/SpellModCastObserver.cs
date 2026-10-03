namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Ends a cast's charged mods when the cast ends: a completed cast removes the auras of the mods it ran out of
/// (vmangos Spell::finish, Spell.cpp:4399 -> Player::RemoveSpellMods), a cancelled or failed one gives the charges back
/// (Spell.cpp:4364, :3534 -> RestoreSpellMods). A channel already sealed its mods when it started
/// (<see cref="ISpellModEngine.Seal"/>), so its end finds a closed scope and does nothing.
/// </summary>
internal sealed class SpellModCastObserver(ISpellModEngine engine) : ISpellCastObserver
{
    public void OnFinished(SpellCast cast, bool completed)
    {
        if (cast.ModScope is not { IsClosed: false } scope)
        {
            return;
        }

        if (completed)
        {
            engine.Seal(scope);
        }
        else
        {
            engine.Restore(scope);
        }
    }
}
