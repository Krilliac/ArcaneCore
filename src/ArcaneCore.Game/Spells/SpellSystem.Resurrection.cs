using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The resurrection requests of this world (set by the death feature of the world daemon): what the resurrect effects
    /// (<see cref="ResurrectEffects"/>) offer a dead player. Without one they do nothing.
    /// </summary>
    public ResurrectionService? Resurrection { get; set; }

    /// <summary>
    /// The player a corpse belongs to (vmangos Spell.cpp:3109-3118: <c>GetMap()->GetCorpse(guid)</c>, then
    /// <c>ObjectAccessor::FindPlayer(owner)</c>, which finds a player on any map: a body left in a dungeon belongs to a ghost outside it).
    /// Null when the corpse is not in the caster's map or its owner is offline.
    /// </summary>
    private Unit? ResolveCorpseOwner(Unit caster, ObjectGuid corpseGuid)
    {
        if (corpseGuid.IsEmpty || caster.Map?.FindObject(corpseGuid) is not Corpse corpse)
        {
            return null;
        }

        return Units.Find(caster, corpse.Owner) ?? Resurrection?.FindPlayer(corpse.Owner);
    }
}
