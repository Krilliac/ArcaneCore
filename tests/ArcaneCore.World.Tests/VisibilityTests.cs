using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Two clients in one map, end to end: they see each other enter, move, leave range, come
/// back, and disconnect.
/// </summary>
public sealed class VisibilityTests
{
    // In-memory start position (Northshire).
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;

    /// <summary>
    /// vmangos regenerates energy for every player (Player.cpp:2283), so these warrior test characters send a short burst of hidden
    /// UNIT_FIELD_POWER4 updates after login. The tests are about visibility and movement relay: they skip exactly that packet and
    /// still assert every other one.
    /// </summary>
    private static void IgnoreEnergyRegen(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            client.IgnoreEnergyRegenUpdates = true;
        }
    }

    [Fact]
    public async Task TwoPlayers_SeeEachOtherEnterAndMove()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("PLAYERA", "Aaa");
        await using WorldTestClient b = await host.EnterWorldAsync("PLAYERB", "Bbb");
        IgnoreEnergyRegen(a, b);

        // B's arrival is pushed to A, and A is pushed to B (UPDATETYPE_CREATE_OBJECT).
        AssertCreate(await a.ReadUpdateAsync(), guid: 2);
        AssertCreate(await b.ReadUpdateAsync(), guid: 1);

        // A moves: B receives the relay = packed GUID of A + movement stamped with server time.
        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, BuildMovement(StartX + 1, StartY, 83.5f, 0.5f, clientTime: 50));
        (WorldOpcode op, byte[] relay) = await b.ReadAsync();
        uint now = host.World.NowMs;

        Assert.Equal(WorldOpcode.MsgMoveHeartbeat, op);
        Assert.Equal([0x01, 0x01], relay[..2]); // packed guid of A
        var reader = new PacketReader(relay.AsSpan(2));
        MovementInfo movement = MovementInfo.Read(ref reader);
        Assert.Equal(StartX + 1, movement.X);
        Assert.InRange((long)movement.Time, Math.Max(0, (long)now - 5000), now); // server receive time (vmangos stime)
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public async Task PlayerLeaving_DestroysObjectForOthers()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("PLAYERA", "Aaa");
        WorldTestClient b = await host.EnterWorldAsync("PLAYERB", "Bbb");
        IgnoreEnergyRegen(a, b);
        await a.ReadUpdateAsync(); // B's create to A
        await b.ReadUpdateAsync(); // A's create to B

        await b.DisposeAsync();

        (WorldOpcode op, byte[] payload) = await a.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgDestroyObject, op);
        Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(payload));
    }

    [Fact]
    public async Task WalkingOutOfRange_RemovesAndWalkingBack_RecreatesBothWays()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("PLAYERA", "Aaa");
        await using WorldTestClient b = await host.EnterWorldAsync("PLAYERB", "Bbb");
        IgnoreEnergyRegen(a, b);
        await a.ReadUpdateAsync();
        await b.ReadUpdateAsync();

        // A walks 200 yards east: B still gets this heartbeat (A was visible when it arrived),
        // then the end-of-tick visibility pass removes each from the other.
        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, BuildMovement(StartX, StartY + 200, 83.5f, 0f));

        Assert.Equal(WorldOpcode.MsgMoveHeartbeat, (await b.ReadAsync()).Opcode);
        AssertOutOfRange(await b.ReadUpdateAsync(), guid: 1);
        AssertOutOfRange(await a.ReadUpdateAsync(), guid: 2);

        // While out of range nothing about A reaches B.
        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, BuildMovement(StartX, StartY + 190, 83.5f, 0f));
        await b.AssertSilentAsync(TimeSpan.FromMilliseconds(150));

        // Back in range: both are re-created; later movement is relayed again.
        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, BuildMovement(StartX, StartY + 10, 83.5f, 0f));
        AssertCreate(await b.ReadUpdateAsync(), guid: 1);
        AssertCreate(await a.ReadUpdateAsync(), guid: 2);

        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, BuildMovement(StartX, StartY + 11, 83.5f, 0f));
        Assert.Equal(WorldOpcode.MsgMoveHeartbeat, (await b.ReadAsync()).Opcode);
    }

    [Fact]
    public async Task SelfCreate_IsCompressedAboveThreshold()
    {
        // vmangos compresses update packets over 128 bytes; a player create is far larger.
        await using var host = WorldTestHost.Start(compressionThreshold: 128);
        byte[] key = await host.AddAccountAsync("PLAYERC");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("PLAYERC", key);
        await client.CreateCharacterAsync("Ccc");

        var login = new PacketWriter(8);
        login.WriteUInt64(1);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgLoginSettimespeed); // the self create follows the time speed

        (WorldOpcode op, byte[] payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCompressedUpdateObject, op);
        CharacterLifecycleTests.AssertSelfCreateBlock(WorldTestClient.Inflate(payload), guid: 1);
    }

    internal static byte[] BuildMovement(float x, float y, float z, float o, uint clientTime = 50)
    {
        var info = new MovementInfo { Flags = MovementFlags.None, Time = clientTime, X = x, Y = y, Z = z, Orientation = o };
        var writer = new PacketWriter(28);
        info.Write(writer);
        return writer.ToArray();
    }

    private static void AssertCreate(byte[] body, byte guid)
    {
        // One block, or two when a hidden energy regen update (vmangos Player.cpp:2283) rides in the same flush after the create.
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(body);
        Assert.InRange(blocks, 1u, 2u);
        if (blocks == 2)
        {
            Assert.True(EndsWithEnergyRegenBlock(body), "the second block of the packet is not a UNIT_FIELD_POWER4-only values update");
        }

        Assert.Equal((byte)ObjectUpdateType.CreateObject, body[5]);
        Assert.Equal([0x01, guid], body[6..8]);
        Assert.Equal(TypeId.Player, body[8]);
        Assert.Equal((byte)(ObjectUpdateFlags.All | ObjectUpdateFlags.Living | ObjectUpdateFlags.HasPosition), body[9]); // no SELF
    }

    /// <summary>The body ends with a values block of one packed guid whose only changed field is UNIT_FIELD_POWER1 + energy.</summary>
    private static bool EndsWithEnergyRegenBlock(byte[] body)
    {
        int energy = UpdateFields.UnitFieldPower1 + (int)PowerType.Energy;
        for (int start = body.Length - 8; start >= 10; start--)
        {
            if (body[start] != (byte)ObjectUpdateType.Values || body[start + 1] != 0x01 || body[start + 2] is not (1 or 2))
            {
                continue;
            }

            int maskBlocks = body[start + 3];
            if (maskBlocks <= (energy >> 5) || start + 4 + (maskBlocks * 4) + 4 != body.Length)
            {
                continue;
            }

            bool onlyEnergy = true;
            for (int i = 0; i < maskBlocks; i++)
            {
                uint mask = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start + 4 + (i * 4)));
                onlyEnergy &= mask == (i == (energy >> 5) ? 1u << (energy & 31) : 0u);
            }

            if (onlyEnergy)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertOutOfRange(byte[] body, byte guid)
    {
        // blockCount=1, hasTransport=0, UPDATETYPE_OUT_OF_RANGE_OBJECTS=4, count=1, packed guid.
        Assert.Equal([1, 0, 0, 0, 0, 4, 1, 0, 0, 0, 0x01, guid], body);
    }
}
