using System.Buffers.Binary;

namespace ArcaneCore.Protocol;

/// <summary>
/// Pure byte logic of the world-socket framing shared by the world session and the gateway. The I/O loops, the header
/// cipher calls and their ordering stay with each caller: the cipher keeps state per byte, so DecryptHeader runs exactly
/// once per frame before <see cref="TryParseClientHeader"/>, and EncryptHeader runs under the caller's send lock.
/// </summary>
public static class WorldFrameCodec
{
    /// <summary>vmangos WorldSocket::handle_input_header rejects sizes outside [4, 0x2800].</summary>
    public const int MaxClientPacketSize = 0x2800;

    /// <summary>The SMSG size field is 16 bits and counts the 2 opcode bytes.</summary>
    public const int MaxServerPayload = ushort.MaxValue - 2;

    /// <summary>
    /// Read the size (big-endian u16, counts the 4 opcode bytes) and raw opcode (little-endian u32) of an already
    /// decrypted CMSG header, and report whether they are in bounds. Run before any payload is allocated. The opcode must fit 16 bits.
    /// The full 32-bit check matches mangos-classic (src/game/Server/WorldSocket.cpp:184, <c>header->cmd >= NUM_MSG_TYPES</c>). vmangos instead
    /// truncates to uint16 through <c>IsDefinitelyBogusOpcode</c> (WorldSocket.cpp:117, Opcodes.h:162) and would accept 0x3CE400DC as 0x00DC.
    /// ArcaneCore rejects deliberately: a genuine 1.12.1 client always sends zero high bits, the only live hits came from a tampered client,
    /// and refusing a malformed header is the safer posture.
    /// </summary>
    public static bool TryParseClientHeader(ReadOnlySpan<byte> header, out ushort size, out uint rawOpcode)
    {
        size = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
        rawOpcode = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(2, 4));
        return !(size < 4 || size > MaxClientPacketSize || rawOpcode > ushort.MaxValue);
    }

    /// <summary>Write the SMSG size field (payload length + 2 opcode bytes, big-endian) at the start of the frame.</summary>
    public static void WriteServerSize(Span<byte> frame, int payloadLength)
        => BinaryPrimitives.WriteUInt16BigEndian(frame[..2], (ushort)(payloadLength + 2));

    /// <summary>Write the SMSG wire opcode (little-endian u16) after the size field.</summary>
    public static void WriteServerOpcode(Span<byte> frame, ushort wireOpcode)
        => BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(2, 2), wireOpcode);
}
