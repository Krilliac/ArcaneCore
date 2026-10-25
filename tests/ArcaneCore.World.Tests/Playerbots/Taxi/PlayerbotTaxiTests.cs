using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Tests.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// Autonomous flight-path use (<see cref="PlayerbotTaxi"/>) against a real world host: the flight master, its taxi nodes and the paths are
/// content data (<see cref="TaxiFixture"/>), the bot talks to the master and asks for flights through the real opcode handlers, and the
/// server's own flight system flies it.
/// </summary>
public sealed partial class PlayerbotTaxiTests
{
    private static PlayerbotOptions Options(Action<PlayerbotTaxiOptions>? taxi = null)
    {
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 };
        options.Taxi.MinRouteYards = 100f;
        taxi?.Invoke(options.Taxi);
        return options;
    }

    [Fact]
    public async Task AFlightMasterInRange_TeachesItsNode_ThroughTheRealHandler()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Assert.False(Rig.Knows(state, 1));
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.True(PlayerbotTaxi.Discover(rig.Session, player, Options()));
            Assert.True(Rig.Knows(state, 1));
            Assert.Equal(1, rig.Session.ManagedBudget.Remaining);

            // Asked once: the scan is throttled, and a known node is not asked for again.
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, Options()));
            Assert.Equal(1, rig.Session.ManagedBudget.Remaining);
            return 0;
        });
    }

    [Fact]
    public async Task ANodeAlreadyKnown_IsNotAskedFor()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, Options()));
            Assert.Equal(2, rig.Session.ManagedBudget.Remaining);
            return 0;
        });
    }

    [Fact]
    public async Task DisabledTaxi_NeitherDiscoversNorFlies()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions off = Options(t => t.Enabled = false);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.False(PlayerbotTaxi.Discover(rig.Session, player, off));
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), off));
            return 0;
        });
    }

    [Fact]
    public async Task ALongTrip_WithBothEndsKnownAndTheFareAffordable_IsFlown()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            TaxiTrip trip = Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player));
            Assert.Equal([1u, 2u], trip.Nodes);
            Assert.Equal(100u, trip.Fare);

            // The master stands within reach: the next drive asks for the flight through the real handler and the server flies the bot.
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            Assert.Equal(TaxiStage.Flying, trip.Stage);
            Assert.Equal(900u, player.Money);

            // In the air the trip only waits, and takes no action.
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.Equal(2, rig.Session.ManagedBudget.Remaining);

            rig.FlyUntilLanded(player);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.Equal(TaxiStep.Idle, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.True(Vector3.Distance(new Vector3(player.X, player.Y, player.Z), rig.NodePosition(2)) < 3f);

            // Landed beside the goal: the rest is walked.
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            return 0;
        });
    }

    [Fact]
    public async Task ATripOverSeveralNodes_UsesTheExpressRequest_AndPaysEachLegAsItFlies()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2, 3);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(3), options));
            TaxiTrip trip = Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player));
            Assert.Equal([1u, 2u, 3u], trip.Nodes);
            Assert.Equal(150u, trip.Fare);

            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            rig.FlyUntilLanded(player);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.Equal(850u, player.Money);
            Assert.True(Vector3.Distance(new Vector3(player.X, player.Y, player.Z), rig.NodePosition(3)) < 3f);
            return 0;
        });
    }

    [Fact]
    public async Task AShortTrip_Walks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            Vector3 near = new(player.X + 60f, player.Y, player.Z);
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, near, Options()));
            Assert.Null(PlayerbotTaxi.TripOf(player));
            // Far enough to fly (262 yards, both nodes known and paid for) but under the configured minimum: walked.
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options(t => t.MinRouteYards = 300f)));
            Assert.Null(PlayerbotTaxi.TripOf(player));
            return 0;
        });
    }

    [Fact]
    public async Task AnUnaffordableFare_Walks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 99;
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options()));
            player.Money = 100;
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options()));
            return 0;
        });
    }

    [Fact]
    public async Task AnUnknownLandingNode_Walks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1);
            player.Money = 1000;
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options()));
            return 0;
        });
    }

    [Fact]
    public async Task AnUnknownDepartureNode_Walks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 2);
            player.Money = 1000;
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options()));
            return 0;
        });
    }

    [Fact]
    public async Task ADepartureNodeBeyondTheWalkLimit_Walks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), Options(t => t.MaxWalkToMasterYards = 10f)));
            return 0;
        });
    }

    [Fact]
    public async Task ADepartureBeyondTheWalkRadius_WhoseFlightStillHalvesTheWalk_IsFlown_AndADetourThatDoesNot_Walks()
    {
        // Quest 6362 (Ride to Thunder Bluff): the bot stands in the Barrens, hundreds of yards past MaxWalkToMasterYards from the Crossroads
        // wind rider, with the ender a couple of thousand yards on. The walk to that master plus the walk from the landing is still at most
        // half the straight line, so the flight is the clear win and the bot speaks to the master. A departure whose walk alone already
        // exceeds half the straight line is not a win and is still walked.
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options(t => t.MaxWalkToMasterYards = 40f);
            Vector3 node1 = rig.NodePosition(1);
            Vector3 goal = rig.Near(2);
            uint now = rig.Session.World.NowMs;

            player.Relocate(node1.X - 400f, node1.Y, node1.Z, 0, now);
            Vector3 far = new(player.X, player.Y, player.Z);
            float farStraight = Vector3.Distance(far, goal);
            Assert.True(400f > options.Taxi.MaxWalkToMasterYards);
            Assert.True(400f > farStraight * 0.5f);
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, goal, options));
            Assert.Null(PlayerbotTaxi.TripOf(player));

            player.Relocate(node1.X - 80f, node1.Y, node1.Z, 0, now);
            Vector3 here = new(player.X, player.Y, player.Z);
            float straight = Vector3.Distance(here, goal);
            float landing = Vector3.Distance(goal, rig.NodePosition(2));
            Assert.True(straight >= options.Taxi.MinRouteYards);
            Assert.True(80f > options.Taxi.MaxWalkToMasterYards);
            Assert.True(landing <= options.Taxi.MaxWalkToMasterYards);
            Assert.True(80f + landing <= straight * 0.5f);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, goal, options));
            TaxiTrip trip = Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player));
            Assert.Equal([1u, 2u], trip.Nodes);
            Assert.Equal(100u, trip.Fare);
            Assert.Equal(TaxiFixture.MasterEntry, trip.MasterEntry);
            return 0;
        });
    }

    [Fact]
    public async Task AServerRefusal_BacksTaxiUseOff_AndTheGoalWalks()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options(t => t.BackoffMinutes = 10);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));

            // The bot is in combat when the flight request reaches the server (a mounted bot is dismounted by the managed action, so a mount no longer refuses): ERR_TAXIPLAYERBUSY, nothing starts.
            player.UnitFlags |= UnitFlags.InCombat;
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Contains("PlayerBusy", failure);
            Assert.False(PlayerbotTaxi.IsFlying(player));
            Assert.Equal(1000u, player.Money);
            Assert.Equal(failure, PlayerbotTaxi.LastFailure(player));

            // The departure node is backed off for the first wait of the travel schedule (60 s), no longer (BackoffMinutes only caps it): the
            // same long trip is walked until the window ends. The other node is not affected.
            player.UnitFlags &= ~UnitFlags.InCombat;
            uint now = rig.Session.World.NowMs;
            Assert.True(PlayerbotTaxi.IsBackedOff(player, now));
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, now));
            Assert.False(PlayerbotTaxi.IsBackedOff(player, 2, now));
            Assert.Equal(TaxiTravel.Walk, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.True(PlayerbotTaxi.IsBackedOff(player, 1, now + 50_000u));
            Assert.False(PlayerbotTaxi.IsBackedOff(player, 1, now + 70_000u));
            return 0;
        });
    }

    [Fact]
    public async Task AMountedBot_IsDismountedByTheFlightRequest_AndFlies()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));

            // The managed action takes the mount off before a bot talks to a flight master (PlayerbotMount.DismountForAction).
            player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, TaxiFixture.GryphonDisplay);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            return 0;
        });
    }

    [Fact]
    public async Task AMountedBot_IsDismountedByTheExpressFlightRequest_AndFlies()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2, 3);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(3), options));
            Assert.Equal([1u, 2u, 3u], Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player)).Nodes);

            // More than two nodes go by the express request, which the server refuses a mounted rider just the same.
            player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, TaxiFixture.GryphonDisplay);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.True(PlayerbotTaxi.IsFlying(player));
            return 0;
        });
    }

    [Fact]
    public async Task AShapeshiftedBot_LeavesItsFormBeforeTheFlightRequest_AndFlies()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2, 3);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            ArcaneCore.World.Spells.SpellFeature spells = rig.Session.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>();
            var catForm = new ArcaneCore.Game.Spells.SpellInfo
            {
                Id = 768,
                Name = "Cat Form",
                RangeIndex = ArcaneCore.Game.Spells.SpellConstants.RangeIndexSelfOnly,
                Duration = new ArcaneCore.Game.Spells.SpellDuration(-1, 0, -1),
                Effects =
                [
                    new ArcaneCore.Game.Spells.SpellEffectInfo
                    {
                        Effect = ArcaneCore.Game.Spells.SpellEffectName.ApplyAura,
                        AuraType = ArcaneCore.Game.Spells.AuraType.ModShapeshift,
                        MiscValue = (int)ArcaneCore.Game.Spells.ShapeshiftForm.Cat,
                        TargetA = ArcaneCore.Game.Spells.SpellImplicitTarget.UnitCaster,
                    },
                ],
            };
            var battleStance = new ArcaneCore.Game.Spells.SpellInfo
            {
                Id = 2457,
                Name = "Battle Stance",
                RangeIndex = ArcaneCore.Game.Spells.SpellConstants.RangeIndexSelfOnly,
                Duration = new ArcaneCore.Game.Spells.SpellDuration(-1, 0, -1),
                Effects =
                [
                    new ArcaneCore.Game.Spells.SpellEffectInfo
                    {
                        Effect = ArcaneCore.Game.Spells.SpellEffectName.ApplyAura,
                        AuraType = ArcaneCore.Game.Spells.AuraType.ModShapeshift,
                        MiscValue = (int)ArcaneCore.Game.Spells.ShapeshiftForm.BattleStance,
                        TargetA = ArcaneCore.Game.Spells.SpellImplicitTarget.UnitCaster,
                    },
                ],
            };
            spells.System.Store = new ArcaneCore.Game.Spells.SpellStore(
                spells.System.Store.All.Append(catForm).Append(battleStance), [], []);

            // 1. 2-node flight (CmsgActivatetaxi) in Cat Form: form is cancelled and flight succeeds.
            Assert.True(spells.System.AddAura(player, catForm.Id));
            Assert.Equal(ArcaneCore.Game.Spells.ShapeshiftForm.Cat, ArcaneCore.Game.Spells.ShapeshiftService.GetForm(player));
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            Assert.Equal([1u, 2u], Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player)).Nodes);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure));
            Assert.Null(failure);
            Assert.Equal(ArcaneCore.Game.Spells.ShapeshiftForm.None, ArcaneCore.Game.Spells.ShapeshiftService.GetForm(player));
            Assert.False(spells.System.HasAura(player, catForm.Id));
            Assert.True(PlayerbotTaxi.IsFlying(player));
            rig.FlyUntilLanded(player);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.Equal(TaxiStep.Idle, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));

            // 2. 3-node flight (CmsgActivatetaxiexpress) in Cat Form: form is cancelled and express flight succeeds.
            Vector3 node1 = rig.NodePosition(1);
            player.Relocate(node1.X, node1.Y, node1.Z, 0, rig.Session.World.NowMs);
            Assert.True(spells.System.AddAura(player, catForm.Id));
            Assert.Equal(ArcaneCore.Game.Spells.ShapeshiftForm.Cat, ArcaneCore.Game.Spells.ShapeshiftService.GetForm(player));
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(3), options));
            Assert.Equal([1u, 2u, 3u], Assert.IsType<TaxiTrip>(PlayerbotTaxi.TripOf(player)).Nodes);
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out failure));
            Assert.Null(failure);
            Assert.Equal(ArcaneCore.Game.Spells.ShapeshiftForm.None, ArcaneCore.Game.Spells.ShapeshiftService.GetForm(player));
            Assert.False(spells.System.HasAura(player, catForm.Id));
            Assert.True(PlayerbotTaxi.IsFlying(player));
            rig.FlyUntilLanded(player);
            Assert.Equal(TaxiStep.Arrived, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));
            Assert.Equal(TaxiStep.Idle, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out _));

            // 3. Allowed stance (BattleStance) is preserved when boarding a flight.
            player.Relocate(node1.X, node1.Y, node1.Z, 0, rig.Session.World.NowMs);
            Assert.True(spells.System.AddAura(player, battleStance.Id));
            Assert.Equal(ArcaneCore.Game.Spells.ShapeshiftForm.BattleStance, ArcaneCore.Game.Spells.ShapeshiftService.GetForm(player));
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));
            rig.Session.ManagedBudget = new ManagedActionBudget(2);
            Assert.Equal(TaxiStep.Traveling, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out failure));
            Assert.Null(failure);
            Assert.Equal(ArcaneCore.Game.Spells.ShapeshiftForm.BattleStance, ArcaneCore.Game.Spells.ShapeshiftService.GetForm(player));
            Assert.True(spells.System.HasAura(player, battleStance.Id));
            Assert.True(PlayerbotTaxi.IsFlying(player));
            return 0;
        });
    }

    [Fact]
    public async Task TheBrain_NeitherThinksNorMoves_WhileTheBotIsInTheAir_AndContinuesAfterTheLanding()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            // Open ground under the bot, so a brain that was free to explore would walk.
            WorldCollision.Of(rig.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            var brain = new PlayerbotBrain(rig.Session, options);
            Assert.Equal(TaxiTravel.Traveling, PlayerbotTaxi.TryTravelByTaxi(rig.Session, player, 0, rig.Near(2), options));

            // The brain drives the trip: at the master it asks for the flight.
            for (int think = 0; think < 4 && !PlayerbotTaxi.IsFlying(player); think++)
            {
                rig.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
            }

            Assert.True(PlayerbotTaxi.IsFlying(player), "the brain did not board the flight");

            Vector3 aloft = new(player.X, player.Y, player.Z);
            // In the air: no action of any kind, think after think.
            for (int think = 0; think < 6; think++)
            {
                rig.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Equal(4, rig.Session.ManagedBudget.Remaining);
                Assert.True(PlayerbotTaxi.IsFlying(player));
                Assert.Equal(aloft, new Vector3(player.X, player.Y, player.Z));
            }

            rig.FlyUntilLanded(player);
            rig.Session.ManagedBudget = new ManagedActionBudget(4);
            brain.Update(500);
            Assert.Equal(TaxiStage.Done, PlayerbotTaxi.TripOf(player)!.Stage);
            Assert.False(PlayerbotTaxi.IsActive(player));
            return 0;
        });
    }

    [Fact]
    public void TheOptions_AreValidated()
    {
        new PlayerbotOptions().Taxi.Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotTaxiOptions { MinRouteYards = 50f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotTaxiOptions { MinRouteYards = float.NaN }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotTaxiOptions { MaxWalkToMasterYards = 5f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotTaxiOptions { BackoffMinutes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotTaxiOptions { BackoffMinutes = 1441 }.Validate());
        var options = new PlayerbotOptions();
        Assert.True(options.Taxi.Enabled);
        Assert.Equal(600f, options.Taxi.MinRouteYards);
        Assert.Equal(10, options.Taxi.BackoffMinutes);
        options.Taxi.BackoffMinutes = 0;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task AFlightAbortedMidAir_CancelsTheTripWithoutReportingLandedOffNodeOrWrongMap()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, state) =>
        {
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
            Rig.Know(state, 1, 2);
            player.Money = 1000;
            PlayerbotOptions options = Options();
            TaxiFlightSystem flights = rig.Host.WorldServices.GetRequiredService<NpcServicesFeature>().Flights!;
            int abortedCount = 0;
            flights.Aborted += (p, _) => { if (ReferenceEquals(p, player)) abortedCount++; };

            // Case 1: Mid-flight external relocation causes Advance to abort (landed: false) far from destination.
            TaxiTrip trip1 = BoardFlight(rig, player, options);
            flights.Update(player.Map!, 1000);
            Assert.True(flights.IsFlying(player));
            player.Relocate(player.X - 500f, player.Y, player.Z, 0, rig.Session.World.NowMs);
            flights.Update(player.Map!, 1000);
            Assert.False(flights.IsFlying(player));
            Assert.Equal(1, abortedCount);

            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure1));
            Assert.NotNull(failure1);
            Assert.True(trip1.Interrupted);
            Assert.False(PlayerbotTaxi.IsActive(player));
            AssertNoTaxiAnomaly(cap);

            // Case 2: Mid-flight cross-map teleport calls Abort(player) directly with the player on another map.
            player.Relocate(TaxiFixture.StartX, TaxiFixture.StartY, TaxiFixture.Z, 0, rig.Session.World.NowMs);
            TaxiTrip trip2 = BoardFlight(rig, player, options);
            flights.Update(player.Map!, 1000);
            Assert.True(flights.IsFlying(player));
            trip2.MapId = 1u;
            flights.Abort(player);
            Assert.False(flights.IsFlying(player));
            Assert.Equal(2, abortedCount);

            Assert.Equal(TaxiStep.Failed, PlayerbotTaxi.Drive(rig.Session, player, options, 500, out string? failure2));
            Assert.NotNull(failure2);
            Assert.True(trip2.Interrupted);
            Assert.False(PlayerbotTaxi.IsActive(player));
            AssertNoTaxiAnomaly(cap);
            return 0;
        });
    }

    [Fact]
    public async Task PopulationProvisioner_Relocate_RefusesAndDoesNotTeleport_WhileBotIsFlyingOnTaxi()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Taxiflyer", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        var safeAt = new WorldSafeLoc(9901, 0, -8200f, -700f, 90f, 0f, "safe");

        await world.Host.World.InvokeAsync(() =>
        {
            var provisioner = new PopulationProvisioner(world.Host.WorldServices) { AreaLevelSource = _ => 1 };
            PopulationPlacementFacts facts = PopulationProvisionerTests.FactsFor(world, safeAt);
            Player bot = world.Bots.FindSession(id)!.Player!;
            float startX = bot.X;
            float startY = bot.Y;

            bot.UnitFlags |= UnitFlags.TaxiFlight;
            Assert.True(PlayerbotTaxi.IsFlying(bot));
            string? refusal = provisioner.Relocate(id, safeAt, facts);
            Assert.NotNull(refusal);
            Assert.Equal(startX, bot.X);
            Assert.Equal(startY, bot.Y);

            bot.UnitFlags &= ~UnitFlags.TaxiFlight;
            Assert.Null(provisioner.Relocate(id, safeAt, facts));
            return true;
        });
    }

    /// <summary>A host with the taxi fixture, the managed bot, and the fixture's helpers.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WorldTestHost _host;

        private Rig(WorldTestHost host, WorldSession session)
        {
            _host = host;
            Session = session;
        }

        public WorldSession Session { get; }

        public ArcaneCore.Game.Maps.WorldRuntime World => _host.World;

        public WorldTestHost Host => _host;

        /// <summary>Waits until the bot no longer sees the master (it was moved away).</summary>
        public Task WaitForMasterOutOfSightAsync()
            => _host.WaitForWorldAsync(() => !Session.Player!.VisibleObjects.Contains(TaxiFixture.MasterGuid), "the master leaves sight");

        public static async Task<Rig> StartAsync(bool withQuest = false, bool alternateDeparture = false)
        {
            TaxiServices.Current.Value = new TaxiFixture { WithQuest = withQuest, AlternateDeparture = alternateDeparture };
            WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TaxiFixture.MasterGuid), "flight master visible");
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(session.Player!) is { Loaded: true },
                "npc state loaded");
            return new Rig(host, session);
        }

        public Task<T> OnPlayer<T>(Func<Player, PlayerNpcState, T> action) => _host.OnWorldAsync(() =>
        {
            Player player = Session.Player!;
            return action(player, _host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!);
        });

        public static void Know(PlayerNpcState state, params uint[] nodes)
        {
            foreach (uint node in nodes) state.TaxiMask[(node - 1) / 32] |= 1u << (int)((node - 1) % 32);
        }

        public static bool Knows(PlayerNpcState state, uint node) => (state.TaxiMask[(node - 1) / 32] & (1u << (int)((node - 1) % 32))) != 0;

        public Vector3 NodePosition(uint node)
        {
            TaxiNode n = _host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.Npcs.Node(node)!;
            return new Vector3(n.X, n.Y, n.Z);
        }

        /// <summary>A point twelve yards past a node: where a trip that the node serves ends.</summary>
        public Vector3 Near(uint node) => NodePosition(node) + new Vector3(0f, 12f, 0f);

        public void FlyUntilLanded(Player player)
        {
            TaxiFlightSystem flights = _host.WorldServices.GetRequiredService<NpcServicesFeature>().Flights!;
            for (int i = 0; i < 40 && flights.IsFlying(player); i++) flights.Update(player.Map!, 2000);
            Assert.False(flights.IsFlying(player));
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await _host.DisposeAsync();
            TaxiServices.Current.Value = null;
        }
    }
}
