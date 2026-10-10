using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Targets;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// A per-spell veto of one selected unit (mangos-classic Spell::OnCheckTarget, the spell-id exceptions such as Nefarian's class calls):
/// false drops the unit from the effect's targets whatever selected it.
/// </summary>
public delegate bool SpellTargetFilter(SpellCast cast, SpellEffectInfo effect, Unit target);

/// <summary>
/// An implicit-target selector registered through <see cref="SpellSystem.RegisterTargetSelector"/>:
/// the units one effect hits. Returns an empty list when nothing qualifies (never null).
/// </summary>
public delegate List<(Unit Unit, float Multiplier)> SpellTargetSelectorHandler(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? unitTarget);

public sealed partial class SpellSystem
{
    private readonly record struct TargetSelectorEntry(SpellTargetSelectorHandler Handler, bool LocationOnly);

    private readonly Dictionary<SpellImplicitTarget, TargetSelectorEntry> _targetSelectors = CreateDefaultTargetSelectors();

    private readonly Dictionary<(uint Spell, SpellImplicitTarget Target), SpellTargetSelectorHandler> _spellTargetSelectors = [];
    private readonly HashSet<uint> _closestAreaTargetSpells = [];

    /// <summary>
    /// Choose the closest units before applying MaxAffectedTargets for this spell's area effects
    /// (vmangos SpellScript::OnSetTargetMap selectClosestTargets). A duplicate registration is an error.
    /// </summary>
    public void RegisterClosestAreaTargets(uint spellId)
    {
        if (!_closestAreaTargetSpells.Add(spellId))
            throw new InvalidOperationException($"Spell {spellId} already selects closest area targets.");
    }

    /// <summary>
    /// Register the selector of an implicit target the built-in switch does not know. A location-only
    /// target names a place, not a unit, so target B picks the units (see <c>SelectTargets</c>).
    /// Registering a target twice (or one the built-in switch already handles) is an error: two owners
    /// of one target value would silently disagree about what it means.
    /// </summary>
    public void RegisterTargetSelector(SpellImplicitTarget target, SpellTargetSelectorHandler handler, bool locationOnly)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_targetSelectors.TryAdd(target, new TargetSelectorEntry(handler, locationOnly)))
        {
            throw new InvalidOperationException($"Implicit target {(uint)target} already has a selector.");
        }
    }

    /// <summary>
    /// A spell-specific selector for an implicit target the built-in switch does not handle (a raid script's spell_script_target rows,
    /// for one spell). It is consulted before the target's general registered selector; other spells keep their existing handling.
    /// Registering one spell and target twice is an error.
    /// </summary>
    public void RegisterSpellTargetSelector(uint spell, SpellImplicitTarget target, SpellTargetSelectorHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_spellTargetSelectors.TryAdd((spell, target), handler))
        {
            throw new InvalidOperationException($"Spell {spell} already owns implicit target {(uint)target}.");
        }
    }

    private readonly Dictionary<uint, SpellTargetFilter> _spellTargetFilters = [];

    /// <summary>
    /// A filter over every unit any implicit target of <paramref name="spell"/> selects, built-in or registered (mangos-classic
    /// Spell::OnCheckTarget). It runs after the selection, so an area capped at MaxAffectedTargets is capped first. Registering one
    /// spell twice is an error.
    /// </summary>
    public void RegisterSpellTargetFilter(uint spell, SpellTargetFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!_spellTargetFilters.TryAdd(spell, filter))
        {
            throw new InvalidOperationException($"Spell {spell} already has a target filter.");
        }
    }

    private List<(Unit Unit, float Multiplier)> FilterRegistered(SpellCast cast, SpellEffectInfo effect, List<(Unit Unit, float Multiplier)> units)
        => _spellTargetFilters.TryGetValue(cast.Spell.Id, out SpellTargetFilter? filter)
            ? units.FindAll(entry => filter(cast, effect, entry.Unit))
            : units;

    /// <summary>Whether <paramref name="target"/> has a registered selector flagged location-only.</summary>
    public bool IsRegisteredLocationTarget(SpellImplicitTarget target)
        => _targetSelectors.TryGetValue(target, out TargetSelectorEntry entry) && entry.LocationOnly;

    /// <summary>
    /// The unit script targets (38, 7) follow the world's spell_script_target rows whenever the spell has any; a spell-specific
    /// selector for them (the Blackwing Lair module's, written from the ClassicDB rows) is only the fallback for a world without
    /// them. Every other implicit target asks the spell-specific selector first, then the general one.
    /// </summary>
    private List<(Unit Unit, float Multiplier)>? TrySelectRegistered(SpellCast cast, SpellEffectInfo effect, int effectIndex, SpellImplicitTarget selector, Unit? unitTarget)
    {
        bool unitScriptTarget = selector is SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc or SpellImplicitTarget.UnitScriptNearCaster;
        if (unitScriptTarget && Store.GetScriptTargets(cast.Spell.Id).Count > 0)
        {
            return SelectScriptTargets(cast, effect, effectIndex, selector == SpellImplicitTarget.UnitScriptNearCaster, unitTarget);
        }

        return _spellTargetSelectors.TryGetValue((cast.Spell.Id, selector), out SpellTargetSelectorHandler? handler)
            ? handler(this, cast, effect, unitTarget)
            : unitScriptTarget
                ? SelectScriptTargets(cast, effect, effectIndex, selector == SpellImplicitTarget.UnitScriptNearCaster, unitTarget)
            : _targetSelectors.TryGetValue(selector, out TargetSelectorEntry entry) ? entry.Handler(this, cast, effect, unitTarget) : null;
    }

    private static Dictionary<SpellImplicitTarget, TargetSelectorEntry> CreateDefaultTargetSelectors() => new()
    {
        [SpellImplicitTarget.LocationUnitMinionPosition] = new((_, cast, effect, _) =>
            effect.Effect == SpellEffectName.Duel ? [(cast.Caster, 1.0f)]
                : SpellTargetSelectors.SelectCasterRelativeLocation(cast, effect, MathF.PI * 0.25f), LocationOnly: true),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterFrontRight] = Location(1.75f),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterBackRight] = Location(1.25f),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterBackLeft] = Location(0.75f),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterFrontLeft] = Location(0.25f),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterFront] = Location(0f),
        [(SpellImplicitTarget)SpellTargetSelectors.UnitRaidAndClass] = new(SpellTargetSelectors.SelectRaidAndClass, LocationOnly: false),
    };

    private static TargetSelectorEntry Location(float piMultiple)
        => new((_, cast, effect, _) => SpellTargetSelectors.SelectCasterRelativeLocation(cast, effect, MathF.PI * piMultiple), LocationOnly: true);
}
