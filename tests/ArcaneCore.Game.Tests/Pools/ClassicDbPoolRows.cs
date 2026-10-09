using ArcaneCore.Kernel.WorldData.Pools;

namespace ArcaneCore.Game.Tests.Pools;

/// <summary>
/// cmangos pools as classic-db 1.12.1 z2815 (D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz) has them: pool, member, chance,
/// limit, entry and respawn values are the dump's rows; positions are moved next to the test player (the dump's are hundreds of yards
/// apart), keeping their order. The dump is GPL data and is not committed; these few values are fixtures, as the other real-row tests keep
/// theirs.
/// <list type="bullet">
/// <item>Pool 2000 "Mineral nodes - Badlands - multinodes subzone 2": max_limit 1, five child pools 3026-3030 (pool_pool chance 0), each
/// max_limit 1 with the three nodes of one spot: Truesilver Deposit 2047 and Gold Vein 1734 at chance 20 (3026: all three at 0) and
/// Mithril Deposit 2040 at 0; spawntimesecs 300-900.</item>
/// <item>Pool 31225 "The Barrens (The Merchant Coast) - Chest Pool": max_limit 1; twelve of its nineteen chests (the ones with their own
/// entry: 3689 and 106319 Battered Chest), 300-900 s.</item>
/// <item>Pool 1069 "Thuros Lightfingers (61)": max_limit 1, a <c>pool_creature_template</c> row for entry 61: his eight Elwynn spawns
/// 81107, 134007-134013, 5400-9000 s.</item>
/// <item>Pool 25002 "Noblegarden (Goldshire) - Brightly Colored Egg": max_limit 10, twelve of its 61 eggs (entry 0 in the dump with gameobject_spawn_entry 113768-113771, given
/// entry 113768 here), every one listed in <c>game_event_gameobject</c> for event 9 (Noblegarden), 30 s.</item>
/// <item>Pool 8648 "Plaguebloom - Eastern Plaguelands - subzone 1": no <c>pool_template</c> row; spawns 19897, 19900, 19901.</item>
/// </list>
/// </summary>
internal static class ClassicDbPoolRows
{
    public const uint BadlandsMother = 2000;
    public const uint TruesilverDeposit = 2047;
    public const uint GoldVein = 1734;
    public const uint MithrilDeposit = 2040;

    /// <summary>Child pool, then its (guid, entry, chance) nodes.</summary>
    public static readonly (uint Pool, (uint Guid, uint Entry, float Chance)[] Nodes)[] BadlandsSpots =
    [
        (3026, [(71301, MithrilDeposit, 0f), (71302, GoldVein, 0f), (71304, TruesilverDeposit, 0f)]),
        (3027, [(70979, TruesilverDeposit, 20f), (70977, GoldVein, 20f), (70976, MithrilDeposit, 0f)]),
        (3028, [(71029, TruesilverDeposit, 20f), (71027, GoldVein, 20f), (71026, MithrilDeposit, 0f)]),
        (3029, [(71084, TruesilverDeposit, 20f), (71082, GoldVein, 20f), (71081, MithrilDeposit, 0f)]),
        (3030, [(71284, TruesilverDeposit, 20f), (71282, GoldVein, 20f), (71281, MithrilDeposit, 0f)]),
    ];

    public const uint MerchantCoastChests = 31225;
    public const uint BatteredChestA = 3689;
    public const uint BatteredChestB = 106319;

    public static readonly (uint Guid, uint Entry)[] MerchantCoastChestSpawns =
    [
        (300129, BatteredChestA), (300132, BatteredChestA), (300141, BatteredChestB), (300161, BatteredChestA), (300143, BatteredChestA),
        (300133, BatteredChestB), (300160, BatteredChestA), (300154, BatteredChestA), (13439, BatteredChestA), (13349, BatteredChestB),
        (85777, BatteredChestB), (300049, BatteredChestA),
    ];

    public const uint ThurosPool = 1069;
    public const uint Thuros = 61;
    public static readonly uint[] ThurosSpawns = [81107, 134007, 134008, 134009, 134010, 134011, 134012, 134013];

    public const uint GoldshireEggs = 25002;
    public const uint BrightlyColoredEgg = 113768;
    public const ushort Noblegarden = 9;
    public static readonly uint[] EggSpawns = [83709, 83513, 83807, 83805, 83803, 83801, 83799, 83705, 83703, 83701, 83699, 83797];

    public const uint PlaguebloomPool = 8648;
    public const uint Plaguebloom = 176587;
    public static readonly uint[] PlaguebloomSpawns = [19897, 19900, 19901];

    public static IEnumerable<PoolTemplateData> Templates =>
    [
        new(BadlandsMother, 1, "Mineral nodes - Badlands - multinodes subzone 2"),
        .. BadlandsSpots.Select(s => new PoolTemplateData(s.Pool, 1, "Mineral nodes - Badlands - multinodes subzone 2")),
        new(MerchantCoastChests, 1, "The Barrens (The Merchant Coast) - Chest Pool"),
        new(ThurosPool, 1, "Thuros Lightfingers (61)"),
        new(GoldshireEggs, 10, "Noblegarden (Goldshire) - Brightly Colored Egg"),
        new(50003, 1, "the largest pool_template entry of z2815 (pool ids up to it are in range)"),
    ];

    public static IEnumerable<PoolPoolLink> BadlandsLinks => BadlandsSpots.Select(s => new PoolPoolLink(s.Pool, BadlandsMother, 0f));

    public static IEnumerable<PoolSpawnLink> BadlandsNodes
        => BadlandsSpots.SelectMany(s => s.Nodes.Select(n => new PoolSpawnLink(n.Guid, s.Pool, n.Chance)));

    /// <summary>A game object catalog over <paramref name="guidRows"/> with the spawn map given by <paramref name="spawns"/> (guid, entry; map 0).</summary>
    public static PoolCatalog Catalog(IEnumerable<PoolSpawnLink> guidRows, IEnumerable<(uint Guid, uint Entry)> spawns,
        IEnumerable<PoolPoolLink>? links = null, IEnumerable<PoolSpawnLink>? entryRows = null)
        => PoolCatalog.Build(Templates, guidRows, entryRows ?? [], links ?? [], spawns.ToDictionary(s => s.Guid, s => (s.Entry, 0u)));
}
