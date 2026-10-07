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

    /// <summary>
    /// The owner of the corpse a resurrect effect is aimed at (vmangos Spell.cpp:3109-3118): the corpse must be on the caster's map, a unit the client
    /// names as well must be that owner, and the owner must be the dead player whose body it is. Null otherwise (CheckCast then reports bad targets).
    /// </summary>
    private Unit? ResolveResurrectionCorpseTarget(Unit caster, SpellCastTargets targets)
    {
        if (targets.Corpse.IsEmpty || caster.Map?.FindObject(targets.Corpse) is not Corpse corpse
            || ((targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0
                && !targets.Unit.IsEmpty && targets.Unit != corpse.Owner))
        {
            return null;
        }

        Player? owner = Units.Find(caster, corpse.Owner) as Player ?? Resurrection?.FindPlayer(corpse.Owner);
        return owner is { IsInWorld: true, IsAlive: false } && ReferenceEquals(owner.Combat.Corpse, corpse) ? owner : null;
    }

    /// <summary>
    /// Whether a cast aims a resurrect effect at a corpse (vmangos Spell.cpp:3109-3118). CheckCast then measures no range to the corpse's
    /// owner, who may be a ghost far away (or on another map); the resurrect effect checks test the body itself (map and line of sight).
    /// </summary>
    private static bool HasResurrectionCorpseTarget(SpellInfo spell, SpellCastTargets targets)
        => (targets.Mask & (SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0
            && spell.Effects.Any(e => e.Effect is SpellEffectName.Resurrect or SpellEffectName.ResurrectNew);
}
