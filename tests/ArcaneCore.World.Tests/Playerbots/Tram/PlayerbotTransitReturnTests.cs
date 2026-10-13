using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Playerbots.Elevator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The failed-return diagnosis (<c>PlayerbotWorldDestinations.ConfirmUnclimbable</c>) sets a completed quest aside for a bot that may not
/// take the tram or a lift yet (no home, below <c>TramMinLevel</c>) only when the tram or a lift really is the way to the ender. Every
/// plan here is refused by the mesh, the way a bot that may not ride meets it. CI regression: the diagnosis set aside every plain failed
/// return of a level 1 bot, because it asked only whether the bot was too low.
/// </summary>
public sealed class PlayerbotTransitReturnTests
{
    private const uint QuestId = 900, EnderEntry = 6;

    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f);
    private static readonly Vector3 NextToTheTramBot = TramTestData.StormwindExitLanding + new Vector3(30f, 0f, 0f);
    private static readonly Vector3 NextToTheLiftBot = ElevatorTestWorld.CanyonStart + new Vector3(-40f, 0f, 0f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100 };

    private static CreatureContent Ender(uint mapId, Vector3 at)
        => new([new CreatureTemplate { Entry = EnderEntry, Name = "ender", NpcFlags = 2, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 30001, Entry = EnderEntry, MapId = mapId, X = at.X, Y = at.Y, Z = at.Z }], [], [], []);

    private static void Configure(IServiceCollection services, CreatureContent content)
    {
        services.AddSingleton<ICreatureDataStore>(new CreatureStore(content));
        services.AddSingleton<IQuestContentStore>(new QuestStoreOf());
        services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
    }

    private static void Complete(WorldSession session, Player player)
    {
        PlayerQuestLog log = session.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!.Quests;
        QuestStatusData row = log.GetOrAdd(QuestId);
        log.SetSlot(log.FindSlot(0), QuestId);
        row.Status = QuestStatus.Complete;
    }

    /// <summary>Thinks towards the ender with the clock moving until the quest is set aside; false after 80 thinks without.</summary>
    private static async Task<bool> SetAsideAsync(Func<Task> think, Func<Task<bool>> suspended, Func<uint, Task> advance)
    {
        for (int i = 0; i < 80; i++)
        {
            await think();
            if (await suspended()) return true;
            await advance(1000);
        }

        return false;
    }

    // ---- the tram ----

    private static async Task<(TramTestWorld World, PlayerbotWorldDestinations Destinations, PlayerbotSuspensions Suspensions)> TramAsync(
        byte level, PlayerbotHomeArea? home, Vector3 ender)
    {
        TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new Refusing(),
            configure: services => Configure(services, Ender(0, ender)));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        await world.OnWorldAsync(() =>
        {
            world.Player.Level = level;
            Complete(world.Session, world.Player);
        });
        var suspensions = new PlayerbotSuspensions();
        return (world, new PlayerbotWorldDestinations(world.Session, Options()) { TramZoneAt = TramTestData.ZoneAt, Home = home, Suspensions = suspensions }, suspensions);
    }

    private static Task<bool> TramSetAsideAsync(TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions)
        => SetAsideAsync(() => world.TickAsync(() => destinations.Update(world.Player, 0, 100, QuestId)),
            () => world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)),
            world.AdvanceAsync);

    [Fact]
    public async Task ALevelOneBot_WhoseEnderLiesAcrossTheTram_HasTheReturnSetAside()
    {
        (TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await TramAsync(1, null, IronforgeGoal);
        await using (world)
        {
            Assert.True(await TramSetAsideAsync(world, destinations, suspensions));
            Assert.Null(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)));
        }
    }

    [Fact]
    public async Task ABotAtTheTramLevel_WhoseEnderLiesAcrossTheTram_RidesInsteadOfBeingSetAside()
    {
        (TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await TramAsync(10, null, IronforgeGoal);
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 0, 100, QuestId)));
            Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
            Assert.False(await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)));
        }
    }

    [Fact]
    public async Task AHomedLevelOneBot_WhoseEnderLiesAcrossTheTram_RidesInsteadOfBeingSetAside()
    {
        var home = new PlayerbotHomeArea(0, TramTestData.StormwindExitLanding, 5000f, "start:northshire");
        (TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await TramAsync(1, home, IronforgeGoal);
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 0, 100, QuestId)));
            Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
            Assert.False(await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)));
        }
    }

    /// <summary>The regression pin: a plain failed return (no tram or lift between) of a homeless level 1 bot is not a transit block.</summary>
    [Fact]
    public async Task ALevelOneBot_WhoseEnderIsPlainlyUnreachable_WithNoTramOrLiftBetween_IsNeverSetAside()
    {
        (TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await TramAsync(1, null, NextToTheTramBot);
        await using (world)
            Assert.False(await TramSetAsideAsync(world, destinations, suspensions));
    }

    [Fact]
    public async Task TramIsWayTo_IsTrueAcrossTheTram_AndFalseNextToTheBot_OrOnTheTramMap()
    {
        (TramTestWorld world, _, _) = await TramAsync(1, null, IronforgeGoal);
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() => PlayerbotTram.IsWayTo(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
            Assert.False(await world.OnWorldAsync(() => PlayerbotTram.IsWayTo(world.Session, world.Player, NextToTheTramBot, TramTestData.ZoneAt)));
            await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform);
            Assert.False(await world.OnWorldAsync(() => PlayerbotTram.IsWayTo(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        }
    }

    // ---- the lift ----

    private static async Task<(ElevatorTestWorld World, PlayerbotWorldDestinations Destinations, PlayerbotSuspensions Suspensions)> LiftAsync(
        byte level, PlayerbotHomeArea? home, Vector3 ender)
    {
        ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(configure: services => Configure(services, Ender(ElevatorTestData.Kalimdor, ender)));
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await world.OnWorldAsync(() =>
        {
            world.Player.Level = level;
            Complete(world.Session, world.Player);
        });
        var suspensions = new PlayerbotSuspensions();
        return (world, new PlayerbotWorldDestinations(world.Session, Options()) { Home = home, Suspensions = suspensions }, suspensions);
    }

    private static Task<bool> LiftSetAsideAsync(ElevatorTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions)
        => SetAsideAsync(() => world.TickAsync(() => destinations.Update(world.Player, 0, 100, QuestId)),
            () => world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)),
            world.AdvanceAsync);

    [Fact]
    public async Task ALevelOneBot_WhoseEnderLiesOnTheOtherLevelOfALift_HasTheReturnSetAside()
    {
        (ElevatorTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await LiftAsync(1, null, ElevatorTestWorld.Ratchet);
        await using (world)
        {
            Assert.True(await LiftSetAsideAsync(world, destinations, suspensions));
            Assert.Null(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));
        }
    }

    [Fact]
    public async Task ABotAtTheTramLevel_OrWithAHome_RidesTheLiftInsteadOfBeingSetAside()
    {
        var home = new PlayerbotHomeArea(ElevatorTestData.Kalimdor, ElevatorTestWorld.CanyonStart, 9000f, "start:durotar");
        foreach ((byte level, PlayerbotHomeArea? area) in new (byte, PlayerbotHomeArea?)[] { (10, null), (1, home) })
        {
            (ElevatorTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await LiftAsync(level, area, ElevatorTestWorld.Ratchet);
            await using (world)
            {
                Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 0, 100, QuestId)));
                Assert.NotNull(await world.OnWorldAsync(() => PlayerbotElevator.TripOf(world.Player)));
                Assert.False(await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)));
            }
        }
    }

    [Fact]
    public async Task ALevelOneBot_WhoseEnderIsOnTheSameLevelAsALift_IsNeverSetAside()
    {
        (ElevatorTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await LiftAsync(1, null, NextToTheLiftBot);
        await using (world)
            Assert.False(await LiftSetAsideAsync(world, destinations, suspensions));
    }

    [Fact]
    public async Task ElevatorIsWayTo_IsTrueAcrossTheCliff_AndFalseOnTheBotsSide()
    {
        (ElevatorTestWorld world, _, _) = await LiftAsync(1, null, ElevatorTestWorld.Ratchet);
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() => PlayerbotElevator.IsWayTo(world.Session, world.Player, ElevatorTestWorld.Ratchet)));
            Assert.False(await world.OnWorldAsync(() => PlayerbotElevator.IsWayTo(world.Session, world.Player, NextToTheLiftBot)));
        }
    }

    // ---- fixtures ----

    /// <summary>Every plan fails and ends at the bot's feet.</summary>
    private sealed class Refusing : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Incomplete, [start, start]);
    }

    private sealed class CreatureStore(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    /// <summary>One quest, turned in to the ender creature.</summary>
    private sealed class QuestStoreOf : IQuestContentStore
    {
        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new QuestContent(
                [new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Across" }],
                [], [new CreatureQuestRelation { Id = EnderEntry, Quest = QuestId }]));
    }
}
