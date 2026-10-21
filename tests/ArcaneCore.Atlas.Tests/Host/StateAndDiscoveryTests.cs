using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>A test that only runs where the operating system has Unix file permissions.</summary>
internal sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file modes do not exist on Windows; the owner-only mode of the discovery file is checked on Linux and macOS.";
        }
    }
}

/// <summary>A temporary state root, deleted on dispose.</summary>
internal sealed class TempState : IDisposable
{
    public TempState()
    {
        State = new AtlasState(Path.Combine(Path.GetTempPath(), "atlas-state-" + Guid.NewGuid().ToString("N")));
    }

    public AtlasState State { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(State.Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }
}

public sealed class AtlasStateTests
{
    [Fact]
    public void ThePathsHangOffTheRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-root");
        var state = new AtlasState(root);

        Assert.Equal(Path.GetFullPath(root), state.Root);
        Assert.Equal(Path.Combine(state.Root, "instances"), state.InstancesDirectory);
        Assert.Equal(Path.Combine(state.Root, "bookmarks.json"), state.BookmarksFile);
        Assert.Equal(Path.Combine(state.Root, "captures"), state.CapturesDirectory);
        Assert.Throws<ArgumentException>(() => new AtlasState(" "));
    }

    [Fact]
    public void WithoutTheOverride_TheRootIsUnderTheUsersApplicationData_AndTheOverrideWins()
    {
        string? saved = Environment.GetEnvironmentVariable(AtlasState.EnvironmentVariable);
        try
        {
            string overridden = Path.Combine(Path.GetTempPath(), "atlas-override-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(AtlasState.EnvironmentVariable, overridden);
            Assert.Equal(Path.GetFullPath(overridden), AtlasState.FromEnvironment().Root);

            Environment.SetEnvironmentVariable(AtlasState.EnvironmentVariable, "   ");
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string expected = string.IsNullOrEmpty(appData) ? Path.GetTempPath() : appData;
            Assert.Equal(Path.GetFullPath(Path.Combine(expected, "ArcaneCore", "atlas")), AtlasState.FromEnvironment().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AtlasState.EnvironmentVariable, saved);
        }
    }

    [Fact]
    public void TheEnvironmentVariableNameIsTheDocumentedOne()
    {
        Assert.Equal("ARCANECORE_ATLAS_STATE_DIR", AtlasState.EnvironmentVariable);
    }
}

public sealed class DiscoveryTests
{
    private static DiscoveryRecord Record(int pid, string token = "tok", DateTimeOffset? started = null, int port = 5000)
        => new(pid, port, token, started ?? DateTimeOffset.UtcNow, "1.2.3", "D:/data", null);

    [Fact]
    public void ARecordIsWrittenAsAJsonFileNamedAfterThePid_AndReadBackIdentically()
    {
        using var temp = new TempState();
        DiscoveryRecord record = Record(4242, "secret-token", port: 51234);

        string file = Discovery.Write(temp.State, record);

        Assert.Equal(Path.Combine(temp.State.InstancesDirectory, "4242.json"), file);
        JsonNode json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(4242, json.Num("pid"));
        Assert.Equal(51234, json.Num("port"));
        Assert.Equal("secret-token", json.Str("token"));
        Assert.Equal("1.2.3", json.Str("version"));
        Assert.Equal("D:/data", json.Str("terrainData"));
        Assert.Null(json.At("clientRoot"));
        Assert.Equal(record, Assert.Single(Discovery.Live(temp.State, _ => true)));
        Assert.Empty(Directory.GetFiles(temp.State.InstancesDirectory, "*.tmp"));
    }

    [Fact]
    public void WritingAgain_ReplacesTheFile()
    {
        using var temp = new TempState();
        Discovery.Write(temp.State, Record(4242, "first"));

        Discovery.Write(temp.State, Record(4242, "second"));

        Assert.Equal("second", Assert.Single(Discovery.Live(temp.State, _ => true)).Token);
    }

    [Fact]
    public void DeleteRemovesTheFile_OnlyWhileItStillHoldsTheSameToken()
    {
        using var temp = new TempState();
        DiscoveryRecord mine = Record(4242, "mine");
        string file = Discovery.Write(temp.State, mine);

        Discovery.Write(temp.State, Record(4242, "a newer run"));
        Discovery.Delete(temp.State, mine);
        Assert.True(File.Exists(file));

        Discovery.Delete(temp.State, Record(4242, "a newer run"));
        Assert.False(File.Exists(file));
        Discovery.Delete(temp.State, mine);
    }

    [Fact]
    public void Live_ListsOldestFirst_AndPrunesTheFilesOfDeadProcesses()
    {
        using var temp = new TempState();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string alive = Discovery.Write(temp.State, Record(10, "a", now.AddMinutes(-1)));
        string dead = Discovery.Write(temp.State, Record(20, "b", now.AddMinutes(-3)));
        string older = Discovery.Write(temp.State, Record(30, "c", now.AddMinutes(-5)));

        IReadOnlyList<DiscoveryRecord> live = Discovery.Live(temp.State, r => r.Pid != 20);

        Assert.Equal(new[] { 30, 10 }, live.Select(r => r.Pid));
        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(alive));
        Assert.True(File.Exists(older));
    }

    [Fact]
    public void Live_IgnoresUnreadableAndIncompleteFiles_WithoutDeletingThem()
    {
        using var temp = new TempState();
        Directory.CreateDirectory(temp.State.InstancesDirectory);
        string[] bad =
        [
            "not json",
            "[]",
            """{"pid":1,"port":2}""",
            """{"pid":0,"port":80,"token":"t","started":"2026-01-01T00:00:00Z"}""",
            """{"pid":1,"port":70000,"token":"t","started":"2026-01-01T00:00:00Z"}""",
            """{"pid":1,"port":80,"token":"","started":"2026-01-01T00:00:00Z"}""",
            """{"pid":1,"port":80,"token":"t","started":"yesterday"}""",
        ];
        for (int i = 0; i < bad.Length; i++)
        {
            File.WriteAllText(Path.Combine(temp.State.InstancesDirectory, $"bad{i}.json"), bad[i]);
        }

        Assert.Empty(Discovery.Live(temp.State, _ => false));
        Assert.Equal(bad.Length, Directory.GetFiles(temp.State.InstancesDirectory, "bad*.json").Length);
        Assert.Empty(Discovery.Live(new AtlasState(Path.Combine(temp.State.Root, "missing"))));
    }

    [Fact]
    public void ARecordNeedsOnlyItsCoreFields_AndToleratesExtras()
    {
        DiscoveryRecord? parsed = DiscoveryRecord.Parse(JsonNode.Parse("""{"pid":7,"port":8,"token":"t","started":"2026-02-03T04:05:06Z","future":{"x":1}}"""));

        Assert.NotNull(parsed);
        Assert.Equal("unknown", parsed.Version);
        Assert.Null(parsed.TerrainData);
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), parsed.Started);
    }

    [Fact]
    public void IsAlive_IsTrueForThisProcess_FalseForAGonePid_AndFalseForAReusedPid()
    {
        using Process self = Process.GetCurrentProcess();
        DateTimeOffset started = self.StartTime.ToUniversalTime();

        Assert.True(Discovery.IsAlive(Record(self.Id, started: DateTimeOffset.UtcNow)));
        Assert.True(Discovery.IsAlive(Record(self.Id, started: started)));
        Assert.False(Discovery.IsAlive(Record(self.Id, started: started.AddHours(-1))), "a pid whose process started after the instance wrote its file belongs to someone else");
        Assert.False(Discovery.IsAlive(Record(int.MaxValue)));
    }

    [Fact]
    public async Task IsAlive_IsFalseOnceTheProcessHasExited()
    {
        DiscoveryRecord record;
        using (Process child = StartShortLivedProcess())
        {
            DateTimeOffset started;
            try
            {
                started = child.StartTime.ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                started = DateTimeOffset.UtcNow;
            }

            record = Record(child.Id, started: started);
            await child.WaitForExitAsync();
        }

        long deadline = Environment.TickCount64 + 500;
        while (Discovery.IsAlive(record) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }


        Assert.False(Discovery.IsAlive(record));
    }

    [UnixOnlyFact]
    public void OnUnix_TheFileIsReadableByItsOwnerOnly_BecauseItHoldsTheToken()
    {
        using var temp = new TempState();

        string file = Discovery.Write(temp.State, Record(4242));

        // UnixOnlyFact already skips on Windows; the guard tells the platform analyzer the same thing.
        Assert.False(OperatingSystem.IsWindows());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    private static Process StartShortLivedProcess()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        return Process.Start(start)!;
    }
}

public sealed partial class BookmarkTests
{
    private static readonly Regex Numbers = NumberPattern();

    [Fact]
    public void BuiltInBookmarks_HaveUniqueValidNames_PositionsOnTheGrid_AndSaneCameras()
    {
        IReadOnlyList<Bookmark> all = BuiltInBookmarks.All;

        Assert.Equal(all.Count, all.Select(b => b.Name.ToLowerInvariant()).Distinct().Count());
        foreach (Bookmark bookmark in all)
        {
            Assert.True(Bookmark.IsValidName(bookmark.Name), bookmark.Name);
            Assert.True(bookmark.BuiltIn);
            Assert.NotNull(WorldGrid.TileOf(bookmark.Map, bookmark.Target.X, bookmark.Target.Y));
            Assert.InRange(bookmark.Distance, CameraState.MinDistance, CameraState.MaxDistance);
            Assert.InRange(bookmark.Pitch, -CameraState.MaxPitch, CameraState.MaxPitch);
            Assert.False(string.IsNullOrWhiteSpace(bookmark.Note));
            Assert.Matches(@"^(src|tests)/[\w./-]+\.cs:\d+$", bookmark.Source!);
        }
    }

    [Fact]
    public void EveryBuiltInBookmark_MatchesTheRepositoryLineItCites()
    {
        string root = RepositoryRoot();

        foreach (Bookmark bookmark in BuiltInBookmarks.All)
        {
            (string path, int line) = SplitSource(bookmark.Source!);
            string[] lines = File.ReadAllLines(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(line >= 1 && line <= lines.Length, $"{bookmark.Name}: {bookmark.Source} is past the end of the file");
            double[] found = [.. Numbers.Matches(lines[line - 1]).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))];

            bool cited = false;
            for (int i = 0; i + 2 < found.Length; i++)
            {
                cited |= found[i] == bookmark.Target.X && found[i + 1] == bookmark.Target.Y && found[i + 2] == bookmark.Target.Z;
            }

            Assert.True(cited, $"{bookmark.Name}: ({bookmark.Target.X}, {bookmark.Target.Y}, {bookmark.Target.Z}) does not appear in order on {bookmark.Source}: {lines[line - 1].Trim()}");
        }
    }

    [Fact]
    public void TheMapIdsOfTheBookmarks_AreTheOnesTheCitedFilesDeclare()
    {
        string root = RepositoryRoot();
        string terrainTests = File.ReadAllText(Path.Combine(root, "tests", "ArcaneCore.Game.Tests", "Collision", "RealTerrainDataTests.cs"));
        string[] terrainLines = File.ReadAllLines(Path.Combine(root, "tests", "ArcaneCore.Game.Tests", "Collision", "RealTerrainDataTests.cs"));
        string elevatorData = File.ReadAllText(Path.Combine(root, "tests", "ArcaneCore.World.Tests", "Playerbots", "Elevator", "ElevatorTestData.cs"));

        Assert.Contains("EasternKingdoms = 0", terrainLines[29], StringComparison.Ordinal);
        Assert.Contains("Kalimdor = 1", terrainLines[30], StringComparison.Ordinal);
        Assert.Matches(@"Kalimdor\s*=\s*1", elevatorData);
        Assert.Contains("EasternKingdoms", terrainTests, StringComparison.Ordinal);
        foreach (string name in new[] { "goldshire-inn", "northshire-abbey", "northshire-start", "deathknell-crypt", "coldridge-valley", "ironforge", "undercity", "undercity-lift", "booty-bay" })
        {
            Assert.True(BuiltInBookmarks.All.Single(b => b.Name == name).Map == 0, name);
        }

        foreach (string name in new[] { "valley-of-trials", "shadowglen", "camp-narache", "thunder-bluff-mesa", "ruttheran-village" })
        {
            Assert.True(BuiltInBookmarks.All.Single(b => b.Name == name).Map == 1, name);
        }
    }

    [Fact]
    public void ABookmarkJsonCarriesItsCameraAndSource()
    {
        Bookmark inn = BuiltInBookmarks.All.Single(b => b.Name == "goldshire-inn");

        JsonObject json = inn.ToJson();
        JsonObject patch = inn.ToPatch();

        Assert.True(json.Flag("builtin"));
        Assert.Equal(-9462.66, json.Num("at.0"));
        Assert.Equal("orbit", json.Str("mode"));
        Assert.Equal(0, patch.Num("map"));
        Assert.Equal(inn.Distance, patch.Num("camera.distance"));
        Assert.Null(patch.At("camera.fov"));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Lion's Pride Inn", true)]
    [InlineData("spot_1.2-b", true)]
    [InlineData("9lives", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("-dash", false)]
    [InlineData("tab\there", false)]
    [InlineData("new\nline", false)]
    [InlineData("quote\"d", false)]
    [InlineData("path/sep", false)]
    [InlineData("back\\slash", false)]
    [InlineData("<b>", false)]
    [InlineData("émigré", false)]
    public void NamesAreRestrictedToASafeAlphabet(string name, bool valid)
    {
        Assert.Equal(valid, Bookmark.IsValidName(name));
    }

    [Fact]
    public void TheLongestNameIs64Characters()
    {
        Assert.True(Bookmark.IsValidName(new string('a', 64)));
        Assert.False(Bookmark.IsValidName(new string('a', 65)));
    }

    [Fact]
    public void SavedBookmarks_AreReadBackWithEveryField_AndABookmarkFileWithABadEntryIsKeptAside()
    {
        using var temp = new TempState();
        var store = new BookmarkStore(temp.State.BookmarksFile, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var saved = new Bookmark("Spot", 1, new WorldPoint(1.5, -2.25, 3), 77.5, 123.5, -12.5, CameraMode.Fly, "a note", null);

        store.Save(saved);
        var reopened = new BookmarkStore(temp.State.BookmarksFile, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        Assert.Equal(saved, Assert.Single(reopened.All(), b => !b.BuiltIn));
        Assert.Null(reopened.LoadProblem);

        File.WriteAllText(temp.State.BookmarksFile, """{"version":1,"bookmarks":[{"name":"x","map":0,"at":[1,2],"distance":5,"yaw":0,"pitch":0,"mode":"orbit"}]}""");
        var damaged = new BookmarkStore(temp.State.BookmarksFile, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        Assert.NotNull(damaged.LoadProblem);
        Assert.DoesNotContain(damaged.All(), b => !b.BuiltIn);
    }

    [Fact]
    public void FindingABookmark_IgnoresCase_AndSavingOverAnExistingNameReplacesIt()
    {
        using var temp = new TempState();
        var store = new BookmarkStore(temp.State.BookmarksFile, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        store.Save(new Bookmark("Spot", 0, new WorldPoint(1, 2, 3), 10, 0, 45, CameraMode.Orbit, null, null));
        store.Save(new Bookmark("SPOT", 0, new WorldPoint(4, 5, 6), 10, 0, 45, CameraMode.Orbit, null, null));

        Assert.True(store.TryGet("spot", out Bookmark found));
        Assert.Equal(4, found.Target.X);
        Assert.Equal(1, store.All().Count(b => !b.BuiltIn));
        Assert.True(store.TryGet("GOLDSHIRE-INN", out _));
        Assert.False(store.TryGet("missing", out _));
    }

    private static (string Path, int Line) SplitSource(string source)
    {
        int colon = source.LastIndexOf(':');
        return (source[..colon], int.Parse(source[(colon + 1)..], CultureInfo.InvariantCulture));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArcaneCore.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ArcaneCore.slnx was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
