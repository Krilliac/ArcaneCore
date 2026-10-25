using System.Numerics;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.World.Tests.Playerbots.Fakes;

/// <summary>
/// A pathfinder for the bots' path-job tests: the owner's <see cref="FindPath"/> and the detached <see cref="FindPathShared(uint, Vector3, Vector3, PathOptions)"/>
/// both answer from a script (default: the straight two-point path, whole), and the owner's calls are counted, because a bot think that
/// uses a job must make none. Scripted missed tiles and a scripted verdict stand in for the tiles a real shared search would report.
/// </summary>
internal sealed class ManualSharedPathSearch : IPathfinder, ISharedTiledPathSearch
{
    private readonly object _gate = new();
    private readonly List<PathOptions> _ownerOptions = [];

    public bool Enabled => true;

    /// <summary>Owner-thread <see cref="FindPath"/> calls.</summary>
    public int OwnerCalls { get; private set; }

    /// <summary>The options of those calls.</summary>
    public IReadOnlyList<PathOptions> OwnerOptions => _ownerOptions;

    /// <summary>Detached searches run.</summary>
    public int SharedCalls { get; private set; }

    /// <summary><see cref="ResolveSharedMisses"/> calls.</summary>
    public int ResolveCalls { get; private set; }

    /// <summary>What <see cref="PrepareShared"/> answers (false: an endpoint tile is still being read).</summary>
    public bool PrepareResult { get; set; } = true;

    /// <summary>The path type the searches answer.</summary>
    public PathType Type { get; set; } = PathType.Normal;

    /// <summary>Tiles every detached search reports as missed.</summary>
    public (int X, int Y)[] Missed { get; set; } = [];

    /// <summary>What <see cref="ResolveSharedMisses"/> answers.</summary>
    public SharedMissVerdict Verdict { get; set; }

    /// <summary>A search over its deadline is counted as one (the owner's, for the bot-level deadline tests).</summary>
    public bool OwnerHitsDeadline { get; set; }

    /// <summary>A detached search is cut short by its time bound: counted as a deadline hit, and the answer is Incomplete.</summary>
    public bool WorkerHitsDeadline { get; set; }

    private PathResult Answer(Vector3 start, Vector3 end)
    {
        PathSearchCounters.RecordSearch(64, 64, capped: false);
        return (Type & PathType.NoPath) != 0 ? PathResult.None(start) : new PathResult(Type, [start, end]);
    }

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        lock (_gate)
        {
            OwnerCalls++;
            _ownerOptions.Add(options ?? PathOptions.Default);
        }

        if (OwnerHitsDeadline) PathSearchCounters.RecordDeadline();
        return Answer(start, end);
    }

    public bool CanSearchShared(uint mapId) => true;

    public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options)
    {
        lock (_gate) SharedCalls++;
        if (WorkerHitsDeadline)
        {
            PathSearchCounters.RecordDeadline();
            PathSearchCounters.RecordSearch(64, 64, capped: true);
            return new PathResult(PathType.Incomplete, [start, end]);
        }

        return Answer(start, end);
    }

    public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options, List<(int X, int Y)> missedTiles)
    {
        missedTiles.AddRange(Missed);
        return FindPathShared(mapId, start, end, options);
    }

    public bool PrepareShared(uint mapId, Vector3 start, Vector3 end)
    {
        if (!PrepareResult) PathSearchCounters.RecordTilePending();
        return PrepareResult;
    }

    public SharedMissVerdict ResolveSharedMisses(uint mapId, IReadOnlyList<(int X, int Y)> missedTiles)
    {
        ResolveCalls++;
        return Verdict;
    }
}
