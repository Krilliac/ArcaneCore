using System.Numerics;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Why the w29 "dungeon" groups never finished: an item objective with one instance dropper among hundreds in the open world made a
/// five-bot Deadmines group; the groups filled every slot, walked on the 300 s step timeout, were retried every ten minutes and logged
/// nothing about where they were. The goal rule, the reserved run slot, the escalating set-aside, the distance-scaled travel budget and
/// the timeout line.
/// </summary>
public sealed class PlayerbotDungeonGroupGoalTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
    private const uint Deadmines = 36;

    // --- the goal rule ------------------------------------------------------------------------------------------------

    private static CreatureContent Spawns(params (uint Entry, uint Map)[] spawns) => new(
        [.. spawns.Select(s => s.Entry).Distinct().Select(e => new CreatureTemplate { Entry = e, Name = "Dropper " + e, Faction = 14, DisplayIds = [903] })],
        [.. spawns.Select((s, i) => new CreatureSpawn { Guid = (uint)(i + 1), Entry = s.Entry, MapId = s.Map, X = 1f, Y = 2f, Z = 3f })],
        [], [], []);

    private static bool Instance(uint map) => map == Deadmines;

    [Fact]
    public void AnItemWithDroppersInTheOpenWorldAndInsideAnInstance_IsNoInstanceGoal()
    {
        CreatureContent creatures = Spawns((100, 0), (200, Deadmines), (300, Deadmines));
        Assert.False(PlayerbotGroupContent.DroppedOnlyInInstances([100, 200, 300], creatures, Instance));
        Assert.False(PlayerbotGroupContent.DroppedOnlyInInstances([200, 100], creatures, Instance)); // whichever comes first
        Assert.False(PlayerbotGroupContent.DroppedOnlyInInstances([200, 300], Spawns((200, Deadmines), (300, 1u)), Instance)); // the other continent too
    }

    [Fact]
    public void AnItemWhoseEveryDropperSpawnsInsideInstances_StillQualifies()
    {
        CreatureContent creatures = Spawns((200, Deadmines), (300, Deadmines), (400, 189));
        Assert.True(PlayerbotGroupContent.DroppedOnlyInInstances([200, 300], creatures, Instance));
        // A dropper that spawns nowhere says nothing either way.
        Assert.True(PlayerbotGroupContent.DroppedOnlyInInstances([200, 999], creatures, Instance));
        // An item without any spawned dropper is no goal at all.
        Assert.False(PlayerbotGroupContent.DroppedOnlyInInstances([999], creatures, Instance));
        Assert.False(PlayerbotGroupContent.DroppedOnlyInInstances([], creatures, Instance));
    }

    [Fact]
    public async Task AQuestItemAlsoDroppedByAnOpenWorldCreature_NeverMakesABotWaitForADungeonGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonItemQuest], dungeon: true, openWorldTrophyDrop: true);
        var tank = await world.AddBotAsync("Opentank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Openprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Openmage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];

        // The bots have the quest and stand at the entrance, exactly as in the twin test that forms the group (the boss is the only dropper).
        Assert.True(await world.RunUntilAsync(60_000, () => bots.All(b => world.HasQuest(b.Id, DungeonItemQuest))), await world.TraceAsync());
        await world.RunAsync(90_000);

        Assert.Empty(world.Coordinator.Groups);
        Assert.Empty(world.Coordinator.WaitingBots);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("needs a group", StringComparison.Ordinal));
    }

    /// <summary>
    /// The real world database (classic-db import): the three items behind the w29 groups have droppers in the open world, so none is an
    /// instance goal; the rule still says yes for some items (it is not a constant false).
    /// </summary>
    [RealWorldContentFact]
    public async Task RealWorld_WoolClothMurlocEyeAndCopperModulator_AreNotInstanceOnly_ButSomeItemsAre()
    {
        string path = Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!;
        DbContextOptions<WorldDbContext> options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options;
        CreatureContent creatures;
        LootContent loot;
        MapContent maps;
        await using (var db = new WorldDbContext(options))
        {
            creatures = await new EfCreatureDataStore(db).LoadAsync();
            loot = await new EfLootDataStore(db).LoadAsync();
            maps = await new EfMapDataStore(db).LoadAsync();
        }

        var registry = new MapRegistry(maps.Maps);
        Dictionary<uint, uint[]> index = PlayerbotGroupContent.DropIndex(loot);
        bool InstanceOnly(uint item) => PlayerbotGroupContent.DroppedOnlyInInstances(index[item], creatures, map => registry.Find(map) is { IsDungeon: true });

        foreach ((uint item, string name) in new[] { (730u, "Murloc Eye"), (2592u, "Wool Cloth"), (4363u, "Copper Modulator") })
        {
            Assert.True(index.ContainsKey(item), $"{name} ({item}) has no dropper in the fixture: the check would pass for the wrong reason");
            Assert.False(InstanceOnly(item), $"{name} ({item}) has droppers in the open world and must not be an instance goal");
        }

        Assert.Contains(index.Keys, item => InstanceOnly(item));
    }

    // --- the reserved run slot ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(4, true, 3)]
    [InlineData(2, true, 1)]
    [InlineData(1, true, 1)] // one slot: nothing to reserve
    [InlineData(4, false, 4)] // runs off: quest groups may use them all
    public void TheQuestGroupLimit_LeavesOneSlotToDungeonRuns_WhileTheyCanStart(int maxGroups, bool runs, int expected)
    {
        var options = new PlayerbotOptions { Groups = { MaxGroups = maxGroups, DungeonRuns = { Enabled = runs } } };
        var coordinator = new PlayerbotGroupCoordinator(new ServiceCollection()
            .AddSingleton(Options.Create(options)).BuildServiceProvider(),
            NullLogger<PlayerbotGroupCoordinator>.Instance);
        Assert.Equal(expected, coordinator.QuestGroupLimit);
    }

    // --- the escalating set-aside -------------------------------------------------------------------------------------

    [Fact]
    public void TheSetAside_DoublesWithEveryFailureInARow_UpToTwoHours()
    {
        Assert.Equal(new uint[] { 600_000, 600_000, 1_200_000, 2_400_000, 4_800_000, 7_200_000, 7_200_000, 7_200_000 },
            Enumerable.Range(0, 8).Select(PlayerbotGroupCoordinator.EscalatedSetAsideMs).ToArray());
        Assert.Equal(PlayerbotGroupCoordinator.MaxSetAsideMs, PlayerbotGroupCoordinator.EscalatedSetAsideMs(int.MaxValue));
    }

    [Fact]
    public async Task AGoalThatFailsAgainForTheSameBots_IsSetAsideLongerEachTime_AndAFreshGoalStartsAtTenMinutes()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true);
        var tank = await world.AddBotAsync("Failtank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Failprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Failmage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];
        (PlayerbotGroupGoalKind, uint) key = (PlayerbotGroupGoalKind.Dungeon, Deadmines);

        uint first = await FailTheGroupAsync(world, bots, 120_000);
        Assert.InRange(first, 590_000u, 600_000u);
        Assert.True(world.Coordinator.IsSetAside(tank.Id, key));

        // Ten minutes later the bots look for a group again (back at the entrance, in case their brains wandered off) and fail again.
        Assert.True(await world.RunUntilAsync(700_000, () => !world.Coordinator.IsSetAside(tank.Id, key)), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            foreach ((Guid id, string _) in bots) world.Player(id).Relocate(EntranceArea.X, EntranceArea.Y, EntranceArea.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        uint second = await FailTheGroupAsync(world, bots, 900_000);
        Assert.InRange(second, 1_190_000u, 1_200_000u);
        Assert.True(second > first, $"first {first} ms, second {second} ms");
    }

    /// <summary>Wait for the bots' group, make its travel step overdue (a clock jump), wait for it to give up; the leader's set-aside left, in ms.</summary>
    private static async Task<uint> FailTheGroupAsync(GroupTestWorld world, (Guid Id, string Name)[] bots, uint waitMs)
    {
        Assert.True(await world.RunUntilAsync(waitMs, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            group.State = PlayerbotGroupState.Travelling;
            group.StateSinceMs = unchecked(world.World.Host.World.NowMs - 7_200_000u);
            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal("travel-timeout", group.Reason);
        return await world.OnWorldAsync(() => world.Coordinator.SetAsideLeftMs(group.LeaderBotId, group.Goal.Key));
    }

    // --- the travel budget and the timeout line --------------------------------------------------------------------------

    [Fact]
    public void TheTravelBudget_GrowsWithTheDistance_FromTheStepMinimumUpToTheCap()
    {
        const uint cap = 30 * 60_000;
        // 7 yards a second, three times the walking time plus two minutes.
        Assert.Equal(PlayerbotGroupCoordinator.StepTimeoutMs, PlayerbotGroupCoordinator.TravelBudgetMs(0f, 7f, cap)); // next to the objective
        Assert.Equal(PlayerbotGroupCoordinator.StepTimeoutMs, PlayerbotGroupCoordinator.TravelBudgetMs(300f, 7f, cap)); // 249 s is below the minimum
        Assert.InRange(PlayerbotGroupCoordinator.TravelBudgetMs(1200f, 7f, cap), 600_000u, 700_000u);
        Assert.InRange(PlayerbotGroupCoordinator.TravelBudgetMs(1500f, 7f, cap), 700_000u, 800_000u);
        Assert.True(PlayerbotGroupCoordinator.TravelBudgetMs(1500f, 7f, cap) > PlayerbotGroupCoordinator.TravelBudgetMs(1200f, 7f, cap));
        Assert.Equal(cap, PlayerbotGroupCoordinator.TravelBudgetMs(100_000f, 7f, cap));
        Assert.Equal(PlayerbotGroupCoordinator.StepTimeoutMs, PlayerbotGroupCoordinator.TravelBudgetMs(float.NaN, 7f, cap));
        Assert.Equal(PlayerbotGroupCoordinator.StepTimeoutMs, PlayerbotGroupCoordinator.TravelBudgetMs(100_000f, 7f, 60_000)); // a cap below the minimum does not shorten it
    }

    /// <summary>
    /// An instance group 1200 yards from its entrance gets at least ten minutes to gather and walk (the generic step timeout of 300 s fired
    /// 110 times in w29 for groups that gathered and then walked up to 1500 yards through the Moonbrook mobs); an instance group beside
    /// its entrance keeps 300 s. When the budget runs out the log line says where every member stood.
    /// </summary>
    [Fact]
    public async Task AnInstanceGroup_FarFromItsEntrance_GetsALongerBudget_AndATimeoutReportsEveryMembersPosition()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true);
        var tank = await world.AddBotAsync("Fartank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Farprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Farmage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];

        // The quest giver stands at the entrance, so the bots take the quest there and then are 1200 yards away when they gather.
        // Each bot is moved in the run condition (the world thread, straight after the tick in which it holds the quest), never in a
        // later world call: a group needs every member's quest, so it cannot form before the last bot is far away. Moving them after
        // the wait returned let a scan in between form the group at the entrance, with the short 300 s entrance budget. Group forming is
        // also off until the last bot is moved: the coordinator's scan can come in the very tick in which the last bot takes the quest,
        // before the run condition sees it.
        Vector3 far = EntranceArea - new Vector3(1200f, 0f, 0f);
        var moved = new HashSet<Guid>();
        await world.OnWorldAsync(() => world.Coordinator.Options.Enabled = false);
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            foreach ((Guid id, string _) in bots)
            {
                if (moved.Contains(id) || !world.HasQuest(id, DungeonQuest)) continue;
                world.Player(id).Relocate(far.X, far.Y, far.Z, 0, world.World.Host.World.NowMs);
                moved.Add(id);
            }

            if (moved.Count != bots.Length) return false;
            world.Coordinator.Options.Enabled = true;
            return true;
        }), await world.TraceAsync());

        Assert.True(await world.RunUntilAsync(180_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(group.Goal.InInstance);
        Assert.InRange(world.Coordinator.StepLimitOf(group), 600_000u, PlayerbotGroupCoordinator.TravelBudgetMs(10_000f, 7f, 30 * 60_000));

        await world.OnWorldAsync(() =>
        {
            group.State = PlayerbotGroupState.Travelling;
            group.StateSinceMs = unchecked(world.World.Host.World.NowMs - 7_200_000u);
            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());

        string line = Assert.Single(world.Coordinator.Events, e => e.Contains("travel-timeout after", StringComparison.Ordinal));
        Assert.Contains("of 6", line, StringComparison.Ordinal); // the budget in seconds: six hundred and something
        Assert.Contains("(travelling)", line, StringComparison.Ordinal);
        Assert.Equal(3, line.Split("yd to goal").Length - 1);
        Assert.Equal(3, line.Split("yd to leader").Length - 1);
        Assert.Equal(3, line.Split(" map 0 at ").Length - 1);
        Assert.Contains("L12 Warrior tank leader", line, StringComparison.Ordinal);
        Assert.Contains("L12 Priest healer", line, StringComparison.Ordinal);
        Assert.Contains("L12 Mage damage", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Fartank", line, StringComparison.Ordinal); // level and class stand for the bot
    }

    [Fact]
    public void TheTimeoutLine_CarriesTheBudget_AndEachMembersMapPositionAndDistances()
    {
        string line = PlayerbotGroupCoordinator.DescribeTimeout("gather-timeout", 301, 300, "dungeon:36 quest=1", "gathering",
        [
            ("L15 Warrior tank leader", 0, new Vector3(-9000.5f, 100f, 40f), 2200.4f, 0f, true, "travel"),
            ("L16 Priest healer", 1, new Vector3(1.5f, 2.5f, 3.5f), 12345f, 12000f, false, "none"),
        ]);
        Assert.Equal("gather-timeout after 301s of 300s (gathering) for dungeon:36 quest=1:"
            + " [L15 Warrior tank leader map 0 at -9000.5,100.0,40.0 2200yd to goal 0yd to leader alive travel]"
            + " [L16 Priest healer map 1 at 1.5,2.5,3.5 12345yd to goal 12000yd to leader dead none]", line);
    }
}
