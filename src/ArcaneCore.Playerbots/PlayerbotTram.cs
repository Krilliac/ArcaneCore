using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// One end of the Deeprun Tram, built from the world's area triggers (<see cref="TramNetwork.Build"/>): the entrance on the continent
/// (<c>areatrigger_template</c> + <c>areatrigger_teleport</c> to the tram map), the platform it lands on, and the exit trigger on the tram
/// map with its teleport back out beside the entrance. classic-db 5875: Stormwind 2173 → (67.8, 2491.0, -4.3), exit 2171 → (-8354.2, 524.1,
/// 91.8); Ironforge 2175 → (69.2, 10.4, -4.3), exit 2166 → (-4839.5, -1317.7, 501.9).
/// </summary>
internal sealed record TramPlatform(AreaTriggerTemplate Entrance, AreaTriggerTeleport EntranceTeleport, AreaTriggerTemplate Exit,
    AreaTriggerTeleport ExitTeleport)
{
    /// <summary>The trigger's name (the entrance teleport's, e.g. "Deeprun Tram - Stormwind Instance").</summary>
    public string Name => string.IsNullOrWhiteSpace(EntranceTeleport.Name) ? $"trigger {Entrance.Id}" : EntranceTeleport.Name;

    /// <summary>The continent the entrance and the way out lie on.</summary>
    public uint ContinentMap => Entrance.MapId;

    public Vector3 EntranceCentre => new(Entrance.X, Entrance.Y, Entrance.Z);

    /// <summary>How far inside the entrance volume's circle on the continent floor <see cref="Approach"/> stands.</summary>
    internal const float ApproachInsetYards = 2f;

    /// <summary>
    /// Where a bot walks to take this entrance: on the continent floor (the height <see cref="Arrival"/> puts a player at), inside the
    /// entrance volume, on the landing's side of it. The trigger centre is the middle of the instance volume. Ironforge's (2175) floats
    /// 6.3 yards above that floor, outside the pathfinder's near box (5 yards) and <see cref="PlayerbotNavigation.ProjectedEndYards"/> (6),
    /// so a way to the centre from the continent is not a walk onto a point the mesh will stand on (live 2026-10-12).
    /// </summary>
    public Vector3 Approach
    {
        get
        {
            Vector3 centre = EntranceCentre;
            Vector3 landing = Arrival;
            if (Entrance.Radius <= 0f) return centre;
            float rise = centre.Z - landing.Z;
            float circleSq = (Entrance.Radius * Entrance.Radius) - (rise * rise);
            if (circleSq <= 0.25f) return centre; // the recorded floor does not meet the volume
            float along = MathF.Max(0f, MathF.Sqrt(circleSq) - ApproachInsetYards);
            float dx = landing.X - centre.X, dy = landing.Y - centre.Y;
            float flat = MathF.Sqrt((dx * dx) + (dy * dy));
            if (flat < 0.5f) return new Vector3(centre.X, centre.Y, landing.Z);
            along = MathF.Min(along, MathF.Max(0f, flat - 0.25f));
            float scale = along / flat;
            return new Vector3(centre.X + (dx * scale), centre.Y + (dy * scale), landing.Z);
        }
    }

    /// <summary>Where the entrance puts a player on the tram map.</summary>
    public Vector3 Landing => new(EntranceTeleport.TargetX, EntranceTeleport.TargetY, EntranceTeleport.TargetZ);

    public Vector3 ExitCentre => new(Exit.X, Exit.Y, Exit.Z);

    /// <summary>Where the exit puts a player on the continent.</summary>
    public Vector3 Arrival => new(ExitTeleport.TargetX, ExitTeleport.TargetY, ExitTeleport.TargetZ);
}

/// <summary>
/// The Deeprun Tram as the world data describes it: map 369 (Map.dbc "DeeprunTram", vmangos <c>map_template</c> 369, a map of its own,
/// neither a continent nor a dungeon), its two platforms (<see cref="TramPlatform"/>) and the cars that shuttle between them (the six
/// GAMEOBJECT_TYPE_TRANSPORT "Subway" spawns 176080-176085, moved by TransportAnimation.dbc; <see cref="GameObjectMapSystem.MapWideElevators"/>).
/// Immutable; cached per trigger table (<see cref="Of"/>).
/// </summary>
internal sealed class TramNetwork
{
    private enum TramSide : byte { Unknown, Stormwind, Ironforge }

    /// <summary>The Deeprun Tram map.</summary>
    public const uint TramMapId = 369;

    /// <summary>An exit counts as an entrance's way back when it lands within this of the entrance (13 and 13 yards in classic-db).</summary>
    public const float PairYards = 60f;

    private static readonly ConditionalWeakTable<IReadOnlyCollection<AreaTriggerTemplate>, Box> Cache = [];

    private sealed class Box(TramNetwork? network)
    {
        public TramNetwork? Network { get; } = network;
    }

    private TramNetwork(TramPlatform a, TramPlatform b) => Platforms = [a, b];

    /// <summary>The two platforms, in entrance trigger id order.</summary>
    public IReadOnlyList<TramPlatform> Platforms { get; }

    public uint ContinentMap => Platforms[0].ContinentMap;

    /// <summary>The network of the world's current trigger table (null when the data does not describe one).</summary>
    public static TramNetwork? Of(WorldMaps maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        IReadOnlyCollection<AreaTriggerTemplate> triggers = maps.AreaTriggers;
        return Cache.GetValue(triggers, t => new Box(Build(t, maps.FindAreaTriggerTeleport))).Network;
    }

    /// <summary>
    /// The network the triggers describe, or null (fail closed): exactly two entrances on one continent lead to <see cref="TramMapId"/>, and
    /// each has its own exit on the tram map, the exit nearest the entrance's landing, whose teleport lands back within
    /// <see cref="PairYards"/> of that entrance.
    /// </summary>
    public static TramNetwork? Build(IEnumerable<AreaTriggerTemplate> triggers, Func<uint, AreaTriggerTeleport?> teleportOf)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(teleportOf);
        var entrances = new List<(AreaTriggerTemplate Trigger, AreaTriggerTeleport Teleport)>();
        var exits = new List<(AreaTriggerTemplate Trigger, AreaTriggerTeleport Teleport)>();
        foreach (AreaTriggerTemplate trigger in triggers.OrderBy(t => t.Id))
        {
            if (teleportOf(trigger.Id) is not { } teleport) continue;
            if (trigger.MapId != TramMapId && teleport.TargetMap == TramMapId) entrances.Add((trigger, teleport));
            else if (trigger.MapId == TramMapId && teleport.TargetMap != TramMapId) exits.Add((trigger, teleport));
        }

        if (entrances.Count != 2 || entrances[0].Trigger.MapId != entrances[1].Trigger.MapId) return null;
        var platforms = new List<TramPlatform>(2);
        foreach ((AreaTriggerTemplate entrance, AreaTriggerTeleport into) in entrances)
        {
            Vector3 landing = new(into.TargetX, into.TargetY, into.TargetZ);
            (AreaTriggerTemplate Trigger, AreaTriggerTeleport Teleport)? exit = exits
                .OrderBy(e => Vector3.DistanceSquared(landing, new Vector3(e.Trigger.X, e.Trigger.Y, e.Trigger.Z)))
                .Select(e => ((AreaTriggerTemplate, AreaTriggerTeleport)?)e).FirstOrDefault();
            if (exit is not { } way || way.Teleport.TargetMap != entrance.MapId
                || Vector2.Distance(new(way.Teleport.TargetX, way.Teleport.TargetY), new(entrance.X, entrance.Y)) > PairYards)
                return null;
            platforms.Add(new TramPlatform(entrance, into, way.Trigger, way.Teleport));
        }

        return platforms[0].Exit.Id == platforms[1].Exit.Id ? null : new TramNetwork(platforms[0], platforms[1]);
    }

    /// <summary>The platform whose landing is nearest <paramref name="position"/> on the tram map.</summary>
    public TramPlatform NearestOnTramMap(Vector3 position)
        => Platforms.OrderBy(p => Vector2.DistanceSquared(Flat(p.Landing), Flat(position))).First();

    public TramPlatform Other(TramPlatform platform) => ReferenceEquals(platform, Platforms[0]) ? Platforms[1] : Platforms[0];

    /// <summary>
    /// Whether a bot at <paramref name="bot"/> on <paramref name="mapId"/> goes to <paramref name="goal"/> (same map) by the tram, and from
    /// which platform to which. Both positions must resolve to known, opposite sides of the tram. AreaTable.dbc zone IDs
    /// identify the connected southern and northern regions; an unknown region does not guess from straight-line distance.
    /// </summary>
    public (TramPlatform From, TramPlatform To)? Choose(uint mapId, Vector3 bot, Vector3 goal,
        uint sourceZoneId = 0, uint goalZoneId = 0)
    {
        if (!CanConsider(mapId, bot, goal)) return null;
        TramSide sourceSide = SideOf(sourceZoneId), goalSide = SideOf(goalZoneId);
        if (sourceSide == TramSide.Unknown || goalSide == TramSide.Unknown || sourceSide == goalSide) return null;
        // ClassicDB's two entrances; a modified network without the matching end fails closed.
        uint entrance = sourceSide == TramSide.Stormwind ? 2173u : 2175u;
        TramPlatform? from = Platforms.FirstOrDefault(p => p.Entrance.Id == entrance);
        return from is null ? null : (from, Other(from));
    }

    internal bool CanConsider(uint mapId, Vector3 bot, Vector3 goal)
        => mapId == ContinentMap && bot.IsFinite() && goal.IsFinite();

    /// <summary>Build-5875 AreaTable.dbc top-level zones of the southern (Stormwind) and northern (Ironforge) road networks.</summary>
    private static TramSide SideOf(uint zoneId) => zoneId switch
    {
        // Stormwind, Elwynn, Westfall, Redridge, Duskwood, Deadwind, Burning Steppes, Swamp of Sorrows,
        // Blasted Lands and Stranglethorn Vale.
        1519 or 12 or 40 or 44 or 10 or 41 or 46 or 8 or 4 or 33 or 1477 => TramSide.Stormwind,
        // Ironforge, Dun Morogh, Loch Modan, Wetlands, Badlands, Searing Gorge, Arathi Highlands,
        // Hinterlands, Hillsbrad, Alterac, the Plaguelands, Tirisfal, Silverpine and Undercity.
        1537 or 1 or 38 or 11 or 3 or 51 or 45 or 47 or 267 or 36 or 28 or 139 or 85 or 130 or 1497
            or 170 or 269 or 330 or 293 => TramSide.Ironforge,
        _ => TramSide.Unknown,
    };

}

/// <summary>When a car stands at a platform: the first stop window found ahead of now, in ms from now.</summary>
internal readonly record struct TramDock(uint ArriveMs, uint DepartMs, Vector3 Position);

/// <summary>The stages of one bot's tram trip (<see cref="PlayerbotTram"/>).</summary>
internal enum TramStage { ToEntrance, ToPlatform, Waiting, Aboard, ToExit, Done, Failed }

/// <summary>What <see cref="PlayerbotTram.Drive"/> did this think.</summary>
internal enum TramStep { Idle, Traveling, Arrived, Failed }

/// <summary>
/// A bot's trip through the Deeprun Tram (<see cref="PlayerbotTram"/>). <see cref="To"/> is where it gets off; a trip that only takes the
/// bot off the tram map (a login there, a failure) has <see cref="To"/> = the platform it is at.
/// </summary>
internal sealed class TramTrip(TramNetwork network, TramPlatform from, TramPlatform to, Vector3 goal, uint nowMs, uint? previousTeleportTarget)
{
    public TramNetwork Network { get; } = network;
    public TramPlatform From { get; } = from;
    public TramPlatform To { get; set; } = to;
    public TramPlatform IntendedTo { get; set; } = to;
    public Vector3 Goal { get; } = goal;
    public uint? PreviousTeleportTarget { get; } = previousTeleportTarget;
    public TramStage Stage { get; set; } = TramStage.ToEntrance;
    public uint StartedMs { get; } = nowMs;
    public uint StageMs { get; set; } = nowMs;
    public uint PlatformSinceMs { get; set; }
    public uint LastDriveMs { get; set; } = nowMs;
    public uint LastPumpMs { get; set; } = nowMs;
    public string? Failure { get; set; }
    public string? FailureCategory { get; set; }
    public bool FailureReported { get; set; }
    public bool SlowReported { get; set; }
    public bool LeftBehindReported { get; set; }
    public bool PassedDestinationReported { get; set; }
    public uint BoardMissedChosenMs { get; set; }
    public float ClosestToDestination { get; set; } = float.PositiveInfinity;
    public PlayerbotRoute? Route { get; set; }

    /// <summary>The options of the last think (<see cref="PlayerbotTram.Drive"/>); the pump chooses a missed car again with them. Null before the first think.</summary>
    public PlayerbotOptions? Options { get; set; }

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) when the platform spot beside a car is planned.</summary>
    public TileWait Tiles;
    public float Best { get; set; } = float.PositiveInfinity;
    public uint BestMs { get; set; } = nowMs;
    public GameObject? Car { get; set; }
    public TramDock Dock { get; set; }
    public uint DockChosenMs { get; set; }
    public Vector3 Offset { get; set; }
    public uint LastBeatMs { get; set; }
    public bool LeftPlatform { get; set; }
    public uint NextPlanMs { get; set; }

    /// <summary>Planning calls in a row that the navigation refused (not pending, not a route): <see cref="PlayerbotTram.MaxRefusedPlans"/> fail the walk.</summary>
    public int RefusedPlans { get; set; }

    /// <summary>The exit trigger's teleport was refused (<see cref="PlayerbotAreaTriggers.TryConsumeTeleportRefusal(Player, out PlayerbotAreaTriggers.TeleportRefusal)"/>): the way out is the server's own teleport of the exit.</summary>
    public bool ExitRefused { get; set; }

    /// <summary>The entrance was backed off for this trip's failure already (once per trip).</summary>
    public bool BackoffRecorded { get; set; }
}

/// <summary>
/// Autonomous bots ride the Deeprun Tram between Stormwind and Ironforge the way a player does (docs/areas/playbots.md, "Travel by the
/// Deeprun Tram"): walk to a point on the continent floor inside the entrance trigger (<see cref="TramPlatform.Approach"/>), arrive on
/// the tram map, walk to the platform beside a car that will stop there, wait,
/// step aboard when it stands at the platform, ride it through the tunnel, step off when it stands at the other platform, and walk into
/// that platform's exit trigger. The trip is begun by a goal that travels (<see cref="PlayerbotWorldDestinations"/>) when
/// <see cref="TramNetwork.Choose"/> says the destination lies on the other side, and is then driven by the brain before anything else
/// (<see cref="PlayerbotBrain"/>), so nothing else moves the bot meanwhile.
/// <para>
/// The client half is the bot's own: aboard, it reports MSG_MOVE_HEARTBEAT with MOVEFLAG_ONTRANSPORT, the car's GUID and its offset, at
/// the world position the car's animation and the offset give (<see cref="PlayerbotMotion.SendAboard"/>; the server computes the car's
/// position as vmangos ElevatorTransport::Update does, <see cref="GameObjectMapSystem.ElevatorPosition(GameObject)"/>). Observers' clients
/// place it on their own car by the offset.
/// </para>
/// <para>
/// Map 369 stays off <see cref="PlayerbotOptions.AllowedMaps"/>: the bot may take the entrance only while its trip heads there
/// (<see cref="PlayerbotAreaTriggers.AllowTeleportsTo"/>), and may walk on the tram map only while a trip is under way
/// (<see cref="PlayerbotMapPolicy.MayMoveOn"/>). A bot found on the tram map without a trip (a login there after a save on the way)
/// takes the nearest platform's exit. Fails closed: a car that never stops at the platform within two cycles of the animation, a
/// platform the bot cannot reach, an exit it cannot walk into, each ends the trip with a stall reason; on the tram map the bot leaves by
/// the exit of the platform it is at (the server's own teleport of that exit when it cannot walk there), and the entrance the trip began
/// at waits out its own backoff (<see cref="PlayerbotTransitBackoff"/>, <see cref="PlayerbotTravelOptions.BackoffMs"/>); the other
/// entrance is not held back.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal static partial class PlayerbotTram
{
    internal const string AnomalyArea = "tram";

    /// <summary>
    /// Half the car's width and length: the collision bounds of Subwaycar.m2 (GameObjectDisplayInfo 3831; vmaps
    /// <c>temp_gameobject_models</c>: x ±9.18, y ±6.62, z -16.85 .. 0.53 around the object's position).
    /// </summary>
    internal const float CarHalfLength = 9.18f, CarHalfWidth = 6.62f;

    /// <summary>Where a bot waits beside a car: this far from the track's centre line (the car's side plus two yards).</summary>
    internal const float BesideYards = CarHalfWidth + 2f;

    /// <summary>How far inside the car's sides a passenger stands at least.</summary>
    internal const float InsideMarginYards = 1.5f;

    /// <summary>A heartbeat aboard at least this often, as a moving client reports itself.</summary>
    internal const uint HeartbeatMs = PlayerbotMotion.HeartbeatIntervalMs;

    /// <summary>Walking that gains less than this on its target within <see cref="NoProgressMs"/> is stuck.</summary>
    internal const float ProgressYards = 5f;

    internal const uint NoProgressMs = 60_000;

    /// <summary>
    /// A bot found on the tram map when the data describes no platform is sent to its bind point (and that reported) at most once this
    /// often. A failed trip holds back only its own entrance (<see cref="PlayerbotTransitBackoff"/>).
    /// </summary>
    internal const uint RetryAfterFailureMs = 600_000;

    /// <summary>
    /// A walk (to the entrance, to the exit) fails after this many refused plans in a row (pending tile reads do not count; a route resets
    /// the count) instead of waiting out <see cref="NoProgressMs"/>.
    /// </summary>
    internal const int MaxRefusedPlans = PlayerbotTaxi.MaxRefusedPlans;

    /// <summary>A car stops at a platform when its position 500 ms later is within this.</summary>
    internal const float StandingYards = 0.05f;

    /// <summary>The stop window must leave this much time to step aboard.</summary>
    internal const uint BoardMarginMs = 2_000;

    private const uint SampleMs = 250;

    /// <summary>After a path query found nothing, the next one waits this long.</summary>
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A riding bot whose world position lags this far behind its seat on the car missed its transport heartbeats.</summary>
    internal const float LeftBehindYards = 150f;

    /// <summary>How close a car comes to the destination platform's landing when it reaches that end of the tunnel.</summary>
    internal const float DestinationPlatformRadiusYards = 100f;

    /// <summary>How far a car must move back away from the destination platform's landing before we report it passed the destination.</summary>
    internal const float PassedDestinationRetreatYards = 15f;

    /// <summary>Tolerance around the intended exit teleport landing on the continent.</summary>
    internal const float WrongStationYards = 80f;

    /// <summary>
    /// A legitimate trip on the tram map, measured from arrival: the walk to the dock, a wait for the chosen car's next stop (up to one
    /// full cycle when the bot just missed a car, and the choice is made again at a think), then the ride (about 57 s of the real
    /// 143,333 ms cycle), the walk to the exit. That is up to <see cref="SlowTramCycles"/> cycles plus
    /// <see cref="SlowTramWalkAllowanceMs"/>. (The old fixed 180 s assumed a 120 s cycle; the real one is 143.3 s, so it fired on trips that were fine, #1499.)
    /// </summary>
    internal const uint SlowTramCycles = 2;

    /// <summary>The walks to the dock and to the exit, and a think's slack, on top of <see cref="SlowTramCycles"/> cycles.</summary>
    internal const uint SlowTramWalkAllowanceMs = 60_000;

    /// <summary>The time on the tram map beyond which a trip is reported slow, for a car cycle of <paramref name="cycleMs"/> ms (0 when no car runs).</summary>
    internal static uint SlowBoundMs(uint cycleMs) => unchecked((SlowTramCycles * cycleMs) + SlowTramWalkAllowanceMs);

    private static readonly ConditionalWeakTable<Player, TramTrip> Trips = [];
    private static readonly ConditionalWeakTable<Player, StrongBox<uint>> SentHomeAt = [];

    /// <summary>The bot's trip, done or not (inspection and tests).</summary>
    internal static TramTrip? TripOf(Player player) => Trips.TryGetValue(player, out TramTrip? trip) ? trip : null;

    /// <summary>Whether a trip is under way (not done, not failed).</summary>
    internal static bool IsTraveling(Player player)
        => Trips.TryGetValue(player, out TramTrip? trip) && trip.Stage is not (TramStage.Done or TramStage.Failed);

    /// <summary>
    /// Whether the bot may plan and walk on <paramref name="map"/> because of the tram: the tram map, while a trip is under way, or as a
    /// ghost whose body lies there (a bot that died on the tram map dropped its trip, PlayerbotBrain; its corpse run walks back to the body
    /// like anywhere else, PlayerbotRecovery, and once revived it takes the nearest exit, <see cref="Drive"/>).
    /// </summary>
    internal static bool AllowsWalkingOn(Player player, Map map)
        => map.MapId == TramNetwork.TramMapId && (IsTraveling(player) || IsGhostWithBodyOnTramMap(player));

    private static bool IsGhostWithBodyOnTramMap(Player player)
        => !player.IsAlive && (player.Flags & PlayerFlags.Ghost) != 0 && player.Combat.Corpse is { MapId: TramNetwork.TramMapId };

    /// <summary>The reason the bot's last trip failed (inspection), null after a trip that arrived.</summary>
    internal static string? LastFailure(Player player)
        => Trips.TryGetValue(player, out TramTrip? trip) && trip.Stage == TramStage.Failed ? trip.Failure : null;

    /// <summary>
    /// Begin a trip towards <paramref name="goal"/> when the tram is the way there (<see cref="TramNetwork.Choose"/>), the world describes
    /// the tram and the entrance the leg begins at is not backed off (<see cref="PlayerbotTransitBackoff"/>: a trip from it failed lately; a
    /// failure at the other entrance holds nothing back here). True when a trip is under way afterwards.
    /// </summary>
    internal static bool TryBegin(WorldSession session, Player player, Vector3 goal, Func<Vector3, uint>? zoneAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (player.Team != Team.Alliance) return false;
        if (IsTraveling(player)) return true;
        uint now = session.World.NowMs;
        if (ChooseLeg(session, player, goal, zoneAt, out TramNetwork? network) is not { } leg || network is null) return false;
        if (PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Tram, leg.From.Entrance.Id, now)) return false;
        PlayerbotAreaTriggers.ClearTeleportRefusal(player);
        uint? previous = PlayerbotAreaTriggers.TeleportTargetOf(player);
        var trip = new TramTrip(network, leg.From, leg.To, goal, now, previous);
        Trips.AddOrUpdate(player, trip);
        PlayerbotAreaTriggers.AllowTeleportsTo(player, TramNetwork.TramMapId);
        return true;
    }

    /// <summary>
    /// Whether the tram is the way to <paramref name="goal"/> but a failed trip keeps <see cref="TryBegin"/> from beginning one now (the
    /// backoff of the entrance the leg begins at): the caller defers the goal for a short retry instead of judging the walk there refused.
    /// World thread, no side effects.
    /// </summary>
    internal static bool IsHeldOff(WorldSession session, Player player, Vector3 goal, Func<Vector3, uint>? zoneAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (player.Team != Team.Alliance || IsTraveling(player)) return false;
        if (ChooseLeg(session, player, goal, zoneAt, out _) is not { } leg) return false;
        return PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Tram, leg.From.Entrance.Id, session.World.NowMs);
    }

    /// <summary>
    /// Whether the tram is the way to <paramref name="goal"/> from where the bot stands (the checks of <see cref="TryBegin"/> short of the
    /// entrance's backoff), without beginning a trip. World thread, no side effects.
    /// </summary>
    internal static bool IsWayTo(WorldSession session, Player player, Vector3 goal, Func<Vector3, uint>? zoneAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        return player.Team == Team.Alliance && ChooseLeg(session, player, goal, zoneAt, out _) is not null;
    }

    /// <summary>
    /// The one set of checks <see cref="TryBegin"/> and <see cref="IsWayTo"/> share: a living bot on a continent map that the world's tram
    /// network (null: none described) joins to <paramref name="goal"/>; the leg to ride, or null.
    /// </summary>
    private static (TramPlatform From, TramPlatform To)? ChooseLeg(WorldSession session, Player player, Vector3 goal,
        Func<Vector3, uint>? zoneAt, out TramNetwork? network)
    {
        network = null;
        if (!player.IsAlive || player.Map is not { } map || map.MapId == TramNetwork.TramMapId) return null;
        if (TramNetwork.Of(WorldMaps.Of(session.World)) is not { } found) return null;
        Vector3 at = new(player.X, player.Y, player.Z);
        if (!found.CanConsider(map.MapId, at, goal)) return null;
        uint sourceZone = zoneAt is null ? map.GetZoneAndAreaId(at.X, at.Y, at.Z).ZoneId : zoneAt(at);
        uint goalZone = zoneAt is null ? map.GetZoneAndAreaId(goal.X, goal.Y, goal.Z).ZoneId : zoneAt(goal);
        if (found.Choose(map.MapId, at, goal, sourceZone, goalZone) is not { } leg) return null;
        if (PlayerbotNavigation.IsUnreachableOnFoot(player, leg.From.Approach, out _, out _)) return null;
        if (map.Collision.MeshPoints is not null && !CanReachEntranceApproach(player, leg.From.Approach)) return null;
        network = found;
        return leg;
    }

    private static bool CanReachEntranceApproach(Player player, Vector3 approach)
    {
        Vector3 at = new(player.X, player.Y, player.Z);
        if (Vector2.Distance(Flat(at), Flat(approach)) > 150f)
            return true;
        var options = new PlayerbotOptions();
        if (!PlayerbotNavigation.TryPlanToward(player, approach, options, out PlayerbotRoute? route) || route is null)
            return false;
        if (!route.MeshEndedShort)
            return true;
        bool reachesApproach = route.Points.Count > 0
            && Vector2.Distance(Flat(route.Points[^1]), Flat(approach)) <= PlayerbotNavigation.ProjectedEndYards
            && MathF.Abs(route.Points[^1].Z - approach.Z) <= PlayerbotNavigation.ProjectedEndYards;
        return reachesApproach || route.Points.Count >= options.MaxPathPoints || route.Distance >= 50f;
    }

    private static bool IsBesideDock(Vector3 bot, Vector3 dock)
        => Vector2.Distance(Flat(bot), Flat(dock)) <= BesideYards + CarHalfLength;

    /// <summary>Drop the trip without a packet (death, a controller taking the bot over); the entrance consent goes with it.</summary>
    internal static void Cancel(Player player)
    {
        if (!Trips.TryGetValue(player, out TramTrip? trip)) return;
        Trips.Remove(player);
        if (!player.IsAlive && (player.Movement.Flags & MovementFlags.OnTransport) != 0 && player.Session is WorldSession session)
            ClearTransport(session, player);
        if (trip.Stage is not (TramStage.Done or TramStage.Failed)) PlayerbotAreaTriggers.AllowTeleportsTo(player, trip.PreviousTeleportTarget);
    }

    private static void ClearTransport(WorldSession session, Player player)
    {
        MovementInfo movement = player.Movement;
        movement.Flags &= ~MovementFlags.OnTransport;
        movement.TransportGuid = 0;
        movement.TransportX = movement.TransportY = movement.TransportZ = movement.TransportOrientation = 0;
        player.ApplyMovement(movement, session.World.NowMs);
        if (!player.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root))
            MovementHandlers.RelayStoredMovement(player, WorldOpcode.MsgMoveHeartbeat);
    }

    /// <summary>
    /// Per-tick tram pump (called from <see cref="PlayerbotMotion.Pump"/> every world tick). When a large bot population spaces brain
    /// thinks 12–23 seconds apart, a ~12-second Deeprun Tram stop would otherwise pass unseen between thinks, and a riding bot would only
    /// send <c>MSG_MOVE_HEARTBEAT</c> every think instead of every 500 ms. Sampling every <see cref="SampleMs"/> (250 ms) after the last
    /// <see cref="Drive"/> transitions a completed platform walk to <see cref="TramStage.Waiting"/>, boards a standing car while its stop
    /// window is open, sends 500 ms riding heartbeats, and steps off at the destination platform.
    /// </summary>
    internal static void Pump(WorldSession session, Player player, uint now)
    {
        if (!player.IsAlive || !player.IsInWorld || player.Map is not { } map || map.MapId != TramNetwork.TramMapId) return;
        if (!Trips.TryGetValue(player, out TramTrip? trip) || trip.Stage is TramStage.Done or TramStage.Failed) return;
        if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return;
        if (trip.Stage == TramStage.ToEntrance)
            EnterTramMapFromEntrance(player, trip, now);
        if (unchecked(now - trip.LastDriveMs) < SampleMs || unchecked(now - trip.LastPumpMs) < SampleMs) return;
        trip.LastPumpMs = now;

        // The chosen car has left without the bot: choose again now. At a think 12-23 s apart a missed car cost the bot a full cycle (#1499).
        // A choice that finds nothing leaves Car null, and the next think decides (wait, or leave after the failure).
        if (trip.Stage is TramStage.ToPlatform or TramStage.Waiting && trip.Options is { } thinkOptions
            && trip.Car is { IsInWorld: true } missedCar && ReferenceEquals(missedCar.Map, map)
            && unchecked(now - trip.DockChosenMs) > trip.Dock.DepartMs + 1_000
            && map.FindUpdater<GameObjectMapSystem>() is { } carSystem)
        {
            TramPlatform platform = trip.Network.Other(trip.To);
            ReportBoardMissed(player, trip, missedCar, platform, new Vector3(player.X, player.Y, player.Z), now, CanReport(session, player));
            ChooseCar(player, map, carSystem, trip, platform, thinkOptions, now, out _, out _, out _);
        }

        switch (trip.Stage)
        {
            case TramStage.ToPlatform:
            {
                if (trip.Car is not { IsInWorld: true } || !ReferenceEquals(trip.Car.Map, map)) return;
                if (trip.Route is { Complete: false }) return;
                Vector3 bot = new(player.X, player.Y, player.Z);
                if (!IsBesideDock(bot, trip.Dock.Position)) return;
                if (!PlayerbotMovementControl.Stop(session, player)) return;
                Enter(trip, TramStage.Waiting, now);
                goto case TramStage.Waiting;
            }
            case TramStage.Waiting:
            {
                if (trip.Car is not { IsInWorld: true } car || !ReferenceEquals(car.Map, map)
                    || map.FindUpdater<GameObjectMapSystem>() is not { } system)
                {
                    return;
                }

                uint cycle = system.ElevatorCycle(car);
                if (cycle == 0) return;
                TramPlatform here = trip.Network.Other(trip.To);
                if (Standing(system, car, 0) is not { } at || !AtEnd(at, here, trip.To)) return;
                Vector3 bot = new(player.X, player.Y, player.Z);
                if (Vector2.Distance(Flat(bot), Flat(at)) > BesideYards + CarHalfLength) return;
                if (StandingFor(system, car, at, cycle) < InsideMarginMs) return;
                if (!PlayerbotMovementControl.Stop(session, player)) return;

                float x = bot.X, y = bot.Y, z = bot.Z, o = 0;
                ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, at.X, at.Y, at.Z, car.Orientation);
                x = Math.Clamp(x, -(CarHalfLength - InsideMarginYards), CarHalfLength - InsideMarginYards);
                y = Math.Clamp(y, -(CarHalfWidth - InsideMarginYards), CarHalfWidth - InsideMarginYards);
                var offset = new Vector3(x, y, z);
                if (!SendAboard(session, player, car, at, offset)) return;
                trip.Offset = offset;
                trip.LastBeatMs = now;
                trip.LeftPlatform = false;
                trip.ClosestToDestination = float.PositiveInfinity;
                Enter(trip, TramStage.Aboard, now);
                return;
            }
            case TramStage.Aboard:
            {
                if (trip.Car is not { IsInWorld: true } car || !ReferenceEquals(car.Map, map)
                    || map.FindUpdater<GameObjectMapSystem>() is not { } system || system.ElevatorPosition(car) is not { } carAt)
                {
                    return;
                }

                if ((player.Movement.Flags & MovementFlags.OnTransport) == 0 || player.Movement.TransportGuid != car.Guid.Value)
                    return;

                TramPlatform here = trip.Network.Other(trip.To);
                Vector3 at = new(carAt.X, carAt.Y, carAt.Z);
                if (!trip.LeftPlatform && Vector2.Distance(Flat(at), Flat(here.Landing))
                        > Vector2.Distance(Flat(trip.Dock.Position), Flat(here.Landing)) + 5f)
                {
                    trip.LeftPlatform = true;
                }

                if (trip.LeftPlatform)
                {
                    float distToDest = Vector2.Distance(Flat(at), Flat(trip.To.Landing));
                    if (distToDest < trip.ClosestToDestination) trip.ClosestToDestination = distToDest;
                }

                if (Standing(system, car, 0) is { } stop && AtEnd(stop, trip.To, here) && trip.LeftPlatform)
                {
                    Vector3? off = NearestSide(map, car, stop, trip.To.Landing.Z, trip.To.ExitCentre);
                    if (off is { } ground && PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation))
                    {
                        Enter(trip, TramStage.ToExit, now);
                        return;
                    }
                }

                if (unchecked(now - trip.LastBeatMs) >= HeartbeatMs)
                {
                    if (SendAboard(session, player, car, at, trip.Offset)) trip.LastBeatMs = now;
                }

                return;
            }
        }
    }

    /// <summary>
    /// One think of the trip, before anything else the brain does. <see cref="TramStep.Idle"/>: no trip and not on the tram map (the
    /// brain goes on). On the tram map without a trip, a trip off it begins. <see cref="TramStep.Arrived"/> and <see cref="TramStep.Failed"/>
    /// are reported once, when the bot stands on the continent again; <paramref name="failure"/> then says why.
    /// </summary>
    internal static TramStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        TramStep step = DriveStage(session, player, options, elapsedMs, out failure);
        // Every failure carries its reason: a trip that failed on the continent noted it on the trip only, and the log said just "the
        // trip failed" (live-w22-r1).
        TramTrip? trip = TripOf(player);
        // The entrance the trip began at waits out its own backoff after a failure (the other platform is not affected), and a ride that
        // arrived clears it.
        if (trip is not null && step == TramStep.Failed && !trip.BackoffRecorded)
        {
            trip.BackoffRecorded = true;
            PlayerbotTransitBackoff.Fail(player, TransitKind.Tram, trip.From.Entrance.Id, session.World.NowMs, options);
        }
        else if (trip is not null && step == TramStep.Arrived)
        {
            PlayerbotTransitBackoff.Clear(player, TransitKind.Tram, trip.From.Entrance.Id);
        }

        if (step == TramStep.Failed && failure is null) failure = trip?.Failure;
        if (step == TramStep.Failed && (trip is null || !trip.FailureReported))
        {
            if (trip is not null) trip.FailureReported = true;
            if (CanReport(session, player))
            {
                string category = trip?.FailureCategory ?? "failed";
                string why = failure ?? "unknown";
                PlayerbotAnomalies.Report(player, AnomalyArea, "trip-failed",
                    string.Create(CultureInfo.InvariantCulture,
                        $"reason={category} map={player.Map?.MapId ?? 0} pos=({player.X:F1},{player.Y:F1},{player.Z:F1}) detail={why}"));
            }
        }

        return step;
    }

    private static TramStep DriveStage(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        failure = null;
        if (!player.IsInWorld || player.Map is not { } map) return IsTraveling(player) ? TramStep.Traveling : TramStep.Idle;
        uint now = session.World.NowMs;
        if (!Trips.TryGetValue(player, out TramTrip? trip) || trip.Stage is TramStage.Done or TramStage.Failed)
        {
            if (map.MapId != TramNetwork.TramMapId || !player.IsAlive) return TramStep.Idle;
            if (TramNetwork.Of(WorldMaps.Of(session.World)) is not { } network)
            {
                // No platforms in the data: nothing to walk to. The way out is the bot's bind point (TeleportToHomebind). Reported once per
                // RetryAfterFailureMs, sent or refused: a refused teleport leaves the bot to the brain (it cannot move on this map), whose
                // stall watch reports it again later; it never loops here silently.
                TeleportService? homebind = session.Services.GetService<TeleportFeature>()?.Teleports;
                if (homebind?.IsBeingTeleported(player) == true) return TramStep.Traveling;
                if (SentHomeAt.TryGetValue(player, out StrongBox<uint>? lately) && unchecked(now - lately.Value) < RetryAfterFailureMs)
                    return TramStep.Idle;
                SentHomeAt.AddOrUpdate(player, new StrongBox<uint>(now));
                bool canReport = CanReport(session, player);
                bool sent = homebind is not null && homebind.TeleportToHomebind(player);
                if (sent && canReport)
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "teleport-out",
                        string.Create(CultureInfo.InvariantCulture,
                            $"reason=no-platforms-homebind map={map.MapId} pos=({player.X:F1},{player.Y:F1},{player.Z:F1})"));
                }

                failure = sent
                    ? "tram: on the Deeprun Tram map, but the world data describes no platforms; sent to its bind point"
                    : "tram: on the Deeprun Tram map, but the world data describes no platforms, and the bind-point teleport was refused";
                if (canReport)
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "trip-failed",
                        string.Create(CultureInfo.InvariantCulture,
                            $"reason=no-platforms map={map.MapId} pos=({player.X:F1},{player.Y:F1},{player.Z:F1}) detail={failure}"));
                }

                return TramStep.Failed;
            }

            TramPlatform here = network.NearestOnTramMap(new Vector3(player.X, player.Y, player.Z));
            trip = new TramTrip(network, here, here, here.Arrival, now, PlayerbotAreaTriggers.TeleportTargetOf(player))
            {
                Stage = TramStage.ToExit,
                PlatformSinceMs = now,
            };
            Trips.AddOrUpdate(player, trip);
        }

        trip.LastDriveMs = now;
        if (!player.IsAlive)
        {
            Cancel(player);
            return TramStep.Idle;
        }

        if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return TramStep.Traveling;

        // The bot walked into the trigger it was heading for and the server did not take it through (policy, level, a refused teleport): the
        // trip ends at once; waiting inside the volume for the progress bound would only repeat the refusal.
        if (trip.Stage == TramStage.ToEntrance && map.MapId != TramNetwork.TramMapId
            && PlayerbotAreaTriggers.TryConsumeTeleportRefusal(player, trip.From.Entrance.Id, trip.StageMs, out string? entranceWhy))
        {
            return Fail(session, player, trip, now, $"tram: the {trip.From.Name} entrance did not take the bot ({entranceWhy})", "teleport-refused");
        }

        if (trip.Stage == TramStage.ToExit && map.MapId == TramNetwork.TramMapId && !trip.ExitRefused
            && PlayerbotAreaTriggers.TryConsumeTeleportRefusal(player, trip.To.Exit.Id, trip.StageMs, out string? exitWhy))
        {
            trip.ExitRefused = true;
            trip.FailureCategory ??= "teleport-refused";
            trip.Failure ??= $"tram: the {trip.To.Name} exit did not take the bot ({exitWhy})";
        }

        if (trip.Stage == TramStage.ToEntrance && map.MapId == TramNetwork.TramMapId)
        {
            EnterTramMapFromEntrance(player, trip, now);
        }

        trip.Options = options;
        // A trip that already failed (it leaves by the platform it stands at) is a failed trip, reported as trip-failed once the bot is
        // out; it is not also a slow one. Without a running car there is no cycle to measure against (AtPlatform fails that trip).
        if (map.MapId == TramNetwork.TramMapId && !trip.SlowReported && trip.Failure is null && map.FindUpdater<GameObjectMapSystem>() is { } carSystem)
        {
            uint cycle = 0;
            foreach (GameObject running in carSystem.MapWideElevators) cycle = Math.Max(cycle, carSystem.ElevatorCycle(running));
            uint mapStartMs = trip.PlatformSinceMs != 0 ? trip.PlatformSinceMs : trip.StartedMs;
            if (cycle != 0 && unchecked(now - mapStartMs) > SlowBoundMs(cycle))
            {
                trip.SlowReported = true;
                if (CanReport(session, player))
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "trip-slow",
                        string.Create(CultureInfo.InvariantCulture,
                            $"stage={trip.Stage} elapsedMs={unchecked(now - mapStartMs)} from={trip.From.Name} to={trip.IntendedTo.Name} pos=({player.X:F1},{player.Y:F1},{player.Z:F1})"));
                }
            }
        }

        switch (trip.Stage)
        {
            case TramStage.ToEntrance:
                PlayerbotThinkProbe.Phase("tram entrance");
                return ToEntrance(session, player, map, trip, options, elapsedMs, now);
            case TramStage.ToPlatform:
            case TramStage.Waiting:
                PlayerbotThinkProbe.Phase("tram platform");
                return AtPlatform(session, player, map, trip, options, elapsedMs, now);
            case TramStage.Aboard:
                PlayerbotThinkProbe.Phase("tram ride");
                return Ride(session, player, map, trip, now);
            case TramStage.ToExit:
                PlayerbotThinkProbe.Phase("tram exit");
                return ToExit(session, player, map, trip, options, elapsedMs, now, out failure);
            default:
                return TramStep.Idle;
        }
    }

    private static void EnterTramMapFromEntrance(Player player, TramTrip trip, uint now)
    {
        // Through the entrance: from here only the ride's own consent is needed (the exits lead back to the continent).
        PlayerbotAreaTriggers.AllowTeleportsTo(player, trip.PreviousTeleportTarget);
        // The landing of the entrance taken: its platform is where the bot boards, the other one where it gets off.
        TramPlatform here = trip.Network.NearestOnTramMap(new Vector3(player.X, player.Y, player.Z));
        trip.To = trip.Network.Other(here);
        trip.IntendedTo = trip.To;
        trip.Car = null;
        trip.Route = null;
        Enter(trip, TramStage.ToPlatform, now);
        if (trip.PlatformSinceMs == 0) trip.PlatformSinceMs = now;
    }

    private static TramStep ToEntrance(WorldSession session, Player player, Map map, TramTrip trip, PlayerbotOptions options, uint elapsedMs, uint now)
    {
        if (map.MapId == TramNetwork.TramMapId)
        {
            EnterTramMapFromEntrance(player, trip, now);
            return TramStep.Traveling;
        }

        if (map.MapId != trip.From.ContinentMap)
            return Fail(session, player, trip, now, $"tram: left for map {map.MapId} on the way to the {trip.From.Name} entrance", "left-map");
        if (!Walk(session, player, trip, trip.From.Approach, options, elapsedMs, now, toward: true))
        {
            return Fail(session, player, trip, now, string.Create(CultureInfo.InvariantCulture,
                $"tram: no way to the {trip.From.Name} entrance from {player.X:F1},{player.Y:F1},{player.Z:F1}{(trip.RefusedPlans >= MaxRefusedPlans ? $" (the path was refused {MaxRefusedPlans} times in a row)" : string.Empty)}"),
                "no-way-to-entrance");
        }

        return TramStep.Traveling;
    }

    /// <summary>Reports the missed car once per choice (the caller has found <c>now - DockChosenMs &gt; DepartMs + 1000</c>).</summary>
    private static void ReportBoardMissed(Player player, TramTrip trip, GameObject missedCar, TramPlatform here, Vector3 bot, uint now, bool canReport)
    {
        if (trip.BoardMissedChosenMs == trip.DockChosenMs) return;
        trip.BoardMissedChosenMs = trip.DockChosenMs;
        if (canReport)
        {
            PlayerbotAnomalies.Report(player, AnomalyArea, "board-missed",
                string.Create(CultureInfo.InvariantCulture,
                    $"stage={trip.Stage} platform={here.Name} car={missedCar.Guid.Entry} arriveMs={trip.Dock.ArriveMs} departMs={trip.Dock.DepartMs} elapsedMs={unchecked(now - trip.DockChosenMs)} pos=({bot.X:F1},{bot.Y:F1},{bot.Z:F1})"));
        }
    }

    private static TramStep AtPlatform(WorldSession session, Player player, Map map, TramTrip trip, PlayerbotOptions options, uint elapsedMs, uint now)
    {
        if (map.MapId != TramNetwork.TramMapId) return Fail(session, player, trip, now, "tram: left the tram map before boarding", "left-map");
        TramPlatform here = trip.Network.Other(trip.To);
        uint cycle = 0;
        if (map.FindUpdater<GameObjectMapSystem>() is { } objects)
            foreach (GameObject running in objects.MapWideElevators) cycle = Math.Max(cycle, objects.ElevatorCycle(running));
        if (cycle == 0)
        {
            return LeaveAfterFailure(trip, here, now, "tram: no tram car runs (no map-wide car with an animation on the tram map)", "no-car");
        }

        if (unchecked(now - trip.PlatformSinceMs) > (2 * cycle) + 30_000)
        {
            return LeaveAfterFailure(trip, here, now, string.Create(CultureInfo.InvariantCulture,
                $"tram: no car stopped at the {here.Name} platform within {((2 * cycle) + 30_000) / 1000}s"), "no-car-stopped");
        }

        GameObjectMapSystem system = map.FindUpdater<GameObjectMapSystem>()!;
        Vector3 bot = new(player.X, player.Y, player.Z);
        if (trip.Car is { IsInWorld: true } missedCar && ReferenceEquals(missedCar.Map, map)
            && unchecked(now - trip.DockChosenMs) > trip.Dock.DepartMs + 1_000)
        {
            ReportBoardMissed(player, trip, missedCar, here, bot, now, CanReport(session, player));
        }

        if (trip.Car is null || !trip.Car.IsInWorld || !ReferenceEquals(trip.Car.Map, map)
            || unchecked(now - trip.DockChosenMs) > trip.Dock.DepartMs + 1_000)
        {
            // Pick (again): the car that stops here soonest at a time the bot can be beside it.
            PlayerbotThinkProbe.Phase("tram choose car");
            if (!ChooseCar(player, map, system, trip, here, options, now, out string? why, out string? category, out bool waitingForTiles))
            {
                // A tile beside the car is still being read: no verdict on the way, so the trip goes on and the car is chosen again.
                if (waitingForTiles) return TramStep.Traveling;
                return LeaveAfterFailure(trip, here, now, why ?? $"tram: no car stops at the {here.Name} platform", category ?? "no-car-stopped");
            }

            PlayerbotThinkProbe.Phase("tram platform");
        }

        if (trip.Stage == TramStage.ToPlatform)
        {
            if (trip.Route is { Complete: false } route && PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now))
                return TramStep.Traveling;
            if (!IsBesideDock(bot, trip.Dock.Position))
            {
                if (trip.Route is { Complete: false })
                    return TramStep.Traveling;
                trip.Route = null;
                if (unchecked((int)(trip.NextPlanMs - now)) > 0) return TramStep.Traveling;
                if (Beside(player, map, trip.Car!, trip.Dock.Position, here, options, ref trip.Tiles, now, out PlayerbotRoute? nextRoute, out bool pending) is not null)
                {
                    if (nextRoute is not null)
                    {
                        trip.RefusedPlans = 0;
                        trip.Route = nextRoute;
                        PlayerbotNavigation.TryAdvance(session, nextRoute, options, elapsedMs, now);
                        return TramStep.Traveling;
                    }
                }
                else if (!pending)
                {
                    trip.Car = null;
                }

                trip.NextPlanMs = unchecked(now + PlanRetryMs);
                return TramStep.Traveling;
            }

            PlayerbotMovementControl.Stop(session, player);
            Enter(trip, TramStage.Waiting, now);
        }
        else if (trip.Stage == TramStage.Waiting && !IsBesideDock(bot, trip.Dock.Position))
        {
            trip.Car = null;
            trip.Route = null;
            Enter(trip, TramStage.ToPlatform, now);
            return TramStep.Traveling;
        }

        GameObject car = trip.Car!;
        if (Standing(system, car, 0) is not { } at || !AtEnd(at, here, trip.To)) return TramStep.Traveling;
        if (Vector2.Distance(Flat(bot), Flat(at)) > BesideYards + CarHalfLength)
        {
            trip.Car = null; // not beside the car (the walk stopped short): pick and walk again
            return TramStep.Traveling;
        }

        uint left = StandingFor(system, car, at, cycle);
        if (left < InsideMarginMs) return TramStep.Traveling; // about to leave: wait for the next stop
        if (!PlayerbotMovementControl.Stop(session, player)) return TramStep.Traveling;

        // Step aboard: the point of the car's floor nearest to where the bot stands, at the height it stands (the platform is level
        // with the car's floor: the model's floor is 11.0 yards under the object's position, the platform 11.3).
        float x = bot.X, y = bot.Y, z = bot.Z, o = 0;
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, at.X, at.Y, at.Z, car.Orientation);
        x = Math.Clamp(x, -(CarHalfLength - InsideMarginYards), CarHalfLength - InsideMarginYards);
        y = Math.Clamp(y, -(CarHalfWidth - InsideMarginYards), CarHalfWidth - InsideMarginYards);
        var offset = new Vector3(x, y, z);
        if (!SendAboard(session, player, car, at, offset)) return TramStep.Traveling;
        trip.Offset = offset;
        trip.LastBeatMs = now;
        trip.LeftPlatform = false;
        trip.ClosestToDestination = float.PositiveInfinity;
        Enter(trip, TramStage.Aboard, now);
        return TramStep.Traveling;
    }

    private static uint WalkMs(Vector3 from, Vector3 to, float speed)
        => (uint)(Vector2.Distance(Flat(from), Flat(to)) / speed * 1000f) + 1_000;

    /// <summary>A stop shorter than this is not boarded.</summary>
    private const uint InsideMarginMs = 1_000;

    private static bool ChooseCar(Player player, Map map, GameObjectMapSystem system, TramTrip trip, TramPlatform here, PlayerbotOptions options,
        uint now, out string? failure, out string? category, out bool waitingForTiles)
    {
        failure = null;
        category = null;
        waitingForTiles = false;
        trip.Car = null;
        trip.Route = null;
        Vector3 bot = new(player.X, player.Y, player.Z);
        float speed = MathF.Max(1f, options.MoveSpeed);
        var candidates = new List<(GameObject Car, TramDock Dock, uint Ready)>();
        foreach (GameObject car in system.MapWideElevators)
        {
            if (!car.IsInWorld || !ReferenceEquals(car.Map, map)) continue;
            uint cycle = system.ElevatorCycle(car);
            if (cycle == 0) continue;
            // The first stop here that the bot can still reach in time (a straight walk at its speed, plus a second).
            TramDock? next = NextDock(cycle, ahead => system.ElevatorProgressAhead(car, ahead) is { } p ? Position(system, car, p) : null,
                here.Landing, trip.To.Landing,
                d => d.DepartMs > Math.Max(d.ArriveMs, WalkMs(bot, d.Position, speed)) + BoardMarginMs);
            if (next is { } dock) candidates.Add((car, dock, Math.Max(dock.ArriveMs, WalkMs(bot, dock.Position, speed))));
        }

        if (candidates.Count == 0)
        {
            bool anyCar = system.MapWideElevators.Count > 0;
            category = anyCar ? "no-car-stopped" : "no-car";
            failure = !anyCar ? "tram: no tram car on the tram map"
                : $"tram: no car stops at the {here.Name} platform";
            return false;
        }

        foreach ((GameObject car, TramDock dock, _) in candidates.OrderBy(c => c.Ready).ThenBy(c => c.Car.Guid.Value))
        {
            if (Beside(player, map, car, dock.Position, here, options, ref trip.Tiles, now, out PlayerbotRoute? route, out bool pending) is not { } spot)
            {
                waitingForTiles |= pending;
                continue;
            }

            trip.Car = car;
            trip.Dock = dock;
            trip.DockChosenMs = now;
            trip.Route = route;
            Enter(trip, route is null ? TramStage.Waiting : TramStage.ToPlatform, now);
            return true;
        }

        // A spot whose tiles are still being read is no refusal: the choice is made again next think (TileWait.MaxMs, then it is one).
        if (waitingForTiles) return false;
        category = "no-way-to-platform";
        failure = string.Create(CultureInfo.InvariantCulture,
            $"tram: no way from {bot.X:F1},{bot.Y:F1},{bot.Z:F1} to the side of a car at the {here.Name} platform");
        return false;
    }

    /// <summary>
    /// The spot on the platform beside a car standing at <paramref name="dock"/> (either side of the track, <see cref="BesideYards"/> from
    /// its centre line, where a floor level with the landing is), with the route to it (null when the bot stands there already).
    /// </summary>
    private static Vector3? Beside(Player player, Map map, GameObject car, Vector3 dock, TramPlatform here, PlayerbotOptions options,
        ref TileWait tiles, uint now, out PlayerbotRoute? route, out bool pending)
    {
        route = null;
        pending = false;
        Vector3 bot = new(player.X, player.Y, player.Z);
        foreach (Vector3 spot in Sides(map, car, dock, here.Landing.Z).OrderBy(s => Vector2.DistanceSquared(Flat(s), Flat(bot))))
        {
            if (Vector2.Distance(Flat(spot), Flat(bot)) <= 1.5f) return spot;
            PlanOutcome outcome = PlayerbotNavigation.PlanExact(player, spot, options, ref tiles, now, out PlayerbotRoute? planned);
            pending |= outcome == PlanOutcome.Pending;
            if (outcome == PlanOutcome.Route && planned is not null
                && Vector2.Distance(Flat(planned.Points[^1]), Flat(spot)) <= 2f)
            {
                route = planned;
                return spot;
            }
        }

        return null;
    }

    /// <summary>Of the <see cref="Sides"/> of a car at <paramref name="stop"/>, the one nearest <paramref name="exitCentre"/> on the flat; null when neither has a floor.</summary>
    // Out of Pump on purpose: a lambda there capturing 'trip' made the compiler allocate a closure on every Pump call for every
    // bot (~16 MB/min on live), tram rider or not. First nearest wins, as OrderBy's stable sort did.
    private static Vector3? NearestSide(Map map, GameObject car, Vector3 stop, float floorZ, Vector3 exitCentre)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        Vector2 target = Flat(exitCentre);
        foreach (Vector3 side in Sides(map, car, stop, floorZ))
        {
            float d = Vector2.DistanceSquared(Flat(side), target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = side;
            }
        }

        return best;
    }

    /// <summary>The platform spots on both sides of a car at <paramref name="at"/>, where the floor is level with <paramref name="floorZ"/>.</summary>
    private static IEnumerable<Vector3> Sides(Map map, GameObject car, Vector3 at, float floorZ)
    {
        // The car's length runs along its local x axis (TransportAnimation moves it along x); its sides are along local y.
        float sideX = -MathF.Sin(car.Orientation), sideY = MathF.Cos(car.Orientation);
        foreach (float sign in (float[])[1f, -1f])
        {
            float x = at.X + (sideX * BesideYards * sign), y = at.Y + (sideY * BesideYards * sign);
            float floor = map.Collision.GetHeight(x, y, floorZ + 3f);
            if (float.IsFinite(floor) && floor > TerrainTile.InvalidHeight && MathF.Abs(floor - floorZ) <= 3f)
                yield return new Vector3(x, y, floor);
        }
    }

    private static TramStep Ride(WorldSession session, Player player, Map map, TramTrip trip, uint now)
    {
        if (map.MapId != TramNetwork.TramMapId || trip.Car is not { IsInWorld: true } car || !ReferenceEquals(car.Map, map)
            || map.FindUpdater<GameObjectMapSystem>() is not { } system || system.ElevatorPosition(car) is not { } carAt)
        {
            if (map.MapId == TramNetwork.TramMapId && CanReport(session, player))
            {
                PlayerbotAnomalies.Report(player, AnomalyArea, "detached-mid-ride",
                    string.Create(CultureInfo.InvariantCulture,
                        $"reason=car-gone to={trip.To.Name} pos=({player.X:F1},{player.Y:F1},{player.Z:F1})"));
            }

            return Fail(session, player, trip, now, "tram: the car the bot rode is gone", "car-gone");
        }

        if ((player.Movement.Flags & MovementFlags.OnTransport) == 0 || player.Movement.TransportGuid != car.Guid.Value)
        {
            Vector3 botAt = new(player.X, player.Y, player.Z);
            if (CanReport(session, player))
            {
                string whyDetached = (player.Movement.Flags & MovementFlags.OnTransport) == 0 ? "transport-flag-cleared" : "transport-guid-mismatch";
                PlayerbotAnomalies.Report(player, AnomalyArea, "detached-mid-ride",
                    string.Create(CultureInfo.InvariantCulture,
                        $"reason={whyDetached} car={car.Guid.Entry} to={trip.To.Name} pos=({botAt.X:F1},{botAt.Y:F1},{botAt.Z:F1})"));
            }

            if ((player.Movement.Flags & MovementFlags.OnTransport) != 0)
                PlayerbotMotion.SendStepOff(session, player, botAt, player.Orientation);
            TramPlatform nearestOnDetach = trip.Network.NearestOnTramMap(botAt);
            return LeaveAfterFailure(trip, nearestOnDetach, now, "tram: detached from the tram car mid-ride", "detached-mid-ride");
        }

        TramPlatform here = trip.Network.Other(trip.To);
        Vector3 at = new(carAt.X, carAt.Y, carAt.Z);
        uint cycle = system.ElevatorCycle(car);
        if (!trip.LeftPlatform && Vector2.Distance(Flat(at), Flat(here.Landing))
                > Vector2.Distance(Flat(trip.Dock.Position), Flat(here.Landing)) + 5f)
            trip.LeftPlatform = true;

        if (!trip.LeftBehindReported)
        {
            Vector3 expectedSeat = Seat(car, at, trip.Offset);
            float seatDrift = Vector2.Distance(Flat(new Vector3(player.X, player.Y, player.Z)), Flat(expectedSeat));
            if (seatDrift > LeftBehindYards)
            {
                trip.LeftBehindReported = true;
                if (CanReport(session, player))
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "left-behind",
                        string.Create(CultureInfo.InvariantCulture,
                            $"car={car.Guid.Entry} driftYards={seatDrift:F1} sinceBeatMs={unchecked(now - trip.LastBeatMs)} bot=({player.X:F1},{player.Y:F1},{player.Z:F1}) seat=({expectedSeat.X:F1},{expectedSeat.Y:F1},{expectedSeat.Z:F1})"));
                }
            }
        }

        if (trip.LeftPlatform)
        {
            float distToDest = Vector2.Distance(Flat(at), Flat(trip.To.Landing));
            if (distToDest < trip.ClosestToDestination)
            {
                trip.ClosestToDestination = distToDest;
            }
            else if (!trip.PassedDestinationReported
                && trip.ClosestToDestination <= DestinationPlatformRadiusYards
                && distToDest > trip.ClosestToDestination + PassedDestinationRetreatYards)
            {
                trip.PassedDestinationReported = true;
                if (CanReport(session, player))
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "passed-destination",
                        string.Create(CultureInfo.InvariantCulture,
                            $"to={trip.To.Name} closestYards={trip.ClosestToDestination:F1} currentYards={distToDest:F1} elapsedMs={unchecked(now - trip.StageMs)}"));
                }
            }
        }

        if (Standing(system, car, 0) is { } stop && AtEnd(stop, trip.To, here) && trip.LeftPlatform)
        {
            // At the other platform: step off on the side nearest its exit.
            Vector3? off = Sides(map, car, stop, trip.To.Landing.Z)
                .OrderBy(s => Vector2.DistanceSquared(Flat(s), Flat(trip.To.ExitCentre)))
                .Select(s => (Vector3?)s).FirstOrDefault();
            if (off is not { } ground)
                return LeaveAfterFailure(trip, trip.To, now, $"tram: no platform floor beside the car at the {trip.To.Name} platform", "no-destination-floor");
            if (!PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation)) return TramStep.Traveling;
            Enter(trip, TramStage.ToExit, now);
            return TramStep.Traveling;
        }

        if (unchecked(now - trip.StageMs) > cycle + 30_000)
        {
            if (!trip.PassedDestinationReported)
            {
                trip.PassedDestinationReported = true;
                if (CanReport(session, player))
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "passed-destination",
                        string.Create(CultureInfo.InvariantCulture,
                            $"to={trip.To.Name} closestYards={trip.ClosestToDestination:F1} elapsedMs={unchecked(now - trip.StageMs)} reason=cycle-timeout"));
                }
            }

            // The car never stood at the other platform (it should within one cycle): off where it stands, out by the nearest exit.
            TramPlatform nearest = trip.Network.NearestOnTramMap(at);
            Vector3? off = Sides(map, car, at, nearest.Landing.Z).Select(s => (Vector3?)s).FirstOrDefault();
            if (off is { } ground) PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation);
            return LeaveAfterFailure(trip, nearest, now, $"tram: the car did not stop at the {trip.To.Name} platform within {(cycle + 30_000) / 1000}s", "passed-destination");
        }

        if (unchecked(now - trip.LastBeatMs) >= HeartbeatMs)
        {
            if (SendAboard(session, player, car, at, trip.Offset)) trip.LastBeatMs = now;
        }

        return TramStep.Traveling;
    }

    private static TramStep ToExit(WorldSession session, Player player, Map map, TramTrip trip, PlayerbotOptions options, uint elapsedMs, uint now,
        out string? failure)
    {
        failure = null;
        if (map.MapId != TramNetwork.TramMapId)
        {
            // Out: the exit put the bot on the continent.
            PlayerbotAreaTriggers.AllowTeleportsTo(player, trip.PreviousTeleportTarget);
            if (trip.Failure is { } why)
            {
                trip.Stage = TramStage.Failed;
                failure = why;
                return TramStep.Failed;
            }

            Vector3 continentPos = new(player.X, player.Y, player.Z);
            float distToArrival = Vector2.Distance(Flat(continentPos), Flat(trip.IntendedTo.Arrival));
            if (map.MapId != trip.IntendedTo.ContinentMap || distToArrival > WrongStationYards)
            {
                string wrongWhy = string.Create(CultureInfo.InvariantCulture,
                    $"tram: exited onto map {map.MapId} at {continentPos.X:F1},{continentPos.Y:F1},{continentPos.Z:F1} ({distToArrival:F1} yd from {trip.IntendedTo.Name} arrival)");
                if (CanReport(session, player))
                {
                    PlayerbotAnomalies.Report(player, AnomalyArea, "arrived-wrong-station",
                        string.Create(CultureInfo.InvariantCulture,
                            $"intended={trip.IntendedTo.Name} map={map.MapId} distYards={distToArrival:F1} pos=({continentPos.X:F1},{continentPos.Y:F1},{continentPos.Z:F1}) expected=({trip.IntendedTo.Arrival.X:F1},{trip.IntendedTo.Arrival.Y:F1},{trip.IntendedTo.Arrival.Z:F1})"));
                }

                trip.Failure = wrongWhy;
                trip.FailureCategory = "wrong-station";
                trip.Stage = TramStage.Failed;
                failure = wrongWhy;
                return TramStep.Failed;
            }

            trip.Stage = TramStage.Done;
            return TramStep.Arrived;
        }

        TramPlatform exit = trip.To;
        if ((player.Movement.Flags & MovementFlags.OnTransport) != 0)
            PlayerbotMotion.SendStepOff(session, player, new Vector3(player.X, player.Y, player.Z), player.Orientation);
        if (!trip.ExitRefused && Walk(session, player, trip, exit.ExitCentre, options, elapsedMs, now, toward: true)) return TramStep.Traveling;

        // The exit cannot be walked into from here: the server sends the bot where the exit would (the trigger's own teleport).
        PlayerbotMovementControl.Stop(session, player);
        trip.FailureCategory ??= "no-way-to-exit";
        trip.Failure ??= string.Create(CultureInfo.InvariantCulture,
            $"tram: no way to the {exit.Name} exit from {player.X:F1},{player.Y:F1},{player.Z:F1}");
        bool canReportTeleport = CanReport(session, player);
        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        if (teleports is null || !teleports.TeleportTo(player, exit.ExitTeleport.TargetMap, exit.ExitTeleport.TargetX, exit.ExitTeleport.TargetY,
                exit.ExitTeleport.TargetZ, exit.ExitTeleport.TargetOrientation))
        {
            // Reported once; a bot still on the tram map begins a fresh way out at a later think.
            trip.Stage = TramStage.Failed;
            failure = trip.Failure + "; the exit teleport was refused";
            return TramStep.Failed;
        }

        if (canReportTeleport)
        {
            PlayerbotAnomalies.Report(player, AnomalyArea, "teleport-out",
                string.Create(CultureInfo.InvariantCulture,
                    $"reason=exit-unreachable exit={exit.Name} pos=({player.X:F1},{player.Y:F1},{player.Z:F1}) targetMap={exit.ExitTeleport.TargetMap}"));
        }

        return TramStep.Traveling;
    }

    /// <summary>
    /// Follow (planning when needed, one path query per <see cref="PlanRetryMs"/> while none is found) a route to <paramref name="target"/>;
    /// false when the bot got no <see cref="ProgressYards"/> closer within <see cref="NoProgressMs"/>, or the navigation refused
    /// <see cref="MaxRefusedPlans"/> plans in a row (<see cref="TramTrip.RefusedPlans"/>; a pending tile read does not count, a route resets
    /// it). <paramref name="toward"/>: a goal that may lie beyond one route (<see cref="PlayerbotNavigation.PlanToward"/>).
    /// </summary>
    private static bool Walk(WorldSession session, Player player, TramTrip trip, Vector3 target, PlayerbotOptions options, uint elapsedMs, uint now,
        bool toward)
    {
        PlayerbotThinkProbe.Phase("tram walk");
        float left = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), target);
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
        // One path query per PlanRetryMs while none is found (the mesh tiles ahead may still be loading, or being read in the
        // background: a pending plan is retried like a refused one, nothing is set aside); the progress bound above ends it.
        if (unchecked((int)(trip.NextPlanMs - now)) > 0) return true;
        PlayerbotThinkProbe.Phase("tram plan");
        PlanOutcome planned = toward ? PlayerbotNavigation.PlanToward(player, target, options, ref trip.Tiles, now, out PlayerbotRoute? next)
            : PlayerbotNavigation.PlanExact(player, target, options, ref trip.Tiles, now, out next);
        if (planned != PlanOutcome.Route || next is null)
        {
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            return planned != PlanOutcome.Refused || ++trip.RefusedPlans < MaxRefusedPlans;
        }

        trip.RefusedPlans = 0;
        trip.Route = next;
        PlayerbotNavigation.TryAdvance(session, next, options, elapsedMs, now);
        return true;
    }

    private static TramStep Fail(WorldSession session, Player player, TramTrip trip, uint now, string why, string category)
    {
        PlayerbotMovementControl.Stop(session, player);
        if (player.Map?.MapId == TramNetwork.TramMapId)
            return LeaveAfterFailure(trip, trip.Network.NearestOnTramMap(new Vector3(player.X, player.Y, player.Z)), now, why, category);
        PlayerbotAreaTriggers.AllowTeleportsTo(player, trip.PreviousTeleportTarget);
        trip.Failure = why;
        trip.FailureCategory ??= category;
        trip.Stage = TramStage.Failed;
        return TramStep.Failed;
    }

    /// <summary>On the tram map: note the failure and leave by the exit of <paramref name="platform"/>; reported once the bot is out.</summary>
    private static TramStep LeaveAfterFailure(TramTrip trip, TramPlatform platform, uint now, string why, string category)
    {
        trip.Failure ??= why;
        trip.FailureCategory ??= category;
        trip.To = platform;
        trip.Route = null;
        trip.Best = float.PositiveInfinity;
        trip.BestMs = now;
        Enter(trip, TramStage.ToExit, now);
        return TramStep.Traveling;
    }

    private static bool CanReport(WorldSession session, Player player)
        => !session.IsInChaosProbe && player.IsAlive && player.IsInWorld && !player.IsLoggingOut;

    private static void Enter(TramTrip trip, TramStage stage, uint now)
    {
        if (trip.Stage != stage)
        {
            trip.Stage = stage;
            trip.StageMs = now;
            trip.NextPlanMs = now;
            trip.RefusedPlans = 0;
            trip.Best = float.PositiveInfinity;
            trip.BestMs = now;
            if (stage is TramStage.ToExit or TramStage.ToEntrance) trip.Route = null;
        }
    }

    private static bool SendAboard(WorldSession session, Player player, GameObject car, Vector3 carAt, Vector3 offset)
    {
        Vector3 world = Seat(car, carAt, offset);
        return PlayerbotMotion.SendAboard(session, player, car.Guid.Value, world, player.Orientation, offset, 0f);
    }

    /// <summary>The world position of <paramref name="offset"/> on a car at <paramref name="carAt"/> (vmangos CalculatePassengerPosition).</summary>
    internal static Vector3 Seat(GameObject car, Vector3 carAt, Vector3 offset)
    {
        float x = offset.X, y = offset.Y, z = offset.Z, o = 0;
        ShipTransport.CalculatePassengerPosition(ref x, ref y, ref z, ref o, carAt.X, carAt.Y, carAt.Z, car.Orientation);
        return new Vector3(x, y, z);
    }

    private static Vector3? Position(GameObjectMapSystem system, GameObject car, uint progress)
        => system.ElevatorPositionAt(car, progress) is { } at ? new Vector3(at.X, at.Y, at.Z) : null;

    /// <summary>Where the car stands <paramref name="aheadMs"/> from now when it is not moving then (null while it moves).</summary>
    private static Vector3? Standing(GameObjectMapSystem system, GameObject car, uint aheadMs)
    {
        if (PositionAhead(system, car, aheadMs) is not { } a || PositionAhead(system, car, aheadMs + 500) is not { } b) return null;
        return Vector3.Distance(a, b) <= StandingYards ? a : null;
    }

    /// <summary>How long the car standing at <paramref name="at"/> stays there (ms, bounded by the cycle).</summary>
    private static uint StandingFor(GameObjectMapSystem system, GameObject car, Vector3 at, uint cycle)
    {
        for (uint ahead = SampleMs; ahead < cycle; ahead += SampleMs)
        {
            if (PositionAhead(system, car, ahead) is not { } then || Vector3.Distance(then, at) > StandingYards)
                return ahead - SampleMs;
        }

        return cycle;
    }

    /// <summary>The car's position <paramref name="aheadMs"/> from now, bridging one missing sample on the cycle boundary.</summary>
    private static Vector3? PositionAhead(GameObjectMapSystem system, GameObject car, uint aheadMs)
        => Sample(ahead => system.ElevatorProgressAhead(car, ahead) is { } p ? Position(system, car, p) : null, aheadMs);

    /// <summary>Whether a car at <paramref name="at"/> stands at <paramref name="platform"/>'s end of the tunnel (nearer its landing).</summary>
    private static bool AtEnd(Vector3 at, TramPlatform platform, TramPlatform other)
        => Vector2.Distance(Flat(at), Flat(platform.Landing))
            < Vector2.Distance(Flat(at), Flat(other.Landing));

    /// <summary>
    /// The first stop of a car at <paramref name="here"/>'s end of the tunnel from now (sampled every 250 ms over one cycle of
    /// <paramref name="cycle"/> ms plus the stop itself): when it arrives, when it leaves, and where it stands. Null when it never stops
    /// there. <paramref name="positionAt"/> gives the car's position that many ms from now. A sample with no position is kept when the
    /// samples beside it stand in the same place: the animation offset is missing at progress 0, and the Deeprun Tram's platform dwell
    /// runs from 129,333 ms through that boundary to 867 ms of the next cycle (one stop, not two).
    /// </summary>
    internal static TramDock? NextDock(uint cycle, Func<uint, Vector3?> positionAt, Vector3 here, Vector3 other, Func<TramDock, bool>? usable = null)
    {
        ArgumentNullException.ThrowIfNull(positionAt);
        if (cycle == 0) return null;
        bool AtHere(Vector3 p) => Vector2.Distance(Flat(p), Flat(here)) < Vector2.Distance(Flat(p), Flat(other));
        Vector3? StandingAt(uint t)
        {
            if (Sample(positionAt, t) is not { } a || Sample(positionAt, t + 500) is not { } b) return null;
            return Vector3.Distance(a, b) <= StandingYards && AtHere(a) ? a : null;
        }

        // Two cycles: a stop that is too short or too soon in this one comes round again in the next.
        for (uint t = 0; t <= 2 * cycle; t += SampleMs)
        {
            if (StandingAt(t) is not { } at) continue;
            uint end = t;
            while (end - t < cycle && StandingAt(end + SampleMs) is { } still && Vector3.Distance(still, at) <= StandingYards) end += SampleMs;
            // It still stands at 'end' and its position 500 ms later is the same, so it leaves after end + 500.
            var dock = new TramDock(t, end + 500, at);
            if (usable is null || usable(dock)) return dock;
            t = end;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="positionAt"/> at <paramref name="t"/>, or the position shared by the samples one <see cref="SampleMs"/> to either
    /// side when that one sample is missing. The animation offset is null at progress 0, so a sample on the cycle boundary is a hole in a
    /// dwell that continues there.
    /// </summary>
    private static Vector3? Sample(Func<uint, Vector3?> positionAt, uint t)
    {
        if (positionAt(t) is { } at) return at;
        if (t >= SampleMs && positionAt(t - SampleMs) is { } before && positionAt(t + SampleMs) is { } after
            && Vector3.Distance(before, after) <= StandingYards)
            return before;
        return null;
    }
}
