using System.Diagnostics;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>Destination checks of <c>client.read</c> for the Windows path forms that name something other than a plain file.</summary>
public sealed class ClientOutputPathTests : IDisposable
{
    internal const string DeviceOrNetwork = "device or network path";

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [WindowsTheory]
    [InlineData(@"\\.\PHYSICALDRIVE0")]
    [InlineData(@"\\.\C:\temp\x.bin")]
    [InlineData(@"\\?\C:\temp\x.bin")]
    [InlineData(@"\\server\share\x.bin")]
    [InlineData("//server/share/x.bin")]
    [InlineData(@"\??\C:\temp\x.bin")]
    [InlineData(@"\??\Volume{01234567-89ab-cdef-0123-456789abcdef}\temp\x.bin")]
    [InlineData(@"\??\UNC\server\share\x.bin")]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume1\temp\x.bin")]
    public void RefusesDeviceAndNetworkPaths_NamingThatRule(string requested)
    {
        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.Check(requested, overwrite: true, []));

        Assert.Equal(CommandException.BadArguments, e.Code);
        Assert.Contains(DeviceOrNetwork, e.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void RefusesAnObjectManagerPath_ThatNamesTheClientFolder_InsteadOfWritingThroughIt()
    {
        // \??\C:\... is the plain location under another spelling; the protected-folder comparison never matched it.
        string inClient = Path.Combine(_dir.Path, "client", "Data", "x.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(inClient)!);

        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.Check(@"\??\" + inClient, overwrite: true, [Path.Combine(_dir.Path, "client")]));

        Assert.Contains(DeviceOrNetwork, e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(inClient));
    }

    [WindowsTheory]
    [InlineData("repo ")]
    [InlineData("repo.")]
    [InlineData("repo...")]
    public void RefusesAComponentThatEndsInASpaceOrDot_BecauseWindowsDropsIt(string component)
    {
        string file = Path.Combine(_dir.Path, component, "x.bin");

        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.Check(file, overwrite: true, []));

        Assert.Contains("ends in a space or a dot", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, component)));
    }

    [WindowsTheory]
    [InlineData(@"\temp\.\x.bin")]
    [InlineData(@"\temp\..\x.bin")]
    [InlineData(@"\temp\x.bin ")]
    public void RefusesDotComponentsAndATrailingSpaceInTheFileName(string tail)
    {
        string requested = Path.GetPathRoot(_dir.Path) + tail.TrimStart('\\');

        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.Check(requested, overwrite: true, []));

        Assert.Contains("ends in a space or a dot", e.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void RefusesAlternateDataStreams_AndShortNames()
    {
        string stream = Path.Combine(_dir.Path, "x.bin") + ":hidden";
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(stream, overwrite: true, []));
        string shortName = Path.Combine(_dir.Path, "PROGRA~1", "x.bin");
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(shortName, overwrite: true, []));
    }

    [WindowsFact]
    public void AcceptsADriveLetterPath_InEitherSeparatorStyle()
    {
        string target = Path.Combine(_dir.Path, "ok.bin");

        Assert.Equal(Path.GetFullPath(target), ClientOutputPath.Check(target.Replace('\\', '/'), overwrite: false, []));
        Assert.Equal(Path.GetFullPath(target), ClientOutputPath.Check(target, overwrite: false, []));
    }

    [Fact]
    public void AcceptsAnOrdinaryNewFile_AndRefusesAnExistingOneWithoutOverwrite()
    {
        string target = Path.Combine(_dir.Path, "ok.bin");
        Assert.Equal(Path.GetFullPath(target), ClientOutputPath.Check(target, overwrite: false, []));
        File.WriteAllBytes(target, [1]);
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(target, overwrite: false, []));
        Assert.Equal(Path.GetFullPath(target), ClientOutputPath.Check(target, overwrite: true, []));
    }
}

/// <summary>Directory links for the path tests: a junction on Windows (needs no privilege), a symbolic link elsewhere.</summary>
internal static class DirectoryLinks
{
    public static void Make(string link, string target)
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

    /// <summary>
    /// Removes the link itself and never what it points to. A recursive delete of a folder holding a junction throws on
    /// .NET 10, so every test removes its links here before its scratch folder goes.
    /// </summary>
    public static void Delete(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(link, recursive: false);
        }
        else
        {
            File.Delete(link);
        }
    }
}

/// <summary>A fact for Windows-only path forms; skipped, visibly, elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows path forms.";
        }
    }
}

/// <summary>A theory for Windows-only path forms; skipped, visibly, elsewhere.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows path forms.";
        }
    }
}
