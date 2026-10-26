using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>Reader tests over archives built at run time by <see cref="MpqTestWriter"/> (no client data).</summary>
public sealed class MpqArchiveTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Text(int length, string seed = "the quick brown fox ")
        => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(seed, (length / seed.Length) + 1)))[..length];

    private static byte[] Noise(int length, int seed = 1) => MpqCodecTests.Pseudo(length, seed);

    private MpqArchive Open(byte[] image, bool verifyChecksums = true) => MpqArchive.Open(_dir.Write(Guid.NewGuid().ToString("N") + ".MPQ", image), verifyChecksums);

    // ---- headers and layout -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false, 3)]
    [InlineData(1, false, 3)]
    [InlineData(0, true, 3)]
    [InlineData(1, true, 0)]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 5)]
    public void Reads_AStoredFile_InEveryHeaderLayout(ushort version, bool userData, ushort shift)
    {
        byte[] data = Noise(10000);
        byte[] image = MpqTestWriter.Build([new TestMpqFile("Dir\\File.bin", data)], new TestMpqOptions { FormatVersion = version, UserData = userData, SectorShift = shift });
        using MpqArchive archive = Open(image);

        Assert.Equal(version, archive.Header.FormatVersion);
        Assert.Equal(version == 0 ? 0x20u : 0x2Cu, archive.Header.HeaderSize);
        Assert.Equal(512 << shift, archive.SectorSize);
        Assert.Equal(userData, archive.Header.UserData is not null);
        Assert.Equal(userData ? 0x200 : 0, archive.Header.Position);
        Assert.Equal(data, archive.Read("Dir\\File.bin"));
    }

    [Fact]
    public void Lookup_IgnoresCaseAndSlashDirection()
    {
        byte[] data = Text(300);
        using MpqArchive archive = Open(MpqTestWriter.Build([new TestMpqFile("World\\Maps\\Azeroth\\Azeroth.wdt", data)]));
        Assert.Equal(data, archive.Read("world/maps/azeroth/AZEROTH.WDT"));
        Assert.True(archive.Contains("WORLD\\MAPS\\AZEROTH\\AZEROTH.WDT"));
        Assert.False(archive.Contains("World\\Maps\\Azeroth\\Azeroth.wdt.bak"));
    }

    [Fact]
    public void Lookup_WalksPastADeletedHashSlot()
    {
        byte[] data = Text(300);
        byte[] image = MpqTestWriter.Build(
            [new TestMpqFile("Target.bin", data)],
            new TestMpqOptions { DeletedHashBefore = "Target.bin", HashSize = 16 });
        using MpqArchive archive = Open(image);

        // The start slot holds a deleted entry; the file sits in a later slot.
        uint start = MpqTestWriter.Hash("Target.bin", MpqCrypto.HashTableIndex) & 15;
        Assert.Equal(MpqArchive.HashEntryDeleted, archive.Hashes[(int)start].BlockIndex);
        Assert.Equal(data, archive.Read("Target.bin"));
    }

    [Fact]
    public void Lookup_OfAnAbsentName_StopsAtAFreeSlot()
    {
        using MpqArchive archive = Open(MpqTestWriter.Build([new TestMpqFile("a.bin", Text(10))]));
        Assert.False(archive.TryFind("b.bin", out _));
        Assert.Null(archive.TryRead("b.bin"));
        Assert.Throws<FileNotFoundException>(() => archive.Read("b.bin"));
    }

    [Fact]
    public void Lookup_FollowsStormLibsLocaleRule_ForPreferredEnUS()
    {
        // ref: StormLib src/SBaseFileTable.cpp:811-850 (GetHashEntryLocale): an exact enUS entry wins at once,
        // otherwise the last neutral-or-enUS entry; any other locale or platform is never returned.
        byte[] neutral = Text(50, "neutral ");
        byte[] english = Text(50, "english ");
        byte[] german = Text(50, "deutsch ");
        byte[] console = Text(50, "console ");
        TestMpqFile Neutral() => new("L.txt", neutral) { Locale = 0 };
        TestMpqFile English() => new("L.txt", english) { Locale = 0x0409 };
        TestMpqFile German() => new("L.txt", german) { Locale = 0x0407 };

        foreach (TestMpqFile[] order in new[]
        {
            new[] { German(), English(), Neutral() },
            new[] { Neutral(), English(), German() },
            new[] { English(), German(), Neutral() },
        })
        {
            using MpqArchive all = Open(MpqTestWriter.Build(order));
            Assert.Equal(english, all.Read("L.txt"));
        }

        using MpqArchive neutralAndGerman = Open(MpqTestWriter.Build([German(), Neutral()]));
        Assert.Equal(neutral, neutralAndGerman.Read("L.txt"));

        using MpqArchive onlyGerman = Open(MpqTestWriter.Build([German()]));
        Assert.False(onlyGerman.TryFind("L.txt", out _));

        // A non-zero platform byte matches neither the neutral nor the enUS rule (SBaseFileTable.cpp:836-841).
        using MpqArchive otherPlatform = Open(MpqTestWriter.Build([new TestMpqFile("L.txt", console) { Locale = 0x0409, Platform = 1 }]));
        Assert.False(otherPlatform.TryFind("L.txt", out _));
        using MpqArchive platformAndNeutral = Open(MpqTestWriter.Build([new TestMpqFile("L.txt", console) { Locale = 0x0409, Platform = 1 }, Neutral()]));
        Assert.Equal(neutral, platformAndNeutral.Read("L.txt"));
    }

    // ---- file kinds ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 20000)]
    [InlineData(3, 20000)]
    [InlineData(3, 4096)]
    [InlineData(3, 4097)]
    [InlineData(3, 1)]
    [InlineData(1, 5000)]
    public void Reads_ZlibFiles_AcrossSectors(ushort shift, int length)
    {
        byte[] compressible = Text(length);
        byte[] mixed = [.. Text(length / 2), .. Noise(length - (length / 2), 5)];
        byte[] noise = Noise(length, 9);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [
                new TestMpqFile("text.bin", compressible) { Zlib = true },
                new TestMpqFile("mixed.bin", mixed) { Zlib = true },
                new TestMpqFile("noise.bin", noise) { Zlib = true },
            ],
            new TestMpqOptions { SectorShift = shift }));

        Assert.Equal(compressible, archive.Read("text.bin"));
        Assert.Equal(mixed, archive.Read("mixed.bin"));
        Assert.Equal(noise, archive.Read("noise.bin"));

        if (length >= 1024)
        {
            // The scanner sees compressed sectors (zlib) and sectors that did not shrink (stored raw).
            MpqScanReport report = MpqScanner.Scan(archive, sectors: true);
            Assert.True(report.SectorMethods.GetValueOrDefault("0x02(zlib)") > 0);
            Assert.True(report.RawSectors > 0);
            Assert.Equal(report.ExistingBlocks, report.InspectedFiles);
            Assert.Equal(new[] { "0x02(zlib)" }, report.SectorMethods.Keys.ToArray());
        }
    }

    [Fact]
    public void Reads_SingleUnitFiles()
    {
        byte[] data = Text(30000);
        byte[] noise = Noise(5000, 2);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [
                new TestMpqFile("unit-zlib.bin", data) { SingleUnit = true, Zlib = true },
                new TestMpqFile("unit-stored.bin", data) { SingleUnit = true },
                new TestMpqFile("unit-noise.bin", noise) { SingleUnit = true, Zlib = true },
            ]));

        Assert.Equal(data, archive.Read("unit-zlib.bin"));
        Assert.Equal(data, archive.Read("unit-stored.bin"));
        Assert.Equal(noise, archive.Read("unit-noise.bin"));
        Assert.True(archive.TryFind("unit-zlib.bin", out MpqEntry entry));
        Assert.True(entry.Block.IsSingleUnit);
        Assert.True(entry.Block.CompressedSize < entry.Block.FileSize);
    }

    [Fact]
    public void Reads_AZeroLengthFile()
    {
        using MpqArchive archive = Open(MpqTestWriter.Build([new TestMpqFile("empty.txt", [])]));
        Assert.Empty(archive.Read("empty.txt"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Reads_EncryptedFiles_WithAndWithoutTheFixKeyFlag(bool zlib, bool singleUnit, bool fixKey)
    {
        byte[] data = [.. Text(7000), .. Noise(2500, 4)];
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [
                new TestMpqFile("Filler\\first.bin", Noise(777, 8)),
                new TestMpqFile("Secret\\Treasure.dat", data) { Zlib = zlib, SingleUnit = singleUnit, Encrypted = true, FixKey = fixKey },
            ],
            new TestMpqOptions { SectorShift = 2 }));

        Assert.True(archive.TryFind("Secret\\Treasure.dat", out MpqEntry entry));
        Assert.True(entry.Block.IsEncrypted);
        Assert.Equal(fixKey, (entry.Block.Flags & MpqFileFlags.FixKey) != 0);
        Assert.Equal(data, archive.Read("secret/TREASURE.dat"));

        // The key comes from the part of the name after the last slash.
        Assert.Equal(data, archive.Read(entry, "Elsewhere\\treasure.DAT"));
        if (zlib && !singleUnit)
        {
            Assert.NotEqual(data, ReadWithWrongKey(archive, entry));
        }
    }

    private static byte[] ReadWithWrongKey(MpqArchive archive, MpqEntry entry)
    {
        try
        {
            return archive.Read(entry, "Wrong.name");
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    // ---- deletion markers, list file ----------------------------------------------------------------------------

    [Fact]
    public void ADeletionMarker_IsFoundAsSuch_AndNotReadAsAFile()
    {
        using MpqArchive archive = Open(MpqTestWriter.Build([new TestMpqFile("Gone.txt", []) { Delete = true }, new TestMpqFile("Kept.txt", Text(10))]));
        Assert.True(archive.TryFind("Gone.txt", out MpqEntry entry));
        Assert.NotEqual(0u, entry.Block.Flags & MpqFileFlags.DeleteMarker);
        Assert.Equal(0u, entry.Block.FileSize);
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => archive.Read("Gone.txt"));
        Assert.Contains("deletion marker", e.Message, StringComparison.Ordinal);
        Assert.Contains("Gone.txt", archive.ListFile);
    }

    [Fact]
    public void ListFile_KeepsOnlyNamesThatResolve()
    {
        string listing = "A.txt\r\nmissing.txt\n\n\rB.TXT\r\n(listfile)\r\n";
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [new TestMpqFile("A.txt", Text(5)), new TestMpqFile("b.txt", Text(5)), new TestMpqFile("(listfile)", Encoding.Latin1.GetBytes(listing)) { Zlib = true }],
            new TestMpqOptions { ListFile = false }));
        Assert.Equal(new[] { "A.txt", "B.TXT", "(listfile)" }, archive.ListFile);
    }

    [Fact]
    public void ListFile_OfAnArchiveWithoutOne_IsEmpty()
    {
        using MpqArchive archive = Open(MpqTestWriter.Build([new TestMpqFile("A.txt", Text(5))], new TestMpqOptions { ListFile = false }));
        Assert.Empty(archive.ListFile);
    }

    // ---- sector checksums ---------------------------------------------------------------------------------------

    [Fact]
    public void Checksums_AreVerified_WhenARealTableIsPresent()
    {
        byte[] data = [.. Text(9000), .. Noise(3000, 3)];
        TestMpqFile file = new("Sum.bin", data) { Zlib = true, Checksums = ChecksumTableForm.Plain };
        using MpqArchive archive = Open(MpqTestWriter.Build([file], new TestMpqOptions { SectorShift = 1 }));
        Assert.True(archive.VerifyChecksums);
        Assert.Equal(data, archive.Read("Sum.bin"));

        MpqScanReport report = MpqScanner.Scan(archive, sectors: true);
        int sectors = (data.Length + 1023) / 1024;
        Assert.Equal(1, report.ChecksumTables);
        Assert.Equal(sectors, report.ChecksumSectorsVerified);
        Assert.Equal(0, report.ChecksumMismatches);
        Assert.Equal(1, report.ChecksumTableMethods["plain"]);
    }

    [Fact]
    public void Checksums_Mismatch_FailsTheRead_UnlessVerificationIsOff()
    {
        byte[] data = Text(8000);
        byte[] image = MpqTestWriter.Build(
            [new TestMpqFile("Bad.bin", data) { Zlib = true, Checksums = ChecksumTableForm.PlainCorrupt }],
            new TestMpqOptions { SectorShift = 1 });

        using (MpqArchive verified = Open(image))
        {
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => verified.Read("Bad.bin"));
            Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
            Assert.Contains("Bad.bin", e.Message, StringComparison.Ordinal);
            Assert.Equal(1, MpqScanner.Scan(verified, sectors: true).ChecksumMismatches);
        }

        using MpqArchive lenient = Open(image, verifyChecksums: false);
        Assert.False(lenient.VerifyChecksums);
        Assert.Equal(data, lenient.Read("Bad.bin"));
    }

    [Fact]
    public void Checksums_AnEmptyTable_IsIgnored()
    {
        byte[] data = Text(8000);
        using MpqArchive archive = Open(MpqTestWriter.Build([new TestMpqFile("E.bin", data) { Zlib = true, Checksums = ChecksumTableForm.Empty }], new TestMpqOptions { SectorShift = 1 }));
        Assert.True(archive.TryFind("E.bin", out MpqEntry entry));
        Assert.NotEqual(0u, entry.Block.Flags & MpqFileFlags.SectorCrc);
        Assert.Equal(data, archive.Read("E.bin"));
        MpqScanReport report = MpqScanner.Scan(archive, sectors: true);
        Assert.Equal(1, report.ChecksumTablesAbsent);
        Assert.Equal(0, report.ChecksumTables);
    }

    [Fact]
    public void Checksums_SparseTables_AreDecoded()
    {
        byte[] data = Text(16000);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [
                new TestMpqFile("Zeros.bin", data) { Zlib = true, Checksums = ChecksumTableForm.SparseZeros },
                new TestMpqFile("One.bin", data) { Zlib = true, Checksums = ChecksumTableForm.SparseValues },
            ],
            new TestMpqOptions { SectorShift = 1 }));
        Assert.Equal(data, archive.Read("Zeros.bin"));
        Assert.Equal(data, archive.Read("One.bin"));

        MpqScanReport report = MpqScanner.Scan(archive, sectors: true);
        int sectors = (data.Length + 1023) / 1024;
        Assert.Equal(2, report.ChecksumTables);
        Assert.Equal(2, report.ChecksumTableMethods["0x20(sparse)"]);
        Assert.Equal(1, report.ChecksumSectorsVerified);
        Assert.Equal((2 * sectors) - 1, report.ChecksumSectorsNotRecorded);
        Assert.Equal(0, report.ChecksumMismatches);
    }

    [Fact]
    public void Checksums_CoverEncryptedFiles_AfterDecryption()
    {
        byte[] data = [.. Text(6000), .. Noise(2000, 6)];
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [new TestMpqFile("Enc.bin", data) { Zlib = true, Encrypted = true, FixKey = true, Checksums = ChecksumTableForm.Plain }],
            new TestMpqOptions { SectorShift = 1 }));
        Assert.Equal(data, archive.Read("Enc.bin"));
    }

    // ---- other compression methods ------------------------------------------------------------------------------

    [Fact]
    public void Implode_FlagFiles_ExpandPkwareSectors()
    {
        // A writer stores a sector raw when compression does not shrink it (StormLib SFileReadFile.cpp:162-165 reads a
        // sector at least as long as its output as stored), so only vectors shorter than their plaintext go in as imploded.
        foreach (PkwareVector vector in PkwareVectors.All.Where(v => v.PlainLength > 1 && v.Stream.Length < v.PlainLength))
        {
            byte[] plain = PkwareVectors.Plain(vector.Source);
            using MpqArchive archive = Open(MpqTestWriter.Build(
                [new TestMpqFile("Imploded.bin", plain) { StoredSectors = [vector.Stream], ImplodeFlag = true }],
                new TestMpqOptions { SectorShift = 4 }));
            Assert.True(archive.TryFind("Imploded.bin", out MpqEntry entry));
            Assert.Equal(MpqFileFlags.Implode, entry.Block.Flags & MpqFileFlags.CompressMask);
            Assert.Equal(plain, archive.Read("Imploded.bin"));
        }
    }

    [Fact]
    public void ASectorStoredLongerThanItsOutput_IsRefused()
    {
        // V5 is nearly all literals: its imploded stream is longer than the 700 bytes it expands to. No archive writer
        // stores such a sector compressed; the reader refuses it instead of copying a prefix the way StormLib does.
        PkwareVector vector = PkwareVectors.All.First(v => v.Source == PlainText.V5);
        Assert.True(vector.Stream.Length > vector.PlainLength);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [new TestMpqFile("Long.bin", PkwareVectors.Plain(vector.Source)) { StoredSectors = [vector.Stream], ImplodeFlag = true }],
            new TestMpqOptions { SectorShift = 4 }));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => archive.Read("Long.bin"));
        Assert.Contains($"sector 0 stores {vector.Stream.Length} bytes for {vector.PlainLength}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pkware_BehindAMethodMask_IsExpanded()
    {
        PkwareVector vector = PkwareVectors.All.First(v => v.Source == PlainText.V3);
        byte[] plain = PkwareVectors.Plain(vector.Source);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [new TestMpqFile("Masked.bin", plain) { StoredSectors = [[MpqCompressionMask.Pkware, .. vector.Stream]], CompressFlag = true }],
            new TestMpqOptions { SectorShift = 4 }));
        Assert.Equal(plain, archive.Read("Masked.bin"));
    }

    [Theory]
    [InlineData(0x10, "bzip2")]
    [InlineData(0x01, "huffman")]
    [InlineData(0x40, "adpcm-mono")]
    [InlineData(0x80, "adpcm-stereo")]
    public void AMethodThatIsNotImplemented_IsNamedInTheError(int mask, string method)
    {
        byte[] plain = Text(500);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [new TestMpqFile("Odd.bin", plain) { StoredSectors = [[(byte)mask, 1, 2, 3, 4, 5, 6, 7, 8, 9]], CompressFlag = true }]));
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => archive.Read("Odd.bin"));
        Assert.Contains(method, e.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{mask:X2}", e.Message, StringComparison.Ordinal);
        Assert.Contains("Odd.bin", e.Message, StringComparison.Ordinal);
        Assert.Contains("sector 0", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompressedSectorOfZeroLength_ReadsAsZeros()
    {
        byte[] plain = Text(2000);
        using MpqArchive archive = Open(MpqTestWriter.Build(
            [new TestMpqFile("Hole.bin", plain) { StoredSectors = [[]], CompressFlag = true }],
            new TestMpqOptions { SectorShift = 4 }));
        Assert.Equal(new byte[2000], archive.Read("Hole.bin"));
    }

    [Fact]
    public void AnIncrementalPatchFile_IsRefused()
    {
        byte[] image = MpqTestWriter.Build([new TestMpqFile("P.bin", Text(100))], new TestMpqOptions { ListFile = false });
        using MpqArchive plain = Open(image);
        Assert.True(plain.TryFind("P.bin", out MpqEntry entry));
        MpqBlock patchBlock = entry.Block with { Flags = entry.Block.Flags | MpqFileFlags.PatchFile };
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => plain.Read(new MpqEntry(entry.HashIndex, entry.BlockIndex, entry.Locale, patchBlock), "P.bin"));
        Assert.Contains("patch", e.Message, StringComparison.Ordinal);
    }

    // ---- damaged and unsupported archives -----------------------------------------------------------------------

    [Fact]
    public void ABrokenSectorTable_IsRefused()
    {
        byte[] image = MpqTestWriter.Build([new TestMpqFile("Z.bin", Text(20000)) { Zlib = true }], new TestMpqOptions { SectorShift = 1, ListFile = false });
        long position;
        using (MpqArchive probe = Open(image))
        {
            Assert.True(probe.TryFind("Z.bin", out MpqEntry entry));
            position = probe.Header.Position + entry.Block.Offset;
        }

        // Offsets decrease: sector 1 would start before sector 0.
        byte[] decreasing = [.. image];
        BinaryPrimitives.WriteUInt32LittleEndian(decreasing.AsSpan((int)position + 4), 1);
        using (MpqArchive archive = Open(decreasing))
        {
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => archive.Read("Z.bin"));
            Assert.Contains("sector table", e.Message, StringComparison.Ordinal);
        }

        // A sector longer than the sector size.
        byte[] oversized = [.. image];
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan((int)position + 4), 0x4000);
        using (MpqArchive archive = Open(oversized))
        {
            Assert.Throws<InvalidDataException>(() => archive.Read("Z.bin"));
        }

        // The table claims the first sector begins inside the table.
        byte[] early = [.. image];
        BinaryPrimitives.WriteUInt32LittleEndian(early.AsSpan((int)position), 4);
        using (MpqArchive archive = Open(early))
        {
            Assert.Throws<InvalidDataException>(() => archive.Read("Z.bin"));
        }
    }

    [Fact]
    public void ACorruptZlibSector_NamesTheSector()
    {
        byte[] data = [.. Noise(300, 1), .. Text(4000)];
        byte[] image = MpqTestWriter.Build([new TestMpqFile("C.bin", data) { Zlib = true }], new TestMpqOptions { SectorShift = 3, ListFile = false });
        int start;
        using (MpqArchive probe = Open(image))
        {
            Assert.True(probe.TryFind("C.bin", out MpqEntry entry));
            start = (int)(probe.Header.Position + entry.Block.Offset + 12 + 4);
        }

        byte[] damaged = [.. image];
        for (int i = 0; i < 12; i++)
        {
            damaged[start + i] ^= 0xFF;
        }

        using MpqArchive archive = Open(damaged);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => archive.Read("C.bin"));
        Assert.Contains("C.bin", e.Message, StringComparison.Ordinal);
        Assert.Contains("sector 0", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHighOffset_IsJoinedIntoTheBlockOffset_AndAFileBeyondTheArchiveIsNotVisible()
    {
        byte[] image = MpqTestWriter.Build([new TestMpqFile("Far.bin", Text(100)) { HighOffset = 1 }], new TestMpqOptions { FormatVersion = 1, ListFile = false });
        using MpqArchive archive = Open(image);
        Assert.True(archive.Header.HiBlockTableOffset != 0);
        Assert.True(archive.Blocks[0].Offset > uint.MaxValue);
        Assert.Equal(1L, archive.Blocks[0].Offset >> 32);

        // StormLib only accepts a hash entry whose data starts inside the file, so the name does not resolve.
        Assert.False(archive.TryFind("Far.bin", out _));
        Assert.Throws<FileNotFoundException>(() => archive.Read("Far.bin"));

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => archive.ReadRaw(1L << 32, new byte[4]));
        Assert.Matches(new Regex(@"at offset 0x100000000 lie outside"), e.Message);
    }

    [Fact]
    public void NotAnMpq_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Open(Noise(5000)));
        Assert.Throws<InvalidDataException>(() => Open(Noise(10)));
        Assert.Throws<InvalidDataException>(() => Open([]));
    }

    [Fact]
    public void ATruncatedArchive_IsRefused()
    {
        byte[] image = MpqTestWriter.Build([new TestMpqFile("T.bin", Text(5000))]);
        Assert.Throws<InvalidDataException>(() => Open(image[..^40]));
        Assert.Throws<InvalidDataException>(() => Open(image[..64]));
    }

    [Fact]
    public void AFormatVersionTwoOrLater_IsRefusedByName()
    {
        byte[] image = MpqTestWriter.Build([new TestMpqFile("T.bin", Text(100))], new TestMpqOptions { FormatVersion = 1 });
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(12), 2);
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => Open(image));
        Assert.Contains("version 2", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHashTableThatIsNotAPowerOfTwo_IsRefused()
    {
        byte[] image = MpqTestWriter.Build([new TestMpqFile("T.bin", Text(100))]);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), 12);
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Open(image));
        Assert.Contains("power of two", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedOpen_ReleasesTheFile()
    {
        string path = _dir.Write("bad.MPQ", Noise(5000));
        Assert.Throws<InvalidDataException>(() => MpqArchive.Open(path));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // ---- concurrency --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ManyThreads_ReadTheSameArchive()
    {
        var files = new List<TestMpqFile>();
        for (int i = 0; i < 24; i++)
        {
            files.Add(new TestMpqFile($"Dir\\File{i:D2}.bin", i % 3 == 0 ? Noise(3000 + (i * 100), i) : Text(5000 + (i * 111))) { Zlib = i % 2 == 0, Encrypted = i % 4 == 0, FixKey = i % 8 == 0 });
        }

        using MpqArchive archive = Open(MpqTestWriter.Build(files, new TestMpqOptions { SectorShift = 1 }));
        Task[] workers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (int round = 0; round < 10; round++)
            {
                foreach (TestMpqFile file in files.OrderBy(f => (f.Name.GetHashCode(StringComparison.Ordinal) + w + round) % 7))
                {
                    Assert.True(file.Data.AsSpan().SequenceEqual(archive.Read(file.Name)));
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);
    }
}
