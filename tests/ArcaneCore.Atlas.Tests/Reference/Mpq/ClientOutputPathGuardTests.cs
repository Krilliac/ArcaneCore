using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// The output-folder guard against what is already on the machine when an extraction starts: links, drive letters, mount points,
/// unreadable folders and repositories without a <c>.git</c> marker. Cases that would need administrator rights or a redefined
/// real drive letter go through <see cref="FakeOutputPathPlatform"/>.
/// </summary>
public sealed class ClientOutputPathGuardTests : IDisposable
{
    private const string Command = "extract.test";

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CommandException Refused(string requested, IOutputPathPlatform? platform = null, params string[] protectedRoots)
    {
        CommandException e = Assert.Throws<CommandException>(
            () => ClientOutputPath.CheckDirectory(Command, requested, protectedRoots, platform ?? SystemOutputPathPlatform.Instance));
        Assert.Equal(CommandException.BadArguments, e.Code);
        Assert.StartsWith(Command + ":", e.Message, StringComparison.Ordinal);
        return e;
    }

    private string Plain(string name = "plain")
    {
        string folder = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ---- accepted: the guard must not simply refuse everything ----

    [Fact]
    public void Accepts_AnOrdinaryLocalFolder_ExistingOrNot_AndReturnsItsRealPath()
    {
        string existing = Plain();
        string fresh = Path.Combine(existing, "a", "b");

        Assert.Equal(existing, ClientOutputPath.CheckDirectory(Command, existing, []));
        Assert.Equal(fresh, ClientOutputPath.CheckDirectory(Command, fresh, []));
        Assert.Equal(Path.Combine(fresh, "x.bin"), ClientOutputPath.Check(Path.Combine(fresh, "x.bin"), overwrite: false, []));
        Assert.False(Directory.Exists(fresh));
    }

    [Fact]
    public void Accepts_AFolderBesideTheClientFolder_AndAFolderHoldingOnlyAHeadFile()
    {
        string client = Plain("client");
        string beside = Path.Combine(_dir.Path, "client-data", "out");
        string notBare = Plain("notes");
        File.WriteAllText(Path.Combine(notBare, "HEAD"), "not a repository");

        Assert.Equal(beside, ClientOutputPath.CheckDirectory(Command, beside, [client]));
        Assert.Equal(Path.Combine(notBare, "out"), ClientOutputPath.CheckDirectory(Command, Path.Combine(notBare, "out"), [client]));
    }

    [TerrainDataFact]
    public void Accepts_TheConfiguredServerDataRoot_AndAFolderInsideIt_WithoutCreatingAnything()
    {
        string terrain = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable)!;
        string inside = Path.Combine(terrain, "guard-accept-probe-" + Guid.NewGuid().ToString("N"));

        string checkedRoot = ClientOutputPath.CheckDirectory(Command, terrain, []);
        string checkedInside = ClientOutputPath.CheckDirectory(Command, inside, []);

        Assert.True(Directory.Exists(checkedRoot));
        Assert.Equal(Path.Combine(checkedRoot, Path.GetFileName(inside)), checkedInside);
        Assert.False(Directory.Exists(inside));
    }

    // ---- D1, D6, D9: what QueryDosDevice and GetDriveType say no longer decides where the path leads ----

    [WindowsFact]
    public void D1_ASubstChainLongerThanTheOldHopLimit_CannotHideARepository()
    {
        string tree = GuardFixtures.WorkingTree(_dir.Path, "repo", out string inside);

        // Every letter claims to be a subst of the next one: the old loop gave up after 8 hops and judged a path that does not exist.
        var platform = new FakeOutputPathPlatform { DosDevice = letter => $@"\??\{(char)(char.ToUpperInvariant(letter) == 'Z' ? 'H' : char.ToUpperInvariant(letter) + 1)}:\" };

        string message = Refused(Path.Combine(inside, "out"), platform).Message;

        Assert.Contains("git working tree", message, StringComparison.Ordinal);
        Assert.Contains(tree, message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void D6_ADriveLetterQueryDosDeviceCannotResolve_IsRefused()
    {
        var platform = new FakeOutputPathPlatform { DosDevice = _ => null };

        string message = Refused(Path.Combine(Plain(), "out"), platform).Message;

        Assert.Contains("QueryDosDevice", message, StringComparison.Ordinal);
    }

    [WindowsTheory]
    [InlineData("redirector")]
    [InlineData("letter-remote")]
    [InlineData("volume-remote")]
    [InlineData("letter-cdrom")]
    public void D9_ANetworkOrCdDrive_IsRefused_WhetherTheLetterOrTheVolumeSaysSo(string how)
    {
        var platform = new FakeOutputPathPlatform
        {
            DosDevice = how == "redirector" ? _ => @"\Device\LanmanRedirector\;Z:0000000000012345\server\share" : null,
            DriveType = root => how switch
            {
                "letter-remote" when root.Length == 3 => WindowsDriveType.Remote,
                "volume-remote" when root.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) => WindowsDriveType.Remote,
                "letter-cdrom" when root.Length == 3 => WindowsDriveType.CdRom,
                _ => null,
            },
        };

        Assert.Contains(ClientOutputPathTests.DeviceOrNetwork, Refused(Path.Combine(Plain(), "out"), platform).Message, StringComparison.Ordinal);
    }

    // ---- D3: link targets are judged where they really lead ----

    [Fact]
    public void D3_ADanglingLinkAlongOut_IsRefused()
    {
        string gone = Plain("gone");
        string link = Path.Combine(_dir.Path, "dangling");
        DirectoryLinks.Make(link, gone);
        Directory.Delete(gone);
        try
        {
            Assert.Contains("does not exist", Refused(Path.Combine(link, "data")).Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", Refused(link).Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryLinks.Delete(link);
        }
    }

    [WindowsFact]
    public void D3_ALinkWhoseTargetIsANetworkPath_IsRefused()
    {
        string plain = Plain();
        var platform = new FakeOutputPathPlatform
        {
            OpenOverride = (path, mode) => mode == OpenMode.Follow && string.Equals(path, plain, StringComparison.OrdinalIgnoreCase)
                ? new OpenResult(OpenOutcome.Opened, new OpenedEntry(null, isDirectory: true, new FileIdentity(1, 2, 3), null, @"\\?\UNC\server\share\plain", "volume GUID name: not a local volume"), null)
                : null,
        };

        Assert.Contains(ClientOutputPathTests.DeviceOrNetwork, Refused(Path.Combine(plain, "out"), platform).Message, StringComparison.Ordinal);
    }

    // ---- D4: protected folders compared by where they are, not by how they are spelled ----

    [ShortNamesFact]
    public void D4_AProtectedFolderSpelledAsAShortName_StillProtects()
    {
        string client = Plain("Protected Client Installation");
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        string shortClient = GuardFixtures.ShortPath(client)!;

        Assert.Contains("read-only", Refused(Path.Combine(client, "Data", "out"), null, shortClient).Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void D4_1_ACanonicalPathThatReopensSomethingElse_IsRefused()
    {
        string plain = Plain();
        int opens = 0;
        var platform = new FakeOutputPathPlatform
        {
            OpenOverride = (path, mode) =>
            {
                if (mode != OpenMode.Follow || !string.Equals(path, plain, StringComparison.OrdinalIgnoreCase) || Interlocked.Increment(ref opens) < 2)
                {
                    return null;
                }

                // The second open of the same folder (the round trip through the canonical name) reaches another file id.
                OpenResult real = SystemOutputPathPlatform.Instance.Open(path, mode);
                using OpenedEntry entry = real.Entry!;
                FileIdentity other = entry.Identity with { IdLow = entry.Identity.IdLow ^ 1 };
                return new OpenResult(OpenOutcome.Opened, new OpenedEntry(null, entry.IsDirectory, other, entry.GuidPath, entry.DosPath, null), null);
            },
        };

        Assert.Contains("opens something else", Refused(Path.Combine(plain, "out"), platform).Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void D4_AProtectedFolderThatCannotBeResolved_RefusesTheCommand()
    {
        string client = Plain("client");
        var platform = new FakeOutputPathPlatform
        {
            OpenOverride = (path, _) => string.Equals(path, client, StringComparison.OrdinalIgnoreCase)
                ? new OpenResult(OpenOutcome.Failed, null, "Windows error 5: Access is denied (test).")
                : null,
        };

        string message = Refused(Path.Combine(Plain(), "out"), platform, client).Message;

        Assert.Contains("protected folder", message, StringComparison.Ordinal);
        Assert.Contains("cannot", message, StringComparison.Ordinal);
    }

    // ---- D5: every mount path of the volume is checked ----

    [WindowsFact]
    public void D5_AnotherMountPathOfTheVolume_InsideARepository_IsRefused()
    {
        string tree = GuardFixtures.WorkingTree(_dir.Path, "repo", out _);
        string mountFolder = Path.Combine(tree, "mnt");
        Directory.CreateDirectory(mountFolder);
        string driveRoot = Path.GetPathRoot(_dir.Path)!;
        var platform = new FakeOutputPathPlatform { MountPaths = _ => [driveRoot, mountFolder + Path.DirectorySeparatorChar] };

        string message = Refused(Path.Combine(Plain(), "out"), platform).Message;

        Assert.Contains("git working tree", message, StringComparison.Ordinal);
        Assert.Contains(tree, message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void D5_AVolumeMountedInsideItself_IsRefusedAtTheAliasCap()
    {
        string loop = Plain("loop");
        string driveRoot = Path.GetPathRoot(_dir.Path)!;
        var platform = new FakeOutputPathPlatform { MountPaths = _ => [driveRoot, loop + Path.DirectorySeparatorChar] };

        Assert.Contains("mount", Refused(Path.Combine(Plain(), "out"), platform).Message, StringComparison.Ordinal);
    }

    // ---- N1, N2, N3: the working-tree walk ----

    [Fact]
    public void N1_ALinkInsideARepositoryLeadingOut_IsRefused_BecauseTheTypedPathIsInsideTheRepository()
    {
        string tree = GuardFixtures.WorkingTree(_dir.Path, "repo", out _);
        string outside = Plain("outside");
        string link = Path.Combine(tree, "link");
        DirectoryLinks.Make(link, outside);
        try
        {
            string message = Refused(Path.Combine(link, "data")).Message;
            Assert.Contains("git working tree", message, StringComparison.Ordinal);
            Assert.Contains(tree, message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            DirectoryLinks.Delete(link);
        }
    }

    [WindowsFact]
    public void N2_AGitMarkerThatCannotBeRead_Refuses_InsteadOfReadingAsNoRepository()
    {
        string tree = GuardFixtures.WorkingTree(_dir.Path, "repo", out string inside);
        string marker = Path.Combine(tree, ".git");
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string? setup = GuardFixtures.Icacls(marker, "/deny", $"{user}:(RA,REA,RD)") ?? GuardFixtures.Icacls(tree, "/deny", $"{user}:(RD)");
        string? cleanup;
        try
        {
            Assert.Null(setup);
            Assert.False(Directory.Exists(marker), "the deny entries did not hide the marker, so this test proves nothing");
            Assert.Contains("cannot be read", Refused(Path.Combine(inside, "out")).Message, StringComparison.Ordinal);
        }
        finally
        {
            cleanup = GuardFixtures.Icacls(tree, "/remove:d", user) ?? GuardFixtures.Icacls(marker, "/remove:d", user);
        }

        Assert.Null(cleanup);
    }

    [Fact]
    public void N3_ABareRepository_IsRefused()
    {
        string bare = Plain("project.git");
        File.WriteAllText(Path.Combine(bare, "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(bare, "objects"));
        Directory.CreateDirectory(Path.Combine(bare, "refs"));

        string message = Refused(Path.Combine(bare, "hooks", "out")).Message;

        Assert.Contains("bare git repository", message, StringComparison.Ordinal);
        Assert.Contains(bare, message, StringComparison.Ordinal);
    }

    // ---- N5: what GetFullPath and the device namespace would rewrite ----

    [WindowsTheory]
    [InlineData(@"NUL\out")]
    [InlineData(@"com1\out")]
    [InlineData(@"LPT9.txt\out")]
    [InlineData(@"data\AUX")]
    [InlineData(@"data\Con.d")]
    public void N5_AReservedDeviceNameComponent_IsRefused(string tail)
    {
        Assert.Contains("reserved device name", Refused(Path.Combine(_dir.Path, tail)).Message, StringComparison.Ordinal);
    }

    // ---- D7: the folder written is the folder judged ----

    [Fact]
    public void D7_HoldDirectory_AChildSwappedForALinkIntoARepositoryAfterItIsCreated_IsRefused()
    {
        GuardFixtures.WorkingTree(_dir.Path, "repo", out string inside);
        string child = Path.Combine(Plain("out"), "maps");
        string expected = ClientOutputPath.CheckDirectory(Command, child, []);
        var platform = new FakeOutputPathPlatform
        {
            AfterCreateDirectory = path =>
            {
                Directory.Delete(path);
                DirectoryLinks.Make(path, inside);
            },
        };
        try
        {
            CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.HoldDirectory(Command, child, expected, platform).Dispose());
            Assert.Contains("changed after the check", e.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(inside));
        }
        finally
        {
            if (Directory.Exists(child))
            {
                DirectoryLinks.Delete(child);
            }
        }
    }

    [WindowsFact]
    public void D7_HoldDirectory_TheHeldFolderAndItsParentCannotBeRenamed_UntilReleased()
    {
        string outRoot = Plain("out");
        string child = Path.Combine(outRoot, "maps");
        string expected = ClientOutputPath.CheckDirectory(Command, child, []);

        using (ClientOutputPath.HoldDirectory(Command, child, expected))
        {
            Assert.True(Directory.Exists(child));
            Assert.ThrowsAny<IOException>(() => Directory.Move(child, child + "-moved"));
            Exception? parent = Record.Exception(() => Directory.Move(outRoot, outRoot + "-moved"));
            Assert.True(parent is IOException or UnauthorizedAccessException, $"renaming the parent of a held folder should fail, got {parent?.GetType().Name ?? "success"}");
            File.WriteAllBytes(Path.Combine(child, "tile.map"), [1, 2, 3]);
            File.Delete(Path.Combine(child, "tile.map"));
        }

        Directory.Move(child, child + "-moved");
        Assert.True(Directory.Exists(child + "-moved"));
    }

    [WindowsFact]
    public void D7_HoldDirectory_ALinkedChildIsHeldAsWell_SoTheLinkCannotBeSwappedDuringTheRun()
    {
        string storage = Plain("storage");
        string child = Path.Combine(Plain("out"), "maps");
        DirectoryLinks.Make(child, storage);
        try
        {
            string expected = ClientOutputPath.CheckDirectory(Command, child, []);
            Assert.Equal(storage, expected);
            using (ClientOutputPath.HoldDirectory(Command, child, expected))
            {
                Assert.ThrowsAny<IOException>(() => Directory.Delete(child, recursive: false));
            }
        }
        finally
        {
            DirectoryLinks.Delete(child);
        }
    }

    [Fact]
    public void D7_HoldDirectory_AnOrdinaryChild_IsCreated_AndAWrongExpectationIsRefused()
    {
        string child = Path.Combine(Plain("out"), "vmaps");
        string expected = ClientOutputPath.CheckDirectory(Command, child, []);

        using (ClientOutputPath.HoldDirectory(Command, child, expected))
        {
            Assert.True(Directory.Exists(child));
        }

        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.HoldDirectory(Command, child, Path.Combine(_dir.Path, "elsewhere")).Dispose());
        Assert.Contains("changed after the check", e.Message, StringComparison.Ordinal);
    }
}

/// <summary>A Windows fact that needs 8.3 short names on the temp volume; skipped, visibly, when the volume makes none.</summary>
public sealed class ShortNamesFactAttribute : FactAttribute
{
    public ShortNamesFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows 8.3 short names.";
            return;
        }

        string probe = Path.Combine(Path.GetTempPath(), "arcane-atlas-short-name-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            if (GuardFixtures.ShortPath(probe) is null)
            {
                Skip = "The temp volume makes no 8.3 short names.";
            }
        }
        finally
        {
            Directory.Delete(probe);
        }
    }
}

/// <summary>A fact that needs the configured server data root (<c>ARCANECORE_TEST_TERRAIN_DIR</c>); skipped, visibly, without it.</summary>
public sealed class TerrainDataFactAttribute : FactAttribute
{
    public TerrainDataFactAttribute()
    {
        string? terrain = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(terrain) || !Directory.Exists(terrain))
        {
            Skip = $"{AtlasPaths.TerrainEnvironmentVariable} is not set to an existing server data root.";
        }
    }
}
