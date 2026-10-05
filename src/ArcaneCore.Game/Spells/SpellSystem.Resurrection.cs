using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Online corpse owners may be ghosts on another map. The world feature supplies its online
    /// registry; local game hosts can resolve an owner through the ordinary unit resolver.
    /// </summary>
    public Func<ObjectGuid, Player?>? ResurrectionPlayers { get; set; }

    private static bool HasResurrectionCorpseTarget(SpellInfo spell, SpellCastTargets targets)
        => (targets.Mask & (SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0
            && spell.Effects.Any(e => e.Effect is SpellEffectName.Resurrect or SpellEffectName.ResurrectNew);

    private Unit? ResolveResurrectionCorpseTarget(Unit caster, SpellCastTargets targets)
    {
        if (caster.Map?.FindObject(targets.Corpse) is not Corpse corpse
            || !ReferenceEquals(corpse.Map, caster.Map)
            || ((targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0
                && !targets.Unit.IsEmpty && targets.Unit != corpse.Owner))
        {
            return null;
        }

        Player? owner = ResurrectionPlayers?.Invoke(corpse.Owner) ?? Units.Find(caster, corpse.Owner) as Player;
        return owner is { IsInWorld: true, IsAlive: false } && ReferenceEquals(owner.Combat.Corpse, corpse) ? owner : null;
    }

    /// <summary>
    /// vmangos Spell.cpp:3112-3117 resolves a local body to its online owner, while 5780-5788
    /// checks the body's LOS. CheckRange does not measure a corpse cast against the ghost's
    /// position (or impose a separate corpse range); the spirit can have left for a graveyard.
    /// </summary>
    internal SpellCastResult CheckResurrectionCorpseTarget(Unit caster, SpellInfo spell, SpellCastTargets targets,
        Unit target, bool triggered, bool strict)
    {
        if (!ReferenceEquals(ResolveResurrectionCorpseTarget(caster, targets), target)
            || caster.Map?.FindObject(targets.Corpse) is not Corpse corpse || caster.Map is not { } map)
        {
            return SpellCastResult.BadTargets;
        }

        // This per-effect LOS check also runs on triggered spells in vmangos CheckCast.
        return spell.HasAttribute(SpellAttributesEx2.IgnoreLineOfSight) || map.Collision.IsWithinLineOfSight(caster, corpse)
            ? SpellCastResult.CastOk : SpellCastResult.LineOfSight;
    }
}
