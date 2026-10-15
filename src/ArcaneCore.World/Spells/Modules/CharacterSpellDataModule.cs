using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Spells.Modules;

/// <summary>
/// World-daemon adapter for resolving corpse targets during spell target updates
/// (<see cref="ISpellCorpseTargetResolver"/>; vmangos SpellCastTargets::Update).
/// </summary>
public sealed class WorldCorpseSpellTargetResolver : ISpellCorpseTargetResolver
{
    public static readonly WorldCorpseSpellTargetResolver Instance = new();

    public Corpse? ResolveCorpse(Unit caster, ObjectGuid corpseGuid)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (corpseGuid.IsEmpty || caster.Map is not { } map)
        {
            return null;
        }

        return map.FindObject(corpseGuid) as Corpse;
    }

    public static void Attach(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.CorpseTargets = Instance;
    }
}
