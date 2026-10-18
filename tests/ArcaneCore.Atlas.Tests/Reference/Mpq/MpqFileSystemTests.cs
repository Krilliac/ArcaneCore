using System.Text;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>The archive set as one file tree: load order, patch overrides, deletion entries and what is not mounted.</summary>
public sealed class MpqFileSystemTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private string Data => Path.Combine(_dir.Path, "Data");

    private void Archive(string name, params TestMpqFile[] files)
        => _dir.Write(Path.Combine("Data", name), MpqTestWriter.Build(files, new TestMpqOptions { FormatVersion = name.StartsWith("patch", StringComparison.Ordinal) ? (ushort)1 : (ushort)0 }));

    [Fact]
    public void LoadOrder_IsTheClientChain_ThenOtherArchives_ThenPatchesByLevel()
    {
        string[] names = ["patch-2.MPQ", "wmo.MPQ", "Fonts.MPQ", "patch.MPQ", "BASE.mpq", "patch-10.MPQ", "dbc.MPQ", "backup.MPQ", "terrain.MPQ", "patch-100.MPQ", "patch-03.MPQ"];
        IReadOnlyList<string> order = MpqFileSystem.LoadOrder(names, out IReadOnlyList<(string Name, string Reason)> skipped);

        // patch-100 is past the extractors' 2..99 scan and patch-03 is not a name "%s-%d.MPQ" produces, so both are just
        // other archives (mounted below the patches).
        Assert.Equal(new[] { "BASE.mpq", "dbc.MPQ", "terrain.MPQ", "wmo.MPQ", "Fonts.MPQ", "patch-03.MPQ", "patch-100.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-10.MPQ" }, order);
        Assert.Equal("backup.MPQ", Assert.Single(skipped).Name);
    }

    [Fact]
    public void ALaterArchive_OverridesAnEarlierOne()
    {
        Archive("dbc.MPQ", new TestMpqFile("DBFilesClient\\Map.dbc", Bytes("old map")), new TestMpqFile("DBFilesClient\\Only.dbc", Bytes("base only")));
        Archive("patch.MPQ", new TestMpqFile("DBFilesClient\\Map.dbc", Bytes("patched map")) { Zlib = true });
        Archive("patch-2.MPQ", new TestMpqFile("World\\Maps\\Azeroth\\Azeroth.wdt", Bytes("wdt")) { Encrypted = true, FixKey = true });

        using MpqFileSystem files = MpqFileSystem.Open(Data);
        Assert.Equal(new[] { "dbc.MPQ", "patch.MPQ", "patch-2.MPQ" }, files.Archives.Select(a => a.Name));
        Assert.Equal(Bytes("patched map"), files.Read("dbfilesclient/map.DBC"));
        Assert.Equal("patch.MPQ", files.ArchiveOf("DBFilesClient\\Map.dbc"));
        Assert.Equal("dbc.MPQ", files.ArchiveOf("DBFilesClient\\Only.dbc"));
        Assert.Equal(Bytes("wdt"), files.Read("World/Maps/Azeroth/Azeroth.wdt"));
        Assert.Null(files.Read("DBFilesClient\\Absent.dbc"));
        Assert.Null(files.ArchiveOf("DBFilesClient\\Absent.dbc"));
        Assert.False(files.Exists(string.Empty));

        Assert.Equal(new[] { "DBFilesClient\\Map.dbc", "DBFilesClient\\Only.dbc", "World\\Maps\\Azeroth\\Azeroth.wdt" }, files.List(string.Empty));
        Assert.Equal(new[] { "DBFilesClient\\Map.dbc", "DBFilesClient\\Only.dbc" }, files.List("dbfilesclient/"));
        Assert.Empty(files.List("Sound\\"));
        Assert.Equal(3, files.ListedFileCount);
        Assert.Contains("dbc.MPQ < patch.MPQ < patch-2.MPQ", files.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void List_IsInOrdinalOrder_ForMixedCaseNames()
    {
        // IClientFileSource.List promises ordinal order of the returned names (Core/ClientFiles.cs:18); the index is ordered by
        // upper-cased key, which differs from it for mixed case and for '_' against letters.
        Archive("dbc.MPQ", new TestMpqFile("abc", Bytes("1")), new TestMpqFile("Bcd", Bytes("2")), new TestMpqFile("_x", Bytes("3")), new TestMpqFile("Zed", Bytes("4")));
        using MpqFileSystem files = MpqFileSystem.Open(Data);
        IReadOnlyList<string> listed = files.List(string.Empty);
        Assert.Equal(4, listed.Count);
        Assert.Equal(listed.Order(StringComparer.Ordinal), listed);
    }

    [Fact]
    public void ADeletionEntryInAPatch_HidesTheEarlierCopy()
    {
        Archive("dbc.MPQ", new TestMpqFile("DBFilesClient\\SpellAuraNames.dbc", Bytes("older copy")), new TestMpqFile("DBFilesClient\\Kept.dbc", Bytes("kept")));
        Archive("patch.MPQ", new TestMpqFile("DBFilesClient\\SpellAuraNames.dbc", []) { Delete = true });

        using MpqFileSystem files = MpqFileSystem.Open(Data);
        Assert.False(files.Exists("DBFilesClient\\SpellAuraNames.dbc"));
        Assert.Null(files.Read("DBFilesClient\\SpellAuraNames.dbc"));
        Assert.Null(files.ArchiveOf("DBFilesClient\\SpellAuraNames.dbc"));
        Assert.Equal("patch.MPQ", files.DeletedBy("DBFilesClient\\SpellAuraNames.dbc"));
        Assert.Null(files.DeletedBy("DBFilesClient\\Kept.dbc"));
        Assert.Equal(new[] { "DBFilesClient\\Kept.dbc" }, files.List("DBFilesClient\\"));
    }

    [Fact]
    public void BackupAndLocaleFolderArchives_AreReportedAsNotMounted()
    {
        Archive("base.MPQ", new TestMpqFile("Launcher.txt", Bytes("launcher")));
        Archive("backup.MPQ", new TestMpqFile("Launcher.txt", Bytes("installer copy")));
        _dir.Write(Path.Combine("Data", "enUS", "locale-enUS.MPQ"), MpqTestWriter.Build([new TestMpqFile("Interface\\Strings.lua", Bytes("x"))]));

        using MpqFileSystem files = MpqFileSystem.Open(Data);
        Assert.Equal("base.MPQ", Assert.Single(files.Archives).Name);
        Assert.Equal(Bytes("launcher"), files.Read("Launcher.txt"));
        Assert.Equal(new[] { "backup.MPQ", "locale-enUS.MPQ" }, files.Skipped.Select(s => Path.GetFileName(s.Path)).Order(StringComparer.Ordinal));
        Assert.All(files.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        Assert.True(MpqFileSystem.HasArchives(Data));
    }

    [Fact]
    public void Open_RefusesAFolderWithNothingToMount_AndADamagedArchive()
    {
        Directory.CreateDirectory(Data);
        Assert.False(MpqFileSystem.HasArchives(Data));
        Assert.Throws<DirectoryNotFoundException>(() => MpqFileSystem.Open(Path.Combine(_dir.Path, "Nope")));

        Archive("backup.MPQ", new TestMpqFile("a", Bytes("a")));
        Assert.Contains("no mountable", Assert.Throws<InvalidDataException>(() => MpqFileSystem.Open(Data)).Message, StringComparison.Ordinal);

        Archive("dbc.MPQ", new TestMpqFile("a", Bytes("a")));
        _dir.Write(Path.Combine("Data", "patch.MPQ"), Bytes("this is not an archive at all, just text that is long enough"));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => MpqFileSystem.Open(Data));
        Assert.Contains("Cannot mount", error.Message, StringComparison.Ordinal);
        Assert.Contains("patch.MPQ", error.Message, StringComparison.Ordinal);
    }
}
