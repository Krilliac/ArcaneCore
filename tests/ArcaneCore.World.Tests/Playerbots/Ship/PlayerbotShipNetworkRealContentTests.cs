using ArcaneCore.Data;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Transports;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Transports;
using ArcaneCore.World.Transports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Ship;

/// <summary>
/// The ship network (<see cref="PlayerbotShipNetwork"/>) on a copy of a refreshed world database and the build-5875 TaxiPathNode.dbc (gated
/// like RealTransportContentTests: <c>ARCANECORE_TEST_WORLD_DB</c>, <c>ARCANECORE_TEST_DBC_DIR</c>; Skipped without them, never a silent
/// pass): every classic route of the classification is built from the real data with both its ports, each on a continent map.
/// </summary>
public sealed class PlayerbotShipNetworkRealContentTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Each classic route and the maps of its two ports (port A first, in key frame order; either order is accepted).</summary>
    private static readonly (uint Entry, uint[] PortMaps)[] s_routes =
    [
        (20808, [0, 1]),
        (164871, [0, 1]),
        (175080, [0, 1]),
        (176231, [0, 1]),
        (176244, [1, 1]),
        (176310, [0, 1]),
        (176495, [0, 0]),
        (177233, [1, 1]),
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-real-ship-network-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle still closing; the temp directory is harmless.
        }
    }

    [RealTransportContentFact]
    public async Task TheNetwork_FindsBothPortsOfEveryClassicRoute_OnTheirContinents()
    {
        Directory.CreateDirectory(_directory);
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(RealTransportContentFactAttribute.WorldVariable)!, world);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={world};Pooling=False",
            ["NpcServices:TaxiPathNodeDbcPath"] = Path.Combine(Environment.GetEnvironmentVariable(RealTransportContentFactAttribute.DbcVariable)!, "TaxiPathNode.dbc"),
            ["World:Transports:Enabled"] = "true",
        }).Build();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(configuration);
            services.AddWorldDatabase(configuration);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is not null, "the transport system is installed");

        IReadOnlyList<ShipRoute> routes = await host.OnWorldAsync(() => PlayerbotShipNetwork.Routes(host.World));
        foreach (ShipRoute route in routes)
        {
            output.WriteLine($"{route.Entry} {route.Means} {route.Access}: A map {route.PortA.MapId} {route.PortA.Position}, "
                + $"B map {route.PortB.MapId} {route.PortB.Position}, period {route.PeriodSeconds:0.0} s");
        }

        foreach ((uint entry, uint[] maps) in s_routes)
        {
            ShipRoute route = Assert.Single(routes, r => r.Entry == entry);
            Assert.Equal(PlayerbotShipNetwork.Classic[entry].Means, route.Means);
            Assert.Equal(PlayerbotShipNetwork.Classic[entry].Access, route.Access);
            Assert.Equal(maps.Order(), new[] { route.PortA.MapId, route.PortB.MapId }.Order());
            Assert.True(route.PortA.DistanceTo(route.PortB) > 100f || route.PortA.MapId != route.PortB.MapId,
                $"{entry}: the two ports are {route.PortA.DistanceTo(route.PortB):0.0} yd apart");
            Assert.True(route.PeriodSeconds is > 60f and < 1200f, $"{entry}: period {route.PeriodSeconds} s");
        }

        Assert.Equal(s_routes.Length, routes.Count);
        Assert.True(PlayerbotShipNetwork.ConnectsMaps(PlayerbotShipNetwork.Filter(routes, alliance: true, new PlayerbotOptions()), 0, 1));
        Assert.True(PlayerbotShipNetwork.ConnectsMaps(PlayerbotShipNetwork.Filter(routes, alliance: false, new PlayerbotOptions()), 0, 1));
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
