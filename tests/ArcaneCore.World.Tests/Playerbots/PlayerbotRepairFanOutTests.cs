using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The repair trip plans to at most <see cref="PlayerbotTownGoals.MaxRepairPlansPerThink"/> spawns in one think (live-w27-r1: a bot
/// whose start was off the mesh planned to every repair spawn of the map in one think, up to 1,740 queries), keeps the refused ones
/// blocked, goes on from the next candidate at the next think, and does not set a spawn aside when the start itself is unplannable.
/// </summary>
public sealed class PlayerbotRepairFanOutTests
{
    private const int Spawns = 10;

    [Fact]
    public async Task EveryPlanFailing_PlansAtMostTheBoundPerThink_AndVisitsEachSpawnOnce()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => false };
        List<int> perThink = await RunThinks(path, thinks: 5);

        Assert.All(perThink, count => Assert.True(count <= PlayerbotTownGoals.MaxRepairPlansPerThink, $"plans per think: {string.Join(", ", perThink)}"));
        Assert.Equal(PlayerbotTownGoals.MaxRepairPlansPerThink, perThink[0]);
        Assert.Equal(Spawns, path.Ends.Distinct().Count()); // every spawn was tried ...
        Assert.Equal(path.Ends.OrderBy(e => e.X).ToList(), path.Ends);   // nearest first, and none twice: the blocked set holds across thinks
    }

    [Fact]
    public async Task StartOffTheMesh_StopsAfterOnePlan_AndSetsNoSpawnAside()
    {
        var path = new ScriptedPathfinder { StartOnMesh = false, Reaches = _ => false };
        List<int> perThink = await RunThinks(path, thinks: 4);

        Assert.Equal([1, 1, 1, 1], perThink);
        Assert.Single(path.Ends.Distinct()); // the nearest spawn again each think: it was not blacklisted
    }

    [Fact]
    public async Task AReachableSpawnFurtherDownTheList_IsFoundWithinAFewThinks()
    {
        const int reachable = 4; // the fifth nearest
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = end => end.X >= ScriptedPathfinder.SpawnX(reachable) - 0.01f };
        List<int> perThink = await RunThinks(path, thinks: 2);

        Assert.All(perThink, count => Assert.True(count <= PlayerbotTownGoals.MaxRepairPlansPerThink));
        Assert.Equal(2, perThink.Count);
        Assert.Equal(ScriptedPathfinder.SpawnX(reachable), path.Ends[^1].X, 0.01f);
        Assert.Equal(950u + reachable, path.TargetEntry);
    }

    [Fact]
    public async Task WhenTheFirstCandidatePlans_OneQueryIsMade_AndItIsTheNearest()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => true };
        List<int> perThink = await RunThinks(path, thinks: 1);

        Assert.Equal([1], perThink);
        Assert.Equal(ScriptedPathfinder.SpawnX(0), path.Ends[0].X, 0.01f);
        Assert.Equal(950u, path.TargetEntry);
    }

    private static async Task<List<int>> RunThinks(ScriptedPathfinder path, int thinks)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9212, durability: 20));
        using IDisposable itemScope = items.Use();
        ManyRepairServices.Current.Value = new ManyRepairFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(RepairPrices()));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                return await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: path);
                    PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    player.Money = 100;

                    var perThink = new List<int>();
                    for (int think = 0; think < thinks; think++)
                    {
                        path.Ends.Clear();
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(1);
                        goals.Update(player, 500);
                        perThink.Add(path.Ends.Distinct().Count()); // candidates planned to (a refused plan may try more starts for one)
                        path.TargetEntry = goals.TargetEntry;
                        path.AllEnds.AddRange(path.Ends);
                    }

                    path.Ends.Clear();
                    path.Ends.AddRange(path.AllEnds);
                    return perThink;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { ManyRepairServices.Current.Value = null; }
    }

    private static ItemTemplate Boots(uint entry, uint durability) => new()
    {
        Entry = entry, Name = $"boots-{entry}", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
        InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = durability, ItemLevel = 10, Quality = 1, Stackable = 1,
        AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
    };

    private static RepairCostTable RepairPrices() => new([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]);
}

/// <summary>
/// Plans to a destination are answered by <see cref="Reaches"/> (a straight line, or no path); a query from a point to itself is the
/// "is the bot on the mesh" probe, answered by <see cref="StartOnMesh"/>. <see cref="Plans"/> counts the plan queries only.
/// </summary>
internal sealed class ScriptedPathfinder : IPathfinder
{
    public bool Enabled => true;
    public bool StartOnMesh { get; init; } = true;
    public Func<Vector3, bool> Reaches { get; init; } = _ => true;
    public int Plans { get; private set; }
    public List<Vector3> Ends { get; } = [];
    public List<Vector3> AllEnds { get; } = [];
    public uint TargetEntry { get; set; }

    /// <summary>The x of the i-th repair spawn: 130 yards and more east of the start position, nearest first.</summary>
    public static float SpawnX(int i) => -8949.95f + 130f + 10f * i;

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        if (start == end) return StartOnMesh ? PathResult.StraightLine(start, end, PathType.Normal) : PathResult.None(start);
        Plans++;
        Ends.Add(end);
        return Reaches(end) ? PathResult.StraightLine(start, end, PathType.Normal) : PathResult.None(start);
    }
}

/// <summary>Ten repair NPCs (entries 950 to 959), each 10 yards further east than the one before, the first 130 yards from the start.</summary>
internal sealed class ManyRepairFixture : ICreatureDataStore, INpcContentStore
{
    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty);

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        Enumerable.Range(0, 10).Select(i => new CreatureTemplate
            { Entry = (uint)(950 + i), Name = $"armorer {i}", Faction = 900011, NpcFlags = (uint)NpcFlags.Repair, DisplayIds = [49] }).ToList(),
        Enumerable.Range(0, 10).Select(i => new CreatureSpawn
            { Guid = (uint)(95001 + i), Entry = (uint)(950 + i), MapId = 0, X = ScriptedPathfinder.SpawnX(i), Y = -132.49f, Z = 83.53f }).ToList(),
        [], [], []));
}

internal sealed class ManyRepairServices : IWorldTestServices
{
    public static readonly AsyncLocal<ManyRepairFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}
