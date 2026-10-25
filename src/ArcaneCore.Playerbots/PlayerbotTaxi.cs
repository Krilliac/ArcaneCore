using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>What <see cref="PlayerbotTaxi.TryTravelByTaxi"/> decided for a trip.</summary>
internal enum TaxiTravel : byte
{
    /// <summary>Not by taxi (disabled, too short, no usable known nodes, unaffordable, backed off): the caller walks as before.</summary>
    Walk,

    /// <summary>A taxi trip is under way (or was just begun); the brain drives it (<see cref="PlayerbotTaxi.Drive"/>) and nothing else moves the bot.</summary>
    Traveling,
}

/// <summary>What <see cref="PlayerbotTaxi.Drive"/> did this think.</summary>
internal enum TaxiStep : byte
{
    /// <summary>No trip: the brain goes on.</summary>
    Idle,

    /// <summary>Walking to the flight master, or in the air: the brain does nothing else this think.</summary>
    Traveling,

    /// <summary>The flight ended (reported once); the goal that began the trip continues from where the bot landed.</summary>
    Arrived,

    /// <summary>The trip failed (reported once); the departure node is backed off (<see cref="PlayerbotTransitBackoff"/>). The goal walks.</summary>
    Failed,

    /// <summary>
    /// The bot stands at the spawn of its flight master, which was killed (dead, or gone and waiting for its respawn timer): normal play, not a
    /// failure. Nothing is reported and no failure is counted; the departure node is held off until about the respawn
    /// (<see cref="PlayerbotTransitBackoff.Defer"/>). The goal walks.
    /// </summary>
    MasterDead,
}

/// <summary>The stages of one bot's taxi trip (<see cref="PlayerbotTaxi"/>).</summary>
internal enum TaxiStage : byte { ToMaster, Flying, Done, Failed }

/// <summary>One bot's taxi trip: where it departs, where it lands, what the flight costs, and the walk to the flight master.</summary>
internal sealed class TaxiTrip(uint mapId, uint[] nodes, uint fare, uint masterEntry, uint masterSpawn, Vector3 master, Vector3 goal, uint nowMs)
{
    public uint MapId { get; internal set; } = mapId;

    /// <summary>The route: departure node, any transfer nodes, landing node (two or more).</summary>
    public uint[] Nodes { get; } = nodes;

    /// <summary>The fares of the route added up without any reputation or honor discount: what the bot must be able to pay.</summary>
    public uint Fare { get; } = fare;

    public uint MasterEntry { get; } = masterEntry;

    /// <summary>The spawn id (<see cref="CreatureSpawn.Guid"/>) of the flight master the trip walks to.</summary>
    public uint MasterSpawn { get; } = masterSpawn;

    /// <summary>Where the departure node's flight master stands (its spawn).</summary>
    public Vector3 Master { get; } = master;

    /// <summary>Where the trip ends; the goal that asked for it resumes from the landing.</summary>
    public Vector3 Goal { get; } = goal;

    /// <summary>
    /// A learning detour (<see cref="PlayerbotTaxi.Discover"/>): the bot walks to the flight master of the one node in <see cref="Nodes"/> it does
    /// not know yet, asks it (CMSG_TAXIQUERYAVAILABLENODES) and is done; it never flies. Not a way to anywhere: a goal's taxi planning ignores it.
    /// </summary>
    public bool LearnOnly { get; init; }

    public TaxiStage Stage { get; set; } = TaxiStage.ToMaster;
    public string? Failure { get; set; }
    public PlayerbotRoute? Route { get; set; }

    /// <summary>The wait for navigation tiles still being read when the way to the master is planned.</summary>
    public TileWait Tiles;
    public float Best { get; set; } = float.PositiveInfinity;
    public uint BestMs { get; set; } = nowMs;
    public uint NextPlanMs { get; set; } = nowMs;
    public int Attempts { get; set; }

    /// <summary>Planning calls in a row that the navigation refused (not pending, not a route): <see cref="PlayerbotTaxi.MaxRefusedPlans"/> fail the walk.</summary>
    public int RefusedPlans { get; set; }

    /// <summary>Times the bot could not be brought to a stop before the flight request: <see cref="PlayerbotTaxi.MaxStopAttempts"/> fail the trip.</summary>
    public int StopAttempts { get; set; }

    /// <summary>When the flight began (world ms), and how long it may take before it is overdue.</summary>
    public uint FlightStartMs { get; set; }

    public uint FlightBudgetMs { get; set; }

    /// <summary>The next in-flight check, and where the bot was at the last one.</summary>
    public uint NextWatchMs { get; set; }

    public uint SampleMs { get; set; }
    public Vector3 SamplePosition { get; set; }

    /// <summary>The bot died (or a chaos probe ran) in flight: where it lands says nothing about the flight.</summary>
    public bool Interrupted { get; set; }

    /// <summary>Which in-flight anomalies were reported already: one report per trip.</summary>
    public bool StallReported { get; set; }

    public bool OverdueReported { get; set; }
    public bool FlagReported { get; set; }
    public bool NonFiniteReported { get; set; }
}

/// <summary>
/// The taxi network the world's data describes, as bots read it: the network nodes (<see cref="NpcStore.IsNetworkNode"/>) and the direct
/// paths between them (<see cref="NpcStore.Path"/>). Immutable; built once per <see cref="NpcStore"/> (<see cref="Of"/>), so a content
/// reload that replaces the store gets a fresh index. Any thread.
/// </summary>
internal sealed class TaxiIndex
{
    private static readonly ConditionalWeakTable<NpcStore, TaxiIndex> Cache = [];
    private const int Slots = NpcStore.TaxiMaskSize * 32 + 1;

    private readonly TaxiNode?[] _nodes = new TaxiNode?[Slots];
    private readonly (uint To, uint Price)[][] _edges = new (uint, uint)[Slots][];

    [ThreadStatic] private static uint[]? _cost;
    [ThreadStatic] private static int[]? _hops;
    [ThreadStatic] private static uint[]? _previous;
    [ThreadStatic] private static bool[]? _done;

    private TaxiIndex(NpcStore store)
    {
        var list = new List<TaxiNode>();
        foreach (TaxiNode node in store.Nodes)
        {
            if (node.Id is 0 or >= Slots || !store.IsNetworkNode(node.Id)) continue;
            _nodes[node.Id] = node;
            list.Add(node);
        }

        All = list;
        foreach (TaxiNode from in list)
        {
            var edges = new List<(uint, uint)>();
            foreach (TaxiNode to in list)
            {
                if (to.Id != from.Id && to.MapId == from.MapId && store.Path(from.Id, to.Id) is { } path) edges.Add((to.Id, path.Price));
            }

            _edges[from.Id] = [.. edges];
        }
    }

    /// <summary>Every network node, in id order.</summary>
    public IReadOnlyList<TaxiNode> All { get; }

    public static TaxiIndex Of(NpcStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Cache.GetValue(store, static s => new TaxiIndex(s));
    }

    public TaxiNode? Node(uint id) => id < Slots ? _nodes[id] : null;

    /// <summary>vmangos <c>ObjectMgr::GetNearestTaxiNode</c> (as <c>QuestNpcServices</c> applies it): the nearest network node on the map with a mount for the team; 0 when none.</summary>
    public uint Nearest(uint mapId, float x, float y, float z, bool alliance)
    {
        uint best = 0;
        float bestDist = 0;
        foreach (TaxiNode node in All)
        {
            if (node.MapId != mapId || (alliance ? node.MountAlliance : node.MountHorde) == 0) continue;
            float dx = node.X - x, dy = node.Y - y, dz = node.Z - z;
            float dist = (dx * dx) + (dy * dy) + (dz * dz);
            if (best == 0 || dist < bestDist) { best = node.Id; bestDist = dist; }
        }

        return best;
    }

    public static bool IsKnown(IReadOnlyList<uint> mask, uint node)
        => node != 0 && (node - 1) / 32 is var field && field < mask.Count && (mask[(int)field] & (1u << (int)((node - 1) % 32))) != 0;

    /// <summary>
    /// The cheapest flight from <paramref name="source"/> over known nodes of its map to the known node the bot lands at, or false. The
    /// landing is the reachable affordable node nearest <paramref name="goal"/> that lies within <paramref name="maxWalk"/> of it, and the
    /// walk to the departure plus the walk from the landing must be at most half of the straight line <paramref name="straight"/>. Cost
    /// ties break on fewer hops.
    /// </summary>
    public bool TryRoute(IReadOnlyList<uint> mask, TaxiNode source, Vector3 bot, Vector3 goal, uint money, float maxWalk, float straight,
        out uint[] route, out uint fare)
    {
        route = [];
        fare = 0;
        // Scratch kept per thread (the world thread): no allocation per replan.
        uint[] cost = _cost ??= new uint[Slots];
        int[] hops = _hops ??= new int[Slots];
        uint[] previous = _previous ??= new uint[Slots];
        bool[] done = _done ??= new bool[Slots];
        Array.Fill(cost, uint.MaxValue);
        Array.Clear(hops);
        Array.Clear(previous);
        Array.Clear(done);
        cost[source.Id] = 0;
        float walkToDeparture = Vector3.Distance(bot, new Vector3(source.X, source.Y, source.Z));
        while (true)
        {
            uint u = 0;
            for (uint i = 1; i < Slots; i++)
            {
                if (!done[i] && cost[i] != uint.MaxValue && (u == 0 || cost[i] < cost[u])) u = i;
            }

            if (u == 0) break;
            done[u] = true;
            if (_edges[u] is not { } edges) continue;
            foreach ((uint to, uint price) in edges)
            {
                if (done[to] || !IsKnown(mask, to)) continue;
                ulong total = (ulong)cost[u] + price;
                if (total >= uint.MaxValue) continue;
                if (total < cost[to] || (total == cost[to] && hops[u] + 1 < hops[to]))
                {
                    cost[to] = (uint)total;
                    hops[to] = hops[u] + 1;
                    previous[to] = u;
                }
            }
        }

        uint landing = 0;
        float landingWalk = float.PositiveInfinity;
        foreach (TaxiNode node in All)
        {
            if (node.Id == source.Id || cost[node.Id] == uint.MaxValue || cost[node.Id] > money) continue;
            float walk = Vector3.Distance(goal, new Vector3(node.X, node.Y, node.Z));
            if (walk > maxWalk || walkToDeparture + walk > straight * 0.5f) continue;
            if (landing == 0 || walk < landingWalk || (walk == landingWalk && cost[node.Id] < cost[landing]))
            {
                landing = node.Id;
                landingWalk = walk;
            }
        }

        if (landing == 0) return false;
        var nodes = new List<uint>();
        for (uint at = landing; at != source.Id; at = previous[at]) nodes.Add(at);
        nodes.Add(source.Id);
        nodes.Reverse();
        route = [.. nodes];
        fare = cost[landing];
        return true;
    }
}

/// <summary>The kinds of transit point <see cref="PlayerbotTransitBackoff"/> keeps apart.</summary>
internal enum TransitKind : byte
{
    /// <summary>A flight path node (the key is the taxi node id).</summary>
    Taxi,

    /// <summary>A Deeprun Tram platform (the key is its entrance trigger id).</summary>
    Tram,

    /// <summary>A lift (the key is <see cref="ElevatorLift.Key"/>).</summary>
    Lift,
}

/// <summary>
/// Per-bot, per-point backoff of the transit means (a flight master's node, a tram platform, a lift): a failure involving one point waits
/// out <see cref="PlayerbotTravelOptions.BackoffMs"/> of the point's failures in a row (first wait doubling to the cap) and no other point
/// is affected; a success at the point (<see cref="Clear"/>) forgets its failures. A wait ends by time only: the failure count stays, so the
/// next failure waits longer. Bounded (<see cref="MaxPoints"/> points per bot, the table starts over when full). World thread; the table
/// hangs off the <see cref="Player"/> and goes with it.
/// </summary>
internal static class PlayerbotTransitBackoff
{
    /// <summary>The most points one bot remembers.</summary>
    internal const int MaxPoints = 64;

    private sealed class Entry
    {
        public int Failures;
        public uint UntilMs;
    }

    private static readonly ConditionalWeakTable<Player, Dictionary<(TransitKind Kind, uint Point), Entry>> Tables = [];

    /// <summary>
    /// Record a failure of <paramref name="point"/> at <paramref name="nowMs"/>: the wait is the schedule of its failures in a row, at most
    /// <paramref name="capMs"/>. Returns the wait (ms).
    /// </summary>
    internal static uint Fail(Player player, TransitKind kind, uint point, uint nowMs, PlayerbotOptions options, uint capMs = uint.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<(TransitKind, uint), Entry> table = Tables.GetOrCreateValue(player);
        if (!table.TryGetValue((kind, point), out Entry? entry))
        {
            if (table.Count >= MaxPoints) table.Clear();
            table[(kind, point)] = entry = new Entry();
        }

        entry.Failures = Math.Min(entry.Failures + 1, 64);
        uint wait = Math.Min(options.Travel.BackoffMs(entry.Failures), capMs);
        entry.UntilMs = unchecked(nowMs + wait);
        return wait;
    }

    /// <summary>
    /// Hold <paramref name="point"/> off for <paramref name="waitMs"/> without counting a failure: the point is not broken, it is out of service
    /// for a known time (a flight master that was killed, until it respawns). The failure count, and so the wait of a later real failure, is
    /// unchanged; a later wait never shortens a longer one still running. <paramref name="waitMs"/> must be below 2^31.
    /// </summary>
    internal static void Defer(Player player, TransitKind kind, uint point, uint nowMs, uint waitMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(waitMs, (uint)int.MaxValue);
        Dictionary<(TransitKind, uint), Entry> table = Tables.GetOrCreateValue(player);
        uint until = unchecked(nowMs + waitMs);
        if (!table.TryGetValue((kind, point), out Entry? entry))
        {
            if (table.Count >= MaxPoints) table.Clear();
            table[(kind, point)] = new Entry { UntilMs = until };
            return;
        }

        // An existing wait still running and longer than this one stays; an expired one (wrapping clock) is replaced.
        bool running = unchecked((int)(entry.UntilMs - nowMs)) > 0;
        if (!running || unchecked((int)(until - entry.UntilMs)) > 0) entry.UntilMs = until;
    }

    /// <summary>Whether <paramref name="point"/> is waiting out a failure at <paramref name="nowMs"/>.</summary>
    internal static bool IsBackedOff(Player player, TransitKind kind, uint point, uint nowMs)
        => Tables.TryGetValue(player, out var table) && table.TryGetValue((kind, point), out Entry? entry)
            && unchecked((int)(entry.UntilMs - nowMs)) > 0;

    /// <summary>Whether any point of <paramref name="kind"/> is waiting out a failure at <paramref name="nowMs"/>.</summary>
    internal static bool AnyBackedOff(Player player, TransitKind kind, uint nowMs)
    {
        if (!Tables.TryGetValue(player, out var table)) return false;
        foreach (((TransitKind k, _), Entry entry) in table)
        {
            if (k == kind && unchecked((int)(entry.UntilMs - nowMs)) > 0) return true;
        }

        return false;
    }

    /// <summary>The failures in a row of <paramref name="point"/> since its last success (0: none).</summary>
    internal static int Failures(Player player, TransitKind kind, uint point)
        => Tables.TryGetValue(player, out var table) && table.TryGetValue((kind, point), out Entry? entry) ? entry.Failures : 0;

    /// <summary>The point worked (a flight landed, a ride arrived): forget its failures and its wait.</summary>
    internal static void Clear(Player player, TransitKind kind, uint point)
    {
        if (Tables.TryGetValue(player, out var table)) table.Remove((kind, point));
    }
}

/// <summary>
/// Autonomous bots use flight paths the way a player does (docs/areas/playbots.md, "Flight paths"). Two parts, both through the real
/// handlers (<c>NpcServiceHandlers</c>, <see cref="WorldSession.TryManagedAction"/>), never by writing the taxi mask or starting a flight:
/// <list type="bullet">
/// <item><see cref="Discover"/>: a bot within interaction range of a flight master it may use, whose node it does not know yet, sends
/// CMSG_TAXIQUERYAVAILABLENODES, which learns the node (vmangos <c>HandleTaxiQueryAvailableNodes</c>).</item>
/// <item><see cref="TryTravelByTaxi"/> + <see cref="Drive"/>: a long trip (the straight line is at least
/// <see cref="PlayerbotTaxiOptions.MinRouteYards"/>) with a known, affordable landing node near its end becomes: walk to that route's
/// flight master, CMSG_ACTIVATETAXI (one hop) or CMSG_ACTIVATETAXIEXPRESS (several), read SMSG_ACTIVATETAXIREPLY, and wait while the
/// server flies the bot. The departure is the nearest known node within <see cref="PlayerbotTaxiOptions.MaxWalkToMasterYards"/>, or, when
/// none is that close, a farther known node whose walk plus the walk from the landing is at most half the straight line (a flight master
/// off the road is spoken to instead of walking the whole trip). The brain drives the
/// trip before anything else (<see cref="PlayerbotBrain"/>), so the bot neither thinks nor moves in the air.</item>
/// </list>
/// <para>
/// A refused or failed attempt backs the departure node off (<see cref="PlayerbotTransitBackoff"/>: <c>Travel.TransitBackoffSeconds</c>
/// doubling to <c>Travel.TransitBackoffMaxSeconds</c>, capped by <see cref="PlayerbotTaxiOptions.BackoffMinutes"/>); the goal walks, or flies
/// from another known node. A landing clears the node's failures.
/// </para>
/// <para>
/// A bot that passes within <see cref="DetourYards"/> of a flight master it may approach whose node it does not know takes a learning detour
/// (<see cref="TaxiTrip.LearnOnly"/>): it walks to the master and asks it, as a player who spots a gryphon master does; no flight.
/// </para>
/// Thread affinity: world thread; per-bot state hangs off the <see cref="Player"/> and goes with it.
/// </summary>
internal static partial class PlayerbotTaxi
{
    /// <summary>A bot looks for a flight master to ask at most this often.</summary>
    internal const uint ScanIntervalMs = 2_000;

    /// <summary>A master that did not teach its node is not asked again for this long.</summary>
    internal const uint AskAgainMs = 60_000;

    /// <summary>Walking that gains less than this on its target within <see cref="NoProgressMs"/> is stuck.</summary>
    internal const float ProgressYards = 5f;

    internal const uint NoProgressMs = 60_000;

    /// <summary>After a path query found nothing, the next one waits this long.</summary>
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A bot this close to the spawn of its flight master without being able to talk to one gives the trip up.</summary>
    internal const float AtSpawnYards = 6f;

    /// <summary>The action budget may refuse the flight request this many thinks in a row before the trip is given up.</summary>
    internal const int MaxActivateAttempts = 20;

    /// <summary>The anomaly area of every report in this file (subsystem <c>bot-taxi</c>).</summary>
    internal const string Area = "taxi";

    /// <summary>In flight the bot is looked at this often (world ms).</summary>
    internal const uint WatchIntervalMs = 5_000;

    /// <summary>A bot that moved less than this between two in-flight looks did not fly (the flight speed is 32 yards a second).</summary>
    internal const float StalledYards = 1f;

    /// <summary>The slowest speed a flight is expected to average, in yards per second, over the straight lines between its nodes (the real speed is 32).</summary>
    internal const float SlowestFlightYardsPerSecond = 8f;

    /// <summary>Time on top of the slowest-speed estimate for a flight to be called overdue (hop changes, a lagging world).</summary>
    internal const uint FlightGraceMs = 60_000;

    /// <summary>A landing farther than this from the route's last node is off it (a flight ends on its last waypoint, at the node).</summary>
    internal const float LandingToleranceYards = 50f;

    /// <summary>A landing lower than this below the last node is under the map.</summary>
    internal const float BelowNodeYards = 100f;

    /// <summary>Failed trips in a row (no landing between) that make a bot's taxi use a repeated failure.</summary>
    internal const int RepeatedFailures = 3;

    /// <summary>The walk to a flight master fails after this many refused plans in a row (pending tile reads do not count; a route resets the count).</summary>
    internal const int MaxRefusedPlans = 3;

    /// <summary>The bot may be refused a stop (the action budget) this many thinks in a row before the flight request; then the trip fails with <c>stop-refused</c>.</summary>
    internal const int MaxStopAttempts = 20;

    /// <summary>A flight master spawn this close (yards, straight line) to a bot, whose node the bot does not know, is worth a learning detour.</summary>
    internal const float DetourYards = 80f;

    private const int MaxAskMemo = 32;

    private static readonly ConditionalWeakTable<Player, TaxiTrip> Trips = [];
    private static readonly ConditionalWeakTable<Player, Memo> Memos = [];
    private static readonly ConditionalWeakTable<CreatureContent, MasterTable> MasterTables = [];

    private sealed class Memo
    {
        public uint NextScanMs;

        /// <summary>Trips that failed since the last one that landed; the repeated-failure anomaly is reported once at <see cref="RepeatedFailures"/>.</summary>
        public int Failures;

        /// <summary>The departure node whose flight master spawn could not be found, as last reported (0 = none): one report per node.</summary>
        public uint MasterMissingNode;

        public readonly Dictionary<ulong, uint> AskedUntil = [];

        // The last route search, reused while the known nodes, the departure, the destination, the bot's 25-yard cell and its money
        // bucket are the same: a bot waiting for tiles does not search again on every think.
        private bool _hasCache;
        private readonly uint[] _cacheMask = new uint[NpcStore.TaxiMaskSize];
        private uint _cacheSource;
        private Vector3 _cacheGoal;
        private int _cacheCellX, _cacheCellY;
        private uint _cacheMoneyBucket;
        private bool _cacheFound;
        private uint[] _cacheRoute = [];
        private uint _cacheFare;
        private uint _cacheMoney;
        private TaxiIndex? _cacheIndex;
        private float _cacheMaxWalk;

        public bool TryCached(IReadOnlyList<uint> mask, uint source, Vector3 goal, Vector3 here, uint money, TaxiIndex index, float maxWalk, out bool found, out uint[] route, out uint fare)
        {
            found = false;
            route = [];
            fare = 0;
            if (!_hasCache || !ReferenceEquals(_cacheIndex, index) || _cacheMaxWalk != maxWalk || _cacheSource != source || _cacheGoal != goal || _cacheCellX != Cell(here.X) || _cacheCellY != Cell(here.Y)
                || _cacheMoneyBucket != money / MoneyBucket) return false;
            for (int i = 0; i < _cacheMask.Length; i++)
            {
                if (i >= mask.Count || _cacheMask[i] != mask[i]) return false;
            }

            if (_cacheFound && _cacheFare > money) return false;

            // A search that found nothing for this much money may find something for more.
            if (!_cacheFound && money > _cacheMoney) return false;
            found = _cacheFound;
            route = _cacheRoute;
            fare = _cacheFare;
            return true;
        }

        public void Store(IReadOnlyList<uint> mask, uint source, Vector3 goal, Vector3 here, uint money, TaxiIndex index, float maxWalk, bool found, uint[] route, uint fare)
        {
            _hasCache = true;
            for (int i = 0; i < _cacheMask.Length; i++) _cacheMask[i] = i < mask.Count ? mask[i] : 0;
            _cacheSource = source;
            _cacheGoal = goal;
            _cacheCellX = Cell(here.X);
            _cacheCellY = Cell(here.Y);
            _cacheMoneyBucket = money / MoneyBucket;
            _cacheFound = found;
            _cacheRoute = route;
            _cacheFare = fare;
            _cacheMoney = money;
            _cacheIndex = index;
            _cacheMaxWalk = maxWalk;
        }

        private static int Cell(float v) => (int)MathF.Floor(v / 25f);
    }

    /// <summary>Copper per money bucket of the route cache (5 silver).</summary>
    private const uint MoneyBucket = 500;

    private sealed class MasterTable(int version, List<(CreatureSpawn Spawn, uint Faction)> spawns)
    {
        public int Version { get; } = version;
        public List<(CreatureSpawn Spawn, uint Faction)> Spawns { get; } = spawns;
    }

    /// <summary>The bot's trip, done or not (inspection and tests).</summary>
    internal static TaxiTrip? TripOf(Player player) => Trips.TryGetValue(player, out TaxiTrip? trip) ? trip : null;

    /// <summary>Whether a trip is under way (not done, not failed).</summary>
    internal static bool IsActive(Player player)
        => Trips.TryGetValue(player, out TaxiTrip? trip) && trip.Stage is TaxiStage.ToMaster or TaxiStage.Flying;

    /// <summary>Whether a trip that takes the bot somewhere is under way (a learning detour, <see cref="TaxiTrip.LearnOnly"/>, is not one).</summary>
    private static bool IsTravelTrip(Player player)
        => Trips.TryGetValue(player, out TaxiTrip? trip) && !trip.LearnOnly && trip.Stage is TaxiStage.ToMaster or TaxiStage.Flying;

    /// <summary>The server is flying the bot (UNIT_FLAG_TAXI_FLIGHT; also true for a flight no trip of ours began).</summary>
    internal static bool IsFlying(Player player) => (player.UnitFlags & UnitFlags.TaxiFlight) != 0;

    /// <summary>The reason the bot's last trip failed (inspection), null after a trip that arrived.</summary>
    internal static string? LastFailure(Player player)
        => Trips.TryGetValue(player, out TaxiTrip? trip) && trip.Stage == TaxiStage.Failed ? trip.Failure : null;

    /// <summary>Whether any flight node is backed off for this bot at <paramref name="nowMs"/> (a failed attempt lately; inspection).</summary>
    internal static bool IsBackedOff(Player player, uint nowMs) => PlayerbotTransitBackoff.AnyBackedOff(player, TransitKind.Taxi, nowMs);

    /// <summary>Whether the flight path <paramref name="node"/> is backed off for this bot at <paramref name="nowMs"/>: a trip departing there failed lately.</summary>
    internal static bool IsBackedOff(Player player, uint node, uint nowMs) => PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Taxi, node, nowMs);

    /// <summary>The next <see cref="Discover"/> scans at once instead of waiting out <see cref="ScanIntervalMs"/> (tests, inspection).</summary>
    internal static void ForceNextScan(Player player) => Memos.GetOrCreateValue(player).NextScanMs = 0;

    /// <summary>Drop the trip without a packet (death, a controller taking the bot over, or an aborted flight). A flight still in the air is the server's and goes on.</summary>
    internal static void Cancel(Player player)
    {
        if (Trips.TryGetValue(player, out TaxiTrip? trip) && (trip.Stage == TaxiStage.ToMaster || !IsFlying(player))) Trips.Remove(player);
    }

    /// <summary>
    /// Learn the node of a flight master within interaction range: CMSG_TAXIQUERYAVAILABLENODES through the real handler, which sets the bit
    /// in the taxi mask and answers SMSG_NEW_TAXI_PATH (vmangos <c>HandleTaxiQueryAvailableNodes</c>). Cheap: at most one look at the
    /// visible objects per <see cref="ScanIntervalMs"/>, and a request only for a visible, usable flight master whose node is not known. True
    /// when a request was sent.
    /// <para>
    /// When no master is within reach to ask, the same scan looks for a flight master spawn within <see cref="DetourYards"/> of the bot that it
    /// may approach and whose node it does not know (nor has backed off): a learning detour (<see cref="TaxiTrip.LearnOnly"/>) begins, which
    /// <see cref="Drive"/> walks and ends by asking that master. Not while a trip is under way, or the bot is in a tram or lift ride. It
    /// returns false then (nothing was sent yet).
    /// </para>
    /// </summary>
    internal static bool Discover(WorldSession session, Player player, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Taxi.Enabled || !player.IsInWorld || !player.IsAlive || player.Map is null || player.Combat.IsInCombat || IsFlying(player))
            return false;
        uint now = session.World.NowMs;
        Memo memo = Memos.GetOrCreateValue(player);
        if (unchecked((int)(memo.NextScanMs - now)) > 0) return false;
        memo.NextScanMs = unchecked(now + ScanIntervalMs);
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || services.StateOf(player) is not { Loaded: true } state
            || services.Deps.Creatures is not { } lookup)
            return false;
        TaxiIndex index = TaxiIndex.Of(services.Npcs);
        bool alliance = player.Team == Team.Alliance;
        CreatureContent? content = session.Services.GetService<CreatureWorldFeature>()?.Content;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (!MayBeFlightMaster(content, guid)) continue;
            if (lookup.Find(player, guid, NpcFlags.FlightMaster) is not { IsAlive: true, IsGameObject: false } npc || (npc.NpcFlags & NpcFlags.FlightMaster) == 0) continue;
            uint node = index.Nearest(npc.MapId, npc.X, npc.Y, npc.Z, alliance);
            if (node == 0 || TaxiIndex.IsKnown(state.TaxiMask, node)) continue;
            if (memo.AskedUntil.TryGetValue(guid.Value, out uint until) && unchecked((int)(until - now)) > 0) continue;
            if (services.InteractableNpc(player, guid, NpcFlags.FlightMaster) is null) continue;
            if (!session.TryManagedAction(WorldOpcode.CmsgTaxiqueryavailablenodes, GuidPayload(guid))) return false;
            if (memo.AskedUntil.Count >= MaxAskMemo) memo.AskedUntil.Clear();
            memo.AskedUntil[guid.Value] = unchecked(now + AskAgainMs);
            return true;
        }

        if (!Trips.TryGetValue(player, out TaxiTrip? current) || current.Stage is TaxiStage.Done or TaxiStage.Failed)
        {
            if (!PlayerbotTram.IsTraveling(player) && !PlayerbotElevator.IsTraveling(player)
                && !PlayerbotHearthstone.IsActive(player) && !PlayerbotMageService.IsActive(player))
                TryBeginLearnTrip(session, player, services, index, state, content, options, now);
        }

        return false;
    }

    /// <summary>
    /// Begin a learning detour to the nearest flight master spawn within <see cref="DetourYards"/> whose node is unknown and not backed off.
    /// Cost: one pass over the cached flight master spawns (a few dozen in the whole world), a squared distance each; the node lookup only for
    /// spawns inside the radius.
    /// </summary>
    private static void TryBeginLearnTrip(WorldSession session, Player player, QuestNpcServices services, TaxiIndex index, PlayerNpcState state,
        CreatureContent? content, PlayerbotOptions options, uint now)
    {
        if (content is null || session.Services.GetService<QuestNpcFeature>()?.FactionTemplates is not { } factions || player.Map is not { } map) return;
        Vector3 here = new(player.X, player.Y, player.Z);
        if (!here.IsFinite()) return;
        bool alliance = player.Team == Team.Alliance;
        float bestSq = DetourYards * DetourYards;
        CreatureSpawn? best = null;
        uint bestNode = 0;
        foreach ((CreatureSpawn spawn, uint faction) in TableOf(content).Spawns)
        {
            if (spawn.MapId != map.MapId || !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
            float dx = spawn.X - here.X, dy = spawn.Y - here.Y, dz = spawn.Z - here.Z;
            float distSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (distSq >= bestSq) continue;
            uint node = index.Nearest(spawn.MapId, spawn.X, spawn.Y, spawn.Z, alliance);
            if (node == 0 || TaxiIndex.IsKnown(state.TaxiMask, node) || IsBackedOff(player, node, now)) continue;
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, faction)) continue;
            if (!CanReachMaster(player, new Vector3(spawn.X, spawn.Y, spawn.Z), options)) continue;
            best = spawn;
            bestNode = node;
            bestSq = distSq;
        }

        if (best is null) return;
        Trips.AddOrUpdate(player, new TaxiTrip(map.MapId, [bestNode], 0, best.Entry, best.Guid, new Vector3(best.X, best.Y, best.Z), new Vector3(best.X, best.Y, best.Z), now)
        {
            LearnOnly = true,
        });
    }

    /// <summary>
    /// Whether <paramref name="destination"/> on <paramref name="mapId"/> can be reached by a known, affordable taxi route from a nearby
    /// known departure node (<see cref="TryTravelByTaxi"/>), without starting a trip. Shares the per-bot route cache with
    /// <see cref="TryTravelByTaxi"/>.
    /// </summary>
    internal static bool CanTravelByTaxi(WorldSession session, Player player, uint mapId, Vector3 destination, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Taxi.Enabled) return false;
        if (IsTravelTrip(player)) return true;
        if (IsActive(player)) return false;
        return TryPlanTaxi(session, player, mapId, destination, options, uint.MaxValue, out _, out _, out _);
    }

    /// <summary>
    /// Begin a taxi trip towards <paramref name="destination"/> on <paramref name="mapId"/> when flying is the clear win, else
    /// <see cref="TaxiTravel.Walk"/>. A trip already under way answers <see cref="TaxiTravel.Traveling"/>. The estimate of the walk is the
    /// straight line (no path query); flying needs a known departure node (the nearest within
    /// <see cref="PlayerbotTaxiOptions.MaxWalkToMasterYards"/>, or a farther one when the walk to it plus the walk from the landing is at
    /// most half the straight line), a flight master for it that the bot may approach, and a known landing node reachable over known nodes
    /// whose total fare (without discounts) the bot can pay now.
    /// </summary>
    internal static TaxiTravel TryTravelByTaxi(WorldSession session, Player player, uint mapId, Vector3 destination, PlayerbotOptions options)
        => TryTravelByTaxi(session, player, mapId, destination, options, uint.MaxValue);

    /// <summary>
    /// <see cref="TryTravelByTaxi(WorldSession, Player, uint, Vector3, PlayerbotOptions)"/> with the fare held to at most
    /// <paramref name="maxFare"/> copper as well as the bot's money (a journey's spend left, <see cref="TravelJourney.SpendLeft"/>): a route
    /// that costs more is not flown, and the goal walks. 0 flies nothing. A trip already under way answers as there.
    /// </summary>
    internal static TaxiTravel TryTravelByTaxi(WorldSession session, Player player, uint mapId, Vector3 destination, PlayerbotOptions options, uint maxFare)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Taxi.Enabled) return TaxiTravel.Walk;
        if (IsTravelTrip(player)) return TaxiTravel.Traveling;
        if (IsActive(player)) return TaxiTravel.Walk; // a learning detour owns the bot; the goal walks meanwhile
        if (!TryPlanTaxi(session, player, mapId, destination, options, maxFare, out uint[] route, out uint fare, out CreatureSpawn master))
            return TaxiTravel.Walk;
        Trips.AddOrUpdate(player, new TaxiTrip(mapId, route, fare, master.Entry, master.Guid, new Vector3(master.X, master.Y, master.Z), destination, session.World.NowMs));
        return TaxiTravel.Traveling;
    }

    /// <param name="maxFare">The most the fare may cost on top of the bot's money (uint.MaxValue: only the money bounds it).</param>
    private static bool TryPlanTaxi(WorldSession session, Player player, uint mapId, Vector3 destination, PlayerbotOptions options, uint maxFare,
        out uint[] route, out uint fare, out CreatureSpawn master)
    {
        route = [];
        fare = 0;
        master = default!;
        PlayerbotTaxiOptions taxi = options.Taxi;
        if (!player.IsInWorld || !player.IsAlive || player.Map is not { } map || map.MapId != mapId || player.Combat.IsInCombat || IsFlying(player)
            || PlayerbotHearthstone.IsActive(player) || PlayerbotMageService.IsActive(player)
            || player.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0)
            return false;
        uint now = session.World.NowMs;
        Vector3 here = new(player.X, player.Y, player.Z);
        if (!here.IsFinite() || !destination.IsFinite()) return false;
        float straight = Vector3.Distance(here, destination);
        if (straight < taxi.MinRouteYards) return false;
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || services.StateOf(player) is not { Loaded: true } state)
            return false;
        TaxiIndex index = TaxiIndex.Of(services.Npcs);
        bool alliance = player.Team == Team.Alliance;

        // The departure: the known node nearest the bot, close enough to walk to, and not backed off after a failed trip from it (another
        // known node may still fly). A node farther than the walk radius is not considered here; TryFartherDeparture does, and only when
        // this search finds nobody.
        TaxiNode? source = null;
        float nearest = taxi.MaxWalkToMasterYards;
        foreach (TaxiNode node in index.All)
        {
            if (node.MapId != mapId || (alliance ? node.MountAlliance : node.MountHorde) == 0 || !TaxiIndex.IsKnown(state.TaxiMask, node.Id)) continue;
            if (IsBackedOff(player, node.Id, now)) continue;
            float walk = Vector3.Distance(here, new Vector3(node.X, node.Y, node.Z));
            if (walk <= nearest) { source = node; nearest = walk; }
        }

        Memo cache = Memos.GetOrCreateValue(player);
        // What the fare may cost: the money, and a journey's spend left (the cache is keyed by this budget, not the money alone).
        uint budget = Math.Min(player.Money, maxFare);
        if (source is not null && TryDeparture(session, player, services, index, cache, state, mapId, source, here, destination, budget, taxi.MaxWalkToMasterYards, straight, options, out route, out fare, out master))
            return true;
        // A nearby node that flies but has no master the bot may approach is a dead end: the report already went out. A nearby node with
        // no route (or no nearby node at all) still leaves a farther known node, which flies when the win test says so.
        if (route.Length > 0) return false;

        // No known node inside the walk radius (quest 6362: the Crossroads wind rider is ~600 yards off while Thunder Bluff is a couple of
        // thousand away). The win test is still the one TryRoute applies — the walk to the node plus the walk from the landing at most half
        // the straight line — so a flight master beyond the radius is spoken to when flying is that clear a win, and walked past otherwise.
        return TryFartherDeparture(session, player, services, index, cache, state, alliance, mapId, here, destination, budget, taxi.MaxWalkToMasterYards, straight, options, now, out route, out fare, out master);
    }

    /// <summary>
    /// A known departure farther than <paramref name="maxWalk"/> whose flight <see cref="TaxiIndex.TryRoute"/> still calls a win, nearest first.
    /// False when none is (the caller walks). A route whose master the bot may not approach is reported once, as <see cref="TryDeparture"/> does.
    /// </summary>
    private static bool TryFartherDeparture(WorldSession session, Player player, QuestNpcServices services, TaxiIndex index, Memo cache, PlayerNpcState state,
        bool alliance, uint mapId, Vector3 here, Vector3 destination, uint budget, float maxWalk, float straight, PlayerbotOptions options, uint now,
        out uint[] route, out uint fare, out CreatureSpawn master)
    {
        route = [];
        fare = 0;
        master = default!;
        float half = straight * 0.5f;
        var departures = new List<(TaxiNode Node, float Walk)>();
        foreach (TaxiNode node in index.All)
        {
            if (node.MapId != mapId || (alliance ? node.MountAlliance : node.MountHorde) == 0 || !TaxiIndex.IsKnown(state.TaxiMask, node.Id)) continue;
            if (IsBackedOff(player, node.Id, now)) continue;
            float walk = Vector3.Distance(here, new Vector3(node.X, node.Y, node.Z));
            // A walk longer than half the trip can never pass TryRoute's win test, and every farther node is worse.
            if (walk > half) continue;
            departures.Add((node, walk));
        }

        departures.Sort(static (a, b) =>
        {
            int byWalk = a.Walk.CompareTo(b.Walk);
            return byWalk != 0 ? byWalk : a.Node.Id.CompareTo(b.Node.Id);
        });
        uint missingMaster = 0;
        uint[] missingRoute = [];
        foreach ((TaxiNode node, _) in departures)
        {
            if (!TryDeparture(session, player, services, index, cache, state, mapId, node, here, destination, budget, maxWalk, straight, options, out route, out fare, out master, reportMissingMaster: false))
            {
                if (route.Length > 0 && missingMaster == 0)
                {
                    missingMaster = node.Id;
                    missingRoute = route;
                }

                continue;
            }

            return true;
        }

        if (missingMaster != 0 && cache.MasterMissingNode != missingMaster)
        {
            cache.MasterMissingNode = missingMaster;
            PlayerbotAnomalies.Report(player, Area, "master-not-found",
                $"a taxi route {string.Join('>', missingRoute)} is known and affordable, but no flight master spawn the bot may approach maps to departure node {missingMaster}");
        }

        route = [];
        fare = 0;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="source"/> flies to a known landing near <paramref name="destination"/> the bot can pay for, and a flight master
    /// of that node the bot may approach exists. <paramref name="reportMissingMaster"/> reports a route with no such master once per node
    /// (the within-radius search); a farther search reports the nearest such node itself after every candidate has been tried.
    /// </summary>
    private static bool TryDeparture(WorldSession session, Player player, QuestNpcServices services, TaxiIndex index, Memo cache, PlayerNpcState state,
        uint mapId, TaxiNode source, Vector3 here, Vector3 destination, uint budget, float maxWalk, float straight, PlayerbotOptions options,
        out uint[] route, out uint fare, out CreatureSpawn master, bool reportMissingMaster = true)
    {
        route = [];
        fare = 0;
        master = default!;
        if (!cache.TryCached(state.TaxiMask, source.Id, destination, here, budget, index, maxWalk, out bool routed, out route, out fare))
        {
            routed = index.TryRoute(state.TaxiMask, source, here, destination, budget, maxWalk, straight, out route, out fare);
            cache.Store(state.TaxiMask, source.Id, destination, here, budget, index, maxWalk, routed, route, fare);
        }

        if (!routed)
        {
            route = [];
            fare = 0;
            return false;
        }

        if (FindMasterSpawn(session, player, services, index, mapId, source.Id, here) is not { } found)
        {
            // A known, affordable route whose departure has no flight master the bot may approach: reported once per node, not per plan.
            if (reportMissingMaster && cache.MasterMissingNode != source.Id)
            {
                cache.MasterMissingNode = source.Id;
                PlayerbotAnomalies.Report(player, Area, "master-not-found",
                    $"a taxi route {string.Join('>', route)} is known and affordable, but no flight master spawn the bot may approach maps to departure node {source.Id}");
            }

            return false;
        }

        if (!CanReachMaster(player, new Vector3(found.X, found.Y, found.Z), options))
        {
            route = [];
            fare = 0;
            return false;
        }

        cache.MasterMissingNode = 0;
        master = found;
        return true;
    }

    private static bool CanReachMaster(Player player, Vector3 masterPos, PlayerbotOptions options)
    {
        if (PlayerbotNavigation.IsUnreachableOnFoot(player, masterPos, out _, out _))
            return false;
        if (player.Map is { } map && map.Collision.MeshPoints is not null)
        {
            TileWait retry = default;
            PlanOutcome outcome = PlayerbotNavigation.PlanToward(player, masterPos, options, ref retry, 0, out PlayerbotRoute? route);
            if (outcome == PlanOutcome.Pending)
                return true;
            if (outcome != PlanOutcome.Route || route is null)
                return false;
            bool reachesMaster = route.Points.Count > 0
                && Vector2.Distance(new Vector2(route.Points[^1].X, route.Points[^1].Y), new Vector2(masterPos.X, masterPos.Y)) <= AtSpawnYards
                && MathF.Abs(route.Points[^1].Z - masterPos.Z) <= PlayerbotNavigation.ProjectedEndYards;
            if (!reachesMaster && (route.PartialToGoal || route.MeshEndedShort) && route.Distance < 50f)
                return false;
        }

        return true;
    }

    /// <summary>
    /// One think of the trip, before anything else the brain does. <see cref="TaxiStep.Idle"/>: no trip. <see cref="TaxiStep.Traveling"/>:
    /// walking to the flight master or in the air. <see cref="TaxiStep.Arrived"/>, <see cref="TaxiStep.Failed"/> and <see cref="TaxiStep.MasterDead"/> are reported once;
    /// <paramref name="failure"/> then says why.
    /// </summary>
    internal static TaxiStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        failure = null;
        if (!Trips.TryGetValue(player, out TaxiTrip? trip) || trip.Stage is TaxiStage.Done or TaxiStage.Failed) return TaxiStep.Idle;
        if (!player.IsInWorld || player.Map is not { } map) return TaxiStep.Traveling;
        if (!player.IsAlive)
        {
            if (trip.Stage == TaxiStage.Flying) trip.Interrupted = true;
            Cancel(player);
            return TaxiStep.Idle;
        }

        uint now = session.World.NowMs;
        if (trip.Stage == TaxiStage.Flying)
        {
            if (IsFlying(player))
            {
                if (unchecked((int)(now - trip.NextWatchMs)) >= 0) WatchFlight(session, player, trip, now);
                return TaxiStep.Traveling;
            }

            if (session.Services.GetService<NpcServicesFeature>()?.Flights?.TryConsumeAborted(player) == true)
            {
                trip.Interrupted = true;
                trip.Stage = TaxiStage.Failed;
                trip.Failure = "taxi: the flight was aborted before reaching its destination";
                failure = trip.Failure;
                Trips.Remove(player);
                return TaxiStep.Failed;
            }

            trip.Stage = TaxiStage.Done;
            Memos.GetOrCreateValue(player).Failures = 0;
            PlayerbotTransitBackoff.Clear(player, TransitKind.Taxi, trip.Nodes[0]);
            CheckLanding(session, player, trip);
            return TaxiStep.Arrived;
        }

        if (!options.Taxi.Enabled)
        {
            Cancel(player);
            return TaxiStep.Idle;
        }

        if (map.MapId != trip.MapId)
            return Fail(session, player, trip, options, now, "map-changed", AnomalySeverity.Info, $"left for map {map.MapId} on the way to the flight master", out failure);
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || services.StateOf(player) is not { } state)
            return Fail(session, player, trip, options, now, "services-unavailable", AnomalySeverity.Warning, "the NPC services are not available", out failure);
        TaxiIndex index = TaxiIndex.Of(services.Npcs);
        PlayerbotThinkProbe.Phase("taxi");
        if (VisibleMaster(player, services, index, trip.Nodes[0], session.Services.GetService<CreatureWorldFeature>()?.Content) is { } master)
            return Board(session, player, trip, options, state, index, master, now, out failure);

        Vector3 here = new(player.X, player.Y, player.Z);
        if (Vector3.Distance(here, trip.Master) <= AtSpawnYards)
        {
            // A master that was killed is normal play: no anomaly, no failure counted, the node held off until about its respawn.
            if (MasterRespawnWaitMs(map, trip.MasterSpawn) is { } respawnWaitMs)
                return MasterDead(session, player, trip, options, now, respawnWaitMs, out failure);
            return Fail(session, player, trip, options, now, "master-missing", AnomalySeverity.Warning,
                "no flight master of the departure node can be talked to where it stands", out failure);
        }

        if (!Walk(session, player, trip, options, elapsedMs, now))
        {
            return Fail(session, player, trip, options, now, "master-unreachable", AnomalySeverity.Warning,
                trip.RefusedPlans >= MaxRefusedPlans ? $"no way to the flight master (the path was refused {MaxRefusedPlans} times in a row)" : "no way to the flight master",
                out failure);
        }

        return TaxiStep.Traveling;
    }

    private static TaxiStep Board(WorldSession session, Player player, TaxiTrip trip, PlayerbotOptions options, PlayerNpcState state, TaxiIndex index, NpcInfo master,
        uint now, out string? failure)
    {
        failure = null;
        if (trip.LearnOnly) return Learn(session, player, trip, options, state, master, now, out failure);
        if (!TaxiIndex.IsKnown(state.TaxiMask, trip.Nodes[0]))
            return Fail(session, player, trip, options, now, "node-forgotten", AnomalySeverity.Info, "the departure node is no longer known", out failure);
        if (player.Money < trip.Fare)
            return Fail(session, player, trip, options, now, "cannot-pay", AnomalySeverity.Info, "the bot can no longer pay the fare", out failure);
        // A client reports itself standing before it asks for a flight (ERR_TAXIPLAYERMOVING / NOTSTANDING).
        if (!PlayerbotMovementControl.Stop(session, player))
        {
            return ++trip.StopAttempts > MaxStopAttempts
                ? Fail(session, player, trip, options, now, "stop-refused", AnomalySeverity.Warning, "the bot could not be brought to a stop before the flight request", out failure)
                : TaxiStep.Traveling;
        }

        WorldOpcode opcode = trip.Nodes.Length == 2 ? WorldOpcode.CmsgActivatetaxi : WorldOpcode.CmsgActivatetaxiexpress;
        // A reply nobody read (a flight another controller asked for) must not be taken for the answer to this request.
        session.DrainManagedPackets(WorldOpcode.SmsgActivatetaxireply);
        if (!session.TryManagedAction(opcode, ActivatePayload(master.Guid, trip.Nodes, trip.Fare)))
        {
            // A spent per-tick budget is retried on the next think; a closed or settling session, or a bot not in the world, is not.
            if (session.ManagedBudget is { Remaining: <= 0 })
            {
                return ++trip.Attempts > MaxActivateAttempts
                    ? Fail(session, player, trip, options, now, "budget-starved", AnomalySeverity.Warning, "the action budget never allowed the flight request", out failure)
                    : TaxiStep.Traveling;
            }

            return Fail(session, player, trip, options, now, "session-refused", AnomalySeverity.Warning,
                "the session refused the flight request (closed, settling a quest reward, or not in the world)", out failure);
        }

        IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.SmsgActivatetaxireply);
        if (!IsFlying(player))
        {
            foreach (ManagedSessionPacket packet in replies)
            {
                if (packet.Payload.Length >= 4 && BitConverter.ToUInt32(packet.Payload, 0) != (uint)ActivateTaxiReply.Ok)
                {
                    var reply = (ActivateTaxiReply)BitConverter.ToUInt32(packet.Payload, 0);
                    // A bot that is busy (in combat) when it asks is an expected, counted refusal; any other refusal is the server saying no to a request the bot checked.
                    return Fail(session, player, trip, options, now, "activate-refused",
                        reply == ActivateTaxiReply.PlayerBusy ? AnomalySeverity.Info : AnomalySeverity.Warning, $"the server refused the flight: {reply}", out failure);
                }
            }

            return Fail(session, player, trip, options, now, "flight-not-started", AnomalySeverity.Warning, "the server did not start the flight", out failure);
        }

        trip.Stage = TaxiStage.Flying;
        trip.Route = null;
        BeginFlightWatch(player, trip, index, now);
        return TaxiStep.Traveling;
    }

    /// <summary>
    /// The end of a learning detour (<see cref="TaxiTrip.LearnOnly"/>): the master of the node is in reach; ask it (CMSG_TAXIQUERYAVAILABLENODES, the
    /// real handler) and check the node is known afterwards. No flight. The budget may refuse the request a few thinks (<see cref="MaxActivateAttempts"/>).
    /// </summary>
    private static TaxiStep Learn(WorldSession session, Player player, TaxiTrip trip, PlayerbotOptions options, PlayerNpcState state, NpcInfo master, uint now,
        out string? failure)
    {
        failure = null;
        uint node = trip.Nodes[0];
        if (!TaxiIndex.IsKnown(state.TaxiMask, node))
        {
            if (!session.TryManagedAction(WorldOpcode.CmsgTaxiqueryavailablenodes, GuidPayload(master.Guid)))
            {
                if (session.ManagedBudget is { Remaining: <= 0 })
                {
                    return ++trip.Attempts > MaxActivateAttempts
                        ? Fail(session, player, trip, options, now, "budget-starved", AnomalySeverity.Warning, "the action budget never allowed the node query", out failure)
                        : TaxiStep.Traveling;
                }

                return Fail(session, player, trip, options, now, "session-refused", AnomalySeverity.Warning,
                    "the session refused the node query (closed, settling a quest reward, or not in the world)", out failure);
            }

            if (!TaxiIndex.IsKnown(state.TaxiMask, node))
                return Fail(session, player, trip, options, now, "node-not-learned", AnomalySeverity.Warning, $"the flight master did not teach node {node}", out failure);
        }

        trip.Stage = TaxiStage.Done;
        trip.Route = null;
        PlayerbotMovementControl.Stop(session, player);
        PlayerbotTransitBackoff.Clear(player, TransitKind.Taxi, node);
        return TaxiStep.Arrived;
    }

    /// <summary>Record when and where the flight began and how long it may take (the straight lines between the route's nodes at the slowest expected speed, plus a grace).</summary>
    private static void BeginFlightWatch(Player player, TaxiTrip trip, TaxiIndex index, uint now)
    {
        float length = 0f;
        TaxiNode? previous = null;
        foreach (uint id in trip.Nodes)
        {
            TaxiNode? node = index.Node(id);
            if (node is not null && previous is not null) length += Vector3.Distance(new Vector3(previous.X, previous.Y, previous.Z), new Vector3(node.X, node.Y, node.Z));
            previous = node ?? previous;
        }

        trip.FlightStartMs = now;
        trip.FlightBudgetMs = FlightGraceMs + (uint)Math.Min(length / SlowestFlightYardsPerSecond * 1000f, 3_600_000f);
        trip.NextWatchMs = unchecked(now + WatchIntervalMs);
        trip.SampleMs = now;
        trip.SamplePosition = new Vector3(player.X, player.Y, player.Z);
    }

    /// <summary>
    /// One look at a bot in the air, every <see cref="WatchIntervalMs"/>: a finite position, the flag matched by a flight the server holds,
    /// the bot moving, and the flight inside its time budget. Each anomaly is reported once per trip. Cost on a bot in the air: one
    /// comparison per think, and a few float operations every five seconds.
    /// </summary>
    private static void WatchFlight(WorldSession session, Player player, TaxiTrip trip, uint now)
    {
        trip.NextWatchMs = unchecked(now + WatchIntervalMs);
        if (session.IsInChaosProbe) trip.Interrupted = true;
        Vector3 here = new(player.X, player.Y, player.Z);
        if (!here.IsFinite())
        {
            if (!trip.NonFiniteReported)
            {
                trip.NonFiniteReported = true;
                PlayerbotAnomalies.Report(player, Area, "flight-nonfinite-position", $"the bot's position is not finite in flight (route {string.Join('>', trip.Nodes)})", AnomalySeverity.Error);
            }

            return;
        }

        uint sinceSample = unchecked(now - trip.SampleMs);
        if (!trip.StallReported && sinceSample >= WatchIntervalMs && Vector3.Distance(here, trip.SamplePosition) < StalledYards)
        {
            trip.StallReported = true;
            PlayerbotAnomalies.Report(player, Area, "flight-stalled",
                $"the flight is on but the bot moved {Vector3.Distance(here, trip.SamplePosition):F2} yards in {sinceSample} ms (route {string.Join('>', trip.Nodes)})");
        }

        trip.SampleMs = now;
        trip.SamplePosition = here;

        if (!trip.FlagReported && session.Services.GetService<NpcServicesFeature>()?.Flights is { } flights && !flights.IsFlying(player))
        {
            trip.FlagReported = true;
            PlayerbotAnomalies.Report(player, Area, "flag-without-flight",
                $"the taxi flight flag is set but the flight system holds no flight for the bot: it will never land (route {string.Join('>', trip.Nodes)})", AnomalySeverity.Error);
        }

        uint inFlightMs = unchecked(now - trip.FlightStartMs);
        if (!trip.OverdueReported && inFlightMs > trip.FlightBudgetMs)
        {
            trip.OverdueReported = true;
            PlayerbotAnomalies.Report(player, Area, "flight-overdue",
                $"still in the air {inFlightMs} ms after take-off, budget {trip.FlightBudgetMs} ms (route {string.Join('>', trip.Nodes)})");
        }
    }

    /// <summary>
    /// The bot is out of the air after a flight this trip began: did it land where the route ends, in the world, with the flight state
    /// cleared? Skipped after a death or a chaos probe in flight (the landing then says nothing about the flight). Once per landing.
    /// </summary>
    private static void CheckLanding(WorldSession session, Player player, TaxiTrip trip)
    {
        if (session.IsInChaosProbe) trip.Interrupted = true;
        if ((player.UnitFlags & UnitFlags.RemoveClientControl) != 0
            || session.Services.GetService<NpcServicesFeature>()?.Flights?.IsFlying(player) == true)
            PlayerbotAnomalies.Report(player, Area, "flight-state-residue", $"the flight ended but the bot is still without client control or held by the flight system (route {string.Join('>', trip.Nodes)})");
        if (trip.Interrupted) return;
        Vector3 here = new(player.X, player.Y, player.Z);
        if (!here.IsFinite())
        {
            PlayerbotAnomalies.Report(player, Area, "landed-nonfinite", $"the bot landed at a non-finite position (route {string.Join('>', trip.Nodes)})", AnomalySeverity.Error);
            return;
        }

        if (player.MapId != trip.MapId)
        {
            PlayerbotAnomalies.Report(player, Area, "landed-wrong-map", $"the flight began on map {trip.MapId} and the bot landed on map {player.MapId}");
            return;
        }

        if (session.Services.GetService<QuestNpcFeature>()?.Services?.Npcs is not { } npcs || TaxiIndex.Of(npcs).Node(trip.Nodes[^1]) is not { } last) return;
        Vector3 landing = new(last.X, last.Y, last.Z);
        if (here.Z < landing.Z - BelowNodeYards)
        {
            PlayerbotAnomalies.Report(player, Area, "landed-under-map",
                $"the bot landed {landing.Z - here.Z:F0} yards below landing node {last.Id} (route {string.Join('>', trip.Nodes)})", AnomalySeverity.Error);
            return;
        }

        float off = Vector3.Distance(here, landing);
        if (off > LandingToleranceYards)
            PlayerbotAnomalies.Report(player, Area, "landed-off-node", $"the bot landed {off:F0} yards from landing node {last.Id} (route {string.Join('>', trip.Nodes)})");
    }

    /// <summary>The shortest hold of a node whose master is dead (a respawn already due is picked up at the next planning).</summary>
    internal const uint MasterDeadMinHoldMs = 5_000;

    /// <summary>
    /// Milliseconds until the creature of flight master spawn <paramref name="spawnGuid"/> is back, when it was killed: dead or a corpse in the
    /// map's creature system, or gone from the map with its respawn time kept (<see cref="CreatureMapSystem.PendingRespawnAt"/>). Null when the
    /// spawn's creature is alive, the map knows nothing of the spawn (a master that is truly absent), or the respawn is already due (a
    /// master that should be back and is not: a link or pool holds it, or the timer is overdue; that stays a "master-missing" report). A
    /// creature that never respawns reports a very long wait. World thread.
    /// </summary>
    internal static long? MasterRespawnWaitMs(Map map, uint spawnGuid)
    {
        if (map.FindUpdater<CreatureMapSystem>() is not { } system) return null;
        long? wait = system.FindBySpawnGuid(spawnGuid) is { } creature
            ? creature.DeathState == CreatureDeathState.Alive ? null : system.RespawnRemainingMs(creature)
            : system.PendingRespawnAt(spawnGuid) is { } at ? at - system.ClockMs : null;
        return wait > 0 ? wait : null;
    }

    private static TaxiStep MasterDead(WorldSession session, Player player, TaxiTrip trip, PlayerbotOptions options, uint now, long respawnWaitMs,
        out string? failure)
    {
        PlayerbotMovementControl.Stop(session, player);
        trip.Failure = "taxi: the flight master of the departure node was killed and has not respawned";
        trip.Stage = TaxiStage.Failed;
        trip.Route = null;
        uint capMs = (uint)Math.Clamp(options.Taxi.BackoffMinutes, 1, 1440) * 60_000u;
        // Held until about the respawn, not counted as a failure (no memo, no repeated-failure, no growing wait).
        PlayerbotTransitBackoff.Defer(player, TransitKind.Taxi, trip.Nodes[0], now, (uint)Math.Clamp(respawnWaitMs, MasterDeadMinHoldMs, capMs));
        failure = trip.Failure;
        return TaxiStep.MasterDead;
    }

    private static TaxiStep Fail(WorldSession session, Player player, TaxiTrip trip, PlayerbotOptions options, uint now, string code, AnomalySeverity severity,
        string why, out string? failure)
    {
        PlayerbotMovementControl.Stop(session, player);
        trip.Failure = "taxi: " + why;
        trip.Stage = TaxiStage.Failed;
        trip.Route = null;
        Memo memo = Memos.GetOrCreateValue(player);
        // Only the departure node is backed off: a failed trip from it says nothing about another node's master.
        PlayerbotTransitBackoff.Fail(player, TransitKind.Taxi, trip.Nodes[0], now, options, (uint)Math.Clamp(options.Taxi.BackoffMinutes, 1, 1440) * 60_000u);
        failure = trip.Failure;
        PlayerbotAnomalies.Report(player, Area, code, $"{why} (route {string.Join('>', trip.Nodes)})", severity);
        if (++memo.Failures == RepeatedFailures)
            PlayerbotAnomalies.Report(player, Area, "repeated-failure", $"{RepeatedFailures} taxi trips failed in a row without a landing, last: {why}");
        return TaxiStep.Failed;
    }

    /// <summary>
    /// Walk towards the flight master's spawn; false when the bot got no <see cref="ProgressYards"/> closer within <see cref="NoProgressMs"/>, or
    /// the navigation refused <see cref="MaxRefusedPlans"/> plans in a row (<see cref="TaxiTrip.RefusedPlans"/>).
    /// </summary>
    private static bool Walk(WorldSession session, Player player, TaxiTrip trip, PlayerbotOptions options, uint elapsedMs, uint now)
    {
        float left = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), trip.Master);
        if (left + ProgressYards <= trip.Best)
        {
            trip.Best = left;
            trip.BestMs = now;
        }
        else if (unchecked(now - trip.BestMs) > NoProgressMs)
        {
            return false;
        }

        if (trip.Route is { Complete: false } route && PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now)) return true;
        trip.Route = null;
        // One path query per PlanRetryMs while none is found; the progress bound above ends it.
        if (unchecked((int)(trip.NextPlanMs - now)) > 0) return true;
        PlanOutcome planned = PlayerbotNavigation.PlanToward(player, trip.Master, options, ref trip.Tiles, now, out PlayerbotRoute? next);
        if (planned != PlanOutcome.Route || next is null)
        {
            // Pending (tiles still being read) and refused alike wait PlanRetryMs. Pending is no verdict; MaxRefusedPlans refusals in a row end
            // the walk (and back the node off) at once, the progress bound ends one that never gets going otherwise.
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            return planned != PlanOutcome.Refused || ++trip.RefusedPlans < MaxRefusedPlans;
        }

        trip.RefusedPlans = 0;
        trip.Route = next;
        PlayerbotNavigation.TryAdvance(session, next, options, elapsedMs, now);
        return true;
    }

    /// <summary>A visible, living flight master of <paramref name="node"/> the bot is within interaction range of and may talk to (the server's own check).</summary>
    private static NpcInfo? VisibleMaster(Player player, QuestNpcServices services, TaxiIndex index, uint node, CreatureContent? content)
    {
        if (services.Deps.Creatures is not { } lookup) return null;
        bool alliance = player.Team == Team.Alliance;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (!MayBeFlightMaster(content, guid)) continue;
            if (lookup.Find(player, guid, NpcFlags.FlightMaster) is not { IsAlive: true, IsGameObject: false } npc || (npc.NpcFlags & NpcFlags.FlightMaster) == 0) continue;
            if (index.Nearest(npc.MapId, npc.X, npc.Y, npc.Z, alliance) != node) continue;
            if (services.InteractableNpc(player, guid, NpcFlags.FlightMaster) is not null) return npc;
        }

        return null;
    }

    /// <summary>
    /// The spawn of a flight master of <paramref name="node"/> (the node the server maps it to) the bot may approach, nearest to the bot:
    /// from the creature content, so a master out of sight is found (the pattern of <see cref="PlayerbotTrainerDestinations"/>).
    /// </summary>
    private static CreatureSpawn? FindMasterSpawn(WorldSession session, Player player, QuestNpcServices services, TaxiIndex index, uint mapId, uint node,
        Vector3 here)
    {
        if (session.Services.GetService<CreatureWorldFeature>()?.Content is not { } content) return null;
        if (session.Services.GetService<QuestNpcFeature>()?.FactionTemplates is not { } factions) return null;
        MasterTable table = TableOf(content);
        bool alliance = player.Team == Team.Alliance;
        CreatureSpawn? best = null;
        float bestDist = float.PositiveInfinity;
        foreach ((CreatureSpawn spawn, uint faction) in table.Spawns)
        {
            if (spawn.MapId != mapId || !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
            if (index.Nearest(spawn.MapId, spawn.X, spawn.Y, spawn.Z, alliance) != node) continue;
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, faction)) continue;
            float dist = Vector3.Distance(here, new Vector3(spawn.X, spawn.Y, spawn.Z));
            if (dist < bestDist) { best = spawn; bestDist = dist; }
        }

        return best;
    }

    private static MasterTable TableOf(CreatureContent content)
        => MasterTables.TryGetValue(content, out MasterTable? cached) && cached.Version == content.DefinitionsVersion ? cached : BuildMasterTable(content);

    private static MasterTable BuildMasterTable(CreatureContent content)
    {
        var spawns = new List<(CreatureSpawn, uint)>();
        foreach (CreatureTemplate template in content.Templates)
        {
            if (((NpcFlags)template.NpcFlags & NpcFlags.FlightMaster) == 0) continue;
            foreach (uint mapId in content.MapsWithSpawns) spawns.AddRange(content.GetSpawns(mapId, template.Entry).Select(s => (s, template.Faction)));
        }

        var table = new MasterTable(content.DefinitionsVersion, spawns);
        MasterTables.AddOrUpdate(content, table);
        return table;
    }

    /// <summary>The cheap pre-filter of a visible guid: a creature whose template carries the flight master flag (a template the content lacks passes; the lookup decides).</summary>
    private static bool MayBeFlightMaster(CreatureContent? content, ObjectGuid guid)
        => guid.High == HighGuid.Unit
            && (content?.FindTemplate(guid.Entry) is not { } template || ((NpcFlags)template.NpcFlags & NpcFlags.FlightMaster) != 0);

    private static byte[] GuidPayload(ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }

    /// <summary>CMSG_ACTIVATETAXI (u64 master, u32 from, u32 to) for one hop, else CMSG_ACTIVATETAXIEXPRESS (u64 master, u32 total fare, u32 count, u32 nodes).</summary>
    internal static byte[] ActivatePayload(ObjectGuid master, IReadOnlyList<uint> nodes, uint fare)
    {
        var writer = new PacketWriter(16 + (nodes.Count * 4));
        writer.WriteUInt64(master.Value);
        if (nodes.Count == 2)
        {
            writer.WriteUInt32(nodes[0]);
            writer.WriteUInt32(nodes[1]);
        }
        else
        {
            writer.WriteUInt32(fare);
            writer.WriteUInt32((uint)nodes.Count);
            foreach (uint node in nodes) writer.WriteUInt32(node);
        }

        return writer.ToArray();
    }

}
