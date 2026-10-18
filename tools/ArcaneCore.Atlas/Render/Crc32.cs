namespace ArcaneCore.Atlas.Render;

/// <summary>
/// The 32-bit cyclic redundancy code PNG puts after every chunk.
/// <para>
/// ref: W3C PNG Specification Second Edition (ISO/IEC 15948:2003), https://www.w3.org/TR/2003/REC-PNG-20031110/,
/// section 5.5 "Cyclic Redundancy Code algorithm" (polynomial x^32 + x^26 + x^23 + x^22 + x^16 + x^12 + x^11 + x^10 +
/// x^8 + x^7 + x^5 + x^4 + x^2 + x + 1, register initialised to all ones, bytes processed least significant bit first,
/// result inverted) and Annex D "Sample Cyclic Redundancy Code implementation" (table of CRCs of all 8-bit messages,
/// generator constant 0xedb88320). Check value: the CRC of the ASCII text "123456789" is 0xCBF43926.
/// </para>
/// </summary>
public static class Crc32
{
    /// <summary>ref: PNG 2nd ed. Annex D, <c>make_crc_table</c>: the reflected generator polynomial.</summary>
    private const uint Generator = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();

    /// <summary>The register value before any byte is processed (PNG 5.5: "initialized to all 1's").</summary>
    public const uint Initial = 0xFFFFFFFF;

    /// <summary>The CRC of <paramref name="data"/>, as stored after a PNG chunk.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(Initial, data);

    /// <summary>
    /// Feeds <paramref name="data"/> through a running register (PNG Annex D <c>update_crc</c>). Start from
    /// <see cref="Initial"/> and invert the final register to obtain the CRC.
    /// </summary>
    public static uint Update(uint register, ReadOnlySpan<byte> data)
    {
        uint c = register;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Generator ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
