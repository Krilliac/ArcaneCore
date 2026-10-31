using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>The spell_script_target types for units (vmangos SpellMgr.h SpellTargetType; 0, the game object type, is not selected here).</summary>
    private const uint ScriptTargetCreature = 1;
    private const uint ScriptTargetDead = 2;
    private const uint ScriptTargetPlayer = 3;

    /// <summary>
    /// The unit script targets of vmangos Spell::SetTargetMap: TARGET_UNIT_SCRIPT_NEAR_CASTER (38) goes through
    /// Spell::CheckScriptTargeting, which keeps a valid explicit target and otherwise takes the one nearest unit any
    /// spell_script_target row lists (a single unit, Spell.cpp:445-575); TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC (7)
    /// fills the source area and keeps the units whose entry and state a row matches, or every unit when the spell has
    /// no rows (Spell.cpp:2356-2398). A listed creature caster may select itself with 38, while area 7 excludes the caster. vmangos fails a 38 cast with no target
    /// (SPELL_FAILED_BAD_TARGETS); here the effect only gets no unit.
    /// </summary>
    /// <para>
    /// <paramref name="cone"/> (TARGET_ENUM_UNITS_SCRIPT_IN_CONE_60) fills the caster's frontal cone (<see cref="SpellConstants.ConeArc"/>) instead of a circle.
    /// </para>
    /// <para>
    /// TARGET_ENUM_UNITS_SCRIPT_AOE_AT_DEST_LOC (8, Spell.cpp:2405-2470) is the same area fill around the destination (<paramref name="destination"/>)
    /// that keeps the caster: a persistent area aura selects nobody (its dynamic object does), and a summon effect acts on the caster.
    /// </para>
    private List<(Unit Unit, float Multiplier)> SelectScriptTargets(
        SpellCast cast, SpellEffectInfo effect, int effectIndex, bool nearest, Unit? explicitTarget, (float X, float Y, float Z)? destination = null, bool cone = false)
    {
        if (cast.Caster.Map is not { } map) return [];
        IReadOnlyList<SpellStore.ScriptTarget> rows = Store.GetScriptTargets(cast.Spell.Id);
        if (nearest && rows.Count == 0) return [];
        // vmangos SetTargetMap: the effect radius, else the spell's maximum range (Spell.cpp:2049-2053).
        float baseRadius = TargetMapRadius(cast.Spell, effect, effectIndex);
        float radius = baseRadius > 0 ? baseRadius : cast.Spell.Range.Max;
        if (radius <= 0) return [];
        // mangos-classic Spell::CheckScriptTargeting bounds the "anywhere" range (50000) of an entry search to 200 yards.
        if (nearest && radius >= 50_000f) radius = 200f;
        (float x, float y, float z) = nearest || cone ? (cast.Caster.X, cast.Caster.Y, cast.Caster.Z) : destination ?? SourceCentre(cast, effect);

        bool Matches(Unit unit)
        {
            if (!nearest && destination is null && ReferenceEquals(unit, cast.Caster)) return false;
            // TARGET_ENUM_UNITS_SCRIPT_IN_CONE_60 (Spell.cpp:2702-2740): the caster's frontal cone; with no rows a SCRIPT_EFFECT takes every
            // living unit in it and any other effect the enemies (the AoE damage targets).
            if (cone && (ReferenceEquals(unit, cast.Caster) || !Combat.MapCombat.HasInArc(cast.Caster, unit, SpellConstants.ConeArc))) return false;
            if (rows.Count == 0) return unit.IsAlive && (!cone || effect.Effect == SpellEffectName.ScriptEffect || IsEnemy(cast.Caster, unit));
            return rows.Any(row => (row.InverseEffectMask & (1u << effectIndex)) == 0
                && (row.TargetEntry == 0 && row.Type == ScriptTargetPlayer && unit is Player
                    || unit is Creature creature && creature.Entry == row.TargetEntry)
                && (row.Type switch
                {
                    ScriptTargetCreature => unit is Creature && unit.IsAlive,
                    ScriptTargetDead => unit is Creature && !unit.IsAlive,
                    ScriptTargetPlayer => unit is Player && unit.IsAlive,
                    _ => false,
                }));
        }

        List<Unit> units = [.. UnitsInRadius(map, x, y, z, radius).Where(Matches)
            .Where(unit => IsInLineOfSightFromPoint(cast.Spell, map, x, y, z, unit))];
        if (nearest)
        {
            // CheckScriptTargeting: a matching explicit target wins, else the nearest match; either way one unit.
            if (explicitTarget is not null && units.Contains(explicitTarget)) return [(explicitTarget, 1f)];
            float Distance(Unit unit) => (unit.X - x) * (unit.X - x) + (unit.Y - y) * (unit.Y - y) + (unit.Z - z) * (unit.Z - z);
            return units.Count == 0 ? [] : [(units.MinBy(Distance)!, 1f)];
        }

        CapTargets(units, MaxTargetsOf(cast.Spell));
        return [.. units.Select(unit => (unit, 1f))];
    }

    /// <summary>Whether an effect of <paramref name="spell"/> selects with target 46 through the generic selector while the spell has no spell_script_target row.</summary>
    private bool HasScriptLocationTargetWithoutRows(SpellInfo spell)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (!effect.IsEmpty && SelectorOf(effect) == SpellImplicitTarget.LocationScriptNearCaster
                && !_spellTargetSelectors.ContainsKey((spell.Id, SpellImplicitTarget.LocationScriptNearCaster))
                && Store.GetScriptTargets(spell.Id).Count == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// TARGET_LOCATION_SCRIPT_NEAR_CASTER (46; vmangos Spell::CheckScriptTargeting, Spell.cpp:446-600, and Spell::SetTargetMap :2848-2860): the
    /// object of the nearest spell_script_target row within the effect radius (else the spell's maximum range, 200 yd for an "anywhere" range)
    /// becomes the destination. Row type 0 is a game object, 1 a living creature, 2 a dead creature, 3 a player; a valid explicit unit is preferred
    /// to the nearest one. The found object is also a target of the effect only when target A is 46 and the effect is not a persistent area aura
    /// (Spell.cpp:566-585): a unit is the effect's unit, a game object is its object target with the caster as carrier. Nothing within range leaves
    /// the destination alone and selects nothing; a spell with no rows at all is refused at cast time (<c>CheckCast</c>, SPELL_FAILED_BAD_TARGETS).
    /// </summary>
    private List<(Unit Unit, float Multiplier)> SelectScriptLocationTarget(SpellCast cast, SpellEffectInfo effect, int effectIndex, Unit? explicitTarget)
    {
        Unit caster = cast.Caster;
        if (caster.Map is not { } map) return [];
        SpellStore.ScriptTarget[] rows = [.. Store.GetScriptTargets(cast.Spell.Id).Where(row => (row.InverseEffectMask & (1u << effectIndex)) == 0)];
        if (rows.Length == 0) return [];

        float radius = TargetMapRadius(cast.Spell, effect, effectIndex);
        if (radius <= 0) radius = cast.Spell.Range.Max;
        if (radius >= 50_000f) radius = 200f; // CheckScriptTargeting bounds the "anywhere" range of an entry search
        if (radius <= 0) return [];

        float DistanceSquared(WorldObject target)
        {
            float dx = target.X - caster.X, dy = target.Y - caster.Y, dz = target.Z - caster.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        bool UnitListed(Unit unit) => rows.Any(row => row.Type switch
        {
            ScriptTargetCreature => unit is Creature creature && unit.IsAlive && creature.Entry == row.TargetEntry,
            ScriptTargetDead => unit is Creature dead && !unit.IsAlive && dead.Entry == row.TargetEntry,
            ScriptTargetPlayer => unit is Player && unit.IsAlive && row.TargetEntry == 0,
            _ => false,
        });

        Unit? chosenUnit = explicitTarget is not null && UnitListed(explicitTarget) && DistanceSquared(explicitTarget) <= radius * radius
            ? explicitTarget
            : UnitsInRadius(map, caster.X, caster.Y, caster.Z, radius).Where(UnitListed)
                .OrderBy(DistanceSquared).ThenBy(unit => unit.Guid.Value).FirstOrDefault();
        GameObject? chosenObject = null;
        if (explicitTarget is null || chosenUnit is null || !ReferenceEquals(chosenUnit, explicitTarget))
        {
            HashSet<uint> objectEntries = [.. rows.Where(row => row.Type == 0 && row.TargetEntry != 0).Select(row => row.TargetEntry)];
            if (objectEntries.Count > 0 && map.FindUpdater<GameObjectMapSystem>() is { } objects)
            {
                chosenObject = objects.GameObjects.Where(go => go.IsSpawned && objectEntries.Contains(go.Entry)
                        && DistanceSquared(go) <= (radius + go.BoundingRadius) * (radius + go.BoundingRadius))
                    .OrderBy(DistanceSquared).ThenBy(go => go.Guid.Value).FirstOrDefault();
            }
        }

        // The nearest of the two kinds wins, as the shrinking search radius of CheckScriptTargeting makes it; an explicit unit always wins.
        if (chosenUnit is not null && chosenObject is not null && !ReferenceEquals(chosenUnit, explicitTarget)
            && DistanceSquared(chosenObject) < DistanceSquared(chosenUnit))
        {
            chosenUnit = null;
        }

        WorldObject? found = chosenUnit is not null ? chosenUnit : chosenObject;
        if (found is null) return [];
        cast.Targets.Dest = (found.X, found.Y, found.Z);
        cast.Targets.Mask |= SpellCastTargetFlags.DestLocation;
        if (effect.TargetA != SpellImplicitTarget.LocationScriptNearCaster || effect.Effect == SpellEffectName.PersistentAreaAura) return [];
        if (chosenUnit is not null) return [(chosenUnit, 1f)];
        cast.ObjectTargetsByEffect[effectIndex] = [chosenObject!.Guid];
        return [(caster, 1f)]; // the caster carries the object effect through the unit-effect pipeline
    }
}
