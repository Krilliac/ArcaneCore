namespace ArcaneCore.Game.Maps;

/// <summary>What state an <see cref="IMapUpdater"/> touches (the declared access contract of W2.2).</summary>
public enum MapAccess
{
    /// <summary>Not classified yet: it runs inside the world's serial gate while maps update in parallel.</summary>
    Unclassified = 0,

    /// <summary>
    /// Only the state of the map it is updating (its objects, grids and per-map systems), plus thread-safe services (packet sends,
    /// the fault and anomaly ledgers, logging, posting world commands). It runs outside the serial gate, concurrently with other maps.
    /// </summary>
    MapLocal = 1,

    /// <summary>Shared mutable state of world features as well: it always runs inside the serial gate.</summary>
    Global = 2,
}

/// <summary>
/// Declares an updater's <see cref="MapAccess"/> (docs/research/multithreading-plan-20261017.md, W2.2). An updater without it is
/// <see cref="MapAccess.Unclassified"/>, which is safe: with <c>World:Parallel:MapUpdateThreads</c> above 1 it runs inside the
/// serial gate, so it never runs at the same time as another map's gated work. Mark a type <see cref="MapAccess.MapLocal"/> only
/// after checking everything it reaches.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MapAccessAttribute(MapAccess access) : Attribute
{
    public MapAccess Access { get; } = access;

    /// <summary>Why the classification holds (for the audit page and reviewers).</summary>
    public string? Reason { get; init; }
}
