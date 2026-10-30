using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The repair trip plans to at most <see cref="PlayerbotTownGoals.MaxRepairPlansPerThink"/> spawns in one think, however many times the
/// think calls Update (live-w27-r1: a bot whose start was off the mesh planned to every repair spawn of the map in one think, up to
/// 1,740 queries). The refused ones stay blocked, the next think goes on from the next candidate, a start off the mesh does not stop
/// the search (a farther spawn may plan), and with every spawn refused the trip rests and is no candidate.
/// </summary>
public sealed class PlayerbotRepairFanOutTests
{
    private const int Bound = PlayerbotTownGoals.MaxRepairPlansPerThink;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryPlanFailing_PlansTheBoundPerThink_VisitsEachSpawnOnce_ThenRests(bool startOnMesh)
    {
        var path = new ScriptedPathfinder { StartOnMesh = startOnMesh, Reaches = _ => false };
        await Run(path, (player, goals, think) =>
        {
            Assert.True(goals.HasCandidate(player));
            var perThink = new List<int>();
            for (int i = 0; i < 5; i++) perThink.Add(think(1).Distinct().Count());
            int[] expected = [Bound, Bound, Bound, ScriptedPathfinder.SpawnCount - 3 * Bound, 0];
            Assert.Equal(expected, perThink);
            Assert.Equal(ScriptedPathfinder.SpawnCount, path.AllEnds.Distinct().Count()); // every spawn tried ...
            Assert.Equal(path.AllEnds.Distinct().OrderBy(e => e.X).ToList(), path.AllEnds.Distinct().ToList()); // ... nearest first
            // Every candidate failed: the trip rests, and with it HasCandidate (so the brain's home return can run).
            Assert.False(goals.HasCandidate(player));
        });
    }

    [Fact]
    public async Task OnceEveryCandidateFailed_NothingIsPlannedDuringTheBackoff()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => false };
        await Run(path, (player, goals, think) =>
        {
            for (int i = 0; i < 20 && goals.HasCandidate(player); i++) think(1);
            Assert.Equal(ScriptedPathfinder.SpawnCount, path.AllEnds.Distinct().Count());
            int plans = path.Queries;
            for (int i = 0; i < 10; i++)
            {
                Assert.False(goals.HasCandidate(player));
                Assert.Empty(think(1));
            }

            Assert.Equal(plans, path.Queries); // Run freezes the world clock (manual clock): no time passes, so the backoff cannot have run out
        });
    }

    [Fact]
    public async Task StartOffTheMesh_NearestRefused_AFartherSpawnThatPlansIsFound()
    {
        var path = new ScriptedPathfinder { StartOnMesh = false, Reaches = end => end.X >= ScriptedPathfinder.SpawnX(1) - 0.01f };
        await Run(path, (player, goals, think) =>
        {
            List<Vector3> ends = think(1);
            Assert.Equal(ScriptedPathfinder.SpawnX(0), ends[0].X, 0.01f);
            Assert.Contains(ends, e => Math.Abs(e.X - ScriptedPathfinder.SpawnX(1)) < 0.01f);
            Assert.True(ends.Distinct().Count() <= Bound);
            Assert.Equal(951u, goals.TargetEntry);
            Assert.Equal(PlayerbotGoalKind.Vendor, goals.Goal);
        });
    }

    [Fact]
    public async Task AReachableSpawnFurtherDownTheList_IsFoundWithinAFewThinks()
    {
        const int reachable = 4; // the fifth nearest
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = end => end.X >= ScriptedPathfinder.SpawnX(reachable) - 0.01f };
        await Run(path, (player, goals, think) =>
        {
            Assert.Equal(Bound, think(1).Distinct().Count());
            Assert.True(think(1).Distinct().Count() <= Bound);
            Assert.Equal(ScriptedPathfinder.SpawnX(reachable), path.AllEnds[^1].X, 0.01f);
            Assert.Equal(950u + reachable, goals.TargetEntry);
        });
    }

    [Fact]
    public async Task WhenTheFirstCandidatePlans_OneCandidateIsPlannedTo_AndItIsTheNearest()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => true };
        await Run(path, (player, goals, think) =>
        {
            Assert.Single(think(1).Distinct());
            Assert.Equal(ScriptedPathfinder.SpawnX(0), path.AllEnds[0].X, 0.01f);
            Assert.Equal(950u, goals.TargetEntry);
        });
    }

    [Fact]
    public async Task TwoUpdatesInOneThink_PlanAtMostTheBoundInTotal()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => false };
        await Run(path, (player, goals, think) =>
        {
            Assert.Equal(Bound, think(2).Distinct().Count());
            Assert.Equal(Bound, think(2).Distinct().Count());
        });
    }

    private delegate List<Vector3> Think(int updates, bool beginThink = true);

    /// <summary>
    /// The stuck clock runs once per think, not once per Update. The walk to the nearest spawn makes no headway (the bot is rooted, so no
    /// step is taken), the brain calls Update twice a think, and the spawn is set aside only when RepairTripStuckMs of thinks (500 ms
    /// each) have passed: a clock ticking on every Update would set it aside after half as many.
    /// </summary>
    [Fact]
    public async Task StuckClock_RunsOncePerThink_NotOncePerUpdate()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => true };
        await Run(path, (player, goals, think) =>
        {
            think(1); // plans to the nearest spawn and takes the first step
            Assert.Equal(950u, goals.TargetEntry);
            // Rooted from here on (the motion reset, so no step is made): no headway for the rest of the walk.
            PlayerbotMotion.Reset(player);
            var movement = player.Movement;
            movement.Flags |= MovementFlags.Root;
            player.ApplyMovement(movement, 0);
            int ticks = (int)(PlayerbotTownGoals.RepairTripStuckMs / 500);
            for (int i = 1; i < ticks; i++)
            {
                think(2);
                Assert.Equal(950u, goals.TargetEntry); // i x 500 ms stuck: still the same spawn
            }

            think(2); // the RepairTripStuckMs-th: the spawn is set aside, the next ones are planned to (and refused: rooted)
            Assert.NotEqual(950u, goals.TargetEntry);
        });
    }

    /// <summary>
    /// A caller that never calls BeginThink is not capped at the bound for its lifetime: the world clock moving on opens a new think.
    /// (Real clock here: the wait only has to outlast one millisecond; the assertions are lower bounds, so a tick inside a think is harmless.)
    /// </summary>
    [Fact]
    public async Task WithoutBeginThink_TheBudgetComesBackWhenTheWorldClockMovesOn()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => false };
        await Run(path, (player, goals, think) =>
        {
            int first = think(2, beginThink: false).Distinct().Count();
            Assert.True(first >= Bound);
        }, manualClock: false, afterTicks: (player, goals, think) =>
            Assert.True(think(2, beginThink: false).Distinct().Count() > 0, "the second think planned to nothing: the first think's budget never came back"));
    }

    /// <summary>Runs <paramref name="body"/> on the world thread; <c>think(updates)</c> opens a think, calls Update that many times, and returns the destinations planned to.</summary>
    private static async Task Run(
        ScriptedPathfinder path, Action<Player, PlayerbotTownGoals, Think> body, bool manualClock = true, Action<Player, PlayerbotTownGoals, Think>? afterTicks = null)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9212, durability: 20));
        using IDisposable itemScope = items.Use();
        ManyRepairServices.Current.Value = new ManyRepairFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(RepairPrices());
                if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                Think think = (updates, beginThink) =>
                {
                    Player player = session.Player!;
                    path.Ends.Clear();
                    if (beginThink) goals.BeginThink();
                    for (int i = 0; i < updates; i++)
                    {
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(1);
                        goals.Update(player, 500);
                    }

                    path.AllEnds.AddRange(path.Ends);
                    return [.. path.Ends];
                };
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: path);
                    PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    player.Money = 100;
                    body(player, goals, think);
                    return true;
                });
                if (afterTicks is not null)
                {
                    // The real clock reads the tick start, so time moves on only between world commands: let a few ticks pass (5 ms each).
                    long tickBefore = host.World.Clock.TickNumber;
                    await WorldTestHost.WaitForAsync(() => host.World.Clock.TickNumber >= tickBefore + 3 && host.World.Clock.ElapsedMs > 0, "world ticks");
                    await host.OnWorldAsync(() => afterTicks(session.Player!, goals, think));
                }
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { ManyRepairServices.Current.Value = null; }
    }

    /// <summary>Freezes game time for the host (<see cref="WorldRuntime.UseManualClock"/>): only <c>AdvanceClockAsync</c> moves it.</summary>
    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
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
/// "is the bot on the mesh" probe, answered by <see cref="StartOnMesh"/>. <see cref="Queries"/> counts every query to a destination (the plan queries and the steep-ground escape ones), not the start probe.
/// </summary>
internal sealed class ScriptedPathfinder : IPathfinder
{
    public const int SpawnCount = 10;
    public bool Enabled => true;
    public bool StartOnMesh { get; init; } = true;
    public Func<Vector3, bool> Reaches { get; init; } = _ => true;
    public int Queries { get; private set; }
    public List<Vector3> Ends { get; } = [];
    public List<Vector3> AllEnds { get; } = [];

    /// <summary>The x of the i-th repair spawn: 130 yards and more east of the start position, nearest first.</summary>
    public static float SpawnX(int i) => -8949.95f + 130f + 10f * i;

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        if (start == end) return StartOnMesh ? PathResult.StraightLine(start, end, PathType.Normal) : PathResult.None(start);
        Queries++;
        Ends.Add(end);
        return Reaches(end) ? PathResult.StraightLine(start, end, PathType.Normal) : PathResult.None(start);
    }
}

/// <summary>Repair NPCs (entries 950 up), each 10 yards further east than the one before, the first 130 yards from the start.</summary>
internal sealed class ManyRepairFixture : ICreatureDataStore, INpcContentStore
{
    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty);

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        Enumerable.Range(0, ScriptedPathfinder.SpawnCount).Select(i => new CreatureTemplate
            { Entry = (uint)(950 + i), Name = $"armorer {i}", Faction = 900011, NpcFlags = (uint)NpcFlags.Repair, DisplayIds = [49] }).ToList(),
        Enumerable.Range(0, ScriptedPathfinder.SpawnCount).Select(i => new CreatureSpawn
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
