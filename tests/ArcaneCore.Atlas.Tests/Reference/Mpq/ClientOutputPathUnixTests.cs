using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// The Unix rules of the output-folder guard. The link resolution and the protected-folder comparison are pure functions of what
/// <c>lstat</c> answers, so they run on every platform against an in-memory tree; the facts that need a real Unix file system are
/// marked <see cref="UnixFactAttribute"/>.
/// </summary>
public sealed class ClientOutputPathUnixTests
{
    private const string Command = "extract.test";

    /// <summary>An in-memory tree: each entry is a directory, a file or a link with its text; anything else is missing.</summary>
    private static FakeOutputPathPlatform Tree(params (string Path, UnixEntry Entry)[] entries)
    {
        Dictionary<string, UnixEntry> map = entries.ToDictionary(e => e.Path, e => e.Entry, StringComparer.Ordinal);
        return new FakeOutputPathPlatform { LstatOverride = path => map.TryGetValue(path, out UnixEntry entry) ? entry : new UnixEntry(UnixEntryKind.Missing, null, null) };
    }

    private static (string, UnixEntry) Dir(string path) => (path, new UnixEntry(UnixEntryKind.Directory, null, null));

    private static (string, UnixEntry) FileAt(string path) => (path, new UnixEntry(UnixEntryKind.File, null, null));

    private static (string, UnixEntry) Link(string path, string target) => (path, new UnixEntry(UnixEntryKind.Link, target, null));

    private static CommandException Refused(FakeOutputPathPlatform tree, string full)
        => Assert.Throws<CommandException>(() => ClientOutputPath.UnixRealPath(Command, "'out'", full, tree));

    [Fact]
    public void N9_ALinkWhoseTargetPassesThroughAnotherLink_IsResolvedAllTheWay()
    {
        // /c -> /b/inside, and /b -> /a/repo: the old code stopped at "/b/inside" (it never re-walked the target) and judged the wrong ancestors.
        FakeOutputPathPlatform tree = Tree(Dir("/a"), Dir("/a/repo"), Dir("/a/repo/inside"), Link("/b", "/a/repo"), Link("/c", "/b/inside"));

        UnixCanonical canonical = ClientOutputPath.UnixRealPath(Command, "'out'", "/c/out", tree);

        Assert.Equal("/a/repo/inside/out", canonical.Path);
        Assert.False(canonical.LeafExists);
    }

    [Fact]
    public void N9_RelativeLinkTargets_AreResolvedFromTheLinksFolder()
    {
        FakeOutputPathPlatform tree = Tree(Dir("/p"), Dir("/p/q"), Dir("/p/q/r"), Link("/p/l", "q/r"), Link("/p/up", "../p/q"));

        Assert.Equal("/p/q/r", ClientOutputPath.UnixRealPath(Command, "'out'", "/p/l", tree).Path);
        Assert.Equal("/p/q/r/x", ClientOutputPath.UnixRealPath(Command, "'out'", "/p/up/r/x", tree).Path);
        Assert.True(ClientOutputPath.UnixRealPath(Command, "'out'", "/p/up", tree).LeafIsDirectory);
    }

    [Fact]
    public void N9_MoreThanFortyLinks_AreRefused()
    {
        var entries = new List<(string, UnixEntry)>();
        for (int i = 0; i < 45; i++)
        {
            entries.Add(Link($"/l{i}", $"/l{i + 1}"));
        }

        entries.Add(Dir("/l45"));
        Assert.Contains("40 symbolic links", Refused(Tree([.. entries]), "/l0/out").Message, StringComparison.Ordinal);
        Assert.Contains("40 symbolic links", Refused(Tree(Link("/loop", "/loop")), "/loop/out").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N9_ADanglingLink_IsRefused()
    {
        FakeOutputPathPlatform tree = Tree(Dir("/data"), Link("/data/link", "/nowhere/folder"));

        Assert.Contains("does not exist", Refused(tree, "/data/link/out").Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", Refused(tree, "/data/link").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegularFileInTheMiddle_IsRefused_AndAMissingTailIsKept()
    {
        FakeOutputPathPlatform tree = Tree(Dir("/data"), FileAt("/data/file"));

        Assert.Contains("passes through the file", Refused(tree, "/data/file/out").Message, StringComparison.Ordinal);
        UnixCanonical fresh = ClientOutputPath.UnixRealPath(Command, "'out'", "/data/new/deeper", tree);
        Assert.Equal("/data/new/deeper", fresh.Path);
        Assert.False(fresh.LeafExists);
    }

    [Fact]
    public void AnUnreadableComponent_IsRefused()
    {
        var tree = new FakeOutputPathPlatform
        {
            LstatOverride = path => path == "/data/locked"
                ? new UnixEntry(UnixEntryKind.Error, null, "Permission denied")
                : new UnixEntry(path == "/data" ? UnixEntryKind.Directory : UnixEntryKind.Missing, null, null),
        };

        Assert.Contains("cannot be read", Refused(tree, "/data/locked/out").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mnt/c/games/WOW/x", "/mnt/c/Games/WoW", true)]
    [InlineData("/mnt/c/Games/WoW", "/mnt/c/games/wow/", true)]
    [InlineData("/data/Café/x", "/data/Café", true)]
    [InlineData("/data/Café", "/data/Café", true)]
    [InlineData("/mnt/c/Games/WoW-extra/x", "/mnt/c/Games/WoW", false)]
    [InlineData("/mnt/c/Games", "/mnt/c/Games/WoW", false)]
    [InlineData("/anything", "/", true)]
    [InlineData("/data/Été/x", "/data/été", true)]
    [InlineData("/data/Cafe/x", "/data/Café", false)]
    [InlineData("/data/́x/y", "/data/́x", true)]

    // Coarser than NFC: two different accented names of the same shape compare equal, which only over-refuses.
    [InlineData("/data/Cafè/x", "/data/Café", true)]

    // Non-ASCII characters whose NFC form, case folding or upper case is ASCII, and default-ignorable code points, which a
    // normalising or case-folding file system drops: each spelling opens the protected folder there.
    [InlineData("/mnt/c/Gameſ/WoW/x", "/mnt/c/Games/WoW", true)]
    [InlineData("/data/Keep/x", "/data/keep", true)]
    [InlineData("/data/Dır", "/data/dir", true)]
    [InlineData("/data/Straße/x", "/data/strasse", true)]
    [InlineData("/data/ẞe", "/data/SSE", true)]
    [InlineData("/data/ﬁle", "/data/file", true)]
    [InlineData("/data/ﬆop", "/data/stop", true)]
    [InlineData("/data/a;b/x", "/data/a;b", true)]
    [InlineData("/data/a`b", "/data/a`b", true)]
    [InlineData("/data/wo‍w/x", "/data/wow", true)]
    [InlineData("/data/w\U000E0001ow", "/data/wow", true)]
    [InlineData("/data/­wow", "/data/wow", true)]
    [InlineData("/data/Ḱ/x", "/data/Ḱ", true)]
    [InlineData("/data/İx", "/data/i̇x", true)]
    [InlineData("/mnt/c/Games-x/WoW", "/mnt/c/Gameſ", false)]

    // Full case folding that gives ASCII next to a modifier letter (U+0149 to U+02BC n, U+1E9A to a U+02BE), and U+0345, a
    // combining mark that case-folds to the letter U+03B9.
    [InlineData("/data/ŉx", "/data/ʼnx", true)]
    [InlineData("/data/ẚx/y", "/data/aʾx", true)]
    [InlineData("/data/aͅx", "/data/aιx", true)]
    public void N10_TheProtectedFolderComparison_IgnoresCaseAndUnicodeNormalisation(string path, string folder, bool inside)
    {
        Assert.Equal(inside, ClientOutputPath.UnixInsideOrEqual(path, folder));
    }

    /// <summary>
    /// /d/x/link1 leads to /e/repo/sub, and /e/repo/sub/link2 to /f/plain: neither the typed path nor the real one (/f/plain/out) is
    /// inside /e/repo, but the folder holding link2 is.
    /// </summary>
    private static FakeOutputPathPlatform ChainedLinks(bool repository)
    {
        FakeOutputPathPlatform tree = Tree(
            Dir("/d"), Dir("/d/x"), Link("/d/x/link1", "/e/repo/sub"),
            Dir("/e"), Dir("/e/repo"), Dir("/e/repo/sub"), Link("/e/repo/sub/link2", "/f/plain"),
            Dir("/f"), Dir("/f/plain"));
        return new FakeOutputPathPlatform
        {
            LstatOverride = tree.LstatOverride,
            ProbeOverride = path => new EntryProbe(repository && path == "/e/repo/.git" ? EntryKind.Directory : EntryKind.Missing, null),
        };
    }

    [Fact]
    public void N1_AChainOfLinks_ThroughAFolderInsideARepository_IsRefused()
    {
        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectoryUnix(Command, "/d/x/link1/link2/out", [], ChainedLinks(repository: true)));

        Assert.Contains("git working tree '/e/repo'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N1_TheSameChainOfLinks_WithoutARepository_IsAccepted_AndTheRealPathIsReturned()
    {
        Assert.Equal("/f/plain/out", ClientOutputPath.CheckDirectoryUnix(Command, "/d/x/link1/link2/out", [], ChainedLinks(repository: false)));
    }

    [UnixFact]
    public void KernelFileSystems_AreRefused()
    {
        foreach (string path in new[] { "/proc/self/out", "/sys/out", "/dev/shm/out", "/proc" })
        {
            CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, path, []));
            Assert.Contains("kernel", e.Message, StringComparison.Ordinal);
        }
    }

    [UnixFact]
    public void N9_ANestedRealSymlinkIntoARepository_IsRefused()
    {
        using var scratch = new TestDirectory();
        string tree = GuardFixtures.WorkingTree(scratch.Path, "repo", out _);
        string first = Path.Combine(scratch.Path, "first");
        string second = Path.Combine(scratch.Path, "second");
        Directory.CreateSymbolicLink(first, tree);
        Directory.CreateSymbolicLink(second, Path.Combine(first, "inside"));

        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, Path.Combine(second, "out"), []));

        Assert.Contains("git working tree", e.Message, StringComparison.Ordinal);
    }
}
