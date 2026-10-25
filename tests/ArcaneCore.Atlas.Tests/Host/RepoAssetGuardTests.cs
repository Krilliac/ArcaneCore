using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// Client assets are copyrighted and must never enter the repository (textures, models, DBCs, MPQs, or anything derived from them,
/// screenshots and fixtures included). This guard fails the build when a tracked file has a client-asset extension or starts with a
/// client-asset magic. It lists files with <c>git ls-files</c> in a checkout and walks the directory in a source archive, and it
/// proves itself: a planted BLP2 must be caught, and a listing of 1000 files or fewer fails, because a guard that scanned nothing
/// would look exactly like a clean repository. Both modes must also sniff at least one binary file (the committed
/// <c>Fixtures/sniff-probe.bin</c>, which is not a client asset), and the REVM check is stricter than the design (a bare REVM at the
/// start, not REVM followed by DHOM/PGOM/RDHM).
/// </summary>
public sealed class RepoAssetGuardTests(ITestOutputHelper output)
{
    private static readonly string[] BannedExtensions =
    [
        ".blp", ".m2", ".mpq", ".dbc", ".adt", ".wmo", ".wdt", ".wdl", ".skin", ".anim", ".bls", ".bone", ".tga",
    ];

    private static readonly (string Name, byte[] Magic)[] Magics =
    [
        ("BLP2", "BLP2"u8.ToArray()),
        ("BLP1", "BLP1"u8.ToArray()),
        ("M2 (MD20)", "MD20"u8.ToArray()),
        ("WDBC", "WDBC"u8.ToArray()),
        ("MPQ archive", [0x4D, 0x50, 0x51, 0x1A]),
        ("MPQ user data", [0x4D, 0x50, 0x51, 0x1B]),
        ("ADT/WDT/WMO chunk file (REVM)", "REVM"u8.ToArray()),
    ];

    [Fact]
    public void NoTrackedFile_IsAClientAsset()
    {
        string root = RepositoryRoot();

        (IReadOnlyList<string> files, string mode) = ListFiles(root, r => GitList(r, null), InCi());
        output.WriteLine($"listing mode: {mode}; {files.Count} files");

        Assert.True(files.Count > 1000, $"only {files.Count} files listed: the guard is not looking at the repository");
        Assert.Empty(Violations(root, files));
        Assert.True(CountBinary(root, files) >= 1, "no binary file was sniffed: the guard is not reading file contents");
    }

    [Fact]
    public void TheSourceArchiveWalk_CoversTheRepository_AndSniffsBinaryFiles()
    {
        // In a git checkout the working tree also holds untracked and gitignored files (extracted client data on a developer box), which
        // are not part of the repository, so the walk runs over a real archive of HEAD (git archive, zip) instead of the live tree.
        // In a source archive (no .git) the tree itself is the archive and is walked directly.
        // git archive honours export-ignore, so export-ignored paths are covered by the ls-files mode (NoTrackedFile_IsAClientAsset)
        // but not by this walk.
        string root = RepositoryRoot();
        using var extracted = new TestDirectory();
        string walkRoot = root;
        if (IsCheckout(root))
        {
            string zip = extracted.NewName(".zip");
            walkRoot = extracted.NewName(string.Empty);
            RunGit(root, null, null, "archive", "--format=zip", "-o", zip, "HEAD");
            ZipFile.ExtractToDirectory(zip, walkRoot);
            File.Delete(zip);
        }

        output.WriteLine($"archive walk over: {(walkRoot == root ? "the source archive tree" : "git archive HEAD")}");
        string[] files = [.. Walk(walkRoot)];

        Assert.True(files.Length > 1000, $"only {files.Length} files walked: the archive mode is not looking at the repository");
        Assert.Empty(Violations(walkRoot, files));
        Assert.True(CountBinary(walkRoot, files) >= 1, "no binary file was sniffed in the archive walk");
    }

    [Fact]
    public void TheGuard_CatchesAPlantedBlp2_ByMagicEvenUnderAnInnocentName()
    {
        using var directory = new TestDirectory();
        directory.Write("src/notes.txt", Encoding.ASCII.GetBytes("BLP2").Concat(new byte[64]).ToArray());
        directory.Write("src/fine.txt", "hello"u8.ToArray());

        string[] hits = Violations(directory.Path, Walk(directory.Path)).ToArray();

        Assert.Single(hits);
        Assert.Contains("notes.txt", hits[0], StringComparison.Ordinal);
        Assert.Contains("BLP2", hits[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b/Wolf.blp")]
    [InlineData("a/Wolf.M2")]
    [InlineData("data/CreatureDisplayInfo.dbc")]
    [InlineData("data/patch.MPQ")]
    [InlineData("shot/Probe.tga")]
    public void TheGuard_CatchesAClientExtension_EvenWhenTheFileIsEmpty(string relative)
    {
        using var directory = new TestDirectory();
        directory.Write(relative, []);

        string[] hits = Violations(directory.Path, Walk(directory.Path)).ToArray();

        Assert.Single(hits);
        Assert.Contains("extension", hits[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuard_ReportsAClientAssetWhoseNameContainsABackslash()
    {
        // On Linux this is one file whose name contains a backslash; on Windows it is a\x.blp. Either way it must be reported, not skipped.
        using var directory = new TestDirectory();
        directory.Write("a\\x.blp", []);

        string[] hits = Violations(directory.Path, Walk(directory.Path)).ToArray();

        Assert.Single(hits);
        Assert.Contains("extension", hits[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WDBC")]
    [InlineData("MD20")]
    [InlineData("REVM")]
    public void TheGuard_CatchesEveryMagic(string magic)
    {
        using var directory = new TestDirectory();
        directory.Write("x.bin", Encoding.ASCII.GetBytes(magic).Concat(new byte[16]).ToArray());

        Assert.Single(Violations(directory.Path, Walk(directory.Path)));
    }

    [Fact]
    public void TheGuard_CatchesTheMpqMagic()
    {
        using var directory = new TestDirectory();
        directory.Write("x.bin", [0x4D, 0x50, 0x51, 0x1A, 0, 0, 0, 0]);

        Assert.Single(Violations(directory.Path, Walk(directory.Path)));
    }

    [Fact]
    public void TheGuard_LeavesShortFilesAndSourceTextAlone()
    {
        using var directory = new TestDirectory();
        directory.Write("empty.txt", []);
        directory.Write("three.txt", "BLP"u8.ToArray());
        directory.Write("code.cs", "// BLP2 is mentioned in a comment, not at the start\n"u8.ToArray());

        Assert.Empty(Violations(directory.Path, Walk(directory.Path)));
    }

    [Fact]
    public void TheWalk_SkipsBuildOutputAndVcsDirectories_ButNotSources()
    {
        using var directory = new TestDirectory();
        directory.Write("bin/Debug/x.blp", []);
        directory.Write("obj/x.blp", []);
        directory.Write("node_modules/p/x.blp", []);
        directory.Write(".git/objects/x.blp", []);
        directory.Write("tests/x.blp", []);

        string[] walked = Walk(directory.Path).ToArray();

        Assert.Equal(["tests/x.blp"], walked);
    }

    [Fact]
    public void TheWalk_SkipsTestResults()
    {
        using var directory = new TestDirectory();
        directory.Write("TestResults/run/x.blp", []);
        directory.Write("tests/y.txt", "y"u8.ToArray());

        Assert.Equal(["tests/y.txt"], Walk(directory.Path).ToArray());
    }

    [Fact]
    public void TheWalk_ScansBinAndObjDirectories_ThatAreNotBuildOutput()
    {
        using var directory = new TestDirectory();
        directory.Write("tools/Bin/Wolf.blp", []);
        directory.Write("src/bin/Wolf.blp", []);
        directory.Write("src/App/App.csproj", "<Project/>"u8.ToArray());
        directory.Write("src/App/bin/Debug/built.blp", []);
        directory.Write("src/App/obj/built.blp", []);
        directory.Write("web/package.json", "{}"u8.ToArray());
        directory.Write("web/node_modules/p/x.blp", []);
        directory.Write("lib/node_modules/p/y.blp", []);
        directory.Write("sub/.git/z.blp", []);

        string[] walked = [.. Walk(directory.Path).Order(StringComparer.Ordinal)];

        Assert.Equal(["lib/node_modules/p/y.blp", "src/App/App.csproj", "src/bin/Wolf.blp", "sub/.git/z.blp", "tools/Bin/Wolf.blp", "web/package.json"], walked);
    }

    [Fact]
    public void TheBinaryCount_SeesANulInTheHead_AndAnImageExtension()
    {
        using var directory = new TestDirectory();
        directory.Write("a.dat", [1, 2, 0, 3]);
        directory.Write("b.png", [1, 2, 3]);
        directory.Write("c.txt", "text"u8.ToArray());

        Assert.Equal(2, CountBinary(directory.Path, Walk(directory.Path)));
    }

    [Fact]
    public void TheGuard_ReportsAListedPathThatIsNotOnDisk()
    {
        using var directory = new TestDirectory();
        directory.Write("present.txt", "x"u8.ToArray());

        string[] hits = Violations(directory.Path, ["present.txt", "gone/file.txt"]).ToArray();

        Assert.Single(hits);
        Assert.Contains("gone/file.txt", hits[0], StringComparison.Ordinal);
        Assert.Contains("not on disk", hits[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuard_ReportsABannedExtension_OnANameThatCannotBeOpened()
    {
        using var directory = new TestDirectory();

        string[] hits = Violations(directory.Path, ["�.blp"]).ToArray();

        Assert.Single(hits);
        Assert.Contains("extension", hits[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheBinaryCount_RefusesAListedPathThatIsNotOnDisk()
    {
        using var directory = new TestDirectory();

        Assert.Throws<InvalidOperationException>(() => CountBinary(directory.Path, ["gone/file.bin"]));
    }

    /// <summary>A fact that is skipped, visibly, when git is not on PATH (a source archive without git).</summary>
    private sealed class GitFactAttribute : FactAttribute
    {
        public GitFactAttribute()
        {
            if (!GitAvailable())
            {
                Skip = "git is not on PATH, so a throwaway repository cannot be created.";
            }
        }
    }

    private static bool GitAvailable()
    {
        try
        {
            var info = new ProcessStartInfo("git", ["--version"]) { RedirectStandardOutput = true, UseShellExecute = false };
            using Process process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Variables that override <c>git -C</c>; git exports them to hooks and linked worktrees, so they must never reach a test's git.</summary>
    private static readonly string[] GitRedirectingVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR", "GIT_CEILING_DIRECTORIES",
    ];

    /// <summary>
    /// A git process rooted at <paramref name="workDir"/> with every redirecting variable removed. <paramref name="pollution"/> simulates a
    /// parent environment that carries such variables (set first, then scrubbed), so a test can prove the scrub; it never touches this process.
    /// </summary>
    private static ProcessStartInfo GitStartInfo(string workDir, IReadOnlyDictionary<string, string>? pollution, params string[] arguments)
    {
        var info = new ProcessStartInfo("git", ["-C", workDir, .. arguments])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (pollution is not null)
        {
            foreach ((string key, string value) in pollution)
            {
                info.Environment[key] = value;
            }
        }

        foreach (string variable in GitRedirectingVariables)
        {
            info.Environment.Remove(variable);
        }

        return info;
    }

    /// <summary>Commits (to the index) a client asset named 0xFF.blp, a name no .NET file API can create on Linux, and returns the listing.</summary>
    private static List<string>? ListInvalidUtf8Repository(string directory, IReadOnlyDictionary<string, string>? pollution)
    {
        RunGit(directory, null, pollution, "init", "-q");
        string blob = RunGit(directory, "payload"u8.ToArray(), pollution, "hash-object", "-w", "--stdin").Trim();

        // ls-files reads the index, so no commit is made: nothing here can run a hook or need a signing key.
        byte[] entry = [.. Encoding.ASCII.GetBytes("100644 " + blob + "\t"), 0xFF, .. ".blp"u8.ToArray(), 0];
        RunGit(directory, entry, pollution, "update-index", "-z", "--add", "--index-info");
        return GitList(directory, pollution);
    }

    [GitFact]
    public void TheGuard_CatchesATrackedClientAsset_WhoseNameIsNotValidUtf8()
    {
        // The name is written into a throwaway repository's index with raw bytes. The listing decodes it to U+FFFD.blp, which does not exist on disk.
        using var directory = new TestDirectory();

        List<string>? listed = ListInvalidUtf8Repository(directory.Path, null);

        Assert.NotNull(listed);
        Assert.Single(listed);
        string[] hits = Violations(directory.Path, listed).ToArray();
        Assert.Single(hits);
        Assert.Contains("extension", hits[0], StringComparison.Ordinal);
    }

    [GitFact]
    public void TheTempRepositoryFlow_IgnoresGitEnvironmentVariables_ThatWouldRedirectIt()
    {
        // Simulates a hook or linked-worktree parent: GIT_DIR and friends point at another repository. The flow must not touch it.
        using var decoyDirectory = new TestDirectory();
        using var directory = new TestDirectory();
        RunGit(decoyDirectory.Path, null, null, "init", "-q");
        string decoyGit = System.IO.Path.Combine(decoyDirectory.Path, ".git");
        var pollution = new Dictionary<string, string>
        {
            ["GIT_DIR"] = decoyGit,
            ["GIT_WORK_TREE"] = decoyDirectory.Path,
            ["GIT_INDEX_FILE"] = System.IO.Path.Combine(decoyGit, "index"),
            ["GIT_OBJECT_DIRECTORY"] = System.IO.Path.Combine(decoyGit, "objects"),
            ["GIT_COMMON_DIR"] = decoyGit,
        };

        List<string>? listed = ListInvalidUtf8Repository(directory.Path, pollution);

        Assert.NotNull(listed);
        Assert.Single(listed);
        Assert.False(File.Exists(System.IO.Path.Combine(decoyGit, "index")), "the decoy repository's index was written");
        string objects = System.IO.Path.Combine(decoyGit, "objects");
        Assert.Empty(Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories));
        Assert.Empty(RunGit(decoyDirectory.Path, null, null, "ls-files", "-z"));
    }

    [Fact]
    public void TheListing_FailsInCi_WhenGitCannotListACheckout_AndWalksOtherwise()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, ".git"));
        directory.Write("a.txt", "x"u8.ToArray());

        Assert.Throws<InvalidOperationException>(() => ListFiles(directory.Path, _ => null, inCi: true));

        (IReadOnlyList<string> files, string mode) = ListFiles(directory.Path, _ => null, inCi: false);
        Assert.Contains("git listing unavailable", mode, StringComparison.Ordinal);
        Assert.Contains("a.txt", files);

        (IReadOnlyList<string> listed, string listedMode) = ListFiles(directory.Path, _ => ["only.txt"], inCi: true);
        Assert.Equal("git ls-files", listedMode);
        Assert.Equal(["only.txt"], listed);
    }

    private static string RunGit(string workDir, byte[]? stdin, IReadOnlyDictionary<string, string>? pollution, params string[] arguments)
    {
        ProcessStartInfo info = GitStartInfo(workDir, pollution, arguments);
        info.RedirectStandardInput = stdin is not null;
        info.RedirectStandardError = true;
        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            process.StandardInput.BaseStream.Write(stdin);
            process.StandardInput.Close();
        }

        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error.Result}");
        return result;
    }

    private static readonly string[] BinaryExtensions = [".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".7z", ".gz", ".dll", ".exe"];

    /// <summary>How many files are binary (an image or archive extension, or a NUL in the first 8 KiB): proof the guard read file contents.</summary>
    private static int CountBinary(string root, IEnumerable<string> files)
    {
        int count = 0;
        byte[] head = new byte[8192];
        foreach (string relative in files)
        {
            string full = System.IO.Path.Combine(root, relative);
            if (!File.Exists(full))
            {
                throw new InvalidOperationException($"{relative}: listed but not on disk, so it cannot be sniffed");
            }

            if (BinaryExtensions.Contains(System.IO.Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }

            int read;
            using (FileStream stream = File.OpenRead(full))
            {
                read = stream.Read(head, 0, head.Length);
            }

            if (head.AsSpan(0, read).IndexOf((byte)0) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> Violations(string root, IEnumerable<string> files)
    {
        foreach (string relative in files)
        {
            // The name is judged first: a name that cannot be opened (not valid UTF-8, a skip-worktree entry) is still a client asset by extension.
            string extension = System.IO.Path.GetExtension(relative);
            if (BannedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"{relative}: client-asset extension {extension}";
                continue;
            }

            string full = System.IO.Path.Combine(root, relative);
            if (!File.Exists(full))
            {
                // Never skipped: an unreadable listed file is an unscanned file, and an unscanned file must fail the guard.
                yield return $"{relative}: listed but not on disk, so it could not be scanned (non-UTF-8 name, skip-worktree/sparse entry, or a file deleted from disk but still in the index)";
                continue;
            }

            byte[] head = new byte[8];
            int read;
            using (FileStream stream = File.OpenRead(full))
            {
                read = stream.Read(head, 0, head.Length);
            }

            foreach ((string name, byte[] magic) in Magics)
            {
                if (read >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic))
                {
                    yield return $"{relative}: starts with the {name} magic";
                }
            }
        }
    }

    private static bool InCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static bool IsCheckout(string root) =>
        Directory.Exists(System.IO.Path.Combine(root, ".git")) || File.Exists(System.IO.Path.Combine(root, ".git"));

    /// <summary>
    /// The files to scan and how they were listed. <paramref name="lister"/> and <paramref name="inCi"/> are parameters so the CI refusal
    /// can be tested: in CI a checkout must be listed by git, and falling back to a walk is a failure.
    /// </summary>
    private static (IReadOnlyList<string> Files, string Mode) ListFiles(string root, Func<string, List<string>?> lister, bool inCi)
    {
        if (!IsCheckout(root))
        {
            return ([.. Walk(root)], "directory walk (not a git checkout)");
        }

        List<string>? tracked = lister(root);
        if (tracked is not null)
        {
            return (tracked, "git ls-files");
        }

        if (inCi)
        {
            throw new InvalidOperationException("git ls-files is unavailable in CI: refusing to fall back to a directory walk that would also scan untracked files");
        }

        return ([.. Walk(root)], "directory walk (git listing unavailable)");
    }

    /// <summary>Tracked files via git, or null when this is not a checkout (a source archive) or git is unavailable.</summary>
    private static List<string>? GitList(string root, IReadOnlyDictionary<string, string>? pollution)
    {
        if (!IsCheckout(root))
        {
            return null;
        }

        try
        {
            ProcessStartInfo info = GitStartInfo(root, pollution, "ls-files", "-z");
            using Process process = Process.Start(info)!;
            using var raw = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(raw); // raw bytes: names need not be UTF-8
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            // Invalid sequences decode to U+FFFD, which keeps the (ASCII) extension judgeable by name.
            return [.. Encoding.UTF8.GetString(raw.GetBuffer(), 0, (int)raw.Length).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".vcxproj", ".esproj"];

    /// <summary>
    /// Whether a directory named <paramref name="name"/> inside <paramref name="parent"/> is build or VCS output that is not part of a
    /// source archive: bin/obj/TestResults at the root or beside a project file, node_modules at the root or beside package.json, and
    /// the root .git. The bin/obj/TestResults names are matched case-sensitively because the SDK emits exactly those, which also keeps
    /// tools/Bin scanned on a case-insensitive file system. Anywhere else (tools/Bin, src/bin without a project) the directory is scanned.
    /// </summary>
    private static bool IsBuildOutput(string parent, bool parentIsRoot, string name)
    {
        if (name.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return parentIsRoot;
        }

        if (name is "bin" or "obj" or "TestResults")
        {
            return parentIsRoot || Directory.EnumerateFiles(parent).Any(file => ProjectExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }

        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            return parentIsRoot || File.Exists(System.IO.Path.Combine(parent, "package.json"));
        }

        return false;
    }

    /// <summary>
    /// Walks a directory tree as a source archive. It skips root-level bin/obj/TestResults/node_modules even when tracked; that is
    /// acceptable only because the ls-files mode (NoTrackedFile_IsAClientAsset) lists and scans tracked files there.
    /// </summary>
    private static IEnumerable<string> Walk(string root) => WalkDirectory(root, string.Empty, isRoot: true);

    private static IEnumerable<string> WalkDirectory(string directory, string prefix, bool isRoot)
    {
        // Relative names always use '/', never a rewritten backslash: on Linux a backslash is a filename character, and rewriting it hides the file from File.Exists.
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            yield return prefix + System.IO.Path.GetFileName(file);
        }

        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            string name = System.IO.Path.GetFileName(sub);
            if (IsBuildOutput(directory, isRoot, name) || new DirectoryInfo(sub).LinkTarget is not null)
            {
                continue;
            }

            foreach (string nested in WalkDirectory(sub, prefix + name + "/", isRoot: false))
            {
                yield return nested;
            }
        }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    {
        for (DirectoryInfo? directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "ArcaneCore.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ArcaneCore.slnx not found above " + path);
    }
}
