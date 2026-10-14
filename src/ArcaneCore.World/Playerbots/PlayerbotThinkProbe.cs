using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Where one bot's think spent its time (world thread): the phase it was in (<see cref="Phase"/>, the last one marked owns the time
/// until the next mark), every navigation-mesh query made through <see cref="Find"/> (count, time, A* nodes, the largest one and
/// where it ran) and the navigation tiles read for them (<see cref="PathSearchCounters"/>). The feature begins a probe before each
/// think and ends it after; the brain and the navigation mark phases and route their queries without a reference to it (the probe
/// of the running think is thread-static). Off a think, <see cref="Phase"/> does nothing and <see cref="Find"/> only queries.
/// <para>
/// Cost: two <see cref="Stopwatch.GetTimestamp"/> calls per mark and per query and no allocation (except a clamped fallback query's
/// options copy, <see cref="Find"/>); the description is built only for a think the caller judges slow. Before (live-w24-r1, 2026-10-11 13:01:27) a 2 139 ms ManagedPlayerbotFeature tick named no bot,
/// goal or phase.
/// </para>
/// </summary>
internal sealed class PlayerbotThinkProbe
{
    [ThreadStatic] private static PlayerbotThinkProbe? t_active;
    [ThreadStatic] private static bool t_lastCachedCapped;

    private const int MaxPhases = 32;
    private readonly string[] _names = new string[MaxPhases];
    private readonly long[] _ticks = new long[MaxPhases];
    private int _phases;
    private string _phase = "start";
    private long _phaseStarted;
    private long _started;
    private long _elapsed;
    private PathSearchTotals _searchBefore;

    /// <summary>The probe of the think running on this thread, if any.</summary>
    internal static PlayerbotThinkProbe? Active => t_active;

    /// <summary>
    /// The most A* nodes the fallback queries of one think may create together (<see cref="PlayerbotOptions.MaxThinkSearchNodes"/>;
    /// 0 = no bound). Only the queries made inside a <see cref="Fallback"/> scope count and are held to it: the steep escape, the starts
    /// beside the bot and the hazard detours of <see cref="PlayerbotNavigation"/>, which only try other ways after a plan's own query was
    /// refused or blocked. A fallback query past the budget is given only what is left (<see cref="Clamped"/>), and with less than
    /// <see cref="MinQueryNodes"/> left it is answered no path without a search (<see cref="Refused"/>): that fallback finds nothing.
    /// A plan's own query is never cut short or counted. A starved fallback can still make its plan fail, and the caller may store
    /// that as it stores any failed plan (a blocked spawn, an unreachable target, a back-off): the same outcome as when no such way
    /// exists, which it could always give. Set before <see cref="Begin"/>.
    /// </summary>
    internal long NodeBudget { get; set; }

    private int _fallbackDepth;
    private long _fallbackNodes;

    /// <summary>
    /// The queries made until the returned scope is disposed are fallbacks, held to <see cref="NodeBudget"/>. Nothing off a think.
    /// A struct: <c>using</c> it allocates nothing.
    /// </summary>
    internal static FallbackScope Fallback()
    {
        if (t_active is { } probe) probe._fallbackDepth++;
        return new FallbackScope(t_active);
    }

    /// <summary>The scope of <see cref="Fallback"/>.</summary>
    internal readonly struct FallbackScope(PlayerbotThinkProbe? probe) : IDisposable
    {
        public void Dispose()
        {
            if (probe is not null && probe._fallbackDepth > 0) probe._fallbackDepth--;
        }
    }

    /// <summary>The smallest search a query is still run with under <see cref="NodeBudget"/>.</summary>
    internal const int MinQueryNodes = 64;

    /// <summary>Queries of this think run with fewer nodes than they asked for (<see cref="NodeBudget"/>).</summary>
    internal int Clamped { get; private set; }

    /// <summary>Queries of this think answered no path without a search (<see cref="NodeBudget"/> spent).</summary>
    internal int Refused { get; private set; }

    /// <summary>Navigation queries made through <see cref="Find"/> in this think.</summary>
    internal int Queries { get; private set; }

    /// <summary>Their time (Stopwatch ticks).</summary>
    internal long QueryTicks { get; private set; }

    /// <summary>The A* search work of the think (every search on this thread, <see cref="Find"/> or not).</summary>
    internal PathSearchTotals Search { get; private set; }
    internal TileLoadDetail SlowestTile { get; private set; }

    /// <summary>The query that created the most nodes: its nodes, time, phase and ends.</summary>
    internal (long Nodes, long Ticks, string Phase, Vector3 From, Vector3 To, int MaxNodes) Largest { get; private set; }

    /// <summary>What drove the bot and its goal after the think (set by the feature after <see cref="End"/>; benchmarks).</summary>
    internal (string Driver, string Goal) Subject { get; set; }

    /// <summary>The think's time in Stopwatch ticks (after <see cref="End"/>).</summary>
    internal long ElapsedTicks => _elapsed;

    /// <summary>The think's time in milliseconds (after <see cref="End"/>).</summary>
    internal double ElapsedMs => _elapsed * 1000.0 / Stopwatch.Frequency;

    /// <summary>Start a think on this thread.</summary>
    internal void Begin()
    {
        Array.Clear(_names, 0, _phases);
        Array.Clear(_ticks, 0, _phases);
        _phases = 0;
        _phase = "start";
        Queries = 0;
        QueryTicks = 0;
        Clamped = 0;
        Refused = 0;
        _fallbackDepth = 0;
        _fallbackNodes = 0;
        Largest = default;
        Search = default;
        SlowestTile = default;
        _elapsed = 0;
        _searchBefore = PathSearchCounters.Current;
        PathSearchCounters.BeginTileCapture();
        _started = _phaseStarted = Stopwatch.GetTimestamp();
        t_active = this;
    }

    /// <summary>End the think: close its last phase and take its search work. Returns its time in Stopwatch ticks.</summary>
    internal long End()
    {
        long now = Stopwatch.GetTimestamp();
        Close(now);
        _elapsed = now - _started;
        Search = PathSearchCounters.Current - _searchBefore;
        SlowestTile = PathSearchCounters.SlowestTile;
        PathSearchCounters.EndTileCapture();
        if (ReferenceEquals(t_active, this)) t_active = null;
        return _elapsed;
    }

    /// <summary>The running think entered phase <paramref name="name"/> (a constant string); nothing off a think.</summary>
    internal static void Phase(string name)
    {
        if (t_active is { } probe) probe.Mark(name);
    }

    /// <summary>The current phase of the running think (null off one).</summary>
    internal static string? CurrentPhase => t_active?._phase;

    private void Mark(string name)
    {
        if (ReferenceEquals(name, _phase)) return;
        Close(Stopwatch.GetTimestamp());
        _phase = name;
    }

    private void Close(long now)
    {
        long spent = now - _phaseStarted;
        _phaseStarted = now;
        for (int i = 0; i < _phases; i++)
        {
            if (ReferenceEquals(_names[i], _phase))
            {
                _ticks[i] += spent;
                return;
            }
        }

        if (_phases < MaxPhases)
        {
            _names[_phases] = _phase;
            _ticks[_phases++] = spent;
        }
        else
        {
            _ticks[MaxPhases - 1] += spent; // more phases than slots: the last slot takes the rest
        }
    }

    /// <summary>
    /// One navigation query of a bot (every bot query goes through here: <see cref="PlayerbotNavigation"/>, the follow drop),
    /// counted on the running think; a fallback query (<see cref="Fallback"/>) is held to the think's <see cref="NodeBudget"/>.
    /// <para>
    /// Before, a bot whose plan fell through to its fallbacks could run 40 searches of up to 32 768 nodes in one think: 557 ms on the
    /// 200-bot terrain benchmark, and most likely the 2 139 ms live tick of live-w24-r1. A clamped query allocates its narrowed
    /// <see cref="PathOptions"/> copy; nothing else here allocates.
    /// </para>
    /// </summary>
    internal static PathResult Find(MapCollision collision, Vector3 from, Vector3 to, PathOptions options)
    {
        t_lastCachedCapped = false;
        if (t_active is not { } probe)
        {
            if (PlayerbotAsyncNavigationPlan.Current is { } detached)
            {
                if (detached.TryGet(from, to, options with { CancellationToken = default }, out PlayerbotNavigationResult answer))
                {
                    t_lastCachedCapped = answer.Capped;
                    return answer.Path ?? PathResult.None(from);
                }

                t_lastCachedCapped = true;
                return PathResult.None(from);
            }

            return collision.FindPath(from, to, options);
        }
        long nodesBefore = PathSearchCounters.NodesCreated;
        bool fallback = probe._fallbackDepth > 0;
        if (probe.NodeBudget > 0 && fallback)
        {
            long left = probe.NodeBudget - probe._fallbackNodes;
            if (left < MinQueryNodes)
            {
                probe.Refused++;
                return PathResult.None(from);
            }

            if (options.MaxSearchNodes > left)
            {
                probe.Clamped++;
                options = options with { MaxSearchNodes = (int)left };
            }
        }

        if (PlayerbotAsyncNavigationPlan.Current is { } plan)
        {
            if (plan.TryGet(from, to, options with { CancellationToken = default }, out PlayerbotNavigationResult answer))
            {
                probe.RecordCached(answer.Search.Nodes, answer.Capped, fallback, from, to, options.MaxSearchNodes);
                t_lastCachedCapped = answer.Capped;
                return answer.Path ?? PathResult.None(from);
            }

            // An uncached query is deliberately reported as capped. The navigation plan marks
            // the owner pass deferred, so this cannot become unreachable/risk evidence.
            t_lastCachedCapped = true;
            return PathResult.None(from);
        }

        long started = Stopwatch.GetTimestamp();
        PathResult path = collision.FindPath(from, to, options);
        long ticks = Stopwatch.GetTimestamp() - started;
        probe.Queries++;
        probe.QueryTicks += ticks;
        long nodes = PathSearchCounters.NodesCreated - nodesBefore;
        if (fallback) probe._fallbackNodes += nodes;
        if (nodes > probe.Largest.Nodes || (nodes == probe.Largest.Nodes && ticks > probe.Largest.Ticks))
            probe.Largest = (nodes, ticks, probe._phase, from, to, options.MaxSearchNodes);
        return path;
    }

    internal static bool ConsumeLastQueryCapped()
    {
        bool capped = t_lastCachedCapped;
        t_lastCachedCapped = false;
        return capped;
    }

    private void RecordCached(long nodes, bool capped, bool fallback, Vector3 from, Vector3 to, int maxNodes)
    {
        Queries++;
        if (fallback) _fallbackNodes += nodes;
        if (capped) Search = Search with { Capped = Search.Capped + 1 };
        if (nodes > Largest.Nodes)
            Largest = (nodes, 0, _phase, from, to, maxNodes);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The phases, slowest first, with their milliseconds (tests and the benchmark).</summary>
    internal IEnumerable<(string Phase, double Ms)> Phases()
    {
        var phases = new List<(string, double)>(_phases);
        for (int i = 0; i < _phases; i++) phases.Add((_names[i], Ms(_ticks[i])));
        return phases.OrderByDescending(p => p.Item2);
    }

    /// <summary>The slowest phase of the think.</summary>
    internal string SlowestPhase
    {
        get
        {
            int best = -1;
            for (int i = 0; i < _phases; i++)
                if (best < 0 || _ticks[i] > _ticks[best]) best = i;
            return best < 0 ? "start" : _names[best];
        }
    }

    /// <summary>
    /// The think in one line (only for a slow one): its phases over a millisecond, slowest first, then its navigation work.
    /// </summary>
    internal string Describe()
    {
        var text = new StringBuilder(256);
        text.Append("phases ");
        bool any = false;
        foreach ((string phase, double ms) in Phases())
        {
            if (ms < 1 && any) break;
            if (any) text.Append(", ");
            text.Append(CultureInfo.InvariantCulture, $"{phase} {ms:F1}");
            any = true;
        }

        text.Append(CultureInfo.InvariantCulture,
            $"; path {Queries} queries {Ms(QueryTicks):F1} ms, {Search.Searches} searches, {Search.Nodes} nodes, {Search.Expansions} expanded, {Search.Capped} at the node cap");
        if (Largest.Nodes > 0)
        {
            (long nodes, long ticks, string phase, Vector3 from, Vector3 to, int max) = Largest;
            text.Append(CultureInfo.InvariantCulture,
                $"; largest {nodes}/{max} nodes {Ms(ticks):F1} ms in {phase} from ({from.X:F1}, {from.Y:F1}, {from.Z:F1}) to ({to.X:F1}, {to.Y:F1}, {to.Z:F1}), {Vector3.Distance(from, to):F0} yd");
        }

        if (Clamped > 0 || Refused > 0)
            text.Append(CultureInfo.InvariantCulture, $"; node budget {NodeBudget}: {Clamped} fallback queries cut short, {Refused} refused");
        if (Search.TilesPending > 0)
            text.Append(CultureInfo.InvariantCulture, $"; tiles pending {Search.TilesPending} (read in the background, the plan waits)");
        if (Search.TileLoads > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"; tiles read {Search.TileLoads} ({Search.TileLoadMs:F1} ms)");
            if (SlowestTile.Ticks > 0)
                text.Append(CultureInfo.InvariantCulture,
                    $"; slowest tile map {SlowestTile.MapId} ({SlowestTile.X}, {SlowestTile.Y}) {Ms(SlowestTile.Ticks):F1} ms, { (SlowestTile.PrefetchHit ? "prefetch hit" : "prefetch miss") }");
        }
        return text.ToString();
    }
}
