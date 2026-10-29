using System.Globalization;

namespace ArcaneCore.Protocol;

/// <summary>What a client header rejected by <see cref="WorldFrameCodec.TryParseClientHeader"/> most likely was.</summary>
public enum BadClientHeaderKind
{
    /// <summary>
    /// The size is in bounds and the low 16 bits of the raw opcode name a known opcode: the cipher is most likely in sync and the client wrote
    /// junk above a real opcode. A desynchronised stream lands here by chance about 0.2% of the time.
    /// </summary>
    ClientMalformedOpcode,

    /// <summary>The size is outside [4, <see cref="WorldFrameCodec.MaxClientPacketSize"/>]: what a desynchronised or plaintext stream decrypts to.</summary>
    CipherDesyncOrPlaintext,

    /// <summary>The size is in bounds but the raw opcode does not fit 16 bits and its low 16 bits name no known opcode. This does not rule out a cipher desync.</summary>
    UnknownOpcode,
}

/// <summary>
/// Pure classification and wording of a rejected client header, shared by the world session and the gateway so both log
/// the same labels. Read-only: it never touches the header cipher. The rejection itself is
/// <see cref="WorldFrameCodec.TryParseClientHeader"/>; this class only explains it.
/// </summary>
public static class BadClientHeader
{
    /// <summary>Classify a header that <see cref="WorldFrameCodec.TryParseClientHeader"/> rejected.</summary>
    public static BadClientHeaderKind Classify(ushort size, uint rawOpcode)
    {
        if (size < 4 || size > WorldFrameCodec.MaxClientPacketSize)
        {
            return BadClientHeaderKind.CipherDesyncOrPlaintext;
        }

        return rawOpcode > ushort.MaxValue && IsKnownOpcode(rawOpcode & 0xFFFF)
            ? BadClientHeaderKind.ClientMalformedOpcode
            : BadClientHeaderKind.UnknownOpcode;
    }

    /// <summary>The stable log label of <paramref name="kind"/>.</summary>
    public static string Label(BadClientHeaderKind kind) => kind switch
    {
        BadClientHeaderKind.ClientMalformedOpcode => "client-malformed-opcode",
        BadClientHeaderKind.CipherDesyncOrPlaintext => "cipher-desync-or-plaintext",
        _ => "unknown-opcode",
    };

    /// <summary>
    /// The explanation that goes with the label: the named low opcode for a malformed opcode, whether the raw ciphertext would itself
    /// be a valid plaintext header for a desync (a client that never engaged its cipher), the unrecognised value otherwise.
    /// </summary>
    /// <param name="kind">The result of <see cref="Classify"/> for the same header.</param>
    /// <param name="ciphertext">The header bytes as received, before <c>DecryptHeader</c>.</param>
    /// <param name="rawOpcode">The decrypted raw opcode.</param>
    public static string Describe(BadClientHeaderKind kind, ReadOnlySpan<byte> ciphertext, uint rawOpcode)
    {
        switch (kind)
        {
            case BadClientHeaderKind.ClientMalformedOpcode:
                return string.Create(CultureInfo.InvariantCulture, $"low 16 bits are {WorldOpcodeNames.GetName(rawOpcode & 0xFFFF)} (0x{rawOpcode & 0xFFFF:X4}), high 16 bits 0x{rawOpcode >> 16:X4}");
            case BadClientHeaderKind.CipherDesyncOrPlaintext:
                if (ciphertext.Length >= 6 && WorldFrameCodec.TryParseClientHeader(ciphertext, out ushort size, out uint opcode))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"ciphertext parses as plaintext: yes (size {size}, {WorldOpcodeNames.GetName(opcode)})");
                }

                return "ciphertext parses as plaintext: no";
            default:
                return string.Create(CultureInfo.InvariantCulture, $"opcode 0x{rawOpcode:X8} is not a known opcode");
        }
    }

    // The generated WorldOpcode enum and WorldOpcodeNames are emitted from the same list, so a defined enum member is a named opcode.
    private static bool IsKnownOpcode(uint opcode) => Enum.IsDefined((WorldOpcode)(ushort)opcode);

    /// <summary>
    /// The whole bad-header WARN text, built here so the world session and the gateway log identical fields.
    /// </summary>
    /// <param name="endpoint">The remote endpoint, the line's prefix.</param>
    /// <param name="size">The decrypted size field.</param>
    /// <param name="rawOpcode">The decrypted raw opcode.</param>
    /// <param name="ciphertext">The header bytes as received.</param>
    /// <param name="plain">The header bytes after <c>DecryptHeader</c>.</param>
    /// <param name="position">The receive-cipher position before this frame.</param>
    /// <param name="trail">What the read loop saw before this frame.</param>
    /// <param name="now">The read loop's monotonic millisecond clock, for the age of the last good frame.</param>
    /// <param name="state">The connection state, already rendered.</param>
    /// <param name="teleportPending">Whether SMSG_NEW_WORLD was sent and its MSG_MOVE_WORLDPORT_ACK not yet parsed.</param>
    public static string FormatWarning(string endpoint, ushort size, uint rawOpcode, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> plain,
        (byte Index, byte Previous) position, ClientFrameTrail trail, long now, string state, bool teleportPending)
    {
        ArgumentNullException.ThrowIfNull(trail);
        BadClientHeaderKind kind = Classify(size, rawOpcode);
        return string.Create(CultureInfo.InvariantCulture,
            $"[{endpoint}] bad packet header {Label(kind)} ({Describe(kind, ciphertext, rawOpcode)}): size {size}, opcode 0x{rawOpcode:X8}, "
            + $"cipher {Convert.ToHexString(ciphertext)}, plain {Convert.ToHexString(plain)}, recv cipher index {position.Index} previous {position.Previous}, "
            + $"{trail.Frames} frames / {trail.Bytes} bytes since the cipher engaged, last good {trail.LastGood(now)}, state {state}, "
            + $"teleport pending {(teleportPending ? "True" : "False")}; disconnecting");
    }
}
