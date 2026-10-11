using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>The two levels an elevator joins.</summary>
internal enum ElevatorLevel { Low, High }

/// <summary>
/// One elevator car: a GAMEOBJECT_TYPE_TRANSPORT spawn whose TransportAnimation.dbc path moves it straight up and down
/// (<see cref="ElevatorNetwork.CarOf"/>). <see cref="Low"/> and <see cref="High"/> are where it stands at the bottom and the top of its
/// path: the spawn position plus the lowest and the highest node offset (the animation turns only about the vertical axis here, which
/// leaves a vertical offset as it is; vmangos ElevatorTransport::Update, <see cref="GameObjectMapSystem.ElevatorPositionAt"/>).
/// </summary>
internal sealed record ElevatorCar(uint MapId, uint SpawnGuid, uint Entry, Vector3 Low, Vector3 High, uint CycleMs)
{
    public Vector3 At(ElevatorLevel level) => level == ElevatorLevel.Low ? Low : High;
}

/// <summary>
/// One lift: the cars that shuttle between the same two levels side by side (the Great Lift and the Thunder Bluff lifts are each a pair of
/// counterweighted cars, one starting at the top and one at the bottom; <see cref="ElevatorNetwork.Build"/>).
/// </summary>
internal sealed class ElevatorLift
{
    internal ElevatorLift(uint mapId, IReadOnlyList<ElevatorCar> cars)
    {
        MapId = mapId;
        Cars = cars;
        Centre = new Vector2(cars.Average(c => c.Low.X), cars.Average(c => c.Low.Y));
        LowZ = cars.Average(c => c.Low.Z);
        HighZ = cars.Average(c => c.High.Z);
    }

    public uint MapId { get; }

    public IReadOnlyList<ElevatorCar> Cars { get; }

    /// <summary>The middle of the cars' shafts, flat.</summary>
    public Vector2 Centre { get; }

    public float LowZ { get; }

    public float HighZ { get; }

    public float LevelZ(ElevatorLevel level) => level == ElevatorLevel.Low ? LowZ : HighZ;

    /// <summary>For logs and stall reasons: "the Great Lift at -4668,-1839 (-44 to 85)".</summary>
    public string Name => string.Create(CultureInfo.InvariantCulture,
        $"{ElevatorNetwork.RegionsOf(this)?.Name ?? "the lift"} at {Centre.X:F0},{Centre.Y:F0} ({LowZ:F0} to {HighZ:F0})");

    public static ElevatorLevel Other(ElevatorLevel level) => level == ElevatorLevel.Low ? ElevatorLevel.High : ElevatorLevel.Low;
}

/// <summary>An AreaTable region: a zone, or one area of it (<see cref="Area"/> 0: any area of the zone).</summary>
internal readonly record struct ElevatorRegion(uint Zone, uint Area = 0)
{
    public bool Contains((uint Zone, uint Area) at) => Zone != 0 && at.Zone == Zone && (Area == 0 || at.Area == Area);
}

/// <summary>
/// The regions a lift joins (<see cref="ElevatorRegions.Known"/>): the region of its lower level and of its upper one, and whether the upper
/// one is a mesa (<see cref="MesaTop"/>: its region reaches down round the mesa's foot, where only ground at least
/// <see cref="ElevatorNetwork.MesaYards"/> below the top is the upper level; lower, it is the foot).
/// </summary>
internal sealed record ElevatorRegions(uint MapId, Vector2 At, ElevatorRegion Low, ElevatorRegion High, bool MesaTop, string Name)
{
    /// <summary>A lift whose centre lies this close (flat) to a row's is that row's.</summary>
    public const float MatchYards = 30f;

    /// <summary>
    /// The one hand-written part, kept to the regions the data cannot give. The lifts, their cars, levels and timings come from the world
    /// database and TransportAnimation.dbc (<see cref="ElevatorNetwork.Build"/>); which AreaTable region lies above and which below does
    /// not: the terrain's area map is flat (one area per 33-yard cell, the same above and below a cliff), and it does not follow the cliffs
    /// (measured on the 5875 terrain and AreaTable.dbc, <c>PlayerbotElevatorRealTerrainTests</c>: Thousand Needles' "The Great Lift" area,
    /// 485, covers the top landing of the Great Lift up to 100 yards into The Barrens; Thunder Bluff's zone, 1638, covers the Mulgore ground
    /// round the mesa's foot; the Thunder Bluff lifts' lower stop, 68.6, stands on a platform 9 yards above that ground). Zone and area ids
    /// are the client's AreaTable.dbc (5875): 17 The Barrens, 400 Thousand Needles, 484 Freewind Post (in 400), 215 Mulgore, 1638 Thunder
    /// Bluff. A lift without a row joins nothing and no bot is sent to it.
    /// </summary>
    public static readonly ElevatorRegions[] Known =
    [
        new(1, new Vector2(-4668f, -1839f), new ElevatorRegion(400), new ElevatorRegion(17), MesaTop: false, "the Great Lift"),
        new(1, new Vector2(-5391f, -2497f), new ElevatorRegion(400), new ElevatorRegion(400, 484), MesaTop: true, "the Freewind Post lift"),
        new(1, new Vector2(-1297f, 188f), new ElevatorRegion(215), new ElevatorRegion(1638), MesaTop: true, "the Thunder Bluff lift"),
        new(1, new Vector2(-1033f, -39f), new ElevatorRegion(215), new ElevatorRegion(1638), MesaTop: true, "the Thunder Bluff lift"),
    ];
}

/// <summary>
/// The AreaTable zone and area at a position of one map, for <see cref="ElevatorNetwork.Choose"/>: the map's terrain
/// (<see cref="Map.GetZoneAndAreaId"/>, vmangos TerrainInfo::GetZoneAndAreaId); tests give a fixture.
/// </summary>
internal sealed class ElevatorSurvey(Func<Vector3, (uint Zone, uint Area)> regionAt)
{
    public (uint Zone, uint Area) RegionAt(Vector3 at) => float.IsFinite(at.X) && float.IsFinite(at.Y) ? regionAt(at) : (0, 0);

    public static ElevatorSurvey Of(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new ElevatorSurvey(at => map.GetZoneAndAreaId(at.X, at.Y, at.Z));
    }
}

/// <summary>
/// The elevators of the world as its data describes them: every spawn of a GAMEOBJECT_TYPE_TRANSPORT template whose
/// TransportAnimation.dbc path rises or falls at least <see cref="MinRiseYards"/> and stays within <see cref="MaxSwayYards"/> of its shaft
/// (so not the Deeprun Tram cars, which travel 2,480 yards along the tunnel), grouped into lifts. No coordinates are written down here:
/// classic-db 5875 with the 5875 client's DBC gives, on Kalimdor, the Great Lift (spawns 16874 entry 11898 at the top, 85.4, and 16876
/// entry 11899 at the bottom, -44.1, each travelling 129.3 yards), the Freewind Post lift (16875/16877), and the two Thunder Bluff lifts
/// (18298/18435, entries 4170/4171; 20639/20640, entries 47296/47297). Which regions each lift joins is the one thing written down
/// (<see cref="ElevatorRegions.Known"/>). Immutable; cached per content and animation catalog (<see cref="Of"/>).
/// </summary>
internal sealed class ElevatorNetwork
{
    /// <summary>A car whose path rises less than this is a door or a plunger, not a lift (the Gnomeregan plunger moves 12.1 yards).</summary>
    public const float MinRiseYards = 20f;

    /// <summary>A car whose path leaves its shaft by more than this, flat, is no lift (a tram car, a moving platform).</summary>
    public const float MaxSwayYards = 1f;

    /// <summary>Cars this close to each other (flat) between the same two levels make one lift (the Great Lift's two are 22.6 yards apart).</summary>
    public const float PairYards = 40f;

    /// <summary>Two cars' levels within this of each other are the same levels.</summary>
    public const float PairLevelYards = 5f;

    /// <summary>
    /// Within this of a lift (flat), the level a position is on is the one its height is nearer, when within <see cref="LevelYards"/> of it:
    /// the terrain's areas there do not tell the top from the foot (<see cref="ElevatorRegions.Known"/>).
    /// </summary>
    public const float NearLiftYards = 150f;

    /// <summary>A height within this of a level, near the lift, is on that level.</summary>
    public const float LevelYards = 20f;

    /// <summary>On a mesa lift, ground of the upper region this far or more below the top is the mesa's foot (<see cref="ElevatorRegions.MesaTop"/>).</summary>
    public const float MesaYards = 30f;

    /// <summary>The network built last for a content, with the animations it was built from (a world has one catalog).</summary>
    private static readonly ConditionalWeakTable<GameObjectContent, Tuple<TransportAnimationCatalog, ElevatorNetwork>> Cache = [];

    private ElevatorNetwork(IReadOnlyList<ElevatorLift> lifts) => Lifts = lifts;

    public IReadOnlyList<ElevatorLift> Lifts { get; }

    /// <summary>The network of a world's game object content and elevator animations (no allocation once cached: asked every plan).</summary>
    public static ElevatorNetwork Of(GameObjectContent content, TransportAnimationCatalog animations)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(animations);
        if (Cache.TryGetValue(content, out Tuple<TransportAnimationCatalog, ElevatorNetwork>? cached) && ReferenceEquals(cached.Item1, animations))
            return cached.Item2;
        ElevatorNetwork network = Build(content.Spawns, content.FindTemplate, animations);
        Cache.AddOrUpdate(content, Tuple.Create(animations, network));
        return network;
    }

    /// <summary>
    /// The lifts of <paramref name="spawns"/>: each spawn that is a car (<see cref="CarOf"/>), grouped with the cars within
    /// <see cref="PairYards"/> whose levels match within <see cref="PairLevelYards"/>, in spawn order.
    /// </summary>
    public static ElevatorNetwork Build(IEnumerable<GameObjectSpawn> spawns, Func<uint, GameObjectTemplate?> templateOf,
        TransportAnimationCatalog animations)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(templateOf);
        ArgumentNullException.ThrowIfNull(animations);
        var groups = new List<List<ElevatorCar>>();
        foreach (GameObjectSpawn spawn in spawns.OrderBy(s => s.MapId).ThenBy(s => s.Guid))
        {
            if (CarOf(spawn, templateOf(spawn.Entry), animations) is not { } car) continue;
            List<ElevatorCar>? lift = groups.FirstOrDefault(g => g[0].MapId == car.MapId
                && Vector2.Distance(Flat(g[0].Low), Flat(car.Low)) <= PairYards
                && MathF.Abs(g[0].Low.Z - car.Low.Z) <= PairLevelYards && MathF.Abs(g[0].High.Z - car.High.Z) <= PairLevelYards);
            if (lift is null) groups.Add([car]);
            else lift.Add(car);
        }

        return new ElevatorNetwork([.. groups.Select(g => new ElevatorLift(g[0].MapId, g))]);
    }

    /// <summary>
    /// The car a spawn is, or null: a GAMEOBJECT_TYPE_TRANSPORT template with an animation, not SPAWN_FLAG_DISABLED, turned about the vertical
    /// axis only (rotation0 and rotation1 zero), whose nodes stay within <see cref="MaxSwayYards"/> of the shaft and span at least
    /// <see cref="MinRiseYards"/> vertically.
    /// </summary>
    internal static ElevatorCar? CarOf(GameObjectSpawn spawn, GameObjectTemplate? template, TransportAnimationCatalog animations)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        ArgumentNullException.ThrowIfNull(animations);
        if (template is null || (GameObjectType)template.Type != GameObjectType.Transport || (spawn.SpawnFlags & 0x02) != 0
            || spawn.Rotation0 != 0 || spawn.Rotation1 != 0 || !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z))
            return null;
        IReadOnlyList<TransportAnimationNode> nodes = animations.Nodes(spawn.Entry);
        uint cycle = animations.TotalTime(spawn.Entry);
        if (nodes.Count < 2 || cycle == 0) return null;
        float low = float.PositiveInfinity, high = float.NegativeInfinity, sway = 0;
        foreach (TransportAnimationNode node in nodes)
        {
            low = MathF.Min(low, node.Z);
            high = MathF.Max(high, node.Z);
            sway = MathF.Max(sway, MathF.Sqrt((node.X * node.X) + (node.Y * node.Y)));
        }

        if (sway > MaxSwayYards || high - low < MinRiseYards) return null;
        return new ElevatorCar(spawn.MapId, spawn.Guid, spawn.Entry, new Vector3(spawn.X, spawn.Y, spawn.Z + low),
            new Vector3(spawn.X, spawn.Y, spawn.Z + high), cycle);
    }

    /// <summary>The regions <paramref name="lift"/> joins (<see cref="ElevatorRegions.Known"/>), or null: no bot is sent to it.</summary>
    public static ElevatorRegions? RegionsOf(ElevatorLift lift)
    {
        ArgumentNullException.ThrowIfNull(lift);
        foreach (ElevatorRegions row in ElevatorRegions.Known)
            if (row.MapId == lift.MapId && Vector2.Distance(row.At, lift.Centre) <= ElevatorRegions.MatchYards) return row;
        return null;
    }

    /// <summary>
    /// Which level of <paramref name="lift"/> <paramref name="at"/> belongs to: near the lift (<see cref="NearLiftYards"/>) the level its
    /// height is nearer, when within <see cref="LevelYards"/> of it; elsewhere the level whose region it lies in (the upper region of a mesa
    /// lift only from <see cref="MesaYards"/> below its top up; lower down it is the foot); null for neither.
    /// </summary>
    public static ElevatorLevel? LevelOf(ElevatorLift lift, ElevatorRegions regions, ElevatorSurvey survey, Vector3 at)
    {
        ArgumentNullException.ThrowIfNull(lift);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(survey);
        if (Vector2.Distance(Flat(at), lift.Centre) <= NearLiftYards)
        {
            ElevatorLevel nearer = MathF.Abs(at.Z - lift.LowZ) <= MathF.Abs(at.Z - lift.HighZ) ? ElevatorLevel.Low : ElevatorLevel.High;
            if (MathF.Abs(at.Z - lift.LevelZ(nearer)) <= LevelYards) return nearer;
        }

        (uint Zone, uint Area) region = survey.RegionAt(at);
        if (regions.High.Contains(region)) return !regions.MesaTop || at.Z >= lift.HighZ - MesaYards ? ElevatorLevel.High : ElevatorLevel.Low;
        return regions.Low.Contains(region) ? ElevatorLevel.Low : null;
    }

    /// <summary>
    /// The lift a bot at <paramref name="bot"/> takes towards <paramref name="goal"/> on <paramref name="mapId"/>, and from which level, or
    /// null. A bot on top of a mesa lift (<see cref="ElevatorRegions.MesaTop"/>: Thunder Bluff, Freewind Post) whose goal is not up there
    /// rides it down whatever lies beyond: a mesa is left only by its lifts (quest 5361 is taken on Freewind Post and ends in Desolace,
    /// in neither of the lift's regions). Otherwise a lift whose regions put the bot on one level and the goal on the other
    /// (<see cref="LevelOf"/>); of several (Thunder Bluff has two), the one the shorter straight-line way passes. The caller first makes
    /// sure the goal is not walkable without it.
    /// </summary>
    public (ElevatorLift Lift, ElevatorLevel From)? Choose(uint mapId, Vector3 bot, Vector3 goal, ElevatorSurvey survey)
    {
        ArgumentNullException.ThrowIfNull(survey);
        if (!Finite(bot) || !Finite(goal)) return null;
        (ElevatorLift Lift, ElevatorLevel From)? best = null;
        float bestVia = float.PositiveInfinity;
        foreach (ElevatorLift lift in Lifts)
        {
            if (lift.MapId != mapId || RegionsOf(lift) is not { MesaTop: true } regions
                || LevelOf(lift, regions, survey, bot) != ElevatorLevel.High || LevelOf(lift, regions, survey, goal) == ElevatorLevel.High) continue;
            float via = Vector2.Distance(Flat(bot), lift.Centre);
            if (via < bestVia)
            {
                bestVia = via;
                best = (lift, ElevatorLevel.High);
            }
        }

        if (best is not null) return best; // off the mesa first
        foreach (ElevatorLift lift in Lifts)
        {
            if (lift.MapId != mapId || RegionsOf(lift) is not { } regions) continue;
            if (LevelOf(lift, regions, survey, bot) is not { } from || LevelOf(lift, regions, survey, goal) is not { } to || from == to) continue;
            float via = Vector2.Distance(Flat(bot), lift.Centre) + Vector2.Distance(lift.Centre, Flat(goal));
            if (via < bestVia)
            {
                bestVia = via;
                best = (lift, from);
            }
        }

        return best;
    }
    internal static Vector2 Flat(Vector3 value) => new(value.X, value.Y);

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>The stages of one bot's lift ride (<see cref="PlayerbotElevator"/>).</summary>
internal enum ElevatorStage { ToStop, Waiting, Aboard, Falling, Done, Failed }

/// <summary>What <see cref="PlayerbotElevator.Drive"/> did this think.</summary>
internal enum ElevatorStep { Idle, Traveling, Arrived, Failed }

/// <summary>A bot's ride on one lift (<see cref="PlayerbotElevator"/>).</summary>
internal sealed class ElevatorTrip(ElevatorLift lift, ElevatorLevel from, Vector3 goal, uint nowMs)
{
    public ElevatorLift Lift { get; } = lift;
    public ElevatorLevel From { get; set; } = from;
    public ElevatorLevel To { get; set; } = ElevatorLift.Other(from);
    public Vector3 Goal { get; set; } = goal;
    public ElevatorStage Stage { get; set; } = ElevatorStage.ToStop;
    public uint StageMs { get; set; } = nowMs;
    public string? Failure { get; set; }
    public PlayerbotRoute? Route { get; set; }
    public float Best { get; set; } = float.PositiveInfinity;
    public uint BestMs { get; set; } = nowMs;
    public uint NextPlanMs { get; set; }
    public ElevatorCar? Car { get; set; }
    public GameObject? CarObject { get; set; }
    public Vector3 Landing { get; set; }
    public Vector3 Offset { get; set; }
    public uint LastBeatMs { get; set; }
    public bool LeftStop { get; set; }
    public Vector3 FallLanding { get; set; }
    public uint FallTimeMs { get; set; }
    public float FallYards { get; set; }

    /// <summary>
    /// The ride ends at the next stop, at either level: a controller took the bot over while it was aboard (<see cref="PlayerbotElevator.Cancel"/>),
    /// or it was found on a car at login or a revival (<see cref="PlayerbotElevator.Settle"/>). <see cref="PlayerbotElevator.HoldsForLanding"/>.
    /// </summary>
    public bool Alighting { get; set; }

    /// <summary>Rescues refused so far (<see cref="PlayerbotElevator.Rescue"/>); the trip fails after <c>MaxRescueTries</c>.</summary>
    public int RescueTries { get; set; }
}

/// <summary>
/// Autonomous bots ride the elevators (docs/areas/playbots.md, "Travel by elevator") the way a player does: walk to the landing beside a
/// car's shaft at their level, wait for the car, step aboard when it stands there, stay on while it climbs or sinks, and step off at the
/// other level onto the landing nearest their destination. A trip is begun by a goal that travels (<see cref="PlayerbotWorldDestinations"/>)
/// when <see cref="ElevatorNetwork.Choose"/> says the destination lies on the lift's other level and the navigation mesh has no way there
/// without it, and is then driven by the brain before anything else (<see cref="PlayerbotBrain"/>), so nothing else moves the bot meanwhile.
/// It follows the Deeprun Tram rider (<see cref="PlayerbotTram"/>), without its map change: the lifts stand on the continent.
/// <para>
/// Aboard, the bot reports what a client standing on a transport reports: MSG_MOVE_HEARTBEAT with MOVEFLAG_ONTRANSPORT, the car's GUID and
/// its offset on the car, at the world position the car's animation (<see cref="GameObjectMapSystem.ElevatorPosition(GameObject)"/>) and the
/// offset give, every <see cref="HeartbeatMs"/> (<see cref="PlayerbotMotion.SendAboard"/>); observers' clients place it on their own car by
/// the offset. Nothing teleports it.
/// </para>
/// <para>
/// Fails closed, with a stall reason and no retry for <see cref="RetryAfterFailureMs"/>: no car of the lift in the world, no floor beside a
/// car at the bot's level, no way to that landing (no progress for <see cref="NoProgressMs"/>), no car standing at the landing within two
/// cycles and 30 s. Aboard: a car that does not stand at the other level within a cycle and 30 s is left where it stands next at either
/// level; one that leaves the world under the bot (or a bot knocked off it in the shaft) drops the bot (<see cref="Drop"/>).
/// </para>
/// <para>
/// Under a car the static collision data has no floor but the bottom of the shaft (a type-11 car is a dynamic object, in no vmap): so
/// nothing here steps a bot off a car where it stands or drops it to "the floor below", except <see cref="Drop"/> when the car is gone,
/// and then only for a fall that costs less than <see cref="MaxFallHealthPercent"/> percent of the bot's health (vmangos fall damage);
/// otherwise the bot is placed on the nearest landing (<see cref="Rescue"/>, a same-map teleport, as vmangos' undermap recall and the
/// spell system's last-safe recall do). A controller taking a riding bot over waits until it alights at the next stop
/// (<see cref="HoldsForLanding"/>); a bot found on a car at login or revival rides to the next stop, one found hanging in a shaft is
/// placed on a landing (<see cref="Settle"/>); a corpse killed aboard loses the transport, and its observers are told.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal static class PlayerbotElevator
{
    /// <summary>
    /// Half the car's width: the collision bounds of Elevatorcar.m2 (GameObjectDisplayInfo 360, every "Mesa Elevator"; vmaps
    /// <c>temp_gameobject_models</c>: x -5.14 .. 5.05, y -5.37 .. 5.10, z -2.65 .. 13.11 round the object's position, its floor at 0).
    /// </summary>
    internal const float CarHalfYards = 5.0f;

    /// <summary>How far inside the car's sides a passenger stands at least.</summary>
    internal const float InsideMarginYards = 1.5f;

    /// <summary>The distances from a car's centre at which its landing is looked for (the floor beside the car at a level).</summary>
    internal static readonly float[] LandingRadii = [CarHalfYards + 1f, CarHalfYards + 2.5f, CarHalfYards + 4f];

    /// <summary>A landing's floor lies within this of the level, vertically.</summary>
    internal const float LandingFloorYards = 2.5f;

    /// <summary>Within this of the lift (flat) the bot picks its car and the landing beside it; farther away it walks towards the lift.</summary>
    internal const float ApproachYards = 60f;

    /// <summary>A direct route ending this close (flat) to the goal reaches it (<see cref="TryBegin"/>).</summary>
    internal const float ReachedYards = 3f;

    /// <summary>A bot this close to its landing (flat) has arrived at it.</summary>
    internal const float AtLandingYards = 3f;

    /// <summary>A car stands at a level when it is within this of it vertically and its position 500 ms later is the same.</summary>
    internal const float AtLevelYards = 1.5f;

    internal const float StandingYards = 0.05f;

    /// <summary>A heartbeat aboard at least this often, as a moving client reports itself.</summary>
    internal const uint HeartbeatMs = PlayerbotMotion.HeartbeatIntervalMs;

    /// <summary>Walking that gains less than this on its landing within <see cref="NoProgressMs"/> is stuck.</summary>
    internal const float ProgressYards = 5f;

    internal const uint NoProgressMs = 60_000;

    /// <summary>A failed trip is not tried again for this long.</summary>
    internal const uint RetryAfterFailureMs = 600_000;

    /// <summary>A stop the car leaves sooner than this is not boarded (the next one is waited for).</summary>
    internal const uint BoardMarginMs = 1_000;

    /// <summary>After a path query found nothing, the next one waits this long.</summary>
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A bot this far (or more) above the floor under it, off a car, is falling.</summary>
    internal const float AirborneYards = 3f;

    /// <summary>A bot found within this of a car's height, inside its footprint, stands on it (<see cref="Settle"/>).</summary>
    internal const float OnCarYards = 2.5f;

    /// <summary>How long <see cref="Settle"/> waits for a car's object to be in the world (its grid loading) before it rescues the bot.</summary>
    internal const uint SettleWaitMs = 10_000;

    /// <summary><see cref="Drop"/> lets a bot fall only when the fall costs less than this percent of its current health.</summary>
    internal const int MaxFallHealthPercent = 50;

    /// <summary>Refused rescues (<see cref="Rescue"/>) before <see cref="Drop"/> gives up and fails the trip.</summary>
    private const int MaxRescueTries = 3;

    private const uint SampleMs = 250;

    private static readonly ConditionalWeakTable<Player, ElevatorTrip> Trips = [];
    private static readonly ConditionalWeakTable<Player, StrongBox<uint>> FailedAt = [];
    private static readonly ConditionalWeakTable<WorldRuntime, Func<Map, ElevatorSurvey>> Surveys = [];
    private static readonly ConditionalWeakTable<Map, ElevatorSurvey> MapSurveys = [];

    /// <summary>The bot's trip, done or not (inspection and tests).</summary>
    internal static ElevatorTrip? TripOf(Player player) => Trips.TryGetValue(player, out ElevatorTrip? trip) ? trip : null;

    /// <summary>Whether a trip is under way (not done, not failed).</summary>
    internal static bool IsTraveling(Player player)
        => Trips.TryGetValue(player, out ElevatorTrip? trip) && trip.Stage is not (ElevatorStage.Done or ElevatorStage.Failed);

    /// <summary>The reason the bot's last trip failed (inspection), null after a trip that arrived.</summary>
    internal static string? LastFailure(Player player)
        => Trips.TryGetValue(player, out ElevatorTrip? trip) && trip.Stage == ElevatorStage.Failed ? trip.Failure : null;

    /// <summary>Survey a world's maps with <paramref name="survey"/> instead of their terrain (tests: a fixture world has no terrain files).</summary>
    internal static void UseSurvey(WorldRuntime world, Func<Map, ElevatorSurvey> survey)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(survey);
        Surveys.AddOrUpdate(world, survey);
    }

    private static ElevatorSurvey SurveyOf(WorldRuntime world, Map map)
        => Surveys.TryGetValue(world, out Func<Map, ElevatorSurvey>? custom) ? MapSurveys.GetValue(map, m => custom(m))
            : MapSurveys.GetValue(map, ElevatorSurvey.Of);

    /// <summary>The world's lifts (null without game object content or elevator animations).</summary>
    internal static ElevatorNetwork? NetworkOf(WorldSession session)
        => session.Services.GetService<GameObjectLootFeature>() is { } objects && objects.ElevatorAnimations.Count > 0
            ? ElevatorNetwork.Of(objects.Content, objects.ElevatorAnimations) : null;

    /// <summary>
    /// Begin a trip towards <paramref name="goal"/> when a lift is the way there (<see cref="ElevatorNetwork.Choose"/>), the navigation mesh
    /// has no way to a goal within one route's reach without it, and no trip failed lately. True when a trip is under way afterwards.
    /// </summary>
    internal static bool TryBegin(WorldSession session, Player player, Vector3 goal, PlayerbotOptions options)
        => TryBegin(session, player, goal, options, out _);

    /// <param name="walk">When false: the route the mesh check planned to the goal, when it reaches it (null when no check ran). The caller
    /// walks it instead of planning again, so a think still costs one path query.</param>
    internal static bool TryBegin(WorldSession session, Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? walk)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        walk = null;
        if (IsTraveling(player)) return true;
        if (!player.IsAlive || !player.IsInWorld || player.Map is not { } map) return false;
        uint now = session.World.NowMs;
        if (FailedAt.TryGetValue(player, out StrongBox<uint>? failed) && unchecked(now - failed.Value) < RetryAfterFailureMs) return false;
        if (NetworkOf(session) is not { } network) return false;
        Vector3 bot = new(player.X, player.Y, player.Z);
        if (network.Choose(map.MapId, bot, goal, SurveyOf(session.World, map)) is not { } choice) return false;
        // The lift only when the mesh has no way: a goal within one route's reach that the mesh joins to the bot is walked to. A route
        // that stops short of it (the mesh's nearest point, at the cliff's foot under a mesa's top) is no way there.
        if (Vector3.Distance(bot, goal) <= options.MaxRouteYards && PlayerbotNavigation.TryPlan(player, goal, options, out PlayerbotRoute? direct)
            && direct is { Points.Count: > 0 } && Vector2.Distance(ElevatorNetwork.Flat(direct.Points[^1]), ElevatorNetwork.Flat(goal)) <= ReachedYards
            && MathF.Abs(direct.Points[^1].Z - goal.Z) <= PlayerbotNavigation.ProjectedEndYards)
        {
            walk = direct;
            return false;
        }

        Begin(session, player, choice.Lift, choice.From, goal);
        return true;
    }

    /// <summary>Begin a trip on <paramref name="lift"/> from <paramref name="from"/> (tests; <see cref="TryBegin"/> chooses).</summary>
    internal static ElevatorTrip Begin(WorldSession session, Player player, ElevatorLift lift, ElevatorLevel from, Vector3 goal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(lift);
        var trip = new ElevatorTrip(lift, from, goal, session.World.NowMs) { NextPlanMs = session.World.NowMs };
        Trips.AddOrUpdate(player, trip);
        return trip;
    }

    /// <summary>
    /// A controller takes the bot over (a party AI) or it died. A bot on a car is never moved off it where it stands: under a car the only
    /// static floor is the bottom of the shaft (see the class remarks). Aboard and alive, the trip is kept and ends at the next stop at
    /// either level (<see cref="ElevatorTrip.Alighting"/>, its goal the bot's own position, so the nearest landing); the controller waits
    /// for it (<see cref="HoldsForLanding"/>). A fall under way lands on schedule. Dead, the trip ends and the corpse loses the transport,
    /// and its observers are told (<see cref="ClearTransport"/>). Any other stage ends the trip.
    /// </summary>
    internal static void Cancel(Player player)
    {
        if (!Trips.TryGetValue(player, out ElevatorTrip? trip)) return;
        if (!player.IsAlive)
        {
            Trips.Remove(player);
            if ((player.Movement.Flags & MovementFlags.OnTransport) != 0 && player.Session is WorldSession session) ClearTransport(session, player);
            return;
        }

        switch (trip.Stage)
        {
            case ElevatorStage.Aboard:
                if (!trip.Alighting)
                {
                    trip.Alighting = true;
                    trip.Goal = new Vector3(player.X, player.Y, player.Z);
                    trip.Failure = null;
                }

                return;
            case ElevatorStage.Falling:
                return;
            default:
                Trips.Remove(player);
                return;
        }
    }

    /// <summary>
    /// Whether the bot's ride holds it until it stands on a landing: aboard and alighting at the next stop, or falling. A controller that
    /// takes the bot over (<see cref="ManagedPlayerbotFeature"/>'s party AI) drives the ride (<see cref="Drive"/>) until this is false.
    /// </summary>
    internal static bool HoldsForLanding(Player player)
        => Trips.TryGetValue(player, out ElevatorTrip? trip)
            && (trip.Stage == ElevatorStage.Falling || (trip.Stage == ElevatorStage.Aboard && trip.Alighting));

    /// <summary>
    /// The stored movement without the transport block (Unit.ApplyMovement; the position, the body's world position, is unchanged: vmangos
    /// Player::KillPlayer and SetDeathState leave the transport alone and the corpse is made at the body's position on release). Observers
    /// are told: when a root order is pending (the death root of MapCombat.SetDeathState), the bot's acknowledgement carries this block and
    /// is relayed as MSG_MOVE_ROOT (RootAckHandler); otherwise (a bot already rooted when it died) it is relayed here as MSG_MOVE_HEARTBEAT.
    /// </summary>
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

    private sealed class SettleState
    {
        public bool Due = true;
        public uint SinceMs;
    }

    private static readonly ConditionalWeakTable<Player, SettleState> Settles = [];

    /// <summary>The bot was revived: at its next think without a trip it is settled again (<see cref="Settle"/>), as at a login.</summary>
    internal static void SettleAfterRevive(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        SettleState state = Settles.GetOrCreateValue(player);
        state.Due = true;
        state.SinceMs = 0;
    }

    /// <summary>
    /// At the first think without a trip after a login (per player object) or a revival (<see cref="SettleAfterRevive"/>): a bot standing
    /// inside a car's footprint (<see cref="CarHalfYards"/> flat of a car's shaft) is judged by the live car, never by the static floor.
    /// On the car (within <see cref="OnCarYards"/> of its height): it boards it again and rides to the next stop (<see cref="BoardFound"/>;
    /// true: a trip began). With the car elsewhere, or no car in the world after <see cref="SettleWaitMs"/>: it is placed on the nearest
    /// landing (<see cref="Rescue"/>). Outside every footprint nothing happens. <paramref name="waiting"/>: the car is not in the world
    /// yet and the bot is held still meanwhile.
    /// </summary>
    internal static bool Settle(WorldSession session, Player player, Map map, uint now, out bool waiting)
    {
        waiting = false;
        SettleState state = Settles.GetOrCreateValue(player);
        if (!state.Due || !player.IsAlive) return false;
        if (NetworkOf(session) is not { } network)
        {
            state.Due = false;
            return false;
        }

        Vector3 at = new(player.X, player.Y, player.Z);
        foreach (ElevatorLift lift in network.Lifts)
        {
            if (lift.MapId != map.MapId) continue;
            foreach (ElevatorCar car in lift.Cars)
            {
                if (Vector2.Distance(ElevatorNetwork.Flat(car.Low), ElevatorNetwork.Flat(at)) > CarHalfYards) continue;
                if (state.SinceMs == 0) state.SinceMs = now == 0 ? 1u : now;
                GameObjectMapSystem? system = map.FindUpdater<GameObjectMapSystem>();
                GameObject? live = system is null ? null : FindCar(system, car);
                if (live is null || system!.ElevatorPosition(live) is not { } position)
                {
                    if (unchecked(now - state.SinceMs) < SettleWaitMs)
                    {
                        waiting = true;
                        return false;
                    }

                    state.Due = false;
                    if ((player.Movement.Flags & MovementFlags.OnTransport) != 0) ClearTransport(session, player);
                    string lost = $"elevator: in the shaft of {lift.Name} at login, its car not in the world after {SettleWaitMs / 1000}s";
                    if (!Rescue(session, player, map, lift, at, out _, lost)) LogRescueRefused(session, player, lost);
                    return false;
                }

                state.Due = false;
                var carAt = new Vector3(position.X, position.Y, position.Z);
                if (MathF.Abs(at.Z - carAt.Z) <= OnCarYards) return BoardFound(session, player, system, lift, car, live, carAt, at, now);
                if ((player.Movement.Flags & MovementFlags.OnTransport) != 0) ClearTransport(session, player);
                string hanging = string.Create(CultureInfo.InvariantCulture,
                    $"elevator: logged in hanging in the shaft of {lift.Name} at z {at.Z:F1} with the car at z {carAt.Z:F1}");
                if (!Rescue(session, player, map, lift, at, out _, hanging)) LogRescueRefused(session, player, hanging);
                return false;
            }
        }

        state.Due = false;
        return false;
    }

    /// <summary>
    /// A bot found on a car (<see cref="Settle"/>): it boards it at the same world position (the seat offset clamped inside the car as
    /// <see cref="Wait"/> clamps it; a change of flags, not of place) and alights at the next stop: the level the car stands at, else the
    /// one it is heading for (its position 500 ms ahead, rising: the top).
    /// </summary>
    private static bool BoardFound(WorldSession session, Player player, GameObjectMapSystem system, ElevatorLift lift, ElevatorCar car, GameObject live,
        Vector3 carAt, Vector3 at, uint now)
    {
        ElevatorLevel to = StandingAt(system, live, car.Low.Z, 0) is not null ? ElevatorLevel.Low
            : StandingAt(system, live, car.High.Z, 0) is not null ? ElevatorLevel.High
            : Position(system, live, 500) is { } ahead && ahead.Z > carAt.Z ? ElevatorLevel.High : ElevatorLevel.Low;
        float x = at.X, y = at.Y, z = carAt.Z, o = 0;
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, carAt.X, carAt.Y, carAt.Z, live.Orientation);
        float inside = CarHalfYards - InsideMarginYards;
        var offset = new Vector3(Math.Clamp(x, -inside, inside), Math.Clamp(y, -inside, inside), 0f);
        var trip = new ElevatorTrip(lift, ElevatorLift.Other(to), at, now)
        {
            Stage = ElevatorStage.Aboard, Alighting = true, LeftStop = true, Car = car, CarObject = live, Offset = offset, LastBeatMs = now,
        };
        Trips.AddOrUpdate(player, trip);
        SendAboard(session, player, live, carAt, offset);
        return true;
    }

    /// <summary>
    /// Place the bot on a landing of <paramref name="lift"/>: the one beside the car nearest the bot at the level nearer its height, then
    /// the other level; else its last safe position on this map (taken off any transport before it boarded); else its bind point. A
    /// same-map teleport (TeleportService: a near teleport that leaves the transport; observers get MSG_MOVE_TELEPORT), used only for this
    /// rescue: vmangos' undermap recall (MovementHandler.cpp:1116-1128) and the spell system's last-safe recall do the same. The teleport
    /// is refused only for a quest settlement under way, a far teleport under way or bad coordinates (TeleportService.Check): false then.
    /// </summary>
    private static bool Rescue(WorldSession session, Player player, Map map, ElevatorLift lift, Vector3 at, out string placed, string why)
    {
        placed = "";
        if (session.Services.GetService<TeleportFeature>()?.Teleports is not { } teleports) return false;
        ElevatorLevel nearer = MathF.Abs(at.Z - lift.LowZ) <= MathF.Abs(at.Z - lift.HighZ) ? ElevatorLevel.Low : ElevatorLevel.High;
        bool sent = false;
        foreach (ElevatorLevel level in (ElevatorLevel[])[nearer, ElevatorLift.Other(nearer)])
        {
            foreach (ElevatorCar car in lift.Cars.OrderBy(c => Vector2.DistanceSquared(ElevatorNetwork.Flat(c.Low), ElevatorNetwork.Flat(at))))
            {
                if (LandingBeside(map, car.At(level), at) is not { } landing) continue;
                if (teleports.TeleportTo(player, map.MapId, landing.X, landing.Y, landing.Z, player.Orientation))
                {
                    placed = $"placed on the {Word(level)} landing of {lift.Name}";
                    sent = true;
                }

                break;
            }

            if (sent) break;
        }

        if (!sent && player.LastSafePosition is { } safe && safe.MapId == map.MapId
            && teleports.TeleportTo(player, map.MapId, safe.X, safe.Y, safe.Z, safe.O))
        {
            placed = "placed at its last safe position";
            sent = true;
        }

        if (!sent && teleports.TeleportToHomebind(player))
        {
            placed = "sent to its bind point";
            sent = true;
        }

        if (sent)
        {
            session.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(PlayerbotElevator)).LogInformation(
                "Playerbot {Bot}: {Why}; {Placed}", player.Name, why, placed);
        }

        return sent;
    }

    /// <summary>
    /// A settle's rescue was refused (every teleport: TeleportService.Check). Settle does not try again (its turn is spent); the warning
    /// is the trace of a bot left where it stands.
    /// </summary>
    private static void LogRescueRefused(WorldSession session, Player player, string why)
        => session.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(PlayerbotElevator)).LogWarning(
            "Playerbot {Bot}: {Why}; the rescue teleport was refused, the bot is left where it stands", player.Name, why);

    /// <summary>
    /// One think of the trip, before anything else the brain does. <see cref="ElevatorStep.Idle"/>: no trip (the brain goes on); without
    /// a trip a bot just logged in or revived is settled first (<see cref="Settle"/>), which may begin one. <see cref="ElevatorStep.Arrived"/>
    /// and <see cref="ElevatorStep.Failed"/> are reported once; <paramref name="failure"/> then says why.
    /// </summary>
    internal static ElevatorStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        failure = null;
        if (!player.IsInWorld || player.Map is not { } map) return IsTraveling(player) ? ElevatorStep.Traveling : ElevatorStep.Idle;
        uint now = session.World.NowMs;
        if (!Trips.TryGetValue(player, out ElevatorTrip? trip) || trip.Stage is ElevatorStage.Done or ElevatorStage.Failed)
        {
            if (!player.IsAlive) return ElevatorStep.Idle;
            if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return ElevatorStep.Idle;
            if (!Settle(session, player, map, now, out bool waiting)) return waiting ? ElevatorStep.Traveling : ElevatorStep.Idle;
            trip = Trips.TryGetValue(player, out ElevatorTrip? begun) ? begun : null;
            if (trip is null) return ElevatorStep.Idle;
        }

        if (!player.IsAlive)
        {
            Cancel(player);
            return ElevatorStep.Idle;
        }

        if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return ElevatorStep.Traveling;
        ElevatorStep step = map.MapId != trip.Lift.MapId
            ? Fail(trip, string.Create(CultureInfo.InvariantCulture, $"elevator: left for map {map.MapId} on the way to {trip.Lift.Name}"))
            : trip.Stage switch
            {
                ElevatorStage.ToStop => ToStop(session, player, map, trip, options, elapsedMs, now),
                ElevatorStage.Waiting => Wait(session, player, map, trip, now),
                ElevatorStage.Aboard => Ride(session, player, map, trip, now),
                ElevatorStage.Falling => Fall(session, player, trip, now),
                _ => ElevatorStep.Idle,
            };
        switch (step)
        {
            case ElevatorStep.Arrived:
                trip.Stage = ElevatorStage.Done;
                trip.StageMs = now; // when it arrived (inspection)
                trip.Failure = null;
                break;
            case ElevatorStep.Failed:
                if (trip.Stage is ElevatorStage.ToStop or ElevatorStage.Waiting) PlayerbotMovementControl.Stop(session, player);
                trip.Stage = ElevatorStage.Failed;
                FailedAt.AddOrUpdate(player, new StrongBox<uint>(now));
                failure = trip.Failure ?? "elevator: the trip failed (no reason recorded)";
                break;
        }

        return step;
    }

    private static ElevatorStep ToStop(WorldSession session, Player player, Map map, ElevatorTrip trip, PlayerbotOptions options, uint elapsedMs,
        uint now)
    {
        Vector3 bot = new(player.X, player.Y, player.Z);
        if (trip.Car is null || trip.CarObject is not { IsInWorld: true } || !ReferenceEquals(trip.CarObject.Map, map))
        {
            Vector3 stop = trip.Lift.Cars[0].At(trip.From);
            if (Vector2.Distance(ElevatorNetwork.Flat(bot), trip.Lift.Centre) > ApproachYards)
            {
                // Far from the lift (its cars are grid objects, in the world only round players near them): walk towards its stop.
                if (Walk(session, player, trip, stop, options, elapsedMs, now)) return ElevatorStep.Traveling;
                return Fail(trip, string.Create(CultureInfo.InvariantCulture,
                    $"elevator: no way to {trip.Lift.Name} ({Word(trip.From)} level) from {bot.X:F1},{bot.Y:F1},{bot.Z:F1}"));
            }

            float best = trip.Best;
            uint bestMs = trip.BestMs;
            if (!ChooseCar(player, map, trip, options, now, out string? why)) return Fail(trip, why!);
            (trip.Best, trip.BestMs) = (best, bestMs); // the progress bound runs on across the choice
        }

        if (Vector2.Distance(ElevatorNetwork.Flat(bot), ElevatorNetwork.Flat(trip.Landing)) <= AtLandingYards
            && MathF.Abs(bot.Z - trip.Landing.Z) <= LandingFloorYards + 1f)
        {
            if (!PlayerbotMovementControl.Stop(session, player)) return ElevatorStep.Traveling;
            Enter(trip, ElevatorStage.Waiting, now);
            return ElevatorStep.Traveling;
        }

        if (!Walk(session, player, trip, trip.Landing, options, elapsedMs, now))
        {
            return Fail(trip, string.Create(CultureInfo.InvariantCulture,
                $"elevator: no way to the {Word(trip.From)} landing of {trip.Lift.Name} from {bot.X:F1},{bot.Y:F1},{bot.Z:F1}"));
        }

        return ElevatorStep.Traveling;
    }

    /// <summary>
    /// Pick the car of the lift that stands at the bot's level soonest after the bot can be beside it (a straight walk at its speed), with
    /// the landing beside it nearest the bot. False (and why) when no car of the lift is in the world or none has a floor beside it.
    /// </summary>
    private static bool ChooseCar(Player player, Map map, ElevatorTrip trip, PlayerbotOptions options, uint now, out string? failure)
    {
        failure = null;
        trip.Car = null;
        trip.CarObject = null;
        trip.Route = null;
        if (map.FindUpdater<GameObjectMapSystem>() is not { } system)
        {
            failure = $"elevator: no game objects on map {map.MapId}";
            return false;
        }

        Vector3 bot = new(player.X, player.Y, player.Z);
        float speed = MathF.Max(1f, options.MoveSpeed);
        var candidates = new List<(ElevatorCar Car, GameObject Object, Vector3 Landing, uint Ready)>();
        bool anyCar = false;
        foreach (ElevatorCar car in trip.Lift.Cars)
        {
            if (FindCar(system, car) is not { } live || system.ElevatorCycle(live) == 0) continue;
            anyCar = true;
            Vector3 stop = car.At(trip.From);
            if (LandingBeside(map, stop, bot) is not { } landing) continue;
            uint walk = (uint)(Vector2.Distance(ElevatorNetwork.Flat(bot), ElevatorNetwork.Flat(landing)) / speed * 1000f);
            uint ready = NextStop(system, live, stop.Z, walk) ?? uint.MaxValue;
            candidates.Add((car, live, landing, ready));
        }

        if (candidates.Count == 0)
        {
            failure = anyCar
                ? string.Create(CultureInfo.InvariantCulture, $"elevator: no floor beside a car of {trip.Lift.Name} at its {Word(trip.From)} level")
                : $"elevator: no car of {trip.Lift.Name} is in the world (none spawned, or no animation)";
            return false;
        }

        (ElevatorCar chosen, GameObject obj, Vector3 at, _) = candidates.OrderBy(c => c.Ready).ThenBy(c => c.Car.SpawnGuid).First();
        trip.Car = chosen;
        trip.CarObject = obj;
        trip.Landing = at;
        trip.Best = float.PositiveInfinity;
        trip.BestMs = now;
        return true;
    }

    private static ElevatorStep Wait(WorldSession session, Player player, Map map, ElevatorTrip trip, uint now)
    {
        if (trip.Car is not { } car || trip.CarObject is not { IsInWorld: true } live || !ReferenceEquals(live.Map, map)
            || map.FindUpdater<GameObjectMapSystem>() is not { } system)
        {
            Enter(trip, ElevatorStage.ToStop, now); // the car left the world: choose again
            trip.Car = null;
            return ElevatorStep.Traveling;
        }

        uint cycle = system.ElevatorCycle(live);
        if (cycle == 0) return Fail(trip, $"elevator: the car of {trip.Lift.Name} has no animation");
        if (unchecked(now - trip.StageMs) > (2 * cycle) + 30_000)
        {
            return Fail(trip, string.Create(CultureInfo.InvariantCulture,
                $"elevator: no car stood at the {Word(trip.From)} landing of {trip.Lift.Name} within {((2 * cycle) + 30_000) / 1000}s"));
        }

        float level = car.At(trip.From).Z;
        if (StandingAt(system, live, level, 0) is not { } at) return ElevatorStep.Traveling;
        Vector3 bot = new(player.X, player.Y, player.Z);
        if (Vector2.Distance(ElevatorNetwork.Flat(bot), ElevatorNetwork.Flat(at)) > LandingRadii[^1] + AtLandingYards
            || MathF.Abs(bot.Z - at.Z) > LandingFloorYards + 1f)
        {
            // Not beside it (pushed away while waiting): walk back to a landing.
            Enter(trip, ElevatorStage.ToStop, now);
            trip.Car = null;
            return ElevatorStep.Traveling;
        }

        if (StandsFor(system, live, at, cycle) < BoardMarginMs) return ElevatorStep.Traveling; // about to leave: the next stop
        if (!PlayerbotMovementControl.Stop(session, player)) return ElevatorStep.Traveling;

        // Step aboard: the point of the car's floor nearest to where the bot stands.
        float x = bot.X, y = bot.Y, z = at.Z, o = 0;
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, at.X, at.Y, at.Z, live.Orientation);
        float inside = CarHalfYards - InsideMarginYards;
        var offset = new Vector3(Math.Clamp(x, -inside, inside), Math.Clamp(y, -inside, inside), 0f);
        if (!SendAboard(session, player, live, at, offset)) return ElevatorStep.Traveling;
        trip.Offset = offset;
        trip.LastBeatMs = now;
        trip.LeftStop = false;
        Enter(trip, ElevatorStage.Aboard, now);
        return ElevatorStep.Traveling;
    }

    private static ElevatorStep Ride(WorldSession session, Player player, Map map, ElevatorTrip trip, uint now)
    {
        if (trip.Car is not { } car || trip.CarObject is not { IsInWorld: true } live || !ReferenceEquals(live.Map, map)
            || map.FindUpdater<GameObjectMapSystem>() is not { } system || system.ElevatorPosition(live) is not { } position)
        {
            return Drop(session, player, map, trip, now, $"elevator: the car of {trip.Lift.Name} left the world under the bot");
        }

        Vector3 carAt = new(position.X, position.Y, position.Z);
        if ((player.Movement.Flags & MovementFlags.OnTransport) == 0)
        {
            // Something took the bot off the car (a knock-back, a fear): on the floor it is just off; in the shaft it falls.
            return Drop(session, player, map, trip, now, $"elevator: knocked off the car of {trip.Lift.Name}");
        }

        uint cycle = system.ElevatorCycle(live);
        float fromZ = car.At(trip.From).Z;
        if (!trip.LeftStop && MathF.Abs(carAt.Z - fromZ) > AtLevelYards) trip.LeftStop = true;
        // At the other level (alighting: at the next stop at either level), step off onto the landing nearest the destination (alighting:
        // nearest the bot).
        foreach (ElevatorLevel level in trip.Alighting ? (ElevatorLevel[])[trip.To, trip.From] : [trip.To])
        {
            if (!trip.Alighting && !trip.LeftStop) break;
            if (StandingAt(system, live, car.At(level).Z, 0) is not { } stop) continue;
            if (LandingBeside(map, stop, trip.Goal) is not { } ground)
            {
                if (trip.Alighting) continue;
                // No floor there: ride back and get off where the bot got on (the trip fails there).
                trip.Failure ??= string.Create(CultureInfo.InvariantCulture,
                    $"elevator: no floor beside the car of {trip.Lift.Name} at its {Word(trip.To)} level");
                (trip.From, trip.To) = (trip.To, trip.From);
                trip.LeftStop = false;
                return Beat(session, player, live, carAt, trip, now);
            }

            if (!PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation)) return ElevatorStep.Traveling;
            return trip.Failure is null ? ElevatorStep.Arrived : ElevatorStep.Failed;
        }

        if (unchecked(now - trip.StageMs) > cycle + 30_000)
        {
            // The car never stood at the other level (it should within one cycle): off where it stands next, at either level.
            trip.Failure ??= string.Create(CultureInfo.InvariantCulture,
                $"elevator: the car of {trip.Lift.Name} did not stand at its {Word(trip.To)} level within {(cycle + 30_000) / 1000}s");
            foreach (ElevatorLevel level in (ElevatorLevel[])[ElevatorLevel.Low, ElevatorLevel.High])
            {
                if (StandingAt(system, live, car.At(level).Z, 0) is { } here && LandingBeside(map, here, trip.Goal) is { } off)
                    return PlayerbotMotion.SendStepOff(session, player, off, player.Orientation) ? ElevatorStep.Failed : ElevatorStep.Traveling;
            }

            if (unchecked(now - trip.StageMs) > (2 * cycle) + 30_000)
                return Drop(session, player, map, trip, now, trip.Failure); // never stands anywhere: off, as a client would fall off
        }

        return Beat(session, player, live, carAt, trip, now);
    }

    private static ElevatorStep Beat(WorldSession session, Player player, GameObject car, Vector3 carAt, ElevatorTrip trip, uint now)
    {
        if (unchecked(now - trip.LastBeatMs) >= HeartbeatMs && SendAboard(session, player, car, carAt, trip.Offset)) trip.LastBeatMs = now;
        return ElevatorStep.Traveling;
    }

    /// <summary>
    /// Off a car that is gone, that never stands at a level, or that the bot was knocked off: the trip fails with <paramref name="why"/>.
    /// On a known floor (within <see cref="AirborneYards"/>) the bot steps off there; above a known floor it falls to it
    /// (<see cref="ElevatorStage.Falling"/>) only when the fall costs less than <see cref="MaxFallHealthPercent"/> percent of its health
    /// (<see cref="Survivable"/>) and is not a pit's bottom (<see cref="InPit"/>); otherwise, or over no known floor, it is placed on a
    /// landing (<see cref="Rescue"/>), retried while the
    /// teleport is refused, up to <see cref="MaxRescueTries"/> times.
    /// </summary>
    private static ElevatorStep Drop(WorldSession session, Player player, Map map, ElevatorTrip trip, uint now, string why)
    {
        trip.Failure ??= why;
        Vector3 at = new(player.X, player.Y, player.Z);
        float floor = FloorBelow(map, at);
        bool pit = float.IsFinite(floor) && InPit(trip.Lift, at, floor);
        if (!pit && float.IsFinite(floor) && at.Z - floor < AirborneYards)
        {
            if ((player.Movement.Flags & MovementFlags.OnTransport) != 0
                && !PlayerbotMotion.SendStepOff(session, player, at with { Z = floor }, player.Orientation))
                return ElevatorStep.Traveling;
            return ElevatorStep.Failed;
        }

        if (pit || !float.IsFinite(floor) || !Survivable(session, player, at.Z - floor))
        {
            if (Rescue(session, player, map, trip.Lift, at, out string placed, trip.Failure))
            {
                trip.Failure = $"{trip.Failure}; {placed}";
                return ElevatorStep.Failed;
            }

            if (++trip.RescueTries < MaxRescueTries) return ElevatorStep.Traveling;
            trip.Failure = $"{trip.Failure}; the rescue teleport was refused {MaxRescueTries} times";
            return ElevatorStep.Failed;
        }

        // In the air: off the car (if still on it), the step that begins the fall, then the landing once the fall's time has passed.
        if ((player.Movement.Flags & MovementFlags.OnTransport) != 0 && !PlayerbotMotion.SendStepOff(session, player, at, player.Orientation))
            return ElevatorStep.Traveling;
        if (!PlayerbotMotion.StartFall(session, player, at, player.Orientation)) return ElevatorStep.Traveling;
        trip.FallYards = at.Z - floor;
        trip.FallLanding = at with { Z = floor };
        trip.FallTimeMs = (uint)MathF.Ceiling(MathF.Sqrt(2f * trip.FallYards / PlayerbotFollowDrop.Gravity) * 1000f);
        Enter(trip, ElevatorStage.Falling, now);
        return ElevatorStep.Traveling;
    }

    private static ElevatorStep Fall(WorldSession session, Player player, ElevatorTrip trip, uint now)
    {
        if (unchecked(now - trip.StageMs) < trip.FallTimeMs) return ElevatorStep.Traveling;
        if (!PlayerbotMotion.Land(session, player, trip.FallLanding, player.Orientation, trip.FallTimeMs) && player.IsAlive)
            return ElevatorStep.Traveling;
        trip.Failure = string.Create(CultureInfo.InvariantCulture, $"{trip.Failure}; fell {trip.FallYards:F1} yards");
        return ElevatorStep.Failed;
    }

    /// <summary>
    /// Whether <paramref name="floor"/>, under <paramref name="at"/>, is the bottom of a shaft below its car's lowest stop: inside a car's
    /// footprint (<see cref="CarHalfYards"/> of its shaft, flat) and more than <see cref="PitYards"/> under the car's lower level. The real
    /// Great Lift's foot is such a pit (the static floor at -49.4, 5.3 to 5.5 yards under the stops; an enclosed recess no way leads out
    /// of): a bot dropped into it would stay there until its next login, so <see cref="Drop"/> places it on the landing instead.
    /// </summary>
    private static bool InPit(ElevatorLift lift, Vector3 at, float floor)
    {
        foreach (ElevatorCar car in lift.Cars)
        {
            if (Vector2.Distance(ElevatorNetwork.Flat(car.Low), ElevatorNetwork.Flat(at)) <= CarHalfYards && floor < car.Low.Z - PitYards)
                return true;
        }

        return false;
    }

    /// <summary>A floor in a car's footprint this far (or more) under its lower stop is a pit (<see cref="InPit"/>).</summary>
    internal const float PitYards = 1f;

    /// <summary>
    /// Whether a fall of <paramref name="yards"/> costs the bot less than <see cref="MaxFallHealthPercent"/> percent of its current health:
    /// vmangos' fall damage (FallDamageCalculator, Player::HandleFall) at the world's fall damage rate, without safe fall (the worst case).
    /// </summary>
    private static bool Survivable(WorldSession session, Player player, float yards)
    {
        uint damage = FallDamageCalculator.Damage(yards, 0, player.MaxHealth, LocomotionEnvironment.For(session.World).Options.RateDamageFall);
        return (ulong)damage * 100 < (ulong)player.Health * MaxFallHealthPercent;
    }

    /// <summary>The floor under <paramref name="at"/> (models and terrain), or NaN where none is known.</summary>
    private static float FloorBelow(Map map, Vector3 at)
    {
        float floor = map.Collision.GetHeight(at.X, at.Y, at.Z + 0.5f, useModels: true, maxSearchDistance: 500f);
        return float.IsFinite(floor) && floor > TerrainTile.InvalidHeight && floor <= at.Z + 0.5f ? floor : float.NaN;
    }

    /// <summary>
    /// Follow (planning when needed, one path query per <see cref="PlanRetryMs"/> while none is found) a route towards
    /// <paramref name="target"/>; false when the bot got no <see cref="ProgressYards"/> closer within <see cref="NoProgressMs"/>.
    /// </summary>
    private static bool Walk(WorldSession session, Player player, ElevatorTrip trip, Vector3 target, PlayerbotOptions options, uint elapsedMs,
        uint now)
    {
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
        if (unchecked((int)(trip.NextPlanMs - now)) > 0) return true;
        if (!PlayerbotNavigation.TryPlanToward(player, target, options, out PlayerbotRoute? next) || next is null)
        {
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            return true;
        }

        trip.Route = next;
        PlayerbotNavigation.TryAdvance(session, next, options, elapsedMs, now);
        return true;
    }

    private static ElevatorStep Fail(ElevatorTrip trip, string why)
    {
        trip.Failure ??= why;
        return ElevatorStep.Failed;
    }

    private static void Enter(ElevatorTrip trip, ElevatorStage stage, uint now)
    {
        if (trip.Stage == stage) return;
        trip.Stage = stage;
        trip.StageMs = now;
        trip.NextPlanMs = now;
        trip.Best = float.PositiveInfinity;
        trip.BestMs = now;
        trip.Route = null;
    }

    private static string Word(ElevatorLevel level) => level == ElevatorLevel.Low ? "lower" : "upper";

    private static bool SendAboard(WorldSession session, Player player, GameObject car, Vector3 carAt, Vector3 offset)
        => PlayerbotMotion.SendAboard(session, player, car.Guid.Value, PlayerbotTram.Seat(car, carAt, offset), player.Orientation, offset, 0f);

    private static readonly ConditionalWeakTable<ElevatorCar, WeakReference<GameObject>> LiveCars = [];

    /// <summary>
    /// The car's object in the map (a grid object: only while a grid round it is loaded). The object found last is kept per car and reused
    /// while it is still in this map's world; only then are the map's objects scanned.
    /// </summary>
    private static GameObject? FindCar(GameObjectMapSystem system, ElevatorCar car)
    {
        if (LiveCars.TryGetValue(car, out WeakReference<GameObject>? known) && known.TryGetTarget(out GameObject? cached) && IsCar(cached))
            return cached;
        foreach (GameObject go in system.GameObjects)
        {
            if (!IsCar(go)) continue;
            LiveCars.AddOrUpdate(car, new WeakReference<GameObject>(go));
            return go;
        }

        return null;

        bool IsCar(GameObject go) => go.IsInWorld && ReferenceEquals(go.Map, system.Map) && go.Spawn is { } spawn && spawn.Guid == car.SpawnGuid
            && go.Entry == car.Entry;
    }

    /// <summary>
    /// The floor beside a car standing at <paramref name="stop"/> (<see cref="LandingRadii"/> from its centre, 16 directions, within
    /// <see cref="LandingFloorYards"/> of its level), nearest <paramref name="toward"/>; null when there is none.
    /// </summary>
    internal static Vector3? LandingBeside(Map map, Vector3 stop, Vector3 toward)
    {
        Vector3? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (float radius in LandingRadii)
        {
            for (int i = 0; i < 16; i++)
            {
                float angle = i * MathF.PI / 8f;
                float x = stop.X + (radius * MathF.Cos(angle)), y = stop.Y + (radius * MathF.Sin(angle));
                float floor = map.Collision.GetHeight(x, y, stop.Z + 3f);
                if (!float.IsFinite(floor) || floor <= TerrainTile.InvalidHeight || MathF.Abs(floor - stop.Z) > LandingFloorYards) continue;
                float distance = Vector2.Distance(new Vector2(x, y), ElevatorNetwork.Flat(toward));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Vector3(x, y, floor);
                }
            }

            if (best is not null) return best; // the nearest ring with a floor: the bot steps the least
        }

        return best;
    }

    private static Vector3? Position(GameObjectMapSystem system, GameObject car, uint aheadMs)
        => system.ElevatorProgressAhead(car, aheadMs) is { } progress && system.ElevatorPositionAt(car, progress) is { } at
            ? new Vector3(at.X, at.Y, at.Z) : null;

    /// <summary>Where the car stands <paramref name="aheadMs"/> from now when it is then at <paramref name="levelZ"/> and not moving (else null).</summary>
    private static Vector3? StandingAt(GameObjectMapSystem system, GameObject car, float levelZ, uint aheadMs)
    {
        if (Position(system, car, aheadMs) is not { } a || Position(system, car, aheadMs + 500) is not { } b) return null;
        return Vector3.Distance(a, b) <= StandingYards && MathF.Abs(a.Z - levelZ) <= AtLevelYards ? a : null;
    }

    /// <summary>How long the car standing at <paramref name="at"/> stays there (ms, bounded by the cycle).</summary>
    private static uint StandsFor(GameObjectMapSystem system, GameObject car, Vector3 at, uint cycle)
    {
        for (uint ahead = SampleMs; ahead < cycle; ahead += SampleMs)
        {
            if (Position(system, car, ahead) is not { } then || Vector3.Distance(then, at) > StandingYards) return ahead - SampleMs;
        }

        return cycle;
    }

    /// <summary>
    /// When the car next stands at <paramref name="levelZ"/> long enough to board (<see cref="BoardMarginMs"/>), at or after
    /// <paramref name="afterMs"/> from now (ms from now), sampled every 250 ms over two cycles; null when it never does.
    /// </summary>
    private static uint? NextStop(GameObjectMapSystem system, GameObject car, float levelZ, uint afterMs)
    {
        uint cycle = system.ElevatorCycle(car);
        if (cycle == 0) return null;
        for (uint t = 0; t <= 2 * cycle; t += SampleMs)
        {
            if (StandingAt(system, car, levelZ, afterMs + t) is not { } at) continue;
            uint stays = 0;
            while (stays < cycle && Position(system, car, afterMs + t + stays + SampleMs) is { } then && Vector3.Distance(then, at) <= StandingYards)
                stays += SampleMs;
            if (stays + 500 >= BoardMarginMs) return afterMs + t;
            t += stays;
        }

        return null;
    }
}
