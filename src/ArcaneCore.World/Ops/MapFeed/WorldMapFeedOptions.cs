using ArcaneCore.World.Ops.Control;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Configuration of the world daemon's map feed (docs/areas/map-feed.md): a local, read-only named pipe that streams a map's entities,
/// splines, bot routes and loaded grids to a visualizer (<c>tools/ArcaneCore.Atlas</c>) and answers line-of-sight, height, path and
/// under-map questions with the server's own collision on the grids it has loaded. Off by default; the pipe is named
/// <c>arcanecore-mapfeed-{pid}</c> and admits only the account the daemon runs as (and SYSTEM), like the control pipe.
/// </summary>
public sealed class WorldMapFeedOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "World:Control:MapFeed";

    /// <summary>The pipe name (without <c>\\.\pipe\</c>); <c>{pid}</c> is the world process id. Fixed, not a configuration key.</summary>
    public const string PipeNameTemplate = "arcanecore-mapfeed-" + WorldControlPipeOptions.PidToken;

    /// <summary>The lowest <see cref="MaxHz"/>.</summary>
    public const int MinMaxHz = 1;

    /// <summary>The highest <see cref="MaxHz"/>.</summary>
    public const int MaxMaxHz = 60;

    /// <summary>The lowest <see cref="MaxEntitiesPerFrame"/>.</summary>
    public const int MinEntitiesPerFrame = 16;

    /// <summary>The highest <see cref="MaxEntitiesPerFrame"/>.</summary>
    public const int MaxEntitiesPerFrameLimit = 65536;

    /// <summary>The highest <see cref="MaxConnections"/> (one named-pipe instance each).</summary>
    public const int MaxMaxConnections = 16;

    /// <summary>
    /// Serve the map feed on a local Windows named pipe. A connected tool may watch one map at a time (players, bots, creatures, game
    /// objects, creature splines, bot routes, loaded grids) and ask for line of sight, ground height, a navigation path or an under-map
    /// verdict between two points of a map; nothing it sends changes the world, and a question never loads a tile. The pipe admits only
    /// the account the daemon runs as and SYSTEM, refuses network clients, and is not created on other operating systems. While nobody
    /// is subscribed the feed does no work on the world thread. Default off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The most frames per second any subscription gets; a tool asking for more is given this rate. 1 to 60.</summary>
    public int MaxHz { get; set; } = 5;

    /// <summary>
    /// The most entities one frame carries. Players and bots come first, then creatures (moving ones before idle ones) and game objects;
    /// the rest are counted as truncated in the frame. 16 to 65536.
    /// </summary>
    public int MaxEntitiesPerFrame { get; set; } = 4096;

    /// <summary>
    /// The most tools connected at once: one pipe instance each, all created at start. A tool beyond the limit finds the pipe busy and
    /// is not served. 1 to 16.
    /// </summary>
    public int MaxConnections { get; set; } = 2;

    /// <summary>
    /// Whether the anomaly ring keeps notices that carry no owner identity (<c>AnomalyNotice.Origin</c> null). The ledger is process-wide,
    /// and a notice with an owner is kept only when the owner is this world; a reporter that passes none cannot be told apart, so it
    /// is kept only when this is true. Default true, because the daemon runs one world per process; tests that run several worlds in one
    /// process set it false.
    /// </summary>
    public bool AnomaliesUnowned { get; set; } = true;

    /// <summary>Everything wrong with the values (empty when valid). Pure; any thread.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (MaxHz is < MinMaxHz or > MaxMaxHz)
        {
            problems.Add($"MaxHz is {MaxHz}; it must be {MinMaxHz} to {MaxMaxHz}");
        }

        if (MaxEntitiesPerFrame is < MinEntitiesPerFrame or > MaxEntitiesPerFrameLimit)
        {
            problems.Add($"MaxEntitiesPerFrame is {MaxEntitiesPerFrame}; it must be {MinEntitiesPerFrame} to {MaxEntitiesPerFrameLimit}");
        }

        if (MaxConnections is < 1 or > MaxMaxConnections)
        {
            problems.Add($"MaxConnections is {MaxConnections}; it must be 1 to {MaxMaxConnections}");
        }

        return problems;
    }
}
