using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>One bounded, collision-backed route owned by a server-managed player.</summary>
internal sealed class PlayerbotRoute
{
    /// <summary>A completed quest's ender route may cross only individually manageable pack hazards.</summary>
    internal bool CompletedQuestReturn { get; set; }

    /// <summary>
    /// Set with <see cref="CompletedQuestReturn"/> when the ender is a friendly town NPC. A death or retreat mark beside the
    /// road then does not take this walk away (<see cref="PlayerbotRisk.Blocking"/>).
    /// </summary>
    internal bool CrossBesidePlaceMarks { get; set; }

    internal PlayerbotRoute(IReadOnlyList<Vector3> points, float distance, bool navigated = false)
    {
        Points = points;
        Distance = distance;
        Navigated = navigated;
    }

    /// <summary>
    /// A route joined from legs of different origins (a hazard detour: a mesh leg, then legs that may have been stepped over the
    /// terrain): <paramref name="legNavigated"/>[i] says whether the leg from point i to point i + 1 came from the navigation mesh.
    /// </summary>
    internal PlayerbotRoute(IReadOnlyList<Vector3> points, float distance, IReadOnlyList<bool> legNavigated)
    {
        if (legNavigated.Count != Math.Max(0, points.Count - 1))
            throw new ArgumentException("one flag per leg", nameof(legNavigated));
        Points = points;
        Distance = distance;
        _legs = legNavigated.ToArray();
        Navigated = legNavigated.Count > 0 && legNavigated.All(leg => leg);
    }

    private bool[]? _legs;

    internal IReadOnlyList<Vector3> Points { get; }
    internal IReadOnlyList<Vector3>? MeshPoints { get; set; }
    internal float Distance { get; }

    /// <summary>
    /// The corners came from the map's navigation mesh: every leg between them is walkable by construction (vmangos
    /// <c>PathFinder</c> moves units along such corners without further checks). The motion still snaps each position to the
    /// floor, but it does not test a straight line of sight between two positions, which across a corner cuts through the very
    /// obstacle the mesh goes around (a pillar, a counter, a doorway's edge). Routes stepped over the terrain without a mesh
    /// (<see cref="PlayerbotNavigation.TryTerrainRoute"/>) carry no such proof and keep the line-of-sight check.
    /// </summary>
    internal bool Navigated { get; }

    internal void SetLegNavigated(int legIndex, bool navigated)
    {
        int count = Math.Max(0, Points.Count - 1);
        if (legIndex < 0 || legIndex >= count) return;
        if (_legs is null || _legs.Length != count)
        {
            var legs = new bool[count];
            Array.Fill(legs, Navigated);
            _legs = legs;
        }

        _legs[legIndex] = navigated;
    }

    /// <summary>
    /// Whether every leg walked from the one ending at point <paramref name="fromPoint"/> through the one ending at point
    /// <paramref name="toPoint"/> came from the navigation mesh (<see cref="Navigated"/> for a route of one origin). A position on a
    /// stepped leg keeps the floor and line-of-sight checks even when an earlier leg of the same route was the mesh's.
    /// </summary>
    internal bool LegsNavigated(int fromPoint, int toPoint)
    {
        if (_legs is null || _legs.Length == 0) return Navigated;
        int first = Math.Clamp(fromPoint, 1, _legs.Length), last = Math.Clamp(toPoint, 1, _legs.Length);
        for (int point = first; point <= last; point++)
            if (!_legs[point - 1]) return false;
        return true;
    }

    /// <summary>A route to a moving target (a chase): re-planned as the target moves, so the loop watch does not count its ends.</summary>
    internal bool Chase { get; set; }

    /// <summary>
    /// The mesh's own answer to a query ended short of its destination (<see cref="PathType.Incomplete"/>, not at the destination): the route
    /// is the part of the way the mesh knows, not a leg cut on purpose.
    /// </summary>
    internal bool MeshEndedShort { get; set; }

    /// <summary>
    /// <see cref="MeshEndedShort"/> for a query toward the goal of <see cref="PlayerbotNavigation.PlanToward"/> itself (the goal, or a point of the straight line
    /// to it): the mesh found no way and this is as near as it gets. A region leg, a chunk the mesh answered whole and a hop end short of the
    /// goal by design and never carry this. The one exception is the leg to the last portal of a region search that found no way
    /// to the goal (<see cref="PlayerbotNavigation.RegionLeg"/>): the graph's nearest ground to the goal, a dead end.
    /// </summary>
    internal bool PartialToGoal { get; set; }

    /// <summary>The map pathfinder active when this route was planned, used to detect live pathfinder replacement.</summary>
    internal IPathfinder? Pathfinder { get; set; }

    internal bool IsStaleFor(Map? map)
        => Pathfinder is not null && map is not null && !ReferenceEquals(Pathfinder, map.Collision.Pathfinder);

    /// <summary>An owner let go of this route (its slot moved to null or another route) without stopping the bot;
    /// a driver's settle (PlayerbotMotion.SettleAfterDriver) stops a walk that still follows it. Cleared when Follow adopts or renews it.</summary>
    internal bool Released { get; set; }

    internal int NextPoint { get; set; } = 1;
    internal bool Complete => NextPoint >= Points.Count;

    internal float RemainingDistanceFrom(Vector3 position)
    {
        if (Complete || Points.Count == 0) return 0f;
        int next = Math.Clamp(NextPoint, 0, Points.Count - 1);
        float remaining = Vector3.Distance(position, Points[next]);
        for (int i = next; i + 1 < Points.Count; i++)
            remaining += Vector3.Distance(Points[i], Points[i + 1]);
        return remaining;
    }

    /// <summary>Whether the leg ending at point <paramref name="point"/> (1-based) came from the navigation mesh.</summary>
    internal bool LegNavigatedAt(int point) => _legs is null || _legs.Length == 0 ? Navigated : _legs[Math.Clamp(point, 1, _legs.Length) - 1];

    /// <summary>
    /// This route's unwalked rest, from <paramref name="here"/>, joined to <paramref name="next"/> (a route planned from this one's end):
    /// one route the motion switches to without stopping. Each leg keeps its own navigation proof (<see cref="LegsNavigated"/>).
    /// </summary>
    internal PlayerbotRoute Then(Vector3 here, PlayerbotRoute next)
    {
        int first = Math.Clamp(NextPoint, 1, Math.Max(1, Points.Count - 1));
        var points = new List<Vector3>(Points.Count - first + next.Points.Count + 1) { here };
        var legs = new List<bool>(points.Capacity);
        float distance = 0;
        for (int i = first; i < Points.Count; i++)
        {
            distance += Vector3.Distance(points[^1], Points[i]);
            points.Add(Points[i]);
            legs.Add(LegNavigatedAt(i));
        }

        for (int i = 1; i < next.Points.Count; i++)
        {
            distance += Vector3.Distance(points[^1], next.Points[i]);
            points.Add(next.Points[i]);
            legs.Add(next.LegNavigatedAt(i));
        }

        return new PlayerbotRoute(points, distance, legs)
        {
            CompletedQuestReturn = CompletedQuestReturn,
            Pathfinder = Pathfinder ?? next.Pathfinder,
            MeshPoints = MeshPoints is not null || next.MeshPoints is not null ? points : null,
        };
    }
}

/// <summary>The answer of a planning call that may wait for navigation tiles read in the background (<see cref="PlayerbotNavigation.PlanToward"/>).</summary>
internal enum PlanOutcome : byte
{
    /// <summary>A route was planned.</summary>
    Route,
    /// <summary>No verdict yet: a tile on the way is being read. Keep the goal, set nothing aside, plan again on a later think.</summary>
    Pending,
    /// <summary>The destination was judged unreachable (or the wait for tiles passed <see cref="TileWait.MaxMs"/>): handle as a refusal.</summary>
    Refused,
}

/// <summary>
/// One goal's wait for navigation tiles (<see cref="PlanOutcome.Pending"/>): held by the caller, passed by reference to the planning call.
/// A wait is a run of holds, each within <see cref="GapMs"/> of the last. Past <see cref="MaxMs"/> it is spent: the refusal stands, for
/// every further pending answer of the run, until a plan succeeds (<see cref="Reset"/>) or the run breaks off. The tiles load on
/// background threads in real time, so a wait that saw such a read in flight is spent only when <see cref="MaxMs"/> have passed on the
/// game clock AND on the wall clock: a world that runs faster than real time (the manual test clock) does not give up before the load
/// had its time. A wait that only met pathfinders answering "pending" (tests) is bounded by the game clock alone. In production the two
/// clocks agree and nothing changes. Owned and touched by the world thread only.
/// </summary>
internal struct TileWait
{
    /// <summary>How long a bot waits for the tiles of one plan before the refusal counts as any other.</summary>
    internal const uint MaxMs = 15_000;

    /// <summary>Holds further apart than this are separate waits.</summary>
    internal const uint GapMs = 5_000;

    /// <summary>Test seam: the wall clock as a <see cref="System.Diagnostics.Stopwatch"/> timestamp (null: the real one).</summary>
    internal Func<long>? RealTicks { get; init; }

    private uint _since;
    private long _sinceRealTicks;
    private uint _lastHold;
    private long _lastHoldRealTicks;
    private bool _started;
    private bool _realRead;
    private bool _spent;

    /// <summary>Whether a wait is under way (not yet spent).</summary>
    internal readonly bool Active => _started && !_spent;

    /// <summary>Whether to keep waiting at <paramref name="now"/> (clock in ms, wrapping); false once the wait passed <see cref="MaxMs"/>.</summary>
    internal bool Hold(uint now)
    {
        if (!_started || unchecked(now - _lastHold) > GapMs)
        {
            _started = true;
            _since = now;
            _sinceRealTicks = RealNow();
            _lastHold = now;
            _lastHoldRealTicks = _sinceRealTicks;
            _realRead = false;
            _spent = false;
        }

        if (PlayerbotPathJobs.LastPendingWasJob)
        {
            // Waiting for a path job (the pool is busy, the search runs) is not a tile read gone wrong: the time spent in it neither spends this
            // wait nor counts toward it (overloaded must not become unreachable), and the tile time already waited is kept: the wait's start
            // moves forward by the time since the last hold. The job wait has its own bound (PathJobWaitMaxMs).
            long realNow = RealNow();
            _since = unchecked(_since + (now - _lastHold));
            _sinceRealTicks += realNow - _lastHoldRealTicks;
            _lastHold = now;
            _lastHoldRealTicks = realNow;
            return true;
        }

        _realRead |= PathSearchCounters.LastPendingWasRealRead;

        _lastHold = now;
        _lastHoldRealTicks = RealNow();
        if (_spent) return false;
        if (unchecked(now - _since) <= MaxMs) return true;
        if (_realRead && (RealNow() - _sinceRealTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency <= MaxMs) return true;
        _spent = true;
        return false;
    }

    private readonly long RealNow() => RealTicks?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>A plan succeeded or the goal changed: the next pending answer starts a new wait.</summary>
    internal void Reset()
    {
        _started = false;
        _spent = false;
    }
}

internal static class PlayerbotNavigation
{
    [ThreadStatic] private static bool t_nonPathRefusal;
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, PlayerbotRisk> Guards = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, PlayerbotStallWatch> StallWatches = [];

    /// <summary>
    /// The risk whose hazards (<see cref="PlayerbotHazards"/>) every route of this bot must keep out of (null: none). The brain sets
    /// it each think; a party bot follows its master wherever he goes and has none.
    /// </summary>
    internal static void Guard(Player player, PlayerbotRisk? risk)
    {
        if (risk is null) Guards.Remove(player);
        else Guards.AddOrUpdate(player, risk);
    }

    /// <summary>The stall watch whose dead-end pin cells (<see cref="PlayerbotStallWatch.IsDeadEndPin"/>) routes of this bot must not end in.</summary>
    internal static void SetStallWatch(Player player, PlayerbotStallWatch? watch)
    {
        if (watch is null) StallWatches.Remove(player);
        else StallWatches.AddOrUpdate(player, watch);
    }

    internal static bool IsDeadEndPin(Player player, Vector3 point)
        => StallWatches.TryGetValue(player, out PlayerbotStallWatch? watch) && watch.World is { } world
            && watch.IsDeadEndPin(player.MapId, point.X, point.Y, world.NowMs);

    /// <summary>
    /// Whether <paramref name="point"/> lies in a cell the bots of the world found a dead end (<see cref="PlayerbotSharedPins"/>). Asked by the
    /// explore filter only: unlike <see cref="IsDeadEndPin"/> it never makes a plan refuse a destination, so an NPC standing there stays reachable.
    /// </summary>
    internal static bool IsWorldPin(Player player, Vector3 point)
        => StallWatches.TryGetValue(player, out PlayerbotStallWatch? watch) && watch.World is { } world
            && watch.IsWorldPin(player.MapId, player.Map?.InstanceId ?? 0, point.X, point.Y, world.NowMs);

    /// <summary>The detour offsets tried past a hazard on the way, on either side (yards beyond its radius).</summary>
    private static readonly float[] HazardDetourYards = [5f, 15f, 28f];

    /// <summary>How many offsets the hazard detours try (<see cref="HazardDetourYards"/>), each on either side of the hazard.</summary>
    internal static int HazardDetourOffsets => HazardDetourYards.Length;

    internal static bool IsUsablePath(PathResult path, int maxPoints, float maxDistance)
        => path.HasPath && (path.Type & (PathType.NotUsingPath | PathType.DestForced | PathType.FlyPath)) == 0
            && path.Points.Count <= maxPoints
            && path.Points.All(static p => p.IsFinite()) && float.IsFinite(path.Length)
            && path.Length <= maxDistance;

    /// <summary>
    /// The count of searches so far (this thread) that had to wait for a navigation tile still being read in the background
    /// (<see cref="PathOptions.NonBlockingTiles"/>). Take it before planning and ask <see cref="PendingSince"/> after a refusal: a refusal
    /// for want of a tile is no verdict on the destination, and the caller asks again on a later think instead of setting the goal aside.
    /// </summary>
    internal static long PendingMark => PathSearchCounters.TilesPending + PlayerbotPathJobs.PendingAnswers;

    /// <summary>Whether a search since <paramref name="mark"/> (<see cref="PendingMark"/>) waited for a navigation tile.</summary>
    internal static bool PendingSince(long mark) => PendingMark != mark;

    /// <summary>
    /// Plans like <see cref="TryPlan"/>, but tells a refusal for want of a navigation tile still being read in the background
    /// (<see cref="PlanOutcome.Pending"/>) from a verdict on the destination (<see cref="PlanOutcome.Refused"/>). The wait is bounded by
    /// <paramref name="wait"/> (<see cref="TileWait.MaxMs"/>): past it a pending answer is returned as <see cref="PlanOutcome.Refused"/>.
    /// </summary>
    internal static PlanOutcome PlanExact(Player player, Vector3 destination, PlayerbotOptions options, ref TileWait wait, uint now,
        out PlayerbotRoute? route, bool completedQuestReturn = false, bool friendlyTownEnder = false, bool chase = false,
        bool trial = false, bool nearestEnd = false)
    {
        using PlayerbotPathJobs.DeferrableScope deferrable = PlayerbotPathJobs.Deferrable();
        long mark = PendingMark;
        return Outcome(TryPlan(player, destination, options, out route, trial: trial, completedQuestReturn: completedQuestReturn,
            friendlyTownEnder: friendlyTownEnder, chase: chase, nearestEnd: nearestEnd, deferTrial: trial), mark, ref wait, now, ref route);
    }

    /// <summary><see cref="TryPlanFar"/> with the tri-state answer of <see cref="PlanExact"/>.</summary>
    internal static PlanOutcome PlanFar(Player player, Vector3 destination, PlayerbotOptions options, int searchNodes, ref TileWait wait,
        uint now, out PlayerbotRoute? route)
    {
        using PlayerbotPathJobs.DeferrableScope deferrable = PlayerbotPathJobs.Deferrable();
        long mark = PendingMark;
        return Outcome(TryPlanFar(player, destination, options, searchNodes, out route), mark, ref wait, now, ref route);
    }

    /// <summary><see cref="TryPlanToward"/> with the tri-state answer of <see cref="PlanExact"/>.</summary>
    internal static PlanOutcome PlanToward(Player player, Vector3 goal, PlayerbotOptions options, ref TileWait wait, uint now,
        out PlayerbotRoute? route, bool completedQuestReturn = false, bool friendlyTownEnder = false)
    {
        using PlayerbotPathJobs.DeferrableScope deferrable = PlayerbotPathJobs.Deferrable();
        long mark = PendingMark;
        return Outcome(TryPlanToward(player, goal, options, out route, completedQuestReturn, friendlyTownEnder, now), mark, ref wait, now, ref route);
    }

    /// <summary>
    /// Maximum flat shortfall (yards) between a blind wander leg's requested destination and the end of a navigated partial path.
    /// A wander ray projected into a cliff wall, canyon wall, or steep slope gets a partial Detour corridor that ends at the wall
    /// edge and slides the bot along the wall; refusing navigated wander routes that stop more than this short of their destination
    /// makes the bot turn away from the wall instead of hugging it.
    /// </summary>
    internal const float WanderMaxShortfallYards = 8f;

    /// <summary>
    /// Plans a blind exploration wander leg toward <paramref name="destination"/>, refusing navigated partial routes on walkable ground
    /// that stop more than <see cref="WanderMaxShortfallYards"/> short of <paramref name="destination"/> (into a wall or cliff).
    /// </summary>
    internal static PlanOutcome PlanWander(Player player, Vector3 destination, PlayerbotOptions options, ref TileWait wait, uint now,
        out PlayerbotRoute? route)
    {
        if (player.Map is { } wanderMap && destination.IsFinite())
        {
            float surface = wanderMap.Collision.GetHeight(destination.X, destination.Y, destination.Z + 20f);
            if (!InvalidHeight(surface)
                && MathF.Abs(surface - destination.Z) > 2f && MathF.Abs(surface - destination.Z) <= 25f)
            {
                destination = destination with { Z = surface };
            }
        }

        PlanOutcome outcome = PlanToward(player, destination, options, ref wait, now, out route);
        if (outcome == PlanOutcome.Route && route is { Navigated: true, Points.Count: > 0 }
            && Vector2.Distance(Flat(route.Points[^1]), Flat(destination)) > WanderMaxShortfallYards
            && !IsOnMeshFragment(player)
            && player.Map is { } map && OnWalkableMesh(map, PlayerbotMotion.CurrentPosition(player)))
        {
            route = null;
            return PlanOutcome.Refused;
        }

        return outcome;
    }

    private static PlanOutcome Outcome(bool planned, long mark, ref TileWait wait, uint now, ref PlayerbotRoute? route)
    {
        if (planned && route is not null)
        {
            wait.Reset();
            return PlanOutcome.Route;
        }

        route = null;
        return PendingSince(mark) && wait.Hold(now) ? PlanOutcome.Pending : PlanOutcome.Refused;
    }

    internal static bool TryPlanTowardPrepared(Player player, Vector3 goal, PlayerbotOptions options,
        PlayerbotAsyncNavigationPlan plan, out PlayerbotRoute? route, out bool deferred, bool completedQuestReturn = false,
        bool friendlyTownEnder = false, uint? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ResetPass();
        using IDisposable scope = plan.Activate();
        bool planned = TryPlanToward(player, goal, options, out route, completedQuestReturn, friendlyTownEnder, nowMs);
        deferred = plan.Deferred || plan.Exhausted || plan.NextRequest is not null;
        if (deferred) route = null;
        return planned && !deferred;
    }

    /// <param name="trial">A plan made to weigh something (the pull estimate's approach, a detour's waypoint), not to walk a goal: a
    /// refusal of it is no evidence that the bot is boxed in (<see cref="PlayerbotRisk.Refused"/> is not told).</param>
    /// <param name="deferTrial">The caller handles a pending answer (<see cref="PlanExact"/>): a trial search may then run as a path job
    /// (<see cref="PlayerbotPathJobs"/>) and be decided on a later think instead of searching on this one.</param>
    /// <param name="nearestEnd">The caller takes the mesh's nearest end to an off-mesh destination as its answer, however far it lies (a
    /// revive spot re-checked by its caller); a chase does too (<paramref name="chase"/>: a ranged bot fights from where the mesh ends).
    /// Other exact plans refuse such an end beyond <see cref="PlayerbotNavigationAnomalies.PathEndShortYards"/> (<see cref="Usable"/>).</param>
    internal static bool TryPlan(Player player, Vector3 destination, PlayerbotOptions options,
        out PlayerbotRoute? route, bool trial = false, bool completedQuestReturn = false, bool friendlyTownEnder = false,
        bool chase = false, bool nearestEnd = false, bool deferTrial = false)
        => Plan(player, destination, destination, options, partial: false, trial, out route, out _,
            completedQuestReturn: completedQuestReturn, friendlyTownEnder: friendlyTownEnder, chase: chase, nearestEnd: nearestEnd,
            deferTrial: deferTrial);

    /// <summary>
    /// A route to <paramref name="destination"/> with no hazard check at all: only the escape from a ring of strong creatures
    /// (<see cref="PlayerbotRisk.Refused"/>), which checks the route against every other hazard itself.
    /// </summary>
    internal static bool TryPlanUnguarded(Player player, Vector3 destination, PlayerbotOptions options, out PlayerbotRoute? route)
        => PlanDirect(player, destination, destination, options, partial: false, out route, out _) && route is not null;

    /// <summary>
    /// <see cref="TryPlan"/> with an explicit search budget (up to <see cref="MaxSearchNodes"/>) whatever the straight distance: for a
    /// goal near in a straight line but far by the mesh (a ledge above the bot, reached by a long way round). One query; the route
    /// bounds (<see cref="PlayerbotOptions.MaxRouteYards"/>, <see cref="PlayerbotOptions.MaxPathPoints"/>) still hold.
    /// </summary>
    internal static bool TryPlanFar(Player player, Vector3 destination, PlayerbotOptions options, int searchNodes, out PlayerbotRoute? route)
        => Plan(player, destination, destination, options, partial: false, trial: false, out route, out _, Math.Clamp(searchNodes, 1, MaxSearchNodes));

    /// <summary>Closer than this to its goal, a partial route ends near enough to count (<see cref="TryPlanToward"/>).</summary>
    internal const float PartialProgressYards = 2f;

    /// <summary>
    /// Flat travel threshold (yards) above which an incomplete partial route must make at least
    /// <see cref="PartialWallSlideProgressRatio"/> of its flat travel in progress toward the goal (<see cref="PartialClosesOnGoal"/>).
    /// </summary>
    internal const float PartialWallSlideTravelYards = 12f;

    /// <summary>
    /// Minimum ratio of goalward progress to flat travel required for an incomplete partial route longer than
    /// <see cref="PartialWallSlideTravelYards"/> (<see cref="PartialClosesOnGoal"/>), rejecting wall-sliding routes that run sideways
    /// along a ridge or cliff while closing on the goal by only a few yards.
    /// </summary>
    internal const float PartialWallSlideProgressRatio = 0.25f;

    /// <summary>
    /// A route towards <paramref name="goal"/>, which may lie beyond one route. Beyond <see cref="PlayerbotOptions.MaxPathPoints"/> yards
    /// the map's region graph leads (<see cref="RegionLeg"/>); without it, the goal itself when it is within one route's
    /// reach (<see cref="PlayerbotOptions.MaxRouteYards"/>, and <see cref="PlayerbotOptions.MaxPathPoints"/> yards for the terrain
    /// stepper), else the point that far along the straight line to it. When the navigation mesh does not connect that point, the
    /// route goes to the walkable point nearest to it (vmangos <c>PathFinder</c> with <c>PATHFIND_INCOMPLETE</c>), taken when it ends
    /// at least <see cref="PartialProgressYards"/> closer to the goal than the bot stands. One path query, as <see cref="TryPlan"/>.
    /// <para>
    /// The straight-line point on its own is often not walkable in the mountains, and the exact search then fails although the
    /// way is open: on the real Dun Morogh terrain the point 126 yards towards Mirthblade's body (359 yards away) has no exact
    /// path, while the mesh reaches a point 4 yards from it. The ghost was judged stalled after 10 seconds and took the spirit
    /// healer, and a living bot with a far quest giver stood still for good.
    /// </para>
    /// </summary>
    internal static bool TryPlanToward(Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? route,
        bool completedQuestReturn = false, bool friendlyTownEnder = false, uint? nowMs = null)
    {
        Vector3 origin = PlayerbotMotion.CurrentPosition(player);
        if (nowMs is { } watchNow && origin.IsFinite() && goal.IsFinite()
            && PlayerbotRoutePlanWatch.ShouldRefuse(player, origin, goal, watchNow))
        {
            route = null;
            return false;
        }

        long pending = PendingMark;
        t_nonPathRefusal = false;
        int probeRefusedBefore = PlayerbotThinkProbe.Active?.Refused ?? 0;
        bool planned = PlanTowardSteps(player, goal, options, out route, completedQuestReturn, friendlyTownEnder);
        // A far goal that got no route, or only a few yards of one, may mean the bot stands on a fragment of mesh no walk leaves: the
        // one straight move off it, when the ground beside it is the mesh's large part (TryFragmentHop). Asked only then (never on a
        // pending tile read, which is no verdict on the route), never per tick.
        Vector3 current = PlayerbotMotion.CurrentPosition(player);
        if (nowMs is { } hopNow && !PendingSince(pending) && (!planned || route is { Distance: < FragmentRouteYards }) && goal.IsFinite()
            && Vector2.Distance(new(current.X, current.Y), new(goal.X, goal.Y)) > FragmentGoalYards
            && TryFragmentHop(player, options, hopNow, out PlayerbotRoute? hop))
        {
            route = hop;
            planned = true;
        }

        bool routed = planned && route is not null;
        bool inconclusive = PendingSince(pending)
            || PlayerbotAsyncNavigationPlan.Current is { Deferred: true } or { Exhausted: true }
            || (!routed && (t_nonPathRefusal || ((PlayerbotThinkProbe.Active?.Refused ?? 0) > probeRefusedBefore)));
        if (nowMs is { } recordNow && !inconclusive && origin.IsFinite() && goal.IsFinite())
        {
            PlayerbotRoutePlanWatch.RecordOutcome(player, origin, goal, routed, route, recordNow);
        }

        // The outcome check (PlayerbotNavigationAnomalies): a pending tile read or a deferred async plan is no verdict, the rest is.
        PlayerbotNavigationAnomalies.ObservePlan(player, goal, current, routed, inconclusive);
        return planned;
    }

    private static bool PlanTowardSteps(Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? route,
        bool completedQuestReturn, bool friendlyTownEnder)
    {
        route = null;
        if (PlayerbotAsyncNavigationPlan.Current?.Exhausted == true) return false;
        Vector3 origin = PlayerbotMotion.CurrentPosition(player);
        if (!goal.IsFinite() || !origin.IsFinite()) return false;
        // A goal in a remembered dead-end pin cell is refused at once, near or far: no region search or path job is worth starting for it.
        if (IsDeadEndPin(player, goal))
        {
            t_nonPathRefusal = true;
            return false;
        }

        float distance = Vector3.Distance(origin, goal);
        long pending = PendingMark;
        // The way to the goal itself first: the search reads the navigation tiles it crosses (PathOptions.LoadTiles), so a road
        // that leaves the loaded tiles is followed. The straight-line steps below are a greedy fallback that stops wherever the
        // mesh ends nearest a point on the line: on the edge of the Red Cloud Mesa above Bloodhoof Village (live stress test
        // 2026-10-08, quest 1656, 99 stalls), whose way down runs south through a tile no grid held.
        // Within the first step the loop below plans to the goal itself, so the goal-first query would only repeat it.
        float chunk = MathF.Min(options.MaxRouteYards * 0.9f, Math.Max(1, options.MaxPathPoints - 2));
        // Beyond the first step the map's region graph knows the way, round mountains and out of dead ends: the leg goes to a portal
        // of that way (RegionLeg). Only when the graph does not decide (none, still being built, both ends in one region) do the
        // goal-first query and the straight-line steps below plan.
        if (distance > chunk && RegionLeg(player, goal, options, out route, completedQuestReturn, friendlyTownEnder) is { } planned) return planned;
        // A tile the plan needs is still being read: nothing below could decide either (each would meet the same tile).
        if (PendingSince(pending)) return false;
        // When the mesh had a corridor to judge (no path, a blacklisted end), the shorter straight-line points below would only repeat
        // the refusal: one plan attempt costs one query, as before. Only an unloaded tile or no mesh (a refused straight line) goes on.
        if (distance > chunk && distance <= options.MaxRouteYards)
        {
            if (Plan(player, goal, goal, options, partial: true, trial: false, out route, out bool goalStraightRefused,
                completedQuestReturn: completedQuestReturn, friendlyTownEnder: friendlyTownEnder))
            {
                if (route is not null) route.PartialToGoal = route.MeshEndedShort;
                return true;
            }
            if (!goalStraightRefused || PendingSince(pending)) return false;
        }
        // A point that far may lie on a navigation-mesh tile not loaded yet (tiles load with the map's grids, around the players):
        // the mesh then answers a straight line (vmangos PathFinder's HaveTiles shortcut), which the terrain stepper refuses across
        // any hill, and the bot stood at the tile's edge for good (Dawnrover on quest 35, 107 yards short of the Elwynn tile
        // boundary south of Goldshire, the 126-yard point beyond it). Only then is a shorter step tried: it stays on the loaded mesh,
        // walking it loads the next tile, and the next plan crosses. Any other refusal (no path, a blacklisted end) costs one query.
        for (; ; chunk /= 2)
        {
            Vector3 destination = distance > chunk ? origin + ((goal - origin) * (chunk / distance)) : goal;
            if (Plan(player, destination, goal, options, partial: true, trial: false, out route, out bool straightRefused,
                completedQuestReturn: completedQuestReturn, friendlyTownEnder: friendlyTownEnder))
            {
                // A chunk the mesh answered whole is a leg cut on purpose; one the mesh could not reach (Incomplete) is the mesh's
                // own partial answer toward the goal, however far along the line its point lay.
                if (route is not null) route.PartialToGoal = route.MeshEndedShort;
                return true;
            }
            if (!straightRefused || PendingSince(pending) || distance <= chunk / 2 || chunk / 2 < MinTowardChunkYards)
            {
                // A goal inside the first chunk never asked the region graph above, so a wall a few yards ahead is refused
                // even when the two sides are one component and the only door is the graph's (bot QA #810: same component,
                // a few dozen yards, the polygon search spent its budget on the open ground beside the wall). A far goal
                // already asked. A tile still being read is no verdict. A graph refusal is not taken here: the plans above
                // already ran the indoor climb and the steep escape.
                float firstChunk = MathF.Min(options.MaxRouteYards * 0.9f, Math.Max(1, options.MaxPathPoints - 2));
                if (!PendingSince(pending) && distance <= firstChunk
                    && RegionLeg(player, goal, options, out route, completedQuestReturn, friendlyTownEnder, goalAlreadyTried: true) is true)
                    return true;
                return false;
            }
        }
    }

    /// <summary>
    /// A component of the map's region graph (<see cref="NavRegionGraph.ComponentSize"/>) of at most this many regions is a fragment of mesh:
    /// the ridge pocket above the Valley of Trials (-137.6, -4265.7, map 1) is 3 regions, the sliver inside Ironforge (-4715.7, -871.2,
    /// map 0) is 1 (bot QA cluster C3, probes in <c>PlayerbotDeadEndPinProbeTests</c>); the mesh's real parts are thousands.
    /// </summary>
    internal const int FragmentRegions = 3;

    /// <summary>
    /// A fragment is also small in area: a component of at most <see cref="FragmentRegions"/> regions that holds more polygons than this
    /// is a real place (a plaza of three regions), not a sliver. The ridge pocket is 26 polygons (3 regions) and the Ironforge sliver is
    /// 2 polygons (1 region); the main continents are 563,799 and 1,457,700 polygons (<c>PlayerbotMeshFragmentTerrainTests</c>).
    /// </summary>
    internal const int FragmentPolygons = 500;

    /// <summary>A plan toward a goal farther than this (flat yards) that is refused or shorter than <see cref="FragmentRouteYards"/> asks whether the bot stands on a fragment.</summary>
    internal const float FragmentGoalYards = 60f;

    /// <summary>The route length (yards) under which a plan toward a far goal counts as no progress (a fragment's own few yards).</summary>
    internal const float FragmentRouteYards = 30f;

    /// <summary>How far (flat yards) from a fragment the hop looks for ground of a large component, and the rings it looks in.</summary>
    internal const float FragmentHopYards = 20f;

    private static readonly float[] HopRings = [3f, 6f, 10f, 14f, 18f, FragmentHopYards];

    private const int HopDirections = 8;

    /// <summary>The shortest time between two hop searches of one bot (ms, the bot clock the plan was asked at: the world's NowMs), so a fragment with no ground beside it costs a search per window, not per plan.</summary>
    internal const int HopRetryMs = 5_000;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, System.Runtime.CompilerServices.StrongBox<long>> HopRetry = new();

    private static readonly PathOptions WalkingLocate = new() { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };

    /// <summary>
    /// Whether the bot stands on a fragment of navigation mesh: the walking region graph (steep slopes excluded) puts its place in a
    /// component of at most <see cref="FragmentRegions"/> regions, so no walk leaves it. False when the graph is not built yet or the
    /// bot is on no region. A region lookup and an array read; allocation-free.
    /// </summary>
    internal static bool IsOnMeshFragment(Player player)
        => player.Map is { } map && IsPointOnMeshFragment(map, PlayerbotMotion.CurrentPosition(player));

    internal static bool IsPointOnMeshFragment(Map map, Vector3 point)
    {
        if (map.Collision.Pathfinder is not NavMeshPathfinder nav
            || nav.RegionGraph(map.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is not { } graph)
            return false;
        int region = nav.RegionAt(graph, point, WalkingLocate);
        return region >= 0 && IsFragment(graph, region);
    }

    private static bool IsFragment(NavRegionGraph graph, int region)
        => (graph.ComponentSize(region) <= FragmentRegions || graph.PolygonCount > FragmentPolygons * 4)
            && graph.ComponentPolygons(region) <= FragmentPolygons;

    /// <summary>
    /// The one straight move off a fragment (<see cref="IsOnMeshFragment"/>): a stepped terrain leg (floor within a yard per yard, in
    /// sight: <see cref="TryTerrainRoute"/>, so a climb above a step or a wall refuses it, a drop is taken as the terrain slopes) to the
    /// nearest point within <see cref="FragmentHopYards"/> that lies on a polygon of a large component. Eight directions in six rings,
    /// nearest ring first. Not asked again for <see cref="HopRetryMs"/> after a miss. World thread.
    /// </summary>
    private static bool TryFragmentHop(Player player, PlayerbotOptions options, uint now, out PlayerbotRoute? route)
    {
        route = null;
        if (player.Map is not { } map || map.Collision.Pathfinder is not NavMeshPathfinder nav
            || nav.RegionGraph(map.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is not { } graph
            || !PlayerbotMapPolicy.MayMoveOn(player, options))
            return false;
        Vector3 here = PlayerbotMotion.CurrentPosition(player);
        if (!here.IsFinite()) return false;
        int own = nav.RegionAt(graph, here, WalkingLocate);
        if (own < 0 || !IsFragment(graph, own)) return false;
        System.Runtime.CompilerServices.StrongBox<long> retry = HopRetry.GetValue(player, static _ => new System.Runtime.CompilerServices.StrongBox<long>(-1));
        if (retry.Value >= 0 && unchecked((int)(now - (uint)retry.Value)) < HopRetryMs) return false;
        long mark = PendingMark;
        foreach (float ring in HopRings)
            for (int direction = 0; direction < HopDirections; direction++)
            {
                float angle = direction * 2f * MathF.PI / HopDirections;
                Vector3 target = new(here.X + (MathF.Cos(angle) * ring), here.Y + (MathF.Sin(angle) * ring), here.Z);
                int region = nav.RegionAt(graph, target, WalkingLocate);
                if (region < 0 || IsFragment(graph, region)) continue;
                if (TryTerrainRoute(here, target, options, (x, y, z) => map.Collision.GetHeight(x, y, z),
                        (a, b) => PlayerbotMoveCheck.LineClear(map.Collision, a, b), out PlayerbotRoute? hop) && hop is not null)
                {
                    hop.Pathfinder = map.Collision.Pathfinder;
                    route = hop;
                    retry.Value = now;
                    return true;
                }
            }

        // A ring point whose tile is still being read answered -1 for that, not for a cliff: the window is spent only on a verdict.
        if (!PendingSince(mark)) retry.Value = now;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="goal"/> is a place no walk reaches from where the bot stands, by the map's region graph alone (no path
    /// search, no tile read beyond the two lookups): the walking graph (steep slopes excluded) puts the bot and the goal in different
    /// components, and the goal is outdoors (an indoor goal may be reached over steep stairs, <see cref="TryClimbIndoors"/>). False
    /// when the graph is not built yet or either place is on no region. <paramref name="fromComponent"/> and <paramref name="toComponent"/>
    /// are each place's component whatever the answer, -1 for a place on no region (or with no graph). Transport (tram, lift, taxi) is the
    /// caller's to try first.
    /// Live case: Chief Hawkwind's platform from the Valley of Trials ridge, 4,900 yards away, drew an 847-yard leg and a 3,349-yard
    /// walk toward a goal no walk reaches.
    /// </summary>
    internal static bool IsUnreachableOnFoot(Player player, Vector3 goal, out int fromComponent, out int toComponent)
    {
        fromComponent = toComponent = -1;
        if (!goal.IsFinite() || player.Map is not { } map || map.Collision.Pathfinder is not NavMeshPathfinder nav
            || nav.RegionGraph(map.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is not { } graph)
            return false;
        Vector3 here = PlayerbotMotion.CurrentPosition(player);
        int from = nav.RegionAt(graph, here, WalkingLocate), to = nav.RegionAt(graph, goal, WalkingLocate);
        // Each side's component is given whether or not the other has one (ComponentOf answers -1 for no region): a goal off the walking
        // mesh (a retreat point in a hillside or in the air) once also read as "bot -1" in nav/goal-unreachable, as if the bot were off it too
        // (bot QA #807: on the real mesh the bot stood in its continent's component in ten of the eleven live cases probed).
        fromComponent = graph.ComponentOf(from);
        toComponent = graph.ComponentOf(to);
        if (from < 0 || to < 0) return false;
        // Only a bot on a large walkable part may call a goal unreachable: on a fragment or indoors the bot's own component says nothing
        // about the world, and PlanToward's escapes (TryLeaveSteepGround, FromBeside, TryFragmentHop, the indoor climb) must run first.
        // A bot on steep ground is not told apart: the lookup snaps it to a walking region within a few yards (horizontally, or just above
        // or below), and it is judged from that region; one with none in reach has from < 0 and gets false above. The lookup is the
        // graph's, so the answer costs no path search.
        return fromComponent != toComponent && !IsFragment(graph, from)
            && !(graph.ComponentPolygons(to) > FragmentPolygons && (long)graph.ComponentPolygons(from) * 16 < graph.ComponentPolygons(to))
            && IsOutdoorsOnSurface(map, goal)
            && IsOutdoorsOnSurface(map, here);
    }

    /// <summary>The shortest step <see cref="TryPlanToward"/> shortens its point to before it gives up.</summary>
    internal const float MinTowardChunkYards = 24f;

    /// <summary>
    /// How far along the region graph's way one leg of <see cref="RegionLeg"/> goes at most (straight lines between portals), and at most
    /// half of <see cref="PlayerbotOptions.MaxRouteYards"/>: the path query's search grows with the distance (<see cref="SearchNodes"/>),
    /// and the way it finds is longer than the straight lines.
    /// </summary>
    internal const float RegionLegYards = 1000f;

    /// <summary>The most portals one region search settles (<see cref="NavRegionGraph.FindRoute"/>).</summary>
    internal const int RegionSearchNodes = 20_000;

    /// <summary>Within this distance (yards) of the final portal of an unreachable region route, <see cref="RegionLeg"/> refuses instead of falling back to straight-line steps into the dead end.</summary>
    internal const float NearestPortalRefusalYards = 4f;

    /// <summary>
    /// A leg of the way to a far <paramref name="goal"/> along the map's region graph (<see cref="NavRegionGraph"/>: which parts of the
    /// whole navigation mesh join which, built once per map off the world thread): the region search finds the way across the map coarsely,
    /// and the leg is the path query to its farthest portal within <see cref="RegionLegYards"/> (the goal itself when that near), or to
    /// its first portal. Without a way (the goal's region is not joined to the bot's) the search's way leads to the portal nearest the
    /// goal, as a partial path does. Null when the graph does not decide: none yet, no region under the bot or the goal, both in one
    /// region, or no portal nearer the goal than the bot; the straight-line planning goes on then.
    /// <para>
    /// Before, a goal beyond one route was approached only by points on the straight line to it. On the real terrain no route at all was
    /// planned from any stall point of issue #191 (quest 5881, Stonetalon to Grish Longrunner at the Great Lift, 5,200 to 7,900 yards)
    /// or of issue #194 (quest 1418, the Swamp of Sorrows to Kargath, 3,400 yards): the line's next point lay off the mesh, in rock. A
    /// partial path to the goal itself reaches Grish in four or five legs, but not Kargath: it ends at the point of the swamp nearest to
    /// it (-9356.8, -3872.5), from which no partial path closes on it, while the way leads west and north round the mountains (by
    /// Deadwind Pass, Redridge, the Burning Steppes and Blackrock Mountain: twelve legs, 9,400 yards).
    /// </para>
    /// </summary>
    private static bool? RegionLeg(Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? route,
        bool completedQuestReturn, bool friendlyTownEnder, bool goalAlreadyTried = false)
    {
        route = null;
        if (player.Map is not { } map || map.Collision.Pathfinder is not NavMeshPathfinder nav
            || nav.RegionGraph(map.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is not { } graph)
            return null;
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        int from = nav.RegionAt(graph, start, locate), to = nav.RegionAt(graph, goal, locate);
        if (from < 0 || to < 0) return null;
        // The region search is cached per graph and region pair (PlayerbotRegionRoutes). A miss under a planning wrapper (its callers handle
        // PlanOutcome.Pending) is queued for the pool (a bounded FIFO) and the bot answers pending, like a path job; elsewhere it searches here
        // once and keeps the answer. A queued pair whose work is lost is searched here after the wait bound, within the jobs' sync budget.
        bool mayDefer = PlayerbotPathJobs.InDeferrableScope && PlayerbotAsyncNavigationPlan.Current is null;
        PlayerbotPathJobs? jobs = PlayerbotPathJobs.Bound;
        long wallMs = jobs?.WallMs() ?? System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (!PlayerbotRegionRoutes.TryRoute(graph, from, start, to, goal, mayDefer, out NavRegionRouteStatus status, out List<Vector3> portals,
            jobs?.Logger, wallMs, options.PathJobWaitMaxMs, jobs?.RegionSyncBudget))
        {
            PlayerbotPathJobs.NoteExternalPending();
            return null;
        }

        bool unreachable = status == NavRegionRouteStatus.Unreachable && from != to && !IsFragment(graph, from)
            && map.Collision.IsOutdoors(goal.X, goal.Y, goal.Z + 1f);
        if (portals.Count == 0) return unreachable ? false : null;
        if (unreachable && Vector3.Distance(start, portals[^1]) <= NearestPortalRefusalYards) return false;
        if (status == NavRegionRouteStatus.Found && !goalAlreadyTried) portals.Add(goal);

        int pick = 0;
        float leg = MathF.Min(RegionLegYards, options.MaxRouteYards * 0.5f), along = Vector3.Distance(start, portals[0]);
        for (int i = 1; i < portals.Count; i++)
        {
            along += Vector3.Distance(portals[i - 1], portals[i]);
            if (along > leg) break;
            pick = i;
        }

        // The portal is a point on the walkable mesh joined to the bot's: the query reaches it, or closes on it partially. When neither
        // query is a route (a blacklisted portal, a hazard in the way) the graph does not decide: the goal-first query and the
        // straight-line steps still get their turn, unless the goal lies in an unreachable region component and the bot cannot even
        // progress toward the nearest portal.
        long pending = PendingMark;
        if (Plan(player, portals[pick], portals[pick], options, partial: true, trial: false, out route, out _,
            completedQuestReturn: completedQuestReturn, friendlyTownEnder: friendlyTownEnder))
        {
            // The region search found no way to the goal: the last portal is the nearest ground the graph joins to the bot, so a leg to
            // it is the walk to the dead end, as much the mesh's partial answer for the goal as a partial path is.
            if (route is not null && unreachable && pick == portals.Count - 1) route.PartialToGoal = true;
            return true;
        }
        if (PendingSince(pending)) { route = null; return null; }
        int fallbackAttempts = 0;
        for (int i = pick - 1; i >= 0 && fallbackAttempts < 3; i--)
        {
            if (i > 0 && Vector2.Distance(Flat(start), Flat(portals[i])) < PartialProgressYards) continue;
            fallbackAttempts++;
            if (Plan(player, portals[i], portals[i], options, partial: true, trial: false, out route, out _,
                completedQuestReturn: completedQuestReturn, friendlyTownEnder: friendlyTownEnder))
            {
                return true;
            }
            if (PendingSince(pending)) { route = null; return null; }
        }
        route = null;
        return unreachable ? false : null;
    }

    /// <summary>
    /// One path query from where the bot is now to <paramref name="destination"/>; with <paramref name="partial"/> a route that
    /// stops short of it on the mesh is accepted when it closes on <paramref name="goal"/>.
    /// </summary>
    /// <param name="straightRefused">The mesh had no corridor to offer, only a straight line (an unloaded tile, or no mesh), and the terrain
    /// stepper refused that line.</param>
    private static bool Plan(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial, bool trial,
        out PlayerbotRoute? route, out bool straightRefused, int? searchNodes = null, bool completedQuestReturn = false,
        bool friendlyTownEnder = false, bool chase = false, bool nearestEnd = false, bool deferTrial = false)
    {
        if (PlayerbotAsyncNavigationPlan.Current?.Deferred == true)
        {
            route = null;
            straightRefused = false;
            return false;
        }
        // A chase answers this think (combat responsiveness). A trial weighs a candidate: through a planning wrapper (PlanExact, whose
        // callers handle PlanOutcome.Pending: deferTrial) it is a path job like any far query and the candidate waits for a later think;
        // a bool caller (TryPlan) cannot tell a pending answer from a refusal and keeps its synchronous search.
        using PlayerbotPathJobs.NoDeferScope noDefer = chase || (trial && !deferTrial) ? PlayerbotPathJobs.NoDefer() : default;
        if (!PlanDirect(player, destination, goal, options, partial, out route, out straightRefused, searchNodes, noFallbacks: trial || chase,
                nearestEnd: chase || nearestEnd) || route is null) return false;
        bool crossBeside = completedQuestReturn && friendlyTownEnder;
        route.CompletedQuestReturn = completedQuestReturn;
        route.CrossBesidePlaceMarks = crossBeside;
        route.Chase = chase;
        if (PlayerbotAsyncNavigationPlan.Current?.Deferred == true)
        {
            route = null;
            straightRefused = false;
            return false;
        }
        Guards.TryGetValue(player, out PlayerbotRisk? risk);
        if (risk?.Blocking(player, route.Points, completedReturn: completedQuestReturn, crossBesidePlaceMarks: crossBeside, chase: chase) is not { } hazard)
        {
            if (!trial && !chase && route.Navigated && player.Map is { } smoothMap && smoothMap.Collision.MeshPoints is not null)
            {
                PlayerbotRoute smoothed = PlayerbotRouteSmoothing.VaryAndSmooth(
                    player, smoothMap, route, varyLateral: !PlayerbotMotion.IsActive(player));
                if (!ReferenceEquals(smoothed, route)
                    && risk?.Blocking(player, smoothed.Points, completedReturn: completedQuestReturn, crossBesidePlaceMarks: crossBeside, chase: chase) is null)
                {
                    smoothed.MeshEndedShort = route.MeshEndedShort;
                    smoothed.MeshPoints ??= route.MeshPoints ?? smoothed.Points;
                    route = smoothed;
                }
            }

            return true;
        }

        // A trial probe (PlayerbotRisk.Facts / FindDetour) only checks the direct route; it does not run 18 hazard detour searches.
        if (trial)
        {
            route = null;
            return false;
        }

        // The way passes through a hazard: walk round it (by a corner before it and one past it, on either side), or not at all.
        PlayerbotRoute direct = route;
        route = null;
        Vector3 start = direct.Points[0], end = direct.Points[^1];
        Vector3 flat = new(end.X - start.X, end.Y - start.Y, 0);
        if (flat.Length() < 1f || player.Map is not { } map) return false; // no way to judge: not evidence of a ring

        Vector3 along = Vector3.Normalize(flat);
        Vector3 side = new(-along.Y, along.X, 0);
        long detourMark = PendingMark;
        // Other ways after the direct one was refused: held to the think's search budget (PlayerbotThinkProbe.Fallback).
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        foreach (float extra in HazardDetourYards)
            foreach (float sign in (float[])[1f, -1f])
            {
                // Round the hazard by its side: a corner before it and a corner past it, each its radius plus the margin off.
                float off = hazard.Radius + extra;
                Vector3 before = hazard.At - (along * off) + (side * sign * off);
                Vector3 past = hazard.At + (along * off) + (side * sign * off);
                if (!PlanDirect(player, before, before, options, partial: false, out PlayerbotRoute? first, out _, noFallbacks: true) || first is null) continue;
                if (Leg(map, first.Points[^1], past, options) is not { } middle
                    || Leg(map, middle.Points[^1], end, options) is not { } last) continue;
                List<Vector3> points = [.. first.Points, .. middle.Points.Skip(1), .. last.Points.Skip(1)];
                float length = 0;
                for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
                if (length > options.MaxRouteYards || points.Count > options.MaxPathPoints * 3) continue;
                // Each leg keeps its own proof: the first may be the mesh's while a later one was stepped over the terrain, and the
                // motion must not skip the floor and line-of-sight checks on that one (PlayerbotMotion.Validate).
                bool[] legs = [.. Enumerable.Repeat(first.Navigated, first.Points.Count - 1),
                    .. Enumerable.Repeat(middle.Navigated, middle.Points.Count - 1), .. Enumerable.Repeat(last.Navigated, last.Points.Count - 1)];
                var detour = new PlayerbotRoute(points, length, legs)
                {
                    Pathfinder = map.Collision.Pathfinder,
                    MeshPoints = points,
                };
                if (risk.Blocking(player, detour.Points, completedReturn: completedQuestReturn, crossBesidePlaceMarks: crossBeside, chase: chase) is not null) continue;
                detour.CompletedQuestReturn = completedQuestReturn;
                detour.CrossBesidePlaceMarks = crossBeside;
                detour.Chase = chase;
                route = detour;
                return true;
            }

        // A detour leg that waited for a tile still being read is no evidence of a ring: the caller asks again once it is in.
        if (PendingSince(detourMark)) return false;
        // A goal's walk refused: evidence for the ring escape (PlayerbotRisk.Refused), which judges whether the bot is boxed in.
        t_nonPathRefusal = true;
        PlayerbotAsyncNavigationPlan? currentPlan = PlayerbotAsyncNavigationPlan.Current;
        if (!trial && (currentPlan is null || (!currentPlan.Deferred && !currentPlan.Exhausted)))
            risk.Refused(player, hazard);
        return false;
    }

    /// <summary>A further leg from <paramref name="from"/> (the navigation mesh's, else a stepped terrain line), or null.</summary>
    private static PlayerbotRoute? Leg(Map map, Vector3 from, Vector3 to, PlayerbotOptions options)
    {
        PathResult path = PlayerbotThinkProbe.Find(map.Collision, from, to, new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints),
            Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, MaxSearchNodes = PathOptions.DefaultMaxSearchNodes, LoadTiles = true, NonBlockingTiles = true });
        if ((path.Type & PathType.NotUsingPath) != 0)
            return PlayerbotMoveCheck.TerrainRoute(map, from, to, options, out PlayerbotRoute? terrain) ? terrain : null;
        return IsUsablePath(path, options.MaxPathPoints, options.MaxRouteYards) && (path.Type & PathType.Incomplete) == 0
            ? new PlayerbotRoute(path.Points, path.Length, navigated: true)
            {
                Pathfinder = map.Collision.Pathfinder,
                MeshPoints = path.Points,
            }
            : null;
    }

    /// <summary>The route without the hazard check (<see cref="Plan"/>).</summary>
    private static bool PlanDirect(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial,
        out PlayerbotRoute? route, out bool straightRefused, int? searchNodes = null, Vector3? from = null, bool noFallbacks = false,
        bool nearestEnd = false)
    {
        bool ok = PlanDirectCore(player, destination, goal, options, partial, out route, out straightRefused, searchNodes, from, noFallbacks,
            nearestEnd);
        if (ok && route is not null && player.Map is { } map)
            route.Pathfinder = map.Collision.Pathfinder;
        return ok;
    }

    private static bool PlanDirectCore(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial,
        out PlayerbotRoute? route, out bool straightRefused, int? searchNodes = null, Vector3? from = null, bool noFallbacks = false,
        bool nearestEnd = false)
    {
        straightRefused = false;
        route = null;
        if (player.Map is not { } map || !destination.IsFinite()
            || !new Vector3(player.X, player.Y, player.Z).IsFinite())
            return false;

        // AllowedMaps limits open-world travel; the dungeon or raid instance the bot is in is always walkable (PlayerbotMapPolicy).
        if (!PlayerbotMapPolicy.MayMoveOn(player, options))
        {
            t_nonPathRefusal = true;
            return false;
        }

        // A destination given up after a loop or remembered as a dead-end pin is refused, so the goal picks another one.
        if (PlayerbotMotion.IsBlacklisted(player, destination) || IsDeadEndPin(player, destination))
        {
            t_nonPathRefusal = true;
            return false;
        }

        // A moving bot plans from where it is now, not from its last heartbeat.
        Vector3 start = from ?? PlayerbotMotion.CurrentPosition(player);
        // The search budget is vmangos' own (navMeshQuery->init(navMesh, 2048), MoveMap.cpp:350) for a near destination, more for a far
        // one (SearchNodes). The old 512 polygons ran out on the real Dun Morogh mesh well inside the route bound: 200 yards from
        // Coldridge Valley to the Rockjaw Raiders (quest 179) answered no path with 512 and an 18-corner path with 2048.
        PathResult path = Query(map, start, destination, options, partial, steep: false, out bool capped, searchNodes);
        // A tile on the way is still being read in the background: neither a route nor a refusal. No fallback query could decide either.
        if ((path.Type & PathType.TilesPending) != 0) return false;
        // The no-mmap answer carries no walkability proof. Validate each short terrain step
        // before using that route; absent heights, steep terrain and known model obstructions refuse it.
        if ((path.Type & PathType.NotUsingPath) != 0)
        {
            // A route over a doodad or off the mesh is refused; TryPlanToward then plans a shorter chunk, which ends before the obstacle.
            straightRefused = !PlayerbotMoveCheck.TerrainRoute(map, start, destination, options, out route);
            return !straightRefused;
        }

        if (partial && from is null && !noFallbacks && (path.Type & PathType.Incomplete) != 0 && !AtDestination(path.End, destination)
            && IsIndoorsOrInWmo(map, destination)
            && Vector2.Distance(Flat(path.End), Flat(destination)) <= ClimbReachYards
            && MathF.Abs(path.End.Z - destination.Z) > ProjectedEndYards)
        {
            using (PlayerbotThinkProbe.Fallback())
            {
                if (TryClimbIndoors(map, start, destination, options, path, out route)) return true;
            }
        }

        if (Usable(player, path, start, destination, goal, partial, nearestEnd, options, out PlayerbotRoute? usable))
        {
            // The corners are the navigation mesh's, which already proves each leg walkable; they are not re-tested against the
            // collision data. A line of sight at their own height hits the floor model itself inside any building (the Deathknell
            // crypt, the Goldshire inn), and the floor probe finds nothing at some spots the mesh covers (on the crypt's stairs, at
            // 1645.4, 1665.9, 132.6, where Graveweaver stopped): either refused every route from or through such a place, and a bot
            // that started or stood there never moved again. The motion snaps to a floor where it finds one (PlayerbotMotion).
            route = usable;
            if (usable is not null) usable.MeshEndedShort = partial && (path.Type & PathType.Incomplete) != 0 && !AtDestination(path.End, destination);
            // The outcome of an exact plan: the route begins at the bot and ends at the destination (a partial plan closes on a goal by design).
            if (!partial && usable is not null) PlayerbotNavigationAnomalies.CheckExactRoute(player, start, destination, path, usable);
            return true;
        }

        // A leg planned from the end of a route (TryChain) or a trial/chase probe has no fallbacks: the next think plans it from the bot as ever.
        if (from is not null || noFallbacks) return false;
        // The way up indoors is a fallback too, held to the think's search budget (PlayerbotThinkProbe.Fallback).
        using (PlayerbotThinkProbe.Fallback())
        {
            if (TryClimbIndoors(map, start, destination, options, partial ? path : null, out route)) return true;
        }

        // No path at all from walkable ground: the destination is the one off the mesh, not the bot. A partial path is a patch of
        // walkable ground among steep slopes, or a fragment of mesh, only when it ends close by and the search ran out of corridor,
        // not out of nodes; any other refusal stays one.
        // A search that hit its node cap (PathSearchCounters) is no sign of a patch or a fragment: those are a few polygons, searched
        // through long before any cap, while a capped search towards a far goal ends on the visited polygon nearest it, which may lie
        // right beside the bot when the way starts off in another direction. Taken for a fragment, it ran the steep escape and the
        // starts beside the bot, up to 34 more searches of up to 32 768 nodes: on the 200-bot terrain benchmark (2026-10-11) the bots
        // in Coldridge Valley planning to a quest destination in Stormwind (1 800 yards) thought for up to 557 ms, 41 searches each.
        if ((path.Type & PathType.NoPath) != 0
                ? OnWalkableMesh(map, start)
                : !partial || (path.Type & PathType.Incomplete) == 0 || Vector2.Distance(Flat(path.End), Flat(start)) > SteepPatchYards || capped)
            return false;
        // The ways off a patch, a fragment or steep ground are fallbacks, held to the think's search budget (PlayerbotThinkProbe.Fallback).
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        if (TryLeaveSteepGround(player, map, start, destination, goal, options, partial, nearestEnd, out route)) return true;
        // A fragment answers a partial corridor that ends where it began; a no-path answer (the bot off the mesh) is not one.
        if ((path.Type & PathType.NoPath) != 0) return false;

        // The polygon under the bot is a fragment the mesh does not join to its surroundings (the nearest polygon is chosen as
        // Detour does, and a sliver on a rock or a root can win over the ground beside it): every query from it ends where it
        // began. Start a step away instead, on ground the bot can step to (live stress test 2026-10-08: bots in Shadowglen,
        // Dolanaar and north of Razor Hill stood on such spots for hours, every way out refused).
        route = FromBeside(player, map, start, destination, options, partial, goal);
        return route is not null;
    }

    /// <summary>
    /// A partial route ending this close to the bot (flat yards) may mean it stands on a patch of walkable ground among steep slopes
    /// (<see cref="TryLeaveSteepGround"/>) or on a fragment of mesh (<see cref="FromBeside"/>: the ones bots stood on in the live
    /// stress test reached 3 to 11 yards); one ending farther away is an ordinary refusal and costs no further query.
    /// </summary>
    internal const float SteepPatchYards = 15f;

    /// <summary>
    /// One query of the navigation mesh, with steep slopes excluded as for every bot route, or allowed (<paramref name="steep"/>);
    /// navigation tiles are read as needed (<see cref="PathOptions.LoadTiles"/>).
    /// </summary>
    private static PathResult Query(Map map, Vector3 from, Vector3 to, PlayerbotOptions options, bool partial, bool steep,
        int? searchNodes = null)
        => Query(map, from, to, options, partial, steep, out _, searchNodes);

    /// <summary><see cref="Query"/>, and whether its search hit the node cap (<see cref="PathSearchCounters.CappedSearches"/>).</summary>
    private static PathResult Query(Map map, Vector3 from, Vector3 to, PlayerbotOptions options, bool partial, bool steep, out bool capped,
        int? searchNodes = null)
    {
        long before = PathSearchCounters.CappedSearches;
        PathResult path = QueryOnce(map, from, to, options, partial, steep, searchNodes);
        capped = PathSearchCounters.CappedSearches != before || PlayerbotThinkProbe.ConsumeLastQueryCapped();
        return path;
    }

    private static PathResult QueryOnce(Map map, Vector3 from, Vector3 to, PlayerbotOptions options, bool partial, bool steep, int? searchNodes)
        => PlayerbotThinkProbe.Find(map.Collision, from, to, new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints), Mover = PathMover.Player,
            ExcludeFlags = steep ? NavTerrain.Empty : NavTerrain.SteepSlopes, AllowPartial = partial,
            MaxSearchNodes = searchNodes ?? SearchNodes(from, to), LoadTiles = true, NonBlockingTiles = true });

    /// <summary>Whether a mesh answer from <paramref name="from"/> is a route the bot takes (<see cref="Bounded"/>), and the route.</summary>
    private static bool Usable(Player player, PathResult path, Vector3 from, Vector3 destination, Vector3 goal, bool partial,
        bool nearestEnd, PlayerbotOptions options, out PlayerbotRoute? route)
    {
        route = null;
        if (!IsUsablePath(path, options.MaxPathPoints, float.PositiveInfinity))
            return false;
        // A partial answer (the corridor did not reach the destination) must at least close on the goal, and must not end at a
        // place given up after a loop or remembered as a dead-end pin. An end projected from above or below the destination stays at its spot and is exact.
        if (partial && (path.Type & PathType.Incomplete) != 0 && !AtDestination(path.End, destination))
        {
            if (PlayerbotMotion.IsBlacklisted(player, path.End) || IsDeadEndPin(player, path.End))
            {
                t_nonPathRefusal = true;
                return false;
            }
            if (!PartialClosesOnGoal(from, path.End, goal, path.Points.Count))
                return false;
        }
        // An exact answer the mesh could only end beside an off-mesh destination (Incomplete, not cut at the point bound) farther than the
        // outcome check allows (PathEndShortYards, flat, the bound of nav/path-ends-short) is no route to it: each end sampled live
        // (bot QA #784, 2026-10-17: 12.6 to 15.7 yards flat, 4.3 to 13.5 up or down) lay on another level, a road or ledge above a cliff
        // foot, and a bot that walked there stood out of reach of its destination. Nearer ends stay routes (an NPC on a crate, a spawn a
        // step off the floor); a caller that takes the nearest end as its answer (nearestEnd: a revive spot, a chase) keeps any.
        if (!partial && !nearestEnd && (path.Type & (PathType.Incomplete | PathType.Short)) == PathType.Incomplete
            && !AtDestination(path.End, destination)
            && Vector2.Distance(Flat(path.End), Flat(destination)) > PlayerbotNavigationAnomalies.PathEndShortYards)
            return false;

        route = Bounded(path.Points, options, complete: Reaches(path) || EndsAt(path, destination));
        return route is not null;
    }

    private static bool PartialClosesOnGoal(Vector3 from, Vector3 end, Vector3 goal, int pointCount = 2)
    {
        float progress = Vector2.Distance(Flat(from), Flat(goal)) - Vector2.Distance(Flat(end), Flat(goal));
        if (progress < PartialProgressYards) return false;
        if (pointCount >= 6 || progress >= 6f) return true;
        float travel = Vector2.Distance(Flat(from), Flat(end));
        return !(travel > PartialWallSlideTravelYards && progress < travel * PartialWallSlideProgressRatio);
    }

    /// <summary>
    /// A partial answer whose end is the destination's own spot, projected onto the mesh from above or below (within a yard, flat): the
    /// way reaches the destination as surely as a complete one, and a long one is cut at the route bound like one (<see cref="Bounded"/>).
    /// An area trigger's centre floats above its floor: the Stormwind tram entrance (2173) is 4.8 yards above it, so the mesh answered
    /// its 2,314-yard way from Sentinel Hill as incomplete, the bound refused it whole, and 67 Westfall bots stood still for a minute
    /// and gave their tram trip up (live-w22-r1, 2026-10-11 08:05).
    /// <para>
    /// Only within <see cref="ProjectedEndYards"/> vertically: <see cref="PathType.Incomplete"/> is also the answer of a search that stopped
    /// short, ending on the visited polygon nearest the goal, which may lie straight under a destination on a level the bot cannot reach
    /// (a tower top, a cliff top, a bridge, an upper floor; the end is searched up to 200 yards vertically). Such an end is no arrival.
    /// Storeys closer than that (some buildings' floors) can still pass; the stall watch gives those goals up.
    /// </para>
    /// </summary>
    internal static bool EndsAt(PathResult path, Vector3 destination)
        => path.HasPath && (path.Type & PathType.Incomplete) != 0 && (path.Type & (PathType.NotUsingPath | PathType.NoPath)) == 0
            && AtDestination(path.End, destination);

    /// <summary>
    /// How far above or below the destination a mesh answer's end may lie and still be the destination's spot: a complete path already
    /// accepts an end found up to 5 yards off vertically (NavMeshPathfinder NearExtents Y = 5), and the Stormwind tram entrance's centre
    /// floats 4.75 yards above its floor.
    /// </summary>
    internal const float ProjectedEndYards = 6f;

    /// <summary>Whether <paramref name="end"/> is <paramref name="destination"/>'s spot: within a yard flat and <see cref="ProjectedEndYards"/> vertically.</summary>
    private static bool AtDestination(Vector3 end, Vector3 destination)
        => Vector2.Distance(Flat(end), Flat(destination)) <= 1f && MathF.Abs(end.Z - destination.Z) <= ProjectedEndYards;

    /// <summary>
    /// The route along <paramref name="points"/>, or null when a point is not finite or it is longer than
    /// <see cref="PlayerbotOptions.MaxRouteYards"/>. A way that reaches its destination is cut there instead: its first part is
    /// progress along the real way, and the bot plans the rest from where it stops.
    /// </summary>
    private static PlayerbotRoute? Bounded(IReadOnlyList<Vector3> points, PlayerbotOptions options, bool complete)
    {
        var kept = new List<Vector3>(points.Count);
        float distance = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Vector3 point = points[index];
            if (!point.IsFinite()) return null;
            if (index > 0)
            {
                float leg = Vector3.Distance(points[index - 1], point);
                if (!float.IsFinite(leg)) return null;
                if (distance + leg > options.MaxRouteYards)
                {
                    if (!complete || kept.Count < 2) return null;
                    break;
                }

                distance += leg;
            }

            kept.Add(point);
        }

        if (kept.Count < 2) return null;
        Vector3[] arr = kept.ToArray();
        return new PlayerbotRoute(arr, distance, navigated: true) { MeshPoints = arr };
    }

    /// <summary>Whether a mesh answer reaches its destination (a whole way, or the first part of one cut at the point bound).</summary>
    private static bool Reaches(PathResult path)
        => path.HasPath && (path.Type & (PathType.Incomplete | PathType.NotUsingPath | PathType.NoPath)) == 0;

    /// <summary>How many directions <see cref="IsOnMeshIsland"/> asks the mesh round the bot about.</summary>
    internal const int IslandDirections = 12;

    /// <summary>How far away the points lie that <see cref="IsOnMeshIsland"/> asks the mesh to approach.</summary>
    internal const float IslandProbeYards = 120f;

    /// <summary>
    /// An island's ground ends this close to the bot (flat yards) in every direction <see cref="IsOnMeshIsland"/> asks about; ground that
    /// leads farther in any of them is no island.
    /// </summary>
    internal const float IslandYards = 55f;

    /// <summary>
    /// The point budget of each of <see cref="IsOnMeshIsland"/>'s queries, fixed (not the operator's MaxPathPoints): 120 yards of winding
    /// ground fit in it, and a path cut at it (<see cref="PathType.Short"/>) is never taken for an island's edge.
    /// </summary>
    internal const int IslandPathPoints = 256;

    /// <summary>The search budget of each of <see cref="IsOnMeshIsland"/>'s queries: an island's few polygons run out long before it.</summary>
    internal const int IslandSearchNodes = 2048;

    /// <summary>
    /// The nodes all of one <see cref="IsOnMeshIsland"/> call's queries may create together (each is given what is left, at most
    /// <see cref="IslandSearchNodes"/>). Before, twelve queries of 8192 nodes each could run synchronously on the world thread in one
    /// think (98 304 nodes; bot QA #1483, think-overran 504 ms in "explore" and "stall decision"); a probe that spends the budget, or
    /// whose search the budget cuts short, proves no island and answers no, as a cut search always did.
    /// </summary>
    internal const int IslandTotalSearchNodes = 16384;

    /// <summary>
    /// Whether the bot stands on a navigation-mesh island: walkable polygons the mesh joins to nothing round them, so that no route from
    /// there leads anywhere else. Partial queries from the bot towards <see cref="IslandDirections"/> points
    /// <see cref="IslandProbeYards"/> away all end within <see cref="IslandYards"/> of it; the first that ends farther (the mesh goes
    /// on, or the path was cut at its point bound), and any answer without the mesh (no navigation data, or no polygon under the bot: other
    /// recoveries' cases), says no. So an island beside water or a void, where a probe point has no polygon near it (a NoPath answer), is
    /// never detected: its bots still stall, and are given up goal by goal as before. At most
    /// <see cref="IslandDirections"/> queries, all through <see cref="PlayerbotThinkProbe.Find"/> as fallbacks (counted, and held to the
    /// think's node budget: a query the budget cut short or refused proves nothing and answers no). The brain asks once a stall gives
    /// a goal up and once when exploring stays confined (PlayerbotBrain.EscapeIsland, EscapeConfinedExplore; that one also from where
    /// the confinement began when the bot said no and has moved off it), and does nothing else in that think.
    /// <para>
    /// Live 2026-10-11 (bot QA #180, live-w23-r1, main 85023136): the ground inside the round wall of the Molsen Farm in Westfall
    /// (x -10305..-10274, y 1355..1385, the wall 1.7 to 3 yards high) is such an island, closed by the collision data too (no floor
    /// within a step and in sight leads over the wall). Wfbotac and Wfbotaj, once inside, stalled there whatever goal they took
    /// (Wiley the Black for quest 65, Bingles Blastenheimer for quest 2039, the Harvest Watchers beside the farm), restart after restart:
    /// every route ended at the island's edge nearest the goal, and the next one was refused.
    /// The lower Stormwind ledge of #207 extends almost 50 yards from the bot, but none of its 36 routes toward points 60, 120 and
    /// 200 yards away leaves it. Probing 120 yards and requiring every end within 55 yards includes this ledge while retaining
    /// a substantial gap between the probe and the edge; ordinary connected ground fails the first long-reaching probe.
    /// </para>
    /// </summary>
    internal static bool IsOnMeshIsland(Player player, PlayerbotOptions options)
        => IsOnMeshIsland(player, PlayerbotMotion.CurrentPosition(player), options);

    /// <summary>
    /// <see cref="IsOnMeshIsland(Player, PlayerbotOptions)"/> judged from <paramref name="start"/> on the bot's map instead of from where
    /// the bot stands: the confined-explore check asks it from the point the confinement began (PlayerbotBrain.EscapeConfinedExplore).
    /// </summary>
    internal static bool IsOnMeshIsland(Player player, Vector3 start, PlayerbotOptions options)
    {
        if (player.Map is not { } map) return false;
        if (!start.IsFinite()) return false;
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        return IsOnMeshIsland(start, (probe, pathOptions) =>
        {
            int cutBefore = PlayerbotThinkProbe.Active?.Clamped ?? 0;
            long nodesBefore = PathSearchCounters.NodesCreated, cappedBefore = PathSearchCounters.CappedSearches;
            PathResult path = PlayerbotThinkProbe.Find(map.Collision, start, probe, pathOptions);
            // A search the think's node budget or its own node cap cut short ends early on ground that may go on: no proof of an island.
            bool cut = (PlayerbotThinkProbe.Active?.Clamped ?? 0) != cutBefore || PathSearchCounters.CappedSearches != cappedBefore;
            return (path, PathSearchCounters.NodesCreated - nodesBefore, cut);
        });
    }

    /// <summary>
    /// The island judgement over <paramref name="query"/> (the path from <paramref name="start"/> to a probe point under the given options,
    /// the nodes it created, whether a node bound cut it short): every query is held to what is left of
    /// <see cref="IslandTotalSearchNodes"/> (at most <see cref="IslandSearchNodes"/> each), and the first that ends far, is cut, or finds the
    /// budget spent (fewer than <see cref="PlayerbotThinkProbe.MinQueryNodes"/> left) answers no. The queries run on the world thread.
    /// </summary>
    internal static bool IsOnMeshIsland(Vector3 start, Func<Vector3, PathOptions, (PathResult Path, long Nodes, bool Cut)> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        long remaining = IslandTotalSearchNodes;
        for (int direction = 0; direction < IslandDirections; direction++)
        {
            if (remaining < PlayerbotThinkProbe.MinQueryNodes) return false;
            float angle = direction * 2f * MathF.PI / IslandDirections;
            Vector3 probe = start + (new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * IslandProbeYards);
            (PathResult path, long nodes, bool cut) = query(probe, new PathOptions { MaxPoints = IslandPathPoints,
                Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = (int)Math.Min(IslandSearchNodes, remaining),
                LoadTiles = true, NonBlockingTiles = true });
            remaining -= Math.Max(0L, nodes);
            if (cut) return false;
            // A path cut at the point bound (Short) ends early on ground that goes on: winding connected ground, never an island.
            if (!path.HasPath || (path.Type & (PathType.NotUsingPath | PathType.NoPath | PathType.Short)) != 0 || !path.End.IsFinite()
                || Vector2.Distance(Flat(path.End), Flat(start)) > IslandYards)
                return false;
        }

        return true;
    }

    /// <summary>How far from the bot the starts beside it lie (<see cref="FromBeside"/>), eight directions each.</summary>
    private static readonly float[] BesideYards = [2.5f, 5f];

    /// <summary>How far toward the target a doodad-aware sight starts in <see cref="FromBeside"/>, clear of the bot's own contact.</summary>
    internal const float FragmentContactYards = 1f;

    /// <summary>
    /// <paramref name="start"/> moved <see cref="FragmentContactYards"/> toward <paramref name="toward"/> (flat), or
    /// <paramref name="start"/> itself when nearer than that. A start on a mesh fragment lying on a rock or root is blocked in
    /// every direction by that doodad; a sight from one yard out sees only what lies beyond it.
    /// </summary>
    private static Vector3 OffStart(Vector3 start, Vector3 toward)
    {
        Vector2 flat = Flat(toward) - Flat(start);
        float length = flat.Length();
        if (!float.IsFinite(length) || length <= FragmentContactYards) return start;
        Vector2 step = flat / length * FragmentContactYards;
        return new Vector3(start.X + step.X, start.Y + step.Y, start.Z);
    }

    /// <summary>
    /// A route that steps from <paramref name="start"/> to ground beside it (a stepped terrain leg: floor within a step, in sight)
    /// and follows the mesh from there to <paramref name="destination"/>, or null. At most sixteen queries.
    /// </summary>
    private static PlayerbotRoute? FromBeside(Player player, Map map, Vector3 start, Vector3 destination, PlayerbotOptions options, bool partial, Vector3 goal)
    {
        foreach (float yards in BesideYards)
        {
            for (int direction = 0; direction < 8; direction++)
            {
                float angle = MathF.Atan2(destination.Y - start.Y, destination.X - start.X) + (direction * MathF.PI / 4f);
                float x = start.X + (MathF.Cos(angle) * yards), y = start.Y + (MathF.Sin(angle) * yards);
                float floor = map.Collision.GetHeight(x, y, start.Z + 2f);
                if (InvalidHeight(floor) || MathF.Abs(floor - start.Z) > 2.5f) continue;
                Vector3 beside = new(x, y, floor + 0.05f);
                // The doodad-blind sight covers the whole hop; the doodad-aware one starts clear of the bot's own contact (OffStart), since the start
                // is often a rock or root and a sight from inside it is blocked in every direction.
                if (!map.Collision.IsInLineOfSight(start.X, start.Y, start.Z + 2, beside.X, beside.Y, beside.Z + 2)
                    || !PlayerbotMoveCheck.LineClear(map.Collision, OffStart(start, beside), beside)) continue;
                PathResult path = Query(map, beside, destination, options, partial, steep: false, out bool capped);
                if ((path.Type & PathType.TilesPending) != 0) return null;
                if (path.Points.Count > 0 && !PlayerbotMoveCheck.LineClear(map.Collision, OffStart(start, path.Points[0]), path.Points[0])) continue;
                // The same arrival rule as Usable: an end at the destination's own spot (EndsAt) reaches it.
                bool reaches = Reaches(path) || EndsAt(path, destination);
                if (!path.HasPath || (path.Type & (PathType.NotUsingPath | PathType.NoPath)) != 0
                    || (!reaches && (Vector3.Distance(path.End, beside) <= SteepPatchYards
                        || !PartialClosesOnGoal(start, path.End, goal, path.Points.Count))))
                {
                    // Refused after running out of nodes short of the destination: the other starts lie a few yards away and search the
                    // same ground, so they end the same way (each up to 32 768 nodes). A search that reached it despite the cap is no such
                    // sign (refused for its length, say), and the next start is tried.
                    if (capped && (path.Type & (PathType.Incomplete | PathType.NoPath)) != 0) return null;
                    continue;
                }

                if (Bounded([start, .. path.Points], options, reaches) is { } joined && joined.Points.Count <= options.MaxPathPoints + 1)
                {
                    return joined;
                }
            }
        }

        return null;
    }

    /// <summary>How far above where the bot stands a way off steep ground may lead (<see cref="TryLeaveSteepGround"/>).</summary>
    internal const float SteepEscapeClimbYards = 1.5f;

    /// <summary>The search budget of <see cref="RequiresSteepAscent"/>'s two queries (a diagnosis, not a route: the climb shows within the first corners).</summary>
    internal const int DiagnosisSearchNodes = 8192;

    /// <summary>A leg of a path climbs steep ground when it rises at least this much per flat yard (45 degrees).</summary>
    internal const float SteepLegSlope = 0.9f;

    /// <summary>The height gained on steep legs within the first corners of the steep-allowed path that makes a way unwalkable.</summary>
    internal const float SteepAscentYards = 15f;

    /// <summary>
    /// Whether the ordinary mesh cannot leave the bot's spot toward <paramref name="goal"/> because the way there climbs steep ground:
    /// the ordinary query ends at the bot's feet, and the path with steep polygons allowed first gains at least
    /// <see cref="SteepAscentYards"/> of height along legs steeper than <see cref="SteepLegSlope"/> (the slope of each leg, not the
    /// height of the highest corner: an ordinary climb to an uphill turn-in gains more than <see cref="SteepEscapeClimbYards"/> too).
    /// A local topology diagnosis, not proof the goal is unreachable from elsewhere. Two queries of
    /// <see cref="DiagnosisSearchNodes"/> nodes at most; the caller runs it once per quest and spot (<see cref="PlayerbotWorldDestinations"/>).
    /// </summary>
    internal static bool RequiresSteepAscent(Player player, Vector3 goal, PlayerbotOptions options, out float ascent)
    {
        ascent = 0;
        if (player.Map is not { } map || !goal.IsFinite()) return false;
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        PathResult ordinary = Query(map, start, goal, options, partial: true, steep: false, DiagnosisSearchNodes);
        if ((ordinary.Type & PathType.NotUsingPath) != 0
            || (ordinary.Type & PathType.NoPath) == 0 &&
                ((ordinary.Type & PathType.Incomplete) == 0 || Vector2.Distance(Flat(ordinary.End), Flat(start)) > SteepPatchYards))
            return false;

        PathResult steep = Query(map, start, goal, options, partial: true, steep: true, DiagnosisSearchNodes);
        if (!steep.HasPath || (steep.Type & (PathType.NotUsingPath | PathType.NoPath | PathType.DestForced)) != 0) return false;
        float climb = 0;
        for (int index = 1; index < steep.Points.Count && index <= SteepEscapeCorners; index++)
        {
            Vector3 from = steep.Points[index - 1], to = steep.Points[index];
            if (!from.IsFinite() || !to.IsFinite()) return false;
            float rise = to.Z - from.Z;
            if (rise > 0 && rise >= Vector2.Distance(Flat(from), Flat(to)) * SteepLegSlope) climb += rise;
        }

        if (climb < SteepAscentYards) return false;
        ascent = climb;
        return true;
    }

    /// <summary>The answer of a deferred diagnosis (<see cref="DiagnoseSteepAscent"/>, <see cref="DiagnoseReturnHazard"/>).</summary>
    internal enum DiagnosisState : byte
    {
        /// <summary>A search of the diagnosis is still a path job (or a tile read): no verdict, ask again on a later think.</summary>
        Pending,

        /// <summary>The verdict: not the case.</summary>
        Clear,

        /// <summary>The verdict: the case.</summary>
        Found,
    }

    /// <summary>The bot may stand this far from where a diagnosis was made and still get its cached verdict (<see cref="PlayerbotWorldDestinations"/>'s spot).</summary>
    internal const float DiagnosisSpotYards = 3f;

    /// <summary>...and within this height (yards): a floor above or below the spot is another place (a verdict made under a mezzanine is not the one above it).</summary>
    internal const float DiagnosisSpotHeightYards = 1.5f;

    /// <summary>How long a diagnosis verdict stands (ms): a hazard or the bot's neighbourhood may change.</summary>
    internal const uint DiagnosisTtlMs = 120_000;

    /// <summary>The verdicts one bot keeps (oldest dropped first).</summary>
    internal const int MaxDiagnoses = 8;

    private sealed class DiagnosisBook
    {
        internal readonly List<DiagnosisEntry> Entries = [];
    }

    private sealed class DiagnosisEntry(bool steep, uint questId, Vector3 spot, Vector3 goal, bool friendlyTownEnder, uint madeMs)
    {
        internal readonly bool Steep = steep;
        internal readonly uint QuestId = questId;
        internal readonly Vector3 Spot = spot;
        internal readonly Vector3 Goal = goal;
        internal readonly bool FriendlyTownEnder = friendlyTownEnder;
        internal readonly uint MadeMs = madeMs;
        internal bool Found;
        internal float Ascent;
        internal PlayerbotHazard Hazard;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, DiagnosisBook> Diagnoses = [];

    private static DiagnosisEntry? CachedDiagnosis(Player player, bool steep, uint questId, Vector3 spot, Vector3 goal, bool friendlyTownEnder, uint now)
    {
        if (!Diagnoses.TryGetValue(player, out DiagnosisBook? book)) return null;
        foreach (DiagnosisEntry entry in book.Entries)
        {
            if (entry.Steep == steep && entry.QuestId == questId && entry.FriendlyTownEnder == friendlyTownEnder
                && unchecked(now - entry.MadeMs) <= DiagnosisTtlMs
                && Vector3.DistanceSquared(entry.Goal, goal) <= 1f
                && Vector2.DistanceSquared(new Vector2(entry.Spot.X, entry.Spot.Y), new Vector2(spot.X, spot.Y)) <= DiagnosisSpotYards * DiagnosisSpotYards
                && MathF.Abs(entry.Spot.Z - spot.Z) <= DiagnosisSpotHeightYards)
                return entry;
        }

        return null;
    }

    private static void RememberDiagnosis(Player player, DiagnosisEntry entry)
    {
        DiagnosisBook book = Diagnoses.GetOrCreateValue(player);
        if (book.Entries.Count >= MaxDiagnoses) book.Entries.RemoveAt(0);
        book.Entries.Add(entry);
    }

    /// <summary>
    /// <see cref="RequiresSteepAscent"/> as a deferrable diagnosis: its two searches of <see cref="DiagnosisSearchNodes"/> nodes run as path
    /// jobs (<see cref="PlayerbotPathJobs"/>) and the verdict is <see cref="DiagnosisState.Pending"/> until they are answered; the caller asks
    /// again on a later think. The verdict is kept per bot, quest and spot (<see cref="DiagnosisSpotYards"/>, <see cref="DiagnosisTtlMs"/>), so
    /// asking again once decided runs no search.
    /// </summary>
    internal static DiagnosisState DiagnoseSteepAscent(Player player, Vector3 goal, PlayerbotOptions options, uint questId, uint now, out float ascent)
    {
        Vector3 spot = PlayerbotMotion.CurrentPosition(player);
        if (CachedDiagnosis(player, steep: true, questId, spot, goal, friendlyTownEnder: false, now) is { } known)
        {
            ascent = known.Ascent;
            return known.Found ? DiagnosisState.Found : DiagnosisState.Clear;
        }

        using PlayerbotPathJobs.DeferrableScope deferrable = PlayerbotPathJobs.Deferrable();
        long mark = PendingMark;
        bool found = RequiresSteepAscent(player, goal, options, out ascent);
        if (PendingSince(mark))
        {
            ascent = 0;
            return DiagnosisState.Pending;
        }

        RememberDiagnosis(player, new DiagnosisEntry(steep: true, questId, spot, goal, false, now) { Found = found, Ascent = ascent });
        return found ? DiagnosisState.Found : DiagnosisState.Clear;
    }

    /// <summary>
    /// <see cref="HasBlockingReturnHazard"/> as a deferrable diagnosis, as <see cref="DiagnoseSteepAscent"/>: its route search is a path job
    /// and the verdict is kept per bot, quest and spot.
    /// </summary>
    internal static DiagnosisState DiagnoseReturnHazard(Player player, Vector3 goal, PlayerbotOptions options, uint questId, uint now,
        out PlayerbotHazard hazard, bool friendlyTownEnder = false)
    {
        Vector3 spot = PlayerbotMotion.CurrentPosition(player);
        if (CachedDiagnosis(player, steep: false, questId, spot, goal, friendlyTownEnder, now) is { } known)
        {
            hazard = known.Hazard;
            return known.Found ? DiagnosisState.Found : DiagnosisState.Clear;
        }

        using PlayerbotPathJobs.DeferrableScope deferrable = PlayerbotPathJobs.Deferrable();
        long mark = PendingMark;
        bool found = HasBlockingReturnHazard(player, goal, options, out hazard, friendlyTownEnder);
        if (PendingSince(mark))
        {
            hazard = default;
            return DiagnosisState.Pending;
        }

        RememberDiagnosis(player, new DiagnosisEntry(steep: false, questId, spot, goal, friendlyTownEnder, now) { Found = found, Hazard = hazard });
        return found ? DiagnosisState.Found : DiagnosisState.Clear;
    }

    /// <summary>
    /// A connected ordinary route still crosses a hazard the completed-return rule keeps in force. A death or retreat mark
    /// beside the road is not one of those when <paramref name="friendlyTownEnder"/> (the only walk to a friendly quest ender
    /// in town).
    /// </summary>
    internal static bool HasBlockingReturnHazard(Player player, Vector3 goal, PlayerbotOptions options,
        out PlayerbotHazard hazard, bool friendlyTownEnder = false)
    {
        hazard = default;
        if (!Guards.TryGetValue(player, out PlayerbotRisk? risk) || risk.Hazards.Count == 0
            || !PlanDirect(player, goal, goal, options, partial: false, out PlayerbotRoute? route, out _,
                DiagnosisSearchNodes) || route is null
            || risk.Blocking(player, route.Points, completedReturn: true, crossBesidePlaceMarks: friendlyTownEnder) is not { } blocked)
            return false;
        hazard = blocked;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="npcTemplate"/> is a friendly town NPC to <paramref name="playerTemplate"/>: neither direction is
    /// hostile, and at least one is friendly (the same template reaction <see cref="ArcaneCore.Game.Combat.FactionCombatHooks"/>
    /// uses). Unresolved templates are not. A neutral quest giver is not this case.
    /// </summary>
    internal static bool IsFriendlyTownNpc(FactionTemplateCatalog? factions, uint playerTemplate, uint npcTemplate)
    {
        if (factions is not { } catalog) return false;
        if (catalog.Find(npcTemplate) is not { } npc || catalog.Find(playerTemplate) is not { } self) return false;
        if (npc.IsHostileTo(self) || self.IsHostileTo(npc)) return false;
        return npc.IsFriendlyTo(self) || self.IsFriendlyTo(npc);
    }

    /// <summary>The corners of a way off steep ground tried as the place where the ordinary routes begin again.</summary>
    private const int SteepEscapeCorners = 16;

    /// <summary>
    /// Every bot route excludes the navigation mesh's steep polygons (<see cref="NavTerrain.SteepSlopes"/>). A bot standing on steep
    /// ground, or on a patch of walkable polygons among them, then gets no route anywhere: the mesh finds no walkable polygon within
    /// three yards of it (no path), or only the patch's own edge (a partial path that never closes). The way off is the mesh's own
    /// path with the steep polygons allowed, cut at its first corner on walkable ground from which the ordinary query is a route,
    /// and only while no corner leads more than <see cref="SteepEscapeClimbYards"/> above the bot: a player slides down a slope it
    /// cannot walk up. A destination off the walkable mesh from walkable ground gets no route, as before.
    /// <para>
    /// Live 2026-10-09: Ironwander (a warrior, level 7) stood on the mountainside north of Coldridge Valley at -5605.6, 155.8, 453.3,
    /// seven yards above the slope's floor, for over two hours. Every route from there answered no path, so every goal failed before
    /// it began: the Ice Claw Bear it last chose (entry 1196) was marked unreachable, the exploration refused, and the stall watch's
    /// give-up had nothing to give up. With the steep polygons allowed the mesh leads down to the bear's meadow in 129 yards, past
    /// a walkable patch at -5573, 185.6, 441.4 whose own edge ends every ordinary route.
    /// </para>
    /// </summary>
    private static bool TryLeaveSteepGround(Player player, Map map, Vector3 start, Vector3 destination, Vector3 goal,
        PlayerbotOptions options, bool partial, bool nearestEnd, out PlayerbotRoute? route)
    {
        route = null;
        PathResult steep = Query(map, start, destination, options, partial: true, steep: true);
        if (!steep.HasPath || (steep.Type & (PathType.NotUsingPath | PathType.DestForced | PathType.FlyPath)) != 0) return false;

        float distance = 0;
        for (int index = 1; index < steep.Points.Count && index <= SteepEscapeCorners; index++)
        {
            Vector3 point = steep.Points[index];
            if (!point.IsFinite() || point.Z > start.Z + SteepEscapeClimbYards) return false;
            distance += Vector3.Distance(steep.Points[index - 1], point);
            if (!float.IsFinite(distance) || distance > options.MaxRouteYards) return false;
            if (!OnWalkableMesh(map, point)) continue;
            if (Vector3.Distance(point, destination) > 1f)
            {
                PathResult onward = Query(map, point, destination, options, partial, steep: false, out bool capped);
                if ((onward.Type & PathType.TilesPending) != 0) return false;
                if (!Usable(player, onward, point, destination, goal, partial, nearestEnd, options, out _))
                {
                    // Ran out of nodes short of the destination: the next corner, a few yards on, searches the same ground (see FromBeside).
                    if (capped && (onward.Type & (PathType.Incomplete | PathType.NoPath)) != 0) return false;
                    continue;
                }
            }
            Vector3[] steepArr = steep.Points.Take(index + 1).ToArray();
            route = new PlayerbotRoute(steepArr, distance, navigated: true)
            {
                Pathfinder = map.Collision.Pathfinder,
                MeshPoints = steepArr,
            };
            return true;
        }

        return false;
    }

    /// <summary>How far (flat) from an indoor destination the walkable mesh must end for <see cref="TryClimbIndoors"/>.</summary>
    internal const float ClimbReachYards = 20f;

    /// <summary>How much longer than the walkable mesh's partial way the way up <see cref="TryClimbIndoors"/> takes may be.</summary>
    internal const float ClimbExtraYards = 60f;

    /// <summary>
    /// The way to a destination inside a building over its stairs, where the mesh flags the stairs as steep slopes. The generator marks
    /// polygons steeper than a player walks up (<see cref="NavTerrain.SteepSlopes"/>) whatever they lie on, and a flight of stairs in a
    /// model is such a ramp, while a player climbs it step by step. Taken only when the destination is indoors (inside a model's group,
    /// <see cref="MapCollision.IsOutdoors"/>), the walkable mesh ends within <see cref="ClimbReachYards"/> of it (flat, the stairs
    /// above or below), and the way with steep polygons reaches it at most <see cref="ClimbExtraYards"/> longer: a slope of the terrain
    /// outdoors stays refused (<see cref="TryLeaveSteepGround"/>). One or two queries more, only for an indoor destination the walkable
    /// mesh does not reach.
    /// <para>
    /// Neeka Bloodscar, the ender of quest 1418, stands at the top of the Kargath tower (-6672.3, -2240.5, 278.6); the walkable mesh
    /// leads up its lower stairs and ends 13 yards below her, where the upper flight is flagged steep. Every bot that reached Kargath
    /// stood there (issue #194).
    /// </para>
    /// <para>
    /// Not in a dungeon, raid or battleground (<see cref="ArcaneCore.Kernel.WorldData.MapTemplate.IsDungeon"/>, <see cref="ArcaneCore.Kernel.WorldData.MapTemplate.IsBattleground"/>): there
    /// nearly every place is indoors, so the indoor test would not tell stairs from slopes, and no stall there is known to need it.
    /// </para>
    /// </summary>
    internal static bool IsOutdoorsOnSurface(Map map, Vector3 point)
    {
        if (!map.Collision.IsOutdoors(point.X, point.Y, point.Z + 1f))
        {
            return false;
        }

        float terrain = map.Terrain.GetHeight(point.X, point.Y, point.Z);
        if (PlayerbotMotion.ValidHeight(terrain) && point.Z < terrain - 8f)
        {
            return false;
        }

        return true;
    }

    internal static bool IsIndoorsOrInWmo(Map map, Vector3 point)
    {
        if (!IsOutdoorsOnSurface(map, point))
        {
            return true;
        }

        return map.Collision.LineOfSight.TryGetAreaInfo(map.MapId, point.X, point.Y, point.Z + 1f, out _);
    }

    private static bool TryClimbIndoors(Map map, Vector3 start, Vector3 destination, PlayerbotOptions options, PathResult? walk,
        out PlayerbotRoute? route)
    {
        route = null;
        if (map.Template is { IsDungeon: true } or { IsBattleground: true }) return false;
        bool destIndoors = IsIndoorsOrInWmo(map, destination);
        bool startIndoors = IsIndoorsOrInWmo(map, start);
        if (!destIndoors && !startIndoors) return false;
        PathResult reach = walk ?? Query(map, start, destination, options, partial: true, steep: false);
        if (!reach.HasPath || (reach.Type & (PathType.NotUsingPath | PathType.NoPath)) != 0 || Reaches(reach) || EndsAt(reach, destination)
            || (!(destIndoors && Vector2.Distance(Flat(reach.End), Flat(destination)) <= ClimbReachYards)
                && !(startIndoors && Vector2.Distance(Flat(reach.End), Flat(start)) <= ClimbReachYards)))
            return false;
        PathResult climb = Query(map, start, destination, options, partial: false, steep: true);
        float maxClimbLength = MathF.Abs(reach.End.Z - destination.Z) > ProjectedEndYards
            ? MathF.Max(reach.Length, Vector2.Distance(Flat(start), Flat(destination))) + 90f
            : (destIndoors && Vector2.Distance(Flat(reach.End), Flat(destination)) <= ClimbReachYards
                ? reach.Length
                : MathF.Max(reach.Length, Vector2.Distance(Flat(start), Flat(destination)))) + ClimbExtraYards;
        if (!IsUsablePath(climb, options.MaxPathPoints, maxClimbLength) || !(Reaches(climb) || EndsAt(climb, destination)))
            return false;
        route = Bounded(climb.Points, options, complete: true);
        return route is not null;
    }

    /// <summary>
    /// Whether the mesh finds walkable (not steep) ground at <paramref name="point"/>: the path from it to itself is not refused (Detour
    /// locates no polygon there otherwise). One more query on a refused plan; a plan that succeeds never makes it.
    /// </summary>
    internal static bool OnWalkableMesh(Map map, Vector3 point)
    {
        PathResult res = PlayerbotThinkProbe.Find(map.Collision, point, point, new PathOptions { MaxPoints = 4, Mover = PathMover.Player,
            ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = 64 });
        return (res.Type & PathType.NoPath) == 0
            && (res.Points.Count == 0 || MathF.Abs(res.End.Z - point.Z) <= NavMeshPathfinder.NearExtents.Y);
    }

    /// <summary>
    /// The search budget of one query: vmangos' 2048 nodes (<see cref="PathOptions.DefaultMaxSearchNodes"/>) within
    /// <see cref="NearYards"/>, else 48 nodes a yard of straight distance, at most <see cref="MaxSearchNodes"/>. The way down from the
    /// Red Cloud Mesa to Bloodhoof Village (405 yards apart, a 1,589-yard road) needs more than 8,192 nodes, the way from Stormwind to
    /// Northshire Abbey (630 yards apart) more than 8,192 and at most 32,768.
    /// </summary>
    internal static int SearchNodes(Vector3 start, Vector3 destination)
    {
        float distance = Vector3.Distance(start, destination);
        return distance <= NearYards || !float.IsFinite(distance) ? PathOptions.DefaultMaxSearchNodes
            : (int)Math.Clamp(distance * 48f, PathOptions.DefaultMaxSearchNodes, MaxSearchNodes);
    }

    /// <summary>Within this straight distance a query keeps vmangos' search budget (<see cref="SearchNodes"/>).</summary>
    internal const float NearYards = 100f;

    /// <summary>The largest search budget of one bot route query (<see cref="SearchNodes"/>).</summary>
    internal const int MaxSearchNodes = PlayerbotOptions.MaxRouteSearchNodes;

    /// <summary>
    /// Follow <paramref name="route"/> this think (<see cref="PlayerbotMotion.Follow"/>): start, switch or keep moving.
    /// The motion itself advances every world tick, so the distance does not depend on <paramref name="elapsedMs"/>;
    /// <paramref name="serverTimeMs"/> is the current server time. False when the route is finished or cannot be
    /// followed (the caller drops it).
    /// </summary>
    internal static bool TryAdvance(WorldSession session, PlayerbotRoute route, PlayerbotOptions options,
        uint elapsedMs, uint serverTimeMs, bool keepMotion = false)
    {
        if (session.Player is not { } player)
            return false;
        if (player.Map is not { } map || options.MoveSpeed <= 0 || elapsedMs == 0)
            return false;
        if (route.Complete || route.IsStaleFor(map))
        {
            PlayerbotMovementControl.Stop(session, player);
            return false;
        }
        // A stun, fear or confusion pauses the walk: the route is kept, nothing is sent and nothing is refused.
        if (PlayerbotMotion.ControlLost(player)) return true;
        if (player.StandState != StandState.Stand)
            return session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u));
        if (!PlayerbotMapPolicy.MayMoveOn(player, options)) return false;
        // A hazard that turned up since the route was planned (a creature that kills outright came into sight): give the route up
        // here, before walking into it; the goal plans again, round it or elsewhere.
        if (Guards.TryGetValue(player, out PlayerbotRisk? risk))
        {
            List<Vector3> rest = [PlayerbotMotion.CurrentPosition(player), .. route.Points.Skip(route.NextPoint)];
            if (risk.Blocking(player, rest, strong: !risk.IsEscape(route),
                completedReturn: route.CompletedQuestReturn,
                crossBesidePlaceMarks: route.CrossBesidePlaceMarks,
                chase: route.Chase) is not null)
            {
                PlayerbotMovementControl.Stop(session, player);
                return false;
            }
        }

        // A long walk is ridden when the bot has a mount (PlayerbotMount): it stops to cast the mount, then walks on mounted.
        if (PlayerbotMount.BeforeStep(session, player, route, options)) return true;
        return PlayerbotMotion.Follow(session, player, route, options, serverTimeMs, keepMotion);
    }

    /// <summary>
    /// The next leg of a walk, planned from the end of the route the bot is moving along now and joined to what is left of it
    /// (<see cref="PlayerbotRoute.Then"/>), so a think that expects to be the last before the bot arrives can hand the motion its
    /// next leg and the bot does not stand until the following think. One path query to <paramref name="destination"/>, the very one the
    /// next think would make from the end of the route; none of the fallbacks of <see cref="PlanToward"/> (the climb, the steep escape,
    /// the starts beside the bot, the hazard detours): a leg that needs one is planned by the next think as before. False (no change)
    /// when the query is not a usable route, waits for a tile, or the leg crosses a hazard.
    /// </summary>
    internal static bool TryChain(Player player, PlayerbotRoute current, Vector3 destination, PlayerbotOptions options,
        out PlayerbotRoute? chained)
    {
        chained = null;
        if (current.Complete || current.Points.Count < 2 || !PlayerbotMotion.IsActive(player) || !destination.IsFinite()) return false;
        Vector3 here = PlayerbotMotion.CurrentPosition(player);
        if (!PlanDirect(player, destination, destination, options, partial: true, out PlayerbotRoute? leg, out _, null, current.Points[^1])
            || leg is null
            || (leg.Navigated && Vector2.Distance(Flat(leg.Points[^1]), Flat(destination)) > WanderMaxShortfallYards)) return false;
        if (Guards.TryGetValue(player, out PlayerbotRisk? risk) && risk.Blocking(player, leg.Points, completedReturn: false) is not null)
            return false;
        chained = current.Then(here, leg);
        return true;
    }

    /// <summary>Propose a step without changing the route; the caller commits after normal admission.</summary>
    internal static bool TryStep(PlayerbotRoute route, Vector3 start, float budget,
        Func<Vector3, Vector3?> validate, out Vector3 next, out int nextPoint)
    {
        next = start;
        nextPoint = route.NextPoint;
        if (!start.IsFinite() || !float.IsFinite(budget) || budget <= 0 || route.Complete
            || nextPoint < 1) return false;
        float remainingBudget = budget;
        int scanned = 0;
        while (nextPoint < route.Points.Count && remainingBudget > 0.001f && ++scanned <= 64)
        {
            Vector3 destination = route.Points[nextPoint];
            float remaining = Vector3.Distance(next, destination);
            if (!destination.IsFinite() || !float.IsFinite(remaining)) break;
            if (remaining <= 0.05f) { nextPoint++; continue; }
            bool reachesPoint = remaining <= remainingBudget;
            Vector3 candidate = reachesPoint ? destination
                : next + Vector3.Normalize(destination - next) * remainingBudget;
            Vector3? checkedPoint = validate(candidate);
            if (checkedPoint is not { } accepted || !accepted.IsFinite()) break;
            float spent = Vector3.Distance(next, accepted);
            if (!float.IsFinite(spent) || spent > remainingBudget + 0.05f) break;
            next = accepted;
            remainingBudget = MathF.Max(0, remainingBudget - spent);
            if (reachesPoint && Vector3.Distance(accepted, destination) <= 0.1f) nextPoint++;
            else break;
        }
        return Vector3.Distance(start, next) > 0.05f;
    }

    internal static bool TryStepOnMap(PlayerbotRoute route, Vector3 start, float budget, Map map,
        out Vector3 next, out int nextPoint)
        => TryStep(route, start, budget, candidate =>
        {
            float floor = map.Collision.GetHeight(candidate.X, candidate.Y, candidate.Z);
            if (!candidate.IsFinite() || InvalidHeight(floor) || MathF.Abs(floor - candidate.Z) > 2f
                || !PlayerbotMoveCheck.LineClear(map.Collision, start, candidate with { Z = floor })) return null;
            return new Vector3(candidate.X, candidate.Y, floor + 0.05f);
        }, out next, out nextPoint);

    internal static bool TryTerrainRoute(Vector3 start, Vector3 destination, PlayerbotOptions options,
        Func<float, float, float, float> getHeight, Func<Vector3, Vector3, bool> clear,
        out PlayerbotRoute? route)
    {
        route = null;
        if (!start.IsFinite() || !destination.IsFinite()) return false;
        float horizontal = Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(destination.X, destination.Y));
        if (!float.IsFinite(horizontal) || horizontal < 0.05f || horizontal > options.MaxRouteYards) return false;
        int steps = (int)MathF.Ceiling(horizontal); // at most one yard between floor probes
        if (steps + 1 > options.MaxPathPoints) return false;
        var points = new List<Vector3>(steps + 1) { start };
        float length = 0;
        for (int i = 1; i <= steps; i++)
        {
            Vector3 previous = points[^1];
            Vector3 next = Vector3.Lerp(start, destination, (float)i / steps);
            float floor = getHeight(next.X, next.Y, previous.Z);
            if (InvalidHeight(floor) || MathF.Abs(floor - previous.Z) > 1f) return false;
            next.Z = floor + 0.05f;
            if (!clear(previous, next)) return false;

            length += Vector3.Distance(previous, next);
            if (!float.IsFinite(length) || length > options.MaxRouteYards) return false;
            points.Add(next);
        }
        route = new PlayerbotRoute(points, length);
        return true;
    }

    internal static byte[] GuidPayload(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    internal static byte[] UseItemPayload(byte bag, byte slot)
        => UseItemPayload(bag, slot, ArcaneCore.Game.Spells.SpellCastTargets.ForSelf());

    internal static byte[] UseItemPayload(byte bag, byte slot, ArcaneCore.Game.Spells.SpellCastTargets targets)
    {
        var writer = new PacketWriter(16);
        writer.WriteByte(bag);
        writer.WriteByte(slot);
        writer.WriteByte(0);
        targets.Write(writer);
        return writer.ToArray();
    }

    internal static byte[] UseItemOnUnitPayload(byte bag, byte slot, ObjectGuid unit, byte spellIndex = 0)
    {
        var writer = new PacketWriter(16);
        writer.WriteByte(bag);
        writer.WriteByte(slot);
        writer.WriteByte(spellIndex);
        var targets = ArcaneCore.Game.Spells.SpellCastTargets.ForUnit(unit);
        writer.WriteUInt16((ushort)targets.Mask);
        writer.WritePackedGuid(unit.Value);
        return writer.ToArray();
    }

    internal static bool InvalidHeight(float height)
        => !float.IsFinite(height) || height == TerrainTile.InvalidHeight
            || height == TerrainTile.InvalidHeightValue;
}
