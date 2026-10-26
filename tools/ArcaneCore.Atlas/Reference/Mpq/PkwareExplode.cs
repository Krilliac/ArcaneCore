namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// Decoder for the PKWARE Data Compression Library "implode" stream as MPQ archives store it (binary mode, 1 KiB, 2 KiB
/// or 4 KiB dictionary). The stream is: byte 0 = literal mode (0 binary, 1 ASCII), byte 1 = dictionary size as bits (4 to 6),
/// then an LSB-first bit stream of tokens. Each token starts with one flag bit: 0 = a literal (8 raw bits in binary mode),
/// 1 = a back-reference (length code, optional extra length bits, distance code, low distance bits). A back-reference of
/// length 519 is the end-of-stream marker.
/// </summary>
/// <remarks>
/// ASCII mode (a Huffman-coded literal alphabet) is refused with <see cref="NotSupportedException"/>: the only encoder that
/// writes MPQs, <c>SCompImplode</c>, always requests binary mode, and no sector in the client's archives uses ASCII mode
/// (see the archive scan in the Atlas tests). Stateless and thread-safe.
/// </remarks>
public static class PkwareExplode
{
    // Every table below is a fact of the PKWARE stream format, transcribed from the reader named in each comment.

    // ref: StormLib src/pklib/explode.c:34-40 (DistBits)
    private static readonly byte[] DistBits =
    [
        0x02, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
    ];

    // ref: StormLib src/pklib/explode.c:42-48 (DistCode)
    private static readonly byte[] DistCode =
    [
        0x03, 0x0D, 0x05, 0x19, 0x09, 0x11, 0x01, 0x3E, 0x1E, 0x2E, 0x0E, 0x36, 0x16, 0x26, 0x06, 0x3A,
        0x1A, 0x2A, 0x0A, 0x32, 0x12, 0x22, 0x42, 0x02, 0x7C, 0x3C, 0x5C, 0x1C, 0x6C, 0x2C, 0x4C, 0x0C,
        0x74, 0x34, 0x54, 0x14, 0x64, 0x24, 0x44, 0x04, 0x78, 0x38, 0x58, 0x18, 0x68, 0x28, 0x48, 0x08,
        0xF0, 0x70, 0xB0, 0x30, 0xD0, 0x50, 0x90, 0x10, 0xE0, 0x60, 0xA0, 0x20, 0xC0, 0x40, 0x80, 0x00,
    ];

    // ref: StormLib src/pklib/explode.c:50-53 (ExLenBits)
    private static readonly byte[] ExtraLengthBits =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
    ];

    // ref: StormLib src/pklib/explode.c:55-59 (LenBase)
    private static readonly ushort[] LengthBase =
    [
        0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0007,
        0x0008, 0x000A, 0x000E, 0x0016, 0x0026, 0x0046, 0x0086, 0x0106,
    ];

    // ref: StormLib src/pklib/explode.c:61-64 (LenBits)
    private static readonly byte[] LengthBits =
    [
        0x03, 0x02, 0x03, 0x03, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x07, 0x07,
    ];

    // ref: StormLib src/pklib/explode.c:66-69 (LenCode)
    private static readonly byte[] LengthCode =
    [
        0x05, 0x03, 0x01, 0x06, 0x0A, 0x02, 0x0C, 0x14, 0x04, 0x18, 0x08, 0x30, 0x10, 0x20, 0x40, 0x00,
    ];

    // Index of the 8 look-ahead bits -> symbol (ref: explode.c:130-149 GenDecodeTabs, used at :512 and :516).
    private static readonly byte[] DistLookup = BuildLookup(DistCode, DistBits);
    private static readonly byte[] LengthLookup = BuildLookup(LengthCode, LengthBits);

    /// <summary>The length value (before adding 2) that marks the end of the stream.</summary>
    // ref: StormLib src/pklib/explode.c:266-267,403 (literal 0x305 = length_code 0x205 + 0x100)
    private const uint EndOfStreamLength = 0x205;

    /// <summary>
    /// Expands <paramref name="input"/> into <paramref name="output"/> and returns the number of bytes written.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is truncated, malformed, or longer than <paramref name="output"/>.</exception>
    /// <exception cref="NotSupportedException">The stream uses ASCII literal mode.</exception>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        // ref: StormLib src/pklib/explode.c:486-505
        if (input.Length <= 4)
        {
            throw new InvalidDataException("PKWARE stream is too short.");
        }

        byte mode = input[0];
        int dictBits = input[1];
        if (dictBits is < 4 or > 6)
        {
            throw new InvalidDataException($"PKWARE stream has an invalid dictionary size field {dictBits} (expected 4, 5 or 6).");
        }

        if (mode == 1)
        {
            throw new NotSupportedException("PKWARE ASCII literal mode is not supported: MPQ archives are written in binary mode only.");
        }

        if (mode != 0)
        {
            throw new InvalidDataException($"PKWARE stream has an invalid literal mode {mode}.");
        }

        var bits = new BitReader(input[2..]);
        int written = 0;
        while (true)
        {
            if (bits.Read(1) == 0)
            {
                // Literal: eight raw bits in binary mode. ref: explode.c:308-320
                uint literal = bits.Read(8);
                if (bits.Overrun)
                {
                    throw new InvalidDataException("PKWARE stream ends inside a literal.");
                }

                if (written >= output.Length)
                {
                    throw new InvalidDataException("PKWARE stream expands past the expected size.");
                }

                output[written++] = (byte)literal;
                continue;
            }

            // Back-reference. ref: explode.c:277-306 (length), :360-389 (distance)
            int lengthSymbol = LengthLookup[bits.Peek(8)];
            bits.Skip(LengthBits[lengthSymbol]);
            uint length = LengthBase[lengthSymbol];
            int extra = ExtraLengthBits[lengthSymbol];
            if (extra != 0)
            {
                length += bits.Read(extra);
            }

            if (length == EndOfStreamLength)
            {
                return written;
            }

            int repeat = (int)length + 2;
            int distSymbol = DistLookup[bits.Peek(8)];
            bits.Skip(DistBits[distSymbol]);
            uint distance = repeat == 2
                ? ((uint)distSymbol << 2) | bits.Read(2)
                : ((uint)distSymbol << dictBits) | bits.Read(dictBits);
            distance++;

            if (bits.Overrun)
            {
                throw new InvalidDataException("PKWARE stream ends inside a back-reference.");
            }

            if (distance > (uint)written)
            {
                throw new InvalidDataException($"PKWARE back-reference reaches {distance} bytes back with only {written} written.");
            }

            if (written + repeat > output.Length)
            {
                throw new InvalidDataException("PKWARE stream expands past the expected size.");
            }

            // The copy may overlap its own output (a run), so it goes byte by byte.
            int from = written - (int)distance;
            for (int i = 0; i < repeat; i++)
            {
                output[written++] = output[from++];
            }
        }
    }

    private static byte[] BuildLookup(byte[] startIndexes, byte[] lengthBits)
    {
        var positions = new byte[0x100];
        for (int symbol = 0; symbol < startIndexes.Length; symbol++)
        {
            int step = 1 << lengthBits[symbol];
            for (int index = startIndexes[symbol]; index < 0x100; index += step)
            {
                positions[index] = (byte)symbol;
            }
        }

        return positions;
    }

    /// <summary>LSB-first bit reader that pads with zero bits past the end and remembers when a read needed them.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _next;
        private ulong _buffer;
        private int _count;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _next = 0;
            _buffer = 0;
            _count = 0;
            Overrun = false;
        }

        /// <summary>True once a read or skip asked for bits the input does not have.</summary>
        public bool Overrun { get; private set; }

        public uint Peek(int n)
        {
            Fill();
            return (uint)(_buffer & ((1UL << n) - 1));
        }

        public void Skip(int n)
        {
            Fill();
            if (n > _count)
            {
                Overrun = true;
                _buffer = 0;
                _count = 0;
                return;
            }

            _buffer >>= n;
            _count -= n;
        }

        public uint Read(int n)
        {
            uint value = Peek(n);
            Skip(n);
            return value;
        }

        private void Fill()
        {
            while (_count <= 56 && _next < _data.Length)
            {
                _buffer |= (ulong)_data[_next++] << _count;
                _count += 8;
            }
        }
    }
}
