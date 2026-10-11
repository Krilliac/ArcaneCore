using System.Numerics;
using System.Diagnostics;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
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
    [RealAreaTerrainFact]
    public async Task ColdridgeBot_UsesTheIronforgeEntranceOnTheRealAreaAndTerrainData()
    {
        string terrain = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = terrain,
            configureServices: services =>
            {
                services.AddSingleton<IMapDataStore>(new TramTestData.MapStore());
                services.AddSingleton<IWorldFeature, ManualClockFeature>();
            });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            AreaTemplate[] areas = [.. AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc"))
                .Select(r => new AreaTemplate(r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel,
                    r.Name, r.Team, r.LiquidTypeId))];
            await host.OnWorldAsync(() =>
            {
                WorldMaps maps = WorldMaps.Of(host.World);
                maps.Terrain.Areas = new AreaTable(areas, maps.Registry);
                return true;
            });
            Player player = session.Player!;
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Vector3 coldridge = new(-6489.42f, 494.74f, 388.03f);
            Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(player, 0, coldridge.X, coldridge.Y, coldridge.Z, 0)));
            for (int i = 0; i < 200 && !await host.OnWorldAsync(() => player.IsInWorld && teleports.StageOf(player) is null
                && Vector2.Distance(new(player.X, player.Y), new(coldridge.X, coldridge.Y)) < 1f); i++)
            {
                await host.OnWorldAsync(() => PlayerbotMovementControl.Update(session, player));
                await host.World.AdvanceClockAsync(50);
            }

            (uint zone, bool began, uint from, double ms) = await host.OnWorldAsync(() =>
            {
                uint zone = player.Map!.GetZoneAndAreaId(player.X, player.Y, player.Z).ZoneId;
                var watch = Stopwatch.StartNew();
                bool began = PlayerbotTram.TryBegin(session, player, new Vector3(-8344.69f, 418.89f, 122.36f));
                return (zone, began, PlayerbotTram.TripOf(player)?.From.Entrance.Id ?? 0, watch.Elapsed.TotalMilliseconds);
            });
            output.WriteLine($"Coldridge zone={zone}; tram choice {ms:F2} ms; entrance={from}");
            Assert.Equal(1u, zone);
            Assert.True(began);
            Assert.Equal(2175u, from);
            (bool planned, double planMs) = await host.OnWorldAsync(() =>
            {
                Vector3 entrance = PlayerbotTram.TripOf(player)!.From.EntranceCentre;
                var watch = Stopwatch.StartNew();
                bool planned = PlayerbotNavigation.TryPlanToward(player, entrance,
                    new PlayerbotOptions { Enabled = true }, out PlayerbotRoute? route) && route is not null;
                return (planned, watch.Elapsed.TotalMilliseconds);
            });
            output.WriteLine($"first walking leg to Ironforge planned={planned}; {planMs:F2} ms");
            Assert.True(planned, "Coldridge bot could not plan its first leg to the Ironforge tram entrance");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
            await host.DisposeAsync();
        }
    }

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
            Assert.True(await host.OnWorldAsync(() => PlayerbotTram.TryBegin(session, player, commons, TramTestData.ZoneAt)), "the trip did not begin");

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
