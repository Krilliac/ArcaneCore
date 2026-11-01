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
        if (selector == SpellImplicitTarget.EnumUnitsScriptAoeAtDestLoc && !_spellTargetSelectors.ContainsKey((cast.Spell.Id, selector)))
        {
            return SelectScriptAoeAtDestination(cast, effect, effectIndex, unitTarget);
        }

        // A spell-specific selector (Destroy Egg's, BlackwingLairTargetModule) keeps precedence over the generic 46 selector.
        if (selector == SpellImplicitTarget.LocationScriptNearCaster && !_spellTargetSelectors.ContainsKey((cast.Spell.Id, selector)))
        {
            return SelectScriptLocationTarget(cast, effect, effectIndex, unitTarget);
        }

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

    /// <summary>
    /// TARGET_ENUM_UNITS_SCRIPT_AOE_AT_DEST_LOC (vmangos Spell.cpp:2405-2470): nobody for a persistent area aura, the caster for a summon, otherwise
    /// the script-target area fill around the destination. A TARGET_LOCATION_DATABASE target A puts the destination at the spell's
    /// spell_target_position row (Spell::SetTargetMap) when it is on the caster's map; else the cast's destination, explicit target or caster.
    /// </summary>
    private List<(Unit Unit, float Multiplier)> SelectScriptAoeAtDestination(SpellCast cast, SpellEffectInfo effect, int effectIndex, Unit? unitTarget)
    {
        if (effect.Effect == SpellEffectName.PersistentAreaAura)
        {
            return [];
        }

        if (effect.Effect is SpellEffectName.Summon or SpellEffectName.SummonWild or SpellEffectName.SummonObjectWild)
        {
            return [(cast.Caster, 1.0f)];
        }

        (float X, float Y, float Z) centre = effect.TargetA == SpellImplicitTarget.LocationDatabase
            && Store.GetTargetPosition(cast.Spell.Id) is { } row && row.MapId == cast.Caster.MapId
                ? (row.X, row.Y, row.Z)
                : DestinationCentre(cast, unitTarget);
        return SelectScriptTargets(cast, effect, effectIndex, nearest: false, unitTarget, centre);
    }

    /// <summary>The implicit targets the built-in switch of <c>SelectEffectTargets</c> (and its script-target defaults) answers with a list, never null.</summary>
    private static readonly HashSet<uint> s_builtInTargets =
    [
        0, 1, 6, 7, 8, 9, 15, 16, 17, 18, 20, 21, 22, 23, 24, 25, 26, 28, 30, 31, 33, 34, 35, 36, 37, 38, 39, 45, 46, 53, 54, 56, 57,
        2, 3, 4, SpellTargetSelectors.UnitsScriptInCone,
    ];

    /// <summary>
    /// The unit-or-location selector <c>SelectTargets</c> uses for <paramref name="effect"/>: target B when target A only names a location and B
    /// picks the units, else target A.
    /// </summary>
    public SpellImplicitTarget SelectorOf(SpellEffectInfo effect)
        => IsLocationTarget(effect.TargetA) && effect.TargetB != SpellImplicitTarget.None && !IsLocationTarget(effect.TargetB)
            ? effect.TargetB
            : effect.TargetA;

    /// <summary>
    /// Whether a cast of <paramref name="spell"/> can select the units of <paramref name="effect"/>: false is exactly what makes <c>SelectTargets</c>
    /// report an "implicit target" as unsupported (a static answer for the QA gap check, against the selectors registered so far). An empty effect and
    /// the resurrect effects (which take the explicit unit whatever the target says) never need one.
    /// </summary>
    public bool CanSelectImplicitTarget(SpellInfo spell, SpellEffectInfo effect)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(effect);
        if (effect.IsEmpty || effect.Effect is SpellEffectName.Resurrect or SpellEffectName.ResurrectNew)
        {
            return true;
        }

        SpellImplicitTarget selector = SelectorOf(effect);
        // The game object script targets (40, 51, 52) select a list only for ACTIVATE_OBJECT (SelectScriptGameObjects).
        return s_builtInTargets.Contains((uint)selector) || (selector is (SpellImplicitTarget)40 or (SpellImplicitTarget)51 or (SpellImplicitTarget)52
                && effect.Effect == SpellEffectName.ActivateObject) || _targetSelectors.ContainsKey(selector)
            || _spellTargetSelectors.ContainsKey((spell.Id, selector));
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
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterBack] = Location(1f),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterLeft] = Location(0.5f),
        [(SpellImplicitTarget)SpellTargetSelectors.LocationCasterRight] = Location(-0.5f),
        [(SpellImplicitTarget)SpellTargetSelectors.UnitRaidAndClass] = new(SpellTargetSelectors.SelectRaidAndClass, LocationOnly: false),
        // Targets 11, 58 and 62 have no SetTargetMap case in vmangos (SpellDefines.h:66 TARGET_PLAYER_NYI, :113 TARGET_UNIT_RAID_NEAR_CASTER and
        // :117 TARGET_PLAYER_RAID_NYI), so a spell using one selects no unit from them; an explicit empty selector says that on purpose.
        [(SpellImplicitTarget)SpellTargetSelectors.UnusedTarget11] = new(static (_, _, _, _) => [], LocationOnly: false),
        [(SpellImplicitTarget)SpellTargetSelectors.UnusedTarget58] = new(static (_, _, _, _) => [], LocationOnly: false),
        [(SpellImplicitTarget)SpellTargetSelectors.UnusedTarget62] = new(static (_, _, _, _) => [], LocationOnly: false),
    };

    private static TargetSelectorEntry Location(float piMultiple)
        => new((_, cast, effect, _) => SpellTargetSelectors.SelectCasterRelativeLocation(cast, effect, MathF.PI * piMultiple), LocationOnly: true);
}
