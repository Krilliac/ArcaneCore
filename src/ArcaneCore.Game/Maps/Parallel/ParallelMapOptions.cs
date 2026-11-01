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

    /// <summary>
    /// Split each creature update into a spline pass (every active living creature's spline advanced and relocated, map-local work)
    /// and the rest (AI, leash, generators, respawns), instead of one pass that does both per creature (default false). With
    /// CreatureMapSystem in <see cref="Ungated"/> the spline pass runs outside the serial gate and the rest inside it, one gate entry
    /// per stage; a relocation that changes cell still takes the gate (grid loads). The order changes for every thread count alike:
    /// a creature's AI sees the other creatures of its map already moved this tick.
    /// </summary>
    public bool CreatureSplinePass { get; set; }

    /// <summary>
    /// Run the bots' thinks per map, on the map's update thread, as a stage after the map's other updaters and before Output
    /// (<see cref="MapBotThinkStage"/>; docs/research/map-local-stages-design-20261024.md, section 5.7), instead of in the playerbot
    /// feature's own pass (default false: today's order, exactly). With it on, the playerbot feature does its movement pump, intake and
    /// coordinators before the maps, each map thinks for the bots standing on it inside its update, and the feature closes the tick after
    /// the maps (the bots on no map, or on a map whose stage did not run, are thought for then, on the world thread). The tick's think time
    /// and actions are split between the maps by head count; each map's share is measured on its own thread, so with parallel maps the
    /// shares overlap and the phase takes about the longest share, not their sum. The stage always runs inside the serial gate:
    /// <see cref="Ungated"/> naming MapBotThinkStage is ignored until bot actions have the per-session queue of design 3.4. Needs a restart.
    /// </summary>
    public bool BotThinkPerMap { get; set; }

    /// <summary>
    /// Helper threads a map thread may lend to the data-parallel stages inside one map update (<see cref="MapLocalStage{TItem, TResult}"/>;
    /// docs/research/map-local-stages-design-20261024.md, section 4.3 and wave 6a), 0..64 (default 0: every stage runs on the map's own
    /// thread, in today's order). A stage computes per-item results (helpers may share this work, results land in per-item slots) and then
    /// applies them on the map thread in item order, so the outcome is the same at every helper count. Needs a restart.
    /// </summary>
    public int MapHelpers { get; set; }

    /// <summary>The thread count actually used (clamped to 1..64).</summary>
    internal int EffectiveMapUpdateThreads => Math.Clamp(MapUpdateThreads, 1, 64);

    /// <summary>The helper count actually used (clamped to 0..64).</summary>
    internal int EffectiveMapHelpers => Math.Clamp(MapHelpers, 0, 64);
}
