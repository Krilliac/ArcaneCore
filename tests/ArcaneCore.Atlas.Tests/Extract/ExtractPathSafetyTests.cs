using System.Diagnostics;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// <see cref="ExtractOutput"/>: which file names a client's data may become on the server data root, and the staged write
/// that does not follow a link left at the staging name. The vmap extractor and command tests that need no
/// <see cref="ExtractOutput"/> are in <see cref="ExtractPathSafetyEndToEndTests"/>.
/// </summary>
public sealed class ExtractPathSafetyTests
{
    [Theory]
    [InlineData("Bed.m2")]
    [InlineData("Stormwind.wmo.vmo")]
    [InlineData("Cube.m2.vmo")]
    [InlineData("a_b-c.m2")]
    [InlineData("CONSOLE.m2")]
    [InlineData("Comma.m2")]
    [InlineData("Lpt10.m2")]
    [InlineData("COM10.m2")]
    [InlineData("Big_Wall.m2")]
    [InlineData("a b.m2")]
    public void IsPlainFileName_OnePlainName_IsAccepted(string name)
    {
        Assert.True(ExtractOutput.IsPlainFileName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("..Bed.m2")]
    [InlineData("a/b.m2")]
    [InlineData("../x.m2")]
    [InlineData("a\\b.m2")]
    [InlineData("..\\x.m2")]
    [InlineData("C:b.m2")]
    [InlineData("/etc/x")]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("x.m2 ")]
    [InlineData("x\0.m2")]
    [InlineData("CON")]
    [InlineData("con.m2")]
    [InlineData("Nul.vmo")]
    [InlineData("Aux.m2.vmo")]
    [InlineData("PRN.txt")]
    [InlineData("COM1.m2")]
    [InlineData("com0.m2")]
    [InlineData("lpt9.m2")]
    [InlineData("COM¹.m2")]
    [InlineData("COM²")]
    [InlineData("LPT³.m2")]
    [InlineData("Bad?.m2")]
    [InlineData("a*b.m2")]
    [InlineData("a|b.m2")]
    [InlineData("a<b.m2")]
    [InlineData("a>b.m2")]
    [InlineData("a\"b.m2")]
    [InlineData("tab\there.m2")]
    [InlineData("ÄModel.m2")]
    [InlineData("CONIN$.m2")]
    [InlineData("conout$")]
    [InlineData("\u0001x.m2")]
    [InlineData("x\u007F.m2")]
    public void IsPlainFileName_NotOnePlainName_IsRefused_OnEveryPlatform(string name)
    {
        Assert.False(ExtractOutput.IsPlainFileName(name));
    }

    [Fact]
    public void IsPlainFileName_Null_IsRefused()
    {
        Assert.False(ExtractOutput.IsPlainFileName(null!));
    }

    [Fact]
    public void FileIn_APlainName_IsTheCombinedPath()
    {
        using var scratch = new TestDirectory();

        string file = ExtractOutput.FileIn(scratch.Path, "Bed.m2.vmo");

        Assert.Equal(Path.Combine(scratch.Path, "Bed.m2.vmo"), file);
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Path));
    }

    [Theory]
    [InlineData("../x.m2")]
    [InlineData("..\\x.m2")]
    [InlineData("x.")]
    [InlineData("")]
    [InlineData("CON")]
    public void FileIn_ANameThatIsNotPlain_Throws_AndCreatesNothing(string name)
    {
        using var scratch = new TestDirectory();
        string directory = Path.Combine(scratch.Path, "a", "b");

        Assert.Throws<InvalidDataException>(() => ExtractOutput.FileIn(directory, name));

        Assert.Empty(Directory.GetFileSystemEntries(scratch.Path));
    }

    [Fact]
    public void FileIn_ARootedPath_Throws_AndCreatesNothing()
    {
        using var scratch = new TestDirectory();
        string directory = Path.Combine(scratch.Path, "vmaps");
        string rooted = Path.Combine(scratch.Path, "elsewhere", "x.m2");

        Assert.True(Path.IsPathRooted(rooted));
        Assert.Throws<InvalidDataException>(() => ExtractOutput.FileIn(directory, rooted));

        Assert.Empty(Directory.GetFileSystemEntries(scratch.Path));
    }

    [Fact]
    public void WriteWhole_ALeftoverRegularStagingFile_IsReplaced_AndNoStagingFileRemains()
    {
        using var scratch = new TestDirectory();
        string target = Path.Combine(scratch.Path, "Cube.m2.vmo");
        File.WriteAllBytes(target + ".part", [9, 9, 9, 9, 9, 9]);

        long written = ExtractOutput.WriteWhole(target, [1, 2, 3]);

        Assert.Equal(3L, written);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(new[] { "Cube.m2.vmo" }, Directory.GetFiles(scratch.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteWhole_AnExistingTarget_IsOverwritten_AndAnEmptyFileIsWritten()
    {
        using var scratch = new TestDirectory();
        string target = Path.Combine(scratch.Path, "000.vmtree");
        File.WriteAllBytes(target, [7, 7, 7, 7]);

        Assert.Equal(2L, ExtractOutput.WriteWhole(target, [4, 5]));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(target));

        Assert.Equal(0L, ExtractOutput.WriteWhole(target, []));
        Assert.Empty(File.ReadAllBytes(target));
        Assert.Equal(new[] { "000.vmtree" }, Directory.GetFiles(scratch.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteWhole_AStagingFileThatIsAHardLinkOutsideTheOutputFolder_IsNotWrittenThrough()
    {
        using var scratch = new TestDirectory();
        using var outside = new TestDirectory();
        string outsideFile = outside.Write("precious.bin", [5, 6, 7, 8]);
        string target = Path.Combine(scratch.Path, "Cube.m2.vmo");
        string staging = target + ".part";
        MakeHardLink(staging, outsideFile);
        try
        {
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(staging));

            long written = ExtractOutput.WriteWhole(target, [1, 2]);

            Assert.Equal(2L, written);
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(outsideFile));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(target));
            Assert.False(File.Exists(staging));
            Assert.Equal(new[] { "Cube.m2.vmo" }, Directory.GetFiles(scratch.Path).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            // The link entries go first, so no cleanup can reach the other folder's file through them.
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    [Fact]
    public void WriteWhole_ATargetThatIsAHardLinkOutsideTheOutputFolder_IsReplacedNotWrittenThrough()
    {
        using var scratch = new TestDirectory();
        using var outside = new TestDirectory();
        string outsideFile = outside.Write("precious.bin", [5, 6, 7, 8]);
        string target = Path.Combine(scratch.Path, "Cube.m2.vmo");
        MakeHardLink(target, outsideFile);
        try
        {
            long written = ExtractOutput.WriteWhole(target, [1, 2]);

            Assert.Equal(2L, written);
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(outsideFile));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally
        {
            // The link entry goes first, so no cleanup can reach the other folder's file through it.
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    [Fact]
    public void WriteWhole_AMoveThatFails_RemovesTheStagingFile()
    {
        using var scratch = new TestDirectory();
        string target = Path.Combine(scratch.Path, "Cube.m2.vmo");
        Directory.CreateDirectory(target);

        Exception? failure = Record.Exception(() => ExtractOutput.WriteWhole(target, [1, 2, 3]));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected an I/O failure, got {failure?.GetType().FullName ?? "none"}");
        Assert.False(File.Exists(target + ".part"));
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    /// <summary>A hard link needs no privilege on either platform: <c>mklink /H</c> on Windows, <c>ln</c> elsewhere.</summary>
    private static void MakeHardLink(string link, string existing)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{existing}\"");
        }
        else
        {
            info = new ProcessStartInfo("ln");
            info.ArgumentList.Add(existing);
            info.ArgumentList.Add(link);
        }

        info.CreateNoWindow = true;
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"could not create the hard link {link} -> {existing} for the test: {output}");
    }
}
