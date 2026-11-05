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
    /// no rows (Spell.cpp:2356-2398). A listed creature caster may select itself with 38, while area 7 excludes the caster. Target 38 runs the same
    /// search as target 46 (<see cref="FindScriptLocationTarget"/>: combat distance, radius mod, no line of sight, nearest of a listed unit or
    /// game object); a game object found by 38 is no unit target (Spell.cpp:566-585). A 38 effect with rows that finds nothing fails the cast
    /// beforehand (<see cref="ScriptLocationTargetMissing"/>); a 38 spell with no rows keeps selecting nobody.
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
        if (nearest)
        {
            (Unit? nearestUnit, _) = FindScriptLocationTarget(cast.Caster, cast.Spell, effect, effectIndex, explicitTarget);
            return nearestUnit is null ? [] : [(nearestUnit, 1f)];
        }

        if (cast.Caster.Map is not { } map) return [];
        IReadOnlyList<SpellStore.ScriptTarget> rows = Store.GetScriptTargets(cast.Spell.Id);
        // vmangos SetTargetMap: the effect radius, else the spell's maximum range (Spell.cpp:2049-2053).
        float baseRadius = TargetMapRadius(cast.Spell, effect, effectIndex);
        float radius = baseRadius > 0 ? baseRadius : cast.Spell.Range.Max;
        if (radius <= 0) return [];
        (float x, float y, float z) = cone ? (cast.Caster.X, cast.Caster.Y, cast.Caster.Z) : destination ?? SourceCentre(cast, effect);

        bool Matches(Unit unit)
        {
            if (destination is null && ReferenceEquals(unit, cast.Caster)) return false;
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
        CapTargets(units, MaxTargetsOf(cast.Spell));
        return [.. units.Select(unit => (unit, 1f))];
    }

    /// <summary>
    /// Whether an effect of <paramref name="spell"/> has target 46 (as target A or B, as vmangos SetTargetMap runs both) through the generic selector while
    /// the spell has no spell_script_target row.
    /// </summary>
    private bool HasScriptLocationTargetWithoutRows(SpellInfo spell)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (!effect.IsEmpty && (effect.TargetA == SpellImplicitTarget.LocationScriptNearCaster || effect.TargetB == SpellImplicitTarget.LocationScriptNearCaster)
                && !_spellTargetSelectors.ContainsKey((spell.Id, SpellImplicitTarget.LocationScriptNearCaster))
                && Store.GetScriptTargets(spell.Id).Count == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the spell has an effect that selects with target 46 (as target A or B) or target 38 (when it is the effect's selector) through the
    /// generic selector, has spell_script_target rows, and finds no listed unit or object within range of <paramref name="caster"/>.
    /// vmangos Spell::CheckScriptTargeting then fails the cast (SPELL_FAILED_BAD_TARGETS, or DONT_REPORT when triggered; Spell.cpp:603-616,
    /// SetTargetMap :2848-2856). A 38 spell with no rows is not refused here: spell-specific selectors and scripts own those.
    /// </summary>
    private bool ScriptLocationTargetMissing(Unit caster, SpellInfo spell, Unit? explicitTarget)
    {
        if (Store.GetScriptTargets(spell.Id).Count == 0)
        {
            return false;
        }

        bool locationOwned = _spellTargetSelectors.ContainsKey((spell.Id, SpellImplicitTarget.LocationScriptNearCaster));
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            bool location = !locationOwned
                && (effect.TargetA == SpellImplicitTarget.LocationScriptNearCaster || effect.TargetB == SpellImplicitTarget.LocationScriptNearCaster);
            if (effect.IsEmpty || !location && SelectorOf(effect) != SpellImplicitTarget.UnitScriptNearCaster)
            {
                continue;
            }

            (Unit? unit, GameObject? gameObject) = FindScriptLocationTarget(caster, spell, effect, i, explicitTarget);
            if (unit is null && gameObject is null)
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
    /// (Spell.cpp:566-585): a unit is the effect's unit, a game object is its object target (with the caster as carrier only for the effects that read one). Nothing within range leaves
    /// the destination alone and selects nothing; the cast itself is refused beforehand (<c>CheckCast</c>, <see cref="ScriptLocationTargetMissing"/>).
    /// Target B 46 beside another target A only sets the destination.
    /// </summary>
    private List<(Unit Unit, float Multiplier)> SelectScriptLocationTarget(SpellCast cast, SpellEffectInfo effect, int effectIndex, Unit? explicitTarget)
    {
        Unit caster = cast.Caster;
        (Unit? chosenUnit, GameObject? chosenObject) = FindScriptLocationTarget(caster, cast.Spell, effect, effectIndex, explicitTarget);
        WorldObject? found = chosenUnit is not null ? chosenUnit : chosenObject;
        if (found is null) return [];
        cast.Targets.Dest = (found.X, found.Y, found.Z);
        cast.Targets.Mask |= SpellCastTargetFlags.DestLocation;
        if (effect.TargetA != SpellImplicitTarget.LocationScriptNearCaster || effect.Effect == SpellEffectName.PersistentAreaAura) return [];
        if (chosenUnit is not null) return [(chosenUnit, 1f)];
        cast.ObjectTargetsByEffect[effectIndex] = [chosenObject!.Guid];
        // vmangos only calls AddGOTarget here (Spell.cpp:590-591): the object is the effect's target and no unit is. Only the effects that read an
        // object target (ActivateObject, DespawnObject) are carried by the caster through the unit-effect pipeline; a Dummy or aura effect must
        // not see the caster as its unit target.
        return effect.Effect is SpellEffectName.ActivateObject or SpellEffectName.DespawnObject ? [(caster, 1f)] : [];
    }

    /// <summary>Extra yards the grid pre-filter of the 46 unit search adds beyond the caster's reach, so a large target combat reach is still a candidate (the exact check follows).</summary>
    private const float MaxCombatReachMargin = 20f;

    /// <summary>The nearest listed unit or game object of the 46 search of <paramref name="effect"/> (a valid explicit unit first); both null when none is in range. Reads state only.</summary>
    private (Unit? Unit, GameObject? Object) FindScriptLocationTarget(Unit caster, SpellInfo spell, SpellEffectInfo effect, int effectIndex, Unit? explicitTarget)
    {
        if (caster.Map is not { } map) return (null, null);
        SpellStore.ScriptTarget[] rows = [.. Store.GetScriptTargets(spell.Id).Where(row => (row.InverseEffectMask & (1u << effectIndex)) == 0)];
        if (rows.Length == 0) return (null, null);

        float radius = TargetMapRadius(spell, effect, effectIndex);
        if (radius <= 0) radius = spell.Range.Max;
        if (radius >= 50_000f) radius = 200f; // mangos-classic CheckScriptTargeting bounds the "anywhere" range of an entry search
        // SPELLMOD_RADIUS applies to the search radius (vmangos Spell.cpp:2059-2062; the sibling game-object path does the same).
        if (radius > 0) radius = ModFloat(caster, spell, SpellModOp.Radius, radius);
        if (radius < 0) return (null, null);

        float DistanceSquared(WorldObject target) => WorldGeometry.DistanceSq3D(target, caster);

        bool UnitListed(Unit unit) => rows.Any(row => row.Type switch
        {
            ScriptTargetCreature => unit is Creature creature && unit.IsAlive && creature.Entry == row.TargetEntry,
            ScriptTargetDead => unit is Creature dead && !unit.IsAlive && dead.Entry == row.TargetEntry,
            ScriptTargetPlayer => unit is Player && unit.IsAlive && row.TargetEntry == 0,
            _ => false,
        });

        // The unit search measures combat distance, as vmangos NearestUnitFitConditionInCombatRangeCheck does (Spell.cpp:539, Object.h:603-606,
        // GetDistance with SizeFactor::CombatReach): centre distance minus both combat reaches, so a radius of 0 (28364 Damage vs. Guards: range
        // index 1, no radius) still finds a listed unit within melee reach.
        // Deviation: the explicit-unit check below also uses combat reach, while vmangos uses IsWithinDistInMap (bounding radius, Spell.cpp:519-533);
        // only the choice among listed units in that band differs.
        float casterReach = CombatReach(caster);
        bool UnitInCombatRange(Unit unit)
        {
            float reach = radius + casterReach + CombatReach(unit);
            return DistanceSquared(unit) <= reach * reach;
        }

        Unit? chosenUnit = explicitTarget is not null && UnitListed(explicitTarget) && UnitInCombatRange(explicitTarget)
            ? explicitTarget
            : UnitsInRadius(map, caster.X, caster.Y, caster.Z, radius + casterReach + MaxCombatReachMargin).Where(unit => UnitListed(unit) && UnitInCombatRange(unit))
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

        return chosenUnit is not null ? (chosenUnit, null) : (null, chosenObject);
    }
}
