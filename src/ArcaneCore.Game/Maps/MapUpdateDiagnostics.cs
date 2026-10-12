using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Maps;

/// <summary>Optional per-map slow-update evidence; allocated only on the enabled warning path (or by a benchmark observer).</summary>
internal sealed class MapUpdateDiagnostics
{
    private long _phaseStart;
    private long _subStart;

    internal long SimulationMicros { get; private set; }
    internal long VisibilityMicros { get; private set; }
    internal long ValuesMicros { get; private set; }
    internal long FlushMicros { get; private set; }
    internal long CleanupMicros { get; private set; }
    internal int Players { get; set; }
    internal int MovedObjects { get; set; }
    internal int ChangedObjects { get; set; }
    internal int NewObjects { get; set; }

    /// <summary>Candidates the visibility phase evaluated (players' and moved objects' passes together).</summary>
    internal long VisibilityCandidates { get; set; }
    internal bool Completed { get; private set; }

    // Sub-phases of the simulation: in-world packets (and transit/logout timers), unit heartbeats, each map updater.
    internal long PacketsMicros { get; private set; }
    internal long HeartbeatMicros { get; private set; }

    /// <summary>Marking the cells around players and active objects (<see cref="Map.UpdatesActiveCellsOnly"/>), between the heartbeat and the first updater.</summary>
    internal long ActiveCellsMicros { get; private set; }

    /// <summary>Per-updater simulation time, in attach order (only filled when diagnostics are on).</summary>
    internal List<(IMapUpdater Updater, long Micros)> Updaters { get; } = [];

    // Sub-phases of the cleanup: the grid state machine (unloads), the terrain clean-up, the deferred actions.
    internal long GridMicros { get; private set; }
    internal long TerrainMicros { get; private set; }
    internal long DeferredMicros { get; private set; }

    /// <summary>Grid lifecycle work during this map update (deltas of the map's <see cref="Grid.GridContainer"/> counters).</summary>
    internal Grid.GridLifecycleCounters Grids { get; set; }

    private PathSearchTotals _tilesBefore;

    /// <summary>
    /// Navigation tile installations the world thread made during this map update: on-demand path-query reads and grid tile loads
    /// (deltas of <see cref="PathSearchCounters"/>; set by <see cref="Complete"/>).
    /// </summary>
    internal PathSearchTotals Tiles { get; private set; }

    /// <summary>The slowest of those installations (default when none).</summary>
    internal TileLoadDetail SlowestTile { get; private set; }

    /// <summary>Forget the previous update's values so one instance can serve every update of a map.</summary>
    internal void Reset()
    {
        SimulationMicros = VisibilityMicros = ValuesMicros = FlushMicros = CleanupMicros = 0;
        PacketsMicros = HeartbeatMicros = ActiveCellsMicros = GridMicros = TerrainMicros = DeferredMicros = 0;
        Players = MovedObjects = ChangedObjects = NewObjects = 0;
        VisibilityCandidates = 0;
        Completed = false;
        Updaters.Clear();
        Grids = default;
        Tiles = default;
        SlowestTile = default;
    }

    /// <summary>Remember the world thread's tile counters at the start of the update (a few loads of thread-static fields).</summary>
    internal void BeginTiles()
    {
        _tilesBefore = PathSearchCounters.Current;
        PathSearchCounters.ResetSlowestSyncTile();
    }

    internal void Begin()
    {
        _phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _subStart = _phaseStart;
    }

    internal void EndPackets() => PacketsMicros = Sub();
    internal void EndHeartbeat() => HeartbeatMicros = Sub();
    internal void EndActiveCells() => ActiveCellsMicros = Sub();
    internal void EndUpdater(IMapUpdater updater) => Updaters.Add((updater, Sub()));
    internal void EndSimulation() { SimulationMicros = ElapsedMicros(); Begin(); }
    internal void EndVisibility() { VisibilityMicros = ElapsedMicros(); Begin(); }
    internal void EndValues() { ValuesMicros = ElapsedMicros(); Begin(); }
    internal void EndFlush() { FlushMicros = ElapsedMicros(); Begin(); }
    internal void EndGrid() => GridMicros = Sub();
    internal void EndTerrain() => TerrainMicros = Sub();
    internal void EndDeferred() => DeferredMicros = Sub();
    internal void Complete()
    {
        CleanupMicros = ElapsedMicros();
        Tiles = PathSearchCounters.Current - _tilesBefore;
        SlowestTile = PathSearchCounters.SlowestSyncTile;
        Completed = true;
    }

    /// <summary>A simulation sub-phase at least this long (µs) is named as dominant in <see cref="Describe"/>.</summary>
    internal const long DominantMicros = 500_000;

    /// <summary>How many of the slowest updaters <see cref="Describe"/> lists.</summary>
    internal const int TopUpdaters = 3;

    /// <summary>
    /// The sub-phase breakdown of this update for the slow-map-update line: simulation split into packets, heartbeats, active cells and
    /// the slowest updaters (the slowest with its own breakdown when it gives one, <see cref="IMapUpdateBreakdown"/>), the cleanup split,
    /// grid lifecycle work and the world thread's navigation tile installations. Allocates; call only when the line is logged.
    /// </summary>
    internal string Describe()
    {
        var sb = new StringBuilder(256);
        sb.Append("simulation: packets ").Append(Ms(PacketsMicros))
            .Append(", heartbeat ").Append(Ms(HeartbeatMicros))
            .Append(", active cells ").Append(Ms(ActiveCellsMicros));

        (IMapUpdater Updater, long Micros)[] ranked = [.. Updaters.OrderByDescending(u => u.Micros).Take(TopUpdaters)];
        sb.Append(", updaters ");
        if (ranked.Length == 0)
        {
            sb.Append("none");
        }

        for (int i = 0; i < ranked.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ranked[i].Updater.GetType().Name).Append(' ').Append(Ms(ranked[i].Micros));
            if (i == 0 && ranked[i].Updater is IMapUpdateBreakdown breakdown && breakdown.DescribeLastUpdate() is { Length: > 0 } inner)
            {
                sb.Append(" (").Append(inner).Append(')');
            }
        }

        if (SimulationMicros >= DominantMicros)
        {
            string name = "packets";
            long top = PacketsMicros;
            if (HeartbeatMicros > top) { name = "heartbeat"; top = HeartbeatMicros; }
            if (ActiveCellsMicros > top) { name = "active cells"; top = ActiveCellsMicros; }
            if (ranked.Length > 0 && ranked[0].Micros > top) { name = ranked[0].Updater.GetType().Name; top = ranked[0].Micros; }
            sb.Append("; DOMINANT ").Append(name).Append(' ').Append(Ms(top));
        }

        sb.Append("; cleanup: grids ").Append(Ms(GridMicros))
            .Append(", terrain ").Append(Ms(TerrainMicros))
            .Append(", deferred ").Append(Ms(DeferredMicros))
            .Append("; grid lifecycle: created ").Append(Grids.Created).Append(", loaded ").Append(Grids.Loaded)
            .Append(", unloaded ").Append(Grids.Unloaded)
            .Append(" (").Append(Ms(Grids.CreateMicros + Grids.LoadMicros + Grids.UnloadMicros)).Append(')');

        sb.Append("; navmesh tile reads on world thread: ").Append(Tiles.SyncTileReads).Append(" (")
            .Append(Tiles.SyncTileReadMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms)");
        if (Tiles.TilesPending > 0)
        {
            sb.Append(", pending ").Append(Tiles.TilesPending);
        }

        if (SlowestTile.Ticks > 0)
        {
            sb.Append(", slowest map ").Append(SlowestTile.MapId).Append(" tile (").Append(SlowestTile.X).Append(", ").Append(SlowestTile.Y)
                .Append(") ").Append((SlowestTile.Ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture))
                .Append(" ms, ").Append(SlowestTile.PrefetchHit ? "prefetch hit" : "prefetch miss");
        }

        return sb.ToString();
    }

    private static string Ms(long micros) => (micros / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " ms";

    private long Sub()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long micros = (now - _subStart) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        _subStart = now;
        return micros;
    }

    private long ElapsedMicros()
        => (System.Diagnostics.Stopwatch.GetTimestamp() - _phaseStart) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
}
