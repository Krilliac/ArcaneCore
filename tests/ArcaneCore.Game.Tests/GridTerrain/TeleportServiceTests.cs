using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>Near and far teleports driven tick by tick (vmangos Player::TeleportTo and the ack handlers).</summary>
public sealed class TeleportServiceTests
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(30, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Alterac Valley", ""),
            new MapTemplate(36, 0, MapType.Instance, 0, 10, 0, 0, -11208f, 1672f, "Deadmines", ""),
        ],
        [], [], [], []);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            World = TestWorld.CreateRuntime();
            WorldMaps.Of(World).Load(Content);
            Teleports = new TeleportService(World, p => Before.Add(p), p => After.Add(p));
            World.PlayerLoggingOut += Teleports.Forget;
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public List<Player> Before { get; } = [];

        public List<Player> After { get; } = [];

        public void Dispose() => World.Dispose();
    }

    private static List<(WorldOpcode Opcode, byte[] Payload)> Drain(FakeSession session)
    {
        var list = new List<(WorldOpcode, byte[])>();
        while (session.Sent.TryDequeue(out var p))
        {
            list.Add(p);
        }

        return list;
    }

    [Fact]
    public void NearTeleport_SendsTheAck_ThenMovesOnTheClientsAck()
    {
        using var f = new Fixture();
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        Player a = TestWorld.CreatePlayer(1, 0, 0, sa);
        Player b = TestWorld.CreatePlayer(2, 10, 0, sb);
        f.World.AddPlayer(a);
        f.World.AddPlayer(b);
        f.World.RunTick(50);
        sa.Clear();
        sb.Clear();

        Assert.True(f.Teleports.TeleportTo(a, 0, 500, 0, 90, 1.5f));
        Assert.True(f.Teleports.IsBeingTeleportedNear(a));

        (WorldOpcode op, byte[] payload) = sa.Next();
        Assert.Equal(WorldOpcode.MsgMoveTeleportAck, op);
        var reader = new PacketReader(payload);
        Assert.Equal(a.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal(0u, reader.ReadUInt32()); // first movement counter
        MovementInfo info = MovementInfo.Read(ref reader);
        Assert.Equal((500f, 0f, 90f, 1.5f), (info.X, info.Y, info.Z, info.Orientation));
        Assert.Equal(0f, a.X); // not moved before the ack

        Assert.False(f.Teleports.HandleTeleportAck(a, b.Guid.Value)); // someone else's GUID
        Assert.True(f.Teleports.HandleTeleportAck(a, a.Guid.Value));
        Assert.Equal((500f, 0f, 90f), (a.X, a.Y, a.Z));
        Assert.False(f.Teleports.IsBeingTeleported(a));
        Assert.False(f.Teleports.HandleTeleportAck(a, a.Guid.Value)); // nothing pending any more

        // B saw A leave (MSG_MOVE_TELEPORT around the old position); the next pass destroys A for B.
        Assert.Equal(WorldOpcode.MsgMoveTeleport, sb.Next().Opcode);
        f.World.RunTick(50);
        Assert.Contains(Drain(sb), p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload[5] == (byte)ObjectUpdateType.OutOfRangeObjects);
        Assert.DoesNotContain(a.Guid, b.VisibleObjects);
    }

    [Fact]
    public void FarTeleport_RunsAfterTheMapUpdate_AndCompletesOnTheWorldportAck()
    {
        using var f = new Fixture();
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        Player a = TestWorld.CreatePlayer(1, 0, 0, sa);
        Player b = TestWorld.CreatePlayer(2, 10, 0, sb);
        f.World.AddPlayer(a);
        f.World.AddPlayer(b);
        f.World.RunTick(50);
        a.Selection = b.Guid;
        f.World.RunTick(50);
        sa.Clear();
        sb.Clear();

        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 2f));
        Assert.True(sa.Sent.IsEmpty); // scheduled for the end of the map update
        Assert.Equal(TeleportStage.FarScheduled, f.Teleports.StageOf(a));

        f.World.RunTick(50);
        List<(WorldOpcode Opcode, byte[] Payload)> sent = Drain(sa);
        int pending = sent.FindIndex(p => p.Opcode == WorldOpcode.SmsgTransferPending);
        int newWorld = sent.FindIndex(p => p.Opcode == WorldOpcode.SmsgNewWorld);
        Assert.True(pending >= 0 && newWorld > pending);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(sent[pending].Payload));
        Assert.Equal(TeleportPackets.BuildNewWorld(1, 1000, 2000, 30, 2f), sent[newWorld].Payload);
        Assert.Null(a.Map);
        Assert.True(a.Selection.IsEmpty);
        Assert.Equal((1u, 1000f, 2000f), (a.MapId, a.X, a.Y)); // a save now stores the destination
        Assert.Contains(Drain(sb), p => p.Opcode == WorldOpcode.SmsgDestroyObject);
        Assert.Equal(TeleportStage.Far, f.Teleports.StageOf(a));
        Assert.False(f.Teleports.TeleportTo(a, 0, 0, 0, 0, 0)); // no second teleport mid-transfer

        Assert.True(f.Teleports.HandleWorldportAck(a));
        Assert.False(f.Teleports.HandleWorldportAck(a)); // a repeated ack is ignored
        f.World.RunTick(50);

        Map kalimdor = f.World.GetMap(1);
        Assert.Same(kalimdor, a.Map);
        Assert.Same(a, kalimdor.FindPlayer(a.Guid));
        Assert.Equal([a], f.Before);
        Assert.Equal([a], f.After);
        Assert.Null(f.World.GetMap(0).FindPlayer(a.Guid));
        Assert.False(f.Teleports.IsBeingTeleported(a));
        Assert.Equal(WorldOpcode.SmsgUpdateObject, sa.Next().Opcode); // self create on the new map
    }

    [Fact]
    public void WorldportAck_WithoutATransfer_IsIgnored()
    {
        using var f = new Fixture();
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        f.World.AddPlayer(a);
        Assert.False(f.Teleports.HandleWorldportAck(a));
        Assert.False(f.Teleports.HandleTeleportAck(a, a.Guid.Value));
    }

    [Theory]
    [InlineData(99u, 0f, 0f, 0f)]     // map not in the registry
    [InlineData(30u, 0f, 0f, 0f)]     // battleground: only through the battleground system
    [InlineData(0u, 20000f, 0f, 0f)]  // outside the map
    [InlineData(0u, 0f, 0f, float.NaN)]
    public void TeleportTo_RefusesInvalidDestinations(uint mapId, float x, float y, float z)
    {
        using var f = new Fixture();
        var session = new FakeSession(1);
        Player a = TestWorld.CreatePlayer(1, 0, 0, session);
        f.World.AddPlayer(a);
        session.Clear();

        Assert.False(f.Teleports.TeleportTo(a, mapId, x, y, z, 0));
        Assert.False(f.Teleports.IsBeingTeleported(a));
        f.World.RunTick(50);
        Assert.Same(f.World.GetMap(0), a.Map);
    }

    [Fact]
    public void LogoutDuringTransfer_ForgetsTheTeleport_AndSavesTheDestination()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        WorldMaps.Of(world).Load(Content);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        world.PlayerLoggingOut += teleports.Forget;
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(a);
        teleports.TeleportTo(a, 1, 1000, 2000, 30, 0);
        world.RunTick(50);

        world.RemovePlayer(a);

        Assert.False(teleports.IsBeingTeleported(a));
        Assert.Equal(0, teleports.PendingCount);
        Assert.Equal(0, world.GetMap(0).TransitCount);
        CharacterStateAssert(saves, 1, 1000, 2000);
    }

    [Fact]
    public void TeleportIntoADungeon_CreatesAnInstanceBinding()
    {
        using var f = new Fixture();
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        f.World.AddPlayer(a);

        Assert.True(f.Teleports.TeleportTo(a, 36, -16.4f, -383.07f, 61.78f, 1.86f));

        uint? instance = WorldMaps.Of(f.World).Instances.BindingOf(a, 36);
        Assert.NotNull(instance);
        Assert.True(instance > InstanceRegistry.ReservedInstancesLast);
        Assert.Equal(36u, WorldMaps.Of(f.World).Instances.MapOf(instance!.Value));
    }

    private static void CharacterStateAssert(RecordingSaveQueue saves, uint mapId, float x, float y)
    {
        var state = Assert.Single(saves.Saved);
        Assert.Equal((mapId, x, y), (state.MapId, state.X, state.Y));
    }
}
