namespace ArcaneCore.Game.Maps;

/// <summary>What a map-ownership violation does (<see cref="ParallelMapOptions.OwnerGuard"/>).</summary>
public enum OwnerGuardMode
{
    /// <summary>No check.</summary>
    Off = 0,

    /// <summary>Report the violation to the anomaly ledger (with the caller's stack, once per call site) and carry on. The default.</summary>
    Report = 1,

    /// <summary>Report and throw <see cref="InvalidOperationException"/> (tests).</summary>
    Throw = 2,
}

/// <summary>
/// Parallel map updates (configuration section <c>World:Parallel</c>; docs/research/multithreading-plan-20261017.md, W2/W3/W4).
/// All of it is read once at world start.
/// </summary>
public sealed class ParallelMapOptions
{
    /// <summary>
    /// Threads that update maps during the maps phase, the world thread included (default 1: today's serial loop, exactly). With 2 or
    /// more, maps (continents, instances, battlegrounds) are updated concurrently, one thread per map at a time; map work that is not
    /// classified as map-local runs inside the world's serial gate, and the tick continues only when every map is done. 1..64.
    /// </summary>
    public int MapUpdateThreads { get; set; } = 1;

    /// <summary>
    /// With <see cref="MapUpdateThreads"/> above 1, run every stage of every map update inside the serial gate, even the ones classified
    /// map-local (default false). The workers and the barrier still run; nothing runs concurrently. A switch to tell a race in a
    /// map-local stage from a problem of the parallel machinery itself.
    /// </summary>
    public bool GateAllStages { get; set; }

    /// <summary>
    /// Map-update stages (Packets, Heartbeat, ActiveCells, Output, Grid, Deferred) or updater type names (CreatureMapSystem, …) to run
    /// outside the serial gate although the code does not classify them map-local, comma-separated (default empty). An experiment switch
    /// for finding what still races; a stage that is not map-local can corrupt shared state when ungated.
    /// </summary>
    public string Ungated { get; set; } = string.Empty;

    /// <summary>Map-update stages or updater type names to run inside the serial gate although the code classifies them map-local (default empty).</summary>
    public string Gated { get; set; } = string.Empty;

    /// <summary>
    /// What a change to a map's state from a thread that does not own it does while maps update in parallel (default Report).
    /// Off with <see cref="MapUpdateThreads"/> 1, where only the world thread runs.
    /// </summary>
    public OwnerGuardMode OwnerGuard { get; set; } = OwnerGuardMode.Report;

    /// <summary>The thread count actually used (clamped to 1..64).</summary>
    internal int EffectiveMapUpdateThreads => Math.Clamp(MapUpdateThreads, 1, 64);
}
