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
/// would look exactly like a clean repository.
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
        Assert.Empty(Violations(root, files));
    }

    [Fact]
    public void TheGuard_CatchesAPlantedBlp2_ByMagicEvenUnderAnInnocentName()
    {
        using var directory = new TestDirectory();
        directory.Write("src\\notes.txt", Encoding.ASCII.GetBytes("BLP2").Concat(new byte[64]).ToArray());
        directory.Write("src\\fine.txt", "hello"u8.ToArray());

        string[] hits = Violations(directory.Path, Walk(directory.Path)).ToArray();

        Assert.Single(hits);
        Assert.Contains("notes.txt", hits[0], StringComparison.Ordinal);
        Assert.Contains("BLP2", hits[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\\b\\Wolf.blp")]
    [InlineData("a\\Wolf.M2")]
    [InlineData("data\\CreatureDisplayInfo.dbc")]
    [InlineData("data\\patch.MPQ")]
    [InlineData("shot\\Probe.tga")]
    public void TheGuard_CatchesAClientExtension_EvenWhenTheFileIsEmpty(string relative)
    {
        using var directory = new TestDirectory();
        directory.Write(relative, []);

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
        directory.Write("bin\\Debug\\x.blp", []);
        directory.Write("obj\\x.blp", []);
        directory.Write("node_modules\\p\\x.blp", []);
        directory.Write(".git\\objects\\x.blp", []);
        directory.Write("tests\\x.blp", []);

        string[] walked = Walk(directory.Path).ToArray();

        Assert.Equal(["tests/x.blp"], walked);
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
        string[] skipped = ["bin", "obj", "node_modules", ".git"];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
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
