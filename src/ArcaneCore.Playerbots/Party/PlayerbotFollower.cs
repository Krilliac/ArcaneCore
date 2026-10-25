using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// One bot following one leader (docs/areas/playbots.md, Following and catch-up): it walks to a point beside the leader, and when
/// it cannot simply walk it catches up by <see cref="PlayerbotFollowOptions.CatchUpMode"/>: a teleport (the reference behaviour,
/// vmangos PartyBotAI and mangoszero's playerbot), a drop to a leader below, or fighting through. The party AI drives it for a
/// bot in a real player's group; any other follow of a managed bot can drive it the same way. World thread.
/// <para>
/// Each think (<see cref="Update"/>) first takes the cheap decision (<see cref="PlayerbotParty.DecideFollow"/>: hold, wait, the
/// distance or other-map teleport, the entrance walk). A follower that must walk plans a route to the leader and judges it
/// (<see cref="PlayerbotFollowRules.Judge"/>) whenever it plans afresh: the route's length against the straight distance, the
/// hostiles far above its level whose aggro reaches it, the vertical rule, and a drop (<see cref="PlayerbotFollowDrop"/>). Every
/// think it also measures its progress (<see cref="PlayerbotFollowProgress"/>): no progress towards a leader standing still for
/// <see cref="PlayerbotFollowOptions.NoProgressSeconds"/> re-plans, twice gives up walking.
/// </para>
/// <para>
/// The caller acknowledges the server's orders every world tick before it thinks (<see cref="PlayerbotMovementControl.Update"/>;
/// <see cref="PlayerbotMotion.Pump"/> acknowledges a far transfer): a catch-up teleport is the ordinary teleport service's
/// (the GM <c>.goname</c> path, with its instance and map checks), which a server-owned session completes only by those
/// acknowledgements. It is never made while one is pending, nor by a session that is not server-owned.
/// </para>
/// </summary>
internal sealed class PlayerbotFollower
{
    /// <summary>A refused teleport (a full or locked instance) is tried again after this long.</summary>
    internal const long TeleportRetryMs = 10_000;

    /// <summary>The route is planned again when the point beside the leader moved more than this many yards.</summary>
    internal const float ReplanYards = 2f;

    /// <summary>A drop is given up when the leader moved this far from where it was planned.</summary>
    internal const float DropLeaderMovedYards = 5f;

    /// <summary>A follower that found no way to walk (and did not teleport) plans and judges again only after this long.</summary>
    internal const long JudgeAgainMs = 1_000;

    /// <summary>A drop searched for in vain is searched for again only after this long (the search probes the collision data).</summary>
    internal const long DropSearchAgainMs = 2_000;

    /// <summary>FightThrough: a blocker the bot failed to reach this many times is no longer engaged.</summary>
    internal const int MaxBlockerApproaches = 3;

    /// <summary>FightThrough: hostiles whose aggro reaches this many yards of the way ahead are engaged.</summary>
    internal const float BlockerLookAheadYards = 40f;

    private enum DropPhase { None, ToEdge, Falling }

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotFollowProgress _progress = new();
    private readonly PlayerbotFollowWatch _watch = new();
    private readonly ILogger _logger;
    private PlayerbotRoute? _routeSlot;

    /// <summary>The route this follower walks. Letting go of one (null or another route) marks it released
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.</summary>
    private PlayerbotRoute? _route
    {
        get => _routeSlot;
        set
        {
            if (ReferenceEquals(_routeSlot, value)) return;
            if (_routeSlot is { } old) old.Released = true;
            _routeSlot = value;
        }
    }
    private Vector3 _routeGoal;
    private PlayerbotFollowStep _step = PlayerbotFollowStep.Walk;
    private PlayerbotDropPlan? _dropSlot;

    /// <summary>The follower's drop plan. Letting go of one (null or another plan) releases its walk to the edge
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.
    /// Assign a <c>with</c> copy of the plan only when its ToEdge route is replaced too: the copy is a different object, so the assignment
    /// releases the old plan's ToEdge even if the new plan still holds it.</summary>
    private PlayerbotDropPlan? _drop
    {
        get => _dropSlot;
        set
        {
            if (ReferenceEquals(_dropSlot, value)) return;
            if (_dropSlot is { ToEdge: { } old }) old.Released = true;
            _dropSlot = value;
        }
    }
    private DropPhase _phase;
    private Vector3 _dropLeader;
    private uint _fallStartMs;
    private float _fallHeading;
    private long _teleportRetryAtMs;
    private long _judgeAgainAtMs;
    private long _dropSearchAgainAtMs;
    private bool _triggersAllowed;
    private Map? _fallMap;
    private bool _routeStale;
    private readonly Dictionary<ObjectGuid, int> _unreachableBlockers = [];

    internal PlayerbotFollower(WorldSession session, PlayerbotOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotFollower>() ?? NullLogger<PlayerbotFollower>.Instance;
    }

    /// <summary>Catch-up teleports made (inspection and tests).</summary>
    internal int Teleports { get; private set; }

    /// <summary>Drops taken (inspection and tests).</summary>
    internal int Drops { get; private set; }

    /// <summary>Stalls counted, each of which re-planned (inspection and tests).</summary>
    internal int Replans { get; private set; }

    /// <summary>Why the follower last gave up walking (a teleport or a drop), or <see cref="PlayerbotCatchUpReason.None"/>.</summary>
    internal PlayerbotCatchUpReason LastCatchUp { get; private set; }

    /// <summary>The last judgement of a planned route (inspection and tests).</summary>
    internal (PlayerbotFollowStep Step, PlayerbotCatchUpReason Reason, float RouteYards, float Straight, int Dangers) LastJudgement { get; private set; }

    /// <summary>Whether the follower is in the air of a drop: its thinks belong to the fall until it lands.</summary>
    internal bool Airborne => _phase == DropPhase.Falling;

    /// <summary>Whether the follower walks its route, or to the edge of a drop, now.</summary>
    internal bool Walking => _route is not null || _phase == DropPhase.ToEdge;

    /// <summary>Test seam (never configuration-bound): milliseconds added to the follower's clock, so a test lets minutes pass between thinks.</summary>
    internal long ClockOffsetMs { get; set; }

    private long Now => (long)_session.World.Uptime.TotalMilliseconds + ClockOffsetMs;

    private TeleportService? TeleportServiceOf => _session.Services.GetService<TeleportFeature>()?.Teleports;

    /// <summary>Forget the route, the drop and the progress (the follower stopped following, or something else moved it).</summary>
    internal void Reset(Player player)
    {
        // A fall under way always ends on the ground: land it now (where the bot can still land) before forgetting it.
        if (_phase == DropPhase.Falling) FinishFall(player);
        _route = null;
        _drop = null;
        _phase = DropPhase.None;
        _fallMap = null;
        _routeStale = false;
        _unreachableBlockers.Clear();
        _step = PlayerbotFollowStep.Walk;
        _progress.Reset();
        _watch.Reset();
        ReleaseTriggers(player);
    }

    /// <summary>
    /// Something else moves the bot for a while (the party AI's fight): a fall under way lands, a walk to a drop's edge is given up,
    /// and the route is planned afresh when the follow resumes (the bot will stand elsewhere). The route is kept until then: it is
    /// the way FightThrough looks along for blockers (<see cref="Blocker"/>).
    /// </summary>
    internal void Suspend(Player player)
    {
        if (_phase == DropPhase.Falling) FinishFall(player);
        CancelDrop();
        _routeStale = true;
        // The route is kept for Blocker, but nothing may keep walking it: the driver's settle stops a walk that still follows it.
        if (_route is { } kept) kept.Released = true;
    }

    /// <summary>
    /// One think of following <paramref name="leader"/> (<paramref name="mode"/> is never Stay: a staying bot is not following), at
    /// <paramref name="followDistance"/> yards and <paramref name="followAngle"/> radians off the leader's facing. Returns the
    /// decision taken before any route (<see cref="PlayerbotParty.DecideFollow"/>).
    /// </summary>
    internal PlayerbotFollowAction Update(Player player, Player leader, PlayerbotPartyMode mode, float followAngle, float followDistance, uint interval)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(leader);
        if (Airborne)
        {
            ContinueFall(player);
            return PlayerbotFollowAction.Move;
        }

        PlayerbotFollowFacts facts = Facts(player, leader, mode);
        PlayerbotFollowAction action = PlayerbotParty.DecideFollow(facts);
        _watch.Observe(player, leader, facts, action, Now); // outcome checks: far behind / on another map for too long
        if (action != PlayerbotFollowAction.WalkToEntrance) ReleaseTriggers(player);
        switch (action)
        {
            case PlayerbotFollowAction.Hold:
            case PlayerbotFollowAction.Wait:
                _route = null;
                CancelDrop();
                _progress.Reset();
                PlayerbotMovementControl.Stop(_session, player);
                break;
            case PlayerbotFollowAction.Teleport:
                Teleport(player, leader, facts.SameMap ? PlayerbotCatchUpReason.Distance : PlayerbotCatchUpReason.OtherMap, null);
                break;
            case PlayerbotFollowAction.WalkToEntrance:
                WalkToEntrance(player, leader, interval);
                break;
            case PlayerbotFollowAction.Move:
                Follow(player, leader, facts, followAngle, followDistance, interval);
                break;
        }

        return action;
    }

    /// <summary>The facts of <see cref="PlayerbotParty.DecideFollow"/> for this follower and leader now.</summary>
    internal PlayerbotFollowFacts Facts(Player player, Player leader, PlayerbotPartyMode mode)
    {
        TeleportService? teleports = TeleportServiceOf;
        Map? leaderMap = leader.Map;
        bool sameMap = leaderMap is not null && ReferenceEquals(player.Map, leaderMap);
        bool otherInstance = !sameMap && leaderMap is not null && player.Map is { } own && own.MapId == leaderMap.MapId;
        bool leaderInWorld = leader.IsInWorld && leaderMap is not null && teleports?.StageOf(leader) is null;
        PlayerbotFollowOptions follow = _options.Follow;
        bool entrance = !sameMap && follow.CatchUpMode == PlayerbotCatchUpMode.FightThrough && player.Map is not null
            && PlayerbotGroupCoordinator.EntranceTo(WorldMaps.Of(_session.World), player.MapId, leader.MapId, Position(player)) is not null;
        return new PlayerbotFollowFacts(mode, leaderInWorld, sameMap, sameMap ? Distance(player, leader) : float.NaN,
            _options.Party.TeleportToLeader && _session.IsManaged,
            PlayerbotMapPolicy.Allows(_options.AllowedMaps, leader.MapId, leaderMap?.Template, current: true),
            _session.Services.GetService<NpcServicesFeature>()?.Flights?.IsFlying(leader) == true, player.Combat.IsInCombat, otherInstance,
            follow.CatchUpMode, follow.TeleportDistanceYards, leader.Combat.IsInCombat, follow.LeaderCombatRangeYards, entrance);
    }

    /// <summary>
    /// FightThrough: the nearest living hostile whose aggro reaches the next <see cref="BlockerLookAheadYards"/> of the way, which the
    /// follower engages instead of running past (or null). Other modes, and a follower not walking, have none.
    /// </summary>
    internal Creature? Blocker(Player player)
    {
        if (_options.Follow.CatchUpMode != PlayerbotCatchUpMode.FightThrough || _route is not { } route || route.Complete) return null;
        List<Vector3> ahead = Ahead(player, route, BlockerLookAheadYards);
        Creature? nearest = null;
        float best = float.PositiveInfinity;
        foreach (PlayerbotThreat threat in PlayerbotRecovery.Threats(player))
        {
            if (threat.Source is not { } creature || !Covers(threat, ahead)
                || _unreachableBlockers.GetValueOrDefault(creature.Guid) >= MaxBlockerApproaches) continue;
            float distance = Distance(player, creature);
            if (distance < best)
            {
                best = distance;
                nearest = creature;
            }
        }

        return nearest;
    }

    // --- walking ------------------------------------------------------------------------------------------------------

    private void Follow(Player player, Player leader, in PlayerbotFollowFacts facts, float followAngle, float followDistance, uint interval)
    {
        Vector3 leaderAt = Position(leader);
        if (_phase == DropPhase.ToEdge)
        {
            if (Vector3.Distance(leaderAt, _dropLeader) <= DropLeaderMovedYards)
            {
                WalkToEdge(player, interval);
                return;
            }

            CancelDrop(); // the leader moved on: judge afresh
        }

        Vector3 goal = PlayerbotParty.FollowPoint(leaderAt, leader.Orientation, followAngle, followDistance);
        bool judge = false;
        if (_routeStale)
        {
            _route = null;
            _routeStale = false;
        }

        if (_route is null && Now < _judgeAgainAtMs)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        if (_route is null || _route.Complete || Vector3.Distance(_routeGoal, goal) > ReplanYards)
        {
            // A leader that moved on: the way planned a moment ago is still the best one while the new one waits for a tile or a path job.
            bool movedGoal = _route is { Complete: false };
            if (Plan(player, goal, leaderAt, keepRouteWhilePending: movedGoal))
            {
                if (_route is null)
                {
                    HoldForTiles(player, true);
                    return;
                }
            }
            else
            {
                judge = true;
            }
        }

        float remaining = _route is { } walking ? Remaining(player, walking) : Distance(player, leader);
        if (_progress.Observe(Now, remaining, leaderAt, _options.Follow.NoProgressSeconds))
        {
            // No progress towards a leader standing still: plan again from where the bot stands (the first time), or judge the way
            // anew with the stalls counted (the second: Teleport and Auto give up walking).
            PlayerbotMotion.Forgive(player, leaderAt);
            PlayerbotMovementControl.Stop(_session, player);
            Replans++;
            if (HoldForTiles(player, Plan(player, goal, leaderAt))) return;
            judge = true;
        }

        if (judge)
        {
            (PlayerbotFollowStep step, PlayerbotCatchUpReason reason) = Judge(player, leader, facts);
            _step = step;
            switch (step)
            {
                case PlayerbotFollowStep.Teleport:
                    if (!Teleport(player, leader, reason, LastJudgement)) _judgeAgainAtMs = Now + JudgeAgainMs;
                    return;
                case PlayerbotFollowStep.Drop:
                    BeginDrop(player, leaderAt, reason);
                    return;
                case PlayerbotFollowStep.Wait:
                    _route = null;
                    _judgeAgainAtMs = Now + JudgeAgainMs;
                    PlayerbotMovementControl.Stop(_session, player);
                    return;
            }
        }

        if (_step != PlayerbotFollowStep.Walk || _route is null)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, interval, _session.World.NowMs)) _route = null;
    }

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of the follower's plans.</summary>
    private TileWait _tileWait;

    /// <summary>
    /// Plan to the point beside the leader, else to the leader itself; FightThrough and Auto also take a partial way towards it. True when
    /// a tile on the way is still being read: the way is not judged (a refusal would read as no route and teleport the follower to its
    /// leader), the caller holds and plans again (for <see cref="TileWait.MaxMs"/>, then the refusal stands).
    /// </summary>
    /// <remarks>
    /// With <paramref name="keepRouteWhilePending"/> a pending answer leaves the route being walked (and the goal it was planned for) in
    /// place and still returns true: the next think asks again, since the goal is still beyond <see cref="ReplanYards"/> of the old one,
    /// and collects the answer then. Without it, or when the kept route is complete or stale, a pending answer clears the route.
    /// </remarks>
    private bool Plan(Player player, Vector3 goal, Vector3 leaderAt, bool keepRouteWhilePending = false)
    {
        PlayerbotRoute? kept = _route;
        Vector3 keptGoal = _routeGoal;
        _routeGoal = goal;
        _route = null;
        bool Pending()
        {
            if (keepRouteWhilePending && kept is { Complete: false } && !kept.IsStaleFor(player.Map))
            {
                _route = kept;
                _routeGoal = keptGoal;
            }

            return true;
        }

        PlanOutcome beside = PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? route);
        if (beside == PlanOutcome.Pending) return Pending();
        PlanOutcome onLeader = beside == PlanOutcome.Route ? beside
            : PlayerbotNavigation.PlanExact(player, leaderAt, _options, ref _tileWait, _session.World.NowMs, out route);
        if (onLeader == PlanOutcome.Pending) return Pending();
        if (onLeader == PlanOutcome.Route)
        {
            _route = route;
            return false;
        }

        PlanOutcome towardGoal = PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, _session.World.NowMs, out route);
        if (towardGoal == PlanOutcome.Pending) return Pending();
        if (towardGoal == PlanOutcome.Route)
        {
            _route = route;
            return false;
        }

        PlanOutcome towardLeader = PlayerbotNavigation.PlanToward(player, leaderAt, _options, ref _tileWait, _session.World.NowMs, out route);
        if (towardLeader == PlanOutcome.Pending) return Pending();
        if (towardLeader == PlanOutcome.Route)
        {
            _route = route;
            return false;
        }

        if (player.Map is { } map && Vector3.Distance(Position(player), leaderAt) <= 35f && MathF.Abs(player.Z - leaderAt.Z) < 6f)
        {
            if (PlayerbotMoveCheck.TerrainRoute(map, Position(player), goal, _options, out PlayerbotRoute? terrain)
                || PlayerbotMoveCheck.TerrainRoute(map, Position(player), leaderAt, _options, out terrain))
            {
                _route = terrain;
            }
        }

        return false;
    }

    /// <summary>Stand and plan again after <see cref="JudgeAgainMs"/> when <paramref name="pending"/> (<see cref="Plan"/>).</summary>
    private bool HoldForTiles(Player player, bool pending)
    {
        if (!pending) return false;
        _judgeAgainAtMs = Now + JudgeAgainMs;
        PlayerbotMovementControl.Stop(_session, player);
        return true;
    }

    private (PlayerbotFollowStep, PlayerbotCatchUpReason) Judge(Player player, Player leader, in PlayerbotFollowFacts facts)
    {
        PlayerbotFollowOptions follow = _options.Follow;
        _drop = null;
        Vector3 here = Position(player), leaderAt = Position(leader);
        float straight = Vector3.Distance(here, leaderAt);
        float across = Vector2.Distance(new(here.X, here.Y), new(leaderAt.X, leaderAt.Y));
        bool found = _route is not null;
        float routeYards = _route?.Distance ?? float.NaN;
        int dangers = found ? Dangers(player, _route!, leaderAt) : 0;
        bool mayTeleport = PlayerbotParty.MayTeleport(facts);
        var routeFacts = new PlayerbotRouteFacts(follow.CatchUpMode, mayTeleport, straight, across, here.Z - leaderAt.Z, found, routeYards,
            dangers, DropAvailable: false, _progress.Stalls);
        (PlayerbotFollowStep step, PlayerbotCatchUpReason reason) = PlayerbotFollowRules.Judge(routeFacts, follow);
        if (reason != PlayerbotCatchUpReason.None && player.Map is { } map && Now >= _dropSearchAgainAtMs)
        {
            if (PlayerbotFollowDrop.Find(map, here, leaderAt, player.Health, player.MaxHealth, _options) is { } drop)
            {
                _drop = drop;
                (step, reason) = PlayerbotFollowRules.Judge(routeFacts with { DropAvailable = true }, follow);
            }
            else
            {
                _dropSearchAgainAtMs = Now + DropSearchAgainMs;
            }
        }

        // Do not teleport straight to the leader over short distances when a walkable route exists and has not stalled.
        if (step == PlayerbotFollowStep.Teleport
            && follow.CatchUpMode != PlayerbotCatchUpMode.Teleport
            && _progress.Stalls < PlayerbotFollowRules.StallsToTeleport)
        {
            if (reason == PlayerbotCatchUpReason.Detour && found && dangers == 0 && routeYards <= 160f)
            {
                step = PlayerbotFollowStep.Walk;
            }
            else if (reason == PlayerbotCatchUpReason.Danger && found && straight <= 45f && routeYards <= 160f && MathF.Abs(here.Z - leaderAt.Z) < 10f)
            {
                step = PlayerbotFollowStep.Walk;
            }
            else if (reason == PlayerbotCatchUpReason.NoRoute && straight <= 45f && !PlayerbotFollowRules.IsVertical(across, here.Z - leaderAt.Z) && _progress.Stalls < 1)
            {
                step = PlayerbotFollowStep.Wait;
            }
        }

        // In FightThrough or Auto, a follower with no route at all still closes on the leader by a partial way when there is one.
        if (step == PlayerbotFollowStep.Wait && follow.CatchUpMode != PlayerbotCatchUpMode.Teleport
            && PlayerbotNavigation.PlanToward(player, leaderAt, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? toward) == PlanOutcome.Route)
        {
            _route = toward;
            step = PlayerbotFollowStep.Walk;
        }

        LastJudgement = (step, reason, routeYards, straight, dangers);
        return (step, reason);
    }

    /// <summary>
    /// The hostiles whose aggro reaches <paramref name="route"/> and that are far above the follower: <see cref="PlayerbotFollowOptions.DangerLevelMargin"/>
    /// levels or more above it, or elite (vmangos <c>CREATURE_ELITE_*</c>, rank 1 to 3) at or above its level. One whose aggro also
    /// reaches the leader where it stands does not count: the follower meets it beside its leader whichever way it comes (a
    /// teleport there would only bring it into the same fight sooner, and again at every step the leader takes past it).
    /// </summary>
    private int Dangers(Player player, PlayerbotRoute route, Vector3 leaderAt)
    {
        int margin = _options.Follow.DangerLevelMargin;
        int count = 0;
        foreach (PlayerbotThreat threat in PlayerbotRecovery.Threats(player))
        {
            if (threat.Source is not { } creature) continue;
            bool elite = creature.Template.Rank is 1 or 2 or 3;
            bool far = creature.Level >= player.Level + margin || (elite && creature.Level >= player.Level);
            if (far && !threat.Reaches(leaderAt, 0f) && Covers(threat, route.Points)) count++;
        }

        return count;
    }

    // --- drop ---------------------------------------------------------------------------------------------------------

    private void BeginDrop(Player player, Vector3 leaderAt, PlayerbotCatchUpReason reason)
    {
        if (_drop is not { } drop)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        _route = null;
        _phase = DropPhase.ToEdge;
        _dropLeader = leaderAt;
        LastCatchUp = reason;
        _logger.LogInformation("Playerbot {Bot} drops {Height:F1} yards from ({EdgeX:F1}, {EdgeY:F1}, {EdgeZ:F1}) to its leader below ({Reason}; fall damage {Damage})",
            player.Name, drop.Height, drop.Edge.X, drop.Edge.Y, drop.Edge.Z, Describe(reason), drop.Damage);
        WalkToEdge(player, 0);
    }

    private void WalkToEdge(Player player, uint interval)
    {
        PlayerbotDropPlan drop = _drop!;
        Vector3 here = PlayerbotMotion.CurrentPosition(player);
        if (drop.ToEdge is { Complete: false } toEdge && Vector2.Distance(new(here.X, here.Y), new(drop.Edge.X, drop.Edge.Y)) > 0.5f)
        {
            if (PlayerbotNavigation.TryAdvance(_session, toEdge, _options, Math.Max(1u, interval), _session.World.NowMs)) return;
            if (Vector2.Distance(new(here.X, here.Y), new(drop.Edge.X, drop.Edge.Y)) > 1.5f)
            {
                CancelDrop(); // the edge cannot be reached after all: judge afresh next think
                return;
            }
        }

        // At the edge: stop, then step over it and fall.
        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        float heading = MathF.Atan2(drop.StepOff.Y - drop.Edge.Y, drop.StepOff.X - drop.Edge.X);
        if (!PlayerbotMotion.StartFall(_session, player, drop.StepOff, heading))
        {
            CancelDrop();
            return;
        }

        _phase = DropPhase.Falling;
        _fallMap = player.Map;
        _fallStartMs = _session.World.NowMs;
        _fallHeading = heading;
    }

    /// <summary>In the air: land once the fall's time has passed (MSG_MOVE_FALL_LAND; the server applies the fall damage).</summary>
    internal void ContinueFall(Player player)
    {
        if (_drop is not { } drop || _phase != DropPhase.Falling) return;
        if (Landable(player) && unchecked(_session.World.NowMs - _fallStartMs) < drop.FallTimeMs) return;
        FinishFall(player);
    }

    /// <summary>
    /// End the fall: land at the drop's floor when the bot can still land there (alive, in the world, on the map instance the fall
    /// began on, no teleport pending), reporting the whole fall time; otherwise (it died, or was teleported away mid-air) the fall
    /// is only forgotten, and the server's own position stands.
    /// </summary>
    private void FinishFall(Player player)
    {
        PlayerbotDropPlan? drop = _drop;
        bool land = drop is not null && Landable(player);
        _phase = DropPhase.None;
        _drop = null;
        _fallMap = null;
        _route = null;
        _progress.Reset();
        if (land && PlayerbotMotion.Land(_session, player, drop!.Landing with { Z = drop.Landing.Z + 0.05f }, _fallHeading, drop.FallTimeMs))
            Drops++;
    }

    private bool Landable(Player player)
        => player.IsAlive && player.IsInWorld && _fallMap is not null && ReferenceEquals(player.Map, _fallMap)
            && TeleportServiceOf?.IsBeingTeleported(player) != true;

    /// <summary>
    /// FightThrough: the party AI could not walk to <paramref name="blocker"/> (no route): after <see cref="MaxBlockerApproaches"/> such
    /// approaches it is no longer engaged, so the follower walks on instead of standing before it for good.
    /// </summary>
    internal void NoteUnreachable(Creature blocker)
    {
        ArgumentNullException.ThrowIfNull(blocker);
        if (_unreachableBlockers.Count >= 64) _unreachableBlockers.Clear();
        _unreachableBlockers[blocker.Guid] = _unreachableBlockers.GetValueOrDefault(blocker.Guid) + 1;
    }

    /// <summary>
    /// Test seam (never configuration-bound): take <paramref name="plan"/> as the drop judged for a leader at <paramref name="leaderAt"/>,
    /// the bot standing at its edge, so its fall starts now. True when the fall started.
    /// </summary>
    internal bool StartDropForTests(Player player, PlayerbotDropPlan plan, Vector3 leaderAt)
    {
        _drop = plan;
        BeginDrop(player, leaderAt, PlayerbotCatchUpReason.Detour);
        return Airborne;
    }

    private void CancelDrop()
    {
        if (_phase == DropPhase.Falling) return; // a fall always lands
        _drop = null;
        _phase = DropPhase.None;
    }

    // --- teleport and entrances --------------------------------------------------------------------------------------

    /// <summary>
    /// vmangos PartyBotAI UpdateAI :806-817 (<c>ChatHandler(me).HandleGonameCommand(leader)</c>): the ordinary teleport service, so
    /// the map resolver puts the bot into the leader's instance (the group's bind) or refuses it as it would refuse the GM command.
    /// The bot lands on the leader's spot, not 5 yards above as .goname puts a GM: it has no client to fall. A leader in the air is
    /// waited for (the bot would land in the air).
    /// </summary>
    private bool Teleport(Player player, Player leader, PlayerbotCatchUpReason reason,
        (PlayerbotFollowStep Step, PlayerbotCatchUpReason Reason, float RouteYards, float Straight, int Dangers)? judged)
    {
        _route = null;
        CancelDrop();
        TeleportService? teleports = TeleportServiceOf;
        if (teleports is null || !_session.IsManaged || teleports.IsBeingTeleported(player) || Now < _teleportRetryAtMs) return false;
        PlayerbotMovementControl.Stop(_session, player);
        if ((leader.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return false;
        // vmangos PartyBotAI.cpp:808: no teleport to the leader while the bot duels (!IsInDuel()).
        if (player.Duel is not null) return false;
        // Within one map id the service teleports near and keeps the bot's own instance: that never reaches a leader in another one.
        if (player.Map is { } own && own.MapId == leader.MapId && !ReferenceEquals(own, leader.Map))
        {
            _teleportRetryAtMs = Now + TeleportRetryMs;
            return false;
        }

        Vector3 from = Position(player);
        if (!teleports.TeleportTo(player, leader.MapId, leader.X, leader.Y, leader.Z, leader.Orientation))
        {
            _teleportRetryAtMs = Now + TeleportRetryMs;
            return false;
        }

        Teleports++;
        LastCatchUp = reason;
        _progress.Reset();
        _watch.Reset();
        string detail = judged is { } j && float.IsFinite(j.RouteYards)
            ? string.Create(CultureInfo.InvariantCulture, $"; way {j.RouteYards:F1} yd for {j.Straight:F1} yd straight, {j.Dangers} dangerous hostiles")
            : string.Empty;
        _logger.LogInformation("Playerbot {Bot} teleports to its leader {Leader} ({Reason}{Detail}) from ({X:F1}, {Y:F1}, {Z:F1}) map {Map}",
            player.Name, leader.Name, Describe(reason), detail, from.X, from.Y, from.Z, player.MapId);
        return true;
    }

    /// <summary>FightThrough, the leader on another map: walk into the area trigger on the bot's map that leads there, taking it.</summary>
    private void WalkToEntrance(Player player, Player leader, uint interval)
    {
        if (PlayerbotGroupCoordinator.EntranceTo(WorldMaps.Of(_session.World), player.MapId, leader.MapId, Position(player)) is not { } entrance)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        // Only a trigger that leads to the leader's map may be taken: every other one keeps the ordinary AllowedMaps rule.
        PlayerbotAreaTriggers.AllowTeleportsTo(player, leader.MapId);
        _triggersAllowed = true;

        var goal = new Vector3(entrance.X, entrance.Y, entrance.Z);
        if (_route is null || _route.Complete || Vector3.Distance(_routeGoal, goal) > ReplanYards)
        {
            _routeGoal = goal;
            // Pending or refused alike: no route, planned again next think (nothing is set aside here).
            _route = PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? route) == PlanOutcome.Route ? route : null;
        }

        if (_route is null || !PlayerbotNavigation.TryAdvance(_session, _route, _options, interval, _session.World.NowMs)) _route = null;
    }

    private void ReleaseTriggers(Player player)
    {
        if (!_triggersAllowed) return;
        PlayerbotAreaTriggers.AllowTeleportsTo(player, null);
        _triggersAllowed = false;
    }

    // --- helpers ------------------------------------------------------------------------------------------------------

    private static string Describe(PlayerbotCatchUpReason reason) => reason switch
    {
        PlayerbotCatchUpReason.Distance => "leader too far",
        PlayerbotCatchUpReason.OtherMap => "leader on another map",
        PlayerbotCatchUpReason.Vertical => "leader far above or below",
        PlayerbotCatchUpReason.NoRoute => "no route",
        PlayerbotCatchUpReason.Detour => "route too long",
        PlayerbotCatchUpReason.Danger => "route past dangerous hostiles",
        PlayerbotCatchUpReason.Stalled => "no progress",
        _ => "none",
    };

    /// <summary>The length of the way left along <paramref name="route"/> from where the bot is now.</summary>
    private static float Remaining(Player player, PlayerbotRoute route)
    {
        Vector3 at = PlayerbotMotion.CurrentPosition(player);
        float length = 0;
        for (int index = Math.Max(1, route.NextPoint); index < route.Points.Count; index++)
        {
            length += Vector3.Distance(at, route.Points[index]);
            at = route.Points[index];
        }

        return length;
    }

    /// <summary>The way ahead of the bot along <paramref name="route"/>, at most <paramref name="yards"/> long.</summary>
    private static List<Vector3> Ahead(Player player, PlayerbotRoute route, float yards)
    {
        Vector3 at = PlayerbotMotion.CurrentPosition(player);
        var points = new List<Vector3> { at };
        float left = yards;
        for (int index = Math.Max(1, route.NextPoint); index < route.Points.Count && left > 0; index++)
        {
            Vector3 next = route.Points[index];
            float leg = Vector3.Distance(at, next);
            if (leg > left) next = at + ((next - at) * (left / leg));
            points.Add(next);
            left -= leg;
            at = next;
        }

        return points;
    }

    /// <summary>Whether the threat's aggro reaches any point of the polyline (sampled every 3 yards, as PlayerbotRisk does).</summary>
    private static bool Covers(PlayerbotThreat threat, IReadOnlyList<Vector3> points)
    {
        if (points.Count == 1) return threat.Reaches(points[0], 0f);
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1], b = points[index];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 3f));
            for (int step = 0; step <= steps; step++)
                if (threat.Reaches(Vector3.Lerp(a, b, (float)step / steps), 0f)) return true;
        }

        return false;
    }

    private static Vector3 Position(WorldObject unit) => new(unit.X, unit.Y, unit.Z);
}
