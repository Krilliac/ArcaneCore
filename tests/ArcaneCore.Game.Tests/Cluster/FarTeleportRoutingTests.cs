using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Death;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Cluster;

/// <summary>
/// S5d: with a <see cref="IClusterFarTeleportRouter"/> set, every teleport source (they all end in <see cref="TeleportService.TeleportTo(Player, uint, float, float, float, float, TeleportOptions)"/>)
/// to a map another worker owns starts one transfer and sends no local loading-screen packet; an owned destination never reaches the router.
/// </summary>
public sealed class FarTeleportRoutingTests
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(33, 0, MapType.Instance, 0, 5, 0, -1, 0, 0, "Dungeon", ""),
        ],
        [new AreaTemplate(12, 0, 0, 0, 0, 0, "Elwynn", 0, 0)],
        [], [], []);

    private sealed class RecordingRouter : IClusterFarTeleportRouter
    {
        public List<(Player Player, TeleportDestination Destination, TeleportOptions Options)> Calls { get; } = [];

        public bool Accept { get; set; } = true;

        public bool TryBeginTransfer(Player player, TeleportDestination destination, TeleportOptions options)
        {
            Calls.Add((player, destination, options));
            return Accept;
        }
    }

    private static (WorldRuntime World, TeleportService Teleports, Player Player, FakeSession Session, RecordingRouter Router) Setup(string owned, bool allowInstanceMaps = true)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Assert.True(MapOwnership.TryParse(owned, allowInstanceMaps: allowInstanceMaps, out MapOwnership ownership, out string? error), error);
        world.Ownership = ownership;
        WorldMaps.Of(world).Load(Content);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        world.PlayerLoggingOut += teleports.Forget;
        var router = new RecordingRouter();
        teleports.ClusterFarRouter = router;
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        session.Clear();
        return (world, teleports, player, session, router);
    }

    [Fact]
    public void TeleportToAnUnownedMap_CallsTheRouterOnce_AndSendsNoLocalTransferPacket()
    {
        (WorldRuntime world, TeleportService teleports, Player player, FakeSession session, RecordingRouter router) = Setup("0");
        using (world)
        {
            Assert.True(teleports.CanTeleportTo(player, 1, 1000, 2000, 30, 0));
            Assert.True(teleports.TeleportTo(player, 1, 1000, 2000, 30, 0));
            world.RunTick(50);

            (Player who, TeleportDestination destination, _) = Assert.Single(router.Calls);
            Assert.Same(player, who);
            Assert.Equal(new TeleportDestination(1, 1000, 2000, 30, 0), destination);
            Assert.Same(world.FindMap(0), player.Map);
            Assert.Null(teleports.StageOf(player));
            Assert.Null(world.FindMap(1));
            Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgTransferPending or WorldOpcode.SmsgNewWorld);
        }
    }

    [Fact]
    public void HearthstoneToAnUnownedBindMap_CallsTheRouterOnce()
    {
        (WorldRuntime world, TeleportService teleports, Player player, _, RecordingRouter router) = Setup("0");
        using (world)
        {
            player.Home = new HomeBind(1, 0, 1000, 2000, 30);

            Assert.True(teleports.TeleportToHomebind(player));

            TeleportDestination destination = Assert.Single(router.Calls).Destination;
            Assert.Equal(1u, destination.MapId);
        }
    }

    [Fact]
    public void TheOptionsOfTheRequest_ReachTheRouter()
    {
        (WorldRuntime world, TeleportService teleports, Player player, _, RecordingRouter router) = Setup("0");
        using (world)
        {
            Assert.True(teleports.TeleportTo(player, 1, 1000, 2000, 30, 0, TeleportOptions.NotLeaveTransport));

            Assert.Equal(TeleportOptions.NotLeaveTransport, Assert.Single(router.Calls).Options);
        }
    }

    [Fact]
    public void AnOwnedDestination_NeverCallsTheRouter()
    {
        (WorldRuntime world, TeleportService teleports, Player player, _, RecordingRouter router) = Setup("0");
        using (world)
        {
            Assert.True(teleports.TeleportTo(player, 0, 500, 0, 90, 0)); // same map, near
            world.RunTick(50);
            Assert.True(teleports.TeleportTo(player, 0, 600, 0, 90, 0, TeleportOptions.ForceFar)); // same map id, far path
            world.RunTick(50);

            Assert.Empty(router.Calls);
        }
    }

    [Fact]
    public void ARefusedTransfer_FailsTheTeleportAndChangesNothing()
    {
        (WorldRuntime world, TeleportService teleports, Player player, FakeSession session, RecordingRouter router) = Setup("0");
        using (world)
        {
            router.Accept = false;

            Assert.False(teleports.TeleportTo(player, 1, 1000, 2000, 30, 0));
            world.RunTick(50);

            Assert.Single(router.Calls);
            Assert.Same(world.FindMap(0), player.Map);
            Assert.Null(teleports.StageOf(player));
            Assert.Empty(session.Sent);
        }
    }

    [Fact]
    public void WithoutARouter_AnUnownedDestinationIsStillRefused()
    {
        (WorldRuntime world, TeleportService teleports, Player player, _, RecordingRouter router) = Setup("0");
        using (world)
        {
            teleports.ClusterFarRouter = null;

            Assert.False(teleports.CanTeleportTo(player, 1, 1000, 2000, 30, 0));
            Assert.False(teleports.TeleportTo(player, 1, 1000, 2000, 30, 0));
            Assert.Empty(router.Calls);
        }
    }

    [Fact]
    public void ADungeonAnotherWorkerOwns_CallsTheRouterOnce()
    {
        // allowInstanceMaps false: this worker owns map 0 only, so an instance copy of map 33 is remote (Owns(33, 1) is false).
        (WorldRuntime world, TeleportService teleports, Player player, FakeSession session, RecordingRouter router) = Setup("0", allowInstanceMaps: false);
        using (world)
        {
            Assert.True(teleports.TeleportTo(player, 33, 10, 20, 30, 0));
            world.RunTick(50);

            Assert.Equal(33u, Assert.Single(router.Calls).Destination.MapId);
            Assert.Same(world.FindMap(0), player.Map);
            Assert.Null(teleports.StageOf(player));
            Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgTransferPending or WorldOpcode.SmsgNewWorld);
        }
    }

    [Fact]
    public void ADungeonThisWorkerOwnsThroughInstanceMaps_NeverCallsTheRouter()
    {
        (WorldRuntime world, TeleportService teleports, Player player, _, RecordingRouter router) = Setup("0", allowInstanceMaps: true);
        using (world)
        {
            Assert.True(world.Owns(33, 1));

            teleports.TeleportTo(player, 33, 10, 20, 30, 0);

            Assert.Empty(router.Calls);
        }
    }

    [Fact]
    public void ASpiritReleasedToAGraveyardOnAnUnownedMap_CallsTheRouterOnce()
    {
        (WorldRuntime world, TeleportService teleports, Player player, _, RecordingRouter router) = Setup("0");
        using (world)
        {
            DeathHooks.Register(world, new DeathHooks(new DeathOptions(), new FixedDeathClock(1_700_000_000)));
            var graveyard = new WorldSafeLoc(1, 1, 1000, 2000, 30, 1f, "loc1");
            WorldGraveyards.Of(world).Load(new GraveyardContent([graveyard], [new GraveyardLink(1, 12, 0)]), null);
            var repop = new GraveyardRepopService(world, () => teleports);

            Assert.True(repop.RepopAtGraveyard(player));

            TeleportDestination destination = Assert.Single(router.Calls).Destination;
            Assert.Equal(new TeleportDestination(1, 1000, 2000, 30, 1f), destination);
        }
    }
}
