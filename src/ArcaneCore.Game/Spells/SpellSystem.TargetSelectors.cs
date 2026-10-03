using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Targets;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// An implicit-target selector registered through <see cref="SpellSystem.RegisterTargetSelector"/>:
/// the units one effect hits. Returns an empty list when nothing qualifies (never null).
/// </summary>
public delegate List<(Unit Unit, float Multiplier)> SpellTargetSelectorHandler(SpellSystem system, SpellCast cast, SpellEffectInfo effect, Unit? unitTarget);

public sealed partial class SpellSystem
{
    private readonly record struct TargetSelectorEntry(SpellTargetSelectorHandler Handler, bool LocationOnly);

    private readonly Dictionary<SpellImplicitTarget, TargetSelectorEntry> _targetSelectors = CreateDefaultTargetSelectors();

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

    /// <summary>Whether <paramref name="target"/> has a registered selector flagged location-only.</summary>
    public bool IsRegisteredLocationTarget(SpellImplicitTarget target)
        => _targetSelectors.TryGetValue(target, out TargetSelectorEntry entry) && entry.LocationOnly;

    private List<(Unit Unit, float Multiplier)>? TrySelectRegistered(SpellCast cast, SpellEffectInfo effect, SpellImplicitTarget selector, Unit? unitTarget)
        => _targetSelectors.TryGetValue(selector, out TargetSelectorEntry entry) ? entry.Handler(this, cast, effect, unitTarget) : null;

    private static Dictionary<SpellImplicitTarget, TargetSelectorEntry> CreateDefaultTargetSelectors() => new()
    {
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
