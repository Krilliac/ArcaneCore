using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>The trip paths that the tests beside the master do not reach: a quest destination, a walk to the master, and the failures on the way.</summary>
public sealed partial class PlayerbotTaxiTests
{
    [Fact]
    public async Task AQuestDestinationBeyondTheMinimum_IsFlown_ThroughTheWorldDestinations_AndTheBotLandsBesideTheGiver()
    {
        await using Rig rig = await Rig.StartAsync(withQuest: true);
        rig.Host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [TaxiFixture.QuestId];
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            var destinations = new PlayerbotWorldDestinations(rig.Session, options);
            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(destinations.Update(player, 0, 500));
            Assert.True(PlayerbotTaxi.IsActive(player), "the quest destination was walked, not flown");
            Assert.Equal(TaxiFixture.GiverEntry, destinations.TargetEntry);

            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.True(PlayerbotTaxi.IsFlying(player));
            rig.FlyUntilLanded(player);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.True(Math.Abs(player.X - TaxiFixture.GiverX) < 15f);
            return 0;
        });
    }

    [Fact]
    public async Task ABotFarFromTheMaster_WalksThere_AndBoards()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            player.Relocate(player.X - 120f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            return 0;
        });
        await rig.WaitForMasterOutOfSightAsync();
        await rig.OnPlayer((player, state) =>
        {
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            float before = player.X;
            for (int think = 0; think < 3; think++)
            {
                PlayerbotMotion.ElapseForTests(player, 500);
                rig.Session.ManagedBudget = new ManagedActionBudget(4);
                Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            }

            Assert.False(PlayerbotTaxi.IsFlying(player));
            Assert.True(player.X > before, $"no step towards the master from x {before:F1}");

            // Arrived (the rest of the way as it would walk it): the master comes into sight.
            PlayerbotMovementControl.Stop(rig.Session, player);
            player.Relocate(TaxiFixture.StartX, player.Y, player.Z, 0, rig.Session.World.NowMs);
            return 0;
        });
        await rig.Host.WaitForWorldAsync(() => rig.Session.Player!.VisibleObjects.Contains(TaxiFixture.MasterGuid), "the master comes into sight");
        await rig.OnPlayer((player, state) =>
        {
            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, Options(), 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            return 0;
        });
    }

    [Fact]
    public async Task AWalkThatGetsNowhere_FailsAfterTheProgressBound_AndBacksOff()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new NoPathfinder());
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            player.Relocate(player.X - 120f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            return 0;
        });
        await rig.WaitForMasterOutOfSightAsync();
        await rig.OnPlayer((player, state) =>
        {
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            TaxiTrip trip = PlayerbotTaxi.TripOf(player)!;
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));

            // No path is ever found: once the progress bound has passed the trip is given up.
            trip.BestMs = unchecked(rig.Session.World.NowMs - PlayerbotTaxi.NoProgressMs - 1_000);
            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Contains("no way to the flight master", failure);
            Assert.True(PlayerbotTaxi.IsBackedOff(player, rig.Session.World.NowMs));
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            return 0;
        });
    }

    [Fact]
    public async Task AMasterMissingAtTheDepartureSpawn_FailsTheTrip_AndBacksOff()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            player.Map!.RemoveObject(player.Map.FindObject(TaxiFixture.MasterGuid)!);

            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Contains("no flight master", failure);
            Assert.False(PlayerbotTaxi.IsFlying(player));
            Assert.True(PlayerbotTaxi.IsBackedOff(player, rig.Session.World.NowMs));
            return 0;
        });
    }

    [Fact]
    public async Task AStaleRefusalInTheRing_IsNotTakenForTheAnswerToTheNewRequest()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();

            // Another controller's flight request was refused (mounted) and nobody read the reply.
            player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, TaxiFixture.GryphonDisplay);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.True(rig.Session.TryManagedAction(WorldOpcode.CmsgActivatetaxi, PlayerbotTaxi.ActivatePayload(TaxiFixture.MasterGuid, [1u, 2u], 100)));
            player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);

            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            return 0;
        });
    }

    [Fact]
    public async Task TheRouteSearch_IsNotRepeated_WhileTheInputsAreTheSame()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            uint[] first = PlayerbotTaxi.TripOf(player)!.Nodes;
            PlayerbotTaxi.Cancel(player);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Same(first, PlayerbotTaxi.TripOf(player)!.Nodes); // the cached route, not a new search
            return 0;
        });
    }

    [Fact]
    public async Task AControllerTakingTheBotOver_DropsAHalfFinishedTrip_WithoutABackoff()
    {
        TaxiServices.Current.Value = new TaxiFixture();
        await using WorldTestHost host = Party.PartyTestHost.Start();
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Taxihand");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        Player player = await host.PlayerAsync("Taxihand");
        await host.OnWorldAsync(() =>
        {
            QuestNpcFeature quests = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            Rig.Know(quests.Services.StateOf(player)!, 1, 2);
            player.Money = 1000;
            // Far from the master, so the brain is still walking to it when the controller comes.
            player.Relocate(TaxiFixture.StartX - 120f, TaxiFixture.StartY, TaxiFixture.Z, 0, host.World.NowMs);
            PlayerbotOptions options = Party.PartyTestHost.PlayerbotOptionsOf(host);
            options.Taxi.MinRouteYards = 100f;
            Vector3 goal = new(TaxiFixture.StartX + 262f, TaxiFixture.StartY, TaxiFixture.Z);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi((WorldSession)player.Session, player, 0, goal, options));
            Assert.True(PlayerbotTaxi.IsActive(player));
        });

        Assert.True(await feature.SetControllerAsync(id, new IdleController()));
        Assert.False(await host.OnWorldAsync(() => PlayerbotTaxi.IsActive(player)));
        Assert.False(await host.OnWorldAsync(() => PlayerbotTaxi.IsBackedOff(player, host.World.NowMs)));
        Assert.Null(await host.OnWorldAsync(() => PlayerbotTaxi.LastFailure(player)));
    }

    [Fact]
    public async Task ThePartyAiEngaging_DropsAHalfFinishedTrip_WithoutABackoff()
    {
        TaxiServices.Current.Value = new TaxiFixture();
        await using WorldTestHost host = Party.PartyTestHost.Start(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Taxiparty");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        Player player = await host.PlayerAsync("Taxiparty");
        await host.OnWorldAsync(() =>
        {
            Rig.Know(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!, 1, 2);
            player.Money = 1000;
            player.Relocate(TaxiFixture.StartX - 120f, TaxiFixture.StartY, TaxiFixture.Z, 0, host.World.NowMs);
            PlayerbotOptions options = Party.PartyTestHost.PlayerbotOptionsOf(host);
            options.Taxi.MinRouteYards = 100f;
            Vector3 goal = new(TaxiFixture.StartX + 262f, TaxiFixture.StartY, TaxiFixture.Z);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi((WorldSession)player.Session, player, 0, goal, options));
        });

        await using WorldTestClient master = await host.EnterWorldAsync("TAXIMASTER", "Taxipartym");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, Party.PartyTestHost.CString("Taxiparty"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the grouped bot");

        Assert.False(await host.OnWorldAsync(() => PlayerbotTaxi.IsActive(player)));
        Assert.False(await host.OnWorldAsync(() => PlayerbotTaxi.IsBackedOff(player, host.World.NowMs)));
        Assert.Null(await host.OnWorldAsync(() => PlayerbotTaxi.LastFailure(player)));
    }

    private sealed class IdleController : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }

    private sealed class NoPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }
}
