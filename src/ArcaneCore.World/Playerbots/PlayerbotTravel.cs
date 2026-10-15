using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>The life of one bot's journey (<see cref="PlayerbotTravel"/>).</summary>
internal enum JourneyState : byte
{
    /// <summary>Under way: a leg is begun, or the next one begins at the next think.</summary>
    Active,

    /// <summary>The bot stands within <see cref="PlayerbotTravel.ArriveYards"/> of the goal.</summary>
    Arrived,

    /// <summary>A leg failed, the one replan failed too (or found no route), or the journey ran past its bound. Reported once.</summary>
    Failed,

    /// <summary>A switch went off (<see cref="PlayerbotTravelOptions.Enabled"/> or the means of the next leg): ended at a safe point, not reported.</summary>
    SwitchedOff,
}

/// <summary>
/// One bot's journey to a far place: the goal, the planned legs (the planner's route, plus a final walk when the last leg ends short of the
/// goal), the leg under way and the handle of its driver. Owned by <see cref="PlayerbotTravel"/>'s per-bot table; mutable, world thread only.
/// Callers read it (<see cref="PlayerbotTravel.JourneyOf"/>) for inspection and tests.
/// </summary>
internal sealed class TravelJourney(TravelPlace goal, TravelRoute route, uint nowMs, uint deadlineMs, Func<WorldRuntime, IReadOnlyList<ShipRoute>> ships)
{
    public TravelPlace Goal { get; } = goal;

    /// <summary>The route as first planned (the GM command prints it).</summary>
    public TravelRoute Planned { get; } = route;

    /// <summary>The legs being driven: the planned ones, or those of the replan, plus a final walk when one was needed.</summary>
    public List<TravelLeg> Legs { get; private set; } = [.. route.Legs];

    public int LegIndex { get; internal set; }
    public JourneyState State { get; internal set; } = JourneyState.Active;
    public uint BeganMs { get; } = nowMs;

    /// <summary>The journey fails past this world time: twice the planned seconds plus <see cref="PlayerbotTravel.SlackMs"/>.</summary>
    public uint DeadlineMs { get; } = deadlineMs;

    /// <summary>Why the journey failed (null while under way and after an arrival).</summary>
    public string? Failure { get; internal set; }

    /// <summary>The one replan after a failed leg was made (a second failure ends the journey).</summary>
    public bool Replanned { get; internal set; }

    /// <summary>The legs of the replan, when one was made (inspection and tests).</summary>
    public TravelRoute? Replan { get; internal set; }

    /// <summary>The final walk to the goal was added after the last planned leg.</summary>
    public bool FinalWalkAdded { get; internal set; }

    internal readonly Func<WorldRuntime, IReadOnlyList<ShipRoute>> Ships = ships;
    internal readonly TravelExclusions Excluded = new();
    internal bool LegStarted;
    internal object? LegHandle;
    internal uint LegBeganMs;
    internal readonly JourneyWalk Walk = new();
    internal bool Returned;

    internal TravelLeg? Current => LegIndex < Legs.Count ? Legs[LegIndex] : null;

    internal void UseRoute(TravelRoute route)
    {
        Legs = [.. route.Legs];
        LegIndex = 0;
        ResetLeg();
    }

    internal void AddFinalWalk(TravelLeg walk)
    {
        Legs.Add(walk);
        FinalWalkAdded = true;
    }

    internal void ResetLeg()
    {
        LegStarted = false;
        LegHandle = null;
        Walk.Reset();
    }
}

/// <summary>
/// What a replan after a failed leg leaves out (<see cref="PlayerbotTravel.BuildFacts(WorldSession, Player, PlayerbotOptions, IReadOnlyList{ShipRoute}, TravelExclusions?)"/>):
/// the means or point that just failed, on top of the drivers' own backoffs. Owned by one journey; world thread only.
/// </summary>
internal sealed class TravelExclusions
{
    public HashSet<uint> Ships { get; } = [];
    public HashSet<ulong> Mages { get; } = [];
    public HashSet<uint> Teleports { get; } = [];
    public bool Hearthstone { get; set; }
    public bool Flights { get; set; }
}

/// <summary>
/// A walk leg of a journey: planned with <see cref="PlayerbotNavigation.PlanToward"/> and followed with <see cref="PlayerbotNavigation.TryAdvance"/>,
/// like <see cref="TravelWalk"/>, but a long stretch is first offered to the Deeprun Tram, a lift or a known flight path (as
/// <see cref="PlayerbotWorldDestinations"/> does), and a teleport trigger that refused the bot on the way counts as a refused plan
/// (<see cref="PlayerbotAreaTriggers.TryConsumeTeleportRefusal(Player, out PlayerbotAreaTriggers.TeleportRefusal)"/>). Given up after
/// <see cref="MaxRefusedPlans"/> refusals in a row or no <see cref="ProgressYards"/> gained in <see cref="NoProgressMs"/>. Owned by one journey;
/// mutable, world thread only.
/// </summary>
internal sealed class JourneyWalk
{
    internal const int MaxRefusedPlans = 3;
    internal const float ProgressYards = 5f;
    internal const uint NoProgressMs = 60_000;
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A stretch at least this long (straight line) is offered to the tram, a lift or a flight path before it is walked.</summary>
    internal const float TransitYards = 150f;

    /// <summary>What one step of the walk did.</summary>
    internal enum Outcome : byte { Walking, Yield, Failed }

    private PlayerbotRoute? _route;
    private TileWait _tiles;
    private int _refused;
    private uint _nextPlanMs;
    private float _best = float.PositiveInfinity;
    private uint _bestMs;
    private uint _startedMs;
    private bool _started;

    /// <summary>Refused plans in a row so far (tests).</summary>
    internal int Refused => _refused;

    internal void Reset()
    {
        _route = null;
        _tiles.Reset();
        _refused = 0;
        _best = float.PositiveInfinity;
        _started = false;
    }

    /// <summary>
    /// One step toward <paramref name="target"/>. <see cref="Outcome.Yield"/>: a tram, lift or taxi trip carries the bot (begun here or
    /// before); its driver in the brain moves it and the walk goes on after it. <see cref="Outcome.Failed"/> with <paramref name="why"/>.
    /// </summary>
    internal Outcome Step(WorldSession session, Player player, PlayerbotOptions options, Vector3 target, uint elapsedMs, uint now, out string? why)
    {
        why = null;
        if (PlayerbotTaxi.IsActive(player) || PlayerbotTram.IsTraveling(player) || PlayerbotElevator.IsTraveling(player))
        {
            // The trip's own driver owns the bot; the progress bound starts afresh after it.
            _route = null;
            _bestMs = now;
            _best = float.PositiveInfinity;
            return Outcome.Yield;
        }

        if (!_started)
        {
            _started = true;
            _startedMs = now;
            _bestMs = now;
            _nextPlanMs = now;
            PlayerbotAreaTriggers.ClearTeleportRefusal(player);
        }

        float left = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), target);
        if (left + ProgressYards <= _best)
        {
            _best = left;
            _bestMs = now;
        }
        else if (unchecked(now - _bestMs) > NoProgressMs)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"no progress for {NoProgressMs / 1000} s ({left:F1} yd left)");
            return Outcome.Failed;
        }

        // A teleport trigger on the way that did not take the bot (a walk never means to take one): the route led into it.
        if (PlayerbotAreaTriggers.TryConsumeTeleportRefusal(player, out PlayerbotAreaTriggers.TeleportRefusal refusal)
            && unchecked((int)(refusal.AtMs - _startedMs)) >= 0)
        {
            _route = null;
            _nextPlanMs = unchecked(now + PlanRetryMs);
            if (++_refused >= MaxRefusedPlans)
            {
                why = $"{_refused} refusals; the last: {refusal.Why}";
                return Outcome.Failed;
            }

            return Outcome.Walking;
        }

        if (_route is { Complete: false } route && PlayerbotNavigation.TryAdvance(session, route, options, Math.Max(1u, elapsedMs), now)) return Outcome.Walking;
        _route = null;
        if (unchecked((int)(_nextPlanMs - now)) > 0) return Outcome.Walking;

        PlayerbotRoute? walk = null;
        if (left >= TransitYards && player.Map is { } map)
        {
            if (PlayerbotTram.TryBegin(session, player, target)) return Outcome.Yield;
            if (PlayerbotElevator.TryBegin(session, player, target, options, out walk)) return Outcome.Yield;
            if (walk is null && PlayerbotTaxi.TryTravelByTaxi(session, player, map.MapId, target, options) == TaxiTravel.Traveling) return Outcome.Yield;
        }

        PlanOutcome planned = walk is not null ? PlanOutcome.Route : PlayerbotNavigation.PlanToward(player, target, options, ref _tiles, now, out walk);
        if (planned == PlanOutcome.Route && walk is not null)
        {
            _refused = 0;
            _route = walk;
            PlayerbotNavigation.TryAdvance(session, walk, options, Math.Max(1u, elapsedMs), now);
            return Outcome.Walking;
        }

        _nextPlanMs = unchecked(now + PlanRetryMs);
        if (planned == PlanOutcome.Pending || ++_refused < MaxRefusedPlans) return Outcome.Walking;
        why = string.Create(CultureInfo.InvariantCulture, $"{_refused} plans were refused ({left:F1} yd left)");
        return Outcome.Failed;
    }
}

/// <summary>
/// The journey driver of far travel (docs/areas/playbots.md, "Travel") and the <see cref="IPlayerbotTravel"/> service: a bot is given a far
/// place (<see cref="TryBegin"/>), the planner (<see cref="PlayerbotTravelPlanner"/>) chooses its legs from what the bot really has
/// (<see cref="BuildFacts(WorldSession, Player, PlayerbotOptions)"/>), and the journey begins each leg in turn on the leg's own driver: a walk
/// (driven here, with the tram, lifts and known flight paths taking over long stretches), a flight path (<see cref="PlayerbotTaxi"/>), a boat or
/// zeppelin (<see cref="PlayerbotShip"/>), the hearthstone (<see cref="PlayerbotHearthstone"/>), a mage's own Teleport or a portal asked of a mage
/// (<see cref="PlayerbotMageService"/>). The brain drives the leg drivers first and this journey after them (<see cref="Drive"/>): the journey
/// only walks and begins legs; nothing it begins is moved by it.
/// <list type="bullet">
/// <item>A walk leg right before a boat, zeppelin or portal leg ends within <see cref="HandOffYards"/> of its end (that leg's driver walks the
/// last yards to its dock or mage itself); a failure of such a walk hands off at once, and the next driver's failure is the leg's.</item>
/// <item>A failed leg (its driver reported it once) is replanned once from where the bot stands, leaving out the failed means or point on top
/// of the drivers' backoffs (<see cref="TravelExclusions"/>); a second failure, or a replan with no route, fails the journey: reported once
/// (anomaly area <see cref="AnomalyArea"/>, <c>leg-failed</c>). The journey is bounded (<see cref="DeadlineOf"/>, <c>overdue</c>). Nothing is
/// reported on arrival.</item>
/// <item>A driver that drops its leg silently (combat or a cancel before its safe point) has the leg begun again; in combat the journey yields
/// (<see cref="TravelLegStep.Idle"/>) so the brain fights. A switch turned off (<see cref="PlayerbotTravelOptions.Enabled"/>, or the means of the
/// next leg) ends the journey there without a report.</item>
/// </list>
/// Thread affinity: world thread, except <see cref="CanReachStatic"/>. Per-bot state lives in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed by the player; the service instance holds only its options and ship-route source.
/// </summary>
internal sealed class PlayerbotTravel(PlayerbotOptions options) : IPlayerbotTravel
{
    /// <summary>The anomaly area of a failed journey (subsystem <c>bot-travel</c>).</summary>
    internal const string AnomalyArea = "travel";

    /// <summary>A bot this close to the goal (on its map) has arrived.</summary>
    internal const float ArriveYards = 10f;

    /// <summary>A walk before a boat, zeppelin or portal leg hands off this close to that leg's start.</summary>
    internal const float HandOffYards = 100f;

    /// <summary>On top of twice the planned seconds, the time a journey may take before it is overdue.</summary>
    internal const uint SlackMs = 600_000;

    private static readonly ConditionalWeakTable<Player, TravelJourney> Journeys = [];

    // The ship routes CanReachStatic answers from: the classic routes of the transport system a journey last saw (immutable records).
    private static readonly ConditionalWeakTable<TransportSystem, object> Published = [];
    private static readonly object PublishedMarker = new();
    private static IReadOnlyList<ShipRoute> s_staticRoutes = [];

    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Where the boat and zeppelin routes come from (<see cref="PlayerbotShipNetwork.Routes(WorldRuntime)"/>; tests give another classification).</summary>
    internal Func<WorldRuntime, IReadOnlyList<ShipRoute>> ShipRoutes { get; init; } = PlayerbotShipNetwork.Routes;

    // --- the service ------------------------------------------------------------------------

    public bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (!_options.Travel.Enabled || !player.IsInWorld || player.Map is null || !Finite(to)) return false;
        TravelFacts facts = BuildFacts(session, player, _options, ShipRoutes(session.World), null);
        return PlayerbotTravelPlanner.Plan(facts, TravelPlace.Of(player), new TravelPlace(toMap, to)) is not null;
    }

    public bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why)
        => TryBegin(session, player, toMap, to, out why, out _);

    /// <summary>
    /// <see cref="IPlayerbotTravel.TryBegin"/>, with the route planned (null when the bot already stands at the goal: nothing to do, true).
    /// Refused: travel off, the bot dead or out of the world, the goal not finite or on a map bots may not walk, a leg of another trip under way,
    /// or no route.
    /// </summary>
    internal bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why, out TravelRoute? route)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        why = null;
        route = null;
        var goal = new TravelPlace(toMap, to);
        if (!_options.Travel.Enabled) return Refuse(out why, "travel is switched off");
        if (!player.IsInWorld || player.Map is null || !player.IsAlive) return Refuse(out why, "the bot is not alive in the world");
        if (!Finite(to)) return Refuse(out why, "the goal is not a finite place");
        if (!PlayerbotMapPolicy.Allows(_options.AllowedMaps, toMap, null, current: false)) return Refuse(out why, $"bots may not walk map {toMap}");
        if (Journeys.TryGetValue(player, out TravelJourney? current) && current.State == JourneyState.Active)
        {
            if (current.Goal.DistanceTo(goal) <= PlayerbotTravelPlanner.ArrivedYards)
            {
                route = current.Planned;
                return true;
            }

            if (LegUnderWay(player)) return Refuse(out why, "a leg of another journey is under way");
            Journeys.Remove(player);
        }
        else if (LegUnderWay(player))
        {
            return Refuse(out why, "a travel leg is under way");
        }

        TravelFacts facts = BuildFacts(session, player, _options, ShipRoutes(session.World), null);
        TravelPlace here = TravelPlace.Of(player);
        if (PlayerbotTravelPlanner.Plan(facts, here, goal) is not { } planned)
            return Refuse(out why, $"no route from map {here.MapId} to map {toMap} by the bot's own means");
        PublishStaticRoutes(session.World);
        if (planned.Legs.Count == 0) return true; // already there
        uint now = session.World.NowMs;
        Journeys.AddOrUpdate(player, new TravelJourney(goal, planned, now, DeadlineOf(planned, now), ShipRoutes));
        route = planned;
        return true;
    }

    public bool IsTraveling(Player player) => IsJourneying(player);

    // --- inspection -------------------------------------------------------------------------

    /// <summary>The bot's journey, under way or ended (inspection and tests).</summary>
    internal static TravelJourney? JourneyOf(Player player) => Journeys.TryGetValue(player, out TravelJourney? journey) ? journey : null;

    /// <summary>Whether a journey is under way.</summary>
    internal static bool IsJourneying(Player player)
        => Journeys.TryGetValue(player, out TravelJourney? journey) && journey.State == JourneyState.Active;

    /// <summary>
    /// Whether the bot is aboard a ship or in the middle of any travel leg (a ship, the hearthstone, a mage leg, a mage serving a portal
    /// request) or a journey: chaos probes and other movers keep off it.
    /// </summary>
    internal static bool IsMidLeg(Player player)
        => LegUnderWay(player) || PlayerbotMageService.IsServing(player) || IsJourneying(player);

    private static bool LegUnderWay(Player player)
        => PlayerbotShip.IsActive(player) || PlayerbotHearthstone.IsActive(player) || PlayerbotMageService.IsActive(player);

    /// <summary>The bound of a journey planned at <paramref name="nowMs"/>: twice its planned seconds plus <see cref="SlackMs"/>.</summary>
    internal static uint DeadlineOf(TravelRoute route, uint nowMs)
        => unchecked(nowMs + (uint)Math.Clamp(route.Seconds * 2_000.0 + SlackMs, SlackMs, int.MaxValue));

    // --- cancel -----------------------------------------------------------------------------

    /// <summary>Drop the journey (no report). Legs under way are left to their drivers (<see cref="CancelLegs"/> drops those too).</summary>
    internal static void Cancel(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Journeys.Remove(player);
    }

    /// <summary>
    /// Death, or a controller, a battleground or a bot-led group taking the bot over: the journey is dropped and every travel leg with it, by
    /// each driver's own rule (<see cref="PlayerbotShip.Cancel"/>: aboard, never put off at sea, its leg rides on to the next port;
    /// <see cref="PlayerbotHearthstone.Cancel"/> and <see cref="PlayerbotMageService.Cancel"/>: a running cast cancelled, a portal request
    /// withdrawn; <see cref="PlayerbotMageService.CancelMage"/>: a mage stops serving). No report.
    /// </summary>
    internal static void CancelLegs(WorldSession session, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        Journeys.Remove(player);
        PlayerbotShip.Cancel(player);
        PlayerbotHearthstone.Cancel(session, player);
        PlayerbotMageService.Cancel(session, player);
        PlayerbotMageService.CancelMage(session, player);
    }

    // --- facts ------------------------------------------------------------------------------

    /// <summary>The planner's facts about the bot now (<see cref="PlayerbotShipNetwork.Routes(WorldRuntime)"/> for its ships).</summary>
    internal static TravelFacts BuildFacts(WorldSession session, Player player, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        return BuildFacts(session, player, options, PlayerbotShipNetwork.Routes(session.World), null);
    }

    /// <summary>
    /// The planner's facts about the bot now, from its real state (world thread):
    /// <list type="bullet">
    /// <item>Faction, level and money; the spend cap the lesser of the money and <see cref="PlayerbotTravelOptions.MaxTravelSpendCopper"/>.</item>
    /// <item>The hearthstone when <see cref="PlayerbotHearthstone.IsReady"/> (its switch on, carried, off cooldown) and the bind point.</item>
    /// <item>The bot's own Teleports (<see cref="PlayerbotMageService.OwnTeleports"/>) and the portals mages near it offer
    /// (<see cref="PlayerbotPortalBoard.Offers"/>); both empty with <see cref="PlayerbotTravelOptions.MageService"/> off.</item>
    /// <item>The ships of <paramref name="allShips"/> the bot may ride (<see cref="PlayerbotShipNetwork.Filter"/>: faction and the Boats and
    /// Zeppelins switches), less a route one of whose docks is backed off for the bot (<see cref="PlayerbotShip.IsBackedOff"/>).</item>
    /// <item>Walkable: the same map, and, where the map's walking region graph answers (<see cref="NavMeshPathfinder.RegionGraph"/>, the check
    /// <see cref="PlayerbotNavigation.IsUnreachableOnFoot"/> makes for the bot), the same walkable component. A place the graph cannot place
    /// (no navmesh, the graph not built yet, a tile not read yet, a point at sea such as a ship's port, or a small fragment) counts as joined:
    /// the walk's own failure then replans. Memoised per place for this one set of facts.</item>
    /// <item>Flight (with <see cref="PlayerbotTaxiOptions.Enabled"/> on): a known departure node within
    /// <see cref="PlayerbotTaxiOptions.MaxWalkToMasterYards"/> of the start of a stretch of at least <see cref="PlayerbotTaxiOptions.MinRouteYards"/>,
    /// not backed off, and a known landing node the fare to which fits the spend cap (<see cref="TaxiIndex.TryRoute"/>, the search
    /// <see cref="PlayerbotTaxi.TryTravelByTaxi"/> makes); its time the walks at both ends plus the hops at 32 yards a second. Memoised per pair.</item>
    /// </list>
    /// <paramref name="excluded"/> leaves out what a failed leg just failed on. Cost: one pass over the ships and portal offers, the hearthstone
    /// and spellbook checks; the walk and flight answers are computed lazily as the planner asks for them.
    /// </summary>
    internal static TravelFacts BuildFacts(WorldSession session, Player player, PlayerbotOptions options, IReadOnlyList<ShipRoute> allShips,
        TravelExclusions? excluded)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allShips);
        bool alliance = PlayerbotShipNetwork.IsAlliance(player);
        uint money = player.Money;
        uint cap = Math.Min(money, options.Travel.MaxTravelSpendCopper);
        uint now = session.World.NowMs;

        bool hearth = excluded is not { Hearthstone: true } && PlayerbotHearthstone.IsReady(session, player, options);
        IReadOnlyList<OwnTeleport> teleports = PlayerbotMageService.OwnTeleports(session, player, options);
        if (excluded is { Teleports.Count: > 0 }) teleports = [.. teleports.Where(t => !excluded.Teleports.Contains(t.SpellId))];
        IReadOnlyList<PortalOffer> portals = PlayerbotPortalBoard.Offers(session, player, options);
        if (excluded is { Mages.Count: > 0 }) portals = [.. portals.Where(p => !excluded.Mages.Contains(p.MageGuid))];
        var ships = new List<ShipRoute>();
        foreach (ShipRoute route in PlayerbotShipNetwork.Filter(allShips, alliance, options))
        {
            if (excluded?.Ships.Contains(route.Entry) == true) continue;
            if (PlayerbotShip.IsBackedOff(player, route.Entry, fromA: true, now) || PlayerbotShip.IsBackedOff(player, route.Entry, fromA: false, now)) continue;
            ships.Add(route);
        }

        return new TravelFacts
        {
            Alliance = alliance,
            Level = player.Level,
            Money = money,
            SpendCap = cap,
            HearthReady = hearth,
            Bind = PlayerbotHearthstone.BindPlace(player),
            OwnTeleports = teleports,
            Portals = portals,
            Ships = ships,
            Walkable = WalkableFor(player),
            Flight = options.Taxi.Enabled && excluded is not { Flights: true } ? FlightFor(session, player, options, cap, now) : null,
        };
    }

    private static readonly PathOptions WalkingLocate = new()
    {
        Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true,
    };

    /// <summary>The walkable test of <see cref="BuildFacts(WorldSession, Player, PlayerbotOptions, IReadOnlyList{ShipRoute}, TravelExclusions?)"/>.</summary>
    private static Func<TravelPlace, TravelPlace, bool> WalkableFor(Player player)
    {
        if (player.Map?.Collision.Pathfinder is not NavMeshPathfinder nav) return static (a, b) => a.MapId == b.MapId;
        var components = new Dictionary<TravelPlace, (int Component, bool Decides)>();
        (int Component, bool Decides) Locate(TravelPlace place)
        {
            if (components.TryGetValue(place, out (int, bool) known)) return known;
            (int, bool) found = (-1, false);
            if (Finite(place.Position) && nav.RegionGraph(place.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes) is { } graph)
            {
                int region = nav.RegionAt(graph, place.Position, WalkingLocate);
                if (region >= 0 && graph.ComponentPolygons(region) > PlayerbotNavigation.FragmentPolygons) found = (graph.ComponentOf(region), true);
            }

            components[place] = found;
            return found;
        }

        return (a, b) =>
        {
            if (a.MapId != b.MapId) return false;
            (int ca, bool da) = Locate(a);
            (int cb, bool db) = Locate(b);
            return !da || !db || ca == cb;
        };
    }

    /// <summary>The flight estimate of <see cref="BuildFacts(WorldSession, Player, PlayerbotOptions, IReadOnlyList{ShipRoute}, TravelExclusions?)"/>, or null without the taxi data.</summary>
    private static Func<TravelPlace, TravelPlace, (float Seconds, uint Copper)?>? FlightFor(WorldSession session, Player player, PlayerbotOptions options,
        uint cap, uint now)
    {
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || services.StateOf(player) is not { Loaded: true } state) return null;
        TaxiIndex index = TaxiIndex.Of(services.Npcs);
        bool alliance = PlayerbotShipNetwork.IsAlliance(player);
        uint[] mask = [.. state.TaxiMask];
        float maxWalk = options.Taxi.MaxWalkToMasterYards;
        float minRoute = options.Taxi.MinRouteYards;
        var memo = new Dictionary<(TravelPlace, TravelPlace), (float, uint)?>();
        return (a, b) =>
        {
            if (a.MapId != b.MapId) return null;
            if (memo.TryGetValue((a, b), out (float, uint)? known)) return known;
            (float, uint)? found = null;
            float straight = Vector3.Distance(a.Position, b.Position);
            if (straight >= minRoute && Finite(a.Position) && Finite(b.Position))
            {
                TaxiNode? source = null;
                float nearest = maxWalk;
                foreach (TaxiNode node in index.All)
                {
                    if (node.MapId != a.MapId || (alliance ? node.MountAlliance : node.MountHorde) == 0 || !TaxiIndex.IsKnown(mask, node.Id)) continue;
                    if (PlayerbotTaxi.IsBackedOff(player, node.Id, now)) continue;
                    float walk = Vector3.Distance(a.Position, new Vector3(node.X, node.Y, node.Z));
                    if (walk <= nearest) { source = node; nearest = walk; }
                }

                if (source is not null && index.TryRoute(mask, source, a.Position, b.Position, cap, maxWalk, straight, out uint[] route, out uint fare))
                {
                    float hops = 0f;
                    for (int i = 1; i < route.Length; i++)
                    {
                        if (index.Node(route[i - 1]) is { } from && index.Node(route[i]) is { } to)
                            hops += Vector3.Distance(new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
                    }

                    TaxiNode landing = index.Node(route[^1])!;
                    float walkOff = Vector3.Distance(new Vector3(landing.X, landing.Y, landing.Z), b.Position);
                    float seconds = ((nearest + walkOff) * PlayerbotTravelPlanner.WalkDetour / PlayerbotTravelPlanner.WalkYardsPerSecond) + (hops / FlightYardsPerSecond);
                    found = (seconds, fare);
                }
            }

            memo[(a, b)] = found;
            return found;
        };
    }

    /// <summary>The speed of a taxi flight in 1.12 (yards per second).</summary>
    internal const float FlightYardsPerSecond = 32f;

    // --- off-thread reachability ----------------------------------------------------------------

    /// <summary>
    /// Off-thread reachability from static data only (for the home progression's sweep): the same map, or maps the boat and zeppelin routes a
    /// bot of the faction may ride join (<see cref="PlayerbotShipNetwork.ConnectsMaps"/>). The routes are the classic routes of the transport
    /// system the world thread last published (<see cref="PublishStaticRoutes"/>, at the first journey begun or driven in a world; immutable
    /// records, read with a volatile load); before that only the same map answers. Neither the Boats and Zeppelins switches nor any walk is
    /// judged here (no options and no navmesh off the world thread): a same-map answer is "maybe on foot", and the trip's own planner decides.
    /// Pure, allocation-light, any thread.
    /// </summary>
    internal static bool CanReachStatic(bool alliance, uint fromMap, Vector3 from, uint toMap, Vector3 to)
    {
        _ = from;
        _ = to;
        if (fromMap == toMap) return true;
        IReadOnlyList<ShipRoute> routes = Volatile.Read(ref s_staticRoutes);
        if (routes.Count == 0) return false;
        var allowed = new List<ShipRoute>(routes.Count);
        foreach (ShipRoute route in routes)
            if (PlayerbotShipNetwork.Admits(route.Access, alliance)) allowed.Add(route);
        return PlayerbotShipNetwork.ConnectsMaps(allowed, fromMap, toMap);
    }

    /// <summary>Publish the classic routes of <paramref name="world"/>'s transport system for <see cref="CanReachStatic"/> (world thread; once per system).</summary>
    internal static void PublishStaticRoutes(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (TransportSystem.Of(world) is not { } system || Published.TryGetValue(system, out _)) return;
        Published.AddOrUpdate(system, PublishedMarker);
        Volatile.Write(ref s_staticRoutes, PlayerbotShipNetwork.Routes(system));
    }

    // --- the driver -------------------------------------------------------------------------

    /// <summary>
    /// One think of the bot's journey, called by the brain after the leg drivers (<see cref="PlayerbotShip.Drive"/>,
    /// <see cref="PlayerbotHearthstone.Drive"/>, <see cref="PlayerbotMageService.Drive"/>, <see cref="PlayerbotMageService.DriveMage"/>) and before
    /// the taxi, tram and lift drivers. <see cref="TravelLegStep.Traveling"/>: the journey walked the bot or began a leg; nothing else moves it.
    /// <see cref="TravelLegStep.Idle"/>: no journey, or one that yields this think (in combat before a leg is under way, a tram, lift or taxi
    /// trip of a walk leg under way: those drivers run next; or one just ended by a switch). <see cref="TravelLegStep.Arrived"/> and
    /// <see cref="TravelLegStep.Failed"/> are answered once; <paramref name="failure"/> then says why.
    /// </summary>
    internal static TravelLegStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        failure = null;
        if (!Journeys.TryGetValue(player, out TravelJourney? journey)) return TravelLegStep.Idle;
        if (journey.State != JourneyState.Active) return Answer(journey, out failure);
        if (!player.IsInWorld || player.Map is null) return TravelLegStep.Traveling; // between maps (a teleport or a ship's map change)
        if (!player.IsAlive)
        {
            Journeys.Remove(player);
            return TravelLegStep.Idle;
        }

        PublishStaticRoutes(session.World);
        uint now = session.World.NowMs;
        if (!options.Travel.Enabled && !LegUnderWay(player))
        {
            End(session, player, journey, JourneyState.SwitchedOff, "travel was switched off", code: null);
            return Answer(journey, out failure);
        }

        if (unchecked((int)(now - journey.DeadlineMs)) > 0 && !LegUnderWay(player))
        {
            End(session, player, journey, JourneyState.Failed, string.Create(CultureInfo.InvariantCulture,
                $"the journey to {Describe(journey.Goal)} still runs {unchecked(now - journey.BeganMs) / 1000} s after it began (bound {unchecked(journey.DeadlineMs - journey.BeganMs) / 1000} s)"),
                "overdue");
            return Answer(journey, out failure);
        }

        PlayerbotThinkProbe.Phase("travel");
        // A few legs can end and begin within one think (a hand-off, a leg found done); each loop either returns or moves one leg on.
        for (int guard = 0; guard <= journey.Legs.Count + 2; guard++)
        {
            if (!journey.LegStarted && TravelPlace.Of(player).DistanceTo(journey.Goal) <= ArriveYards && !LegUnderWay(player))
            {
                End(session, player, journey, JourneyState.Arrived, null, null);
                return Answer(journey, out failure);
            }

            if (journey.Current is not { } leg)
            {
                // The last leg ended short of the goal: a final walk on the goal's map, once; anywhere else the leg failed.
                if (player.MapId == journey.Goal.MapId && !journey.FinalWalkAdded)
                {
                    journey.AddFinalWalk(new TravelLeg(TravelMeans.Walk, TravelPlace.Of(player), journey.Goal,
                        PlayerbotTravelPlanner.WalkSeconds(TravelPlace.Of(player), journey.Goal), 0));
                    continue;
                }

                return LegFailed(session, player, options, journey, TravelMeans.Walk, string.Create(CultureInfo.InvariantCulture,
                    $"the legs ended at {Describe(TravelPlace.Of(player))}, not at the goal {Describe(journey.Goal)}"), out failure);
            }

            StepResult result = journey.LegStarted ? Watch(session, player, options, journey, leg, elapsedMs, now, out string? why)
                : Begin(session, player, options, journey, leg, elapsedMs, now, out why);
            switch (result)
            {
                case StepResult.Next:
                    journey.LegIndex++;
                    journey.ResetLeg();
                    continue;
                case StepResult.Failed:
                    return LegFailed(session, player, options, journey, leg.Means, why!, out failure);
                case StepResult.SwitchedOff:
                    End(session, player, journey, JourneyState.SwitchedOff, why ?? $"{leg.Means} was switched off", code: null);
                    return Answer(journey, out failure);
                case StepResult.Yield:
                    return TravelLegStep.Idle;
                default:
                    return TravelLegStep.Traveling;
            }
        }

        return TravelLegStep.Traveling;
    }

    private enum StepResult : byte { Traveling, Yield, Next, Failed, SwitchedOff }

    /// <summary>Begin <paramref name="leg"/> on its driver (a walk is driven here at once).</summary>
    private static StepResult Begin(WorldSession session, Player player, PlayerbotOptions options, TravelJourney journey, TravelLeg leg, uint elapsedMs,
        uint now, out string? why)
    {
        why = null;
        if (SwitchOf(leg.Means, options) is { } off)
        {
            why = off;
            return StepResult.SwitchedOff;
        }

        // A fight comes first: the brain fights, and the leg begins after it.
        if (player.Combat.IsInCombat) return StepResult.Yield;
        journey.LegBeganMs = now;
        switch (leg.Means)
        {
            case TravelMeans.Walk:
            case TravelMeans.FlightPath:
                journey.LegStarted = true;
                return Walk(session, player, options, journey, leg, elapsedMs, now, out why);
            case TravelMeans.Boat:
            case TravelMeans.Zeppelin:
            {
                ShipRoute? route = null;
                foreach (ShipRoute candidate in journey.Ships(session.World))
                {
                    if (candidate.Entry == leg.TransportEntry) { route = candidate; break; }
                }

                if (route is null)
                {
                    why = $"ship {leg.TransportEntry} is no longer among the routes";
                    return StepResult.Failed;
                }

                bool fromA = route.PortA.DistanceTo(leg.From) <= PlayerbotShipNetwork.SamePortYards;
                if (!PlayerbotShip.TryBegin(session, player, route, fromA, options, out why)) return StepResult.Failed;
                journey.LegStarted = true;
                journey.LegHandle = PlayerbotShip.TripOf(player);
                return StepResult.Traveling;
            }

            case TravelMeans.Hearthstone:
                if (!PlayerbotHearthstone.TryBegin(session, player, options, out why)) return StepResult.Failed;
                journey.LegStarted = true;
                journey.LegHandle = PlayerbotHearthstone.LegOf(player);
                return StepResult.Traveling;
            case TravelMeans.MageTeleport:
                if (!PlayerbotMageService.TryBeginTeleport(session, player, leg.SpellId, options, out why)) return StepResult.Failed;
                journey.LegStarted = true;
                journey.LegHandle = PlayerbotMageService.LegOf(player);
                return StepResult.Traveling;
            case TravelMeans.MagePortal:
            {
                var offer = new PortalOffer(leg.MageGuid, leg.SpellId, leg.From, leg.To, leg.Copper);
                if (!PlayerbotMageService.TryBeginPortal(session, player, offer, options, out why)) return StepResult.Failed;
                journey.LegStarted = true;
                journey.LegHandle = PlayerbotMageService.LegOf(player);
                return StepResult.Traveling;
            }

            default:
                why = $"{leg.Means} is not a means the journey drives";
                return StepResult.Failed;
        }
    }

    /// <summary>A leg already begun: a walk steps on; a driver's leg is read off its handle (done, failed, or dropped and begun again).</summary>
    private static StepResult Watch(WorldSession session, Player player, PlayerbotOptions options, TravelJourney journey, TravelLeg leg, uint elapsedMs,
        uint now, out string? why)
    {
        why = null;
        switch (leg.Means)
        {
            case TravelMeans.Walk:
            case TravelMeans.FlightPath:
                if (SwitchOf(leg.Means, options) is { } off)
                {
                    PlayerbotMovementControl.Stop(session, player);
                    why = off;
                    return StepResult.SwitchedOff;
                }

                if (player.Combat.IsInCombat) return StepResult.Yield;
                return Walk(session, player, options, journey, leg, elapsedMs, now, out why);
            case TravelMeans.Boat:
            case TravelMeans.Zeppelin:
            {
                ShipTrip? trip = PlayerbotShip.TripOf(player);
                if (trip is null || !ReferenceEquals(trip, journey.LegHandle)) return Dropped(journey);
                if (trip.Stage == ShipStage.Done) return StepResult.Next;
                if (trip.Stage != ShipStage.Failed) return StepResult.Traveling;
                if (!PlayerbotShipNetwork.SwitchedOn(trip.Route.Means, options.Travel))
                {
                    why = trip.Failure;
                    return StepResult.SwitchedOff;
                }

                journey.Excluded.Ships.Add(trip.Route.Entry);
                why = trip.Failure ?? "ship: the leg failed";
                return StepResult.Failed;
            }

            case TravelMeans.Hearthstone:
            {
                TravelCastLeg? cast = PlayerbotHearthstone.LegOf(player);
                if (cast is null || !ReferenceEquals(cast, journey.LegHandle)) return Dropped(journey);
                if (cast.Stage == TravelCastStage.Done) return StepResult.Next;
                if (cast.Stage != TravelCastStage.Failed) return StepResult.Traveling;
                journey.Excluded.Hearthstone = true;
                why = cast.Failure ?? "hearth: the leg failed";
                return StepResult.Failed;
            }

            case TravelMeans.MageTeleport:
            case TravelMeans.MagePortal:
            {
                MageLeg? mage = PlayerbotMageService.LegOf(player);
                if (mage is null || !ReferenceEquals(mage, journey.LegHandle)) return Dropped(journey);
                if (mage.Stage == MageLegStage.Done) return StepResult.Next;
                if (mage.Stage != MageLegStage.Failed) return StepResult.Traveling;
                if (leg.Means == TravelMeans.MageTeleport) journey.Excluded.Teleports.Add(leg.SpellId);
                else journey.Excluded.Mages.Add(mage.MageGuid != 0 ? mage.MageGuid : leg.MageGuid);
                why = mage.Failure ?? "mage: the leg failed";
                return StepResult.Failed;
            }

            default:
                why = $"{leg.Means} is not a means the journey drives";
                return StepResult.Failed;
        }
    }

    /// <summary>A driver dropped its leg without an end (combat or a cancel before its safe point): it is begun again at the next think.</summary>
    private static StepResult Dropped(TravelJourney journey)
    {
        journey.ResetLeg();
        return StepResult.Yield;
    }

    /// <summary>One step of a walk (or flight-path) leg; a hand-off walk ends near the next leg's start, and its failure hands off too.</summary>
    private static StepResult Walk(WorldSession session, Player player, PlayerbotOptions options, TravelJourney journey, TravelLeg leg, uint elapsedMs,
        uint now, out string? why)
    {
        why = null;
        bool handOff = journey.LegIndex + 1 < journey.Legs.Count
            && journey.Legs[journey.LegIndex + 1].Means is TravelMeans.Boat or TravelMeans.Zeppelin or TravelMeans.MagePortal;
        if (player.MapId != leg.To.MapId)
        {
            if (handOff) return StepResult.Next;
            why = $"walk: on map {player.MapId}, the leg's end on map {leg.To.MapId}";
            return StepResult.Failed;
        }

        float left = TravelPlace.Of(player).DistanceTo(leg.To);
        if (left <= (handOff ? HandOffYards : ArriveYards) && !PlayerbotTaxi.IsActive(player) && !PlayerbotTram.IsTraveling(player)
            && !PlayerbotElevator.IsTraveling(player))
        {
            PlayerbotMovementControl.Stop(session, player);
            return StepResult.Next;
        }

        switch (journey.Walk.Step(session, player, options, leg.To.Position, elapsedMs, now, out string? walkWhy))
        {
            case JourneyWalk.Outcome.Yield:
                return StepResult.Yield;
            case JourneyWalk.Outcome.Failed:
                if (handOff) return StepResult.Next; // the next leg's own driver walks to its dock or mage, and its failure is the leg's
                why = "walk: " + walkWhy;
                return StepResult.Failed;
            default:
                return StepResult.Traveling;
        }
    }

    /// <summary>Why the means of a leg may not be used now (its switch is off), or null.</summary>
    private static string? SwitchOf(TravelMeans means, PlayerbotOptions options) => means switch
    {
        TravelMeans.FlightPath when !options.Taxi.Enabled => "flight paths were switched off",
        TravelMeans.Boat when !options.Travel.Boats => "boats were switched off",
        TravelMeans.Zeppelin when !options.Travel.Zeppelins => "zeppelins were switched off",
        TravelMeans.Hearthstone when !options.Travel.Hearthstone => "the hearthstone was switched off",
        TravelMeans.MageTeleport or TravelMeans.MagePortal when !options.Travel.MageService => "the mage service was switched off",
        _ => null,
    };

    /// <summary>
    /// A leg failed (its driver reported it, when it is a failure of the driver's): replan once from here without what failed; else the journey
    /// fails, reported once.
    /// </summary>
    private static TravelLegStep LegFailed(WorldSession session, Player player, PlayerbotOptions options, TravelJourney journey, TravelMeans means,
        string why, out string? failure)
    {
        failure = null;
        if (means == TravelMeans.FlightPath) journey.Excluded.Flights = true;
        if (!journey.Replanned)
        {
            journey.Replanned = true;
            TravelFacts facts = BuildFacts(session, player, options, journey.Ships(session.World), journey.Excluded);
            if (PlayerbotTravelPlanner.Plan(facts, TravelPlace.Of(player), journey.Goal) is { } route)
            {
                journey.Replan = route;
                journey.UseRoute(route);
                session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotTravel>()
                    .LogInformation("Playerbot {Bot} travel: a {Means} leg failed ({Why}); replanned {Route}", player.Name, means, why, Describe(route));
                return TravelLegStep.Traveling;
            }

            why += "; the replan from here found no route";
        }
        else
        {
            why += "; it was already replanned once";
        }

        End(session, player, journey, JourneyState.Failed, $"{means} leg: {why}", "leg-failed");
        return Answer(journey, out failure);
    }

    private static void End(WorldSession session, Player player, TravelJourney journey, JourneyState state, string? why, string? code)
    {
        journey.State = state;
        journey.Failure = state == JourneyState.Arrived ? null : "travel: " + why;
        journey.ResetLeg();
        if (code is not null && !session.IsInChaosProbe && player.IsInWorld && !player.IsLoggingOut)
        {
            PlayerbotAnomalies.Report(player, AnomalyArea, code, string.Create(CultureInfo.InvariantCulture,
                $"journey from {Describe(TravelPlace.Of(player))} to {Describe(journey.Goal)}: {why}"));
        }
    }

    /// <summary>The end of the journey, answered once: Arrived or Failed; a switch-off answers Idle.</summary>
    private static TravelLegStep Answer(TravelJourney journey, out string? failure)
    {
        failure = journey.State == JourneyState.Failed ? journey.Failure : null;
        if (journey.Returned) return TravelLegStep.Idle;
        journey.Returned = true;
        return journey.State switch
        {
            JourneyState.Arrived => TravelLegStep.Arrived,
            JourneyState.Failed => TravelLegStep.Failed,
            _ => TravelLegStep.Idle,
        };
    }

    private static bool Refuse(out string? why, string reason)
    {
        why = "travel: " + reason;
        return false;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static string Describe(TravelPlace place)
        => string.Create(CultureInfo.InvariantCulture, $"map {place.MapId} ({place.Position.X:F0},{place.Position.Y:F0},{place.Position.Z:F0})");

    /// <summary>One line per leg, for logs and the GM command.</summary>
    internal static string Describe(TravelRoute route)
        => route.Legs.Count == 0 ? "no legs (already there)"
            : string.Join(" -> ", route.Legs.Select(Describe)) + string.Create(CultureInfo.InvariantCulture, $" [{route.Seconds:F0} s, {route.Copper} copper]");

    internal static string Describe(TravelLeg leg)
    {
        string what = leg.Means switch
        {
            TravelMeans.Boat or TravelMeans.Zeppelin => string.Create(CultureInfo.InvariantCulture, $"{leg.Means} {leg.TransportEntry}"),
            TravelMeans.MageTeleport => string.Create(CultureInfo.InvariantCulture, $"MageTeleport {leg.SpellId}"),
            TravelMeans.MagePortal => string.Create(CultureInfo.InvariantCulture, $"MagePortal {leg.SpellId} by {leg.MageGuid:X}"),
            _ => leg.Means.ToString(),
        };
        return string.Create(CultureInfo.InvariantCulture, $"{what} to {Describe(leg.To)} ({leg.Seconds:F0} s{(leg.Copper > 0 ? $", {leg.Copper} c" : string.Empty)})");
    }
}
