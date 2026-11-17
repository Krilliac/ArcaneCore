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

    // Canonically equivalent orders of U+0345 (combining class 240) and U+0301 (230): NFC makes both a-acute U+0345.
    [InlineData("/data/áͅx/y", "/data/áͅx", true)]
    [InlineData("/data/áͅx", "/data/áͅx", true)]

    // An ASCII character, U+0345 or an iota, then a mark. U+20DD (Me) and U+093E (Mc) have combining class 0, so NFC does not move
    // U+0345 behind them: those two pairs are one name whether the file system normalises before or after it folds. The U+0301
    // pairs below them, and the two precomposed iotas against U+0345 U+0301 after them, are one name only on a file system that
    // folds first (ext4/f2fs casefold): normalising first makes a U+0345 U+0301 into a-acute U+03B9. The rows after those are one
    // name under either order (the iota-run row because U+0345 cannot move past the iota that starts U+03AF; the rest are canonically
    // equivalent).
    [InlineData("/data/a\u03B9\u20DD/x", "/data/a\u0345\u20DD", true)]
    [InlineData("/data/a\u03B9\u093E/x", "/data/a\u0345\u093E", true)]
    [InlineData("/data/a\u03B9\u0301x", "/data/a\u0345\u0301x", true)]
    [InlineData("/data/1\u03B9\u0301x", "/data/1\u0345\u0301x", true)]
    [InlineData("/data/_\u03B9\u0301x", "/data/_\u0345\u0301x", true)]
    [InlineData("/data/a\u0399\u0301x", "/data/a\u0345\u0301x", true)]
    [InlineData("/data/a\u1FBE\u0301x", "/data/a\u0345\u0301x", true)]

    // Against a precomposed iota with that mark (U+03AF iota with tonos, U+038A capital iota with tonos).
    [InlineData("/data/a\u03AF/x", "/data/a\u0345\u0301", true)]
    [InlineData("/data/1\u03AFx", "/data/1\u0345\u0301x", true)]

    // An iota between the ASCII character and the precomposed iota: the precomposed one is looked for past the iota run too.
    [InlineData("/data/a\u0345\u03AFx", "/data/a\u03B9\u03B9\u0301x", true)]

    // Canonically equivalent pairs that folded equal before the iota rule too (both sides 'A?X' then): they pass with the whole
    // iota rule removed, and fail if either half alone (the skip set or IsIotaWithMark) is removed.
    [InlineData("/data/a\u038Ax", "/data/a\u0399\u0301x", true)]
    [InlineData("/data/a\u03AFx", "/data/a\u1FBE\u0301x", true)]

    // One member of each IsIotaWithMark range against its decomposed form, so the list is pinned.
    [InlineData("/data/a\u0390x", "/data/a\u03B9\u0308\u0301x", true)]
    [InlineData("/data/a\u03AAx", "/data/a\u0399\u0308x", true)]
    [InlineData("/data/a\u03CAx", "/data/a\u03B9\u0308x", true)]
    [InlineData("/data/a\u1F30x", "/data/a\u03B9\u0313x", true)]
    [InlineData("/data/a\u1F76x", "/data/a\u03B9\u0300x", true)]
    [InlineData("/data/a\u1FD3x", "/data/a\u03B9\u0308\u0301x", true)]
    [InlineData("/data/a\u1FDBx", "/data/a\u0399\u0301x", true)]

    // Must stay apart, so the rule cannot pass by refusing everything.
    [InlineData("/data/abc/x", "/data/abd", false)]
    [InlineData("/data/a\u0345x", "/data/b\u0345x", false)]

    // Linux vfat always, and exFAT by default, ignore trailing dots in a lookup: "WoW." opens "WoW". Only Fold drops
    // trailing spaces (vfat and exFAT do not), so rows with a trailing space are deliberate over-refusals.
    [InlineData("/mnt/usb/WoW./x", "/mnt/usb/WoW", true)]
    [InlineData("/mnt/usb/WoW.../x", "/mnt/usb/WoW", true)]
    [InlineData("/mnt/usb/WoW ./x", "/mnt/usb/WoW", true)]
    [InlineData("/mnt/usb/WoW/x", "/mnt/usb/WoW.", true)]
    [InlineData("/mnt/usb/WoW/x", "/mnt/usb/WoW. .", true)]
    [InlineData("/mnt/usb/WoWx/y", "/mnt/usb/WoW", false)]
    [InlineData("/mnt/usb/Wo.W/x", "/mnt/usb/WoW", false)]

    // The raw trailing-dot run is dropped before ignorable code points, and a component of only dots is kept.
    [InlineData("/mnt/usb/ ./x", "/mnt/usb/ ", true)]
    [InlineData("/mnt/usb/. ./x", "/mnt/usb/. ", true)]
    [InlineData("/mnt/usb/​./WoW/x", "/mnt/usb/​/WoW", true)]
    [InlineData("/mnt/usb/.../x", "/mnt/usb/..", false)]
    [InlineData("/mnt/usb/WoW.​/x", "/mnt/usb/WoW", true)]
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
