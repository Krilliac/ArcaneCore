using System.Numerics;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// Needs the live server's terrain extraction with Kalimdor's navigation mesh and models (<c>ARCANECORE_TEST_TERRAIN_DIR</c>: maps/,
/// vmaps/, mmaps/001.mmap) and the client's TransportAnimation.dbc (<c>ARCANECORE_TEST_DBC_DIR</c>). Neither is committed; without them the
/// test is reported Skipped, never a silent pass.
/// </summary>
internal sealed class RealLiftRideFactAttribute : FactAttribute
{
    public RealLiftRideFactAttribute()
    {
        string? terrain = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.TerrainVariable);
        string? dbc = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.DbcVariable);
        if (string.IsNullOrWhiteSpace(terrain) || !File.Exists(Path.Combine(terrain, "mmaps", "001.mmap")))
            Skip = $"{RealElevatorFactAttribute.TerrainVariable} is not set to a terrain data root with mmaps/001.mmap: the real Great Lift ride did NOT run.";
        else if (string.IsNullOrWhiteSpace(dbc) || !File.Exists(Path.Combine(dbc, "TransportAnimation.dbc")))
            Skip = $"{RealElevatorFactAttribute.DbcVariable} is not set to a directory with TransportAnimation.dbc: the real Great Lift ride did NOT run.";
    }
}

/// <summary>
/// The Great Lift ridden on the real data: Kalimdor's terrain, models and navigation mesh, the client's paths of the two cars, the classic-db
/// car rows. A managed bot on the Thousand Needles floor beside the lift's foot walks to a landing on the real mesh, waits, rides up and
/// steps off onto the floor at the top (found in the real models), where the mesh leads on towards Ratchet. Game time only (manual clock).
/// </summary>
public sealed class PlayerbotElevatorRealRideTests(ITestOutputHelper output)
{
    private static readonly Vector3 Foot = new(-4700f, -1870f, -48f);

    [RealLiftRideFact]
    public async Task ABot_RidesTheRealGreatLift_UpFromThousandNeedles()
    {
        string terrain = Environment.GetEnvironmentVariable(RealElevatorFactAttribute.TerrainVariable)!;
        TransportAnimationCatalog animation = TransportAnimationDbcReader.Load(
            Path.Combine(Environment.GetEnvironmentVariable(RealElevatorFactAttribute.DbcVariable)!, "TransportAnimation.dbc"));
        WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = terrain, configureServices: services =>
        {
            services.AddSingleton<IMapDataStore>(new MapStore());
            services.AddScoped<IGameObjectDataStore>(_ => new GameObjectStore());
            services.AddSingleton(animation);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(player, 1, Foot.X, Foot.Y, Foot.Z, 0)), "teleport refused");
            for (int i = 0; i < 200 && !await host.OnWorldAsync(() => player.IsInWorld && teleports.StageOf(player) is null && player.MapId == 1); i++)
            {
                await host.OnWorldAsync(() => PlayerbotMovementControl.Update(session, player));
                await host.World.AdvanceClockAsync(50);
            }

            var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 };
            var brain = new PlayerbotBrain(session, options);
            ElevatorLift lift = await host.OnWorldAsync(() => Assert.Single(PlayerbotElevator.NetworkOf(session)!.Lifts));
            await host.OnWorldAsync(() => PlayerbotElevator.Begin(session, player, lift, ElevatorLevel.Low, ElevatorTestData.Dizzywig));

            ElevatorStage? last = null;
            float highestAboard = float.MinValue;
            for (int step = 0; step < 3_000; step++)
            {
                (ElevatorStage? stage, bool aboard, Vector3 at) = await host.OnWorldAsync(() =>
                {
                    brain.Update(100);
                    return (PlayerbotElevator.TripOf(player)?.Stage, (player.Movement.Flags & MovementFlags.OnTransport) != 0,
                        new Vector3(player.X, player.Y, player.Z));
                });
                if (stage != last)
                {
                    output.WriteLine($"{host.World.NowMs / 1000.0:F1}s {stage} at {at}");
                    last = stage;
                }

                if (aboard) highestAboard = MathF.Max(highestAboard, at.Z);
                if (stage is ElevatorStage.Done or ElevatorStage.Failed) break;
                for (int i = 0; i < 2; i++)
                {
                    await host.World.AdvanceClockAsync(50);
                    await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, player, host.World.NowMs));
                }
            }

            (ElevatorStage? end, string? failure, Vector3 off) = await host.OnWorldAsync(() => (PlayerbotElevator.TripOf(player)?.Stage,
                PlayerbotElevator.TripOf(player)?.Failure, new Vector3(player.X, player.Y, player.Z)));
            output.WriteLine($"end {end} ({failure}) at {off}; rode up to z {highestAboard:F1}");
            Assert.Equal(ElevatorStage.Done, end);
            Assert.True(highestAboard > lift.HighZ - 2f, $"rode only up to {highestAboard:F1}");
            Assert.True(MathF.Abs(off.Z - lift.HighZ) < 3f, $"off at {off}, not at the top");

            // From the top the real mesh leads on towards Ratchet.
            bool onward = await host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(player, ElevatorTestData.Dizzywig, options, out PlayerbotRoute? route)
                && route is not null && Vector2.Distance(new(route.Points[^1].X, route.Points[^1].Y), new(ElevatorTestData.Dizzywig.X, ElevatorTestData.Dizzywig.Y))
                    < Vector2.Distance(new(off.X, off.Y), new(ElevatorTestData.Dizzywig.X, ElevatorTestData.Dizzywig.Y)) - 50f);
            Assert.True(onward, "no way on towards Ratchet from where the bot got off");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
            await host.DisposeAsync();
        }
    }

    private sealed class MapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent([.. TramTestData.Maps.Where(m => m.Entry != TramTestData.Tram)], [], [], [], []));
    }

    private sealed class GameObjectStore : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(ElevatorTestData.Content(16874, 16876));
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
