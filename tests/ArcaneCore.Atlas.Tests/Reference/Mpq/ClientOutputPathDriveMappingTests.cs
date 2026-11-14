using System.Diagnostics;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// A drive letter made by <c>subst</c> (or a network mapping) hides where its files really are; the output rules are applied to the
/// real location. The QueryDosDevice strings are tested as text; one test makes a real <c>subst</c> drive.
/// </summary>
public sealed class ClientOutputPathDriveMappingTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3", DriveKind.Plain, null)]
    [InlineData(@"\Device\CdRom0", DriveKind.Plain, null)]
    [InlineData(@"\??\D:\repo\sub", DriveKind.Substituted, @"D:\repo\sub")]
    [InlineData(@"\??\d:\", DriveKind.Substituted, @"d:\")]
    [InlineData(@"\??\D:", DriveKind.Substituted, @"D:\")]
    [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\server\share", DriveKind.Foreign, null)]
    [InlineData(@"\Device\Mup\server\share", DriveKind.Foreign, null)]
    [InlineData(@"\DEVICE\LANMANREDIRECTOR\;Z:0000000000012345\server\share\dir", DriveKind.Foreign, null)]
    [InlineData(@"\??\UNC\server\share\dir", DriveKind.Foreign, null)]
    [InlineData(@"\??\Volume{01234567-89ab-cdef-0123-456789abcdef}\dir", DriveKind.Foreign, null)]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume1", DriveKind.Foreign, null)]
    public void ParseDosDeviceTarget_ClassifiesWhatQueryDosDeviceReturns(string target, DriveKind kind, string? path)
    {
        DriveTarget parsed = ClientOutputPath.ParseDosDeviceTarget(target);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(path, parsed.Path);
    }

    /// <summary>A drive letter that no drive, mapping or substitute uses.</summary>
    private static char FreeLetter()
    {
        HashSet<char> used = [.. DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0]))];
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter) && ClientOutputPath.QueryDosDevice(letter) is null)
            {
                return letter;
            }
        }

        throw new InvalidOperationException("no free drive letter for the subst test");
    }

    private static void Subst(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("subst.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"subst {arguments} failed: {output}");
    }

    [WindowsFact]
    public void ASubstDrive_IsJudgedByWhereItReallyPoints()
    {
        string tree = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        string client = Path.Combine(_dir.Path, "client");
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        string plain = Path.Combine(_dir.Path, "plain");
        Directory.CreateDirectory(plain);

        char letter = FreeLetter();
        Subst($"{letter}: \"{Path.Combine(tree, "sub")}\"");
        try
        {
            DriveTarget target = ClientOutputPath.ParseDosDeviceTarget(ClientOutputPath.QueryDosDevice(letter)!);
            Assert.Equal(DriveKind.Substituted, target.Kind);
            Assert.Equal(Path.Combine(tree, "sub"), target.Path, ignoreCase: true);
            string asked = $@"{letter}:\data";

            CommandException directory = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory("extract.test", asked, []));
            Assert.Contains("git working tree", directory.Message, StringComparison.Ordinal);
            Assert.Contains(tree, directory.Message, StringComparison.Ordinal);
            CommandException file = Assert.Throws<CommandException>(() => ClientOutputPath.Check(asked + @"\x.bin", overwrite: true, []));
            Assert.Contains("git working tree", file.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tree, "sub")));
        }
        finally
        {
            Subst($"{letter}: /d");
        }

        // The same letter pointing at ordinary storage is accepted, and a protected folder reached through it is still protected.
        letter = FreeLetter();
        Subst($"{letter}: \"{_dir.Path}\"");
        try
        {
            string viaDrive = $@"{letter}:\plain\out";
            Assert.Equal(viaDrive, ClientOutputPath.CheckDirectory("extract.test", viaDrive, []));
            CommandException read = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory("extract.test", $@"{letter}:\client\Data\out", [client]));
            Assert.Contains("read-only", read.Message, StringComparison.Ordinal);
            CommandException reverse = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory("extract.test", Path.Combine(client, "Data", "out"), [$@"{letter}:\client"]));
            Assert.Contains("read-only", reverse.Message, StringComparison.Ordinal);
        }
        finally
        {
            Subst($"{letter}: /d");
        }
    }
}
