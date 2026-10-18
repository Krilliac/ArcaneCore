using System.Diagnostics;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Render;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

public sealed class OutputPathsTests
{
    /// <summary>A directory that looks like a repository root: a <c>.git</c> directory, a <c>.git</c> file or the solution file.</summary>
    private static string Repository(TempDirectory dir, string name, string marker)
    {
        string root = Path.Combine(dir.Root, name);
        Directory.CreateDirectory(root);
        switch (marker)
        {
            case ".git/":
                Directory.CreateDirectory(Path.Combine(root, ".git"));
                break;
            case ".git":
                File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../elsewhere\n");
                break;
            default:
                File.WriteAllText(Path.Combine(root, marker), string.Empty);
                break;
        }

        return root;
    }

    private static CommandException Refused(Func<string> resolve)
    {
        CommandException e = Assert.Throws<CommandException>(resolve);
        Assert.Equal(CommandException.BadArguments, e.Code);
        return e;
    }

    [Theory]
    [InlineData(".git/")]
    [InlineData(".git")]
    [InlineData("ArcaneCore.slnx")]
    public void APathInsideAWorkingTree_IsRefused_WhateverMarksTheRoot(string marker)
    {
        using var dir = new TempDirectory();
        string root = Repository(dir, "checkout", marker);
        string target = Path.Combine(root, "docs", "deeper", "out.png");

        CommandException e = Refused(() => OutputPaths.Resolve("render.png", target, ".png", allowRelative: false));

        Assert.Equal(root, OutputPaths.FindWorkingTree(target));
        Assert.Contains("repository working tree", e.Message, StringComparison.Ordinal);
        Assert.Contains(root, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathNextToTheRootMarker_IsRefusedToo()
    {
        using var dir = new TempDirectory();
        string root = Repository(dir, "checkout", ".git/");

        Refused(() => OutputPaths.Resolve("render.png", Path.Combine(root, "out.png"), ".png", allowRelative: false));
    }

    [Fact]
    public void APathOutsideEveryWorkingTree_IsAccepted_AndNothingIsCreated()
    {
        using var dir = new TempDirectory();
        string sibling = Repository(dir, "checkout", ".git/");
        string target = Path.Combine(dir.Root, "elsewhere", "nested", "out.png");

        string resolved = OutputPaths.Resolve("render.png", target, ".png", allowRelative: false);

        Assert.Equal(target, resolved);
        Assert.Null(OutputPaths.FindWorkingTree(target));
        Assert.False(Directory.Exists(Path.Combine(dir.Root, "elsewhere")));
        Assert.Equal(new[] { ".git" }, Directory.GetFileSystemEntries(sibling).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void TheRealRepository_IsRecognised_FromTheTestBinaries()
    {
        // The tests run from <repo>/tests/ArcaneCore.Atlas.Tests/bin/...: the guard must find the checkout above them.
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ArcaneCore.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        string target = Path.Combine(root, "docs", "capture.png");
        Assert.NotNull(OutputPaths.FindWorkingTree(target));
        Assert.NotNull(OutputPaths.FindWorkingTree(Path.Combine(AppContext.BaseDirectory, "capture.png")));
        Refused(() => OutputPaths.Resolve("render.png", target, ".png", allowRelative: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPath_IsRefused(string requested)
    {
        CommandException e = Refused(() => OutputPaths.Resolve("export.obj", requested, ".obj", allowRelative: true));

        Assert.Contains("export.obj", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePath_IsOnlyAcceptedWhenTheCommandRunsInTheCallersOwnProcess()
    {
        CommandException e = Refused(() => OutputPaths.Resolve("render.png", "shot.png", ".png", allowRelative: false));
        Assert.Contains("absolute", e.Message, StringComparison.Ordinal);

        // Allowed: resolved against the current directory, then judged like any other path (the test directory is inside the checkout).
        string expected = Path.GetFullPath("shot.png");
        if (OutputPaths.FindWorkingTree(expected) is { } tree)
        {
            CommandException inside = Refused(() => OutputPaths.Resolve("render.png", "shot.png", ".png", allowRelative: true));
            Assert.Contains(tree, inside.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(expected, OutputPaths.Resolve("render.png", "shot.png", ".png", allowRelative: true));
        }
    }

    [Theory]
    [InlineData("out.txt")]
    [InlineData("out.png.bak")]
    [InlineData("out")]
    public void TheExtension_MustMatch(string name)
    {
        using var dir = new TempDirectory();

        CommandException e = Refused(() => OutputPaths.Resolve("render.png", dir.File(name), ".png", allowRelative: false));

        Assert.Contains(".png", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExtension_IsMatchedIgnoringCase()
    {
        using var dir = new TempDirectory();

        Assert.Equal(dir.File("OUT.PNG"), OutputPaths.Resolve("render.png", dir.File("OUT.PNG"), ".png", allowRelative: false));
    }

    [Fact]
    public void AnExistingDirectory_IsNotAFile()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("looks-like-a-file.png"));

        CommandException e = Refused(() => OutputPaths.Resolve("render.png", dir.File("looks-like-a-file.png"), ".png", allowRelative: false));

        Assert.Contains("directory", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryLink_IntoAWorkingTree_IsFollowed()
    {
        // The link points at a subdirectory of the checkout, so nothing above the link itself marks a repository.
        using var dir = new TempDirectory();
        string root = Repository(dir, "checkout", ".git/");
        string docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string link = Path.Combine(dir.Root, "innocent");
        MakeDirectoryLink(link, docs);

        string target = Path.Combine(link, "captures", "out.png");

        Assert.Null(OutputPaths.FindWorkingTree(Path.Combine(dir.Root, "elsewhere", "out.png")));
        Assert.Equal(root, OutputPaths.FindWorkingTree(target), ignoreCase: true);
        CommandException e = Refused(() => OutputPaths.Resolve("render.png", target, ".png", allowRelative: false));
        Assert.Contains(root, e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALinkToALink_AndALinkInTheMiddleOfThePath_AreFollowed()
    {
        using var dir = new TempDirectory();
        string root = Repository(dir, "checkout", ".git/");
        string docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        string middle = Path.Combine(dir.Root, "middle");
        Directory.CreateDirectory(middle);
        MakeDirectoryLink(Path.Combine(middle, "inner"), docs);
        MakeDirectoryLink(Path.Combine(dir.Root, "outer"), middle);

        string target = Path.Combine(dir.Root, "outer", "inner", "deep", "out.png");

        Assert.Equal(root, OutputPaths.FindWorkingTree(target), ignoreCase: true);
        Refused(() => OutputPaths.Resolve("render.png", target, ".png", allowRelative: false));
    }

    [Fact]
    public void ADirectoryLink_ToPlainStorage_IsAccepted()
    {
        using var dir = new TempDirectory();
        string storage = Path.Combine(dir.Root, "storage");
        Directory.CreateDirectory(storage);
        string link = Path.Combine(dir.Root, "shortcut");
        MakeDirectoryLink(link, storage);
        string target = Path.Combine(link, "out.png");

        Assert.Null(OutputPaths.FindWorkingTree(target));
        Assert.Equal(target, OutputPaths.Resolve("render.png", target, ".png", allowRelative: false));
    }

    [Fact]
    public void ALinkCycle_EndsInARefusal_NotAHang()
    {
        using var dir = new TempDirectory();
        string loop = Path.Combine(dir.Root, "loop");
        Directory.CreateDirectory(loop);
        MakeDirectoryLink(Path.Combine(loop, "self"), loop);

        string few = Path.Combine([loop, .. Enumerable.Repeat("self", 3), "out.png"]);
        string many = Path.Combine([loop, .. Enumerable.Repeat("self", 60), "out.png"]);

        Assert.Null(OutputPaths.FindWorkingTree(few));
        Assert.Throws<IOException>(() => OutputPaths.FindWorkingTree(many));
        CommandException e = Refused(() => OutputPaths.Resolve("render.png", many, ".png", allowRelative: false));
        Assert.Contains("directory links", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAtomically_CreatesTheDirectory_AndReplacesAnExistingFile()
    {
        using var dir = new TempDirectory();
        string path = dir.File(Path.Combine("deep", "er", "out.bin"));

        Assert.True(OutputPaths.WriteAtomically(path, s => s.Write([1, 2, 3])));
        Assert.True(OutputPaths.WriteAtomically(path, s => s.Write([9, 8])));

        Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(path));
        Assert.Equal(new[] { "out.bin" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteAtomically_AFailedWrite_LeavesTheOldFileAndNoTemporaryFile()
    {
        using var dir = new TempDirectory();
        string path = dir.File("out.bin");
        File.WriteAllBytes(path, [7, 7, 7]);

        Assert.Throws<InvalidDataException>(() => OutputPaths.WriteAtomically(path, s =>
        {
            s.Write([1, 2]);
            throw new InvalidDataException("half way");
        }));

        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(path));
        Assert.Equal(new[] { "out.bin" }, Directory.GetFiles(dir.Root).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteAtomically_DeclinedCommit_WritesNothing()
    {
        using var dir = new TempDirectory();
        string path = dir.File("out.bin");

        bool written = OutputPaths.WriteAtomically(path, s => s.Write([1]), () => false);

        Assert.False(written);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    /// <summary>A directory symbolic link where the OS allows one for this user, else a junction (Windows, needs no privilege).</summary>
    private static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"could not create the junction {link} -> {target} for the test");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
