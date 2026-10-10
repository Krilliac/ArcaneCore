using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The game-object script targets of effect 86: type 40 chooses one listed object near the caster;
    /// types 51 and 52 visit every listed object at the source or destination. The rows are the
    /// imported spell_script_target entries, narrowed by the effect's inverse mask.
    /// </summary>
    private List<(Unit Unit, float Multiplier)>? SelectScriptGameObjects(
        SpellCast cast, SpellEffectInfo effect, int effectIndex, SpellImplicitTarget selector, Unit? unitTarget)
    {
        if (effect.Effect != SpellEffectName.ActivateObject)
            return null;
        Unit caster = cast.Caster;
        if (caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
            return [];

        HashSet<uint> entries = [.. Store.GetScriptTargets(cast.Spell.Id)
            .Where(row => row.Type == 0 && (row.InverseEffectMask & (1u << effectIndex)) == 0)
            .Select(row => row.TargetEntry)];
        if (entries.Count == 0)
            return [];

        float radius = TargetMapRadius(cast.Spell, effect, effectIndex);
        if (radius <= 0) radius = cast.Spell.Range.Max;
        if (radius >= 50_000f) radius = 200f; // cmangos CheckScriptTargeting bounds entry scans
        radius = radius > 0 ? ModFloat(caster, cast.Spell, SpellModOp.Radius, radius) : radius;
        if (radius <= 0)
            return [];

        (float x, float y, float z) = selector switch
        {
            (SpellImplicitTarget)51 => SourceCentre(cast, effect),
            (SpellImplicitTarget)52 => DestinationCentre(cast, unitTarget),
            _ => (caster.X, caster.Y, caster.Z),
        };
        float DistanceSquared(GameObject go)
        {
            float dx = go.X - x, dy = go.Y - y, dz = go.Z - z;
            return dx * dx + dy * dy + dz * dz;
        }

        GameObject[] candidates = [.. objects.GameObjects.Where(go => go.IsSpawned && entries.Contains(go.Entry)
            && DistanceSquared(go) <= (radius + go.BoundingRadius) * (radius + go.BoundingRadius))
            .OrderBy(DistanceSquared).ThenBy(go => go.Guid.Value)];
        if (candidates.Length == 0)
            return [];

        if (selector == (SpellImplicitTarget)40)
        {
            GameObject chosen = candidates.FirstOrDefault(go => go.Guid == cast.Targets.GameObject) ?? candidates[0];
            cast.Targets.GameObject = chosen.Guid;
            cast.Targets.Mask |= SpellCastTargetFlags.GameObject;
            cast.ObjectTargetsByEffect[effectIndex] = [chosen.Guid];
        }
        else
        {
            cast.ObjectTargetsByEffect[effectIndex] = [.. candidates.Select(go => go.Guid)];
        }

        return [(caster, 1f)]; // the caster carries the object effect through the unit-effect pipeline
    }
}
