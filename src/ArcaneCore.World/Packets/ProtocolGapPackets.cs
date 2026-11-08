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
/// <item>SMSG_SPLINE_MOVE_START_SWIM 0x030B and SMSG_SPLINE_MOVE_STOP_SWIM 0x030C: packed guid (wow_messages smsg_spline_move_*_swim.wowm, 1.12; mangos-classic Unit.cpp:12404 SetSwim).</item>
/// <item>SMSG_COMPRESSED_MOVES 0x02FB: u32 uncompressed size, then a zlib stream of entries u8 size (opcode and body), u16 opcode, body (wow_messages smsg_compressed_moves.wowm, 1.12; vmangos UpdateData.cpp:190-225 MovementData).</item>
/// <item>The layouts below are read from the 1.12.1 client's own packet handlers (Wow.exe, sha256 b4756d38...d2dc7, static check with a disassembler, no live run); each item gives the handler address.</item>
/// <item>SMSG_LOOT_ITEM_NOTIFY 0x0164 (client 0x5EC040): u64 guid, u8, u8, u32, cstring of at most 63 bytes; the client reads it and discards it. WCell LootHandler.cs:305 gives the same field list as a comment.</item>
/// <item>SMSG_STANDSTATE_CHANGE_FAILURE 0x0261 (0x603E00), SMSG_PLAYERBINDERROR 0x01B6 (0x5E38C0), SMSG_CLEAR_FAR_SIGHT_IMMEDIATE 0x020D (0x5E38C0) and SMSG_INVALID_PROMOTION_CODE 0x01E7 (0x48F690): the client reads no body byte, so the body is empty.</item>
/// <item>SMSG_SPELLSTEALLOG 0x0333 (0x5E8C00): packed guid, packed guid, u32 count, count x u32 spell id.</item>
/// <item>SMSG_RWHOIS 0x01FE (0x5ADDE0): u32 count (0xFFFFFFFF and 0 only show a message); per entry cstring name, u32 inner count, u32 flag, inner-count x cstring.</item>
/// <item>SMSG_GROUP_CANCEL 0x0071 (0x5E6770): the client reads one cstring and ignores its text; one NUL byte satisfies the read.</item>
/// <item>SMSG_GAMETIME_SET 0x0045 (0x6C6120): u32 packed game time and nothing else (the client logs an error when bytes are left). SMSG_GAMESPEED_SET 0x0047 (0x6C5DE0): f32 and nothing else.</item>
/// <item>SMSG_ITEM_QUERY_MULTIPLE_RESPONSE 0x0059 (0x555160 into 0x55BDB0): u8 count, then per item u32 entry with bit 31 set meaning "not found" and no further bytes; a found item continues with the item template as in SMSG_ITEM_QUERY_SINGLE_RESPONSE.</item>
/// <item>SMSG_MINIGAME_SETUP 0x02F6 (0x4C4C70, then 0x7285E0): u64 guid, u8 game type (only 1 creates the game object), u8 state, u64, u64, 64 raw bytes, and one more u8 when state is 2. SMSG_MINIGAME_STATE 0x02F7 (0x4C4CF0, object method 0x728700): 9 board bytes and 1 more byte; ignored while no game object exists.</item>
/// <item>Sent although the stock client has no handler on any of its three registration routes (a stock client drops them, so they prove only the bytes): SMSG_NPC_WONT_TALK 0x0181 (u64 guid, u8 1: MangosSharp WS_Creatures.cs:1979), SMSG_CHAT_PLAYER_AMBIGUOUS 0x032D (cstring player: wow_messages smsg_chat_player_ambiguous.wowm, a 2.4.3 layout), and with no layout in any reference, an empty body: SMSG_GHOSTEE_GONE, SMSG_SCRIPT_MESSAGE, SMSG_GAMETIMEBIAS_SET, SMSG_CHARACTER_PROFILE, SMSG_CHARACTER_PROFILE_REALM_CONNECTED, SMSG_MINIGAME_MOVE_FAILED.</item>
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
    public static byte[] PlayTimeWarning(uint flag, int secondsLeft) => ArcaneCore.Game.Entities.PlayTimeLimit.WarningBody(flag, secondsLeft);

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

        return CompressedMoves(moves.AsSpan());
    }

    /// <summary>The SMSG_COMPRESSED_MOVES body for already framed <paramref name="entries"/>: u32 size of the entries, then their zlib stream (vmangos MovementData::BuildPacket).</summary>
    public static byte[] CompressedMoves(ReadOnlySpan<byte> entries)
    {
        using var output = new MemoryStream(entries.Length + 24);
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)entries.Length);
        output.Write(prefix);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(entries);
        }

        return output.ToArray();
    }

    /// <summary>Bit 31 of an entry id in SMSG_ITEM_QUERY_MULTIPLE_RESPONSE: the item was not found (client 0x55BDF7 masks it off and flags the entry).</summary>
    public const uint ItemNotFoundFlag = 0x80000000;

    /// <summary>The body of the packets whose 1.12.1 client handler reads no byte, and of the ones with no layout in any reference: empty.</summary>
    public static byte[] Empty() => [];

    /// <summary>SMSG_LOOT_ITEM_NOTIFY: u64 guid, u8, u8, u32, cstring (client 0x5EC040; the two bytes are named by no source).</summary>
    public static byte[] LootItemNotify(ulong guid, byte first, byte second, uint value, string text)
    {
        var writer = new PacketWriter(16 + text.Length);
        writer.WriteUInt64(guid);
        writer.WriteByte(first);
        writer.WriteByte(second);
        writer.WriteUInt32(value);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    /// <summary>SMSG_SPELLSTEALLOG: packed victim, packed caster, u32 count, count x u32 spell id (client 0x5E8C00).</summary>
    public static byte[] SpellStealLog(ulong victim, ulong caster, IReadOnlyList<uint> spellIds)
    {
        var writer = new PacketWriter(24 + 4 * spellIds.Count);
        writer.WritePackedGuid(victim);
        writer.WritePackedGuid(caster);
        writer.WriteUInt32((uint)spellIds.Count);
        foreach (uint spellId in spellIds)
        {
            writer.WriteUInt32(spellId);
        }

        return writer.ToArray();
    }

    /// <summary>One SMSG_RWHOIS entry: a name, a flag and the strings listed under it.</summary>
    public readonly record struct RWhoisEntry(string Name, uint Flag, IReadOnlyList<string> Lines);

    /// <summary>SMSG_RWHOIS: u32 count, then per entry cstring name, u32 inner count, u32 flag, inner-count x cstring (client 0x5ADDE0).</summary>
    public static byte[] RWhois(IReadOnlyList<RWhoisEntry> entries)
    {
        var writer = new PacketWriter(64);
        writer.WriteUInt32((uint)entries.Count);
        foreach (RWhoisEntry entry in entries)
        {
            writer.WriteCString(entry.Name);
            writer.WriteUInt32((uint)entry.Lines.Count);
            writer.WriteUInt32(entry.Flag);
            foreach (string line in entry.Lines)
            {
                writer.WriteCString(line);
            }
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_GROUP_CANCEL: one NUL byte, the cstring the client reads and ignores (client 0x5E6770).</summary>
    public static byte[] GroupCancel() => [0];

    /// <summary>SMSG_GAMETIME_SET: the u32 packed game time (client 0x6C6120).</summary>
    public static byte[] GameTimeSet(uint packedTime)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(packedTime);
        return writer.ToArray();
    }

    /// <summary>SMSG_GAMESPEED_SET: one f32, game minutes per real second (client 0x6C5DE0).</summary>
    public static byte[] GameSpeedSet(float speed)
    {
        var writer = new PacketWriter(4);
        writer.WriteSingle(speed);
        return writer.ToArray();
    }

    /// <summary>SMSG_ITEM_QUERY_MULTIPLE_RESPONSE answering "not found" for each entry: u8 count, then u32 entry with bit 31 set (client 0x55BDB0).</summary>
    public static byte[] ItemQueryMultipleNotFound(IReadOnlyList<uint> entries)
    {
        var writer = new PacketWriter(1 + 4 * entries.Count);
        writer.WriteByte((byte)entries.Count);
        foreach (uint entry in entries)
        {
            writer.WriteUInt32(entry | ItemNotFoundFlag);
        }

        return writer.ToArray();
    }

    /// <summary>Size of the raw block SMSG_MINIGAME_SETUP carries after its two u64 (client 0x7285E0 reads 0x40 bytes).</summary>
    public const int MinigameSetupBlockBytes = 64;

    /// <summary>SMSG_MINIGAME_SETUP: u64 guid, u8 game type, u8 state, u64, u64, 64 raw bytes, u8 when state is 2 (client 0x4C4C70 and 0x7285E0).</summary>
    public static byte[] MinigameSetup(ulong guid, byte gameType, byte state, ulong firstPlayer, ulong secondPlayer, ReadOnlySpan<byte> block, byte extra)
    {
        var writer = new PacketWriter(96);
        writer.WriteUInt64(guid);
        writer.WriteByte(gameType);
        writer.WriteByte(state);
        writer.WriteUInt64(firstPlayer);
        writer.WriteUInt64(secondPlayer);
        Span<byte> padded = stackalloc byte[MinigameSetupBlockBytes];
        block[..Math.Min(block.Length, MinigameSetupBlockBytes)].CopyTo(padded);
        writer.WriteBytes(padded);
        if (state == 2)
        {
            writer.WriteByte(extra);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_MINIGAME_STATE: nine board bytes and one more byte (client object method 0x728700).</summary>
    public static byte[] MinigameState(ReadOnlySpan<byte> board, byte last)
    {
        var writer = new PacketWriter(10);
        Span<byte> padded = stackalloc byte[9];
        board[..Math.Min(board.Length, 9)].CopyTo(padded);
        writer.WriteBytes(padded);
        writer.WriteByte(last);
        return writer.ToArray();
    }

    /// <summary>SMSG_NPC_WONT_TALK: u64 guid, u8 (MangosSharp WS_Creatures.cs:1979 sends 1). The stock client has no handler for it.</summary>
    public static byte[] NpcWontTalk(ulong guid)
    {
        var writer = new PacketWriter(9);
        writer.WriteUInt64(guid);
        writer.WriteByte(1);
        return writer.ToArray();
    }

    /// <summary>SMSG_CHAT_PLAYER_AMBIGUOUS: cstring player (wow_messages smsg_chat_player_ambiguous.wowm, 2.4.3). The stock 1.12.1 client has no handler for it.</summary>
    public static byte[] ChatPlayerAmbiguous(string player)
    {
        var writer = new PacketWriter(player.Length + 1);
        writer.WriteCString(player);
        return writer.ToArray();
    }
}
