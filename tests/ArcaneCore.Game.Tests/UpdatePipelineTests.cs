using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Update masks, block serialization and per-client batching.</summary>
public sealed class UpdatePipelineTests
{
    [Fact]
    public void UpdateMask_NextSetBit_WalksBitsAcrossBlocks()
    {
        var mask = new UpdateMask(100);
        mask.SetBit(3);
        mask.SetBit(31);
        mask.SetBit(32);
        mask.SetBit(99);

        List<int> bits = [];
        for (int i = mask.NextSetBit(0); i >= 0; i = mask.NextSetBit(i + 1))
        {
            bits.Add(i);
        }

        Assert.Equal([3, 31, 32, 99], bits);
        Assert.Equal(4, mask.BlockCount);

        mask.Clear();
        Assert.True(mask.IsEmpty);
        Assert.Equal(-1, mask.NextSetBit(0));
    }

    [Fact]
    public void ValuesBlock_OwnerSeesPrivateFields_OthersOnlyPublic()
    {
        var sessionA = new FakeSession(1);
        var sessionB = new FakeSession(2);
        Player a = TestWorld.CreatePlayer(1, 0, 0, sessionA);
        Player b = TestWorld.CreatePlayer(2, 0, 0, sessionB);
        a.ClearChangedFields();

        a.Health = 5;                                // UF_FLAG_PUBLIC
        a.SetUInt32(UpdateFields.PlayerXp, 77);      // UF_FLAG_PRIVATE

        var own = new PacketWriter();
        Assert.True(UpdateBlockWriter.TryWriteValuesBlock(own, a, a));
        Assert.Equal(new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 5, [UpdateFields.PlayerXp] = 77 }, ParseValues(own.ToArray()));

        var other = new PacketWriter();
        Assert.True(UpdateBlockWriter.TryWriteValuesBlock(other, a, b));
        Assert.Equal(new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 5 }, ParseValues(other.ToArray()));

        // Only a private field changed: nothing for an observer.
        a.ClearChangedFields();
        a.SetUInt32(UpdateFields.PlayerXp, 78);
        var none = new PacketWriter();
        Assert.False(UpdateBlockWriter.TryWriteValuesBlock(none, a, b));
        Assert.Equal(0, none.Length);
    }

    [Fact]
    public void CreateBlock_SendsGuidFieldsAsPairs_AndPrivateFieldsOnlyToSelf()
    {
        var session = new FakeSession();
        Player a = TestWorld.CreatePlayer(1, 0, 0, session);
        Player b = TestWorld.CreatePlayer(2, 0, 0, new FakeSession(2));
        a.Target = ObjectGuid.Player(2); // low half non-zero, high half zero

        Dictionary<int, uint> self = ParseCreateValues(Create(a, a));
        Dictionary<int, uint> seenByB = ParseCreateValues(Create(a, b));

        // vmangos _SetCreateBits: both halves of a GUID field, even when one is zero.
        Assert.Equal(2u, seenByB[UpdateFields.UnitFieldTarget]);
        Assert.Equal(0u, seenByB[UpdateFields.UnitFieldTarget + 1]);

        // PLAYER_NEXT_LEVEL_XP is private.
        Assert.Equal(400u, self[UpdateFields.PlayerNextLevelXp]);
        Assert.False(seenByB.ContainsKey(UpdateFields.PlayerNextLevelXp));
    }

    [Fact]
    public void Flush_PutsOutOfRangeFirst_CountsItAsOneBlock()
    {
        var data = new UpdateData();
        data.AddOutOfRange(ObjectGuid.Player(7));
        PacketWriter block = data.BeginBlock();
        block.WriteBytes([0xAA, 0xBB]);
        data.EndBlock();

        List<(WorldOpcode, byte[])> sent = [];
        data.Flush((op, payload) => sent.Add((op, payload)), compressionThreshold: 0);

        (WorldOpcode op, byte[] body) = Assert.Single(sent);
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);
        Assert.Equal([2, 0, 0, 0, 0, 4, 1, 0, 0, 0, 0x01, 0x07, 0xAA, 0xBB], body);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void Flush_SplitsBodiesLargerThanTheLimit()
    {
        var data = new UpdateData();
        byte[] chunk = new byte[25000];
        for (int i = 0; i < 3; i++)
        {
            data.BeginBlock().WriteBytes(chunk);
            data.EndBlock();
        }

        List<byte[]> bodies = [];
        data.Flush((_, payload) => bodies.Add(payload), compressionThreshold: 0);

        Assert.Equal(2, bodies.Count); // 2 blocks (50005 bytes) + 1 block
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bodies[0]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bodies[1]));
        Assert.All(bodies, b => Assert.True(b.Length <= UpdateData.MaxBodySize));
    }

    [Fact]
    public void Flush_CompressesAboveThreshold()
    {
        var data = new UpdateData();
        data.BeginBlock().WriteBytes(new byte[500]);
        data.EndBlock();

        List<(WorldOpcode Op, byte[] Payload)> sent = [];
        data.Flush((op, payload) => sent.Add((op, payload)), compressionThreshold: 128);

        (WorldOpcode op, byte[] payload) = Assert.Single(sent);
        Assert.Equal(WorldOpcode.SmsgCompressedUpdateObject, op);
        Assert.Equal(505u, BinaryPrimitives.ReadUInt32LittleEndian(payload)); // uncompressed size prefix
        Assert.True(payload.Length < 505);
    }

    [Fact]
    public void Flush_WithNothingQueued_SendsNothing()
    {
        var data = new UpdateData();
        int sent = 0;
        data.Flush((_, _) => sent++, compressionThreshold: 0);
        Assert.Equal(0, sent);
    }

    private static byte[] Create(WorldObject obj, Player viewer)
    {
        var writer = new PacketWriter();
        UpdateBlockWriter.WriteCreateBlock(writer, obj, viewer, isNewObject: false, serverTimeMs: 1000);
        return writer.ToArray();
    }

    /// <summary>Values block: type, packed guid, mask, values.</summary>
    private static Dictionary<int, uint> ParseValues(byte[] block)
    {
        var reader = new PacketReader(block);
        Assert.Equal((byte)ObjectUpdateType.Values, reader.ReadByte());
        reader.ReadPackedGuid();
        return ReadMaskAndValues(ref reader);
    }

    /// <summary>Create block for a living object: skip the movement block, read the values.</summary>
    private static Dictionary<int, uint> ParseCreateValues(byte[] block)
    {
        var reader = new PacketReader(block);
        reader.ReadByte();       // update type
        reader.ReadPackedGuid();
        reader.ReadByte();       // type id
        reader.ReadByte();       // update flags
        MovementInfo.Read(ref reader);
        reader.Skip(6 * 4);      // speeds
        reader.Skip(4);          // UPDATEFLAG_ALL uint32
        return ReadMaskAndValues(ref reader);
    }

    private static Dictionary<int, uint> ReadMaskAndValues(ref PacketReader reader)
    {
        int blocks = reader.ReadByte();
        uint[] mask = new uint[blocks];
        for (int i = 0; i < blocks; i++)
        {
            mask[i] = reader.ReadUInt32();
        }

        var values = new Dictionary<int, uint>();
        for (int index = 0; index < blocks * 32; index++)
        {
            if ((mask[index >> 5] & (1u << (index & 31))) != 0)
            {
                values[index] = reader.ReadUInt32();
            }
        }

        Assert.Equal(0, reader.Remaining);
        return values;
    }
}
