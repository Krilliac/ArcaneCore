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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// Needs the live server's terrain extraction (<c>ARCANECORE_TEST_TERRAIN_DIR</c>: maps/, vmaps/, mmaps/, which hold the Deeprun Tram's
/// 369 mesh and models) and the client's build-5875 DBCs (<c>ARCANECORE_TEST_DBC_DIR</c>: TransportAnimation.dbc). Neither is committed;
/// without them the test is reported Skipped, never a silent pass.
/// </summary>
internal sealed class RealTramFactAttribute : FactAttribute
{
    public const string TerrainVariable = "ARCANECORE_TEST_TERRAIN_DIR", DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    public RealTramFactAttribute()
    {
        string? terrain = Environment.GetEnvironmentVariable(TerrainVariable);
        string? dbc = Environment.GetEnvironmentVariable(DbcVariable);
        if (string.IsNullOrWhiteSpace(terrain) || !File.Exists(Path.Combine(terrain, "mmaps", "369.mmap")))
            Skip = $"{TerrainVariable} is not set to a terrain data root with mmaps/369.mmap: the real Deeprun Tram ride did NOT run.";
        else if (string.IsNullOrWhiteSpace(dbc) || !File.Exists(Path.Combine(dbc, "TransportAnimation.dbc")))
            Skip = $"{DbcVariable} is not set to a directory with TransportAnimation.dbc: the real Deeprun Tram ride did NOT run.";
    }
}

/// <summary>
/// The tram ride on the real data: the Deeprun Tram's navigation mesh and models (map 369), the Stormwind and Ironforge terrain, the
/// client's TransportAnimation.dbc for the six cars, and the classic-db trigger and car rows (<see cref="TramTestData"/>). A managed bot
/// leaves the Stormwind tram exit, walks into the entrance, finds the platform beside a car on the real mesh, rides the real animation
/// through the tunnel, gets off at Ironforge and walks into the exit. Game time only (manual clock).
/// </summary>
public sealed class PlayerbotTramRealTerrainTests(ITestOutputHelper output)
{
    [RealTramFact]
    public async Task ABot_RidesTheRealTram_FromStormwindToIronforge()
    {
        string terrain = Environment.GetEnvironmentVariable(RealTramFactAttribute.TerrainVariable)!;
        TransportAnimationCatalog animation = TransportAnimationDbcReader.Load(
            Path.Combine(Environment.GetEnvironmentVariable(RealTramFactAttribute.DbcVariable)!, "TransportAnimation.dbc"));
        WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = terrain, configureServices: services =>
        {
            services.AddSingleton<IMapDataStore>(new TramTestData.MapStore());
            services.AddScoped<IGameObjectDataStore>(_ => new TramTestData.GameObjectStore());
            services.AddSingleton(animation);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Vector3 start = TramTestData.StormwindExitLanding;
            Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(player, 0, start.X, start.Y, start.Z, 0)), "teleport refused");
            for (int i = 0; i < 200 && !await host.OnWorldAsync(() => player.IsInWorld && teleports.StageOf(player) is null && player.MapId == 0); i++)
            {
                await host.OnWorldAsync(() => PlayerbotMovementControl.Update(session, player));
                await host.World.AdvanceClockAsync(50);
            }

            var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 };
            var brain = new PlayerbotBrain(session, options);
            Vector3 commons = new(-4918.9f, -982.5f, 501.5f);
            Assert.True(await host.OnWorldAsync(() => PlayerbotTram.TryBegin(session, player, commons)), "the trip did not begin");

            TramStage? last = null;
            float southmost = float.MaxValue;
            for (int step = 0; step < 5_000; step++)
            {
                (TramStage? stage, bool aboard, float x, float y, float z, uint map) = await host.OnWorldAsync(() =>
                {
                    brain.Update(100);
                    return (PlayerbotTram.TripOf(player)?.Stage, (player.Movement.Flags & MovementFlags.OnTransport) != 0, player.X, player.Y, player.Z, player.MapId);
                });
                if (stage != last)
                {
                    output.WriteLine($"{host.World.NowMs / 1000.0:F1}s {stage} at {x:F1},{y:F1},{z:F1} map {map}");
                    last = stage;
                }

                if (aboard) southmost = MathF.Min(southmost, y);
                if (stage is TramStage.Done or TramStage.Failed) break;
                for (int i = 0; i < 2; i++)
                {
                    await host.World.AdvanceClockAsync(50);
                    await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, player, host.World.NowMs));
                }
            }

            (TramStage? end, string? failure, uint mapId, Vector3 at) = await host.OnWorldAsync(() => (PlayerbotTram.TripOf(player)?.Stage,
                PlayerbotTram.TripOf(player)?.Failure, player.MapId, new Vector3(player.X, player.Y, player.Z)));
            output.WriteLine($"end {end} {failure} at {at} map {mapId}; rode to y={southmost:F0}");
            Assert.Equal(TramStage.Done, end);
            Assert.True(southmost < 100f, $"rode only to y={southmost:F0}: not through the tunnel");
            Assert.Equal(0u, mapId);
            Assert.True(Vector2.Distance(new(at.X, at.Y), new(TramTestData.IronforgeExitLanding.X, TramTestData.IronforgeExitLanding.Y)) < 5f,
                $"out at {at}, not at the Ironforge exit");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
            await host.DisposeAsync();
        }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
