using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>
/// SMSG_MONSTER_MOVE intermediate offsets and the walk/run mode toggle, asserted against bytes derived by hand from the
/// retail layout (vmangos Movement/spline/packet_builder.cpp:77-111, MoveSplineInit.cpp:100-182), NOT against the production
/// unpacker. Unit-verified against the reference bytes only; a 1.12.1 client run is still outstanding.
/// </summary>
public sealed class MonsterMovePacketFidelityTests
{
    /// <summary>The u32 after the destination: the offsets of the intermediate points, read as raw words.</summary>
    private static (uint Count, Vector3 Destination, uint[] Words) ReadRaw(byte[] payload, int expectedWords)
    {
        var r = new PacketReader(payload);
        r.ReadPackedGuid();
        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // start
        r.ReadUInt32();                                  // spline id
        Assert.Equal((byte)MonsterMoveType.Normal, r.ReadByte());
        r.ReadUInt32();                                  // flags
        r.ReadUInt32();                                  // duration
        uint count = r.ReadUInt32();
        var destination = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var words = new uint[expectedWords];
        for (int i = 0; i < expectedWords; i++)
        {
            words[i] = r.ReadUInt32();
        }

        return (count, destination, words);
    }

    private static byte[] Build(Vector3[] points, MonsterMoveOffsetBase? offsetBase = null)
    {
        var guid = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, 7);
        return offsetBase is { } b
            ? CreatureMovePackets.BuildPath(guid, Vector3.Zero, 9, SplineFacing.None, run: true, 4000, points, b)
            : CreatureMovePackets.BuildPath(guid, Vector3.Zero, 9, SplineFacing.None, run: true, 4000, points);
    }

    [Fact]
    public void IntermediateOffsets_AreDestinationMinusPoint_AsInTheReferences()
    {
        // destination (20,20,6); first intermediate (10,4,2): offset (10,16,4) = quarter yards (40,64,16).
        uint expected = 40u | (64u << 11) | (16u << 22);

        (uint count, Vector3 destination, uint[] words) = ReadRaw(Build([new(10, 4, 2), new(20, 20, 6)]), 1);

        Assert.Equal(2u, count);
        Assert.Equal(new Vector3(20, 20, 6), destination);
        Assert.Equal(expected, words[0]);
    }

    [Fact]
    public void ThreeIntermediatePoints_EachMeasuredFromTheDestination()
    {
        // destination (8,0,0); points (0,0,0)->offset (8,0,0)=32,0,0 ; (4,-2,1)->(4,2,-1)=16,8,-4 (10-bit two's complement).
        uint first = 32u;
        uint second = 16u | (8u << 11) | ((uint)(-4 & 0x3FF) << 22);

        (uint count, _, uint[] words) = ReadRaw(Build([new(0.0f, 0, 0), new(4, -2, 1), new(8, 0, 0)]), 2);

        Assert.Equal(3u, count);
        Assert.Equal(first, words[0]);
        Assert.Equal(second, words[1]);
    }

    [Theory]
    [InlineData(19.9f, 20f, 6.1f, 1u)]   // offset (0.1,0,-0.1): z < 0 -> +0.51 = 0.41 yd = 1 quarter
    [InlineData(19.9f, 20f, 5.9f, 1u)]   // offset (0.1,0,+0.1): z >= 0 -> +0.26 = 0.36 yd = 1 quarter
    [InlineData(20f, 20f, 6f, 1u)]       // offset zero: the client freezes on a zero offset -> +0.26
    public void TinyOffsets_GetTheVmangosZAdjustment_SoNoZeroOffsetIsEverSent(float x, float y, float z, uint zQuarters)
    {
        (_, _, uint[] words) = ReadRaw(Build([new(x, y, z), new(20, 20, 6)]), 1);

        Assert.Equal(zQuarters << 22, words[0]);
    }

    [Fact]
    public void TheLegacyMidpointSwitch_ReproducesTheOldLayout()
    {
        // start (0,0,0), destination (20,20,6): middle (10,10,3); point (10,4,2): middle-point = (0,6,1) = (0,24,4) quarters.
        uint expected = (24u << 11) | (4u << 22);

        (_, _, uint[] words) = ReadRaw(Build([new(10, 4, 2), new(20, 20, 6)], MonsterMoveOffsetBase.Midpoint), 1);

        Assert.Equal(expected, words[0]);
    }

    [Fact]
    public void TheOptionDefaultsToTheRetailLayout_AndBinds()
    {
        Assert.Equal(MonsterMoveOffsetBase.Destination, new CreatureOptions().Movement.MonsterMoveOffsetBase);
    }

    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf, FakeSession Session) Setup()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        (Player _, FakeSession session) = AddPlayer(runtime, 1, 0, 0);
        return (runtime, system, Assert.Single(system.Creatures), session);
    }

    private static ulong ParseToggle(byte[] payload)
    {
        var r = new PacketReader(payload);
        ulong guid = r.ReadPackedGuid();
        Assert.Equal(0, r.Remaining); // the body is the packed guid and nothing else (smsg_spline_move_set_walk_mode.wowm)
        return guid;
    }

    [Fact]
    public void LaunchingAWalkSpline_SendsSetWalkModeOnceBeforeTheMonsterMove_AndSetsTheFlag()
    {
        (WorldRuntime world, _, Creature wolf, FakeSession session) = Setup();
        using WorldRuntime w = world;
        Assert.False(wolf.Movement.HasFlag(MovementFlags.WalkMode));

        wolf.Motion.MovePoint(1, 14, 0, 83.5f, run: false);
        wolf.Motion.MovePoint(2, 18, 0, 83.5f, run: false);

        Assert.True(wolf.Movement.HasFlag(MovementFlags.WalkMode));
        List<WorldOpcode> order = [.. session.Sent.Select(p => p.Opcode)];
        Assert.Equal(1, order.Count(o => o == WorldOpcode.SmsgSplineMoveSetWalkMode));
        Assert.Equal(2, order.Count(o => o == WorldOpcode.SmsgMonsterMove));
        Assert.True(order.IndexOf(WorldOpcode.SmsgSplineMoveSetWalkMode) < order.IndexOf(WorldOpcode.SmsgMonsterMove));
        Assert.Equal(wolf.Guid.Value, ParseToggle(Packets(session, WorldOpcode.SmsgSplineMoveSetWalkMode).Single()));
    }

    [Fact]
    public void LaunchingARunSpline_AfterWalking_SendsSetRunMode_AndClearsTheFlag()
    {
        (WorldRuntime world, _, Creature wolf, FakeSession session) = Setup();
        using WorldRuntime w = world;
        wolf.Motion.MovePoint(1, 14, 0, 83.5f, run: false);
        session.Clear();

        wolf.Motion.MovePoint(2, 18, 0, 83.5f, run: true);

        Assert.False(wolf.Movement.HasFlag(MovementFlags.WalkMode));
        Assert.Equal(wolf.Guid.Value, ParseToggle(Packets(session, WorldOpcode.SmsgSplineMoveSetRunMode).Single()));
        Assert.Empty(Packets(session, WorldOpcode.SmsgSplineMoveSetWalkMode));

        session.Clear();
        wolf.Motion.MovePoint(3, 22, 0, 83.5f, run: true); // already running: no second toggle
        Assert.Empty(Packets(session, WorldOpcode.SmsgSplineMoveSetRunMode));
    }
}
