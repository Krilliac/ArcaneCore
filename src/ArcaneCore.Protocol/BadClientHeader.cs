using System.Globalization;

namespace ArcaneCore.Protocol;

/// <summary>What a client header rejected by <see cref="WorldFrameCodec.TryParseClientHeader"/> most likely was.</summary>
public enum BadClientHeaderKind
{
    /// <summary>The size is in bounds and the low 16 bits of the raw opcode name a known opcode: the cipher is in sync and the client wrote junk above a real opcode.</summary>
    ClientMalformedOpcode,

    /// <summary>The size is outside [4, <see cref="WorldFrameCodec.MaxClientPacketSize"/>]: what a desynchronised or plaintext stream decrypts to.</summary>
    CipherDesyncOrPlaintext,

    /// <summary>The size is in bounds but neither the raw opcode fits 16 bits nor its low 16 bits name a known opcode.</summary>
    UnknownOpcode,
}

/// <summary>
/// Pure classification and wording of a rejected client header, shared by the world session and the gateway so both log
/// the same labels. Read-only: it never touches the header cipher.
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

    // WorldOpcodeNames.GetName falls back to "0x..." for a value without a name; no real name starts that way.
    private static bool IsKnownOpcode(uint opcode) => !WorldOpcodeNames.GetName(opcode).StartsWith("0x", StringComparison.Ordinal);
}
