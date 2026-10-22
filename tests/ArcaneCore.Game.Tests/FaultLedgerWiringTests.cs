using System.Text.Json;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.Transports;
using ArcaneCore.Game.Transports;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>The world's catch-and-log sites count into the process-wide fault ledger (unique exception types keep parallel tests apart).</summary>
public sealed class FaultLedgerWiringTests
{
    private sealed class WorldTickProbeException : Exception;

    private sealed class PacketProbeException : Exception;

    private sealed class MapUpdaterProbeException : Exception;

    private sealed class WorldInvokeProbeException : Exception;

    private sealed class PetPersistenceProbeException(string message) : Exception(message);

    private sealed class TransportProbeException(string message) : Exception(message);

    private sealed class CollisionLifecycleProbeException(string message) : Exception(message);

    private sealed class ThrowingSession : IPlayerSession
    {
        public bool Kicked { get; private set; }

        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player) => throw new PacketProbeException();

        public void Kick() => Kicked = true;

        public void OnLoggedOut()
        {
        }
    }

    private sealed class ThrowingMapUpdater : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => throw new MapUpdaterProbeException();

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class ThrowingCollisionLoS : ILineOfSight, ICollisionTileLifecycle
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, bool ignoreM2 = true) => true;

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }

        public bool TryGetObjectHit(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, float modifyDist, out System.Numerics.Vector3 hit)
        {
            hit = default;
            return false;
        }

        public void OnTileLoaded(uint mapId, int tileX, int tileY) => throw new CollisionLifecycleProbeException("los tile load failed");

        public void OnTileUnloaded(uint mapId, int tileX, int tileY)
        {
        }
    }

    private static FaultSnapshot? Find(string site, Type type)
        => FaultLedger.Shared.Snapshot().FirstOrDefault(s => s.Site == site && s.ExceptionType == type.FullName);

    [Fact]
    public void AFailingWorldTickHandler_IsCountedAtTheWorldtickSite_AndTheTickGoesOn()
    {
        using var world = TestWorld.CreateRuntime();
        var seen = new List<uint>();
        world.WorldTick += _ => throw new WorldTickProbeException();
        world.WorldTick += seen.Add;

        world.RunTick(10);
        world.RunTick(10);

        Assert.Equal([10u, 10u], seen);
        FaultSnapshot fault = Assert.IsType<FaultSnapshot>(Find("worldtick", typeof(WorldTickProbeException)));
        Assert.Equal(2, fault.Count);
    }

    [Fact]
    public void APacketHandlerFault_IsCountedAtThePacketHandlingSite_AndKicksThePlayer()
    {
        using var world = TestWorld.CreateRuntime();
        var session = new ThrowingSession();
        world.AddPlayer(TestWorld.CreatePlayer(1, 7, 5, session));

        world.RunTick(50);

        Assert.True(session.Kicked);
        Assert.NotNull(Find("packet.handling", typeof(PacketProbeException)));
    }

    [Fact]
    public async Task MapUpdaterAndWorldInvokeFaults_AreRecordedInFaultLedger()
    {
        using var world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        map.AddUpdater(new ThrowingMapUpdater());

        Task<int> invoked = world.InvokeAsync<int>(() => throw new WorldInvokeProbeException());
        world.RunTick(20);

        await Assert.ThrowsAsync<WorldInvokeProbeException>(() => invoked);
        Assert.NotNull(Find("map.updater", typeof(MapUpdaterProbeException)));
        Assert.NotNull(Find("world.invoke", typeof(WorldInvokeProbeException)));
    }

    [Fact]
    public async Task PersistentPetServiceFault_RecordsFaultAndPersistenceAnomaly()
    {
        const uint CharLow = 991_401;
        var service = new SummonService
        {
            DeletePersistence = (_, _) => throw new PetPersistenceProbeException("db write offline"),
        };
        Player player = TestWorld.CreatePlayer(CharLow, 0f, 0f, new FakeSession());
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "pet/persistence-fault" && n.Subject == $"character {CharLow}");

        service.QueueDeletePet(player);
        await Assert.ThrowsAsync<PetPersistenceProbeException>(() => service.FlushCharacterAsync((int)CharLow));

        Assert.NotNull(Find("pet.persistence", typeof(PetPersistenceProbeException)));
        Assert.True(capture.Any("pet/persistence-fault"), capture.Describe());
    }

    [Fact]
    public void TransportSystemFault_RecordsFaultAndLifecycleAnomaly()
    {
        const uint PrivateEntry = 4_991_001;
        WorldRuntime world = TransportTestKit.ManualWorld();
        TransportTemplate? template = TransportTemplateBuilder.Build(
            TransportTestKit.Ship(PrivateEntry, TransportTestKit.FerryPath),
            TransportTestKit.Paths(),
            _ => false,
            null,
            out _);
        Assert.NotNull(template);
        var system = new TransportSystem(world, [template!]);
        TransportSystem.Register(world, system);
        system.Install();
        system.PassengerBoarded += (_, _) => throw new TransportProbeException("passenger hook failed");

        ShipTransport ship = Assert.IsType<ShipTransport>(system.FindByEntry(PrivateEntry));
        Player player = TestWorld.CreatePlayer(991_402, 100f, 0f, new FakeSession(991_402));
        world.AddPlayer(player);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "transport/lifecycle-fault" && n.Subject == $"transport entry {PrivateEntry}");

        system.OnBoarded(ship, player);

        Assert.NotNull(Find("transport.passenger-handler", typeof(TransportProbeException)));
        Assert.Single(capture.Notices);
    }

    [Fact]
    public void WorldStateDefaultsCorruptJson_RecordsFaultAndCorruptJsonAnomaly()
    {
        const string BadJson = "{not-valid-world-state-json-991403";
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "worldstate/corrupt-json" && n.Subject == "world-state-defaults");
        long before = FaultLedger.Shared.Snapshot()
            .Where(s => s.Site == "worldstate.parse-defaults")
            .Sum(s => s.Count);

        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse(BadJson));

        long after = FaultLedger.Shared.Snapshot()
            .Where(s => s.Site == "worldstate.parse-defaults")
            .Sum(s => s.Count);
        Assert.True(after >= before + 1);
        Assert.True(capture.Any("worldstate/corrupt-json"), capture.Describe());
    }

    [Fact]
    public void MapCollisionAndTerrainManagerFaults_RecordFaultsAndAnomalies()
    {
        using var world = TestWorld.CreateRuntime();
        WorldCollision.Of(world).Install(lineOfSight: new ThrowingCollisionLoS());
        Map map = world.GetMap(0);
        using AnomalyCapture collisionCapture = AnomalyLedger.Shared.Capture(
            n => n.Key == "collision/tile-load-failed" && n.Subject.StartsWith($"map {map.MapId}", StringComparison.Ordinal));

        world.AddPlayer(TestWorld.CreatePlayer(991_404, 10f, 10f, new FakeSession()));

        Assert.NotNull(Find("collision.tile-lifecycle", typeof(CollisionLifecycleProbeException)));
        Assert.True(collisionCapture.Any("collision/tile-load-failed"), collisionCapture.Describe());

        string tempDir = Path.Combine(Path.GetTempPath(), "arcanecore-corrupt-terrain-" + Guid.NewGuid().ToString("N"));
        string mapsDir = Path.Combine(tempDir, "maps");
        Directory.CreateDirectory(mapsDir);
        try
        {
            const uint CorruptMapId = 777;
            const int TileX = 31;
            const int TileY = 31;
            File.WriteAllBytes(Path.Combine(mapsDir, TerrainTile.FileName(CorruptMapId, TileX, TileY)), [1, 2, 3, 4, 5, 6, 7, 8]);
            var terrain = new TerrainManager(tempDir);
            using AnomalyCapture terrainCapture = AnomalyLedger.Shared.Capture(
                n => n.Key == "terrain/tile-corrupt" && n.Subject == $"map {CorruptMapId} tile ({TileX}, {TileY})");
            long before = FaultLedger.Shared.Snapshot()
                .Where(s => s.Site == "terrain.tile-parse" && s.ExceptionType == typeof(InvalidDataException).FullName)
                .Sum(s => s.Count);

            TerrainTile loaded = terrain.For(CorruptMapId).GetTile(TileX, TileY);

            Assert.False(loaded.HasData);
            long after = FaultLedger.Shared.Snapshot()
                .Where(s => s.Site == "terrain.tile-parse" && s.ExceptionType == typeof(InvalidDataException).FullName)
                .Sum(s => s.Count);
            Assert.True(after >= before + 1);
            Assert.Single(terrainCapture.Notices);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
