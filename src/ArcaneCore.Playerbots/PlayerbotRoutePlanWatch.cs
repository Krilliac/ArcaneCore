using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Per-bot route-plan progress watch and negative cache for <see cref="PlayerbotNavigation.TryPlanToward"/>.
/// <para>
/// When a bot repeatedly plans toward the same goal (within <see cref="SameGoalYards"/>) from the same spot
/// (within <see cref="SameOriginYards"/>) without closing flat distance to the goal by at least
/// <see cref="MinProgressYards"/> over <see cref="MinLoopSpanMs"/> (or when consecutive timestamped plan
/// attempts for the same origin/goal pair are definitively refused <see cref="MaxConsecutiveRefusals"/> times),
/// the <c>(origin, goal)</c> pair is suppressed for <see cref="SuppressMs"/> (doubled per earlier strike of the same pair,
/// <see cref="SuppressionSpanMs"/>) and blacklisted in
/// <see cref="PlayerbotMotion"/> so callers abandon the unproductive goal instead of entering a replan loop
/// (<c>bot-nav/replan-loop</c>) or hammering refused path searches (<c>bot-nav/goal-unreachable</c>,
/// <c>bot-nav/no-path-connected</c>).
/// </para>
/// </summary>
internal static partial class PlayerbotRoutePlanWatch
{
    /// <summary>Two goals within this flat distance (yards) count as the same goal.</summary>
    internal const float SameGoalYards = 10f;

    /// <summary>Two plan origins within this flat distance (yards) count as the same origin spot.</summary>
    internal const float SameOriginYards = 3f;

    /// <summary>Minimum flat distance reduction toward the goal required to count as forward progress.</summary>
    internal const float MinProgressYards = 3f;

    /// <summary>Maximum unproductive successful plans allowed from one spot toward one goal before suppression.</summary>
    internal const int MaxUnproductivePlans = 3;

    /// <summary>
    /// Minimum elapsed time (ms) across unproductive plans before a replan loop is suppressed. Ordered strictly between the destinations' own
    /// not-closing clock (<see cref="PlayerbotWorldDestinations.SpawnNotClosingMs"/>), which must see a stalled walk first (it suspends the quest
    /// or passes the spawn over), and <see cref="PlayerbotNavigationAnomalies.ReplanLoopMinMs"/>, which this watch must beat.
    /// </summary>
    internal const uint MinLoopSpanMs = PlayerbotWorldDestinations.SpawnNotClosingMs + 5_000;

    /// <summary>Maximum consecutive definitive refusals for one origin/goal pair before negative-cache suppression.</summary>
    internal const int MaxConsecutiveRefusals = 4;

    /// <summary>Sliding window (ms) after which an idle watch entry resets.</summary>
    internal const uint WindowMs = 180_000;

    /// <summary>
    /// Duration (ms) for which an origin/goal pair is held in the negative cache the first time it is suppressed. A pair suppressed again
    /// while its strike is remembered is held for twice as long per strike (<see cref="SuppressionSpanMs"/>), up to <see cref="MaxSuppressMs"/>.
    /// </summary>
    internal const uint SuppressMs = 180_000;

    /// <summary>The longest a pair is held in the negative cache however often it was suppressed (ms): 30 minutes.</summary>
    internal const uint MaxSuppressMs = 1_800_000;

    /// <summary>
    /// How long (ms) after its suppression expires a pair's strike count is remembered: a pair suppressed again within it is held longer
    /// (<see cref="SuppressionSpanMs"/>); after it, the next suppression is a first one again.
    /// </summary>
    internal const uint StrikeMemoryMs = MaxSuppressMs;

    private const int MaxSuppressedEntries = 8;

    /// <summary>A suppressed pair: refused until <see cref="UntilMs"/>, its <see cref="Strikes"/> remembered until <see cref="ForgetAtMs"/>.</summary>
    private readonly record struct SuppressedPair(uint MapId, Vector3 Origin, Vector3 Goal, uint UntilMs, int Strikes, uint ForgetAtMs);

    /// <summary>The span (ms) of a pair's <paramref name="strikes"/>-th suppression: <see cref="SuppressMs"/> doubled per earlier strike, at most <see cref="MaxSuppressMs"/>.</summary>
    internal static uint SuppressionSpanMs(int strikes)
    {
        ulong span = (ulong)SuppressMs << Math.Clamp(strikes - 1, 0, 16);
        return (uint)Math.Min(span, MaxSuppressMs);
    }

    /// <summary>
    /// A number that changes each time the watch suppresses a pair for <paramref name="player"/> (0 before the first). Callers that keep
    /// their own count of plans or refusals (<see cref="PlayerbotNavigationAnomalies.ObservePlan"/>) start it over when it changes: the
    /// plans refused by the negative cache never reach them, so a count kept across a suppression joins two episodes into one.
    /// </summary>
    internal static int SuppressionEpoch(Player player) => States.TryGetValue(player, out WatchState? state) ? state.Epoch : 0;

    /// <summary>How many times in a row (strikes remembered) the pair of the latest suppression for <paramref name="player"/> has been suppressed (0 when none).</summary>
    internal static int SuppressionStrikes(Player player) => States.TryGetValue(player, out WatchState? state) ? state.LastStrikes : 0;

    private sealed class WatchState
    {
        internal int Epoch;
        internal int LastStrikes;
        internal bool Has;
        internal uint MapId;
        internal Vector3 Origin;
        internal Vector3 Goal;
        internal Vector3 LastRouteEnd;
        internal float BestDistance;
        internal uint FirstAt;
        internal uint LastAt;
        internal int Plans;
        internal int Refusals;
        internal readonly List<SuppressedPair> Suppressed = new(MaxSuppressedEntries);
    }

    private static readonly ConditionalWeakTable<Player, WatchState> States = [];

    /// <summary>
    /// True when <paramref name="goal"/> from <paramref name="origin"/> is currently suppressed for <paramref name="player"/>,
    /// or when the active watch has already reached the unproductive-plan ceiling and must refuse the next attempt.
    /// </summary>
    internal static bool ShouldRefuse(Player player, Vector3 origin, Vector3 goal, uint now)
    {
        if (!States.TryGetValue(player, out WatchState? state)) return false;
        PruneExpired(state, now);

        uint mapId = player.Map?.MapId ?? player.MapId;
        for (int i = 0; i < state.Suppressed.Count; i++)
        {
            SuppressedPair entry = state.Suppressed[i];
            if (IsActive(entry, now)
                && entry.MapId == mapId
                && FlatDistanceSquared(entry.Origin, origin) <= SameOriginYards * SameOriginYards
                && FlatDistanceSquared(entry.Goal, goal) <= SameGoalYards * SameGoalYards)
            {
                return true;
            }
        }

        if (!state.Has || state.MapId != mapId) return false;
        if (unchecked(now - state.LastAt) > WindowMs)
        {
            state.Has = false;
            return false;
        }

        if (FlatDistanceSquared(state.Goal, goal) > SameGoalYards * SameGoalYards) return false;
        float distNow = FlatDistance(origin, goal);
        bool sameSpot = FlatDistanceSquared(state.Origin, origin) <= SameOriginYards * SameOriginYards;
        bool progressed = state.BestDistance - distNow >= MinProgressYards;
        if (!sameSpot || progressed) return false;

        if (CanMove(player)
            && state.Plans >= MaxUnproductivePlans
            && unchecked(now - state.FirstAt) >= MinLoopSpanMs)
        {
            Suppress(player, state, mapId, state.Origin, goal, state.LastRouteEnd, now);
            return true;
        }

        return false;
    }

    /// <summary>Records the outcome of a definitive (non-pending, non-deferred) timestamped <c>TryPlanToward</c> call.</summary>
    internal static void RecordOutcome(Player player, Vector3 origin, Vector3 goal, bool planned, PlayerbotRoute? route, uint now)
    {
        WatchState state = States.GetValue(player, static _ => new WatchState());
        PruneExpired(state, now);

        uint mapId = player.Map?.MapId ?? player.MapId;
        float distNow = FlatDistance(origin, goal);
        Vector3 routeEnd = route is { Points.Count: > 0 } ? route.Points[^1] : goal;

        if (!state.Has
            || state.MapId != mapId
            || unchecked(now - state.LastAt) > WindowMs
            || FlatDistanceSquared(state.Goal, goal) > SameGoalYards * SameGoalYards)
        {
            state.Has = true;
            state.MapId = mapId;
            state.Origin = origin;
            state.Goal = goal;
            state.LastRouteEnd = routeEnd;
            state.BestDistance = distNow;
            state.FirstAt = now;
            state.LastAt = now;
            state.Plans = planned && CanMove(player) ? 1 : 0;
            state.Refusals = planned ? 0 : 1;
            return;
        }

        bool sameSpot = FlatDistanceSquared(state.Origin, origin) <= SameOriginYards * SameOriginYards;
        bool progressed = state.BestDistance - distNow >= MinProgressYards;
        if (!sameSpot || progressed)
        {
            state.Origin = origin;
            state.Goal = goal;
            state.LastRouteEnd = routeEnd;
            state.BestDistance = MathF.Min(state.BestDistance, distNow);
            state.FirstAt = now;
            state.LastAt = now;
            state.Plans = planned && CanMove(player) ? 1 : 0;
            state.Refusals = planned ? 0 : 1;
            return;
        }

        state.LastAt = now;
        if (planned)
        {
            state.Refusals = 0;
            state.LastRouteEnd = routeEnd;
            if (CanMove(player))
            {
                if (state.Plans == 0) state.FirstAt = now;
                state.Plans++;
            }
            return;
        }

        state.Refusals++;
        if (state.Refusals >= MaxConsecutiveRefusals)
        {
            Suppress(player, state, mapId, state.Origin, goal, goal, now);
        }
    }

    internal static void Clear(Player player) => States.Remove(player);

    private static void Suppress(Player player, WatchState state, uint mapId, Vector3 origin, Vector3 goal, Vector3 routeEnd, uint now)
    {
        // A pair suppressed before (its strike still remembered) is held longer each time: the goal did not become reachable meanwhile.
        int strikes = 1;
        for (int i = state.Suppressed.Count - 1; i >= 0; i--)
        {
            SuppressedPair existing = state.Suppressed[i];
            if (existing.MapId == mapId
                && FlatDistanceSquared(existing.Origin, origin) <= SameOriginYards * SameOriginYards
                && FlatDistanceSquared(existing.Goal, goal) <= SameGoalYards * SameGoalYards)
            {
                strikes = Math.Max(strikes, existing.Strikes + 1);
                state.Suppressed.RemoveAt(i);
            }
        }

        if (state.Suppressed.Count >= MaxSuppressedEntries)
        {
            // Make room: a remembered strike (no longer refusing) goes before a pair still held, the oldest first.
            int evict = state.Suppressed.FindIndex(entry => !IsActive(entry, now));
            state.Suppressed.RemoveAt(evict >= 0 ? evict : 0);
        }

        uint until = unchecked(now + SuppressionSpanMs(strikes));
        state.Suppressed.Add(new SuppressedPair(mapId, origin, goal, until, strikes, unchecked(until + StrikeMemoryMs)));
        state.Epoch = unchecked(state.Epoch + 1);
        state.LastStrikes = strikes;
        state.Has = false;
        if (player.IsAlive)
        {
            PlayerbotMotion.Blacklist(player, [goal, routeEnd], now);
        }
    }

    private static void PruneExpired(WatchState state, uint now)
    {
        for (int i = state.Suppressed.Count - 1; i >= 0; i--)
        {
            if (unchecked((int)(state.Suppressed[i].ForgetAtMs - now)) <= 0)
            {
                state.Suppressed.RemoveAt(i);
            }
        }
    }

    /// <summary>Whether <paramref name="entry"/> still refuses its pair at <paramref name="now"/> (past it, only its strike count is remembered).</summary>
    private static bool IsActive(SuppressedPair entry, uint now) => unchecked((int)(entry.UntilMs - now)) > 0;

    private static bool CanMove(Player player)
        => player.IsAlive && (player.Movement.Flags & MovementFlags.Root) == 0 && !player.Combat.IsInCombat;

    private static float FlatDistance(Vector3 a, Vector3 b)
        => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    private static float FlatDistanceSquared(Vector3 a, Vector3 b)
        => Vector2.DistanceSquared(new(a.X, a.Y), new(b.X, b.Y));
}
