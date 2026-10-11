using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The tram trip (<see cref="PlayerbotTram"/>) beside the home return (#139), the outside-home stall rule of the shared set-asides (#145)
/// and the ghost recovery (#151): a homed bot is not teleported home mid-trip (map 369 is "another map" to its home, which Auto
/// teleports from at once), a failed trip is the bot's own set-aside and never every bot's, and a bot that dies on the tram map drops its
/// trip, walks back to its body there as a ghost, and leaves by the nearest exit once revived.
/// </summary>
public sealed class PlayerbotTramInteractionTests(ITestOutputHelper output)
{
    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f);

    /// <summary>A Westfall home (Sentinel Hill), thousands of yards from both tram exits; Auto return (the default).</summary>
    private static readonly Vector3 WestfallHome = new(-10508f, 1047f, 55f);

    private static PlayerbotOptions Options() => new() { Enabled = true, ThinkIntervalMs = 100 };

    /// <summary>
    /// A bot homed in Westfall rides from Stormwind to Ironforge: on map 369 (another map to its home) and at the far exit (beyond
    /// <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>) Auto would teleport it home, but the trip owns the bot until it stands on
    /// the continent again, so the ride completes. Only after it does the home return, with no errand left, take the bot home.
    /// </summary>
    [Fact]
    public async Task AHomedBot_OnATramTrip_IsNotTeleportedHome_UntilTheTripEnds()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        var brain = new PlayerbotBrain(world.Session, Options());
        await world.OnWorldAsync(() => brain.Home = new PlayerbotHomeArea(0, WestfallHome, 300f, "Sentinel Hill"));
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)), "the trip did not begin");

        TramStage? stage = null;
        bool sawTramMap = false;
        for (int step = 0; step < 4_000 && stage is not (TramStage.Done or TramStage.Failed); step++)
        {
            (TramStage? s, uint map, Vector3 at) = await world.TickAsync(() =>
            {
                brain.Update(100);
                Player p = world.Player;
                return (PlayerbotTram.TripOf(p)?.Stage, p.MapId, new Vector3(p.X, p.Y, p.Z));
            });
            stage = s;
            sawTramMap |= map == TramTestData.Tram;
            Assert.False(map == 0 && Vector2.Distance(new(at.X, at.Y), new(WestfallHome.X, WestfallHome.Y)) < 300f,
                $"teleported home mid-trip (stage {s})");
        }

        Assert.True(sawTramMap, "never on the tram map");
        Assert.Equal(TramStage.Done, stage);
        (uint arrivedMap, Vector3 arrived) = await world.OnWorldAsync(() => (world.Player.MapId, new Vector3(world.Player.X, world.Player.Y, world.Player.Z)));
        Assert.Equal(0u, arrivedMap);
        Assert.True(Vector2.Distance(new(arrived.X, arrived.Y), new(TramTestData.IronforgeExitLanding.X, TramTestData.IronforgeExitLanding.Y)) < 5f,
            $"out at {arrived}, not at the Ironforge exit");

        // The trip is over and nothing else holds the bot: now the home return applies (Auto, far beyond the teleport distance).
        bool home = false;
        for (int step = 0; step < 200 && !home; step++)
        {
            home = await world.TickAsync(() =>
            {
                brain.Update(100);
                PlayerbotMovementControl.Update(world.Session, world.Player);
                return world.Player.MapId == 0 && Vector2.Distance(new(world.Player.X, world.Player.Y), new(WestfallHome.X, WestfallHome.Y)) < 300f;
            });
        }

        Assert.True(home, "the home return did not take the bot home after the trip");
    }

    /// <summary>
    /// A trip that fails (no car runs) sets the goal aside for the bot alone: two other bots already stalled on the quest, so a shared
    /// report would make this the third and set it aside for every bot. The tram not carrying the bot says nothing about the quest beyond
    /// it, and the failure is reported at the exit the bot left by, not where the quest failed.
    /// </summary>
    [Fact]
    public async Task AFailedTrip_SetsTheGoalAsideForTheBotOnly_NeverForEveryBot()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(animation: new TransportAnimationCatalog([(4170u, new TransportAnimationNode(0, 0, 0, 0))]));
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        var shared = new PlayerbotSharedSetAsides();
        var brain = new PlayerbotBrain(world.Session, Options(), shared: shared);
        await world.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            shared.Report(PlayerbotGoalKind.Quest, 0, 400, ulong.MaxValue - 1, now);
            shared.Report(PlayerbotGoalKind.Quest, 0, 400, ulong.MaxValue - 2, now);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 1376, 400);
        });
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));

        string? stall = null;
        for (int step = 0; step < 2_000 && stall is null; step++)
        {
            stall = await world.TickAsync(() =>
            {
                int before = brain.StallCount;
                brain.Update(100);
                return brain.StallCount != before ? brain.StallReport : null;
            });
        }

        output.WriteLine(stall ?? "(none)");
        Assert.NotNull(stall);
        Assert.StartsWith("tram: ", stall);
        await world.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            Assert.True(brain.Suspensions.IsQuestSuspended(400, now), "the bot's own set-aside is missing");
            Assert.False(shared.IsQuestSetAside(400, now), "a failed tram trip set the quest aside for every bot");
            Assert.Null(brain.SharedSetAside);
        });
    }

    /// <summary>
    /// A bot dies on the platform while waiting for its car (map 369 is off AllowedMaps; only a trip makes it walkable). The death drops
    /// the trip and its entrance consent. The ghost, released away from its body (as a graveyard landing would put it), walks back to the
    /// body on the tram map, reclaims it there without faulting, and the revived bot leaves by the nearest platform's exit.
    /// </summary>
    [Fact]
    public async Task ABotThatDiesOnTheTramMap_DropsItsTrip_RecoversItsBodyThere_AndLeavesByTheExit()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        PlayerbotOptions options = Options();
        var brain = new PlayerbotBrain(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));

        TramStage? stage = null;
        for (int step = 0; step < 4_000 && stage is not (TramStage.Waiting or TramStage.Done or TramStage.Failed); step++)
            stage = await world.TickAsync(() => { brain.Update(100); return PlayerbotTram.TripOf(world.Player)?.Stage; });
        Assert.Equal(TramStage.Waiting, stage);

        Vector3 body = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            Assert.Equal(TramTestData.Tram, p.MapId);
            p.Health = 0;
            p.Map!.Combat.KillPlayer(p);
            world.Session.ManagedBudget = new ManagedActionBudget(4);
            brain.Update(1);
            Assert.False(p.IsAlive);
            Assert.Null(PlayerbotTram.TripOf(p)); // the death dropped the trip
            Assert.Null(PlayerbotAreaTriggers.TeleportTargetOf(p)); // and its entrance consent
            return new Vector3(p.X, p.Y, p.Z);
        });

        // Released, the ghost stands away from the body (beyond the reclaim distance), on the tram map.
        bool ghost = false;
        for (int step = 0; step < 200 && !ghost; step++)
        {
            ghost = await world.TickAsync(() =>
            {
                world.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(100);
                return (world.Player.Flags & PlayerFlags.Ghost) != 0;
            });
        }

        Assert.True(ghost, "the bot was never released");
        Vector3 away = body with { Y = body.Y + 60f };
        await world.PlaceAsync(TramTestData.Tram, away);
        Assert.False(await world.OnWorldAsync(() => world.Player.IsAlive));

        bool revivedOnTram = false;
        float closest = float.MaxValue;
        uint finalMap = TramTestData.Tram;
        for (int step = 0; step < 6_000 && finalMap != 0; step++)
        {
            (bool alive, uint map, Vector3 at) = await world.TickAsync(() =>
            {
                world.Session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(100);
                Player p = world.Player;
                return (p.IsAlive, p.MapId, new Vector3(p.X, p.Y, p.Z));
            });
            if (!alive) closest = MathF.Min(closest, Vector2.Distance(new(at.X, at.Y), new(body.X, body.Y)));
            revivedOnTram |= alive && map == TramTestData.Tram;
            finalMap = map;
        }

        output.WriteLine($"ghost came within {closest:F1} yd of its body; last recovery step {brain.Recovery.LastStep}");
        Assert.True(revivedOnTram, "the bot was not revived at its body on the tram map");
        Assert.True(closest < 40f, $"the ghost never walked back to its body (closest {closest:F1} yd)");
        Assert.Equal(0u, finalMap);
        Vector3 outAt = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        Assert.True(Vector2.Distance(new(outAt.X, outAt.Y), new(TramTestData.StormwindExitLanding.X, TramTestData.StormwindExitLanding.Y)) < 5f,
            $"out at {outAt}, not at the Stormwind exit (the nearest platform's)");
    }
}
