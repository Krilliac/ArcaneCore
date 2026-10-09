using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells.Targets;

namespace ArcaneCore.Game.Spells;

/// <summary>One unit a cast affects: its effect mask, per-effect value multipliers (chain jumps) and hit result.</summary>
internal sealed class SpellTargetEntry
{
    public int EffectMask { get; set; }

    public float[] Multipliers { get; } = [1.0f, 1.0f, 1.0f];

    public SpellMissInfo Miss { get; set; }
}

public sealed partial class SpellSystem
{
    /// <summary>Enemy/friend relations used by implicit target selection.</summary>
    public ISpellTargetRelations Relations { get; set; } = CombatHookRelations.Instance;

    /// <summary>Party/raid membership for group targets and party area auras.</summary>
    public ISpellGroupResolver Groups { get; set; } = NoGroupResolver.Instance;

    /// <summary>
    /// Per effect, the units it hits (vmangos Spell::FillTargetMap / SetTargetMap, re-implemented):
    /// the caster; the explicit unit (with chain jumps when EffectChainTarget &gt; 1); random
    /// units near the caster; units in an area around the caster, around the explicit target or
    /// at the destination; a frontal cone; party/raid members. When target A only names a
    /// location (e.g. TARGET_LOCATION_CASTER_SRC for Arcane Explosion) target B picks the units.
    /// Area lists are filtered by relation, liveness and line of sight from the area centre, then
    /// capped at MaxAffectedTargets by random selection. Returns unit → entry in first-hit order.
    /// </summary>
    private Dictionary<Unit, SpellTargetEntry> SelectTargets(SpellCast cast, Unit? unitTarget)
    {
        var result = new Dictionary<Unit, SpellTargetEntry>();
        var order = new List<Unit>();
        IReadOnlyList<SpellEffectInfo> effects = cast.Spell.Effects;
        for (int i = 0; i < effects.Count; i++)
        {
            SpellEffectInfo effect = effects[i];
            if (effect.IsEmpty)
            {
                continue;
            }

            if (effect.Effect is SpellEffectName.Resurrect or SpellEffectName.ResurrectNew)
            {
                // vmangos Spell::SetTargetMap (Spell.cpp:3106-3117): the targets of the resurrect effects are the explicit unit and the
                // owner of the explicit corpse, whatever the implicit target says (the player spells have none: TARGET_NONE).
                if (unitTarget is not null)
                {
                    if (!result.TryGetValue(unitTarget, out SpellTargetEntry? resurrectEntry))
                    {
                        resurrectEntry = new SpellTargetEntry();
                        result[unitTarget] = resurrectEntry;
                        order.Add(unitTarget);
                    }

                    resurrectEntry.EffectMask |= 1 << i;
                }

                continue;
            }

            SpellImplicitTarget selector = IsLocationTarget(effect.TargetA) && effect.TargetB != SpellImplicitTarget.None && !IsLocationTarget(effect.TargetB)
                ? effect.TargetB
                : effect.TargetA;
            // Location32 also prepares the destination when target B selects the units.
            if (selector != effect.TargetA && effect.TargetA == SpellImplicitTarget.LocationUnitMinionPosition
                && effect.Effect != SpellEffectName.Duel)
                SpellTargetSelectors.SelectCasterRelativeLocation(cast, effect, MathF.PI * 0.25f);
            // TARGET_UNIT_ENEMY updates the explicit target for later effects too, including
            // UNIT / NONE selectors. Arcane Missiles' caster-selector exception leaves it alone.
            if (cast.MagnetTarget is { } magnet && cast.Targets.Unit == magnet.Guid)
            {
                unitTarget = magnet;
            }

            List<(Unit Unit, float Multiplier)>? units = SelectEffectTargets(cast, effect, i, selector, unitTarget);
            if (units is null)
            {
                ReportUnsupported("implicit target", (uint)selector, cast.Spell.Id);
                continue;
            }

            foreach ((Unit unit, float multiplier) in units)
            {
                if (!result.TryGetValue(unit, out SpellTargetEntry? entry))
                {
                    entry = new SpellTargetEntry();
                    result[unit] = entry;
                    order.Add(unit);
                }

                entry.EffectMask |= 1 << i;
                entry.Multipliers[i] = multiplier;
            }
        }

        // Dictionary enumeration order is insertion order only without removals; rebuild to be explicit.
        var ordered = new Dictionary<Unit, SpellTargetEntry>(order.Count);
        foreach (Unit unit in order)
        {
            ordered[unit] = result[unit];
        }

        return ordered;
    }

    /// <summary>null = the target type is not implemented; an empty list = nothing qualified.</summary>
    private List<(Unit Unit, float Multiplier)>? SelectEffectTargets(SpellCast cast, SpellEffectInfo effect, int effectIndex, SpellImplicitTarget selector, Unit? unitTarget)
    {
        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        Unit? explicitOrSelf = unitTarget ?? (cast.Targets.Mask == SpellCastTargetFlags.Self ? caster : null);
        switch (selector)
        {
            case SpellImplicitTarget.UnitCaster:
                // Arcane Missiles' channel aura uses UNIT_CASTER / NONE but is placed on
                // the enemy (vmangos Spell.cpp:249-255; SpellClassMask.h channel bit 11).
                if (effect.TargetB == SpellImplicitTarget.None && spell.IsFitToFamily(3, 11) && unitTarget is not null)
                {
                    Unit missileTarget = SelectMagnetTarget(cast, unitTarget, updateExplicitTarget: false);
                    return IsEnemy(caster, missileTarget) ? [(missileTarget, 1.0f)] : [];
                }

                return [(caster, 1.0f)];
            case SpellImplicitTarget.None:
                return [(unitTarget ?? caster, 1.0f)];
            case SpellImplicitTarget.LocationCasterHomeBind:
            case SpellImplicitTarget.LocationDatabase:
            case SpellImplicitTarget.LocationCasterSrc:
            case SpellImplicitTarget.LocationCasterTargetPosition:
            case SpellImplicitTarget.LocationCasterFishingSpot: // vmangos Spell.cpp:2859: the caster
                // A location-only effect (teleport, summon) acts on the caster.
                return [(caster, 1.0f)];
            case SpellImplicitTarget.LocationCasterDest:
                // Spell.cpp:3110-3117: resurrection uses the explicit unit or resolved corpse
                // owner here; other location-only effects still act on the caster.
                return effect.Effect is SpellEffectName.Resurrect or SpellEffectName.ResurrectNew
                    ? unitTarget is null ? [] : [(unitTarget, 1.0f)]
                    : [(caster, 1.0f)];
            case SpellImplicitTarget.GameObject:
            case SpellImplicitTarget.GameObjectItem:
                // The effect reads the explicit object or item of the target block itself (vmangos m_targets.getGOTarget /
                // getItemTarget); the caster carries the effect.
                return [(caster, 1.0f)];
            case SpellImplicitTarget.UnitEnemy:
                if (explicitOrSelf is null)
                {
                    return [];
                }

                Unit enemy = SelectMagnetTarget(cast, explicitOrSelf);
                // Spell::SetTargetMap: a redirected chain ends at the magnet; it never jumps
                // back into the protected party after consuming Grounding's charge.
                return !ReferenceEquals(enemy, explicitOrSelf) || cast.MagnetTarget is not null
                    ? [(enemy, 1.0f)]
                    : effect.ChainTarget > 1 ? Chain(cast, effect, selector, enemy) : [(enemy, 1.0f)];
            case SpellImplicitTarget.UnitFriend:
            case SpellImplicitTarget.Unit:
            case SpellImplicitTarget.UnitFriendChainHeal:
                if (explicitOrSelf is null)
                {
                    return [];
                }

                return ChainTargetsOf(cast, effect) > 1 ? Chain(cast, effect, selector, explicitOrSelf) : [(explicitOrSelf, 1.0f)];
            case SpellImplicitTarget.UnitParty:
            case SpellImplicitTarget.UnitRaid:
                return explicitOrSelf is not null && IsGroupMember(caster, explicitOrSelf, raid: selector == SpellImplicitTarget.UnitRaid)
                    ? [(explicitOrSelf, 1.0f)]
                    : [];
            case SpellImplicitTarget.UnitEnemyNearCaster:
            case SpellImplicitTarget.UnitFriendNearCaster:
            case SpellImplicitTarget.UnitNearCaster:
                return RandomNearCaster(cast, effect, selector);
            case SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc:
            {
                (float x, float y, float z) = SourceCentre(cast, effect);
                return Area(cast, effect, x, y, z, AreaRadius(cast, effect, selector), u => IsEnemy(caster, u), cone: false);
            }

            case SpellImplicitTarget.EnumUnitsEnemyWithinCasterRange:
                // vmangos PUSH_SELF_CENTER (Spell.cpp:2557-2558): the caster, whatever the source.
                return Area(cast, effect, caster.X, caster.Y, caster.Z, AreaRadius(cast, effect, selector), u => IsEnemy(caster, u), cone: false);
            case SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc:
            {
                (float x, float y, float z) = SourceCentre(cast, effect);
                return Area(cast, effect, x, y, z, AreaRadius(cast, effect, selector), u => IsFriend(cast, u), cone: false);
            }

            case SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc:
            case SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc:
            case SpellImplicitTarget.EnumUnitsPartyAoeAtDestLoc:
            {
                (float x, float y, float z) = DestinationCentre(cast, unitTarget);
                Func<Unit, bool> filter = selector switch
                {
                    SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc => u => IsEnemy(caster, u),
                    SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc => u => IsFriend(cast, u),
                    _ => u => IsAliveGroupMember(cast, caster, u, raid: false),
                };
                return Area(cast, effect, x, y, z, AreaRadius(cast, effect, selector), filter, cone: false);
            }

            case SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc:
            case SpellImplicitTarget.EnumUnitsPartyWithinCasterRange:
                return Area(cast, effect, caster.X, caster.Y, caster.Z, AreaRadius(cast, effect, selector),
                    u => IsAliveGroupMember(cast, caster, u, raid: false), cone: false);
            case SpellImplicitTarget.EnumUnitsRaidWithinCasterRange:
                return Area(cast, effect, caster.X, caster.Y, caster.Z, AreaRadius(cast, effect, selector),
                    u => IsAliveGroupMember(cast, caster, u, raid: true), cone: false);
            case SpellImplicitTarget.UnitFriendAndParty:
            {
                // vmangos TARGET_AREAEFFECT_PARTY: the explicit (or self) target's party around it.
                Unit centre = explicitOrSelf ?? caster;
                return Area(cast, effect, centre.X, centre.Y, centre.Z, AreaRadius(cast, effect, selector),
                    u => IsAliveGroupMember(cast, centre, u, raid: false), cone: false);
            }

            case SpellImplicitTarget.EnumUnitsEnemyInCone24:
            case SpellImplicitTarget.EnumUnitsEnemyInCone54:
                return Area(cast, effect, caster.X, caster.Y, caster.Z, AreaRadius(cast, effect, selector), u => IsEnemy(caster, u), cone: true);
            default:
                // Class lanes register further targets (SpellSystem.TargetSelectors.cs); null = not implemented.
                return TrySelectRegistered(cast, effect, effectIndex, selector, unitTarget);
        }
    }

    /// <summary>Location-only implicit targets (no unit of their own).</summary>
    private bool IsLocationTarget(SpellImplicitTarget target)
        => IsRegisteredLocationTarget(target) || target is SpellImplicitTarget.LocationCasterHomeBind or SpellImplicitTarget.LocationDatabase
            or SpellImplicitTarget.LocationCasterDest or SpellImplicitTarget.LocationCasterSrc
            or SpellImplicitTarget.LocationCasterTargetPosition;

    /// <summary>
    /// EffectRadius, or for "within caster range" targets without a radius the spell's maximum range, with the caster's
    /// SPELLMOD_RADIUS (vmangos Spell::SetTargetMap, Spell.cpp:2050-2062: the mod applies to either source).
    /// </summary>
    private float AreaRadius(SpellCast cast, SpellEffectInfo effect, SpellImplicitTarget selector)
    {
        SpellInfo spell = cast.Spell;
        float radius = effect.Radius > 0
            ? effect.Radius
            : selector is SpellImplicitTarget.EnumUnitsEnemyWithinCasterRange or SpellImplicitTarget.EnumUnitsPartyWithinCasterRange
                or SpellImplicitTarget.EnumUnitsRaidWithinCasterRange
                ? spell.Range.Max
                : 0f;
        return radius > 0 ? ModFloat(cast.Caster, spell, SpellModOp.Radius, radius) : radius;
    }

    /// <summary>
    /// The effect's chain length with the caster's SPELLMOD_JUMP_TARGETS (vmangos Spell.cpp:2058-2062 and
    /// InitializeDamageMultipliers :1945-1948).
    /// </summary>
    private int ChainTargetsOf(SpellCast cast, SpellEffectInfo effect)
        => Math.Max(ModInt(cast.Caster, cast.Spell, SpellModOp.JumpTargets, (int)effect.ChainTarget), 0);

    /// <summary>The destination of a dest-location area: the client's destination, else the explicit target, else the caster.</summary>
    private static (float X, float Y, float Z) DestinationCentre(SpellCast cast, Unit? unitTarget)
    {
        if (cast.Targets.HasDest)
        {
            return (cast.Targets.Dest.X, cast.Targets.Dest.Y, cast.Targets.Dest.Z);
        }

        Unit centre = unitTarget ?? cast.Caster;
        return (centre.X, centre.Y, centre.Z);
    }

    /// <summary>
    /// The centre of a source-location area (vmangos PUSH_SRC_CENTER, Spell.cpp:7968-7971: <c>m_targets.m_src</c>). An effect
    /// whose first target is TARGET_LOCATION_CASTER_SRC writes the casting object there first (Spell.cpp:2549-2555): the caster,
    /// or the trap of a cast made on behalf of a game object (whose position <see cref="CastFromObject"/> puts in the source).
    /// Otherwise the source the client sent, and the caster when there is none (vmangos would leave the source at 0,0,0).
    /// </summary>
    private (float X, float Y, float Z) SourceCentre(SpellCast cast, SpellEffectInfo effect)
    {
        bool hasSource = (cast.Targets.Mask & SpellCastTargetFlags.SourceLocation) != 0;
        bool casterSource = effect.TargetA == SpellImplicitTarget.LocationCasterSrc && _objectCastDepth == 0;
        return hasSource && !casterSource ? cast.Targets.Source : (cast.Caster.X, cast.Caster.Y, cast.Caster.Z);
    }

    private bool IsEnemy(Unit caster, Unit unit) => unit.IsAlive && Relations.IsHostile(caster, unit);

    private bool IsFriend(SpellCast cast, Unit unit)
        => unit.IsAlive && Relations.IsFriendly(cast.Caster, unit)
            && !(ReferenceEquals(unit, cast.Caster) && cast.Spell.HasAttribute(SpellAttributesEx.CantTargetSelf));

    private bool IsAliveGroupMember(SpellCast cast, Unit reference, Unit unit, bool raid)
        => unit.IsAlive && IsGroupMember(reference, unit, raid)
            && !(ReferenceEquals(unit, cast.Caster) && cast.Spell.HasAttribute(SpellAttributesEx.CantTargetSelf));

    /// <summary>Whether <paramref name="unit"/> shares <paramref name="reference"/>'s party (or raid).</summary>
    public bool IsGroupMember(Unit reference, Unit unit, bool raid)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(unit);
        return ReferenceEquals(reference, unit) || Groups.GetGroupMembers(reference, raid).Contains(unit.Guid);
    }

    /// <summary>
    /// Line of sight between two units for area and chain targets, through the vmap-los seam
    /// (<c>map.Collision</c>, eye height on both ends; no collision data means visible), honouring
    /// IGNORE_LINE_OF_SIGHT. Explicit targets are checked by <see cref="SpellLineOfSight"/> in CheckCast.
    /// </summary>
    public bool IsInLineOfSight(SpellInfo spell, Unit from, Unit to)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return ReferenceEquals(from, to) || spell.HasAttribute(SpellAttributesEx2.IgnoreLineOfSight)
            || (from.Map is { } map && map.Collision.IsWithinLineOfSight(from, to));
    }

    /// <summary>Line of sight from an area centre (a point raised to eye height) to a unit (vmangos Spell::CheckTarget from the AoE centre).</summary>
    private static bool IsInLineOfSightFromPoint(SpellInfo spell, Map map, float x, float y, float z, Unit to)
        => spell.HasAttribute(SpellAttributesEx2.IgnoreLineOfSight)
            || map.Collision.IsInLineOfSight(x, y, z + MapCollision.DefaultEyeHeight, to.X, to.Y, to.Z + MapCollision.DefaultEyeHeight);

    /// <summary>Units of the caster's map within <paramref name="radius"/> (3D, plus the unit's bounding radius) of a point, deduplicated, in grid order.</summary>
    private static List<Unit> UnitsInRadius(Map map, float x, float y, float z, float radius)
    {
        var objects = new List<WorldObject>();
        map.Grids.CollectObjects(x, y, radius, objects);
        var seen = new HashSet<Unit>(ReferenceEqualityComparer.Instance);
        var units = new List<Unit>();
        foreach (WorldObject obj in objects)
        {
            if (obj is not Unit unit || !unit.IsInWorld || !ReferenceEquals(unit.Map, map) || !seen.Add(unit))
            {
                continue;
            }

            float dx = unit.X - x;
            float dy = unit.Y - y;
            float dz = unit.Z - z;
            float reach = radius + unit.BoundingRadius;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= reach * reach)
            {
                units.Add(unit);
            }
        }

        return units;
    }

    private List<(Unit Unit, float Multiplier)> Area(SpellCast cast, SpellEffectInfo effect, float x, float y, float z, float radius, Func<Unit, bool> filter, bool cone)
    {
        _ = effect;
        Unit caster = cast.Caster;
        if (caster.Map is not { } map || radius <= 0)
        {
            return [];
        }

        var found = new List<Unit>();
        foreach (Unit unit in UnitsInRadius(map, x, y, z, radius))
        {
            if (!filter(unit))
            {
                continue;
            }

            if (cone && (ReferenceEquals(unit, caster) || !Combat.MapCombat.HasInArc(caster, unit, SpellConstants.ConeArc)))
            {
                continue;
            }

            if (!IsInLineOfSightFromPoint(cast.Spell, map, x, y, z, unit))
            {
                continue;
            }

            found.Add(unit);
        }

        CapTargets(found, cast.Spell.MaxAffectedTargets);
        return [.. found.Select(u => (u, 1.0f))];
    }

    /// <summary>vmangos MaxAffectedTargets: keep a random subset of at most <paramref name="max"/> units (0 = no cap).</summary>
    private void CapTargets(List<Unit> units, uint max)
    {
        if (max == 0 || units.Count <= max)
        {
            return;
        }

        while (units.Count > max)
        {
            units.RemoveAt(Random.Next(units.Count));
        }
    }

    /// <summary>
    /// TARGET_UNIT_*_NEAR_CASTER (vmangos TARGET_RANDOM_*_CHAIN_IN_AREA): up to EffectChainTarget
    /// (at least one) random qualifying units within the effect radius (or spell range) of the caster.
    /// </summary>
    private List<(Unit Unit, float Multiplier)> RandomNearCaster(SpellCast cast, SpellEffectInfo effect, SpellImplicitTarget selector)
    {
        Unit caster = cast.Caster;
        float radius = effect.Radius > 0 ? effect.Radius : cast.Spell.Range.Max;
        radius = radius > 0 ? ModFloat(caster, cast.Spell, SpellModOp.Radius, radius) : radius;
        if (caster.Map is not { } map || radius <= 0)
        {
            return [];
        }

        var candidates = UnitsInRadius(map, caster.X, caster.Y, caster.Z, radius).Where(u => selector switch
        {
            SpellImplicitTarget.UnitEnemyNearCaster => IsEnemy(caster, u),
            SpellImplicitTarget.UnitFriendNearCaster => IsFriend(cast, u),
            _ => u.IsAlive && !ReferenceEquals(u, caster),
        } && IsInLineOfSight(cast.Spell, caster, u)).ToList();
        int count = Math.Max(1, ChainTargetsOf(cast, effect));
        if (cast.Spell.MaxAffectedTargets > 0)
        {
            count = Math.Min(count, (int)cast.Spell.MaxAffectedTargets);
        }

        var picked = new List<(Unit, float)>();
        while (picked.Count < count && candidates.Count > 0)
        {
            int index = Random.Next(candidates.Count);
            picked.Add((candidates[index], 1.0f));
            candidates.RemoveAt(index);
        }

        return picked;
    }

    /// <summary>
    /// Chain targets (vmangos Spell::SetTargetMap chain branch, re-implemented): from the primary
    /// target, jump up to EffectChainTarget - 1 times to a qualifying unit within
    /// <see cref="SpellConstants.ChainJumpRadius"/> of the previous one and in its line of sight;
    /// damage chains take the nearest, chain heals the most injured. Each jump multiplies the
    /// value by Spell.dbc DmgMultiplier.
    /// </summary>
    private List<(Unit Unit, float Multiplier)> Chain(SpellCast cast, SpellEffectInfo effect, SpellImplicitTarget selector, Unit primary)
    {
        Unit caster = cast.Caster;
        var chain = new List<(Unit, float)> { (primary, 1.0f) };
        if (caster.Map is not { } map || !ReferenceEquals(primary.Map, map))
        {
            return chain;
        }

        int max = ChainTargetsOf(cast, effect);
        if (cast.Spell.MaxAffectedTargets > 0)
        {
            max = Math.Min(max, (int)cast.Spell.MaxAffectedTargets);
        }

        bool hostile = selector == SpellImplicitTarget.UnitEnemy
            || (selector == SpellImplicitTarget.Unit && Relations.IsHostile(caster, primary));
        bool heal = selector == SpellImplicitTarget.UnitFriendChainHeal;
        float jumpRadius = ChainJumpRadiusFor(cast, effect);
        // vmangos Spell.cpp:1766 / :1920: SPELLMOD_EFFECT_PAST_FIRST on the per-jump multiplier (Improved Chain Heal shape).
        float factor = effect.DamageMultiplier is > 0f and not 1.0f ? effect.DamageMultiplier : 1.0f;
        factor = ModFloat(caster, cast.Spell, SpellModOp.EffectPastFirst, factor);
        float multiplier = 1.0f;
        var used = new HashSet<Unit>(ReferenceEqualityComparer.Instance) { primary };
        Unit last = primary;
        while (chain.Count < max)
        {
            Unit? next = null;
            float best = float.MaxValue;
            foreach (Unit unit in UnitsInRadius(map, last.X, last.Y, last.Z, jumpRadius))
            {
                if (used.Contains(unit) || !(hostile ? IsEnemy(caster, unit) : IsFriend(cast, unit)) || !IsInLineOfSight(cast.Spell, last, unit))
                {
                    continue;
                }

                float score = heal
                    ? (unit.MaxHealth == 0 ? 1f : (float)unit.Health / unit.MaxHealth)
                    : DistanceSquared(last, unit);
                if (score < best)
                {
                    best = score;
                    next = unit;
                }
            }

            if (next is null)
            {
                break;
            }

            multiplier *= factor;
            chain.Add((next, multiplier));
            used.Add(next);
            last = next;
        }

        return chain;
    }

    private static float DistanceSquared(Unit a, Unit b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
