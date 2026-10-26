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
    /// Radius around players whose grids are loaded (an active non-player object loads only its own grid; a grid stays
    /// active while something active is within the visibility range of it)
    /// (vmangos <c>Map::m_gridActivationDistance</c>, initialised from
    /// <c>Visibility.Distance.Continents</c> = 100).
    /// </summary>
    public float GridActivationDistance { get; set; } = Map.VisibilityRange;

    /// <summary>
    /// How far (yards, 3D) a unit (player or creature) must move from where its last movement-triggered visibility pass was
    /// scheduled before the next one is (vmangos <c>Visibility.RelocationLowerLimit</c>, default 10, World.cpp:830;
    /// Unit::OnRelocated). Moves within it keep the visible sets of the last pass; the grey distance
    /// (<see cref="Map.VisibilityGreyDistance"/>) and the next pass cover the difference. Game objects get a pass on every move.
    /// 0 runs a pass on every move.
    /// </summary>
    public float VisibilityRelocationLowerLimit { get; set; } = 10f;

    /// <summary>
    /// On continents, update creatures only in the cells within <see cref="GridActivationDistance"/> of a player or active object (vmangos
    /// Map::UpdateCells → UpdateActiveCellsSynch, Maps/Map.cpp:815-857): a creature elsewhere in a loaded grid stands still, its
    /// AI and movement paused, until a player comes near; its respawn time is wall-clock and its corpse decay catches up on the
    /// first update (WorldObject::UpdateHelper::UpdateRealTime, Objects/Object.h:477-481). False updates every creature of the
    /// loaded grids each tick (the behaviour before 2026-10-11).
    /// </summary>
    public bool UpdateActiveCellsOnly { get; set; } = true;

    /// <summary>
    /// How far beyond <see cref="GridActivationDistance"/> a player looks for grids that do not exist yet, so
    /// their terrain, vmap and navmesh tiles are read and parsed on the thread pool before the grid is created
    /// (<see cref="GridContainer.GridApproaching"/>); other objects, active ones included, look half this distance around themselves. 0 turns the prefetch off. An ArcaneCore
    /// addition (vmangos reads the tiles inside the update): it changes when the files are read, not what is loaded or when. It also
    /// turns off the arrival prefetch (the tiles of a teleport's destination are read when the teleport starts), which has a budget of
    /// its own so it never evicts the approach prefetches.
    /// </summary>
    public float GridPrefetchDistance { get; set; } = 200f;

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
