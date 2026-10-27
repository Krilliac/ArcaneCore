using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Outcome checks of the bots' navigation planning and destination picking: did the plan do what a plan should (a route that ends
/// where it was asked to, begins where the bot stands), did a goal that keeps being planned ever get closer, and did a destination
/// chosen make sense (right map, not an enemy's town). Every report goes through <see cref="PlayerbotAnomalies"/> (areas <c>nav</c>,
/// <c>destination</c>). All of it is failure-path work: a branch and a few float compares on success, no allocation, no formatting.
/// <para>
/// Thread affinity: world thread only. State is per bot (<see cref="ConditionalWeakTable{TKey, TValue}"/>, dies with the player).
/// Nothing here reports for a session in a chaos probe (<see cref="WorldSession.IsInChaosProbe"/>: the probe breaks things on purpose).
/// </para>
/// </summary>
internal static class PlayerbotNavigationAnomalies
{
    /// <summary>An exact (non-partial) route that was not cut by a bound ends this far (flat yards) from its destination: the mesh answered a spot that is not the one asked for.</summary>
    internal const float PathEndShortYards = 12f;

    /// <summary>A route begins this far (yards) from where the bot stands: it is a route for someone else's place.</summary>
    internal const float RouteStartOffYards = 15f;

    /// <summary>Plans toward one goal from one spot (within <see cref="SameOriginYards"/>) that all succeeded and still left the bot there.</summary>
    internal const int ReplanLoopPlans = 8;

    /// <summary>... over at least this long (ms): a bot rooted or stunned for a few seconds replans every think and is no loop.</summary>
    internal const uint ReplanLoopMinMs = 30_000;

    /// <summary>Consecutive refusals of one goal (no success between) before the goal counts as repeatedly unreachable.</summary>
    internal const int RepeatedRefusals = 5;

    /// <summary>Two goals within this distance (yards) are one goal.</summary>
    internal const float SameGoalYards = 2f;

    /// <summary>Two plan origins within this distance (yards) are one spot: the bot has not moved.</summary>
    internal const float SameOriginYards = 3f;

    /// <summary>One trip to one destination (every leg of it) that has run this long (ms) without arriving: 20 minutes, several times the longest walk across a continent.</summary>
    internal const uint TripBudgetMs = 1_200_000;

    private sealed class GoalWatch
    {
        internal bool Has;
        internal Vector3 Goal;
        internal Vector3 Origin;
        internal int Plans;
        internal uint FirstAt;
        internal uint LastAt;
        internal int Refusals;
        internal bool LoopReported;
        internal bool RefusalReported;

        /// <summary>The route watch's suppression epoch these counts belong to (<see cref="PlayerbotRoutePlanWatch.SuppressionEpoch"/>).</summary>
        internal int Epoch;

        /// <summary>The route watch has suppressed this goal's pair more than once and it is still refused: the next refusal is reported.</summary>
        internal bool Escalated;
    }

    private static readonly ConditionalWeakTable<Player, GoalWatch> Watches = [];

    /// <summary>Whether the checks stay silent for this bot: it is in a chaos probe, which breaks its navigation on purpose.</summary>
    internal static bool Silenced(Player player) => player.Session is WorldSession { IsInChaosProbe: true };

    private static uint NowOf(Player player) => player.Session is WorldSession session ? session.World.NowMs : 0u;

    /// <summary>
    /// A route to <paramref name="destination"/> was accepted by an exact (non-partial) plan. It must begin where the bot stands
    /// (<paramref name="asked"/>, within <see cref="RouteStartOffYards"/>) and, unless the route was cut short by a bound
    /// (<see cref="PathType.Short"/>, or the route bound: its end is then not the path's end), end at the destination
    /// (<see cref="PathEndShortYards"/> flat). Reports <c>nav/route-start-off</c> and <c>nav/path-ends-short</c>.
    /// </summary>
    internal static void CheckExactRoute(Player player, Vector3 asked, Vector3 destination, PathResult path, PlayerbotRoute route)
    {
        if (route.Points.Count < 2 || path.Points.Count < 2 || Silenced(player)) return;
        Vector3 first = path.Points[0];
        if (Vector3.DistanceSquared(first, asked) > RouteStartOffYards * RouteStartOffYards)
            PlayerbotAnomalies.Report(player, "nav", "route-start-off",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"a route toward {destination.X:F1},{destination.Y:F1},{destination.Z:F1} begins {Vector3.Distance(first, asked):F1} yd from where the bot was asked from ({asked.X:F1},{asked.Y:F1},{asked.Z:F1})"));
        Vector3 end = route.Points[^1];
        // A route cut by the route bound or the point bound ends part of the way along: its end is not the path's end.
        if ((path.Type & PathType.Short) != 0 || Vector3.DistanceSquared(end, path.Points[^1]) > 0.01f) return;
        float flat = Vector2.Distance(new(end.X, end.Y), new(destination.X, destination.Y));
        if (flat > PathEndShortYards)
            PlayerbotAnomalies.Report(player, "nav", "path-ends-short",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"an exact route ends {flat:F1} yd (flat) short of its destination {destination.X:F1},{destination.Y:F1},{destination.Z:F1} (route end {end.X:F1},{end.Y:F1},{end.Z:F1}, {route.Points.Count} points, type {path.Type})"));
    }

    /// <summary>
    /// One finished plan toward <paramref name="goal"/> from <paramref name="origin"/> (<see cref="PlayerbotNavigation.TryPlanToward"/>).
    /// <paramref name="inconclusive"/>: no verdict (a tile being read, an async plan deferred): not counted at all. Reports
    /// <c>nav/replan-loop</c> (<see cref="ReplanLoopPlans"/> successful plans from one spot over <see cref="ReplanLoopMinMs"/>, each made
    /// while the bot could move), <c>nav/goal-unreachable</c> (<see cref="RepeatedRefusals"/> refusals in a row within one episode of the route watch,
    /// or a refusal after it has suppressed the goal more than once: <see cref="PlayerbotRoutePlanWatch.SuppressionEpoch"/>), and <c>nav/no-path-connected</c>
    /// besides when the region graph puts the bot and the goal in one component (the search refused a goal the mesh joins to the bot).
    /// Once per episode: the next report of a key needs the goal or the bot's place to change, or a success.
    /// <para>
    /// An episode is the one <see cref="PlayerbotRoutePlanWatch"/> watches (issue #1494): a plan more than
    /// <see cref="PlayerbotRoutePlanWatch.WindowMs"/> after the last one starts a new episode (a bot back at one spot minutes later is
    /// on a new visit, not in a loop), and a plan made while the bot cannot move (<see cref="CanMove"/>: dead, rooted, fighting) does
    /// not count toward a loop. <paramref name="route"/>, the route the plan answered, is named in the report.
    /// </para>
    /// </summary>
    internal static void ObservePlan(Player player, Vector3 goal, Vector3 origin, bool routed, bool inconclusive, PlayerbotRoute? route = null)
    {
        if (inconclusive || Silenced(player)) return;
        GoalWatch watch = Watches.GetValue(player, static _ => new GoalWatch());
        uint now = NowOf(player);
        int epoch = PlayerbotRoutePlanWatch.SuppressionEpoch(player);
        bool canMove = CanMove(player);
        if (!watch.Has || Vector3.DistanceSquared(watch.Goal, goal) > SameGoalYards * SameGoalYards
            || unchecked(now - watch.LastAt) > PlayerbotRoutePlanWatch.WindowMs)
        {
            watch.Has = true;
            watch.Goal = goal;
            watch.Origin = origin;
            watch.Plans = routed && canMove ? 1 : 0;
            watch.FirstAt = now;
            watch.LastAt = now;
            watch.Refusals = routed ? 0 : 1;
            watch.LoopReported = false;
            watch.RefusalReported = false;
            watch.Epoch = epoch;
            watch.Escalated = false;
            return;
        }

        if (watch.Epoch != epoch)
        {
            // The route watch suppressed a pair since the last plan counted here (bot QA #1492): the plans its negative cache refused never
            // came here, so a count kept across the suppression would join two episodes and report the first refusal after it expired as
            // the RepeatedRefusals-th in a row. The count starts over. A goal whose pair the watch has suppressed more than once (each time
            // for longer) and that is still refused is reported on its next refusal: the suppressions did not make it reachable.
            watch.Epoch = epoch;
            watch.Origin = origin;
            watch.Plans = 0;
            watch.FirstAt = now;
            watch.Refusals = 0;
            watch.LoopReported = false;
            watch.Escalated = PlayerbotRoutePlanWatch.SuppressionStrikes(player) >= 2;
            // A refusal seen with a new epoch is the one that made the watch suppress (RecordOutcome runs first): not one after it.
            if (!routed) return;
        }

        if (Vector3.DistanceSquared(watch.Origin, origin) > SameOriginYards * SameOriginYards)
        {
            // The bot moved since the first of these plans: progress, a new count (a long trip plans each leg from a new spot).
            watch.Origin = origin;
            watch.Plans = routed && canMove ? 1 : 0;
            watch.FirstAt = now;
            watch.LoopReported = false;
        }
        else if (routed && canMove)
        {
            // The count's span starts at its first counted plan (plans made while the bot could not move are no part of it).
            if (watch.Plans == 0) watch.FirstAt = now;
            watch.Plans++;
        }

        watch.LastAt = now;

        if (routed)
        {
            watch.Refusals = 0;
            watch.RefusalReported = false;
            watch.Escalated = false;
            if (!watch.LoopReported && watch.Plans >= ReplanLoopPlans && unchecked(now - watch.FirstAt) >= ReplanLoopMinMs && canMove)
            {
                watch.LoopReported = true;
                PlayerbotAnomalies.Report(player, "nav", "replan-loop",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{watch.Plans} routes planned toward {goal.X:F1},{goal.Y:F1},{goal.Z:F1} from within {SameOriginYards:F0} yd of {origin.X:F1},{origin.Y:F1},{origin.Z:F1} over {unchecked(now - watch.FirstAt) / 1000}s: the bot does not get closer{RouteOf(route)}"));
            }

            return;
        }

        if ((++watch.Refusals < RepeatedRefusals && !watch.Escalated) || watch.RefusalReported) return;
        watch.RefusalReported = true;
        int fromComponent = -1, toComponent = -1;
        PlayerbotNavigation.IsUnreachableOnFoot(player, goal, out fromComponent, out toComponent);
        PlayerbotAnomalies.Report(player, "nav", "goal-unreachable", watch.Escalated
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"plans toward {goal.X:F1},{goal.Y:F1},{goal.Z:F1} are still refused after the route watch suppressed the goal {PlayerbotRoutePlanWatch.SuppressionStrikes(player)} times (region components bot {fromComponent}, goal {toComponent})")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{watch.Refusals} plans in a row toward {goal.X:F1},{goal.Y:F1},{goal.Z:F1} were refused (region components bot {fromComponent}, goal {toComponent})"));
        // A far goal (hung in the air or on excluded ground) that snaps into the bot's own component reports goal-unreachable with goal -1,
        // not no-path-connected: it has no region of its own, so the graph says nothing about it.
        if (fromComponent >= 0 && fromComponent == toComponent)
            PlayerbotAnomalies.Report(player, "nav", "no-path-connected",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"the region graph joins the bot and {goal.X:F1},{goal.Y:F1},{goal.Z:F1} (component {fromComponent}) but {watch.Refusals} path searches found no way"));
    }

    /// <summary>The last route planned, for the <c>nav/replan-loop</c> detail: its first leg, its size, and whether the mesh made it.</summary>
    private static string RouteOf(PlayerbotRoute? route)
    {
        if (route is not { Points.Count: >= 2 }) return string.Empty;
        Vector3 a = route.Points[0], b = route.Points[1];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $" (last route: first leg {a.X:F1},{a.Y:F1},{a.Z:F1} -> {b.X:F1},{b.Y:F1},{b.Z:F1} of {Vector3.Distance(a, b):F1} yd, {route.Points.Count} points, {route.Distance:F1} yd, {(route.Navigated ? "mesh" : "stepped")})");
    }

    /// <summary>Whether the bot could be walking at all: alive, not rooted, not in a fight.</summary>
    private static bool CanMove(Player player)
        => player.IsAlive && (player.Movement.Flags & MovementFlags.Root) == 0 && !player.Combat.IsInCombat;

    /// <summary>
    /// Whether a destination chosen for a quest giver or ender trip makes sense. Returns the anomaly code, or null when it does:
    /// <c>wrong-map</c> (the spawn is on another map than the bot's), <c>non-finite</c> (the spawn has no place), <c>hostile-faction</c>
    /// (the NPC's faction template is hostile to the bot's: an enemy's town; resolved templates only).
    /// </summary>
    internal static string? DestinationFault(uint botMap, uint botFactionTemplate, CreatureSpawn spawn, uint npcFactionTemplate,
        FactionTemplateCatalog? factions, bool wantsFriendly)
    {
        if (spawn.MapId != botMap) return "wrong-map";
        if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) return "non-finite";
        // Hostile by the templates in both directions (the relation PlayerbotNavigation.IsFriendlyTownNpc reads); unresolved: no verdict.
        if (wantsFriendly && factions?.Find(npcFactionTemplate) is { } npc && factions.Find(botFactionTemplate) is { } self
            && npc.IsHostileTo(self) && self.IsHostileTo(npc))
            return "hostile-faction";
        return null;
    }

    /// <summary>Reports <paramref name="code"/> (from <see cref="DestinationFault"/>) for the destination <paramref name="entry"/>.</summary>
    internal static void ReportDestination(Player player, string code, uint entry, CreatureSpawn spawn)
    {
        if (Silenced(player)) return;
        PlayerbotAnomalies.Report(player, "destination", code,
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"chose creature {entry} spawn {spawn.Guid} (map {spawn.MapId} at {spawn.X:F1},{spawn.Y:F1},{spawn.Z:F1}) as a destination"));
    }

    /// <summary>
    /// The time one bot has been travelling to one destination (legs included, <see cref="Note"/> each time a leg is planned or walked),
    /// for <c>destination/not-reached</c>. Owned by one <see cref="PlayerbotWorldDestinations"/>; world thread.
    /// </summary>
    internal sealed class TripWatch
    {
        private bool _active;
        private uint _entry;
        private uint _spawn;
        private uint _startedAt;
        private bool _reported;

        /// <summary>Whether the bot still travels to a destination being watched (tests).</summary>
        internal bool Active => _active;

        /// <summary>The bot is on its way to <paramref name="entry"/>'s spawn <paramref name="spawn"/> at <paramref name="now"/> (the world clock, ms).</summary>
        internal void Note(Player player, uint entry, uint spawn, uint now)
        {
            if (!_active || _entry != entry || _spawn != spawn)
            {
                _active = true;
                _entry = entry;
                _spawn = spawn;
                _startedAt = now;
                _reported = false;
                return;
            }

            if (_reported || unchecked(now - _startedAt) < TripBudgetMs || Silenced(player)) return;
            _reported = true;
            PlayerbotAnomalies.Report(player, "destination", "not-reached",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"travelling to creature {entry} spawn {spawn} for {unchecked(now - _startedAt) / 60_000} min without arriving"));
        }

        /// <summary>The trip ended (arrived, given up, or the goal changed).</summary>
        internal void Clear() => _active = false;
    }
}
