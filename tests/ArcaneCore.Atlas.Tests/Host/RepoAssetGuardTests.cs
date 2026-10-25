using System.Diagnostics;
using System.Text;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

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
public sealed class RepoAssetGuardTests
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

        IReadOnlyList<string> files = ListFiles(root);

        Assert.True(files.Count > 1000, $"only {files.Count} files listed: the guard is not looking at the repository");
        Assert.True(CountBinary(root, files) >= 1, "no binary file was sniffed: the guard is not reading file contents");
        Assert.Empty(Violations(root, files));
    }

    [Fact]
    public void TheSourceArchiveWalk_CoversTheRepository_AndSniffsBinaryFiles()
    {
        string root = RepositoryRoot();

        string[] files = [.. Walk(root)];

        Assert.True(files.Length > 1000, $"only {files.Length} files walked: the archive mode is not looking at the repository");
        Assert.True(CountBinary(root, files) >= 1, "no binary file was sniffed in the archive walk");
        Assert.Empty(Violations(root, files));
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
    public void TheGuard_CatchesABackslashNamedClientAsset_OnEveryOs()
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
    public void TheBinaryCount_SeesANulInTheHead_AndAnImageExtension()
    {
        using var directory = new TestDirectory();
        directory.Write("a.dat", [1, 2, 0, 3]);
        directory.Write("b.png", [1, 2, 3]);
        directory.Write("c.txt", "text"u8.ToArray());

        Assert.Equal(2, CountBinary(directory.Path, Walk(directory.Path)));
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
                continue;
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
            string full = System.IO.Path.Combine(root, relative);
            if (!File.Exists(full))
            {
                continue;
            }

            string extension = System.IO.Path.GetExtension(relative);
            if (BannedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"{relative}: client-asset extension {extension}";
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

    private static IReadOnlyList<string> ListFiles(string root) => GitList(root) ?? [.. Walk(root)];

    /// <summary>Tracked files via git, or null when this is not a checkout (a source archive) or git is unavailable.</summary>
    private static List<string>? GitList(string root)
    {
        if (!Directory.Exists(System.IO.Path.Combine(root, ".git")) && !File.Exists(System.IO.Path.Combine(root, ".git")))
        {
            return null;
        }

        try
        {
            var info = new ProcessStartInfo("git", ["-C", root, "ls-files", "-z"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)] : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> Walk(string root)
    {
        string[] skipped = ["bin", "obj", "node_modules", ".git", "TestResults"];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, file)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/'); // never '\\' on Linux: there it is a filename character, and rewriting it hides the file from File.Exists
            if (relative.Split('/').SkipLast(1).Any(part => skipped.Contains(part, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return relative;
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
