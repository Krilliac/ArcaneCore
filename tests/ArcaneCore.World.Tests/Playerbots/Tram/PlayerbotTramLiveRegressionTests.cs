using System.Numerics;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The w22 live regression (2026-10-11 08:05, <c>live-w22-r1</c>): 67 dwarf and gnome bots homed in Westfall, 1,879 to 2,913 yards from
/// the Stormwind tram entrance, began a trip to their Ironforge turn-ins at their first think and failed it about a minute later with
/// "tram: the trip failed" and no reason. Replayed on the real terrain from where one of them stood (Wfbotak, the characters database
/// before the start: -10193.8, 1089.2, 36.8, Sentinel Hill) with the live bot options.
/// </summary>
public sealed class PlayerbotTramLiveRegressionTests(ITestOutputHelper output)
{
    /// <summary>Wfbotak at the start of live-w22-r1 (before-start-characters.db).</summary>
    private static readonly Vector3 Wfbotak = new(-10193.776f, 1089.2113f, 36.766f);

    /// <summary>The live <c>World:Playerbots</c> options (appsettings of live-w22-r1).</summary>
    private static PlayerbotOptions LiveOptions() => new()
    {
        Enabled = true, ThinkIntervalMs = 100, MaxActionsPerTick = 12, MaxPathPoints = 128, MaxRouteYards = 2000, MoveSpeed = 7,
    };

    [RealTramFact]
    public async Task AWestfallBot_ReachesTheStormwindEntrance_AndRidesToIronforge()
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
            Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(player, 0, Wfbotak.X, Wfbotak.Y, Wfbotak.Z, 0)), "teleport refused");
            for (int i = 0; i < 200 && !await host.OnWorldAsync(() => player.IsInWorld && teleports.StageOf(player) is null
                && Vector2.Distance(new(player.X, player.Y), new(Wfbotak.X, Wfbotak.Y)) < 1f); i++)
            {
                await host.OnWorldAsync(() => PlayerbotMovementControl.Update(session, player));
                await host.World.AdvanceClockAsync(50);
            }

            PlayerbotOptions options = LiveOptions();
            var brain = new PlayerbotBrain(session, options);
            Vector3 commons = new(-4918.9f, -982.5f, 501.5f);
            Assert.True(await host.OnWorldAsync(() => PlayerbotTram.TryBegin(session, player, commons, TramTestData.ZoneAt)), "the trip did not begin");

            TramStage? last = null;
            string? stall = null;
            uint lastLog = 0;
            for (int step = 0; step < 12_000; step++)
            {
                (TramStage? stage, float x, float y, float z, uint map, string? report) = await host.OnWorldAsync(() =>
                {
                    int before = brain.TransitFailureCount;
                    brain.Update(100);
                    return (PlayerbotTram.TripOf(player)?.Stage, player.X, player.Y, player.Z, player.MapId,
                        brain.TransitFailureCount != before ? brain.LastTransitFailure : null);
                });
                stall ??= report;
                if (stage != last || host.World.NowMs - lastLog >= 30_000)
                {
                    output.WriteLine($"{host.World.NowMs / 1000.0:F1}s {stage} at {x:F1},{y:F1},{z:F1} map {map}");
                    last = stage;
                    lastLog = host.World.NowMs;
                }

                if (stage is TramStage.Done or TramStage.Failed) break;
                for (int i = 0; i < 2; i++)
                {
                    await host.World.AdvanceClockAsync(50);
                    await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, player, host.World.NowMs));
                }
            }

            (TramStage? end, string? failure, uint mapId, Vector3 at) = await host.OnWorldAsync(() => (PlayerbotTram.TripOf(player)?.Stage,
                PlayerbotTram.TripOf(player)?.Failure, player.MapId, new Vector3(player.X, player.Y, player.Z)));
            output.WriteLine($"end {end} failure={failure} stall={stall} at {at} map {mapId}");
            Assert.Equal(TramStage.Done, end);
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
