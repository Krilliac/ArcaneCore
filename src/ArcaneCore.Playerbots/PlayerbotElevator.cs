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
using ArcaneCore.Kernel.Diagnostics;
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
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

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

    /// <summary>A stable id of this lift for per-bot bookkeeping (<see cref="PlayerbotTransitBackoff"/>): its map and shaft centre, rounded to the yard.</summary>
    public uint Key => unchecked((uint)HashCode.Combine(MapId, (int)MathF.Round(Centre.X), (int)MathF.Round(Centre.Y)));

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
internal sealed record ElevatorRegions(uint MapId, Vector2 At, ElevatorRegion Low, ElevatorRegion High, bool MesaTop, string Name, bool VaultLow = false)
{
    /// <summary>A lift whose centre lies this close (flat) to a row's is that row's.</summary>
    public const float MatchYards = 30f;

    /// <summary>
    /// The one hand-written part, kept to the regions the data cannot give. The lifts, their cars, levels and timings come from the world
    /// database and TransportAnimation.dbc (<see cref="ElevatorNetwork.Build"/>); which AreaTable region lies above and which below does
    /// not: the terrain's area map is flat (one area per 33-yard cell, the same above and below a cliff), and it does not follow the cliffs
    /// (measured on the 5875 terrain and AreaTable.dbc, <c>PlayerbotElevatorRealTerrainTests</c>: Thousand Needles' "The Great Lift" area,
    /// 485, covers the top landing of the Great Lift up to 100 yards into The Barrens; Thunder Bluff's zone, 1638, covers the Mulgore ground
    /// round the mesa's foot; the Thunder Bluff lifts' lower stop, 68.6, stands on a platform 9 yards above that ground; Undercity's three
    /// lifts stand under the Ruins of Lordaeron, area 153 in zone 85 Tirisfal Glades). Zone and area ids
    /// are the client's AreaTable.dbc (5875): 17 The Barrens, 400 Thousand Needles, 484 Freewind Post (in 400), 215 Mulgore, 1638 Thunder
    /// Bluff, 85 Tirisfal Glades, 1497 Undercity. A lift without a row joins nothing and no bot is sent to it.
    /// </summary>
    public static readonly ElevatorRegions[] Known =
    [
        new(1, new Vector2(-4668f, -1839f), new ElevatorRegion(400), new ElevatorRegion(17), MesaTop: false, "the Great Lift"),
        new(1, new Vector2(-5391f, -2497f), new ElevatorRegion(400), new ElevatorRegion(400, 484), MesaTop: true, "the Freewind Post lift"),
        new(1, new Vector2(-1297f, 188f), new ElevatorRegion(215), new ElevatorRegion(1638), MesaTop: true, "the Thunder Bluff lift"),
        new(1, new Vector2(-1033f, -39f), new ElevatorRegion(215), new ElevatorRegion(1638), MesaTop: true, "the Thunder Bluff lift"),
        new(0, new Vector2(1596f, 292f), new ElevatorRegion(1497), new ElevatorRegion(85), MesaTop: false, "the Undercity lift", VaultLow: true),
        new(0, new Vector2(1544f, 241f), new ElevatorRegion(1497), new ElevatorRegion(85), MesaTop: false, "the Undercity lift", VaultLow: true),
        new(0, new Vector2(1595f, 189f), new ElevatorRegion(1497), new ElevatorRegion(85), MesaTop: false, "the Undercity lift", VaultLow: true),
    ];

    /// <summary>True when <paramref name="at"/> on <paramref name="mapId"/> is an enclosed vault lift (<see cref="VaultLow"/>: Undercity).</summary>
    public static bool IsVaultLift(uint mapId, Vector3 at)
    {
        Vector2 flat = new(at.X, at.Y);
        foreach (ElevatorRegions row in Known)
            if (row.VaultLow && row.MapId == mapId && Vector2.Distance(row.At, flat) <= MatchYards) return true;
        return false;
    }
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

    /// <summary>Within this of a vault lift (flat), ground at or below <see cref="VaultMaxZ"/> lies in the underground vault (<see cref="ElevatorRegions.VaultLow"/>: Undercity).</summary>
    public const float VaultRadiusYards = 420f;

    /// <summary>On a vault lift, ground at or below this height within <see cref="VaultRadiusYards"/> is the underground level (<see cref="ElevatorRegions.VaultLow"/>).</summary>
    public const float VaultMaxZ = -5f;

    /// <summary>The network built last for a content, with the animations it was built from (a world has one catalog).</summary>
    private static readonly ConditionalWeakTable<GameObjectContent, Tuple<TransportAnimationCatalog, ElevatorNetwork>> Cache = [];

    internal ElevatorNetwork(IReadOnlyList<ElevatorLift> lifts) => Lifts = lifts;

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
        // The cars first, then the order: the first call runs on the world thread (ElevatorNetwork.Of caches it) and the world has hundreds
        // of thousands of game object spawns, of which a handful are lifts; sorting all of them first cost the first bot to ask a long tick.
        var cars = new List<ElevatorCar>();
        foreach (GameObjectSpawn spawn in spawns)
            if (CarOf(spawn, templateOf(spawn.Entry), animations) is { } found) cars.Add(found);
        foreach (ElevatorCar car in cars.OrderBy(c => c.MapId).ThenBy(c => c.SpawnGuid))
        {
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
        if (regions.VaultLow)
        {
            float dist = Vector2.Distance(Flat(at), lift.Centre);
            if (at.Z <= VaultMaxZ && (regions.Low.Contains(region) || dist <= VaultRadiusYards))
            {
                return ElevatorLevel.Low;
            }

            if (at.Z > VaultMaxZ && (regions.High.Contains(region) || regions.Low.Contains(region) || dist <= VaultRadiusYards))
            {
                return ElevatorLevel.High;
            }

            return null;
        }

        if (regions.High.Contains(region)) return !regions.MesaTop || at.Z >= lift.HighZ - MesaYards ? ElevatorLevel.High : ElevatorLevel.Low;
        return regions.Low.Contains(region) ? ElevatorLevel.Low : null;
    }

    /// <summary>
    /// The lift a bot at <paramref name="bot"/> takes towards <paramref name="goal"/> on <paramref name="mapId"/>, and from which level, or
    /// null. A bot on top of a mesa lift (<see cref="ElevatorRegions.MesaTop"/>: Thunder Bluff, Freewind Post) whose goal is not up there
    /// rides it down whatever lies beyond: a mesa is left only by its lifts (quest 5361 is taken on Freewind Post and ends in Desolace,
    /// in neither of the lift's regions). Otherwise a lift whose regions put the bot on one level and the goal on the other
    /// (<see cref="LevelOf"/>); of several (Thunder Bluff has two, Undercity has three), the one the shorter straight-line way passes. The caller first makes
    /// sure the goal is not walkable without it.
    /// </summary>
    public (ElevatorLift Lift, ElevatorLevel From)? Choose(uint mapId, Vector3 bot, Vector3 goal, ElevatorSurvey survey)
    {
        ArgumentNullException.ThrowIfNull(survey);
        if (!bot.IsFinite() || !goal.IsFinite()) return null;
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
            float toLift = Vector2.Distance(Flat(bot), lift.Centre);
            float via = regions.VaultLow && (from == ElevatorLevel.High ? toLift <= NearLiftYards : toLift <= PlayerbotElevator.ApproachYards)
                ? toLift
                : toLift + Vector2.Distance(lift.Centre, Flat(goal));
            if (via < bestVia)
            {
                bestVia = via;
                best = (lift, from);
            }
        }

        return best;
    }
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
    private PlayerbotRoute? _route;

    /// <summary>The route the trip walks. Letting go of one (null or another route) marks it released
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.</summary>
    public PlayerbotRoute? Route
    {
        get => _route;
        set
        {
            if (ReferenceEquals(_route, value)) return;
            if (_route is { } old) old.Released = true;
            _route = value;
        }
    }

    /// <summary>Puts back a route that <see cref="Route"/> was cleared of for a moment (ChooseCar clears it, which marks it released): the
    /// route is held again, so it is no longer released. A method rather than the setter alone because the setter marks the route it
    /// drops released, and putting the same route back would leave that mark on it (a keep-think would then stop the bot).</summary>
    public void RestoreRoute(PlayerbotRoute? route)
    {
        Route = route;
        if (route is not null) route.Released = false;
    }
    public float Best { get; set; } = float.PositiveInfinity;
    public float LegBest { get; set; } = float.PositiveInfinity;
    public uint BestMs { get; set; } = nowMs;
    public uint NextPlanMs { get; set; }

    /// <summary>The wait for navigation tiles still being read when the way to the lift is planned.</summary>
    public TileWait Tiles;

    /// <summary>Planning calls in a row that the navigation refused (not pending, not a route): <see cref="PlayerbotElevator.MaxRefusedPlans"/> fail the walk.</summary>
    public int RefusedPlans { get; set; }

    /// <summary>The lift was backed off for this trip's failure already (once per trip).</summary>
    public bool BackoffRecorded { get; set; }
    public ElevatorCar? Car { get; set; }
    public GameObject? CarObject { get; set; }
    public Vector3 Landing { get; set; }
    public Vector3 Offset { get; set; }
    public uint LastBeatMs { get; set; }
    public uint LastDriveMs { get; set; } = nowMs;
    public uint LastPumpMs { get; set; } = nowMs;
    public uint NextPumpChooseMs { get; set; }
    public ElevatorStep? PendingStep { get; set; }
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

    public uint BeganMs { get; } = nowMs;
    public Vector3 StartPosition { get; set; }
    public PlayerbotOptions? Options { get; set; }
    public bool HadRoute { get; set; }
    public bool StopActiveAtFrom { get; set; }
    public uint StopMaxStandsForMs { get; set; }
    public string? StopSkipReason { get; set; }
    public uint NextStopAtMs { get; set; }
    public int MissedStops { get; set; }
    public bool BoardWindowShortReported { get; set; }
    public bool WaitingInsideDoorReported { get; set; }
    public bool DetachedReported { get; set; }
    public bool LeftBehindReported { get; set; }
    public bool DestStopActive { get; set; }
    public string? DestSkipReason { get; set; }
    public uint NextDestStopAtMs { get; set; }
    public int PassedDestinations { get; set; }
    public ElevatorLevel? StepOffLevel { get; set; }

    /// <summary>Where the bot was sent when it stepped off (the landing ground); <see cref="PlayerbotElevator.Relocated"/> measures from it.</summary>
    public Vector3? StepOffPoint { get; set; }
    public bool PitFallReported { get; set; }
    public bool RescueRefusedReported { get; set; }
    public bool TripFailedReported { get; set; }
    public bool TripSlowReported { get; set; }
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
/// Fails closed, with a stall reason, and that lift waits out its own backoff (<see cref="PlayerbotTransitBackoff"/>; another lift is not
/// held back): no car of the lift in the world, no floor beside a
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
internal static partial class PlayerbotElevator
{
    /// <summary>
    /// Half the car's width: the collision bounds of Elevatorcar.m2 (GameObjectDisplayInfo 360, every "Mesa Elevator"; vmaps
    /// <c>temp_gameobject_models</c>: x -5.14 .. 5.05, y -5.37 .. 5.10, z -2.65 .. 13.11 round the object's position, its floor at 0).
    /// </summary>
    internal const float CarHalfYards = 5.0f;

    /// <summary>How far inside the car's sides a passenger stands at least.</summary>
    internal const float InsideMarginYards = 1.5f;

    /// <summary>The distances from a car's centre at which its landing is looked for (the floor beside the car at a level, including outside Undercity's 9..11 yd enclosed shaft doors).</summary>
    internal static readonly float[] LandingRadii =
    [
        CarHalfYards + 1f,
        CarHalfYards + 2.5f,
        CarHalfYards + 4f,
        CarHalfYards + 6.5f,
        CarHalfYards + 7.5f,
        CarHalfYards + 8.5f,
    ];

    /// <summary>
    /// On an enclosed vault lift (<see cref="ElevatorRegions.VaultLow"/>: Undercity), the shaft doors stand at 9.06..10.60 yards from the
    /// shaft centre and only open while the car stands at that level. Bots wait and alight outside the doors (11.5..13.5 yards from the
    /// shaft centre) so they never phase through a closed door or wait inside the shaft alcove.
    /// </summary>
    internal static readonly float[] VaultLandingRadii = [CarHalfYards + 6.5f, CarHalfYards + 7.5f, CarHalfYards + 8.5f];

    /// <summary>Outer radius of the closed Undercity vault lift doors (9.06..10.60 yards from the shaft centre).</summary>
    internal const float VaultDoorOuterYards = 10.6f;

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

    /// <summary>
    /// The walk to a lift fails after this many refused plans in a row (pending tile reads do not count; a route resets the count) instead
    /// of waiting out <see cref="NoProgressMs"/>; the lift is then backed off (<see cref="PlayerbotTransitBackoff"/>).
    /// </summary>
    internal const int MaxRefusedPlans = PlayerbotTaxi.MaxRefusedPlans;

    /// <summary>A stop the car leaves sooner than this is not boarded (the next one is waited for).</summary>
    internal const uint BoardMarginMs = 1_000;

    /// <summary>After a path query found nothing, the next one waits this long.</summary>
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A bot this far (or more) above the floor under it, off a car, is falling.</summary>
    internal const float AirborneYards = 3f;

    /// <summary>A bot found within this of a car's height, inside its footprint, stands on it (<see cref="Settle"/>).</summary>
    internal const float OnCarYards = 2.5f;

    /// <summary>
    /// Vertical gap between the car and a supposedly-aboard bot beyond which the car has left the bot behind. The bot's own height is
    /// only as fresh as its last heartbeat (<see cref="HeartbeatMs"/>, 500 ms) and a car moves up to ~25 yards a second, so a bot that
    /// rides correctly lags the car by up to ~9 yards (8.1 measured on a clean Great Lift ride); 25 yards is a bot missing a few beats.
    /// </summary>
    internal const float LeftBehindYards = 25f;

    /// <summary>How long <see cref="Settle"/> waits for a car's object to be in the world (its grid loading) before it rescues the bot.</summary>
    internal const uint SettleWaitMs = 10_000;

    /// <summary><see cref="Drop"/> lets a bot fall only when the fall costs less than this percent of its current health.</summary>
    internal const int MaxFallHealthPercent = 50;

    /// <summary>Refused rescues (<see cref="Rescue"/>) before <see cref="Drop"/> gives up and fails the trip.</summary>
    private const int MaxRescueTries = 3;

    private const uint SampleMs = 250;
    private const uint RescueWindowMs = 600_000;
    private const string AnomalyArea = "elevator";

    private static readonly ConditionalWeakTable<Player, ElevatorTrip> Trips = [];
    private static readonly ConditionalWeakTable<Player, List<uint>> RescueTimestamps = [];

    /// <summary>Each bot's wait for the navigation tiles of the direct walk <see cref="TryBegin"/> weighs against the lift.</summary>
    private static readonly ConditionalWeakTable<Player, StrongBox<TileWait>> TileWaits = [];
    private static readonly ConditionalWeakTable<WorldRuntime, Func<Map, ElevatorSurvey>> Surveys = [];
    private static readonly ConditionalWeakTable<Map, ElevatorSurvey> MapSurveys = [];

    /// <summary>The bot's trip, done or not (inspection and tests).</summary>
    internal static ElevatorTrip? TripOf(Player player) => Trips.TryGetValue(player, out ElevatorTrip? trip) ? trip : null;

    /// <summary>
    /// The trips under way (not done, not failed) of the players on <paramref name="map"/> (inspection: <c>.playerbot lift</c>). Only that
    /// map's own players are read: a trip is written by the thread updating its bot's map, so the caller must be on <paramref name="map"/>'s
    /// thread (the world thread outside the parallel maps phase); another map's trips are never touched.
    /// </summary>
    internal static IEnumerable<(Player Player, ElevatorTrip Trip)> ActiveTrips(Map map)
    {
        foreach (Player player in map.Players)
        {
            if (player.IsInWorld && Trips.TryGetValue(player, out ElevatorTrip? trip) && trip.Stage is not (ElevatorStage.Done or ElevatorStage.Failed))
            {
                yield return (player, trip);
            }
        }
    }

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
        if (NetworkOf(session) is not { } network) return false;
        Vector3 bot = new(player.X, player.Y, player.Z);
        if (network.Choose(map.MapId, bot, goal, SurveyOf(session.World, map)) is not { } choice) return false;
        // The lift only when the mesh has no way: a goal within one route's reach that the mesh joins to the bot is walked to. A route
        // that stops short of it (the mesh's nearest point, at the cliff's foot under a mesa's top) is no way there.
        PlanOutcome directPlan = PlanOutcome.Refused;
        PlayerbotRoute? direct = null;
        if (Vector3.Distance(bot, goal) <= options.MaxRouteYards)
        {
            StrongBox<TileWait> tiles = TileWaits.GetValue(player, static _ => new StrongBox<TileWait>());
            directPlan = PlayerbotNavigation.PlanExact(player, goal, options, ref tiles.Value, now, out direct);
            // A tile of the direct way is still being read: no verdict yet on whether the mesh joins the goal, so no lift either. The
            // caller plans its own walk, which meets the same tile and waits (for TileWait.MaxMs; then both stand as refused).
            if (directPlan == PlanOutcome.Pending) return false;
        }

        if (directPlan == PlanOutcome.Route && direct is { Points.Count: > 0 } && Vector2.Distance(Flat(direct.Points[^1]), Flat(goal)) <= ReachedYards
            && MathF.Abs(direct.Points[^1].Z - goal.Z) <= PlayerbotNavigation.ProjectedEndYards)
        {
            walk = direct;
            return false;
        }

        // A lift that failed lately (its own backoff) is not ridden again yet; another lift and the mesh walk above are never held back.
        if (IsHeldOff(session, player, choice.Lift)) return false;
        if (map.Collision.MeshPoints is not null && !HasReachableLanding(player, map, choice.Lift, choice.From, bot, options))
            return false;
        ElevatorTrip begun = Begin(session, player, choice.Lift, choice.From, goal);
        begun.Options = options;
        return true;
    }

    private static bool HasReachableLanding(Player player, Map map, ElevatorLift lift, ElevatorLevel from, Vector3 bot, PlayerbotOptions options)
    {
        foreach (ElevatorCar car in lift.Cars)
        {
            if (LandingBesideReachable(player, map, car.At(from), bot, options) is not null)
                return true;
        }

        return false;
    }

    private static bool IsHeldOff(WorldSession session, Player player, ElevatorLift lift)
        => PlayerbotTransitBackoff.IsBackedOff(player, TransitKind.Lift, lift.Key, session.World.NowMs);

    /// <summary>
    /// Whether a lift is the way to <paramref name="goal"/> but a failed ride keeps <see cref="TryBegin"/> from riding it now (that lift's
    /// backoff after a failure): the caller defers the goal for a short retry instead of judging the walk there refused. Unlike
    /// <see cref="TryBegin"/> this does not weigh the mesh walk, so a caller asks it only after <see cref="TryBegin"/> returned false without a
    /// walk. World thread, no side effects.
    /// </summary>
    internal static bool IsHeldOff(WorldSession session, Player player, Vector3 goal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (IsTraveling(player) || !player.IsAlive || !player.IsInWorld || player.Map is not { } map || NetworkOf(session) is not { } network) return false;
        return network.Choose(map.MapId, new Vector3(player.X, player.Y, player.Z), goal, SurveyOf(session.World, map)) is { } choice
            && IsHeldOff(session, player, choice.Lift);
    }

    /// <summary>
    /// Whether a lift is the way to <paramref name="goal"/> from where the bot stands (<see cref="ElevatorNetwork.Choose"/>), without
    /// beginning a trip or planning a route. World thread, no side effects.
    /// <para>
    /// Unlike <see cref="TryBegin"/> this skips the direct-mesh check (a goal within one route's reach that the mesh joins is walked, not
    /// ridden): it answers "is a lift the way" only for a caller that already knows the walk was refused, such as the failed-return
    /// diagnosis (<c>PlayerbotWorldDestinations.ConfirmUnclimbable</c>). Anywhere else it can say true for a goal the bot simply walks to.
    /// </para>
    /// </summary>
    internal static bool IsWayTo(WorldSession session, Player player, Vector3 goal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsAlive || !player.IsInWorld || player.Map is not { } map || NetworkOf(session) is not { } network) return false;
        return network.Choose(map.MapId, new Vector3(player.X, player.Y, player.Z), goal, SurveyOf(session.World, map)) is not null;
    }

    /// <summary>Begin a trip on <paramref name="lift"/> from <paramref name="from"/> (tests; <see cref="TryBegin"/> chooses).</summary>
    internal static ElevatorTrip Begin(WorldSession session, Player player, ElevatorLift lift, ElevatorLevel from, Vector3 goal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(lift);
        var trip = new ElevatorTrip(lift, from, goal, session.World.NowMs)
        {
            NextPlanMs = session.World.NowMs,
            StartPosition = new Vector3(player.X, player.Y, player.Z),
        };
        if (Trips.TryGetValue(player, out ElevatorTrip? replaced)) replaced.Route = null;
        Trips.AddOrUpdate(player, trip);
        PlayerbotMotion.Of(player).ElevatorTrip = trip;
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
            trip.Route = null;
            Trips.Remove(player);
            if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? deadState)) deadState.ElevatorTrip = null;
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
                trip.Route = null;
                Trips.Remove(player);
                if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? cancelState)) cancelState.ElevatorTrip = null;
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
    /// Per-tick elevator pump (called from <see cref="PlayerbotMotion.Pump"/> every world tick). When a large bot population spaces brain
    /// thinks 12–16 seconds apart, a 4-second elevator stop would otherwise pass unseen between thinks. Sampling every
    /// <see cref="SampleMs"/> (250 ms) after the last <see cref="Drive"/> catches arrival at the landing, boards a standing car while its
    /// doors are open, sends 500 ms riding heartbeats, and steps off at the destination stop.
    /// </summary>
    internal static void Pump(WorldSession session, Player player, uint now)
    {
        if (!player.IsAlive || !player.IsInWorld || player.Map is not { } map) return;
        if (!Trips.TryGetValue(player, out ElevatorTrip? trip) || trip.Stage is ElevatorStage.Done or ElevatorStage.Failed)
        {
            if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? endState)) endState.ElevatorTrip = null;
            return;
        }
        if (map.MapId != trip.Lift.MapId) return;
        if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return;
        if (trip.PendingStep is not null) return; // completed: the step-off is sent, the next Drive reports it (no second step-off)
        if (trip.Stage is ElevatorStage.ToStop or ElevatorStage.Waiting)
            GuardPlatformShaft(session, player, map, trip);
        if (unchecked(now - trip.LastDriveMs) < SampleMs || unchecked(now - trip.LastPumpMs) < SampleMs) return;
        trip.LastPumpMs = now;

        switch (trip.Stage)
        {
            case ElevatorStage.ToStop:
            {
                if (TryCompleteAtDestinationFloor(session, player, map, trip))
                {
                    trip.PendingStep = ElevatorStep.Arrived;
                    return;
                }

                Vector3 bot = new(player.X, player.Y, player.Z);
                if (trip.Car is null || trip.CarObject is not { IsInWorld: true } || !ReferenceEquals(trip.CarObject.Map, map))
                {
                    if (trip.Options is not { } opt || Vector2.Distance(Flat(bot), trip.Lift.Centre) > ApproachYards) return;
                    // ChooseCar allocates and may plan a path per candidate: the pump retries it at most once per PlanRetryMs.
                    if (trip.NextPumpChooseMs != 0 && unchecked((int)(now - trip.NextPumpChooseMs)) < 0) return;
                    trip.NextPumpChooseMs = unchecked(now + PlanRetryMs) | 1u;
                    float best = trip.Best;
                    uint bestMs = trip.BestMs;
                    PlayerbotRoute? route = trip.Route;
                    if (!ChooseCar(player, map, trip, opt, now, out _)) { trip.RestoreRoute(route); return; }
                    (trip.Best, trip.BestMs) = (best, bestMs);
                    trip.RestoreRoute(route);
                }

                GuardPlatformShaft(session, player, map, trip);
                bot = new(player.X, player.Y, player.Z);
                if (Vector2.Distance(Flat(bot), Flat(trip.Landing)) <= AtLandingYards
                    && MathF.Abs(bot.Z - trip.Landing.Z) <= LandingFloorYards + 1f)
                {
                    if (!PlayerbotMovementControl.Stop(session, player)) return;
                    Enter(trip, ElevatorStage.Waiting, now);
                }

                return;
            }
            case ElevatorStage.Waiting:
            {
                if (TryCompleteAtDestinationFloor(session, player, map, trip))
                {
                    FlushStopAtFrom(session, player, trip);
                    trip.PendingStep = ElevatorStep.Arrived;
                    return;
                }

                if (trip.Car is not { } car || trip.CarObject is not { IsInWorld: true } live || !ReferenceEquals(live.Map, map)
                    || map.FindUpdater<GameObjectMapSystem>() is not { } system)
                {
                    return;
                }

                GuardPlatformShaft(session, player, map, trip);
                uint cycle = system.ElevatorCycle(live);
                if (cycle == 0) return;
                TryBoardFromWaiting(session, player, map, system, trip, car, live, cycle, now, fromDrive: false);
                return;
            }
            case ElevatorStage.Aboard:
            {
                if (trip.Car is not { } car || trip.CarObject is not { IsInWorld: true } live || !ReferenceEquals(live.Map, map)
                    || map.FindUpdater<GameObjectMapSystem>() is not { } system || system.ElevatorPosition(live) is not { } position)
                {
                    return;
                }

                Vector3 carAt = new(position.X, position.Y, position.Z);
                if (!CheckAboardState(session, player, trip, live, carAt)) return;

                float fromZ = car.At(trip.From).Z;
                if (!trip.LeftStop && MathF.Abs(carAt.Z - fromZ) > AtLevelYards) trip.LeftStop = true;
                bool atTargetStop = false;
                foreach (ElevatorLevel level in trip.Alighting ? (ElevatorLevel[])[trip.To, trip.From] : [trip.To])
                {
                    if (!trip.Alighting && !trip.LeftStop) break;
                    if (StandingAt(system, live, car.At(level).Z, 0) is not { } stop) continue;
                    atTargetStop = true;
                    trip.NextDestStopAtMs = 0;
                    trip.DestStopActive = true;
                    if (LandingBeside(map, stop, trip.Goal) is not { } ground)
                    {
                        trip.DestSkipReason = "no-landing-floor";
                        continue;
                    }

                    if (PlayerbotMotion.IsActive(player) && !PlayerbotMovementControl.Stop(session, player))
                    {
                        trip.DestSkipReason = "stop-refused";
                        return;
                    }

                    if (!PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation))
                    {
                        trip.DestSkipReason = "send-step-off-refused";
                        return;
                    }

                    trip.DestStopActive = false;
                    trip.DestSkipReason = null;
                    trip.StepOffLevel = level;
                    trip.StepOffPoint = ground;
                    trip.PendingStep = trip.Failure is null ? ElevatorStep.Arrived : ElevatorStep.Failed;
                    return;
                }

                if (!atTargetStop && (trip.LeftStop || trip.Alighting))
                {
                    ObserveDestinationWindow(session, player, system, live, car, carAt, trip, now);
                }

                Beat(session, player, live, carAt, trip, now);
                return;
            }
        }
    }

    /// <summary>
    /// The stored movement without the transport block (Unit.ApplyMovement; the position, the body's world position, is unchanged: vmangos
    /// Player::KillPlayer and SetDeathState leave the transport alone and the corpse is made at the body's position on release). Observers
    /// are told: when a root order is pending (the death root of MapCombat.SetDeathState), the bot's acknowledgement carries this block and
    /// is relayed as MSG_MOVE_ROOT (RootAckHandler); otherwise (a bot already rooted when it died) it is relayed here as MSG_MOVE_HEARTBEAT.
    /// </summary>
    internal static void ClearTransport(WorldSession session, Player player)
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
                if (Vector2.Distance(Flat(car.Low), Flat(at)) > CarHalfYards) continue;
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
        if (!PlayerbotMovementControl.Stop(session, player)) return false;
        ElevatorLevel to = StandingAt(system, live, car.Low.Z, 0) is not null ? ElevatorLevel.Low
            : StandingAt(system, live, car.High.Z, 0) is not null ? ElevatorLevel.High
            : Position(system, live, 500) is { } ahead && ahead.Z > carAt.Z ? ElevatorLevel.High : ElevatorLevel.Low;
        float x = at.X, y = at.Y, z = carAt.Z, o = 0;
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, carAt.X, carAt.Y, carAt.Z, live.Orientation);
        float inside = CarHalfYards - InsideMarginYards;
        var offset = new Vector3(Math.Clamp(x, -inside, inside), Math.Clamp(y, -inside, inside), 0f);
        if (!SendAboard(session, player, live, carAt, offset)) return false;
        var trip = new ElevatorTrip(lift, ElevatorLift.Other(to), at, now)
        {
            Stage = ElevatorStage.Aboard,
            Alighting = true,
            LeftStop = true,
            Car = car,
            CarObject = live,
            Offset = offset,
            LastBeatMs = now,
            StartPosition = at,
        };
        if (Trips.TryGetValue(player, out ElevatorTrip? replaced)) replaced.Route = null;
        Trips.AddOrUpdate(player, trip);
        PlayerbotMotion.Of(player).ElevatorTrip = trip;
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
            foreach (ElevatorCar car in lift.Cars.OrderBy(c => Vector2.DistanceSquared(Flat(c.Low), Flat(at))))
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
            uint now = session.World.NowMs;
            List<uint> history = RescueTimestamps.GetOrCreateValue(player);
            history.RemoveAll(t => unchecked(now - t) > RescueWindowMs);
            history.Add(now);
            AnomalySeverity severity = history.Count > 2 ? AnomalySeverity.Warning : AnomalySeverity.Info;
            ReportAnomaly(session, player, "rescue-teleport",
                string.Create(CultureInfo.InvariantCulture, $"{why}; {placed} (rescues in 10m: {history.Count})"), severity);
        }

        return sent;
    }

    /// <summary>
    /// A settle's rescue was refused (every teleport: TeleportService.Check). Settle does not try again (its turn is spent); the warning
    /// is the trace of a bot left where it stands.
    /// </summary>
    private static void LogRescueRefused(WorldSession session, Player player, string why)
    {
        session.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(PlayerbotElevator)).LogWarning(
            "Playerbot {Bot}: {Why}; the rescue teleport was refused, the bot is left where it stands", player.Name, why);
        ReportAnomaly(session, player, "rescue-refused", $"{why}; the rescue teleport was refused", AnomalySeverity.Warning);
    }

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

        trip.Options ??= options;
        trip.LastDriveMs = now;
        if (!player.IsAlive)
        {
            Cancel(player);
            return ElevatorStep.Idle;
        }

        if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return ElevatorStep.Traveling;
        ElevatorStep step;
        if (trip.PendingStep is { } completed)
        {
            trip.PendingStep = null;
            step = completed;
        }
        else
        {
            step = map.MapId != trip.Lift.MapId
                ? Fail(trip, string.Create(CultureInfo.InvariantCulture, $"elevator: left for map {map.MapId} on the way to {trip.Lift.Name}"))
                : trip.Stage switch
                {
                    ElevatorStage.ToStop => ToStop(session, player, map, trip, options, elapsedMs, now),
                    ElevatorStage.Waiting => Wait(session, player, map, trip, now),
                    ElevatorStage.Aboard => Ride(session, player, map, trip, now),
                    ElevatorStage.Falling => Fall(session, player, trip, now),
                    _ => ElevatorStep.Idle,
                };
        }

        switch (step)
        {
            case ElevatorStep.Arrived when Relocated(trip, player):
                AbandonRelocated(session, player, trip);
                return ElevatorStep.Idle;
            case ElevatorStep.Arrived:
                trip.Stage = ElevatorStage.Done;
                trip.StageMs = now; // when it arrived (inspection)
                trip.Failure = null;
                PlayerbotTransitBackoff.Clear(player, TransitKind.Lift, trip.Lift.Key);
                CheckArrivedAnomalies(session, player, map, trip, options, now);
                break;
            case ElevatorStep.Failed:
                if (trip.Stage is ElevatorStage.ToStop or ElevatorStage.Waiting) PlayerbotMovementControl.Stop(session, player);
                trip.Stage = ElevatorStage.Failed;
                if (!trip.BackoffRecorded)
                {
                    trip.BackoffRecorded = true;
                    PlayerbotTransitBackoff.Fail(player, TransitKind.Lift, trip.Lift.Key, now, options);
                }

                failure = trip.Failure ?? "elevator: the trip failed (no reason recorded)";
                if (!trip.TripFailedReported)
                {
                    trip.TripFailedReported = true;
                    string category = ClassifyFailure(trip, failure);
                    ReportAnomaly(session, player, "trip-failed", $"[{category}] {failure}", AnomalySeverity.Warning);
                }

                break;
        }

        return step;
    }

    private static ElevatorStep ToStop(WorldSession session, Player player, Map map, ElevatorTrip trip, PlayerbotOptions options, uint elapsedMs,
        uint now)
    {
        if (TryCompleteAtDestinationFloor(session, player, map, trip))
        {
            return ElevatorStep.Arrived;
        }

        Vector3 bot = new(player.X, player.Y, player.Z);
        if (InPit(trip.Lift, bot, bot.Z) || InFromShaftPit(trip, bot))
        {
            ReportPitFall(session, player, trip, bot.Z);
            return Drop(session, player, map, trip, now, $"elevator: in the shaft pit of {trip.Lift.Name}");
        }

        if (trip.Car is null || trip.CarObject is not { IsInWorld: true } || !ReferenceEquals(trip.CarObject.Map, map))
        {
            Vector3 stop = trip.Lift.Cars[0].At(trip.From);
            if (Vector2.Distance(Flat(bot), trip.Lift.Centre) > ApproachYards)
            {
                // Far from the lift (its cars are grid objects, in the world only round players near them): walk towards its landing/stop.
                Vector3? approachTarget = null;
                foreach (ElevatorCar car in trip.Lift.Cars.OrderBy(c => Vector2.DistanceSquared(Flat(c.At(trip.From)), Flat(bot))))
                {
                    if (LandingBesideReachable(player, map, car.At(trip.From), bot, options) is { } reachable)
                    {
                        approachTarget = reachable;
                        break;
                    }
                }

                approachTarget ??= LandingBeside(map, stop, bot) ?? SafeWaitOutsideShaft(map, stop, bot);
                if (Walk(session, player, trip, approachTarget.Value, options, elapsedMs, now)) return ElevatorStep.Traveling;
                return Fail(trip, string.Create(CultureInfo.InvariantCulture,
                    $"elevator: no way to {trip.Lift.Name} ({Word(trip.From)} level) from {bot.X:F1},{bot.Y:F1},{bot.Z:F1}{(trip.RefusedPlans >= MaxRefusedPlans ? $" (the path was refused {MaxRefusedPlans} times in a row)" : string.Empty)}"));
            }

            float best = trip.Best;
            uint bestMs = trip.BestMs;
            if (!ChooseCar(player, map, trip, options, now, out string? why)) return Fail(trip, why!);
            (trip.Best, trip.BestMs) = (best, bestMs); // the progress bound runs on across the choice
        }

        if (GuardPlatformShaft(session, player, map, trip))
        {
            return ElevatorStep.Traveling;
        }

        bot = new(player.X, player.Y, player.Z);
        if (Vector2.Distance(Flat(bot), Flat(trip.Landing)) <= AtLandingYards
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
            if ((LandingBesideReachable(player, map, stop, bot, options) ?? LandingBeside(map, stop, bot)) is not { } landing) continue;
            uint walk = (uint)(Vector2.Distance(Flat(bot), Flat(landing)) / speed * 1000f);
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

        if (candidates.Count > 1 && map.Collision.MeshPoints is not null)
        {
            var reachable = candidates.Where(c => PlayerbotNavigation.TryPlanToward(player, c.Landing, options, out _)).ToList();
            if (reachable.Count > 0) candidates = reachable;
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
        if (TryCompleteAtDestinationFloor(session, player, map, trip))
        {
            FlushStopAtFrom(session, player, trip);
            return ElevatorStep.Arrived;
        }

        Vector3 bot = new(player.X, player.Y, player.Z);
        if (InPit(trip.Lift, bot, bot.Z) || InFromShaftPit(trip, bot))
        {
            ReportPitFall(session, player, trip, bot.Z);
            return Drop(session, player, map, trip, now, $"elevator: in the shaft pit of {trip.Lift.Name}");
        }

        if (trip.Car is not { } car || trip.CarObject is not { IsInWorld: true } live || !ReferenceEquals(live.Map, map)
            || map.FindUpdater<GameObjectMapSystem>() is not { } system)
        {
            FlushStopAtFrom(session, player, trip);
            Enter(trip, ElevatorStage.ToStop, now); // the car left the world: choose again
            trip.Car = null;
            return ElevatorStep.Traveling;
        }

        GuardPlatformShaft(session, player, map, trip);

        uint cycle = system.ElevatorCycle(live);
        if (cycle == 0) return Fail(trip, $"elevator: the car of {trip.Lift.Name} has no animation");
        if (unchecked(now - trip.StageMs) > (2 * cycle) + 30_000)
        {
            FlushStopAtFrom(session, player, trip);
            return Fail(trip, string.Create(CultureInfo.InvariantCulture,
                $"elevator: no car stood at the {Word(trip.From)} landing of {trip.Lift.Name} within {((2 * cycle) + 30_000) / 1000}s"));
        }

        TryBoardFromWaiting(session, player, map, system, trip, car, live, cycle, now, fromDrive: true);
        return ElevatorStep.Traveling;
    }

    private static void TryBoardFromWaiting(WorldSession session, Player player, Map map, GameObjectMapSystem system, ElevatorTrip trip,
        ElevatorCar car, GameObject live, uint cycle, uint now, bool fromDrive)
    {
        _ = fromDrive;
        CheckWaitingLanding(session, player, map, trip, car);
        Vector3 bot = new(player.X, player.Y, player.Z);
        Vector3 stopAtFrom = car.At(trip.From);
        float preFlatDist = Vector2.Distance(Flat(bot), Flat(stopAtFrom));
        float preZDiff = MathF.Abs(bot.Z - stopAtFrom.Z);
        if (preFlatDist > LandingRadii[^1] + AtLandingYards || preZDiff > LandingFloorYards + 1f)
        {
            trip.StopSkipReason = preFlatDist > LandingRadii[^1] + AtLandingYards ? "drifted-from-landing" : "z-mismatch";
            FlushStopAtFrom(session, player, trip);
            Enter(trip, ElevatorStage.ToStop, now);
            trip.Car = null;
            return;
        }

        float level = stopAtFrom.Z;
        if (StandingAt(system, live, level, 0) is not { } at)
        {
            FlushStopAtFrom(session, player, trip);
            if (trip.NextStopAtMs != 0 && unchecked((int)(now - (trip.NextStopAtMs + BoardMarginMs))) > 0)
            {
                trip.MissedStops++;
                ReportAnomaly(session, player, "board-missed",
                    string.Create(CultureInfo.InvariantCulture,
                        $"elevator: missed boarding {trip.Lift.Name} at {Word(trip.From)} level (missed stops: {trip.MissedStops}, reason: pump-not-sampled)"),
                    AnomalySeverity.Warning);
                trip.NextStopAtMs = 0;
            }

            if (trip.NextStopAtMs == 0)
            {
                if (NextStop(system, live, level, 0) is { } nextIn)
                {
                    uint due = unchecked(now + nextIn);
                    trip.NextStopAtMs = due == 0 ? 1u : due;
                }
                else if (!trip.BoardWindowShortReported)
                {
                    ReportBoardWindowShort(session, player, system, live, trip, level, cycle);
                }
            }

            return;
        }

        trip.NextStopAtMs = 0;
        uint standsFor = StandsFor(system, live, at, cycle);
        if (!trip.StopActiveAtFrom)
        {
            trip.StopActiveAtFrom = true;
            trip.StopMaxStandsForMs = standsFor;
            trip.StopSkipReason = null;
        }
        else if (standsFor > trip.StopMaxStandsForMs)
        {
            trip.StopMaxStandsForMs = standsFor;
        }

        float flatDist = Vector2.Distance(Flat(bot), Flat(at));
        float zDiff = MathF.Abs(bot.Z - at.Z);
        if (flatDist > LandingRadii[^1] + AtLandingYards || zDiff > LandingFloorYards + 1f)
        {
            trip.StopSkipReason = flatDist > LandingRadii[^1] + AtLandingYards ? "drifted-from-landing" : "z-mismatch";
            FlushStopAtFrom(session, player, trip);
            Enter(trip, ElevatorStage.ToStop, now);
            trip.Car = null;
            return;
        }

        if (standsFor < BoardMarginMs)
        {
            trip.StopSkipReason ??= "window-short";
            if (!trip.BoardWindowShortReported && trip.StopMaxStandsForMs < BoardMarginMs && NextStop(system, live, level, 0) is null)
            {
                ReportBoardWindowShort(session, player, system, live, trip, level, cycle);
            }

            return;
        }

        if (!PlayerbotMovementControl.Stop(session, player))
        {
            trip.StopSkipReason = "stop-refused";
            return;
        }

        // Step aboard: the point of the car's floor nearest to where the bot stands.
        float x = bot.X, y = bot.Y, z = at.Z, o = 0;
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, at.X, at.Y, at.Z, live.Orientation);
        float inside = CarHalfYards - InsideMarginYards;
        var offset = new Vector3(Math.Clamp(x, -inside, inside), Math.Clamp(y, -inside, inside), 0f);
        if (!SendAboard(session, player, live, at, offset))
        {
            trip.StopSkipReason = "send-aboard-refused";
            return;
        }

        trip.StopActiveAtFrom = false;
        trip.StopMaxStandsForMs = 0;
        trip.StopSkipReason = null;
        trip.NextStopAtMs = 0;
        trip.Offset = offset;
        trip.LastBeatMs = now;
        trip.LeftStop = false;
        Enter(trip, ElevatorStage.Aboard, now);
    }

    private static ElevatorStep Ride(WorldSession session, Player player, Map map, ElevatorTrip trip, uint now)
    {
        if (trip.Car is not { } car || trip.CarObject is not { IsInWorld: true } live || !ReferenceEquals(live.Map, map)
            || map.FindUpdater<GameObjectMapSystem>() is not { } system || system.ElevatorPosition(live) is not { } position)
        {
            return Drop(session, player, map, trip, now, $"elevator: the car of {trip.Lift.Name} left the world under the bot");
        }

        Vector3 carAt = new(position.X, position.Y, position.Z);
        if (!CheckAboardState(session, player, trip, live, carAt))
        {
            // Something took the bot off the car (a knock-back, a fear): on the floor it is just off; in the shaft it falls.
            return Drop(session, player, map, trip, now, $"elevator: knocked off the car of {trip.Lift.Name}");
        }

        uint cycle = system.ElevatorCycle(live);
        float fromZ = car.At(trip.From).Z;
        if (!trip.LeftStop && MathF.Abs(carAt.Z - fromZ) > AtLevelYards) trip.LeftStop = true;
        // At the other level (alighting: at the next stop at either level), step off onto the landing nearest the destination (alighting:
        // nearest the bot).
        bool atTargetStop = false;
        foreach (ElevatorLevel level in trip.Alighting ? (ElevatorLevel[])[trip.To, trip.From] : [trip.To])
        {
            if (!trip.Alighting && !trip.LeftStop) break;
            if (StandingAt(system, live, car.At(level).Z, 0) is not { } stop) continue;
            atTargetStop = true;
            trip.NextDestStopAtMs = 0;
            trip.DestStopActive = true;
            if (LandingBeside(map, stop, trip.Goal) is not { } ground)
            {
                trip.DestSkipReason = "no-landing-floor";
                if (trip.Alighting) continue;
                FlushPassedDestination(session, player, trip, "no-landing-floor");
                // No floor there: ride back and get off where the bot got on (the trip fails there).
                trip.Failure ??= string.Create(CultureInfo.InvariantCulture,
                    $"elevator: no floor beside the car of {trip.Lift.Name} at its {Word(trip.To)} level");
                (trip.From, trip.To) = (trip.To, trip.From);
                trip.LeftStop = false;
                return Beat(session, player, live, carAt, trip, now);
            }

            if (PlayerbotMotion.IsActive(player) && !PlayerbotMovementControl.Stop(session, player))
            {
                trip.DestSkipReason = "stop-refused";
                return ElevatorStep.Traveling;
            }

            if (!PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation))
            {
                trip.DestSkipReason = "send-step-off-refused";
                return ElevatorStep.Traveling;
            }

            trip.DestStopActive = false;
            trip.DestSkipReason = null;
            trip.StepOffLevel = level;
            trip.StepOffPoint = ground;
            return trip.Failure is null ? ElevatorStep.Arrived : ElevatorStep.Failed;
        }

        if (!atTargetStop && (trip.LeftStop || trip.Alighting))
        {
            ObserveDestinationWindow(session, player, system, live, car, carAt, trip, now);
        }

        if (unchecked(now - trip.StageMs) > cycle + 30_000)
        {
            if (trip.DestStopActive)
            {
                FlushPassedDestination(session, player, trip, trip.DestSkipReason ?? "timeout");
            }

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
        if (unchecked(now - trip.LastBeatMs) >= HeartbeatMs)
        {
            if (PlayerbotMotion.IsActive(player)) PlayerbotMovementControl.Stop(session, player);
            if (SendAboard(session, player, car, carAt, trip.Offset)) trip.LastBeatMs = now;
        }

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
    internal static ElevatorStep Drop(WorldSession session, Player player, Map map, ElevatorTrip trip, uint now, string why)
    {
        trip.Failure ??= why;
        Vector3 at = new(player.X, player.Y, player.Z);
        float floor = FloorBelow(map, at);
        bool pit = (float.IsFinite(floor) && InPit(trip.Lift, at, floor))
            || InPit(trip.Lift, at, at.Z)
            || InFromShaftPit(trip, at);
        if (pit)
        {
            ReportPitFall(session, player, trip, floor);
        }

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

            if (!trip.RescueRefusedReported)
            {
                trip.RescueRefusedReported = true;
                ReportAnomaly(session, player, "rescue-refused",
                    $"{trip.Failure}; the rescue teleport was refused", AnomalySeverity.Warning);
            }

            if (++trip.RescueTries < MaxRescueTries) return ElevatorStep.Traveling;
            trip.Failure = $"{trip.Failure}; the rescue teleport was refused {MaxRescueTries} times";
            return ElevatorStep.Failed;
        }

        // In the air: off the car (if still on it), the step that begins the fall, then the landing once the fall's time has passed.
        if ((player.Movement.Flags & MovementFlags.OnTransport) != 0 && !PlayerbotMotion.SendStepOff(session, player, at, player.Orientation))
            return ElevatorStep.Traveling;
        if (!PlayerbotMotion.StartFall(session, player, at, player.Orientation)) return ElevatorStep.Traveling;
        trip.FallYards = MathF.Max(0f, at.Z - floor);
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
    /// On an enclosed vault lift (Undercity) the shaft reaches the doors (<see cref="VaultDoorOuterYards"/>), not only the car's footprint.
    /// LS-009 (live 2026-10-17): 356 trips failed as "no way to the lower landing" from 1599.0,181.5,-62.0, 8.5 yards from the
    /// shaft centre of the lift at 1595,189 and 21 yards under its lower stop. That is the shaft's bottom, inside the doors and outside
    /// the 5-yard footprint, so the bot was never rescued.
    /// </summary>
    internal static bool InPit(ElevatorLift lift, Vector3 at, float floor)
    {
        bool vault = ElevatorRegions.IsVaultLift(lift.MapId, new Vector3(lift.Centre, lift.LowZ));
        float footprint = vault ? VaultDoorOuterYards : CarHalfYards + 1.5f;
        foreach (ElevatorCar car in lift.Cars)
        {
            if (Vector2.Distance(Flat(car.Low), Flat(at)) <= footprint && floor < car.Low.Z - PitYards)
                return true;
        }

        return false;
    }

    /// <summary>A floor in a car's footprint this far (or more) under its lower stop is a pit (<see cref="InPit"/>).</summary>
    internal const float PitYards = 1f;

    /// <summary>
    /// When a bot in <c>ToStop</c> or <c>Waiting</c> has already reached (or dropped intact onto) the destination floor <c>trip.To</c>,
    /// steps it out to the destination landing and completes the trip instead of waiting at the wrong floor for 90 seconds or failing.
    /// </summary>
    private static bool TryCompleteAtDestinationFloor(WorldSession session, Player player, Map map, ElevatorTrip trip)
    {
        if (trip.From == trip.To) return false;
        Vector3 bot = new(player.X, player.Y, player.Z);
        ElevatorCar car = ClosestCar(trip.Lift, trip.To, bot);
        Vector3 toStop = car.At(trip.To);

        bool startedAtFromFloor = MathF.Abs(trip.StartPosition.Z - trip.Lift.LevelZ(trip.From)) <= ElevatorNetwork.LevelYards;
        bool startedNearShaft = Vector2.Distance(Flat(trip.StartPosition), Flat(toStop)) <= LandingRadii[^1] + AtLandingYards;
        if (trip.Stage != ElevatorStage.Waiting && !startedAtFromFloor && !startedNearShaft)
        {
            return false;
        }

        float flatToDest = Vector2.Distance(Flat(bot), Flat(toStop));
        float zToDest = MathF.Abs(bot.Z - toStop.Z);
        if (flatToDest > LandingRadii[^1] + AtLandingYards || zToDest > LandingFloorYards + 1.5f)
        {
            return false;
        }

        Vector3 ground = LandingBeside(map, toStop, trip.Goal)
            ?? LandingBeside(map, toStop, bot)
            ?? SafeWaitOutsideShaft(map, toStop, trip.Goal);
        if (PlayerbotMotion.IsActive(player) && !PlayerbotMovementControl.Stop(session, player))
        {
            return false;
        }

        if (!PlayerbotMotion.SendStepOff(session, player, ground, player.Orientation))
        {
            return false;
        }

        trip.StepOffLevel = trip.To;
        trip.StepOffPoint = ground;
        trip.Failure = null;
        return true;
    }

    /// <summary>
    /// Detects when a bot approaching or waiting for an upper-level stop has fallen down the shaft to a depth well below <c>trip.From</c>,
    /// even if the shaft floor is within <c>PitYards</c> of <c>car.Low.Z</c>.
    /// </summary>
    private static bool InFromShaftPit(ElevatorTrip trip, Vector3 bot)
    {
        float fromZ = trip.Lift.LevelZ(trip.From);
        if (bot.Z >= fromZ - (LandingFloorYards + 2f))
        {
            return false;
        }

        bool startedAtFromFloor = MathF.Abs(trip.StartPosition.Z - fromZ) <= ElevatorNetwork.LevelYards;
        if (trip.Stage != ElevatorStage.Waiting && !startedAtFromFloor)
        {
            return false;
        }

        bool vault = ElevatorRegions.IsVaultLift(trip.Lift.MapId, new Vector3(trip.Lift.Centre, trip.Lift.LowZ));
        float footprint = vault ? VaultDoorOuterYards : CarHalfYards + 1.5f;
        foreach (ElevatorCar car in trip.Lift.Cars)
        {
            if (Vector2.Distance(Flat(car.Low), Flat(bot)) <= footprint
                || Vector2.Distance(Flat(car.High), Flat(bot)) <= footprint)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Prevents the bot from walking into or standing inside the elevator shaft (or inside closed Undercity vault doors) during
    /// <c>ToStop</c> and <c>Waiting</c> while the car is not standing at <c>trip.From</c>.
    /// </summary>
    internal static bool GuardPlatformShaft(WorldSession session, Player player, Map map, ElevatorTrip trip)
    {
        Vector3 bot = new(player.X, player.Y, player.Z);
        ElevatorCar car = trip.Car ?? ClosestCar(trip.Lift, trip.From, bot);
        Vector3 stop = car.At(trip.From);
        bool vault = ElevatorRegions.IsVaultLift(map.MapId, stop);
        float guardRadius = vault ? VaultDoorOuterYards : CarHalfYards;

        if (trip.Landing != default)
        {
            float landingFlat = Vector2.Distance(Flat(trip.Landing), Flat(stop));
            if (landingFlat < guardRadius)
            {
                trip.Landing = (trip.Options is { } opt ? LandingBesideReachable(player, map, stop, bot, opt) : null)
                    ?? LandingBeside(map, stop, bot)
                    ?? LandingBeside(map, stop, trip.Goal)
                    ?? SafeWaitOutsideShaft(map, stop, bot);
            }
        }

        ElevatorCar nearestShaftCar = ClosestCar(trip.Lift, trip.From, bot);
        Vector3 nearestStop = nearestShaftCar.At(trip.From);
        bool nearestVault = ElevatorRegions.IsVaultLift(map.MapId, nearestStop);
        float nearestGuardRadius = nearestVault ? VaultDoorOuterYards : CarHalfYards;
        float botFlat = Vector2.Distance(Flat(bot), Flat(nearestStop));
        float botZDiff = MathF.Abs(bot.Z - nearestStop.Z);
        if (botFlat < nearestGuardRadius && botZDiff <= LandingFloorYards + 2f)
        {
            Vector3 safeTarget = trip.Landing != default && Vector2.Distance(Flat(trip.Landing), Flat(nearestStop)) >= nearestGuardRadius
                ? trip.Landing
                : LandingBeside(map, nearestStop, bot) ?? SafeWaitOutsideShaft(map, nearestStop, bot);
            trip.Landing = safeTarget;
            if (PlayerbotMotion.IsActive(player))
            {
                PlayerbotMovementControl.Stop(session, player);
            }

            PlayerbotMotion.SendStepOff(session, player, safeTarget, player.Orientation);
            return true;
        }

        return false;
    }

    internal static Vector3 SafeWaitOutsideShaft(Map map, Vector3 stop, Vector3 toward, uint? mapIdOverride = null)
    {
        bool vault = ElevatorRegions.IsVaultLift(mapIdOverride ?? map.MapId, stop);
        float safeRadius = vault ? VaultLandingRadii[0] : LandingRadii[0];
        Vector2 dir = Flat(toward) - Flat(stop);
        float len = dir.Length();
        dir = len > 1e-3f ? dir / len : new Vector2(1f, 0f);
        float x = stop.X + (dir.X * safeRadius);
        float y = stop.Y + (dir.Y * safeRadius);
        float floor = TryLandingFloor(map, stop, x, y, out float f) ? f : stop.Z;
        return new Vector3(x, y, floor);
    }

    private static ElevatorCar ClosestCar(ElevatorLift lift, ElevatorLevel level, Vector3 near)
        => lift.Cars.OrderBy(c => Vector2.DistanceSquared(Flat(c.At(level)), Flat(near))).First();

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
    /// <paramref name="target"/>; false when the bot got no <see cref="ProgressYards"/> closer within <see cref="NoProgressMs"/>, or the navigation refused
    /// <see cref="MaxRefusedPlans"/> plans in a row (<see cref="ElevatorTrip.RefusedPlans"/>).
    /// </summary>
    private static bool Walk(WorldSession session, Player player, ElevatorTrip trip, Vector3 target, PlayerbotOptions options, uint elapsedMs,
        uint now)
    {
        Vector3 bot = new(player.X, player.Y, player.Z);
        float left = Vector3.Distance(bot, target);
        if (left + ProgressYards <= trip.Best)
        {
            trip.Best = left;
            trip.BestMs = now;
        }
        else if (trip.Route is { Complete: false } activeRoute && activeRoute.Points.Count > 0
            && activeRoute.RemainingDistanceFrom(bot) + ProgressYards <= trip.LegBest)
        {
            trip.LegBest = activeRoute.RemainingDistanceFrom(bot);
            trip.BestMs = now;
        }
        else if (unchecked(now - trip.BestMs) > NoProgressMs)
        {
            return false;
        }

        if (trip.Route is { Complete: false } route && PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now))
        {
            trip.HadRoute = true;
            return true;
        }

        trip.Route = null;
        if (unchecked((int)(trip.NextPlanMs - now)) > 0) return true;
        // A plan refused for want of a tile still being read is no verdict (a query per PlanRetryMs, TileWait.MaxMs, then it is one);
        // MaxRefusedPlans refusals in a row end the walk, a route resets the count.
        PlanOutcome planned = PlayerbotNavigation.PlanToward(player, target, options, ref trip.Tiles, now, out PlayerbotRoute? next);
        if (planned == PlanOutcome.Route && next is not null && player.Map is { } walkMap && walkMap.Collision.MeshPoints is not null
            && !IsRouteReachableToLanding(next, target, options))
        {
            planned = PlanOutcome.Refused;
            next = null;
        }

        if (planned != PlanOutcome.Route || next is null)
        {
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            return planned != PlanOutcome.Refused || ++trip.RefusedPlans < MaxRefusedPlans;
        }

        if (!PlayerbotNavigation.TryAdvance(session, next, options, elapsedMs, now))
        {
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            return ++trip.RefusedPlans < MaxRefusedPlans;
        }

        trip.RefusedPlans = 0;
        trip.HadRoute = true;
        trip.Route = next;
        trip.LegBest = next.Points.Count > 0 ? next.RemainingDistanceFrom(bot) : float.PositiveInfinity;
        return true;
    }

    private static ElevatorStep Fail(ElevatorTrip trip, string why)
    {
        trip.Failure ??= why;
        trip.Route = null;
        return ElevatorStep.Failed;
    }

    private static void Enter(ElevatorTrip trip, ElevatorStage stage, uint now)
    {
        if (trip.Stage == stage) return;
        trip.Stage = stage;
        trip.StageMs = now;
        trip.NextPlanMs = now;
        trip.RefusedPlans = 0;
        trip.Best = float.PositiveInfinity;
        trip.LegBest = float.PositiveInfinity;
        trip.BestMs = now;
        trip.Route = null;
    }

    private static void ReportAnomaly(WorldSession session, Player player, string code, string detail,
        AnomalySeverity severity = AnomalySeverity.Warning)
    {
        if (session.IsInChaosProbe) return;
        PlayerbotAnomalies.Report(player, AnomalyArea, code, detail, severity);
    }

    internal static void ReportPitFall(WorldSession session, Player player, ElevatorTrip trip, float floorZ)
    {
        if (trip.PitFallReported) return;
        trip.PitFallReported = true;
        ReportAnomaly(session, player, "pit-fall",
            string.Create(CultureInfo.InvariantCulture,
                $"elevator: bot in or above shaft pit of {trip.Lift.Name} at z {player.Z:F1} (pit floor z {floorZ:F1})"),
            AnomalySeverity.Warning);
    }

    internal static void CheckWaitingLanding(WorldSession session, Player player, Map map, ElevatorTrip trip, ElevatorCar car)
    {
        if (trip.WaitingInsideDoorReported) return;
        Vector3 stop = car.At(trip.From);
        Vector3 checkPos = trip.Landing == default ? new Vector3(player.X, player.Y, player.Z) : trip.Landing;
        float radial = Vector2.Distance(Flat(checkPos), Flat(stop));
        float botRadial = Vector2.Distance(new Vector2(player.X, player.Y), Flat(stop));
        bool vault = ElevatorRegions.IsVaultLift(map.MapId, stop);
        bool insideDoor = vault && (radial < VaultDoorOuterYards || botRadial < VaultDoorOuterYards);
        bool insideShaft = radial < CarHalfYards || botRadial < CarHalfYards;
        bool offMesh = map.Collision.MeshPoints is not null && !PlayerbotNavigation.OnWalkableMesh(map, checkPos);
        if (!insideDoor && !insideShaft && !offMesh) return;

        trip.WaitingInsideDoorReported = true;
        string reason = insideDoor ? "inside-vault-door" : insideShaft ? "inside-shaft" : "off-walkable-mesh";
        ReportAnomaly(session, player, "waiting-inside-door",
            string.Create(CultureInfo.InvariantCulture,
                $"elevator: waiting for {trip.Lift.Name} ({Word(trip.From)}) at radial {MathF.Min(radial, botRadial):F2} yd ({reason})"),
            AnomalySeverity.Warning);
    }

    internal static void FlushStopAtFrom(WorldSession session, Player player, ElevatorTrip trip)
    {
        if (!trip.StopActiveAtFrom) return;
        uint maxWindow = trip.StopMaxStandsForMs;
        string reason = trip.StopSkipReason ?? "unboarded";
        trip.StopActiveAtFrom = false;
        trip.StopMaxStandsForMs = 0;
        trip.StopSkipReason = null;
        if (maxWindow < BoardMarginMs) return;

        trip.MissedStops++;
        ReportAnomaly(session, player, "board-missed",
            string.Create(CultureInfo.InvariantCulture,
                $"elevator: missed boarding {trip.Lift.Name} at {Word(trip.From)} level (missed stops: {trip.MissedStops}, window {maxWindow}ms, reason: {reason})"),
            AnomalySeverity.Warning);
    }

    private static void ReportBoardWindowShort(WorldSession session, Player player, GameObjectMapSystem system, GameObject live,
        ElevatorTrip trip, float levelZ, uint cycle)
    {
        trip.BoardWindowShortReported = true;
        uint maxStand = MaxCycleStandMs(system, live, levelZ, cycle);
        ReportAnomaly(session, player, "board-window-short",
            string.Create(CultureInfo.InvariantCulture,
                $"elevator: {trip.Lift.Name} ({Word(trip.From)} level) max standing window {maxStand}ms < {BoardMarginMs}ms across {cycle}ms cycle"),
            AnomalySeverity.Warning);
    }

    private static uint MaxCycleStandMs(GameObjectMapSystem system, GameObject car, float levelZ, uint cycle)
    {
        uint best = 0;
        for (uint t = 0; t < cycle; t += SampleMs)
        {
            if (StandingAt(system, car, levelZ, t) is not { } at) continue;
            uint stays = SampleMs;
            while (stays < cycle && Position(system, car, t + stays) is { } then && Vector3.Distance(then, at) <= StandingYards)
                stays += SampleMs;
            if (stays > best) best = stays;
            t += stays;
        }

        return best;
    }

    internal static bool CheckAboardState(WorldSession session, Player player, ElevatorTrip trip, GameObject live, Vector3 carAt)
    {
        bool hasTransport = (player.Movement.Flags & MovementFlags.OnTransport) != 0;
        bool guidMatches = player.Movement.TransportGuid == live.Guid.Value;
        if (MathF.Abs(carAt.Z - player.Z) > LeftBehindYards && !trip.LeftBehindReported)
        {
            trip.LeftBehindReported = true;
            ReportAnomaly(session, player, "left-behind",
                string.Create(CultureInfo.InvariantCulture,
                    $"elevator: car of {trip.Lift.Name} at z {carAt.Z:F1} moved > {LeftBehindYards:F1} yd from bot at z {player.Z:F1} while Aboard (onTransport={hasTransport})"),
                AnomalySeverity.Warning);
        }

        // Report-only: the base treated the OnTransport flag alone as aboard, so a bot on another transport keeps riding.
        if (hasTransport && guidMatches) return true;

        if (!trip.DetachedReported)
        {
            trip.DetachedReported = true;
            string cause = !hasTransport && session.Services.GetService<TeleportFeature>()?.Teleports.StageOf(player) is not null ? "teleport"
                : hasTransport && !guidMatches ? "transport-guid-mismatch"
                : player.Combat.IsInCombat ? "combat-knockback"
                : player.Locomotion.Pending.Count > 0 ? "locomotion-order"
                : "knockback-or-cleared";
            ReportAnomaly(session, player, "detached-mid-ride",
                string.Create(CultureInfo.InvariantCulture,
                    $"elevator: detached from {trip.Lift.Name} mid-ride at z {player.Z:F1} (car z {carAt.Z:F1}, cause: {cause}, onTransport={hasTransport}, transportGuid=0x{player.Movement.TransportGuid:X})"),
                AnomalySeverity.Warning);
        }

        return hasTransport;
    }

    internal static void ObserveDestinationWindow(WorldSession session, Player player, GameObjectMapSystem system, GameObject live,
        ElevatorCar car, Vector3 carAt, ElevatorTrip trip, uint now)
    {
        float toZ = car.At(trip.To).Z;
        if (trip.DestStopActive)
        {
            if (MathF.Abs(carAt.Z - toZ) > AtLevelYards)
            {
                FlushPassedDestination(session, player, trip, trip.DestSkipReason ?? "left-before-step-off");
            }

            return;
        }

        if (trip.NextDestStopAtMs != 0 && unchecked((int)(now - (trip.NextDestStopAtMs + BoardMarginMs))) > 0)
        {
            FlushPassedDestination(session, player, trip, "pump-gap");
            trip.NextDestStopAtMs = 0;
        }

        if (trip.NextDestStopAtMs == 0 && NextStop(system, live, toZ, 0) is { } nextIn)
        {
            uint due = unchecked(now + nextIn);
            trip.NextDestStopAtMs = due == 0 ? 1u : due;
        }
    }

    private static void FlushPassedDestination(WorldSession session, Player player, ElevatorTrip trip, string reason)
    {
        trip.DestStopActive = false;
        trip.DestSkipReason = null;
        trip.PassedDestinations++;
        ReportAnomaly(session, player, "passed-destination",
            string.Create(CultureInfo.InvariantCulture,
                $"elevator: passed {Word(trip.To)} destination stop on {trip.Lift.Name} without stepping off (count: {trip.PassedDestinations}, reason: {reason})"),
            AnomalySeverity.Warning);
    }

    /// <summary>
    /// A completed trip's bot stands within this (flat) of the point it was sent to when it stepped off (<see cref="ElevatorTrip.StepOffPoint"/>);
    /// the server may nudge the position a little. Farther, the bot was relocated between the pump's step-off and the <see cref="Drive"/> that
    /// reports it (a GM <c>.playerbot send</c>, a teleport, a resurrection at a spirit healer, a catch-up teleport).
    /// </summary>
    internal const float StepOffToleranceYards = 8f;

    /// <summary>The vertical counterpart of <see cref="StepOffToleranceYards"/>: a bot this far above or below its step-off point was moved to another level.</summary>
    internal const float StepOffZToleranceYards = 5f;

    /// <summary>
    /// Whether the bot of a trip that is to be reported arrived was relocated since it stepped off: measured from the stored step-off point
    /// (the car it stepped off beside is not always <see cref="ElevatorTrip.Car"/>: <c>TryCompleteAtDestinationFloor</c> uses the closest car).
    /// Every path that completes a trip stores the point; a trip without one has nothing to judge an arrival by, so it counts as relocated
    /// (<see cref="Drive"/> drops it, <see cref="CheckArrivedAnomalies"/> skips its checks).
    /// </summary>
    private static bool Relocated(ElevatorTrip trip, Player player)
    {
        if (trip.StepOffPoint is not { } point) return true;
        return Vector2.Distance(new Vector2(player.X, player.Y), new Vector2(point.X, point.Y)) > StepOffToleranceYards
            || MathF.Abs(player.Z - point.Z) > StepOffZToleranceYards;
    }

    /// <summary>
    /// A trip reported as arrived whose bot is no longer near its stop was relocated, so it neither arrived nor failed: the trip is dropped
    /// (no backoff, no anomaly) and the bot is idle for the brain. Logged at Debug.
    /// </summary>
    private static void AbandonRelocated(WorldSession session, Player player, ElevatorTrip trip)
    {
        trip.Route = null;
        Trips.Remove(player);
        if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? state)) state.ElevatorTrip = null;
        session.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(PlayerbotElevator)).LogDebug(
            "Playerbot {Bot}: the trip on {Lift} was abandoned, the bot was relocated to {X:F1},{Y:F1},{Z:F1} before it was reported arrived",
            player.Name, trip.Lift.Name, player.X, player.Y, player.Z);
    }

    internal static void CheckArrivedAnomalies(WorldSession session, Player player, Map map, ElevatorTrip trip, PlayerbotOptions options, uint now)
    {
        if (Relocated(trip, player)) return; // not at its stop: nothing about the arrival (or its duration) can be judged
        if (trip.Car is not null)
        {
            ElevatorLevel targetLevel = trip.Alighting && trip.StepOffLevel is { } alighted ? alighted : trip.To;
            Vector3 stop = ClosestCar(trip.Lift, targetLevel, new Vector3(player.X, player.Y, player.Z)).At(targetLevel); // the car it stepped off beside
            if (MathF.Abs(player.Z - stop.Z) > LandingFloorYards + 1f)
            {
                ReportAnomaly(session, player, "arrived-wrong-level",
                    string.Create(CultureInfo.InvariantCulture,
                        $"elevator: arrived on {trip.Lift.Name} at z {player.Z:F1}, expected {Word(targetLevel)} level z {stop.Z:F1}"),
                    AnomalySeverity.Warning);
            }

            Vector3 pos = new(player.X, player.Y, player.Z);
            float radial = Vector2.Distance(Flat(pos), Flat(stop));
            bool insideVaultDoor = ElevatorRegions.IsVaultLift(map.MapId, stop) && radial < VaultDoorOuterYards;
            bool insideShaft = radial < CarHalfYards;
            bool offMesh = map.Collision.MeshPoints is not null && !PlayerbotNavigation.OnWalkableMesh(map, pos);
            if (insideVaultDoor || insideShaft || offMesh)
            {
                string reason = insideVaultDoor ? "inside-vault-door" : insideShaft ? "inside-shaft" : "off-walkable-mesh";
                ReportAnomaly(session, player, "arrived-off-landing",
                    string.Create(CultureInfo.InvariantCulture,
                        $"elevator: stepped off {trip.Lift.Name} ({Word(targetLevel)}) at radial {radial:F2} yd ({reason})"),
                    AnomalySeverity.Warning);
            }
        }

        if (!trip.TripSlowReported)
        {
            uint cycleMs = trip.Car?.CycleMs ?? (trip.Lift.Cars.Count > 0 ? trip.Lift.Cars[0].CycleMs : 40_000u);
            if (cycleMs == 0) cycleMs = 40_000u;
            float speed = MathF.Max(1f, options.MoveSpeed);
            float walkDist = Vector2.Distance(Flat(trip.StartPosition), trip.Lift.Centre);
            uint walkMs = (uint)(walkDist / speed * 1000f);
            uint effectiveCycleMs = Math.Max(cycleMs, 30_000u);
            uint walkBudgetMs = walkMs * 2;
            uint expectedMaxMs = (3 * effectiveCycleMs) + walkBudgetMs + 45_000u;
            uint durationMs = unchecked(now - trip.BeganMs);
            if (durationMs > expectedMaxMs)
            {
                trip.TripSlowReported = true;
                ReportAnomaly(session, player, "trip-slow",
                    string.Create(CultureInfo.InvariantCulture,
                        $"elevator: trip on {trip.Lift.Name} took {durationMs / 1000.0:F1}s, expected <= {expectedMaxMs / 1000.0:F1}s (cycle {cycleMs}ms, walk est {walkMs}ms, missedStops {trip.MissedStops})"),
                    AnomalySeverity.Warning);
            }
        }
    }

    private static string ClassifyFailure(ElevatorTrip trip, string failure)
        => failure.Contains("left for map", StringComparison.Ordinal) ? "map-changed"
            : failure.Contains("no way to", StringComparison.Ordinal) ? (trip.HadRoute ? "no-progress" : "no-way")
            : failure.Contains("no car of", StringComparison.Ordinal) || failure.Contains("no game objects", StringComparison.Ordinal) ? "no-car"
            : failure.Contains("no animation", StringComparison.Ordinal) ? "no-animation"
            : failure.Contains("no floor beside", StringComparison.Ordinal) ? "no-floor"
            : failure.Contains("within ", StringComparison.Ordinal) ? "timeout"
            : failure.Contains("left the world under", StringComparison.Ordinal) ? "car-despawned"
            : failure.Contains("knocked off", StringComparison.Ordinal) ? "detached"
            : failure.Contains("shaft pit", StringComparison.Ordinal) ? "pit"
            : "failed";

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
    /// The floor beside a car standing at <paramref name="stop"/> (<see cref="LandingRadii"/> from its centre, or
    /// <see cref="VaultLandingRadii"/> outside the enclosed shaft doors on Undercity's vault lifts; 16 directions, within
    /// <see cref="LandingFloorYards"/> of its level and in line of sight from the shaft), nearest <paramref name="toward"/>; null when there is none.
    /// The stop may lie where no grid is loaded (a lift far from the bot): its model and navigation tiles are loaded on demand
    /// (<see cref="MapCollision.LoadTilesOnDemand"/>), never as a grid's.
    /// </summary>
    internal static Vector3? LandingBeside(Map map, Vector3 stop, Vector3 toward)
    {
        map.Collision.LoadTilesOnDemand(stop.X, stop.Y);
        if (ElevatorRegions.IsVaultLift(map.MapId, stop))
        {
            return FindOnRings(map, stop, toward, VaultLandingRadii);
        }

        return FindOnRings(map, stop, toward, LandingRadii);

        static Vector3? FindOnRings(Map map, Vector3 stop, Vector3 toward, ReadOnlySpan<float> radii)
        {
            bool hasMesh = map.Collision.MeshPoints is not null;
            Vector3? fallbackWalkableMesh = null;
            Vector3? fallbackNonFragmentAny = null;
            Vector3? fallbackAny = null;
            foreach (float radius in radii)
            {
                Vector3? bestMesh = null;
                float bestMeshDistance = float.PositiveInfinity;
                Vector3? bestWalkableMesh = null;
                float bestWalkableMeshDistance = float.PositiveInfinity;
                Vector3? bestNonFragmentAny = null;
                float bestNonFragmentAnyDistance = float.PositiveInfinity;
                Vector3? bestAny = null;
                float bestAnyDistance = float.PositiveInfinity;
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * MathF.PI / 8f;
                    float x = stop.X + (radius * MathF.Cos(angle)), y = stop.Y + (radius * MathF.Sin(angle));
                    if (!TryLandingFloor(map, stop, x, y, out float floor)) continue;
                    var candidate = new Vector3(x, y, floor);
                    if (!PlayerbotMoveCheck.LineClear(map.Collision, stop with { Z = floor }, candidate)
                        || !PlayerbotMoveCheck.LineClear(map.Collision, stop, candidate)) continue;
                    float distance = Vector2.Distance(new Vector2(x, y), Flat(toward));
                    if (distance < bestAnyDistance)
                    {
                        bestAnyDistance = distance;
                        bestAny = candidate;
                    }

                    bool onFragment = hasMesh && PlayerbotNavigation.IsPointOnMeshFragment(map, candidate);
                    if (!onFragment && distance < bestNonFragmentAnyDistance)
                    {
                        bestNonFragmentAnyDistance = distance;
                        bestNonFragmentAny = candidate;
                    }

                    if (hasMesh && PlayerbotNavigation.OnWalkableMesh(map, candidate))
                    {
                        if (distance < bestWalkableMeshDistance)
                        {
                            bestWalkableMeshDistance = distance;
                            bestWalkableMesh = candidate;
                        }

                        if (!onFragment && distance < bestMeshDistance)
                        {
                            bestMeshDistance = distance;
                            bestMesh = candidate;
                        }
                    }
                }

                if (bestMesh is not null) return bestMesh;
                fallbackWalkableMesh ??= bestWalkableMesh;
                fallbackNonFragmentAny ??= bestNonFragmentAny;
                fallbackAny ??= bestAny;
            }

            return fallbackWalkableMesh ?? fallbackNonFragmentAny ?? fallbackAny;
        }
    }

    private static bool IsRouteReachableToLanding(PlayerbotRoute? route, Vector3 candidate, PlayerbotOptions options)
    {
        if (route is null || route.Points.Count == 0) return false;
        if (!route.MeshEndedShort) return true;
        if (Vector2.Distance(Flat(route.Points[^1]), Flat(candidate)) <= 10f
            && MathF.Abs(route.Points[^1].Z - candidate.Z) <= LandingFloorYards + 2f)
        {
            return true;
        }

        return route.Points.Count >= options.MaxPathPoints || route.Distance >= 50f;
    }

    internal static Vector3? LandingBesideReachable(Player player, Map map, Vector3 stop, Vector3 toward, PlayerbotOptions options)
    {
        if (LandingBeside(map, stop, toward) is not { } primary) return null;
        if (map.Collision.MeshPoints is null) return primary;
        if (PlayerbotNavigation.TryPlanToward(player, primary, options, out PlayerbotRoute? primaryRoute)
            && IsRouteReachableToLanding(primaryRoute, primary, options))
        {
            return primary;
        }

        if (ElevatorRegions.IsVaultLift(map.MapId, stop))
        {
            return FindReachableOnRings(player, map, stop, toward, options, VaultLandingRadii);
        }

        return FindReachableOnRings(player, map, stop, toward, options, LandingRadii);

        static Vector3? FindReachableOnRings(Player player, Map map, Vector3 stop, Vector3 toward, PlayerbotOptions options,
            ReadOnlySpan<float> radii)
        {
            foreach (float radius in radii)
            {
                var candidates = new List<(Vector3 Point, bool Fragment, float Distance)>();
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * MathF.PI / 8f;
                    float x = stop.X + (radius * MathF.Cos(angle)), y = stop.Y + (radius * MathF.Sin(angle));
                    if (!TryLandingFloor(map, stop, x, y, out float floor)) continue;
                    var candidate = new Vector3(x, y, floor);
                    if (!PlayerbotMoveCheck.LineClear(map.Collision, stop with { Z = floor }, candidate)
                        || !PlayerbotMoveCheck.LineClear(map.Collision, stop, candidate)) continue;
                    if (!PlayerbotNavigation.OnWalkableMesh(map, candidate)) continue;
                    bool onFragment = PlayerbotNavigation.IsPointOnMeshFragment(map, candidate);
                    float distance = Vector2.Distance(new Vector2(x, y), Flat(toward));
                    candidates.Add((candidate, onFragment, distance));
                }

                candidates.Sort((a, b) => a.Fragment != b.Fragment ? a.Fragment.CompareTo(b.Fragment) : a.Distance.CompareTo(b.Distance));
                foreach ((Vector3 candidate, _, _) in candidates)
                {
                    if (PlayerbotNavigation.TryPlanToward(player, candidate, options, out PlayerbotRoute? route)
                        && IsRouteReachableToLanding(route, candidate, options))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }

    private static bool TryLandingFloor(Map map, Vector3 stop, float x, float y, out float floor)
    {
        bool vault = ElevatorRegions.IsVaultLift(map.MapId, stop);
        float maxBelow = vault ? 4.5f : LandingFloorYards;
        floor = map.Collision.GetHeight(x, y, stop.Z + 3f);
        if (float.IsFinite(floor) && floor > TerrainTile.InvalidHeight && floor <= stop.Z + LandingFloorYards && floor >= stop.Z - maxBelow)
            return true;
        return map.Collision.TryGetMeshFloor(x, y, stop.Z, maxBelow, LandingFloorYards, out floor)
            && float.IsFinite(floor) && floor <= stop.Z + LandingFloorYards && floor >= stop.Z - maxBelow;
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
