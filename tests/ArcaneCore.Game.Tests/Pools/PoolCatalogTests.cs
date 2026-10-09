using ArcaneCore.Kernel.WorldData.Pools;
using Xunit;
using static ArcaneCore.Game.Tests.Pools.ClassicDbPoolRows;

namespace ArcaneCore.Game.Tests.Pools;

/// <summary>
/// <see cref="PoolCatalog.Build"/> against the load rules of cmangos PoolManager::LoadFromDB (Pools/PoolManager.cpp:598-1046), over classic-db
/// z2815 rows (<see cref="ClassicDbPoolRows"/>).
/// </summary>
public sealed class PoolCatalogTests
{
    [Fact]
    public void BadlandsMultinode_ChildPoolsBelongToTheMother_AndOnlyTheMotherAutoSpawns()
    {
        PoolCatalog catalog = Catalog(BadlandsNodes, BadlandsSpots.SelectMany(s => s.Nodes.Select(n => (n.Guid, n.Entry))), BadlandsLinks);

        PoolDefinition mother = catalog.Find(BadlandsMother)!;
        Assert.True(mother.AutoSpawn);
        Assert.Equal(0u, mother.MapId); // through its children: the mother has no spawn of its own
        Assert.Equal(new uint[] { 3026, 3027, 3028, 3029, 3030 }, mother.Children.Select(c => c.Id).Order());
        PoolDefinition spot = catalog.Find(3027)!;
        Assert.False(spot.AutoSpawn); // a child is spawned by its mother
        Assert.Equal(BadlandsMother, spot.Mother);
        Assert.Equal(new uint[] { 70977, 70979 }, spot.ExplicitlyChanced.Select(m => m.Id).Order()); // chance 20 in a max_limit 1 pool: explicit
        Assert.Equal([70976u], spot.EqualChanced.Select(m => m.Id));
        Assert.Equal(BadlandsMother, catalog.TopPoolOf(3027));
        Assert.Equal(3027u, catalog.PoolOf(70979));
    }

    [Fact]
    public void AChanceInAPoolWhoseLimitIsNotOne_IsEqualChanced()
    {
        // cmangos PoolGroup::AddEntry: explicit only when max_limit is 1 (Goldshire eggs: max_limit 10).
        PoolCatalog catalog = Catalog([new PoolSpawnLink(EggSpawns[0], GoldshireEggs, 30f), new PoolSpawnLink(EggSpawns[1], GoldshireEggs, 0f)],
            EggSpawns.Take(2).Select(g => (g, BrightlyColoredEgg)));
        PoolDefinition pool = catalog.Find(GoldshireEggs)!;
        Assert.Empty(pool.ExplicitlyChanced);
        Assert.Equal(2, pool.EqualChanced.Count);
    }

    [Fact]
    public void RowsCmangosDrops_AreDroppedAndReported()
    {
        PoolCatalog catalog = PoolCatalog.Build(
            Templates,
            [
                new PoolSpawnLink(1, MerchantCoastChests, 0f),       // no such spawn
                new PoolSpawnLink(13439, 60000, 0f),                  // pool id above the largest template entry (50003)
                new PoolSpawnLink(13349, MerchantCoastChests, 120f),  // chance out of range
                new PoolSpawnLink(85777, MerchantCoastChests, 0f),
                new PoolSpawnLink(300049, MerchantCoastChests, 0f),   // on another map than the pool's first spawn
            ],
            [],
            [new PoolPoolLink(3027, 3028, 0f), new PoolPoolLink(3028, 3027, 0f), new PoolPoolLink(3029, 3029, 0f)], // a cycle, a self-link
            new Dictionary<uint, (uint, uint)> { [13439] = (BatteredChestA, 1), [13349] = (BatteredChestB, 1), [85777] = (BatteredChestB, 1), [300049] = (BatteredChestA, 0) });

        Assert.Equal([85777u], catalog.Find(MerchantCoastChests)!.Spawns.Select(m => m.Id));
        Assert.Equal(6, catalog.Issues.Count);
        Assert.True(catalog.IsPooled(13439)); // still left out of its grid, as cmangos' pool_gameobject join does
        Assert.Equal(0u, catalog.PoolOf(13439));
        // One link of the 3027 <-> 3028 cycle is cut, so neither pool is its own ancestor.
        Assert.False(catalog.Find(3027)!.Mother == 3028 && catalog.Find(3028)!.Mother == 3027);
    }

    [Fact]
    public void ExplicitChancesThatCannotPick_KeepThePoolFromSpawning()
    {
        // cmangos CheckPool: with no equally chanced member the explicit chances must add up to 100 (or 0).
        PoolCatalog catalog = Catalog(
            [new PoolSpawnLink(71084, 3029, 20f), new PoolSpawnLink(71082, 3029, 20f)], [(71084u, TruesilverDeposit), (71082u, GoldVein)]);
        Assert.False(catalog.Find(3029)!.AutoSpawn);
        Assert.Contains(catalog.Issues, i => i.Contains("3029", StringComparison.Ordinal));
    }
}
