namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// Decoder for the MPQ "sparse" method (mask bit 0x20): a run-length scheme for zero runs. The stream is the output length
/// as a big-endian 32-bit number, then operations: a byte with the high bit set is followed by <c>(b &amp; 0x7F) + 1</c>
/// literal bytes, a byte with the high bit clear stands for <c>(b &amp; 0x7F) + 3</c> zero bytes. In the 1.12.1 client this
/// method appears only on the sector checksum tables of the version-1 archives, never on file sectors (see the archive scan
/// in the Atlas tests).
/// Stateless and thread-safe.
/// </summary>
public static class MpqSparse
{
    /// <summary>Expands <paramref name="input"/> into <paramref name="output"/> and returns the byte count (the declared length).</summary>
    /// <exception cref="InvalidDataException">The stream is truncated, declares a length other than the output's, or its operations overrun it.</exception>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        // ref: StormLib src/sparse/sparse.cpp:225-287 (DecompressSparse)
        if (input.Length < 5)
        {
            throw new InvalidDataException("Sparse stream is shorter than its 5-byte minimum.");
        }

        uint declared = ((uint)input[0] << 24) | ((uint)input[1] << 16) | ((uint)input[2] << 8) | input[3];
        if (declared != (uint)output.Length)
        {
            throw new InvalidDataException($"Sparse stream declares {declared} bytes, expected {output.Length}.");
        }

        int read = 4;
        int written = 0;
        while (read < input.Length)
        {
            int op = input[read++];
            if ((op & 0x80) != 0)
            {
                int count = (op & 0x7F) + 1;
                if (read + count > input.Length)
                {
                    throw new InvalidDataException("Sparse stream ends inside a literal run.");
                }

                if (written + count > output.Length)
                {
                    throw new InvalidDataException("Sparse literal run passes the declared length.");
                }

                input.Slice(read, count).CopyTo(output[written..]);
                read += count;
                written += count;
            }
            else
            {
                // A zero run that reaches past the declared length is cut at it: the checksum tables of some patch.MPQ files
                // end that way, and StormLib clips identically (min(run, remaining) at sparse.cpp:277-278).
                int count = Math.Min((op & 0x7F) + 3, output.Length - written);
                output.Slice(written, count).Clear();
                written += count;
            }
        }

        if (written != output.Length)
        {
            throw new InvalidDataException($"Sparse stream produced {written} of {output.Length} bytes.");
        }

        return written;
    }
}
