using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Transports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The published static routes (<see cref="PlayerbotTravel.StaticRoutes"/>) are one process-wide value, written by whichever world publishes
/// last; the tests that publish their own and read them back run alone, with no other test class in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TravelStaticRoutesCollection
{
    public const string Name = "TravelStaticRoutes";
}

/// <summary>
/// The home progression's travel seam answered by authentic travel (<see cref="PlayerbotTravelHomeTravel"/>): a home across the sea is
/// reachable without a teleport when a published boat or zeppelin route joins the two places (another map: a route either faction may ride;
/// one map across Teldrassil's water: a route with a port on each side), never with travel or the means switched off; every case the
/// default answers stays the default's, and the return rules (<see cref="IPlayerbotHomeTravel.Decide"/>) are the default's exactly. The
/// world daemon's container resolves the seam to it, over the bound bot options.
/// <para>
/// The routes are published from a transport system built on synthetic paths under the classic entries (<see cref="PlayerbotShipNetwork.Classic"/>):
/// the Booty Bay boat (both factions) and the Orgrimmar - Undercity zeppelin (Horde) between map 0 and map 1, the Auberdine - Rut'theran
/// boat (Alliance) across the water to Teldrassil on map 1, and the Feathermoon boat (Alliance) with both ports on the Kalimdor mainland.
/// </para>
/// </summary>
[Collection(TravelStaticRoutesCollection.Name)]
public sealed class TravelHomeAdapterTests : IDisposable
{
    private const uint BootyBay = 20808;
    private const uint OrgrimmarUndercity = 164871;
    private const uint AuberdineRuttheran = 176244;
    private const uint Feathermoon = 177233;
    private const uint TeldrassilPath = 9201;
    private const uint MainlandPath = 9202;

    /// <summary>Auberdine's side of the water (Darkshore, map 1) and Rut'theran Village's (Teldrassil: x above 8000, y above -200).</summary>
    private static readonly Vector3 Darkshore = new(6450f, 800f, 10f);
    private static readonly Vector3 Teldrassil = new(9000f, 900f, 10f);
    private static readonly Vector3 Elwynn = new(TransportWorldContent.StartX, TransportWorldContent.StartY, TransportWorldContent.StartZ);

    private readonly ServiceProvider _empty = new ServiceCollection().BuildServiceProvider();
    private readonly List<WorldRuntime> _worlds = [];

    public void Dispose()
    {
        // Leave the process-wide routes as a fresh process has them (none), for the tests that run after this collection.
        Publish();
        foreach (WorldRuntime world in _worlds) world.Dispose();
        _empty.Dispose();
    }

    private static TaxiPathNodeCatalog Paths() => new(
    [
        .. TransportWorldContent.Paths().Nodes(TransportWorldContent.CrossingPath),
        // Auberdine (x 6500) to Rut'theran Village (x 8600), both on map 1, a stop at each.
        Node(TeldrassilPath, 0, 6400f, 800f),
        Node(TeldrassilPath, 1, 6500f, 800f, stop: true),
        Node(TeldrassilPath, 2, 7000f, 800f),
        Node(TeldrassilPath, 3, 7500f, 800f),
        Node(TeldrassilPath, 4, 8000f, 800f),
        Node(TeldrassilPath, 5, 8300f, 800f),
        Node(TeldrassilPath, 6, 8600f, 800f, stop: true),
        Node(TeldrassilPath, 7, 8700f, 800f),
        Node(TeldrassilPath, 8, 8800f, 800f),
        // Two ports on the mainland side (x below 8000).
        Node(MainlandPath, 0, 4900f, 800f),
        Node(MainlandPath, 1, 5000f, 800f, stop: true),
        Node(MainlandPath, 2, 5300f, 800f),
        Node(MainlandPath, 3, 5600f, 800f),
        Node(MainlandPath, 4, 6000f, 800f, stop: true),
        Node(MainlandPath, 5, 6100f, 800f),
        Node(MainlandPath, 6, 6200f, 800f),
    ]);

    private static TaxiPathNodeRecord Node(uint path, uint index, float x, float y, bool stop = false)
        => new(path * 100 + index, path, index, 1, x, y, 10f, stop ? 2u : 0u, stop ? 10u : 0u);

    private static TransportTemplate Template(uint entry, uint path)
        => TransportTemplateBuilder.Build(TransportWorldContent.Ship(entry, path), Paths(), _ => false, null, out TransportTemplateError error)
            ?? throw new InvalidOperationException($"synthetic route {entry} refused: {error}");

    private static uint PathOf(uint entry) => entry switch
    {
        AuberdineRuttheran => TeldrassilPath,
        Feathermoon => MainlandPath,
        _ => TransportWorldContent.CrossingPath,
    };

    /// <summary>Publish the routes of a new transport system holding the ships of <paramref name="entries"/> (none: no route at all).</summary>
    private IReadOnlyList<ShipRoute> Publish(params uint[] entries)
    {
        var saves = new CharacterSaveQueue(_empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        _worlds.Add(world);
        TransportSystem.Register(world, new TransportSystem(world, entries.Select(e => Template(e, PathOf(e)))));
        PlayerbotTravel.PublishStaticRoutes(world);
        IReadOnlyList<ShipRoute> published = PlayerbotTravel.StaticRoutes;
        Assert.Equal(entries.Order(), published.Select(r => r.Entry).Order());
        return published;
    }

    private static PlayerbotTravelHomeTravel Adapter(PlayerbotTravelOptions? travel = null)
        => new(new PlayerbotOptions { Travel = travel ?? new PlayerbotTravelOptions() });

    private static bool CrossesToKalimdor(IPlayerbotHomeTravel seam) => seam.CanReachWithoutTeleport(0, Elwynn, 1, Darkshore);

    private static bool CrossesToTeldrassil(IPlayerbotHomeTravel seam) => seam.CanReachWithoutTeleport(1, Darkshore, 1, Teldrassil);

    // --- another map ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AnotherMap_IsReachable_WhenAPublishedRouteJoinsTheMaps_BothWays()
    {
        IReadOnlyList<ShipRoute> routes = Publish(BootyBay);
        Assert.Equal([0u, 1u], new[] { routes[0].PortA.MapId, routes[0].PortB.MapId }.Order());
        PlayerbotTravelHomeTravel adapter = Adapter();

        Assert.True(adapter.CanReachWithoutTeleport(0, Elwynn, 1, Darkshore));
        Assert.True(adapter.CanReachWithoutTeleport(1, Darkshore, 0, Elwynn));
        Assert.False(adapter.CanReachWithoutTeleport(0, Elwynn, 530, Darkshore)); // no route touches map 530
        Assert.False(DefaultHomeTravel.Instance.CanReachWithoutTeleport(0, Elwynn, 1, Darkshore));
    }

    [Fact]
    public void AnotherMap_CountsARouteOnlyOneFactionMayRide()
    {
        // The seam has no faction: the Horde's zeppelin alone joins the maps (an Alliance bot could not ride it, a Horde bot can).
        Publish(OrgrimmarUndercity);
        Assert.True(CrossesToKalimdor(Adapter()));
        Assert.False(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Zeppelins = false })));
        Assert.True(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Boats = false })));
    }

    [Fact]
    public void AnotherMap_IsNotReachable_WithTravelOff_OrBoatsAndZeppelinsOff()
    {
        Publish(BootyBay, OrgrimmarUndercity);
        Assert.True(CrossesToKalimdor(Adapter()));
        Assert.True(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Boats = false })));      // the zeppelin
        Assert.True(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Zeppelins = false })));  // the boat
        Assert.False(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Boats = false, Zeppelins = false })));
        Assert.False(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Enabled = false })));
    }

    [Fact]
    public void AnotherMap_BeforeAnyRouteIsPublished_AnswersAsTheDefault()
    {
        Publish();
        Assert.False(CrossesToKalimdor(Adapter()));
        Assert.False(adapterTo(530));

        static bool adapterTo(uint map) => Adapter().CanReachWithoutTeleport(1, Darkshore, map, Elwynn);
    }

    /// <summary>The adapter reads the options it was given live: a switch turned off after it was made answers at once.</summary>
    [Fact]
    public void TheSwitches_AreReadLive()
    {
        Publish(BootyBay);
        var options = new PlayerbotOptions();
        var adapter = new PlayerbotTravelHomeTravel(options);
        Assert.True(CrossesToKalimdor(adapter));
        options.Travel.Boats = false;
        Assert.False(CrossesToKalimdor(adapter));
        options.Travel.Boats = true;
        options.Travel.Enabled = false;
        Assert.False(CrossesToKalimdor(adapter));
    }

    // --- one map, Teldrassil's water ------------------------------------------------------------------------------------

    [Fact]
    public void Teldrassil_IsReachableFromTheMainland_OnlyByABoatWithAPortOnEachSide()
    {
        Assert.True(ArcaneCore.World.Playerbots.Population.PopulationTownProgression.SeparatedByWater(1, Darkshore.X, Darkshore.Y, 1, Teldrassil.X, Teldrassil.Y));
        Assert.False(CrossesToTeldrassil(DefaultHomeTravel.Instance));

        Publish(Feathermoon); // both ports on the mainland: no crossing
        Assert.False(CrossesToTeldrassil(Adapter()));

        Publish(BootyBay, OrgrimmarUndercity); // other maps' routes: no crossing either
        Assert.False(CrossesToTeldrassil(Adapter()));

        Publish(AuberdineRuttheran, Feathermoon);
        Assert.True(CrossesToTeldrassil(Adapter()));
        Assert.True(Adapter().CanReachWithoutTeleport(1, Teldrassil, 1, Darkshore));
    }

    [Fact]
    public void Teldrassil_IsNotReachable_WithTravelOff_OrBoatsOff()
    {
        Publish(AuberdineRuttheran);
        Assert.True(CrossesToTeldrassil(Adapter()));
        Assert.True(CrossesToTeldrassil(Adapter(new PlayerbotTravelOptions { Zeppelins = false })));
        Assert.False(CrossesToTeldrassil(Adapter(new PlayerbotTravelOptions { Boats = false })));
        Assert.False(CrossesToTeldrassil(Adapter(new PlayerbotTravelOptions { Enabled = false })));
    }

    // --- the Walk return mode -------------------------------------------------------------------------------------------

    /// <summary>
    /// A Walk-mode return never begins a journey (PlayerbotBrain.TryTravelHome), so a home across the sea is one such a bot never reaches:
    /// in Walk mode the adapter answers as the default (no crossing, to another map or to Teldrassil) with every route published and every
    /// switch on; Auto and Teleport cross. The mode is read live, and the default's own yes cases stay yes in Walk mode.
    /// </summary>
    [Fact]
    public void InWalkMode_NoCrossingIsReachable_EvenWithTheRoutesPublished()
    {
        Publish(BootyBay, OrgrimmarUndercity, AuberdineRuttheran);
        var options = new PlayerbotOptions();
        var adapter = new PlayerbotTravelHomeTravel(options);
        foreach (PlayerbotHomeReturnMode crosses in new[] { PlayerbotHomeReturnMode.Auto, PlayerbotHomeReturnMode.Teleport })
        {
            options.Home.ReturnMode = crosses;
            Assert.True(CrossesToKalimdor(adapter), $"{crosses}: no crossing to Kalimdor");
            Assert.True(CrossesToTeldrassil(adapter), $"{crosses}: no crossing to Teldrassil");
        }

        options.Home.ReturnMode = PlayerbotHomeReturnMode.Walk;
        Assert.True(options.Travel.Enabled && options.Travel.Boats && options.Travel.Zeppelins);
        Assert.False(CrossesToKalimdor(adapter));
        Assert.False(adapter.CanReachWithoutTeleport(1, Darkshore, 0, Elwynn));
        Assert.False(CrossesToTeldrassil(adapter));
        Assert.False(adapter.CanReachWithoutTeleport(1, Teldrassil, 1, Darkshore));
        Assert.Equal(DefaultHomeTravel.Instance.CanReachWithoutTeleport(0, Elwynn, 1, Darkshore), CrossesToKalimdor(adapter));
        Assert.Equal(DefaultHomeTravel.Instance.CanReachWithoutTeleport(1, Darkshore, 1, Teldrassil), CrossesToTeldrassil(adapter));

        // The default's own cases are unchanged in Walk mode.
        Assert.True(adapter.CanReachWithoutTeleport(0, Elwynn, 0, Elwynn + new Vector3(3000f, 0f, 0f)));
        Assert.True(adapter.CanReachWithoutTeleport(1, Teldrassil, 1, Teldrassil + new Vector3(200f, 200f, 0f)));

        // Live: back to Auto, the crossings answer again.
        options.Home.ReturnMode = PlayerbotHomeReturnMode.Auto;
        Assert.True(CrossesToKalimdor(adapter) && CrossesToTeldrassil(adapter));
    }

    // --- the default's cases --------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultsCases_AreUnchanged_WithTravelOn_OrOff()
    {
        Publish(BootyBay, AuberdineRuttheran);
        foreach (PlayerbotTravelOptions travel in new[] { new PlayerbotTravelOptions(), new PlayerbotTravelOptions { Enabled = false } })
        {
            PlayerbotTravelHomeTravel adapter = Adapter(travel);
            // One map with no water between: the default's yes.
            Assert.True(adapter.CanReachWithoutTeleport(0, Elwynn, 0, Elwynn + new Vector3(3000f, 0f, 0f)));
            Assert.True(adapter.CanReachWithoutTeleport(1, Darkshore, 1, new Vector3(-618.5f, -4251.7f, 38.7f)));
            Assert.True(adapter.CanReachWithoutTeleport(1, Teldrassil, 1, Teldrassil + new Vector3(200f, 200f, 0f)));
            Assert.True(adapter.CanReachWithoutTeleport(36, Vector3.Zero, 36, Vector3.One));
            Assert.True(DefaultHomeTravel.Instance.CanReachWithoutTeleport(1, Teldrassil, 1, Teldrassil + new Vector3(200f, 200f, 0f)));
        }

        // Only travel's own crossings differ from the default, and only with travel on.
        Assert.True(CrossesToKalimdor(Adapter()) && CrossesToTeldrassil(Adapter()));
        Assert.False(CrossesToKalimdor(Adapter(new PlayerbotTravelOptions { Enabled = false })));
        Assert.False(CrossesToTeldrassil(Adapter(new PlayerbotTravelOptions { Enabled = false })));
    }

    /// <summary>Every return mode, map, distance (the thresholds, water's infinity), area level, blocked walk and teleport distance.</summary>
    [Fact]
    public void Decide_IsTheDefaultsExactly()
    {
        PlayerbotTravelHomeTravel on = Adapter(), off = Adapter(new PlayerbotTravelOptions { Enabled = false });
        var seen = new HashSet<PlayerbotHomeTeleportReason>();
        int cases = 0;
        foreach (PlayerbotHomeReturnMode mode in Enum.GetValues<PlayerbotHomeReturnMode>())
        foreach (bool onMap in new[] { true, false })
        foreach (float distance in new[] { float.NaN, 0f, 300f, 1500f, 1500.5f, 1960f, float.PositiveInfinity })
        foreach ((int areaLevel, int botLevel) in new[] { (0, 14), (19, 14), (20, 14), (30, 1) })
        foreach (bool blocked in new[] { false, true })
        foreach (float teleportAt in new[] { 1500f, 4000f })
        {
            var options = new PlayerbotHomeOptions { ReturnMode = mode };
            var facts = new PlayerbotHomeFacts(onMap, distance, areaLevel, botLevel, blocked);
            PlayerbotHomeTeleportReason expected = DefaultHomeTravel.Instance.Decide(facts, options, teleportAt);
            Assert.Equal(expected, on.Decide(facts, options, teleportAt));
            Assert.Equal(expected, off.Decide(facts, options, teleportAt));
            seen.Add(expected);
            cases++;
        }

        Assert.Equal(672, cases);
        // The grid reaches every reason the rules give from facts (not only None), so the comparison is not vacuous.
        Assert.Superset(new HashSet<PlayerbotHomeTeleportReason>
        {
            PlayerbotHomeTeleportReason.None, PlayerbotHomeTeleportReason.Mode, PlayerbotHomeTeleportReason.OtherMap,
            PlayerbotHomeTeleportReason.Distance, PlayerbotHomeTeleportReason.Danger, PlayerbotHomeTeleportReason.WalkBlocked,
        }, seen);
    }

    // --- the container ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The world daemon's registrations (<see cref="WorldServiceCollectionExtensions.AddWorldDaemon"/>) resolve the seam to the adapter, one
    /// instance, over the bound <c>World:Playerbots:Travel</c> options: with travel switched off in the configuration the crossing is refused.
    /// </summary>
    [Fact]
    public void TheWorldDaemon_ResolvesTheSeamToTheAdapter_OverTheBoundOptions()
    {
        Publish(BootyBay);
        using ServiceProvider on = Daemon(new Dictionary<string, string?>());
        IPlayerbotHomeTravel seam = on.GetRequiredService<IPlayerbotHomeTravel>();
        Assert.IsType<PlayerbotTravelHomeTravel>(seam);
        Assert.Same(seam, on.GetRequiredService<IPlayerbotHomeTravel>());
        Assert.IsType<PlayerbotTravel>(on.GetRequiredService<IPlayerbotTravel>());
        Assert.True(CrossesToKalimdor(seam));

        using ServiceProvider off = Daemon(new Dictionary<string, string?> { ["World:Playerbots:Travel:Enabled"] = "false" });
        Assert.False(CrossesToKalimdor(off.GetRequiredService<IPlayerbotHomeTravel>()));
        using ServiceProvider noShips = Daemon(new Dictionary<string, string?>
        {
            ["World:Playerbots:Travel:Boats"] = "false", ["World:Playerbots:Travel:Zeppelins"] = "false",
        });
        Assert.False(CrossesToKalimdor(noShips.GetRequiredService<IPlayerbotHomeTravel>()));
    }

    private static ServiceProvider Daemon(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddWorldDaemon(configuration);
        return services.BuildServiceProvider();
    }
}
