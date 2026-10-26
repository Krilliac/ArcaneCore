using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Spell::CheckRange, game-object branch (Spell.cpp:6922-6927): a player's cast that names a game object in its target block
    /// fails with OUT_OF_RANGE unless the object is at interact distance of the spell's maximum range plus the cast's range leeway
    /// (GameObject::IsAtInteractDistance, GameObject.cpp:2512-2534). A named object that is not on the map or not spawned is left to the
    /// effect, as in vmangos (getGOTarget is then null). Limit: the oriented display-bounds box of IsAtInteractDistance needs model bounds
    /// this codebase does not load, so the centre distance is compared with the larger of the spell reach and the object's own interaction
    /// distance (<see cref="GameObjectMapSystem.InteractionDistanceFor"/>). vmangos' no-bounds path is only GetDistance3dToCenter <= maxRange
    /// (GameObject.cpp:2525-2526, 2608); taking the max is a deliberate leniency that stands in for the missing bounds box and is wider than vmangos. Line of sight is not checked: vmangos has none for this branch.
    /// </summary>
    private SpellCastResult CheckGameObjectTargetRange(Unit caster, SpellInfo spell, SpellCastTargets targets, bool triggered, bool strict)
    {
        if (triggered || caster is not Player || (targets.Mask & SpellCastTargetFlags.GameObject) == 0 || targets.GameObject.IsEmpty
            || spell.RangeIndex == SpellConstants.RangeIndexSelfOnly)
        {
            return SpellCastResult.CastOk;
        }

        if (caster.Map?.FindUpdater<GameObjectMapSystem>()?.Find(targets.GameObject) is not { IsSpawned: true } go)
        {
            return SpellCastResult.CastOk;
        }

        float maxRange = SpellModifiers.Apply(caster, spell, SpellModOp.Range, spell.Range.Max);
        maxRange += strict ? SpellConstants.PlayerStrictRangeLeeway : SpellConstants.PlayerLandingRangeLeeway;
        float reach = Math.Max(maxRange, GameObjectMapSystem.InteractionDistanceFor(go.Type));
        float dx = go.X - caster.X;
        float dy = go.Y - caster.Y;
        float dz = go.Z - caster.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) > reach * reach ? SpellCastResult.OutOfRange : SpellCastResult.CastOk;
    }
}
