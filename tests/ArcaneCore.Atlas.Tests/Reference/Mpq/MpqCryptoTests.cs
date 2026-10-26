using System.Buffers.Binary;
using System.Security.Cryptography;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

public sealed class MpqCryptoTests
{
    [Fact]
    public void CryptTable_MatchesLibmpqLiteralTable()
    {
        // The table in vmangos@0e3ff01 dep/libmpq/libmpq/crypt_buf.h is a literal array written out by libmpq's generator, not
        // the StormLib formula the reader uses (SBaseCommon.cpp:219-244), so agreement is an independent check. The digest is
        // SHA-256 over its 1280 entries as little-endian words, computed from that header when this test was written.
        ReadOnlySpan<uint> table = MpqCrypto.Table;
        Assert.Equal(0x500, table.Length);
        var bytes = new byte[table.Length * 4];
        for (int i = 0; i < table.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), table[i]);
        }

        Assert.Equal("3d55980901998ca3c729097c18579ae81d4c542218f7dae65e3248f24b82a402", Convert.ToHexStringLower(SHA256.HashData(bytes)));

        // Spot values from the same header (crypt_buf.h:3, a row boundary, the start of the key-mix row, the last entry).
        Assert.Equal(0x55C636E2u, table[0]);
        Assert.Equal(0x76F8C1B1u, table[0x100]);
        Assert.Equal(0x193AA698u, table[0x400]);
        Assert.Equal(0x7303286Cu, table[0x4FF]);
    }

    [Fact]
    public void HashString_OfTheTableNames_GivesStormLibsKeys()
    {
        // StormLib src/StormLib.h:295-296 names these two values as the keys of the hash and block tables;
        // vmangos@0e3ff01 dep/libmpq/libmpq/mpq.c:223,242 derives the same keys with the same function.
        Assert.Equal(0xC3AF3770u, MpqCrypto.HashString("(hash table)", MpqCrypto.HashFileKey));
        Assert.Equal(0xEC83B3A3u, MpqCrypto.HashString("(block table)", MpqCrypto.HashFileKey));
        Assert.Equal(MpqCrypto.HashTableKey, MpqCrypto.HashString("(hash table)", MpqCrypto.HashFileKey));
        Assert.Equal(MpqCrypto.BlockTableKey, MpqCrypto.HashString("(block table)", MpqCrypto.HashFileKey));
    }

    [Fact]
    public void HashString_FoldsCaseAndSlashes()
    {
        foreach (uint type in new[] { MpqCrypto.HashTableIndex, MpqCrypto.HashNameA, MpqCrypto.HashNameB, MpqCrypto.HashFileKey })
        {
            uint expected = MpqCrypto.HashString("WORLD\\MAPS\\AZEROTH\\AZEROTH.WDT", type);
            Assert.Equal(expected, MpqCrypto.HashString("World/Maps/Azeroth/Azeroth.wdt", type));
            Assert.Equal(expected, MpqCrypto.HashString("world\\maps\\azeroth\\azeroth.wdt"u8, type));
        }

        Assert.NotEqual(
            MpqCrypto.HashString("a", MpqCrypto.HashNameA),
            MpqCrypto.HashString("a", MpqCrypto.HashNameB));
    }

    [Fact]
    public void HashString_AboveLatin1_HashesAsQuestionMark()
    {
        Assert.Equal(MpqCrypto.HashString("a?b", MpqCrypto.HashNameA), MpqCrypto.HashString("a\u4e2db", MpqCrypto.HashNameA));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(1000)]
    public void Decrypt_InvertsTheWritersEncrypt(int length)
    {
        byte[] plain = Pattern(length);
        byte[] data = [.. plain];
        MpqTestWriter.Encrypt(data, 0xC3AF3770);
        if (length >= 4)
        {
            Assert.NotEqual(plain, data);
        }

        MpqCrypto.Decrypt(data, 0xC3AF3770);
        Assert.Equal(plain, data);
    }

    [Fact]
    public void Decrypt_LeavesATrailingPartialWordAsStored()
    {
        byte[] plain = Pattern(11);
        byte[] data = [.. plain];
        MpqTestWriter.Encrypt(data, 12345);
        byte[] expectedTail = data[8..];
        MpqCrypto.Decrypt(data, 12345);
        Assert.Equal(plain[..8], data[..8]);
        Assert.Equal(expectedTail, data[8..]);
    }

    [Fact]
    public void Decrypt_WithTheWrongKey_DoesNotRecoverTheData()
    {
        byte[] plain = Pattern(256);
        byte[] data = [.. plain];
        MpqTestWriter.Encrypt(data, 1000);
        MpqCrypto.Decrypt(data, 1001);
        Assert.NotEqual(plain, data);
    }

    [Fact]
    public void FileKey_UsesThePlainNameAndTheFixKeyFormula()
    {
        // Plain key: hash of the name after the last slash. Fix key: (key + block offset) ^ file size.
        // ref: StormLib src/SBaseCommon.cpp:685-704
        uint plain = MpqCrypto.HashString("FILE.TXT", MpqCrypto.HashFileKey);
        Assert.Equal(plain, MpqCrypto.FileKey("Some\\Dir\\file.txt", 0x1234, 77, fixKey: false));
        Assert.Equal(plain, MpqCrypto.FileKey("Some/Dir/file.txt", 0x1234, 77, fixKey: false));
        Assert.Equal(plain, MpqCrypto.FileKey("file.txt", 0x1234, 77, fixKey: false));
        Assert.Equal((plain + 0x1234u) ^ 77u, MpqCrypto.FileKey("Some\\Dir\\file.txt", 0x1234, 77, fixKey: true));

        // Only the low 32 bits of the block offset count (StormLib casts to DWORD).
        Assert.Equal((plain + 0x1234u) ^ 77u, MpqCrypto.FileKey("file.txt", 0x1_0000_1234L, 77, fixKey: true));
    }

    [Fact]
    public void Describe_NamesEveryMaskBit()
    {
        Assert.Equal("0x02(zlib)", MpqCompressionMask.Describe(MpqCompressionMask.Zlib));
        Assert.Equal("0x0A(zlib+pkware)", MpqCompressionMask.Describe(0x0A));
        Assert.Equal("0xFB(huffman+zlib+pkware+bzip2+sparse+adpcm-mono+adpcm-stereo)", MpqCompressionMask.Describe(0xFB));
        Assert.Contains("unknown-0x04", MpqCompressionMask.Describe(0x04), StringComparison.Ordinal);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        uint x = 0x2545F491;
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
