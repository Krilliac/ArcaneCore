using System.Buffers.Binary;
using System.IO.Compression;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Packets;

/// <summary>
/// Server packet builders for opcodes whose 1.12 body layout a reference states, used by the login flow and the <c>.wiresample</c> diagnostic. Pure
/// functions: no state, any thread.
/// <list type="bullet">
/// <item>SMSG_EXPECTED_SPAM_RECORDS 0x0332: u32 count, then that many NUL-terminated strings (gtker/wow_messages smsg_expected_spam_records.wowm, 1.12; mangos-classic WorldSession::SendExpectedSpamRecords, WorldSession.cpp:856-866).</item>
/// <item>SMSG_CHAT_RESTRICTED 0x02FD: empty body for 1.12 (wow_messages smsg_chat_restricted.wowm; vmangos Chat.cpp:31 ChatRestricted::AppendBodyTo writes nothing).</item>
/// <item>SMSG_PLAY_TIME_WARNING 0x02F5: u32 flag, i32 seconds left (vmangos Misc.cpp:285-289, PlayTimeWarning).</item>
/// <item>SMSG_SPLINE_MOVE_START_SWIM 0x030B and SMSG_SPLINE_MOVE_STOP_SWIM 0x030C: packed guid (wow_messages smsg_spline_move_*_swim.wowm, 1.12; mangos-classic Unit.cpp:12397-12399).</item>
/// <item>SMSG_COMPRESSED_MOVES 0x02FB: u32 uncompressed size, then a zlib stream of entries u8 size (opcode and body), u16 opcode, body (wow_messages smsg_compressed_moves.wowm, 1.12; vmangos UpdateData.cpp:190-225 MovementData).</item>
/// </list>
/// </summary>
public static class ProtocolGapPackets
{
    /// <summary>Most bytes of record text one packet carries (a server-side bound so a long configured list cannot outgrow a frame).</summary>
    public const int MaxSpamRecordBytes = 0xF000;

    /// <summary>The 1.12 SMSG_EXPECTED_SPAM_RECORDS body; records that would take the body past <see cref="MaxSpamRecordBytes"/> are left out.</summary>
    public static byte[] ExpectedSpamRecords(IReadOnlyList<string> records)
    {
        int count = 0;
        int bytes = 0;
        foreach (string record in records)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(record) + 1;
            if (bytes + size > MaxSpamRecordBytes)
            {
                break;
            }

            bytes += size;
            count++;
        }

        var writer = new PacketWriter(4 + bytes);
        writer.WriteUInt32((uint)count);
        for (int i = 0; i < count; i++)
        {
            writer.WriteCString(records[i]);
        }

        return writer.ToArray();
    }

    /// <summary>The 1.12 SMSG_CHAT_RESTRICTED body: empty.</summary>
    public static byte[] ChatRestricted() => [];

    /// <summary>The SMSG_PLAY_TIME_WARNING body: flag, seconds left.</summary>
    public static byte[] PlayTimeWarning(uint flag, int secondsLeft)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(flag);
        writer.WriteInt32(secondsLeft);
        return writer.ToArray();
    }

    /// <summary>The body of SMSG_SPLINE_MOVE_START_SWIM and SMSG_SPLINE_MOVE_STOP_SWIM: the unit's packed guid.</summary>
    public static byte[] SplineMoveSwim(ulong guid)
    {
        var writer = new PacketWriter(9);
        writer.WritePackedGuid(guid);
        return writer.ToArray();
    }

    /// <summary>
    /// The SMSG_COMPRESSED_MOVES body holding one SMSG_SPLINE_SET_RUN_SPEED entry (packed guid, f32 speed): u32 size of the entries, then their zlib stream.
    /// </summary>
    public static byte[] CompressedRunSpeedMove(ulong guid, float speed)
    {
        var entry = new PacketWriter(16);
        entry.WritePackedGuid(guid);
        entry.WriteSingle(speed);
        var moves = new PacketWriter(24);
        moves.WriteByte((byte)(entry.Length + 2)); // opcode and body, not the size byte itself (vmangos MovementData::Add)
        moves.WriteUInt16((ushort)WorldOpcode.SmsgSplineSetRunSpeed);
        moves.WriteBytes(entry.AsSpan());

        using var output = new MemoryStream(moves.Length + 24);
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)moves.Length);
        output.Write(prefix);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(moves.AsSpan());
        }

        return output.ToArray();
    }
}
