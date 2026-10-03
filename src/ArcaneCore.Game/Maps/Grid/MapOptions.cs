namespace ArcaneCore.Game.Maps.Grid;

/// <summary>
/// Grid lifecycle and terrain settings, bound from the <c>World:Maps</c> configuration section
/// (<see cref="WorldRuntimeOptions.Maps"/>).
/// </summary>
public sealed class MapOptions
{
    /// <summary>vmangos <c>MIN_GRID_DELAY</c> = 1 minute (GridDefines.h): the lowest accepted <see cref="GridCleanUpDelayMs"/>.</summary>
    public const int MinGridDelayMs = 60_000;

    /// <summary>Whether idle grids are unloaded at all (vmangos <c>GridUnload</c>, default on).</summary>
    public bool GridUnload { get; set; } = true;

    /// <summary>
    /// How long an idle grid stays loaded (vmangos <c>GridCleanUpDelay</c>, default 5 minutes,
    /// at least <see cref="MinGridDelayMs"/> — World.cpp <c>setConfigMin</c>).
    /// </summary>
    public int GridCleanUpDelayMs { get; set; } = 5 * 60 * 1000;

    /// <summary>
    /// Radius around players (and active objects) whose grids are loaded and kept alive
    /// (vmangos <c>Map::m_gridActivationDistance</c>, initialised from
    /// <c>Visibility.Distance.Continents</c> = 100).
    /// </summary>
    public float GridActivationDistance { get; set; } = Map.VisibilityRange;

    /// <summary>
    /// Directory holding the extractor output (a <c>maps/</c> folder of <c>.map</c> files, as
    /// written by the vmangos/cmangos-classic map extractor — vmangos <c>DataDir</c>). Empty, or
    /// a directory without <c>maps/</c>, means no terrain: height, area and liquid lookups then
    /// report "no data" and nothing fails.
    /// </summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>The expiry actually used: the configured delay, raised to the minimum.</summary>
    public long EffectiveCleanUpDelayMs => Math.Max(GridCleanUpDelayMs, MinGridDelayMs);
}
