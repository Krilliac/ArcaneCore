using System.Buffers.Binary;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Spell packet layouts (vmangos PacketsSpell / Spell.cpp; gtker wow_messages where they agree).</summary>
public sealed class SpellPacketTests
{
    [Fact]
    public void InitialSpells_HasSpecByte_SpellsWithZeroSlot_AndCooldowns()
    {
        byte[] packet = SpellPackets.BuildInitialSpells([78, 6603], [new InitialSpellCooldown(78, 0, 5, 1500, 0)]);

        Assert.Equal(1 + 2 + (2 * 4) + 2 + 14, packet.Length);
        Assert.Equal(0, packet[0]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1)));
        Assert.Equal(78, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(5)));
        Assert.Equal(6603, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(7)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(11)));
        Assert.Equal(78, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(13)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(17)));
        Assert.Equal(1500u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(19)));
    }

    [Fact]
    public void InitialSpells_Empty_MatchesTheOldStaticPacket()
        => Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, SpellPackets.BuildInitialSpells([], []));

    [Fact]
    public void CastResult_SuccessIsStatusOnly_FailureCarriesTheReasonAndItsArgument()
    {
        Assert.Equal(new byte[] { 100, 0, 0, 0, 0 }, SpellPackets.BuildCastResult(100, SpellCastResult.CastOk));
        Assert.Equal(new byte[] { 100, 0, 0, 0, 2, (byte)SpellCastResult.NotReady }, SpellPackets.BuildCastResult(100, SpellCastResult.NotReady));

        byte[] focus = SpellPackets.BuildCastResult(100, SpellCastResult.RequiresSpellFocus, 4);
        Assert.Equal(10, focus.Length);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(focus.AsSpan(6)));
    }

    [Fact]
    public void SpellCooldown_IsGuidThenSpellMsPairs()
    {
        byte[] packet = SpellPackets.BuildSpellCooldown(ObjectGuid.Player(7), [(133, 1500), (116, 0)]);
        Assert.Equal(8 + 16, packet.Length);
        Assert.Equal(7ul, BinaryPrimitives.ReadUInt64LittleEndian(packet));
        Assert.Equal(133u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Assert.Equal(1500u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)));
        Assert.Equal(116u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
    }

    [Fact]
    public void SpellFailure_AndFailedOther_AndAuraDuration_Layouts()
    {
        byte[] failure = SpellPackets.BuildSpellFailure(ObjectGuid.Player(2), 101, SpellCastResult.Interrupted);
        Assert.Equal(13, failure.Length);
        Assert.Equal((byte)SpellCastResult.Interrupted, failure[12]);
        Assert.Equal(12, SpellPackets.BuildSpellFailedOther(ObjectGuid.Player(2), 101).Length);
        Assert.Equal(new byte[] { 3, 0x10, 0x27, 0, 0 }, SpellPackets.BuildUpdateAuraDuration(3, 10000));
        Assert.Equal(new byte[] { 0x10, 0x27, 0, 0 }, SpellPackets.BuildLearnedSpell(10000));
        Assert.Equal(new byte[] { 0x10, 0x27 }, SpellPackets.BuildRemovedSpell(10000));
    }

    [Fact]
    public void SpellStart_PackedGuids_SpellFlagsCastTimeAndTargets()
    {
        byte[] packet = SpellPackets.BuildSpellStart(ObjectGuid.Player(1), ObjectGuid.Player(1), 101, SpellCastFlags.Unknown2, 2000, SpellCastTargets.ForSelf());

        // packed guid of 1 = mask 0x01, byte 0x01 (twice), spell, flags, cast time, target mask 0.
        Assert.Equal(new byte[] { 1, 1, 1, 1, 101, 0, 0, 0, 2, 0, 0xD0, 0x07, 0, 0, 0, 0 }, packet);
    }

    [Fact]
    public void SpellGo_ListsHitsAsFullGuids_AndMisses()
    {
        byte[] packet = SpellPackets.BuildSpellGo(ObjectGuid.Player(1), ObjectGuid.Player(1), 101, SpellCastFlags.Unknown9,
            [ObjectGuid.Player(2)], [(ObjectGuid.Player(3), SpellMissInfo.Resist)], SpellCastTargets.ForUnit(ObjectGuid.Player(2)));

        int offset = 4 + 4 + 2;
        Assert.Equal(1, packet[offset]);
        Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(offset + 1)));
        Assert.Equal(1, packet[offset + 9]);
        Assert.Equal(3ul, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(offset + 10)));
        Assert.Equal((byte)SpellMissInfo.Resist, packet[offset + 18]);
        Assert.Equal((ushort)SpellCastTargetFlags.Unit, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset + 19)));
    }

    [Fact]
    public void CastTargets_RoundTrip_UnitAndDestination()
    {
        var targets = new SpellCastTargets
        {
            Mask = SpellCastTargetFlags.Unit | SpellCastTargetFlags.DestLocation,
            Unit = ObjectGuid.Player(42),
            Dest = (1.5f, -2.25f, 30f),
        };
        var writer = new PacketWriter(32);
        targets.Write(writer);
        var reader = new PacketReader(writer.ToArray());

        SpellCastTargets read = SpellCastTargets.Read(ref reader);

        Assert.Equal(targets.Mask, read.Mask);
        Assert.Equal(targets.Unit, read.Unit);
        Assert.Equal(targets.Dest, read.Dest);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void CastTargets_SelfIsAnEmptyMask()
    {
        var writer = new PacketWriter(2);
        SpellCastTargets.ForSelf().Write(writer);
        Assert.Equal(new byte[] { 0, 0 }, writer.ToArray());
    }
}
