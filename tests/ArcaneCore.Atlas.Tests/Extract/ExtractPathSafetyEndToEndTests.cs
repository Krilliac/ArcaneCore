using System.Diagnostics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// The extractors' path safety seen from outside: model names read from client data must not become file names outside
/// <c>vmaps</c>, and a link at a command's child folder (<c>maps</c>, <c>vmaps</c>, <c>mmaps</c>) is judged like <c>out</c>
/// itself. Nothing here touches <c>ExtractOutput</c>, so this file builds before the fix does.
/// </summary>
public sealed class ExtractPathSafetyEndToEndTests
{
    private const string NotPlain = "is not a plain file name";

    /// <summary>
    /// Doodad paths as an ADT's MMDX can store them, with the name the vmap extractor derives from each (the plain name
    /// cuts at the last backslash only, so a forward slash stays in the name) and whether that name is a plain file name.
    /// The derived names are already in the case <c>FixNameCase</c> gives, so they equal the stored text with <c>.m2</c>.
    /// <c>ClientFile</c> is the client file the fixture adds for the model (null: the fixture has it already).
    /// </summary>
    private static readonly (string Stored, string Name, bool Plain, string? ClientFile)[] Doodads =
    [
        (VmapFixture.CubeStoredPath, "Cube.m2", true, null),
        ("../../Escape.mdx", "../../Escape.m2", false, "../../Escape.m2"),
        ("World/../../Escape2.mdx", "World/../../Escape2.m2", false, "World/../../Escape2.m2"),
        ("/Zzqx-Arcane-Test/Rooted.mdx", "/Zzqx-Arcane-Test/Rooted.m2", false, "/Zzqx-Arcane-Test/Rooted.m2"),

        // Backslashes are cut by the plain-name step already: the folder part never reaches the file name.
        ("..\\..\\Escape3.mdx", "Escape3.m2", true, "..\\..\\Escape3.m2"),
    ];

    [Fact]
    public void Vmaps_ModelNamesThatEscapeTheFolder_AreDropped_ReportedOnce_AndNothingLandsOutsideVmaps()
    {
        var fixture = new VmapFixture();
        byte[] cube = ModelFixtures.CubeM2();
        foreach ((_, _, _, string? clientFile) in Doodads)
        {
            if (clientFile is not null)
            {
                fixture.Files.Add(clientFile, cube);
            }
        }

        var tile = new AdtSpec { Tile = VmapFixture.Tile };
        uint id = 7001;
        double offset = 0;
        foreach ((string stored, _, _, _) in Doodads)
        {
            tile.Doodads.Add(DoodadAt(stored, id++, offset += 10));
        }

        // The first escaping name is placed a second time: it is still reported once.
        tile.Doodads.Add(DoodadAt("../../Escape.mdx", id, offset + 10));
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", VmapFixture.Tile), SyntheticTerrain.Adt(tile).Bytes);
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", VmapFixture.Neighbour), SyntheticTerrain.Adt(new AdtSpec { Tile = VmapFixture.Neighbour }).Bytes);

        using var scratch = new TestDirectory();
        string output = Path.Combine(scratch.Path, "a", "b", "out");

        VmapExtractReport report = fixture.Extractor().Extract(output, [0], threads: 2, CancellationToken.None);

        // Every file anywhere under the scratch root is one of the plain placements' files, inside <out>/vmaps.
        string[] files = [.. Directory.EnumerateFiles(scratch.Path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(scratch.Path, f).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)];
        Assert.Equal(
            new[] { "a/b/out/vmaps/000.vmtree", "a/b/out/vmaps/000_31_49.vmtile", "a/b/out/vmaps/Cube.m2.vmo", "a/b/out/vmaps/Escape3.m2.vmo" },
            files);
        Assert.Equal(Path.Combine(output, "vmaps"), report.VmapsDirectory);
        Assert.True(File.Exists(Path.Combine(report.VmapsDirectory, "Cube.m2.vmo")));

        // The spawn count, the model file count, the tree and the tile hold the two plain placements only.
        Assert.Equal(2, Assert.Single(report.Maps).Spawns);
        Assert.Equal(2, report.ModelFiles);
        (VMapTree tree, _) = VMapTree.Parse(0, File.ReadAllBytes(Path.Combine(report.VmapsDirectory, "000.vmtree")));
        Assert.Equal(2, tree.Tree.PrimitiveCount);
        List<(ModelSpawn Spawn, uint Slot)> spawns = VMapTree.ParseTile(File.ReadAllBytes(Path.Combine(report.VmapsDirectory, "000_31_49.vmtile")));
        Assert.Equal(new[] { "Cube.m2", "Escape3.m2" }, spawns.Select(s => s.Spawn.Name));

        // Each escaping name is reported once, with the reason; nothing else is reported as not plain.
        foreach (string name in Doodads.Where(d => !d.Plain).Select(d => d.Name))
        {
            Assert.Single(report.Problems, p => p.Contains(NotPlain, StringComparison.Ordinal) && p.Contains(name, StringComparison.Ordinal));
        }

        Assert.Equal(3, report.Problems.Count(p => p.Contains(NotPlain, StringComparison.Ordinal)));
    }

    private static DoodadSpec DoodadAt(string stored, uint uniqueId, double yOffset)
        => new(stored, uniqueId, ModelWorld.Raw(VmapFixture.Cube.X, VmapFixture.Cube.Y + yOffset, VmapFixture.Cube.Z), new WorldPoint(0, 0, 0), 1024, 0);

    /// <summary>A scratch folder holding a <c>.git</c> directory and an empty folder inside it; returns the empty folder.</summary>
    private static string FakeWorkingTree(TestDirectory scratch)
    {
        string tree = Path.Combine(scratch.Path, "tree");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        string folder = Path.Combine(tree, "target");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task<CommandResult> Invoke(string command, string root, string? clientRoot)
    {
        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(new AtlasPaths(null, clientRoot));
        var args = new JsonObject { ["threads"] = 2 };
        switch (command)
        {
            case "extract.maps":
                var terrain = new TerrainFixture();
                terrain.Files.Add(ClientMapDirectory.AreaTablePath, ExtractFixtures.AreaTable((TerrainFixture.Area, 0x0042)));
                services.Add<IClientFileSource>(terrain.Files);
                services.Add<IMapDirectory>(terrain.Maps);
                args["out"] = root;
                break;
            case "extract.vmaps":
                var models = new VmapFixture();
                services.Add<IClientFileSource>(models.Files);
                services.Add<IMapDirectory>(models.Maps);
                args["out"] = root;
                break;
            case "extract.mmaps":
                args["data"] = root;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "not an extract command");
        }

        return await registry.InvokeAsync(command, args, services, CommandOrigin.Cli, headless: true, CancellationToken.None);
    }

    [Theory]
    [InlineData("extract.maps", "maps")]
    [InlineData("extract.vmaps", "vmaps")]
    [InlineData("extract.mmaps", "mmaps")]
    public async Task Command_ChildFolderLinkedIntoAWorkingTree_IsRefused_AndNothingIsWrittenThroughIt(string command, string child)
    {
        using var scratch = new TestDirectory();
        string target = FakeWorkingTree(scratch);
        string root = Path.Combine(scratch.Path, "out");
        Directory.CreateDirectory(root);
        if (command == "extract.mmaps")
        {
            // The command wants a maps or vmaps folder before it does anything else; give it one so the refusal can only be the link.
            Directory.CreateDirectory(Path.Combine(root, "maps"));
        }

        string link = Path.Combine(root, child);
        MakeDirectoryLink(link, target);
        try
        {
            CommandResult result = await Invoke(command, root, clientRoot: null);

            Assert.False(result.Ok, "the command ran through a link into a git working tree");
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
            Assert.StartsWith(command + ":", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("git working tree", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(target));
            Assert.Equal(new[] { ".git", "target" }, Directory.GetFileSystemEntries(Path.GetDirectoryName(target)!).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            DeleteDirectoryLink(link);
        }
    }

    [Fact]
    public async Task Command_MapsChildFolderLinkedIntoTheClientFolder_IsRefused_AndNothingIsWrittenThroughIt()
    {
        using var scratch = new TestDirectory();
        using var client = new TestDirectory();
        string inClient = Path.Combine(client.Path, "Data");
        Directory.CreateDirectory(inClient);
        string root = Path.Combine(scratch.Path, "out");
        Directory.CreateDirectory(root);
        string link = Path.Combine(root, "maps");
        MakeDirectoryLink(link, inClient);
        try
        {
            CommandResult result = await Invoke("extract.maps", root, client.Path);

            Assert.False(result.Ok, "the command ran through a link into the client folder");
            Assert.Equal(CommandException.BadArguments, result.ErrorCode);
            Assert.StartsWith("extract.maps:", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("read-only", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(inClient));
        }
        finally
        {
            DeleteDirectoryLink(link);
        }
    }

    [Fact]
    public async Task Command_MapsChildFolderLinkedToOrdinaryStorage_IsAccepted_AndTheFilesLandThere()
    {
        using var scratch = new TestDirectory();
        using var client = new TestDirectory();
        string storage = Path.Combine(scratch.Path, "storage");
        Directory.CreateDirectory(storage);
        string root = Path.Combine(scratch.Path, "out");
        Directory.CreateDirectory(root);
        string link = Path.Combine(root, "maps");
        MakeDirectoryLink(link, storage);
        try
        {
            CommandResult result = await Invoke("extract.maps", root, client.Path);

            Assert.True(result.Ok, result.ErrorMessage);
            Assert.Equal(2, result.Data!["written"]!.GetValue<int>());
            Assert.Equal(new[] { "0004832.map", "0004833.map" }, Directory.GetFiles(storage).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            DeleteDirectoryLink(link);
        }
    }

    /// <summary>A directory link: a junction on Windows (needs no privilege), a symbolic link elsewhere.</summary>
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

    /// <summary>
    /// Removes the link itself and never what it points to. A recursive delete of a folder holding a junction throws without
    /// elevation on Windows, so every test removes its links here before its scratch folder goes.
    /// </summary>
    private static void DeleteDirectoryLink(string link)
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
