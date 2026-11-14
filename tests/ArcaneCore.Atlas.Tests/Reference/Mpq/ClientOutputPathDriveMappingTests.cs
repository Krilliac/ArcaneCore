using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// A drive letter made by <c>subst</c> or <c>DefineDosDevice</c> (or a network mapping) hides where its files really are; the output
/// rules are applied to the real location, found by opening the path and asking Windows for its final name. The QueryDosDevice
/// strings are tested as text; the other tests make real session drive letters through <see cref="SessionDrive"/>, which serialises
/// them across test processes and removes them again.
/// </summary>
public sealed class ClientOutputPathDriveMappingTests : IDisposable
{
    private const string Command = "extract.test";

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// The classification is only used for the refusal message of a network drive and for a letter that is not defined at all. A
    /// <c>\Device\...</c> name reads as Plain whatever it is (a CD drive, a raw path into a folder): where such a letter leads, and
    /// what kind of drive that is, is decided by opening the path (see the raw-target tests below and the drive-type tests in
    /// <see cref="ClientOutputPathGuardTests"/>).
    /// </summary>
    [Theory]
    [InlineData(@"\Device\HarddiskVolume3", DriveKind.Plain, null)]
    [InlineData(@"\Device\CdRom0", DriveKind.Plain, null)]
    [InlineData(@"\Device\HarddiskVolume3\Users\someone\repo\sub", DriveKind.Plain, null)]
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

    private string Repository(out string sub)
    {
        string tree = GuardFixtures.WorkingTree(_dir.Path, "repo", out _);
        sub = Path.Combine(tree, "sub");
        Directory.CreateDirectory(sub);
        return tree;
    }

    [WindowsFact]
    public void ASubstDriveIntoARepository_IsJudgedByWhereItReallyPoints()
    {
        string tree = Repository(out string sub);
        SessionDrive drive = SessionDrive.Subst(sub);
        try
        {
            DriveTarget target = ClientOutputPath.ParseDosDeviceTarget(ClientOutputPath.QueryDosDevice(drive.Letter)!);
            Assert.Equal(DriveKind.Substituted, target.Kind);
            Assert.Equal(sub, target.Path, ignoreCase: true);
            string asked = $@"{drive.Letter}:\data";

            CommandException directory = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, asked, []));
            Assert.Contains("git working tree", directory.Message, StringComparison.Ordinal);
            Assert.Contains(tree, directory.Message, StringComparison.Ordinal);
            CommandException file = Assert.Throws<CommandException>(() => ClientOutputPath.Check(asked + @"\x.bin", overwrite: true, []));
            Assert.Contains("git working tree", file.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(sub));
        }
        finally
        {
            drive.Dispose();
        }

        Assert.Null(drive.CleanupError);
    }

    [WindowsFact]
    public void ASubstDriveToOrdinaryStorage_IsAccepted_AndTheRealPathIsReturned()
    {
        string plain = Path.Combine(_dir.Path, "plain");
        Directory.CreateDirectory(plain);
        SessionDrive drive = SessionDrive.Subst(_dir.Path);
        try
        {
            // The check returns the folder it judged (D7), so the command writes there and not through the letter.
            Assert.Equal(Path.Combine(plain, "out"), ClientOutputPath.CheckDirectory(Command, $@"{drive.Letter}:\plain\out", []), ignoreCase: true);
            Assert.Equal(Path.Combine(plain, "x.bin"), ClientOutputPath.Check($@"{drive.Letter}:\plain\x.bin", overwrite: false, []), ignoreCase: true);
            Assert.Equal(_dir.Path, ClientOutputPath.CheckDirectory(Command, $@"{drive.Letter}:\", []), ignoreCase: true);
        }
        finally
        {
            drive.Dispose();
        }

        Assert.Null(drive.CleanupError);
    }

    [WindowsFact]
    public void AProtectedFolderReachedThroughASubstDrive_IsStillProtected_EitherWay()
    {
        string client = Path.Combine(_dir.Path, "client");
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        SessionDrive drive = SessionDrive.Subst(_dir.Path);
        try
        {
            CommandException read = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, $@"{drive.Letter}:\client\Data\out", [client]));
            Assert.Contains("read-only", read.Message, StringComparison.Ordinal);
            CommandException reverse = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, Path.Combine(client, "Data", "out"), [$@"{drive.Letter}:\client"]));
            Assert.Contains("read-only", reverse.Message, StringComparison.Ordinal);
        }
        finally
        {
            drive.Dispose();
        }

        Assert.Null(drive.CleanupError);
    }

    [WindowsFact]
    public void D2_ARawTargetDriveIntoARepository_IsJudgedByWhereItLeads()
    {
        string tree = Repository(out string sub);
        SessionDrive drive = SessionDrive.RawTarget(SessionDrive.NtPathOf(sub));
        try
        {
            // QueryDosDevice reads \Device\HarddiskVolumeN\...\repo\sub: the old classification called that Plain and judged X:\data as written.
            Assert.Equal(DriveKind.Plain, ClientOutputPath.ParseDosDeviceTarget(ClientOutputPath.QueryDosDevice(drive.Letter)!).Kind);
            string asked = $@"{drive.Letter}:\data";

            CommandException directory = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, asked, []));
            Assert.Contains("git working tree", directory.Message, StringComparison.Ordinal);
            Assert.Contains(tree, directory.Message, StringComparison.Ordinal);
            CommandException file = Assert.Throws<CommandException>(() => ClientOutputPath.Check(asked + @"\x.bin", overwrite: true, []));
            Assert.Contains("git working tree", file.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(sub));
        }
        finally
        {
            drive.Dispose();
        }

        Assert.Null(drive.CleanupError);
    }

    [WindowsFact]
    public void D4_ASecondLetterForTheWholeVolume_StillMeetsTheProtectedFolder_EitherWay()
    {
        string client = Path.Combine(_dir.Path, "client");
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        SessionDrive drive = SessionDrive.RawTarget(ClientOutputPath.QueryDosDevice(_dir.Path[0])!);
        try
        {
            string viaLetter = drive.Letter + client[1..];
            CommandException read = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, Path.Combine(viaLetter, "Data", "out"), [client]));
            Assert.Contains("read-only", read.Message, StringComparison.Ordinal);
            CommandException reverse = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, Path.Combine(client, "Data", "out"), [viaLetter]));
            Assert.Contains("read-only", reverse.Message, StringComparison.Ordinal);
        }
        finally
        {
            drive.Dispose();
        }

        Assert.Null(drive.CleanupError);
    }

    [WindowsFact]
    public void D3_AJunctionToASubstDriveIntoARepository_IsJudgedByWhereTheDriveLeads()
    {
        string tree = Repository(out string sub);
        string junction = Path.Combine(_dir.Path, "via-letter");
        SessionDrive drive = SessionDrive.Subst(sub);
        try
        {
            // The junction stores \??\X:\, which the old code took as the real place and never mapped again.
            GuardFixtures.JunctionTo(junction, $@"{drive.Letter}:\");
            try
            {
                CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.CheckDirectory(Command, Path.Combine(junction, "data"), []));
                Assert.Contains("git working tree", e.Message, StringComparison.Ordinal);
                Assert.Contains(tree, e.Message, StringComparison.Ordinal);
            }
            finally
            {
                DirectoryLinks.Delete(junction);
            }
        }
        finally
        {
            drive.Dispose();
        }

        Assert.Null(drive.CleanupError);
    }
}
