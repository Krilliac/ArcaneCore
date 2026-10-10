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

    /// <summary>Per-updater simulation time, in attach order (only filled when diagnostics are on).</summary>
    internal List<(IMapUpdater Updater, long Micros)> Updaters { get; } = [];

    // Sub-phases of the cleanup: the grid state machine (unloads), the terrain clean-up, the deferred actions.
    internal long GridMicros { get; private set; }
    internal long TerrainMicros { get; private set; }
    internal long DeferredMicros { get; private set; }

    /// <summary>Grid lifecycle work during this map update (deltas of the map's <see cref="Grid.GridContainer"/> counters).</summary>
    internal Grid.GridLifecycleCounters Grids { get; set; }

    /// <summary>Forget the previous update's values so one instance can serve every update of a map.</summary>
    internal void Reset()
    {
        SimulationMicros = VisibilityMicros = ValuesMicros = FlushMicros = CleanupMicros = 0;
        PacketsMicros = HeartbeatMicros = GridMicros = TerrainMicros = DeferredMicros = 0;
        Players = MovedObjects = ChangedObjects = NewObjects = 0;
        VisibilityCandidates = 0;
        Completed = false;
        Updaters.Clear();
        Grids = default;
    }

    internal void Begin()
    {
        _phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _subStart = _phaseStart;
    }

    internal void EndPackets() => PacketsMicros = Sub();
    internal void EndHeartbeat() => HeartbeatMicros = Sub();
    internal void EndUpdater(IMapUpdater updater) => Updaters.Add((updater, Sub()));
    internal void EndSimulation() { SimulationMicros = ElapsedMicros(); Begin(); }
    internal void EndVisibility() { VisibilityMicros = ElapsedMicros(); Begin(); }
    internal void EndValues() { ValuesMicros = ElapsedMicros(); Begin(); }
    internal void EndFlush() { FlushMicros = ElapsedMicros(); Begin(); }
    internal void EndGrid() => GridMicros = Sub();
    internal void EndTerrain() => TerrainMicros = Sub();
    internal void EndDeferred() => DeferredMicros = Sub();
    internal void Complete() { CleanupMicros = ElapsedMicros(); Completed = true; }

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
