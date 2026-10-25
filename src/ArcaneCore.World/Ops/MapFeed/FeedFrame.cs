using System.Numerics;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>What a subscription wants to see (the <c>kinds</c> of the subscribe line).</summary>
[Flags]
internal enum FeedKinds : byte
{
    None = 0,
    Player = 1,
    Bot = 2,
    Creature = 4,
    GameObject = 8,
    All = Player | Bot | Creature | GameObject,
}

/// <summary>The <c>k</c> of an entity.</summary>
internal enum FeedEntityKind : byte
{
    Player,
    Bot,
    Creature,
    GameObject,
}

/// <summary>Which optional parts of a <see cref="FeedEntity"/> were captured.</summary>
[Flags]
internal enum FeedEntityParts : byte
{
    None = 0,
    Health = 1,
    Target = 2,
    Home = 4,
    Spline = 8,
    Route = 16,
    SplineCut = 32,
    RouteCut = 64,
}

/// <summary>
/// One captured entity: plain values and indexes into the frame's point store, nothing that refers back to the world, so serialization can
/// run on any thread after the world thread filled it. Names are immutable strings.
/// </summary>
internal struct FeedEntity
{
    public ulong Guid;
    public ulong Target;
    public uint Entry;
    public string? Name;
    public float X;
    public float Y;
    public float Z;
    public float Orientation;
    public float HealthPercent;
    public bool InCombat;
    public uint DisplayId;
    public byte Race;
    public byte Class;
    public FeedEntityKind Kind;
    public byte Motion;
    public FeedEntityParts Parts;
    public float HomeX;
    public float HomeY;
    public float HomeZ;

    public uint SplineId;
    public float SplineStartX;
    public float SplineStartY;
    public float SplineStartZ;
    public long SplineT0;
    public uint SplineDuration;
    public bool SplineRun;
    public int SplineOffset;
    public int SplineCount;

    public int RouteOffset;
    public int RouteCount;
    public int RouteNext;
    public BotRouteFlags RouteFlags;
}

/// <summary>The totals of a frame (<c>counters</c>).</summary>
internal struct FeedCounters
{
    public int Players;
    public int Bots;
    public int Creatures;
    public int GameObjects;
    public int Truncated;
    public int NonFinite;
    public int PathsOmitted;
    public long DeferredPaths;
    public long DirectFallbackPaths;
    public long NoPathPaths;
    public long HomePathTeleports;
    public int Online;
    public uint DiffMs;
}

/// <summary>
/// One captured frame of a map, pooled and shared by every connection that subscribed to the same view: the world thread fills it (the
/// arrays only grow, so a steady-state capture allocates nothing), connection threads serialize it, and the last holder returns it to
/// <see cref="FramePool"/>. Fill on the world thread only; after it is shared it is read-only.
/// </summary>
internal sealed class FeedFrame
{
    private readonly FramePool _pool;
    private FeedEntity[] _entities = [];
    private Vector3[] _points = [];
    private (int X, int Y)[] _grids = [];
    private int _holders;

    public FeedFrame(FramePool pool) => _pool = pool;

    /// <summary>The captured entities; only the first <see cref="EntityCount"/> are valid.</summary>
    public FeedEntity[] Entities => _entities;

    public int EntityCount { get; private set; }

    /// <summary>The point store the entities' splines and routes index into; only the first <see cref="PointCount"/> are valid.</summary>
    public Vector3[] Points => _points;

    public int PointCount { get; private set; }

    /// <summary>Loaded grids as (gx, gy) server grid pairs; only the first <see cref="GridCount"/> are valid.</summary>
    public (int X, int Y)[] Grids => _grids;

    public int GridCount { get; private set; }

    public uint MapId { get; set; }

    public uint InstanceId { get; set; }

    /// <summary>The map's creature clock (<c>CreatureMapSystem.ClockMs</c>), the clock a spline's start is on.</summary>
    public long ClockMs { get; set; }

    public FeedCounters Counters;

    /// <summary>The view this frame was captured for; connections with an equal view share it.</summary>
    public FeedView View { get; set; }

    /// <summary>Make room for one more entity and return its index.</summary>
    public int AddEntity()
    {
        if (EntityCount == _entities.Length)
        {
            Array.Resize(ref _entities, Math.Max(256, _entities.Length * 2));
        }

        _entities[EntityCount] = default;
        return EntityCount++;
    }

    /// <summary>
    /// Room for <paramref name="count"/> more points: the span to write them into. Nothing is added until <see cref="CommitPoints"/>
    /// says how many were used, so a reservation that is not needed costs only capacity.
    /// </summary>
    public Span<Vector3> ReservePoints(int count)
    {
        int needed = PointCount + count;
        if (needed > _points.Length)
        {
            Array.Resize(ref _points, Math.Max(needed, Math.Max(1024, _points.Length * 2)));
        }

        return _points.AsSpan(PointCount, count);
    }

    /// <summary>Keep the first <paramref name="used"/> points of the last <see cref="ReservePoints"/> span.</summary>
    public void CommitPoints(int used) => PointCount += used;

    public void AddGrid(int x, int y)
    {
        if (GridCount == _grids.Length)
        {
            Array.Resize(ref _grids, Math.Max(16, _grids.Length * 2));
        }

        _grids[GridCount++] = (x, y);
    }

    /// <summary>Forget the content (the arrays stay for the next capture).</summary>
    public void Clear()
    {
        Array.Clear(_entities, 0, EntityCount); // the names must not outlive the capture
        EntityCount = 0;
        PointCount = 0;
        GridCount = 0;
        Counters = default;
        View = default;
        MapId = 0;
        InstanceId = 0;
        ClockMs = 0;
    }

    /// <summary>Add a holder (the capture itself, and each queue the frame is put on).</summary>
    public void AddHolder() => Interlocked.Increment(ref _holders);

    /// <summary>Drop a holder; the last one returns the frame to the pool.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
        {
            _pool.Return(this);
        }
    }
}

/// <summary>The view a subscription has of a map: frames captured for equal views are the same frame.</summary>
internal readonly record struct FeedView(uint MapId, uint InstanceId, FeedKinds Kinds, bool Paths);

/// <summary>
/// The frames in use, bounded: a capture that finds none free is skipped (and counted as dropped) instead of allocating, so a stalled
/// connection can pin only a few frames. Backed by a fixed array so returning a frame allocates nothing. Any thread.
/// </summary>
internal sealed class FramePool(int limit)
{
    private readonly Lock _gate = new();
    private readonly FeedFrame?[] _free = new FeedFrame?[limit];
    private int _freeCount;
    private int _created;

    /// <summary>A cleared frame holding one reference (the caller's), or null when <c>limit</c> frames are in use.</summary>
    public FeedFrame? Rent()
    {
        FeedFrame? frame = null;
        lock (_gate)
        {
            if (_freeCount > 0)
            {
                frame = _free[--_freeCount];
                _free[_freeCount] = null;
            }
            else if (_created < limit)
            {
                _created++;
                frame = new FeedFrame(this);
            }
        }

        if (frame is null)
        {
            return null;
        }

        frame.AddHolder();
        return frame;
    }

    public void Return(FeedFrame frame)
    {
        frame.Clear();
        lock (_gate)
        {
            if (_freeCount < _free.Length)
            {
                _free[_freeCount++] = frame;
            }
        }
    }

    /// <summary>Frames created so far (tests).</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>Frames waiting for reuse (tests).</summary>
    public int Free => Volatile.Read(ref _freeCount);
}
