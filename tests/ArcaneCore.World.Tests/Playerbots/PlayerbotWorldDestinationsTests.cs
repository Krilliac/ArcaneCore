using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotWorldDestinationsTests
{
    [Fact]
    public void TripScan_IsCachedPerQuestAndMap_AndInvalidatedByContentReplacement()
    {
        var quest = new QuestTemplate { Entry = 6030, Method = 2, MinLevel = 1, QuestLevel = 1 };
        var quests = new QuestStore(new QuestContent([quest],
            [new CreatureQuestRelation { Id = 11038, Quest = 6030 }],
            [new CreatureQuestRelation { Id = 11039, Quest = 6030 }]));
        var giver = new CreatureSpawn { Guid = 3, Entry = 11038, MapId = 1, X = 0, Y = 0, Z = 3 };
        var distant = new CreatureContent([], [giver, new CreatureSpawn { Guid = 1, Entry = 11039, MapId = 1,
            X = 2000, Y = 0, Z = 3 }], [], [], []);
        var nearby = new CreatureContent([], [giver, new CreatureSpawn { Guid = 2, Entry = 11039, MapId = 1,
            X = 200, Y = 0, Z = 3 }], [], [], []);
        var trip = new PlayerbotQuestTripFeasibility();

        Assert.True(trip.IsInfeasible(1, 0, 0, quests, distant, 6030, 1200));
        for (int think = 0; think < 10; think++)
            Assert.True(trip.IsInfeasible(1, 0, 0, quests, distant, 6030, 1200));
        Assert.Equal(1, trip.Evaluations);
        var anotherBot = new PlayerbotQuestTripFeasibility();
        Assert.True(anotherBot.IsInfeasible(1, 1000, 0, quests, distant, 6030, 1200));
        Assert.Equal(0, anotherBot.Evaluations); // the second caller shares the static facts
        Assert.False(trip.IsInfeasible(1, 1000, 0, quests, distant, 6030, 1200, completed: true));
        Assert.Equal(1, trip.Evaluations); // moving the bot does not rescan content
        Assert.False(trip.IsInfeasible(1, 0, 0, quests, distant, 6030, 0));
        Assert.Equal(1, trip.Evaluations); // option change also needs no rescan
        Assert.False(trip.IsInfeasible(1, 0, 0, quests, nearby, 6030, 1200));
        Assert.Equal(2, trip.Evaluations);
    }
    [Fact]
    public void CompletedTripRule_ReachableNowThenStatic()
    {
        var quest = new QuestTemplate { Entry = 6030, Method = 2, MinLevel = 1, QuestLevel = 1 };
        var quests = new QuestStore(new QuestContent([quest],
            [new CreatureQuestRelation { Id = 11038, Quest = 6030 }],
            [new CreatureQuestRelation { Id = 11039, Quest = 6030 }]));
        CreatureSpawn Giver(uint map) => new() { Guid = 3, Entry = 11038, MapId = map, X = 0, Y = 0, Z = 3 };
        CreatureSpawn Ender(uint guid, uint map, float x) => new() { Guid = guid, Entry = 11039, MapId = map, X = x, Y = 0, Z = 3 };
        CreatureContent Content(params CreatureSpawn[] spawns) => new([], spawns, [], [], []);
        var beside = Content(Giver(0), Ender(1, 0, 10));
        var crossContinent = Content(Giver(0), Ender(1, 1, 10));
        var far = Content(Giver(0), Ender(1, 0, 2000));
        var nearAndFar = Content(Giver(0), Ender(1, 0, 10), Ender(2, 0, 5000));
        var noGiver = Content(Ender(1, 1, 0));
        var bothGivers = Content(Giver(0), new CreatureSpawn { Guid = 4, Entry = 11038, MapId = 1, X = 0, Y = 0, Z = 3 },
            Ender(1, 0, 10));
        var trip = new PlayerbotQuestTripFeasibility();
        bool Completed(uint map, float x, CreatureContent content, float limit)
            => trip.IsInfeasible(map, x, 0, quests, content, 6030, limit, completed: true);

        // Rule 1: a bot on the ender's continent next to the ender is never judged infeasible, with or without a limit.
        Assert.False(Completed(1, 0, crossContinent, 1200));
        Assert.False(Completed(1, 0, crossContinent, 0));
        // ... and on the giver's continent the same quest is statically infeasible.
        Assert.True(Completed(0, 0, crossContinent, 1200));
        Assert.True(Completed(0, 0, crossContinent, 0));
        Assert.True(trip.IsStaticallyInfeasible(quests, crossContinent, 6030, 1200));
        Assert.True(trip.IsStaticallyInfeasible(quests, crossContinent, 6030, 0));
        // Rule 1 with a limit: on the ender's continent but beyond it, the static rule still says cross-continent.
        Assert.True(Completed(1, 5000, crossContinent, 1200));
        // Adjacent giver and ender: kept wherever the bot is, including another continent or an instance map.
        Assert.False(Completed(1, 0, beside, 1200));
        Assert.False(Completed(0, 9000, beside, 1200));
        Assert.False(Completed(0, 9000, beside, 0));
        Assert.False(Completed(30, 0, crossContinent, 1200));
        Assert.False(trip.IsStaticallyInfeasible(quests, beside, 6030, 1200));
        Assert.False(trip.IsStaticallyInfeasible(quests, beside, 6030, 0));
        // Rule 2 distance: giver to ender 2000 yd. Beyond the limit from the ender: infeasible; within it: kept; L=0: kept.
        Assert.True(Completed(0, 0, far, 1200));
        Assert.False(Completed(0, 1500, far, 1200));
        Assert.False(Completed(0, 0, far, 0));
        Assert.True(trip.IsStaticallyInfeasible(quests, far, 6030, 1200));
        Assert.False(trip.IsStaticallyInfeasible(quests, far, 6030, 0));
        // A near ender plus a far one: statically feasible whether or not the near one is spawned right now.
        Assert.False(trip.IsStaticallyInfeasible(quests, nearAndFar, 6030, 1200));
        // No creature giver on map 0 or 1: nothing to prove.
        Assert.False(trip.IsStaticallyInfeasible(quests, noGiver, 6030, 1200));
        Assert.False(Completed(0, 0, noGiver, 1200));
        // Every giver map must be infeasible: a giver on map 1 with no ender there, and one on map 0 beside its ender.
        Assert.False(trip.IsStaticallyInfeasible(quests, bothGivers, 6030, 1200));
        var bothInfeasible = Content(Giver(0), new CreatureSpawn { Guid = 4, Entry = 11038, MapId = 1, X = 0, Y = 0, Z = 3 },
            Ender(1, 0, 2000), Ender(2, 1, 2000));
        Assert.True(trip.IsStaticallyInfeasible(quests, bothInfeasible, 6030, 1200));
    }

    [Fact]
    public void AnotherContinentOnly_IsIneligible_ButALocalAlternativeRemainsEligible()
    {
        var quest = new QuestTemplate { Entry = 6030, Method = 2, MinLevel = 1, QuestLevel = 1 };
        var remote = new CreatureQuestRelation { Id = 11039, Quest = 6030 };
        var local = new CreatureQuestRelation { Id = 11040, Quest = 6030 };
        var remoteSpawn = new CreatureSpawn { Guid = 1, Entry = 11039, MapId = 0, X = 1, Y = 2, Z = 3 };
        var localSpawn = new CreatureSpawn { Guid = 2, Entry = 11040, MapId = 1, X = 4, Y = 5, Z = 6 };
        var content = new CreatureContent([], [remoteSpawn, localSpawn], [], [], []);
        var remoteOnly = new QuestStore(new QuestContent([quest], [], [remote]));
        var alternatives = new QuestStore(new QuestContent([quest], [], [remote, local]));
        var unknown = new QuestStore(new QuestContent([quest], [], [remote,
            new CreatureQuestRelation { Id = 11041, Quest = 6030 }]));

        var trip = new PlayerbotQuestTripFeasibility();
        Assert.True(trip.IsInfeasible(1, 0, 0, remoteOnly, content, 6030, 0));
        Assert.False(trip.IsInfeasible(1, 0, 0, alternatives, content, 6030, 0));
        Assert.False(trip.IsInfeasible(1, 0, 0, unknown, content, 6030, 0));
        Assert.False(trip.IsInfeasible(0, 0, 0, remoteOnly, content, 6030, 0));
    }

    [Theory]
    [InlineData(308u, 2686u)] // Distracting Jarven: Thunder Ale
    [InlineData(3861u, 11109u)] // CLUCK!: Special Chicken Feed
    public async Task VendorOnlyRepeatableItem_IsNotDeferred(uint questId, uint itemId)
    {
        var quests = new QuestStore(new QuestContent(
            [new QuestTemplate { Entry = questId, Method = 2, MinLevel = 1, QuestLevel = 1,
                SpecialFlags = 1, ReqItemId1 = itemId, ReqItemCount1 = 1 }], [], []));
        var npcs = new NpcStore(NpcContent.Empty with
        {
            VendorItems = [new VendorItem { Entry = 990001, Item = itemId }],
        });
        Assert.Contains(npcs.Content.VendorItems, vendor => vendor.Item == itemId);
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Assert.True(PlayerbotWorldDestinations.CanPursueStarter(session.Player!, quests.Get(questId)!, LootWith(4886), npcs));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task QuestRewardOnlyItem_DoesNotCountAsAvailableToAnUntakenRepeatable()
    {
        const uint rewardItem = 930001;
        var quests = new QuestStore(new QuestContent(
            [new QuestTemplate { Entry = 930002, Method = 2, MinLevel = 1, QuestLevel = 1,
                SpecialFlags = 1, ReqItemId1 = rewardItem, ReqItemCount1 = 1 },
             new QuestTemplate { Entry = 930003, Method = 2, RewItemId1 = rewardItem, RewItemCount1 = 1 }], [], []));
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Assert.False(PlayerbotWorldDestinations.CanPursueStarter(session.Player!, quests.Get(930002)!, LootWith(4886), NpcStore.Empty));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Theory]
    [InlineData(813u, 4886u, 4u)] // Finding the Antidote: quest-only creature drops
    [InlineData(410u, 3080u, 1u)] // The Dormant Shade: quest-only creature drop
    public async Task RepeatableQuestWithAQuestGatedDrop_IsNotDeferred(uint questId, uint itemId, uint count)
    {
        var quests = new QuestStore(new QuestContent(
            [new QuestTemplate { Entry = questId, Method = 2, MinLevel = 1, QuestLevel = 1,
                SpecialFlags = 1, ReqItemId1 = itemId, ReqItemCount1 = count }], [], []));
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Assert.True(PlayerbotWorldDestinations.CanPursueStarter(session.Player!, quests.Get(questId)!, LootWith(itemId), NpcStore.Empty));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task NonRepeatableDeliveryWithoutItems_IsNotDeferred()
    {
        var quests = new QuestStore(new QuestContent(
            [new QuestTemplate { Entry = 810001, Method = 2, MinLevel = 1, QuestLevel = 1,
                ReqItemId1 = 4886, ReqItemCount1 = 4 }], [], []));
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Assert.True(PlayerbotWorldDestinations.CanPursueStarter(session.Player!, quests.Get(810001)!, LootContent.Empty, NpcStore.Empty));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ItemOnlyRepeatableStarterNeedsItsTurnInItemBeforeBotTravels()
    {
        // ClassicDB quest 579 at Donyal Tovald in Stormwind asks for Library Scrip 3898.
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 3898, Name = "Library Scrip", Class = (uint)ItemClass.Quest, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        var quests = new QuestStore(new QuestContent(
            [new QuestTemplate { Entry = 579, Method = 2, MinLevel = 1, QuestLevel = 1,
                SpecialFlags = 1, ReqItemId1 = 3898, ReqItemCount1 = 1 }], [], []));
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                Assert.False(PlayerbotWorldDestinations.CanPursueStarter(player, quests.Get(579)!, LootWith(4886), NpcStore.Empty));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(3898, 1, out _));
                Assert.True(PlayerbotWorldDestinations.CanPursueStarter(player, quests.Get(579)!, LootWith(4886), NpcStore.Empty));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task BlockedNearestHintRotatesToReachableSameEntryOnNextThink()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 20001, Entry = 6, MapId = 0, X = startX + 300, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 20002, Entry = 6, MapId = 0, X = startX - 400, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content)));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var pathfinder = new BlockEastPathfinder();
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                var player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                float before = player.X;
                Assert.False(destinations.Update(player, 6, 500));
                Assert.Equal(2, pathfinder.Calls); // the refused route, then whether the bot stands on walkable ground (it does)
                Assert.Equal(before, player.X);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.Equal(3, pathfinder.Calls);
                Assert.Equal(before, player.X);
                Assert.Equal(6u, destinations.TargetEntry);
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.True(player.X < before);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FailedLongRouteDoesNotSearchAnotherFarSpawnBeforeBackoff()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "far objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 21001, Entry = 6, MapId = 0, X = startX + 1600, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 21002, Entry = 6, MapId = 0, X = startX + 1800, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content));
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var pathfinder = new BlockEastPathfinder();
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                Assert.False(destinations.Update(session.Player!, 6, 100));
                int afterFirst = pathfinder.Calls;
                Assert.True(afterFirst > 0);
                Assert.False(destinations.Update(session.Player!, 6, 100));
                Assert.Equal(afterFirst, pathfinder.Calls);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FarFailure_RetainsItsSpawnThroughAdvisoryBackoff_ThenTriesTheNextSpawn()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "far objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 22001, Entry = 6, MapId = 0, X = startX + 1600, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 22002, Entry = 6, MapId = 0, X = startX + 1800, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content));
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            PlayerbotWorldDestinations? destinations = null;
            BlockEastPathfinder pathfinder = await host.OnWorldAsync(() =>
            {
                var pathfinder = new BlockEastPathfinder();
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                Assert.False(destinations.Update(session.Player!, 6, 100));
                Assert.Contains(pathfinder.Ends, end => MathF.Abs(end.X - (startX + 1600)) < 1f);
                return pathfinder;
            });
            await host.World.AdvanceClockAsync(2_100);
            await host.OnWorldAsync(() =>
            {
                destinations!.HasQuestCandidate(session.Player!); // the brain's advisory call before Update
                Assert.False(destinations.Update(session.Player!, 6, 100));
                Assert.Contains(pathfinder.Ends, end => MathF.Abs(end.X - (startX + 1800)) < 1f);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ObjectiveChange_ReplansTowardRealSpawnBeyondVisibilityUsingNormalMovement()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "east", DisplayIds = [49] },
             new CreatureTemplate { Entry = 7, Name = "west", DisplayIds = [49] }],
            [.. Enumerable.Range(1, 4100).Select(i => new CreatureSpawn { Guid = (uint)i, Entry = 99, MapId = 0, X = 0, Y = 0, Z = 83.53f }),
             new CreatureSpawn { Guid = 10001, Entry = 6, MapId = 0, X = -8648.95f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 10002, Entry = 7, MapId = 0, X = -9248.95f, Y = -132.49f, Z = 83.53f }], [], [], []);
        // Manual world clock: the motion catches its route up to world.NowMs on every think, so on the wall clock the
        // time between reading `east` and the re-routing think would move the bot (a load-dependent flake).
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content));
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                var player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                float start = player.X;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.Equal(start, player.X);
                Assert.Equal(6u, destinations.TargetEntry);
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.True(player.X > start);
                float east = player.X;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 7, 500));
                // Already moving: changing the objective turns the bot at once (a heartbeat with the new
                // orientation where it is), and it then runs toward the new objective.
                Assert.Equal(east, player.X, 2);
                Assert.True(MathF.Abs(player.Orientation - MathF.PI) < 0.01f);
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 7, 500));
                Assert.True(player.X < east);
                Assert.Equal(7u, destinations.TargetEntry);
                Assert.DoesNotContain(player.VisibleObjects, guid => player.Map!.FindObject(guid) is Creature c && c.Entry is 6 or 7);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Cluster 3 of the bot QA stalls (#368: 5 085 "stalled 120s: goal=Grind target=80" at one spot of the Echo Ridge Mine): every leg the
    /// mesh gives toward the east spawn is a yard's step sideways, so the bot keeps moving (the 5-second stuck clock never runs out) and
    /// keeps planning, but never comes nearer. Within <see cref="PlayerbotWorldDestinations.SpawnNotClosingMs"/> and a little more the walk
    /// gives that spawn up and the hunt plans toward the other (west) spawn.
    /// </summary>
    [Fact]
    public async Task LegsThatNeverCloseOnTheSpawn_GiveItUp_AndTheHuntTurnsToAnotherSpawn()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 23001, Entry = 6, MapId = 0, X = startX + 200, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 23002, Entry = 6, MapId = 0, X = startX - 260, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content));
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var pathfinder = new SidestepEastPathfinder();
            var destinations = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                return new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
            });
            const uint think = 500;
            uint westAtMs = 0;
            for (uint elapsed = 0; elapsed < 40_000 && westAtMs == 0; elapsed += think)
            {
                await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    destinations.Update(player, 6, think);
                    PlayerbotMotion.ElapseForTests(player, think);
                    return true;
                });
                if (pathfinder.WestQueries > 0) westAtMs = elapsed + think;
                await host.World.AdvanceClockAsync(think);
            }

            float fromEast = await host.OnWorldAsync(() => MathF.Abs(session.Player!.X - startX));
            Assert.True(pathfinder.EastQueries > 4, $"the east spawn was planned only {pathfinder.EastQueries} times: the sidestep legs were never walked");
            Assert.True(fromEast < 10f, $"the bot drifted {fromEast:F1} yd along x, so it did close on a spawn");
            Assert.True(westAtMs != 0, $"after 40 s of legs that never came nearer the east spawn ({pathfinder.EastQueries} east plans) the hunt never turned west");
            Assert.True(westAtMs >= PlayerbotWorldDestinations.SpawnNotClosingMs, $"turned west after {westAtMs} ms, before the not-closing clock ran out");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Every query east of the bot answers a complete one-yard leg sideways (alternating north and south): a route the bot walks, which
    /// never brings it nearer the east. Queries west are answered straight.
    /// </summary>
    private sealed class SidestepEastPathfinder : IPathfinder
    {
        private bool _north;
        public bool Enabled => true;
        public int EastQueries { get; private set; }
        public int WestQueries { get; private set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (end.X > start.X + 20f)
            {
                EastQueries++;
                _north = !_north;
                return PathResult.StraightLine(start, start + new Vector3(0, _north ? 1f : -1f, 0), PathType.Normal);
            }

            if (end.X < start.X - 20f) WestQueries++;
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class DestinationContent(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private static LootContent LootWith(uint itemId) => new(
        [(LootTableKind.Creature, new LootStoreRow(3127, itemId, -40f, 0, 1, 1))], []);

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    /// <summary>
    /// A think asks the grind planner, the hunt of an entry and the quest-giver window in turn. Each identity keeps its own cached list
    /// (up to three), asking for the window is advisory, and the spawns a refused plan blocked stay blocked across the switches (guids
    /// are global). Before, every switch rebuilt the one cached list, cleared the route and forgot the blocked spawns, so the bot
    /// searched the same refused spawn again.
    /// </summary>
    [Fact]
    public async Task AlternatingGiversAndHunt_KeepsEachCachedList_AndTheBlockedSpawnsBlocked()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 30001, Entry = 6, MapId = 0, X = startX + 300, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 30002, Entry = 6, MapId = 0, X = startX - 400, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content)));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var pathfinder = new BlockEastPathfinder();
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                var player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(destinations.Update(player, 6, 500)); // the nearer east spawn is refused and blocked
                int eastSearches = pathfinder.Ends.Count(end => end.X > player.X + 100);
                Assert.True(eastSearches > 0);

                for (int think = 0; think < 10; think++)
                {
                    destinations.HasQuestCandidate(player);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    destinations.Update(player, 0, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(destinations.Update(player, 6, 500));
                    Assert.Equal(6u, destinations.TargetEntry);
                }

                Assert.True(destinations.EntryListRebuilds <= 3, $"{destinations.EntryListRebuilds} rebuilds");
                Assert.True(eastSearches == pathfinder.Ends.Count(end => end.X > player.X + 100), string.Join(" ; ", pathfinder.Ends.Select(e => $"{e.X:F1}")) + $" player {player.X:F1}"); // the refused east spawn was never searched again
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A think that has already spent its budget when the destination loop reaches a costly step (here: another path search of the same
    /// think took 30 ms) reaches no verdict on the spawn: no refused plan, no spawn block, no back-off. The next think tries it.
    /// </summary>
    [Fact]
    public async Task SpentStepBudget_DefersThePlan_WithoutBlockingTheSpawn_AndTheNextThinkTriesIt()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 31001, Entry = 6, MapId = 0, X = startX + 300, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content)));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var clock = new PlayerbotThinkProbeTests.ManualClock();
                var pathfinder = new BlockEastPathfinder { OnFirstCall = () => clock.Advance(30) };
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                var player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                var probe = new PlayerbotThinkProbe(clock);

                probe.Begin();
                try
                {
                    // Earlier work of the same think: one path search whose first call takes 30 ms.
                    PlayerbotThinkProbe.Find(player.Map!.Collision, new(player.X, player.Y, player.Z), new(player.X - 50, player.Y, player.Z), new PathOptions());
                    Assert.Equal(1, pathfinder.Calls);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(destinations.Update(player, 6, 500));
                    Assert.False(destinations.LastPlanRefused);
                    Assert.True(destinations.IsPlanning);
                    Assert.Equal(1, pathfinder.Calls); // no search was started
                }
                finally { probe.End(); }

                probe.Begin();
                try
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(destinations.Update(player, 6, 500));
                    Assert.True(destinations.LastPlanRefused); // now it is searched, and the east spawn is refused
                    Assert.True(pathfinder.Calls > 1);
                    Assert.Contains(pathfinder.Ends, end => end.X > player.X);
                }
                finally { probe.End(); }
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class BlockEastPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public int Calls { get; private set; }
        public List<Vector3> Ends { get; } = [];

        /// <summary>Run on the first call (a test advances its think's clock there).</summary>
        public Action? OnFirstCall { get; init; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (Calls == 0) OnFirstCall?.Invoke();
            Calls++;
            Ends.Add(end);
            return end.X > start.X ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    [Fact]
    public void StaticSpawnDestinationRejectsNonFiniteCoordinates()
    {
        CreatureSpawn valid = new() { Guid = 1, Entry = 6, MapId = 0, X = 1, Y = 2, Z = 3 };
        CreatureSpawn invalid = valid with { Z = float.NaN };
        Assert.True(PlayerbotWorldDestinations.IsFiniteDestination(valid));
        Assert.False(PlayerbotWorldDestinations.IsFiniteDestination(invalid));
    }

    [Fact]
    public void PreferredObjectiveEntryUsesLoadedImmutableSpawnContent()
    {
        CreatureSpawn spawn = new() { Guid = 77, Entry = 6, MapId = 1, X = 10, Y = 20, Z = 30 };
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective" }], [spawn], [], [], []);
        Assert.True(PlayerbotWorldDestinations.IsDestinationEntry(content, QuestStore.Empty, spawn, 6));
        Assert.False(PlayerbotWorldDestinations.IsDestinationEntry(content, QuestStore.Empty, spawn, 7));
        Assert.Equal((uint)77, content.GetSpawns(1).Single().Guid);
    }

    [Fact]
    public void NonPreferredEntryRequiresNpcFlagAndQuestRelation()
    {
        CreatureSpawn spawn = new() { Guid = 88, Entry = 42, MapId = 0, X = 1, Y = 2, Z = 3 };
        var template = new CreatureTemplate { Entry = 42, Name = "giver", NpcFlags = 2 };
        var quest = new QuestTemplate { Entry = 9 };
        var content = new CreatureContent([template], [spawn], [], [], []);
        var quests = new QuestStore(new QuestContent([quest], [new CreatureQuestRelation { Id = 42, Quest = 9 }], []));
        Assert.True(PlayerbotWorldDestinations.IsDestinationEntry(content, quests, spawn, 0));
        Assert.Equal(new uint[] { 42 }, quests.CreatureStartersOf(9));
        Assert.Empty(quests.CreatureEndersOf(9));
        Assert.Equal(spawn, Assert.Single(content.GetSpawns(0, 42)));
        Assert.Empty(content.GetSpawns(1, 42));
    }

    /// <summary>
    /// Regression for #790: when a quest giver / ender trip takes sideways steps that never close 5 yards on the spawn within
    /// <see cref="PlayerbotWorldDestinations.SpawnNotClosingMs"/>, <c>NotClosing</c> abandons the non-closing route, suspends the quest,
    /// and clears <c>_tripWatch</c> before the 180s <c>bot-destination/not-reached</c> anomaly window.
    /// </summary>
    [Fact]
    public async Task QuestGiverTrip_ThatNeverClosesOnSpawn_AbandonsRouteWithinNotClosingWindow_AndSuspendsQuest()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 42, Name = "giver", NpcFlags = 2, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 24001, Entry = 42, MapId = 0, X = startX + 200f, Y = -132.49f, Z = 83.53f }], [], [], []);
        var quests = new QuestContent(
            [new QuestTemplate { Entry = 9, Method = 2, MinLevel = 1, QuestLevel = 1 }],
            [new CreatureQuestRelation { Id = 42, Quest = 9 }], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new DestinationContent(content));
            services.AddSingleton<IQuestContentStore>(new QuestDestinationContent(quests));
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var pathfinder = new SidestepEastPathfinder();
            var suspensions = new PlayerbotSuspensions();
            var destinations = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                return new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true }) { Suspensions = suspensions };
            });
            const uint think = 500;
            bool stopped = false;
            for (uint elapsed = 0; elapsed < 25_000 && !stopped; elapsed += think)
            {
                stopped = await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    bool active = destinations.Update(player, 0, think);
                    PlayerbotMotion.ElapseForTests(player, think);
                    return !active;
                });
                await host.World.AdvanceClockAsync(think);
            }

            Assert.True(stopped, "the non-closing quest giver trip never gave up within 25s");
            bool suspended = await host.OnWorldAsync(() => suspensions.IsQuestSuspended(9, host.World.NowMs));
            Assert.True(suspended, "the non-closing quest was not suspended when NotClosing triggered");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Regression for #775: when many quest entries are in disconnected region-graph components, <see cref="PlayerbotWorldDestinations"/> caps
    /// unreachable entry skips at <see cref="PlayerbotWorldDestinations.MaxUnreachableSkipsPerThink"/> per think and arms its backoff instead of
    /// resetting <c>attempted</c> to false on every skip.
    /// </summary>
    [Fact]
    public async Task ManyUnreachableQuestEntries_AreCappedPerThink_AndArmBackoff()
    {
        using var navFixture = new ArcaneCore.TestSupport.NavMeshFixture();
        (int X, int Y, ArcaneCore.TestSupport.CellTile Tile)[] unjoined =
        [
            (10, 10, new ArcaneCore.TestSupport.CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
            (10, 11, new ArcaneCore.TestSupport.CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
            (11, 10, new ArcaneCore.TestSupport.CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
            (12, 10, new ArcaneCore.TestSupport.CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
            (13, 10, new ArcaneCore.TestSupport.CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
        ];
        var templates = Enumerable.Range(0, 40)
            .Select(i => new CreatureTemplate { Entry = (uint)(500 + i), Name = $"giver-{i}", NpcFlags = 2, DisplayIds = [49] })
            .ToArray();
        var spawns = Enumerable.Range(0, 40)
            .Select(i => new CreatureSpawn { Guid = (uint)(25000 + i), Entry = (uint)(500 + i), MapId = 0, X = 75f, Y = 5f, Z = 0f })
            .ToArray();
        var questTemplates = Enumerable.Range(0, 40)
            .Select(i => new QuestTemplate { Entry = (uint)(900 + i), Method = 2, MinLevel = 1, QuestLevel = 1 })
            .ToArray();
        var starters = Enumerable.Range(0, 40)
            .Select(i => new CreatureQuestRelation { Id = (uint)(500 + i), Quest = (uint)(900 + i) })
            .ToArray();
        var content = new CreatureContent(templates, spawns, [], [], []);
        var quests = new QuestContent(questTemplates, starters, []);
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(navFixture, unjoined, new FloorLineOfSight(),
            configureServices: services =>
            {
                services.AddSingleton<ICreatureDataStore>(new DestinationContent(content));
                services.AddSingleton<IQuestContentStore>(new QuestDestinationContent(quests));
            });
        await world.PlaceAsync(new Vector3(5, 130, 0));
        await world.Host.WaitForWorldAsync(() => world.Player.VisibleObjects.Count == 0, "offscreen unreachable givers");
        for (int think = 0; think < 80; think++)
        {
            if (await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsUnreachableOnFoot(world.Player, new Vector3(75, 5, 0), out _, out _)))
                break;
            await world.Host.World.AdvanceClockAsync(50);
        }

        await world.Host.OnWorldAsync(() =>
        {
            var suspensions = new PlayerbotSuspensions();
            var destinations = new PlayerbotWorldDestinations(world.Session, new PlayerbotOptions { Enabled = true })
            {
                Suspensions = suspensions,
                Untakeable = (_, _) => true,
            };
            Assert.False(destinations.Update(world.Player, 0, 500));
            int suspendedAfterFirstThink = Enumerable.Range(0, 40)
                .Count(i => suspensions.IsEntrySuspended((uint)(500 + i), world.Host.World.NowMs));
            Assert.Equal(PlayerbotWorldDestinations.MaxUnreachableSkipsPerThink, suspendedAfterFirstThink);

            // Immediate second Update at the same timestamp hits the backoff armed by the unreachable skips.
            Assert.False(destinations.Update(world.Player, 0, 500));
            int suspendedAfterSecondCall = Enumerable.Range(0, 40)
                .Count(i => suspensions.IsEntrySuspended((uint)(500 + i), world.Host.World.NowMs));
            Assert.Equal(PlayerbotWorldDestinations.MaxUnreachableSkipsPerThink, suspendedAfterSecondCall);
            return true;
        });
    }

    private sealed class QuestDestinationContent(QuestContent content) : IQuestContentStore
    {
        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
