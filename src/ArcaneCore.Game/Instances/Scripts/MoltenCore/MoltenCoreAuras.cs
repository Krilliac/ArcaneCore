using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic SpellAuras.cpp Aura::HandleAuraDummy/PeriodicTick, 21094 and 23487:
/// every five seconds a living boss's add outside the DBC radius casts Separation Anxiety.</summary>
public sealed class MoltenCoreAuraModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.HolderAdded += holder =>
        {
            if (holder.Spell.Id is 21094 or 23487)
                holder.Target.Map?.FindUpdater<MoltenCoreInstance>()?.TrackAnxiety(system, holder);
        };
    }
}
