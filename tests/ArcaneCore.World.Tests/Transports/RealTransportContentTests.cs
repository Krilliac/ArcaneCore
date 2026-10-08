using System.Globalization;
using ArcaneCore.Data;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Game.Transports;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.Transports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Transports;

/// <summary>
/// A fact that needs a real world database (<see cref="WorldVariable"/>: a SQLite world.db after
/// <c>tools/content/refresh-world-content.ps1</c>; the test copies it and never writes the original) and the build-5875 client DBC
/// directory (<see cref="DbcVariable"/>, holding TaxiPathNode.dbc). Without them it is reported Skipped, never a silent pass.
/// </summary>
internal sealed class RealTransportContentFactAttribute : FactAttribute
{
    public const string WorldVariable = "ARCANECORE_TEST_WORLD_DB";
    public const string DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    public RealTransportContentFactAttribute()
    {
        string? world = Environment.GetEnvironmentVariable(WorldVariable);
        string? dbc = Environment.GetEnvironmentVariable(DbcVariable);
        if (string.IsNullOrWhiteSpace(world) || !File.Exists(world)
            || string.IsNullOrWhiteSpace(dbc) || !File.Exists(Path.Combine(dbc, "TaxiPathNode.dbc")))
        {
            Skip = $"{WorldVariable} (a refreshed world.db) and {DbcVariable} (a directory with TaxiPathNode.dbc) are not both set: the real-ship check did NOT run.";
        }
    }
}

/// <summary>
/// The real ships and zeppelins (docs/areas/transports.md) on a copy of a refreshed world database: the world daemon starts with
/// <c>World:Transports:Enabled</c>, builds every continent route from the classic-db <c>gameobject_template</c> type 15 rows, the
/// <c>transports</c> periods and the client's TaxiPathNode.dbc, and, with game time advanced on the manual clock, every ship waits at
/// each port of its route (at the port's TaxiPathNode position, on its map) and the crossings change maps between Kalimdor and the
/// Eastern Kingdoms. Waits are on the ships' own state, tick by tick, never on wall-clock time.
/// </summary>
public sealed class RealTransportContentTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The eight continent routes of classic-db z2815 (vmangos the same), with the maps each docks on.</summary>
    private static readonly (uint Entry, string Route, uint[] Maps)[] s_continentRoutes =
    [
        (20808, "Ratchet and Booty Bay", [0, 1]),
        (164871, "Orgrimmar and Undercity", [0, 1]),
        (175080, "Grom'Gol Base Camp and Orgrimmar", [0, 1]),
        (176231, "Menethil Harbor and Theramore Isle", [0, 1]),
        (176244, "Teldrassil and Auberdine", [1]),
        (176310, "Menethil Harbor and Auberdine", [0, 1]),
        (176495, "Grom'Gol Base Camp and Undercity", [0]),
        (177233, "Forgotton Coast and Feathermoon Stronghold", [1]),
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-real-ships-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A pooled handle still closing; the temp directory is harmless.
        }
    }

    /// <summary>The terrain data root of the live server (maps, vmaps, mmaps), used by the boat ride when set.</summary>
    public const string TerrainVariable = "ARCANECORE_TEST_TERRAIN_DIR";

    /// <summary>A copy of the real world database and the configuration the deploy gives the world for ships.</summary>
    private IConfiguration RealConfiguration(bool transports = true)
    {
        Directory.CreateDirectory(_directory);
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(RealTransportContentFactAttribute.WorldVariable)!, world);
        string taxiPathNodes = Path.Combine(Environment.GetEnvironmentVariable(RealTransportContentFactAttribute.DbcVariable)!, "TaxiPathNode.dbc");
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={world};Pooling=False",
            ["NpcServices:TaxiPathNodeDbcPath"] = taxiPathNodes,
            ["World:Transports:Enabled"] = transports ? "true" : "false",
        }).Build();
    }

    [RealTransportContentFact]
    public async Task TheWorldStartsWithTheRealShips_EachWaitsAtEveryPortOfItsRoute_AndTheCrossingsChangeMaps()
    {
        IConfiguration configuration = RealConfiguration();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(configuration);
            services.AddWorldDatabase(configuration);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is not null, "the transport system is installed");

        (uint Entry, uint Period, uint[] StopFrames, uint[] Maps)[] routes = await host.OnWorldAsync(() => feature.System!.Ships
            .Select(s => (s.Entry, s.Period,
                s.Template.KeyFrames.Where(f => f.IsStopFrame).Select(f => f.Node.Id).ToArray(), // TaxiPathNode ids: a frame's Index counts per spline
                s.Template.MapsUsed.ToArray()))
            .OrderBy(r => r.Entry).ToArray());
        foreach ((uint entry, uint period, uint[] stops, uint[] maps) in routes)
        {
            output.WriteLine($"ship {entry}: period {period} ms, ports at TaxiPathNode {string.Join(",", stops)}, maps {string.Join("/", maps)}");
        }

        foreach ((uint entry, TransportTemplateError error) in feature.Refused)
        {
            output.WriteLine($"refused {entry}: {error}");
        }

        foreach ((uint entry, string route, uint[] maps) in s_continentRoutes)
        {
            (uint Entry, uint Period, uint[] StopFrames, uint[] Maps) ship = Assert.Single(routes, r => r.Entry == entry);
            Assert.True(ship.StopFrames.Length >= 2, $"{route}: {ship.StopFrames.Length} stop frame(s)");
            Assert.Equal(maps, ship.Maps);
        }

        // Advance game time until every continent ship has waited at every port of its route and been on every map of it; a round
        // trip is under six minutes, so two of the longest periods are the bound.
        var visits = s_continentRoutes.ToDictionary(r => r.Entry, _ => new Visits());
        string? misplaced = null;
        uint bound = 2 * routes.Max(r => r.Period);
        bool done = await host.World.AdvanceClockUntilAsync(bound, () =>
        {
            TransportSystem system = feature.System!;
            foreach ((uint entry, Visits seen) in visits)
            {
                ShipTransport ship = system.FindByEntry(entry)!;
                seen.Maps.Add(ship.MapId);
                if (seen.LastMap is { } last && last != ship.MapId)
                {
                    seen.MapChanges.Add($"{last}->{ship.MapId} at {host.World.NowMs.ToString(CultureInfo.InvariantCulture)}");
                }

                seen.LastMap = ship.MapId;
                if (!ship.IsMoving && ship.CurrentFrame.IsStopFrame)
                {
                    TransportKeyFrame port = ship.CurrentFrame;
                    float distance = MathF.Sqrt(Square(ship.X - port.Node.X) + Square(ship.Y - port.Node.Y) + Square(ship.Z - port.Node.Z));
                    if (port.Node.MapId != ship.MapId || !ReferenceEquals(ship.CurrentMap, host.World.FindMap(port.Node.MapId)) || distance > 2f)
                    {
                        misplaced ??= $"ship {entry} waits at TaxiPathNode {port.Node.Id} (map {port.Node.MapId}) on map {ship.MapId}, {distance:0.00} yd from the port";
                    }

                    seen.Ports.Add(port.Node.Id);
                }
            }

            return misplaced is not null || visits.All(v =>
                routes.Single(r => r.Entry == v.Key).StopFrames.All(v.Value.Ports.Contains)
                && routes.Single(r => r.Entry == v.Key).Maps.All(v.Value.Maps.Contains));
        });

        foreach ((uint entry, Visits seen) in visits)
        {
            output.WriteLine($"ship {entry}: ports {string.Join(",", seen.Ports.Order())}, maps {string.Join("/", seen.Maps.Order())}, changes {string.Join("; ", seen.MapChanges)}");
        }

        Assert.Null(misplaced);
        Assert.True(done, $"not every ship reached every port within {bound} ms of game time");
        foreach ((uint entry, string route, uint[] maps) in s_continentRoutes.Where(r => r.Maps.Length > 1))
        {
            Assert.True(visits[entry].MapChanges.Count > 0, $"{route} never changed maps");
        }
    }

    /// <summary>
    /// The shipped <c>ship</c> scenario (<see cref="ShipCrossingScenario"/>) on the real content: a managed bot boards the Ratchet - Booty Bay
    /// boat at a port, crosses to the other continent aboard and steps off at the other port; with <see cref="TerrainVariable"/> set, on the
    /// live server's terrain (heights, liquids, vmaps, navmeshes).
    /// </summary>
    [RealTransportContentFact]
    public async Task ABot_RidesTheRealBootyBayBoat_ToTheOtherContinent_AndStepsOffAtThePort()
    {
        IConfiguration configuration = RealConfiguration();
        string? terrain = Environment.GetEnvironmentVariable(TerrainVariable);
        output.WriteLine(string.IsNullOrWhiteSpace(terrain) ? $"no terrain ({TerrainVariable} unset)" : $"terrain {terrain}");
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            services.AddSingleton(configuration);
            services.AddWorldDatabase(configuration);
        }, runtime =>
        {
            if (!string.IsNullOrWhiteSpace(terrain))
            {
                runtime.Maps.DataDirectory = terrain;
            }
        });

        ScenarioReport report = await world.RunAsync(PlayerbotScenarioCatalog.Find(world.Services, "ship")!, new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5),
        });

        output.WriteLine(report.ToString());
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>
    /// The flight masters' tables the refresh fills from TaxiNodes.dbc and TaxiPath.dbc (the live world had them empty): the world loads
    /// every node, and a player flown from Stormwind (node 2) along the Stormwind - Ironforge path lands at Ironforge (node 6) once the
    /// flight's game time has run.
    /// </summary>
    [RealTransportContentFact]
    public async Task TheRefreshedTaxiTables_FlyAPlayerFromStormwindToIronforge()
    {
        const uint Stormwind = 2, Ironforge = 6;

        // Without the ships: the test client's login expects the synthetic packet order, and a ship of map 0 is sent ahead of it.
        IConfiguration configuration = RealConfiguration(transports: false);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(configuration);
            services.AddWorldDatabase(configuration);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        NpcStore npcs = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.Npcs;
        output.WriteLine($"{npcs.Nodes.Count()} taxi nodes");
        Assert.True(npcs.Nodes.Count() > 50, $"{npcs.Nodes.Count()} taxi nodes");
        TaxiNode from = npcs.Node(Stormwind)!;
        TaxiNode to = npcs.Node(Ironforge)!;
        TaxiPath path = npcs.Path(Stormwind, Ironforge)!;
        output.WriteLine($"{from.Name} -> {to.Name}: path {path.Id}, {path.Price} copper, alliance mount {from.MountAlliance}");

        await host.EnterWorldAsync("FLYER", "Flyer");
        await host.PlaceAsync("Flyer", from.X, from.Y, from.Z);
        TaxiFlightSystem flights = host.WorldServices.GetRequiredService<NpcServicesFeature>().Flights!;
        Assert.True(await host.OnWorldAsync(() => flights.StartFlight(host.World.FindOnlinePlayer("Flyer")!, [Stormwind, Ironforge], [path.Id], from.MountAlliance)),
            "the flight did not start");

        bool landed = await host.World.AdvanceClockUntilAsync(30 * 60 * 1000, () => !flights.IsFlying(host.World.FindOnlinePlayer("Flyer")!));
        (uint map, float distance) = await host.OnWorldAsync(() =>
        {
            Player flyer = host.World.FindOnlinePlayer("Flyer")!;
            return (flyer.MapId, MathF.Sqrt(Square(flyer.X - to.X) + Square(flyer.Y - to.Y)));
        });
        output.WriteLine($"landed {landed} at {host.World.NowMs} ms, map {map}, {distance:0.0} yd from {to.Name}");
        Assert.True(landed, "still flying after 30 minutes of game time");
        Assert.Equal(to.MapId, map);
        Assert.True(distance < 20f, $"{distance:0.0} yd from {to.Name}");
    }

    private static float Square(float value) => value * value;

    private sealed class Visits
    {
        public HashSet<uint> Ports { get; } = [];

        public HashSet<uint> Maps { get; } = [];

        public List<string> MapChanges { get; } = [];

        public uint? LastMap { get; set; }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
