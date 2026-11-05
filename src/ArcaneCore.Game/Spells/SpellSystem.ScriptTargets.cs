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
    /// beforehand (<see cref="ScriptLocationTargetMissing"/>); a 38 effect of a spell with no rows and no spell-specific selector fails it
    /// with BAD_TARGETS too (<see cref="HasScriptTargetWithoutRows"/>, Spell.cpp:449-460). When an item casts the spell (vmangos m_CastItem), the
    /// unit found by 38 also becomes the cast's explicit unit target (Spell.cpp:567-568: Toss Fuel on Bonfire 28806, Dominion of Soul 16053);
    /// <c>SelectTargets</c> then hands that unit to the effects selected after the 38 effect as their explicit unit target.
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
            if (nearestUnit is null) return [];
            if (cast.CastItem is not null)
            {
                // vmangos CheckScriptTargeting: "if (m_CastItem) m_targets.setUnitTarget(unitScriptTarget)" for target 38 (Spell.cpp:567-568).
                cast.Targets.Unit = nearestUnit.Guid;
                cast.Targets.Mask |= SpellCastTargetFlags.Unit;
            }

            return [(nearestUnit, 1f)];
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
                })
                && ScriptTargetConditionHolds(row, cast.Caster, unit));
        }

        List<Unit> units = [.. UnitsInRadius(map, x, y, z, radius).Where(Matches)
            .Where(unit => IsInLineOfSightFromPoint(cast.Spell, map, x, y, z, unit))];
        CapTargets(units, MaxTargetsOf(cast.Spell));
        return [.. units.Select(unit => (unit, 1f))];
    }

    /// <summary>
    /// The row's <c>conditionId</c> (vmangos NearestUnitFitConditionInCombatRangeCheck and NearestGameObjectEntryFitConditionInObjectRangeCheck,
    /// GridNotifiers.h:672 and :1210: <c>IsConditionSatisfied(conditionId, candidate, map, caster)</c>, checked once entry, state and range fit): the
    /// candidate is the condition's target and the caster its source, through the creature system's relay-condition path. A row with no condition
    /// holds; a condition that is missing or cannot be decided here (no creature system on the map, an undecidable type) does not, so the
    /// candidate is not selected. World thread only.
    /// </summary>
    private static bool ScriptTargetConditionHolds(SpellStore.ScriptTarget row, Unit caster, WorldObject candidate)
        => row.ConditionId == 0
            || caster.Map?.FindUpdater<CreatureMapSystem>()?.ScriptTargetConditionHolds(row.ConditionId, candidate, caster) == true;

    /// <summary>
    /// Whether an effect of <paramref name="spell"/> has target 46 (as target A or B, as vmangos SetTargetMap runs both) or target 38 (as target A, or as
    /// the selector when target A only names a location) through the generic selector while the spell has no spell_script_target row. vmangos
    /// Spell::CheckScriptTargeting answers SPELL_FAILED_BAD_TARGETS for both whether or not the cast is triggered (Spell.cpp:449-460). A spell-specific
    /// selector registered for the target (the Blackwing Lair and Molten Core modules, Twin Emperors) owns its own targeting and is not refused here.
    /// </summary>
    private bool HasScriptTargetWithoutRows(SpellInfo spell)
    {
        if (Store.GetScriptTargets(spell.Id).Count != 0)
        {
            return false;
        }

        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty)
            {
                continue;
            }

            if ((effect.TargetA == SpellImplicitTarget.LocationScriptNearCaster || effect.TargetB == SpellImplicitTarget.LocationScriptNearCaster)
                && !_spellTargetSelectors.ContainsKey((spell.Id, SpellImplicitTarget.LocationScriptNearCaster)))
            {
                return true;
            }

            if ((effect.TargetA == SpellImplicitTarget.UnitScriptNearCaster || SelectorOf(effect) == SpellImplicitTarget.UnitScriptNearCaster)
                && !_spellTargetSelectors.ContainsKey((spell.Id, SpellImplicitTarget.UnitScriptNearCaster)))
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
    /// SetTargetMap :2848-2856). A spell with no rows is refused by <see cref="HasScriptTargetWithoutRows"/> instead. Both run at cast time only,
    /// after the range checks (vmangos reaches CheckScriptTargeting from SetTargetMap, after CheckCast).
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
    /// (Spell.cpp:566-585): a unit is the effect's unit, a game object is its object target (carried by the caster for every effect except those <see cref="CannotBeCarried"/> names: aura, damage, heal, power and instakill effects; the carrier is no unit target, see <c>ObjectCarriers</c>). Nothing within range leaves
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
        // The destination flag must not turn a self-aimed cast into a "dest only" one for the effects selected after this one
        // (a ForSelf A-unit / B46 effect needs its explicit-or-self fallback): the flag keeps the self aim (SelectEffectTargets).
        if (cast.Targets.Mask == SpellCastTargetFlags.Self) cast.ScriptTargetKeptSelfAim = true;
        cast.Targets.Mask |= SpellCastTargetFlags.DestLocation;
        if (effect.TargetA != SpellImplicitTarget.LocationScriptNearCaster || effect.Effect == SpellEffectName.PersistentAreaAura) return [];
        if (chosenUnit is not null) return [(chosenUnit, 1f)];
        cast.ObjectTargetsByEffect[effectIndex] = [chosenObject!.Guid];
        // vmangos calls AddGOTarget here (Spell.cpp:590-591) and DoAllEffectOnTarget(GOTargetInfo) then runs HandleEffects(nullptr, nullptr, go, i)
        // (Spell.cpp:1841-1843), so a summon (SummonWild, SummonObjectWild), Dummy or activate effect still runs and reads the destination. The unit pipeline
        // needs a unit, so the caster carries the effect: the carrier is no hit (no hit roll, no immunity test, no threat, procs or outcome notice; see
        // IsObjectCarriedEffect and ApplyEffects). An effect that needs a unit target (aura, direct damage, heal, power) is a no-op for a null unit
        // in vmangos (EffectSchoolDMG, EffectHeal, EffectInstaKill ... return on !unitTarget), so it is skipped instead of landing on the carrier.
        if (CannotBeCarried(effect.Effect)) return [];
        cast.ObjectCarriers[effectIndex] = caster;
        return [(caster, 1f)];
    }

    /// <summary>
    /// An effect that needs a real unit target, which the caster standing in as the carrier of a found game object must not receive: the aura
    /// effects, the direct damage and healing effects and the power effects (vmangos returns from each of them when its unit target is null).
    /// Dummy, summon, activate-object, trigger and send-event effects run for a game object and stay carried.
    /// </summary>
    private static bool CannotBeCarried(SpellEffectName effect) => IsAuraApplyingEffect(effect)
        || effect is SpellEffectName.Instakill or SpellEffectName.SchoolDamage or SpellEffectName.EnvironmentalDamage or SpellEffectName.PowerDrain
            or SpellEffectName.HealthLeech or SpellEffectName.Heal or SpellEffectName.WeaponDamageNoschool or SpellEffectName.Energize
            or SpellEffectName.WeaponPercentDamage or SpellEffectName.WeaponDamage or SpellEffectName.NormalizedWeaponDmg or SpellEffectName.PowerBurn
            or SpellEffectName.HealthFunnel or SpellEffectName.PowerFunnel or SpellEffectName.HealMaxHealth or SpellEffectName.HealMechanical;

    /// <summary>An effect that puts an aura on its unit target, which a game object carrier must not receive.</summary>
    private static bool IsAuraApplyingEffect(SpellEffectName effect) => effect is SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty
        or SpellEffectName.ApplyAreaAuraPet or SpellEffectName.ApplyAreaAuraFriend or SpellEffectName.ApplyAreaAuraEnemy
        or SpellEffectName.ApplyAreaAuraRaid or SpellEffectName.ApplyAreaAuraOwner or SpellEffectName.PersistentAreaAura;

    /// <summary>Whether <paramref name="target"/> only carries effect <paramref name="effectIndex"/> for a game object found by target A 46 (no unit hit). A unit that target B also selected is a real target and not a carrier.</summary>
    private static bool IsObjectCarriedEffect(SpellCast cast, int effectIndex, Unit target)
        => cast.ObjectCarriersOrNull is { } carriers && carriers.TryGetValue(effectIndex, out Unit? carrier) && ReferenceEquals(carrier, target);

    /// <summary>
    /// Whether every effect in <paramref name="effectMask"/> only carries a found game object for <paramref name="target"/> (see <see cref="IsObjectCarriedEffect"/>):
    /// the entry then has no unit hit at all. False for an empty mask.
    /// </summary>
    private static bool IsCarrierOnlyEntry(SpellCast cast, Unit target, int effectMask)
    {
        if (effectMask == 0 || cast.ObjectCarriersOrNull is null)
        {
            return false;
        }

        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if ((effectMask & (1 << i)) != 0 && !IsObjectCarriedEffect(cast, i, target))
            {
                return false;
            }
        }

        return true;
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
        bool anywhere = radius >= 50_000f;
        // SPELLMOD_RADIUS applies to the search radius first (vmangos Spell.cpp:2059-2062: the mod, then CheckScriptTargeting; the sibling game-object path does the same).
        if (radius > 0) radius = ModFloat(caster, spell, SpellModOp.Radius, radius);
        if (radius < 0) return (null, null);
        // mangos-classic CheckScriptTargeting bounds the "anywhere" range of an entry search to 200 yd; vmangos has no cap (documented limit in docs/areas/spells.md).
        if (anywhere) radius = MathF.Min(radius, 200f);

        float DistanceSquared(WorldObject target) => WorldGeometry.DistanceSq3D(target, caster);

        bool UnitListed(Unit unit) => rows.Any(row => row.Type switch
        {
            ScriptTargetCreature => unit is Creature creature && unit.IsAlive && creature.Entry == row.TargetEntry,
            ScriptTargetDead => unit is Creature dead && !unit.IsAlive && dead.Entry == row.TargetEntry,
            ScriptTargetPlayer => unit is Player && unit.IsAlive && row.TargetEntry == 0,
            _ => false,
        } && ScriptTargetConditionHolds(row, caster, unit));

        // Limits (docs/areas/spells.md): vmangos walks the rows one by one and shrinks the radius to the last distance found, so a later row wins only
        // when nearer (Spell.cpp:466-556); this search takes the nearest unit and the nearest object over all rows at once, so the choice can differ
        // when rows overlap in range. The game object distance here adds only the object's bounding radius; vmangos IsWithinDistInMap adds the caster's too
        // (Object.cpp:1626-1630), a documented limit. The search can run up to three times per cast (each landing check and the selection).
        // A type 0 row with no entry (the spell focus object of the cast) is not selected here.
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
            SpellStore.ScriptTarget[] objectRows = [.. rows.Where(row => row.Type == 0 && row.TargetEntry != 0)];
            if (objectRows.Length > 0 && map.FindUpdater<GameObjectMapSystem>() is { } objects)
            {
                chosenObject = objects.GameObjects.Where(go => go.IsSpawned
                        && DistanceSquared(go) <= (radius + go.BoundingRadius) * (radius + go.BoundingRadius)
                        && objectRows.Any(row => row.TargetEntry == go.Entry && ScriptTargetConditionHolds(row, caster, go)))
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
