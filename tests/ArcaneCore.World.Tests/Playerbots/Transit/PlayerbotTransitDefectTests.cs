using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Playerbots.Elevator;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Transit;

/// <summary>
/// The tram and lift legs of the transit-defect work: a walk that the navigation keeps refusing fails after three refusals and backs its
/// point off, a teleport trigger that does not take the bot fails the tram trip at once, a failed leg defers only that leg (the brain keeps
/// the quest, a backed-off tram makes the destination wait instead of judging the walk refused), and a completed quest across the tram is
/// held for the bot's level rather than abandoned.
/// </summary>
public sealed class PlayerbotTransitDefectTests
{
    private const uint QuestId = 900, EnderEntry = 6;

    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };

    private static string TramKey(string code) => PlayerbotAnomalies.Key("tram", code);

    private static string LiftKey(string code) => PlayerbotAnomalies.Key("elevator", code);

    private sealed class NoWay : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    /// <summary>Every plan fails and ends at the bot's feet.</summary>
    private sealed class Refusing : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Incomplete, [start, start]);
    }

    private sealed class GatedMapStore(AreaTriggerTeleport[] teleports) : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent(TramTestData.Maps, [], TramTestData.Triggers, teleports, []));
    }

    /// <summary>The tram fixture's teleports with the one of <paramref name="trigger"/> asking for <paramref name="level"/>.</summary>
    private static AreaTriggerTeleport[] Gated(uint trigger, byte level)
        => [.. TramTestData.Teleports.Select(t => t.Id == trigger ? t with { RequiredLevel = level } : t)];

    private static Task<TramTestWorld> GatedWorldAsync(uint trigger) => TramTestWorld.StartAsync(
        configure: services => services.AddSingleton<IMapDataStore>(new GatedMapStore(Gated(trigger, 60))));

    // ---- 3: a refused walk fails after three refusals ----

    [Fact]
    public async Task TramWalk_ThreeRefusedPlans_FailTheTripLongBeforeTheProgressBound_AndBackTheEntranceOff()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new NoWay());
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        uint began = await world.OnWorldAsync(() => world.Host.World.NowMs);
        TramStep step = TramStep.Traveling;
        string? failure = null;
        for (int i = 0; i < 400 && step == TramStep.Traveling; i++)
            (step, failure) = await world.TickAsync(() => (PlayerbotTram.Drive(world.Session, world.Player, options, 100, out string? why), why));
        uint took = await world.OnWorldAsync(() => world.Host.World.NowMs) - began;

        Assert.Equal(TramStep.Failed, step);
        Assert.InRange(took, 3_900u, 15_000u);
        Assert.Contains("refused 3 times", failure);
        await world.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            Assert.Equal("no-way-to-entrance", PlayerbotTram.TripOf(world.Player)!.FailureCategory);
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Tram, 2173, now));
            Assert.False(PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Tram, 2175, now)); // the other platform is not affected
            Assert.False(PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Tram, 2173, now + 70_000u));
            Assert.False(PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt));
        });
        Assert.Equal(1, capture.Count(TramKey("trip-failed")));
    }

    [Fact]
    public async Task LiftWalk_ThreeRefusedPlans_FailTheRideLongBeforeTheProgressBound_AndBackTheLiftOff()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new ElevatorTestWorld.Floors(null), pathfinder: new NoWay()));
        ElevatorLift lift = await world.GreatLiftAsync();
        PlayerbotOptions options = Options();
        await world.OnWorldAsync(() => PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.High, ElevatorTestWorld.CanyonStart));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        uint began = await world.OnWorldAsync(() => world.Host.World.NowMs);
        ElevatorStep step = ElevatorStep.Traveling;
        string? failure = null;
        for (int i = 0; i < 400 && step == ElevatorStep.Traveling; i++)
            (step, failure) = await world.TickAsync(() => (PlayerbotElevator.Drive(world.Session, world.Player, options, 100, out string? why), why));
        uint took = await world.OnWorldAsync(() => world.Host.World.NowMs) - began;

        Assert.Equal(ElevatorStep.Failed, step);
        Assert.InRange(took, 3_900u, 15_000u);
        Assert.Contains("refused 3 times", failure);
        await world.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Lift, lift.Key, now));
            Assert.True(PlayerbotElevator.IsHeldOff(world.Session, world.Player, ElevatorTestWorld.Ratchet));
            Assert.False(PlayerbotElevator.TryBegin(world.Session, world.Player, ElevatorTestWorld.Ratchet, options));
        });
        Assert.Equal(1, capture.Count(LiftKey("trip-failed")));
    }

    // ---- 5: a teleport that does not take the bot ----

    [Fact]
    public async Task ATramEntrance_ThatDoesNotTakeTheBot_FailsTheTripAtOnce_AndBacksTheEntranceOff()
    {
        await using TramTestWorld world = await GatedWorldAsync(2173);
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        PlayerbotOptions options = Options();
        uint began = await world.OnWorldAsync(() => world.Host.World.NowMs);
        TramStep step = TramStep.Traveling;
        string? failure = null;
        for (int i = 0; i < 400 && step == TramStep.Traveling; i++)
            (step, failure) = await world.TickAsync(() => (PlayerbotTram.Drive(world.Session, world.Player, options, 100, out string? why), why));
        uint took = await world.OnWorldAsync(() => world.Host.World.NowMs) - began;

        Assert.Equal(TramStep.Failed, step);
        Assert.True(took < 20_000, $"failed after {took} ms");
        Assert.Contains("did not take the bot", failure);
        await world.OnWorldAsync(() =>
        {
            Assert.Equal(0u, world.Player.MapId);
            Assert.Equal("teleport-refused", PlayerbotTram.TripOf(world.Player)!.FailureCategory);
            Assert.True(PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Tram, 2173, world.Host.World.NowMs));
            Assert.False(PlayerbotAreaTriggers.TryConsumeTeleportRefusal(world.Player, out _, out _)); // the trip took it
        });
        Assert.Equal(1, capture.Count(TramKey("trip-failed")));
    }

    [Fact]
    public async Task ATramEntranceBlockedByPolicy_FailsTheTripAtOnce()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        await world.OnWorldAsync(() => PlayerbotAreaTriggers.AllowTeleportsTo(world.Player, null)); // the consent goes
        PlayerbotOptions options = Options();
        TramStep step = TramStep.Traveling;
        string? failure = null;
        for (int i = 0; i < 400 && step == TramStep.Traveling; i++)
            (step, failure) = await world.TickAsync(() => (PlayerbotTram.Drive(world.Session, world.Player, options, 100, out string? why), why));

        Assert.Equal(TramStep.Failed, step);
        Assert.Contains("blocked by policy", failure);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTransitBackoff.IsBackedOff(world.Player, TransitKind.Tram, 2173, world.Host.World.NowMs)));
    }

    [Fact]
    public async Task ATramExit_ThatDoesNotTakeTheBot_SendsItOutByTheServersTeleport_AtOnce()
    {
        await using TramTestWorld world = await GatedWorldAsync(2171);
        await world.PlaceAsync(TramTestData.Tram, TramTestData.StormwindPlatform with { X = 40f });
        PlayerbotOptions options = Options();
        uint began = await world.OnWorldAsync(() => world.Host.World.NowMs);
        TramStep step = TramStep.Traveling;
        string? failure = null;
        for (int i = 0; i < 400 && step == TramStep.Traveling; i++)
            (step, failure) = await world.TickAsync(() => (PlayerbotTram.Drive(world.Session, world.Player, options, 100, out string? why), why));
        uint took = await world.OnWorldAsync(() => world.Host.World.NowMs) - began;

        Assert.Equal(TramStep.Failed, step);
        Assert.True(took < 30_000, $"left after {took} ms");
        Assert.Contains("exit did not take the bot", failure);
        Assert.Equal(0u, await world.OnWorldAsync(() => world.Player.MapId));
    }

    [Fact]
    public async Task TheTeleportRefusal_IsRecordedForPolicyAndForATriggerThatDidNotTeleport_AndConsumedOnce()
    {
        await using TramTestWorld policy = await TramTestWorld.StartAsync();
        await policy.PlaceAsync(0, new Vector3(-8346.46f, 514.031f, 96.5989f));
        await policy.OnWorldAsync(() =>
        {
            PlayerbotAreaTriggers.Begin(policy.Session, policy.Player, policy.Player.Map!, new Vector3(-8300f, 514f, 96f), Options());
            Assert.Empty(PlayerbotAreaTriggers.Update(policy.Session, policy.Player, policy.Player.Map!, new Vector3(-8346.46f, 514.031f, 96.5989f)));
            Assert.True(PlayerbotAreaTriggers.TryConsumeTeleportRefusal(policy.Player, out uint trigger, out string why));
            Assert.Equal(2173u, trigger);
            Assert.Contains("blocked by policy", why);
            Assert.False(PlayerbotAreaTriggers.TryConsumeTeleportRefusal(policy.Player, out _, out _));
        });

        await using TramTestWorld gated = await GatedWorldAsync(2173);
        await gated.PlaceAsync(0, new Vector3(-8346.46f, 514.031f, 96.5989f));
        await gated.OnWorldAsync(() =>
        {
            PlayerbotAreaTriggers.AllowTeleportsTo(gated.Player, TramTestData.Tram);
            PlayerbotAreaTriggers.Begin(gated.Session, gated.Player, gated.Player.Map!, new Vector3(-8300f, 514f, 96f), Options());
            Assert.Equal([2173u], PlayerbotAreaTriggers.Update(gated.Session, gated.Player, gated.Player.Map!, new Vector3(-8346.46f, 514.031f, 96.5989f)));
            Assert.True(PlayerbotAreaTriggers.TryConsumeTeleportRefusal(gated.Player, out PlayerbotAreaTriggers.TeleportRefusal refusal));
            Assert.Equal(2173u, refusal.Trigger);
            Assert.Contains("did not teleport", refusal.Why);
            Assert.Equal(gated.Host.World.NowMs, refusal.AtMs);
            Assert.False(PlayerbotAreaTriggers.TryConsumeTeleportRefusal(gated.Player, out PlayerbotAreaTriggers.TeleportRefusal _));
        });
    }

    // ---- 6: a failed leg defers only that leg ----

    private static void AssertQuestKept(PlayerbotBrain brain, uint now, Player player)
    {
        Assert.Equal(0, brain.StallCount);
        Assert.Null(brain.StallReport);
        Assert.False(brain.Suspensions.IsQuestSuspended(QuestId, now));
        Assert.False(brain.Suspensions.IsEntrySuspended(EnderEntry, now));
        Assert.Equal(0, brain.Suspensions.QuestSetAsideCount(QuestId));
        Assert.Equal(0, brain.StallWatch.PinStallCount(player.MapId, player.X, player.Y));
        Assert.Equal(1, brain.TransitFailureCount);
    }

    [Fact]
    public async Task AFailedTramTrip_GivesUpTheLegOnly_TheQuestIsNotSetAside_AndNoPinStallIsRecorded()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new NoWay());
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        var brain = new PlayerbotBrain(world.Session, Options());
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        await world.OnWorldAsync(() => brain.HoldGoalForTests(PlayerbotGoalKind.Quest, EnderEntry, QuestId));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        for (int i = 0; i < 400 && await world.OnWorldAsync(() => brain.TransitFailureCount) == 0; i++)
            await world.TickAsync(() => { brain.Update(100); return 0; });

        await world.OnWorldAsync(() =>
        {
            Assert.StartsWith("tram:", brain.LastTransitFailure);
            AssertQuestKept(brain, world.Host.World.NowMs, world.Player);
            Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
        });
        Assert.Equal(1, capture.Count(TramKey("trip-failed"))); // the driver's own anomaly stays, once
    }

    [Fact]
    public async Task AFailedLiftRide_GivesUpTheLegOnly_TheQuestIsNotSetAside_AndNoPinStallIsRecorded()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        await world.OnWorldAsync(() => WorldCollision.Of(world.Host.World).Install(lineOfSight: new ElevatorTestWorld.Floors(null), pathfinder: new NoWay()));
        ElevatorLift lift = await world.GreatLiftAsync();
        var brain = new PlayerbotBrain(world.Session, Options());
        await world.OnWorldAsync(() =>
        {
            PlayerbotElevator.Begin(world.Session, world.Player, lift, ElevatorLevel.High, ElevatorTestWorld.CanyonStart);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, EnderEntry, QuestId);
        });
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        for (int i = 0; i < 400 && await world.OnWorldAsync(() => brain.TransitFailureCount) == 0; i++)
            await world.TickAsync(() => { brain.Update(100); return 0; });

        await world.OnWorldAsync(() =>
        {
            Assert.StartsWith("elevator:", brain.LastTransitFailure);
            AssertQuestKept(brain, world.Host.World.NowMs, world.Player);
        });
        Assert.Equal(1, capture.Count(LiftKey("trip-failed")));
    }

    // ---- 6 and 7: the destination and a completed quest across the tram ----

    private static CreatureContent Ender(uint mapId, Vector3 at)
        => new([new CreatureTemplate { Entry = EnderEntry, Name = "ender", NpcFlags = 2, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 30001, Entry = EnderEntry, MapId = mapId, X = at.X, Y = at.Y, Z = at.Z }], [], [], []);

    private static async Task<(TramTestWorld World, PlayerbotWorldDestinations Destinations, PlayerbotSuspensions Suspensions)> TramQuestAsync(
        byte level, PlayerbotOptions options)
    {
        TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new Refusing(), configure: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new CreatureStore(Ender(0, IronforgeGoal)));
            services.AddSingleton<IQuestContentStore>(new QuestStoreOf());
            services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
        });
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        await world.OnWorldAsync(() =>
        {
            world.Player.Level = level;
            PlayerQuestLog log = world.Session.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(world.Player)!.Quests;
            QuestStatusData row = log.GetOrAdd(QuestId);
            log.SetSlot(log.FindSlot(0), QuestId);
            row.Status = QuestStatus.Complete;
        });
        var suspensions = new PlayerbotSuspensions();
        return (world, new PlayerbotWorldDestinations(world.Session, options) { TramZoneAt = TramTestData.ZoneAt, Suspensions = suspensions }, suspensions);
    }

    [Fact]
    public async Task ABackedOffTram_MakesTheDestinationWaitForTheBackoff_WithoutASetAside_ThenRides()
    {
        PlayerbotOptions options = Options();
        options.Travel.TransitBackoffSeconds = 10;
        options.Travel.TransitBackoffMaxSeconds = 10;
        (TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await TramQuestAsync(10, options);
        await using (world)
        {
            await world.OnWorldAsync(() => PlayerbotTransitBackoff.Fail(world.Player, TransitKind.Tram, 2173, world.Host.World.NowMs, options));
            for (int i = 0; i < 4; i++)
            {
                Assert.False(await world.OnWorldAsync(() => destinations.Update(world.Player, 0, 100, QuestId)));
                Assert.Null(await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)));
                await world.AdvanceAsync(1_000);
            }

            await world.OnWorldAsync(() =>
            {
                uint now = world.Host.World.NowMs;
                Assert.False(suspensions.IsQuestSuspended(QuestId, now));
                Assert.False(suspensions.IsEntrySuspended(EnderEntry, now));
                Assert.Equal(0, suspensions.QuestSetAsideCount(QuestId));
            });

            // The backoff ended: the destination rides at once (a refused far walk would have blocked the ender for 30 s).
            await world.AdvanceAsync(8_000);
            Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 0, 100, QuestId)));
            Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
        }
    }

    [Fact]
    public async Task ACompletedQuestAcrossTheTram_ForABotBelowTheTramLevel_IsKeptAndNeverAbandoned_AndIsTakenUpAgainAtTheLevel()
    {
        PlayerbotOptions options = Options();
        options.Population.TramMinLevel = 10;
        (TramTestWorld world, PlayerbotWorldDestinations destinations, PlayerbotSuspensions suspensions) = await TramQuestAsync(1, options);
        await using (world)
        {
            for (int cycle = 0; cycle < PlayerbotSuspensions.MaxReturnSetAsides + 2; cycle++)
            {
                bool held = false;
                for (int i = 0; i < 80 && !held; i++)
                {
                    await world.TickAsync(() => destinations.Update(world.Player, 0, 100, QuestId));
                    held = await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs));
                    if (!held) await world.AdvanceAsync(1_000);
                }

                Assert.True(held, $"cycle {cycle}: the quest was not held");
                await world.AdvanceAsync(PlayerbotSuspensions.SuspendMs + 1_000);
            }

            await world.OnWorldAsync(() =>
            {
                PlayerQuestLog log = world.Session.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(world.Player)!.Quests;
                Assert.Equal(QuestStatus.Complete, log.Get(QuestId)?.Status); // not abandoned
                Assert.Equal(0, suspensions.Ledger.Strikes(new DeferKey(DeferKind.UnreachableQuest, QuestId)));
                Assert.Equal(0, suspensions.QuestSetAsideCount(QuestId)); // no growing count from this cause
            });

            // Held again, then the level comes: the next update takes the quest up and rides.
            for (int i = 0; i < 80 && !await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)); i++)
            {
                await world.TickAsync(() => destinations.Update(world.Player, 0, 100, QuestId));
                await world.AdvanceAsync(1_000);
            }

            Assert.True(await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)));
            await world.AdvanceAsync(3_000); // past the destination's short retry
            Assert.True(await world.OnWorldAsync(() =>
            {
                world.Player.Level = 10;
                return destinations.Update(world.Player, 0, 100, QuestId);
            }));
            Assert.False(await world.OnWorldAsync(() => suspensions.IsQuestSuspended(QuestId, world.Host.World.NowMs)));
            Assert.Equal(TramStage.ToEntrance, await world.OnWorldAsync(() => PlayerbotTram.TripOf(world.Player)?.Stage));
        }
    }

    // ---- fixtures ----

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
