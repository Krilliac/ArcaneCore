using System.IO.Compression;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// Builds the SMSG_ADDON_INFO payload from the addon block the client appends to
/// CMSG_AUTH_SESSION. Logic + the public-key blob verified against vmangos
/// src/game/Handlers/AddonHandler.cpp (build 1.12.1 path).
///
/// Block layout: uint32 uncompressedSize, then zlib-compressed per-addon records of
/// { CString name, uint8 flags, uint32 modulusCrc, uint32 urlCrc }.
/// </summary>
public static class AddonInfo
{
    private const byte StatusVisible = 1; // ADDON_STATUS_VISIBLE (build > 1.7.1)
    private const byte StatusHidden = 2;  // ADDON_STATUS_HIDDEN
    private const uint CorrectModulusCrc = 0x4C1C776D;

    /// <summary>Longest addon name read (bytes). Addon folder names are short; the bound stops a 1 MiB decompressed block from becoming one string.</summary>
    public const int MaxAddonNameBytes = 256;

    /// <summary>Most records answered; a retail client ships a few dozen addons, heavy UIs a few hundred.</summary>
    public const int MaxAddons = 4096;

    /// <summary>The largest record the response can grow by: status, info, key flag, the 256-byte key, revision and url flag.</summary>
    private const int MaxRecordBytes = 1 + 1 + 1 + 256 + 4 + 1;

    /// <summary>
    /// The response never exceeds one SMSG payload (<see cref="WorldSession.MaxServerPayload"/>): a
    /// block with tens of thousands of records would otherwise build a frame the size field cannot
    /// carry, and <c>Send</c> would throw out of the authentication handler.
    /// </summary>
    public const int MaxResponseBytes = WorldSession.MaxServerPayload;

    /// <summary>Blizzard public-key blob sent when an addon's modulus CRC mismatches.</summary>
    private static ReadOnlySpan<byte> PublicKey =>
    [
        0xC3, 0x5B, 0x50, 0x84, 0xB9, 0x3E, 0x32, 0x42, 0x8C, 0xD0, 0xC7, 0x48, 0xFA, 0x0E, 0x5D, 0x54,
        0x5A, 0xA3, 0x0E, 0x14, 0xBA, 0x9E, 0x0D, 0xB9, 0x5D, 0x8B, 0xEE, 0xB6, 0x84, 0x93, 0x45, 0x75,
        0xFF, 0x31, 0xFE, 0x2F, 0x64, 0x3F, 0x3D, 0x6D, 0x07, 0xD9, 0x44, 0x9B, 0x40, 0x85, 0x59, 0x34,
        0x4E, 0x10, 0xE1, 0xE7, 0x43, 0x69, 0xEF, 0x7C, 0x16, 0xFC, 0xB4, 0xED, 0x1B, 0x95, 0x28, 0xA8,
        0x23, 0x76, 0x51, 0x31, 0x57, 0x30, 0x2B, 0x79, 0x08, 0x50, 0x10, 0x1C, 0x4A, 0x1A, 0x2C, 0xC8,
        0x8B, 0x8F, 0x05, 0x2D, 0x22, 0x3D, 0xDB, 0x5A, 0x24, 0x7A, 0x0F, 0x13, 0x50, 0x37, 0x8F, 0x5A,
        0xCC, 0x9E, 0x04, 0x44, 0x0E, 0x87, 0x01, 0xD4, 0xA3, 0x15, 0x94, 0x16, 0x34, 0xC6, 0xC2, 0xC3,
        0xFB, 0x49, 0xFE, 0xE1, 0xF9, 0xDA, 0x8C, 0x50, 0x3C, 0xBE, 0x2C, 0xBB, 0x57, 0xED, 0x46, 0xB9,
        0xAD, 0x8B, 0xC6, 0xDF, 0x0E, 0xD6, 0x0F, 0xBE, 0x80, 0xB3, 0x8B, 0x1E, 0x77, 0xCF, 0xAD, 0x22,
        0xCF, 0xB7, 0x4B, 0xCF, 0xFB, 0xF0, 0x6B, 0x11, 0x45, 0x2D, 0x7A, 0x81, 0x18, 0xF2, 0x92, 0x7E,
        0x98, 0x56, 0x5D, 0x5E, 0x69, 0x72, 0x0A, 0x0D, 0x03, 0x0A, 0x85, 0xA2, 0x85, 0x9C, 0xCB, 0xFB,
        0x56, 0x6E, 0x8F, 0x44, 0xBB, 0x8F, 0x02, 0x22, 0x68, 0x63, 0x97, 0xBC, 0x85, 0xBA, 0xA8, 0xF7,
        0xB5, 0x40, 0x68, 0x3C, 0x77, 0x86, 0x6F, 0x4B, 0xD7, 0x88, 0xCA, 0x8A, 0xD7, 0xCE, 0x36, 0xF0,
        0x45, 0x6E, 0xD5, 0x64, 0x79, 0x0F, 0x17, 0xFC, 0x64, 0xDD, 0x10, 0x6F, 0xF3, 0xF5, 0xE0, 0xA6,
        0xC3, 0xFB, 0x1B, 0x8C, 0x29, 0xEF, 0x8E, 0xE5, 0x34, 0xCB, 0xD1, 0x2A, 0xCE, 0x79, 0xC3, 0x9A,
        0x0D, 0x36, 0xEA, 0x01, 0xE0, 0xAA, 0x91, 0x20, 0x54, 0xF0, 0x72, 0xD8, 0x1E, 0xC7, 0x89, 0xD2,
    ];

    /// <summary>
    /// Build the SMSG_ADDON_INFO response. Returns an empty payload if the block is absent
    /// or cannot be decompressed (the client still proceeds to character select). Parsing never
    /// throws: a record that does not fit (name over <see cref="MaxAddonNameBytes"/>, a truncated
    /// tail) ends the list, and the list ends at <see cref="MaxAddons"/> records or when the next
    /// record could push the response past <see cref="MaxResponseBytes"/>.
    /// </summary>
    public static byte[] BuildResponse(ReadOnlySpan<byte> addonBlock)
    {
        var response = new PacketWriter(64);
        if (!TryDecompress(addonBlock, out byte[] decompressed))
        {
            return response.AsMemory().ToArray();
        }

        var reader = new PacketReader(decompressed);
        int records = 0;
        while (reader.Remaining > 0 && records < MaxAddons && response.Length + MaxRecordBytes <= MaxResponseBytes)
        {
            if (!reader.TryReadCStringBytes(MaxAddonNameBytes, out ReadOnlySpan<byte> name)
                || !reader.TryReadByte(out _)                // flags (enabled)
                || !reader.TryReadUInt32(out uint modulusCrc)
                || !reader.TryReadUInt32(out _))             // url crc
            {
                break; // a truncated or oversized record ends the list
            }

            records++;
            if (name.IndexOf("Blizzard"u8) >= 0)
            {
                response.WriteByte(StatusHidden);
                response.WriteByte(1); // info provided
                if (modulusCrc != CorrectModulusCrc)
                {
                    response.WriteByte(1); // key provided
                    response.WriteBytes(PublicKey);
                }
                else
                {
                    response.WriteByte(0); // key not needed
                }

                response.WriteUInt32(0); // revision
                response.WriteByte(0);   // url not provided
            }
            else
            {
                response.WriteByte(StatusVisible);
                response.WriteByte(0); // info not provided
                response.WriteByte(0); // url not provided
            }
        }

        return response.AsMemory().ToArray();
    }

    private static bool TryDecompress(ReadOnlySpan<byte> addonBlock, out byte[] decompressed)
    {
        decompressed = [];
        if (addonBlock.Length < 4)
        {
            return false;
        }

        uint uncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(addonBlock);
        if (uncompressedSize is 0 or > 0xFFFFF)
        {
            return false;
        }

        try
        {
            byte[] output = new byte[uncompressedSize];
            using var input = new MemoryStream(addonBlock[4..].ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            zlib.ReadExactly(output, 0, output.Length);
            decompressed = output;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }
}
