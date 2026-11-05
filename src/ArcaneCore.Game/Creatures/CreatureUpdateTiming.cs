using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Where one <see cref="CreatureMapSystem"/> update spent its time (ticks of <see cref="Stopwatch"/>); filled on every update by the
/// system, read only to log a slow one. A plain value the system owns (one field, reset per update); world thread only.
/// </summary>
internal struct UpdateTiming
{
    public long CatchUp, Linked, Creatures, SpawnGroups, PendingAi, Relay, Despawns, AfterUpdate;

    /// <summary>Inside <see cref="Creatures"/>: AI updates, motion generator updates, and (inside either) path searches.</summary>
    public long Ai, Motion, Path;

    /// <summary>The spline pass of World:Parallel:CreatureSplinePass (inside <see cref="Creatures"/>; 0 when the pass is off).</summary>
    public long SplinePass;
    public int UngatedWanderers;

    public int Walked, Updated, Searches;
    public long SlowestCreatureTicks;
    public uint SlowestEntry;
    public Type? SlowestGenerator;

    /// <summary>The update's slowest A* search: its time, who asked, the endpoints and the work it did.</summary>
    public long SlowestSearchTicks;
    public SlowSearch SlowestSearch;

    /// <summary>Path-job answers applied this update: count, worker time, slowest (not on this thread).</summary>
    public int JobSearches;
    public long JobSearchTicks, SlowestJobSearchTicks;
}

/// <summary>
/// One search's detail for the slow-map-update line. Few nodes and expansions with a long time mean the search didn't get the
/// thread (a GC, paging, the host). Many nodes mean the search did the work. LS-008: single creature searches of 120-560 ms with
/// no GC on the live 2026-10-17 run, under node caps of 512-2048.
/// </summary>
internal readonly record struct SlowSearch(
    uint Entry, MovementGeneratorType Motion, Vector3 From, Vector3 To, long Nodes, long Expansions, PathType Result, bool GcDuring);

/// <summary>
/// Formats an <see cref="UpdateTiming"/> for the slow-map-update line (<see cref="ArcaneCore.Game.Maps.IMapUpdateBreakdown"/>). Stateless; allocates, so
/// call only when the line is logged. The wording is pinned by tests: ops tooling and several tests match substrings of it.
/// </summary>
internal static class CreatureUpdateReport
{
    /// <summary>Stopwatch ticks as milliseconds with one decimal, invariant culture.</summary>
    internal static string TickMs(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>The stages of <paramref name="t"/> plus the system's running path-outcome counters and the path jobs still in flight.</summary>
    internal static string Describe(in UpdateTiming t, long noPathPaths, long directFallbackPaths, long deferredPathSearches, long homePathTeleports, int pathJobsInFlight)
    {
        string text = $"creatures {TickMs(t.Creatures)} ms over {t.Updated}/{t.Walked} active (ai {TickMs(t.Ai)}, motion {TickMs(t.Motion)}{(t.SplinePass > 0 ? $", spline pass {TickMs(t.SplinePass)}" : "")}{(t.UngatedWanderers > 0 ? $", {t.UngatedWanderers} wanderers outside the gate" : "")}), path searches {TickMs(t.Path)} ms in {t.Searches} searches from any stage, "
            + $"catch-up moves {TickMs(t.CatchUp)}, linked followers {TickMs(t.Linked)}, spawn groups {TickMs(t.SpawnGroups)}, pending ai {TickMs(t.PendingAi)}, "
            + $"relay scripts {TickMs(t.Relay)}, forced despawns {TickMs(t.Despawns)}, observers {TickMs(t.AfterUpdate)}, "
            + $"path outcomes so far: {noPathPaths} no-path, {directFallbackPaths} straight authored routes, {deferredPathSearches} deferred, {homePathTeleports} home teleports";
        if (t.SlowestCreatureTicks > 0)
        {
            text += $"; slowest creature entry {t.SlowestEntry} {TickMs(t.SlowestCreatureTicks)} ms, top generator {t.SlowestGenerator?.Name ?? "none"}";
        }

        if (t.JobSearches > 0 || pathJobsInFlight > 0)
        {
            text += $"; path jobs applied {t.JobSearches} (worker time {TickMs(t.JobSearchTicks)} ms, slowest {TickMs(t.SlowestJobSearchTicks)} ms), in flight {pathJobsInFlight}";
        }

        if (t.SlowestSearchTicks > 0)
        {
            SlowSearch s = t.SlowestSearch;
            text += string.Create(CultureInfo.InvariantCulture,
                $"; slowest path search {TickMs(t.SlowestSearchTicks)} ms: entry {s.Entry} ({s.Motion}) from {s.From.X:F1},{s.From.Y:F1},{s.From.Z:F1} to {s.To.X:F1},{s.To.Y:F1},{s.To.Z:F1}, {s.Nodes} nodes, {s.Expansions} expansions, {s.Result}{(s.GcDuring ? ", a GC ran during it" : "")}");
        }

        return text;
    }
}
