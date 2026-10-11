using System.Numerics;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The Kalimdor lifts of the world database and the client (classic-db 5875 <c>gameobject</c> rows, the live world's import; the 5875
/// client's TransportAnimation.dbc rows of their entries). Only the values the tests need, copied from the rows.
/// </summary>
internal static class ElevatorTestData
{
    public const uint Kalimdor = 1;

    /// <summary>(guid, entry, x, y, z, orientation, rotation2, rotation3) of the eight "Mesa Elevator" cars on map 1 (display 360).</summary>
    public static readonly (uint Guid, uint Entry, float X, float Y, float Z, float O, float R2, float R3)[] Cars =
    [
        (16874, 11898, -4665.43f, -1827.67f, 85.4053f, 0.061086f, 0.030538f, 0.999534f), // the Great Lift, top car
        (16875, 11898, -5399.19f, -2504.62f, 89.0211f, 2.60927f, 0.964787f, 0.263032f), // Freewind Post, top car
        (16876, 11899, -4670.77f, -1849.61f, -44.1442f, -0.18326f, -0.091502f, 0.995805f), // the Great Lift, bottom car
        (16877, 11899, -5382.5f, -2489.42f, -40.5284f, 2.36492f, 0.92554f, 0.378649f), // Freewind Post, bottom car
        (18298, 4170, -1286.24f, 189.72f, 130.08f, -1.07338f, -0.511294f, 0.859406f), // Thunder Bluff, top car
        (18435, 4171, -1308.38f, 185.29f, 68.5858f, -0.270525f, -0.13485f, 0.990866f), // Thunder Bluff, bottom car
        (20639, 47296, -1028.04f, -28.3568f, 69.0226f, 2.9147f, 0.993572f, 0.113203f), // Thunder Bluff (second lift), bottom car
        (20640, 47297, -1037.27f, -49.2355f, 140.495f, 3.07178f, 0.999391f, 0.034899f), // Thunder Bluff (second lift), top car
    ];

    /// <summary>TransportAnimation.dbc (5875) rows of the entries: (TimeSeg, z offset); x and y stay within 0.00001 and are left 0.</summary>
    public static readonly Dictionary<uint, (uint T, float Z)[]> Paths = new()
    {
        [11898] = [(0, 0), (5000, 0), (6100, -1.754788f), (6867, -5.176656f), (12867, -124.866852f), (13833, -127.901558f), (15000, -129.299881f),
            (20000, -129.299881f), (21333, -127.888763f), (22233, -124.790787f), (28133, -5.247491f), (28800, -1.585036f), (29900, -0.108609f), (30000, 0)],
        [11899] = [(0, 0), (5000, 0), (5900, 1.201138f), (6667, 4.690253f), (13100, 121.523071f), (14067, 127.640358f), (15000, 129.553680f),
            (20000, 129.553680f), (21000, 127.604843f), (21800, 122.328789f), (27933, 8.591193f), (28967, 1.968553f), (30000, 0)],
        [4170] = [(0, 0), (5000, 0), (6100, -1.754786f), (6933, -5.176656f), (12867, -56.811295f), (13833, -59.846001f), (15000, -61.244316f),
            (20000, -61.244316f), (21333, -59.833206f), (22233, -56.735229f), (28133, -5.247490f), (28800, -1.585038f), (30033, 0)],
        [4171] = [(0, 0), (5000, 0), (5900, 1.201138f), (6667, 4.690253f), (10567, 32.550922f), (13100, 53.467518f), (14067, 59.584812f),
            (15000, 61.498127f), (20000, 61.498127f), (21000, 59.549297f), (21800, 54.273232f), (25267, 29.109110f), (27933, 8.591193f),
            (28967, 1.968553f), (30000, 0)],
        [47296] = [(0, 0), (5000, 0), (5900, 1.201138f), (6667, 4.690253f), (10567, 37.673847f), (13100, 63.445599f), (14067, 69.562889f),
            (15000, 71.476212f), (20000, 71.476212f), (21000, 69.527382f), (21800, 64.251320f), (25267, 34.232037f), (27933, 8.591193f),
            (28967, 1.968553f), (30000, 0)],
        [47297] = [(0, 0), (5000, 0), (6100, -1.754781f), (6933, -5.176656f), (12867, -66.789375f), (13833, -69.824081f), (15000, -71.222397f),
            (20000, -71.222397f), (21333, -69.811287f), (22233, -66.713310f), (28133, -5.247484f), (28800, -1.585036f), (30033, 0)],
    };

    /// <summary>Wharfmaster Dizzywig in Ratchet (creature 3453, spawn 14419): quest 1111's ender.</summary>
    public static readonly Vector3 Dizzywig = new(-985.599f, -3796.71f, 5.20934f);

    /// <summary>Nataka Longhorn at Ghost Walker Post, Desolace (creature 11259, spawn 29198): quest 5361's ender.</summary>
    public static readonly Vector3 NatakaLonghorn = new(-1221.01f, 1742.1f, 90.7142f);

    /// <summary>Cliffwatcher Longhorn on Freewind Post (creature 10537, spawn 21336): quest 5361's giver.</summary>
    public static readonly Vector3 CliffwatcherLonghorn = new(-5454.95f, -2442.79f, 90.0237f);

    /// <summary>Where three bots stalled on quest 1111 (issue #186, live 2026-10-11 10:23): the Shimmering Flats edge of Thousand Needles.</summary>
    public static readonly Vector3 ShimmeringFlatsStall = new(-5450.1f, -3750.1f, -47.0f);

    /// <summary>Another stall on quest 1111 (issue #186, 11:26), under the Great Lift's cliff.</summary>
    public static readonly Vector3 UnderTheLiftStall = new(-4816.9f, -2079.2f, -26.3f);

    public static GameObjectTemplate Template(uint entry) => new()
    {
        Entry = entry, Type = (uint)GameObjectType.Transport, DisplayId = 360, Name = "Mesa Elevator", Flags = 40,
        Data = new uint[GameObjectTemplate.DataCount],
    };

    public static GameObjectSpawn Spawn((uint Guid, uint Entry, float X, float Y, float Z, float O, float R2, float R3) car) => new()
    {
        Guid = car.Guid, Entry = car.Entry, MapId = Kalimdor, X = car.X, Y = car.Y, Z = car.Z, Orientation = car.O,
        Rotation2 = car.R2, Rotation3 = car.R3, SpawnTimeSeconds = 180, AnimProgress = 0, State = 1,
    };

    public static GameObjectContent Content(params uint[] onlyGuids) => new(
        [.. Cars.Select(c => c.Entry).Distinct().Select(Template)],
        [.. Cars.Where(c => onlyGuids.Length == 0 || onlyGuids.Contains(c.Guid)).Select(Spawn)],
        [], [], []);

    public static TransportAnimationCatalog Animation()
        => new([.. Paths.SelectMany(p => p.Value.Select(n => (p.Key, new TransportAnimationNode(n.T, 0, 0, n.Z))))]);

    public static ElevatorNetwork Network() => ElevatorNetwork.Build(Content().Spawns, Content().FindTemplate, Animation());
}
