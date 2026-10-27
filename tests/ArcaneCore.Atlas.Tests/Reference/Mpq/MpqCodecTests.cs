using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

public sealed class MpqCodecTests
{
    // ---- Adler-32 -----------------------------------------------------------------------------------------------

    [Fact]
    public void Adler32_Wikipedia_IsTheDocumentedValue()
    {
        // Worked example of the Adler-32 article (and RFC 1950 section 8.2): "Wikipedia" -> 0x11E60398.
        Assert.Equal(0x11E60398u, MpqChecksum.Adler32(MpqChecksum.ZlibSeed, "Wikipedia"u8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5551)]
    [InlineData(5552)]
    [InlineData(5553)]
    [InlineData(100000)]
    public void Adler32_MatchesTheTrailerOfAZlibStream(int length)
    {
        // Every zlib stream ends with the big-endian Adler-32 of its data (RFC 1950). .NET's compressor is the oracle.
        byte[] data = Pseudo(length, 7);
        Assert.Equal(TrailerOf(data), MpqChecksum.Adler32(MpqChecksum.ZlibSeed, data));
    }

    [Fact]
    public void Adler32_OfAllOnes_DoesNotOverflow()
    {
        byte[] data = Enumerable.Repeat((byte)0xFF, 1 << 20).ToArray();
        Assert.Equal(TrailerOf(data), MpqChecksum.Adler32(MpqChecksum.ZlibSeed, data));
    }

    [Fact]
    public void Adler32_IsIncremental_AndTheSectorSeedIsZero()
    {
        byte[] data = Pseudo(20000, 3);
        uint whole = MpqChecksum.Adler32(MpqChecksum.SectorSeed, data);
        uint parts = MpqChecksum.Adler32(MpqChecksum.Adler32(MpqChecksum.SectorSeed, data.AsSpan(0, 7777)), data.AsSpan(7777));
        Assert.Equal(whole, parts);
        Assert.Equal(0u, MpqChecksum.SectorSeed);
        Assert.NotEqual(whole, MpqChecksum.Adler32(MpqChecksum.ZlibSeed, data));

        // A seed of 0 has a = 0 where the standard has a = 1: for n bytes the low halves differ by exactly 1.
        Assert.Equal(1u, ((MpqChecksum.Adler32(MpqChecksum.ZlibSeed, data) & 0xFFFF) + 65521 - (whole & 0xFFFF)) % 65521);
    }

    // ---- Sparse -------------------------------------------------------------------------------------------------

    [Fact]
    public void Sparse_ExpandsLiteralAndZeroRuns()
    {
        // Declared length 8; literal run of 4 ("ABCD"), then a zero run of (1 & 0x7F) + 3 = 4 zeros.
        byte[] stream = [0, 0, 0, 8, 0x83, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 0x01];
        var output = new byte[8];
        Assert.Equal(8, MpqSparse.Decompress(stream, output));
        Assert.Equal(new byte[] { (byte)'A', (byte)'B', (byte)'C', (byte)'D', 0, 0, 0, 0 }, output);
    }

    [Fact]
    public void Sparse_LongestZeroRunIs130()
    {
        byte[] stream = [0, 0, 0, 130, 0x7F];
        var output = new byte[130];
        output.AsSpan().Fill(0xAA);
        Assert.Equal(130, MpqSparse.Decompress(stream, output));
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Sparse_CutsAZeroRunAtTheDeclaredLength_LikeStormLib()
    {
        // Observed in the checksum tables of two patch.MPQ files: the final zero run reaches past the declared length.
        // StormLib clips it (sparse.cpp:277-278), so the reader must as well.
        byte[] stream = [0, 0, 0, 6, 0x82, (byte)'x', (byte)'y', (byte)'z', 0x01];
        var output = new byte[6];
        output.AsSpan().Fill(0xAA);
        Assert.Equal(6, MpqSparse.Decompress(stream, output));
        Assert.Equal(new byte[] { (byte)'x', (byte)'y', (byte)'z', 0, 0, 0 }, output);
    }

    [Fact]
    public void Sparse_RoundTripsZeroHeavyData()
    {
        byte[] plain = Pseudo(5000, 11);
        plain.AsSpan(100, 900).Clear();
        plain.AsSpan(2000, 3).Clear();
        plain.AsSpan(3000, 1500).Clear();
        byte[] stream = MpqTestWriter.SparseEncode(plain);
        Assert.True(stream.Length < plain.Length);
        var output = new byte[plain.Length];
        Assert.Equal(plain.Length, MpqSparse.Decompress(stream, output));
        Assert.Equal(plain, output);
    }

    [Fact]
    public void Sparse_RefusesMalformedStreams()
    {
        Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([0, 0, 0, 1], new byte[1]));
        Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([0, 0, 0, 9, 0x00], new byte[8]));
        Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([0, 0, 0, 8, 0x83, 1, 2], new byte[8]));
        Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([0, 0, 0, 2, 0x84, 1, 2, 3, 4, 5], new byte[2]));
        Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([0, 0, 0, 8, 0x82, 1, 2, 3], new byte[8]));
    }

    // ---- PKWARE -------------------------------------------------------------------------------------------------

    [Fact]
    public void Pkware_ExpandsEveryExternalVector()
    {
        foreach (PkwareVector vector in PkwareVectors.All)
        {
            byte[] expected = PkwareVectors.Plain(vector.Source);
            Assert.Equal(vector.PlainLength, expected.Length);
            var output = new byte[expected.Length];
            int produced = PkwareExplode.Decompress(vector.Stream, output);
            Assert.True(produced == expected.Length, $"{vector.Source} with a {vector.DictionaryBytes}-byte dictionary produced {produced} of {expected.Length} bytes");
            Assert.True(expected.AsSpan().SequenceEqual(output), $"{vector.Source} with a {vector.DictionaryBytes}-byte dictionary expands to the wrong bytes");
        }

        // The recorded set covers all three dictionary sizes.
        Assert.Equal(new[] { 1024, 2048, 4096 }, PkwareVectors.All.Select(v => v.DictionaryBytes).Distinct().Order().ToArray());
    }

    [Fact]
    public void Pkware_RefusesAShortOutputBuffer()
    {
        PkwareVector vector = PkwareVectors.All.First(v => v.Source == PlainText.V1);
        Assert.Throws<InvalidDataException>(() => PkwareExplode.Decompress(vector.Stream, new byte[vector.PlainLength - 1]));
    }

    [Fact]
    public void Pkware_RefusesATruncatedStream()
    {
        foreach (PkwareVector vector in PkwareVectors.All.Where(v => v.PlainLength > 100))
        {
            byte[] stream = vector.Stream;
            for (int cut = 1; cut <= 3; cut++)
            {
                byte[] shorter = stream[..^cut];
                var output = new byte[vector.PlainLength];
                InvalidDataException e = Assert.Throws<InvalidDataException>(() => PkwareExplode.Decompress(shorter, output));
                Assert.NotEmpty(e.Message);
            }
        }
    }

    [Fact]
    public void Pkware_RefusesAsciiModeAndBadHeaders()
    {
        byte[] stream = PkwareVectors.All[0].Stream;

        byte[] ascii = [.. stream];
        ascii[0] = 1;
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => PkwareExplode.Decompress(ascii, new byte[2000]));
        Assert.Contains("ASCII", ex.Message, StringComparison.Ordinal);

        byte[] mode = [.. stream];
        mode[0] = 2;
        Assert.Throws<InvalidDataException>(() => PkwareExplode.Decompress(mode, new byte[2000]));

        foreach (byte bits in new byte[] { 0, 3, 7, 255 })
        {
            byte[] dictionary = [.. stream];
            dictionary[1] = bits;
            Assert.Throws<InvalidDataException>(() => PkwareExplode.Decompress(dictionary, new byte[2000]));
        }

        Assert.Throws<InvalidDataException>(() => PkwareExplode.Decompress([0, 4, 0, 0], new byte[10]));
    }

    [Fact]
    public void Pkware_OnGarbage_FailsCleanlyOrSucceeds()
    {
        // 400 pseudo-random streams behind a valid header: the decoder may accept or refuse each, but must neither hang nor
        // throw anything other than InvalidDataException.
        for (int seed = 1; seed <= 400; seed++)
        {
            byte[] body = Pseudo(8 + (seed % 90), seed);
            byte[] stream = [0, (byte)(4 + (seed % 3)), .. body];
            var output = new byte[2048];
            try
            {
                int produced = PkwareExplode.Decompress(stream, output);
                Assert.InRange(produced, 0, output.Length);
            }
            catch (InvalidDataException)
            {
                // refused: fine
            }
        }
    }

    // ---- MpqCompression -----------------------------------------------------------------------------------------

    [Fact]
    public void Compression_ARawSectorIsCopied()
    {
        byte[] data = Pseudo(100, 5);
        var output = new byte[100];
        MpqCompression.Decompress(data, output);
        Assert.Equal(data, output);
    }

    [Fact]
    public void Compression_ExpandsZlib_AndChecksTheLength()
    {
        byte[] plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabc", 200)));
        byte[] stored = [MpqCompressionMask.Zlib, .. ZlibBytes(plain)];
        var output = new byte[plain.Length];
        MpqCompression.Decompress(stored, output);
        Assert.Equal(plain, output);

        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(stored, new byte[plain.Length - 1]));
        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(stored, new byte[plain.Length + 1]));
        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(stored[..^6], new byte[plain.Length]));
    }

    [Fact]
    public void Compression_UndoesStagesInTheTablesOrder()
    {
        // Mask 0x22 = sparse + zlib: the sparse stream was compressed first, so zlib is undone first and sparse last.
        byte[] plain = new byte[3000];
        plain.AsSpan(0, 40).Fill(7);
        plain.AsSpan(2500, 10).Fill(9);
        byte[] stored = [0x22, .. ZlibBytes(MpqTestWriter.SparseEncode(plain))];
        Assert.True(stored.Length < plain.Length);
        var output = new byte[plain.Length];
        MpqCompression.Decompress(stored, output);
        Assert.Equal(plain, output);
    }

    [Fact]
    public void Compression_ExpandsPkwareBehindTheMask()
    {
        PkwareVector vector = PkwareVectors.All.First(v => v.Source == PlainText.V0 && v.DictionaryBytes == 1024);
        byte[] stored = [MpqCompressionMask.Pkware, .. vector.Stream];
        var output = new byte[vector.PlainLength];
        MpqCompression.Decompress(stored, output);
        Assert.Equal(PkwareVectors.Plain(PlainText.V0), output);
    }

    [Theory]
    [InlineData(0x01, "huffman")]
    [InlineData(0x10, "bzip2")]
    [InlineData(0x12, "bzip2")]
    [InlineData(0x40, "adpcm-mono")]
    [InlineData(0x80, "adpcm-stereo")]
    [InlineData(0x41, "huffman")]
    public void Compression_NamesAMethodItDoesNotImplement(int mask, string method)
    {
        byte[] stored = [(byte)mask, 1, 2, 3, 4, 5, 6, 7, 8];
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => MpqCompression.Decompress(stored, new byte[100]));
        Assert.Contains(method, e.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{mask:X2}", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x04)]
    [InlineData(0x06)]
    public void Compression_RefusesAMaskWithNoMethodOrAnUnknownBit(int mask)
    {
        byte[] stored = [(byte)mask, 1, 2, 3, 4, 5, 6, 7, 8];
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => MpqCompression.Decompress(stored, new byte[100]));
        Assert.Contains($"0x{mask:X2}", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compression_Format1_RefusesLzmaByName()
    {
        // ref: StormLib src/SCompression.cpp:1066-1115 (0x12 is LZMA in SCompDecompress2) and :1155-1161
        byte[] stored = [0x12, 1, 2, 3, 4, 5, 6, 7, 8];
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => MpqCompression.Decompress(stored, new byte[100], formatVersion: 1));
        Assert.Contains("lzma", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bzip2", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x40)]
    [InlineData(0x03)]
    [InlineData(0x42)]
    public void Compression_Format1_RefusesCombinationsSCompDecompress2Rejects(int mask)
    {
        byte[] stored = [(byte)mask, 1, 2, 3, 4, 5, 6, 7, 8];
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => MpqCompression.Decompress(stored, new byte[100], formatVersion: 1));
        Assert.Contains("format version 1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compression_Format1_Accepts0x22_AndFormat0StillAcceptsItsOwnCombinations()
    {
        byte[] plain = new byte[3000];
        plain.AsSpan(0, 40).Fill(7);
        byte[] stored = [0x22, .. ZlibBytes(MpqTestWriter.SparseEncode(plain))];
        var output = new byte[plain.Length];
        MpqCompression.Decompress(stored, output, formatVersion: 1);
        Assert.Equal(plain, output);

        // Format 0 has no such restriction: zlib+pkware (0x0A) passes the mask check and fails only on the payload.
        byte[] odd = [0x0A, 1, 2, 3, 4, 5, 6, 7, 8];
        Exception? failure = Record.Exception(() => MpqCompression.Decompress(odd, new byte[100], formatVersion: 0));
        Assert.NotNull(failure);
        Assert.DoesNotContain("method set", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compression_RefusesImpossibleSizes()
    {
        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(new byte[200], new byte[100]));
        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(new byte[1], new byte[100]));
        Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(ReadOnlyMemory<byte>.Empty, new byte[100]));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    internal static byte[] ZlibBytes(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain);
        }

        return output.ToArray();
    }

    internal static uint TrailerOf(byte[] data)
    {
        byte[] stream = ZlibBytes(data);
        return BinaryPrimitives.ReadUInt32BigEndian(stream.AsSpan(stream.Length - 4));
    }

    internal static byte[] Pseudo(int length, int seed)
    {
        var bytes = new byte[length];
        uint x = 0x2545F491u ^ ((uint)seed * 2654435761u);
        if (x == 0)
        {
            x = 1;
        }

        for (int i = 0; i < length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            bytes[i] = (byte)x;
        }

        return bytes;
    }
}
