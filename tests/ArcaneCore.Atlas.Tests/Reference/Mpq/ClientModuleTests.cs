using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Dbc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>ClientModule composition and the client.* commands over synthetic client folders.</summary>
public sealed class ClientModuleTests : IDisposable
{
    private static readonly byte[] Wdt = Encoding.ASCII.GetBytes("synthetic wdt bytes");

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static AtlasContext Compose(string? clientRoot)
        => AtlasModules.Compose(new AtlasPaths(null, clientRoot), NullLoggerFactory.Instance, headless: true, [new ClientModule()]);

    private static Task<CommandResult> Run(AtlasContext context, string name, string args = "{}")
        => context.Commands.InvokeAsync(name, JsonNode.Parse(args)!.AsObject(), context.Services, CommandOrigin.Script, headless: true, CancellationToken.None);

    private string ArchiveClient()
    {
        MemoryClientFiles tables = ClientMapDirectoryTests.SampleFiles();
        string root = Path.Combine(_dir.Path, "client");
        _dir.Write(
            Path.Combine("client", "Data", "dbc.MPQ"),
            MpqTestWriter.Build(
            [
                new TestMpqFile(ClientMapDirectory.MapTablePath, tables.Read(ClientMapDirectory.MapTablePath)!) { Zlib = true },
                new TestMpqFile(ClientMapDirectory.AreaTablePath, tables.Read(ClientMapDirectory.AreaTablePath)!) { Zlib = true, Encrypted = true },
                new TestMpqFile("World\\Maps\\Azeroth\\Azeroth.wdt", Wdt),
            ]));
        return root;
    }

    [Fact]
    public async Task WithArchives_RegistersTheArchives_TheMapDirectory_AndAnsweringCommands()
    {
        using AtlasContext context = Compose(ArchiveClient());
        Assert.IsType<MpqFileSystem>(context.Services.Get<IClientFileSource>());
        Assert.NotNull(context.Services.Get<MpqFileSystem>());
        Assert.True(context.Services.Require<IMapDirectory>().TryGetMap(409, out ClientMap core));
        Assert.Equal("MoltenCore", core.Directory);
        Assert.Empty(context.Problems);

        CommandResult status = await Run(context, "client.status");
        Assert.True(status.Ok, status.ErrorMessage);
        Assert.Equal("mpq", (string?)status.Data!["source"]);
        Assert.Equal(1, (int)status.Data["totals"]!["archives"]!);
        Assert.Equal(3, (int)status.Data["listedFiles"]!);
        Assert.Equal("dbc.MPQ", (string?)status.Data["archives"]![0]!["archive"]);

        CommandResult deep = await Run(context, "client.status", """{"deep":true}""");
        Assert.True(deep.Ok, deep.ErrorMessage);
        Assert.True((bool)deep.Data!["totals"]!["sectorsScanned"]!);

        CommandResult listed = await Run(context, "client.files", """{"prefix":"world/maps","limit":10}""");
        Assert.True(listed.Ok, listed.ErrorMessage);
        Assert.Equal(1, (int)listed.Data!["total"]!);
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth.wdt", (string?)listed.Data["files"]![0]!["path"]);
        Assert.Equal("dbc.MPQ", (string?)listed.Data["files"]![0]!["archive"]);

        CommandResult truncated = await Run(context, "client.files", """{"limit":1}""");
        Assert.True((bool)truncated.Data!["truncated"]!);
        CommandResult badLimit = await Run(context, "client.files", """{"limit":0}""");
        Assert.Equal(CommandException.BadArguments, badLimit.ErrorCode);

        CommandResult maps = await Run(context, "client.maps");
        Assert.True(maps.Ok, maps.ErrorMessage);
        Assert.Equal(3, (int)maps.Data!["count"]!);
        Assert.Equal("Eastern Kingdoms", (string?)maps.Data["maps"]![0]!["name"]);
    }

    [Fact]
    public async Task ClientRead_WritesOneFile_AndRefusesUnsafeDestinations()
    {
        using AtlasContext context = Compose(ArchiveClient());
        string outPath = Path.Combine(_dir.Path, "out", "Azeroth.wdt");
        string args = new JsonObject { ["path"] = "World/Maps/Azeroth/Azeroth.wdt", ["out"] = outPath }.ToJsonString();

        CommandResult written = await Run(context, "client.read", args);
        Assert.True(written.Ok, written.ErrorMessage);
        Assert.Equal(Wdt, File.ReadAllBytes(outPath));
        Assert.Equal(Wdt.Length, (int)written.Data!["bytes"]!);
        Assert.Equal("dbc.MPQ", (string?)written.Data["archive"]);

        // An existing file needs overwrite=true.
        CommandResult again = await Run(context, "client.read", args);
        Assert.Equal(CommandException.BadArguments, again.ErrorCode);
        Assert.Contains("overwrite", again.ErrorMessage, StringComparison.Ordinal);
        string overwrite = new JsonObject { ["path"] = "World/Maps/Azeroth/Azeroth.wdt", ["out"] = outPath, ["overwrite"] = true }.ToJsonString();
        Assert.True((await Run(context, "client.read", overwrite)).Ok);

        // Relative paths, the client folder and any git working tree (a fake checkout is made here) are refused.
        string relative = new JsonObject { ["path"] = "World/Maps/Azeroth/Azeroth.wdt", ["out"] = "x.wdt" }.ToJsonString();
        Assert.Equal(CommandException.BadArguments, (await Run(context, "client.read", relative)).ErrorCode);
        string intoClient = new JsonObject { ["path"] = "World/Maps/Azeroth/Azeroth.wdt", ["out"] = Path.Combine(_dir.Path, "client", "copy.wdt") }.ToJsonString();
        Assert.Equal(CommandException.BadArguments, (await Run(context, "client.read", intoClient)).ErrorCode);
        string repoTree = Path.Combine(_dir.Path, "checkout");
        Directory.CreateDirectory(Path.Combine(repoTree, ".git"));
        Assert.Equal(repoTree, ClientOutputPath.FindWorkingTree(Path.Combine(repoTree, "artifacts", "Azeroth.wdt")));
        string intoRepo = new JsonObject { ["path"] = "World/Maps/Azeroth/Azeroth.wdt", ["out"] = Path.Combine(repoTree, "artifacts", "Azeroth.wdt") }.ToJsonString();
        CommandResult refused = await Run(context, "client.read", intoRepo);
        Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        Assert.Contains("working tree", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repoTree, "artifacts", "Azeroth.wdt")));

        // An absent file fails without writing anything.
        string absentOut = Path.Combine(_dir.Path, "out", "absent.bin");
        string absent = new JsonObject { ["path"] = "World/None.bin", ["out"] = absentOut }.ToJsonString();
        CommandResult missing = await Run(context, "client.read", absent);
        Assert.Equal(CommandException.Failed, missing.ErrorCode);
        Assert.False(File.Exists(absentOut));
    }

    [Fact]
    public async Task WithAnExtractedTree_FallsBackToTheDirectory()
    {
        MemoryClientFiles tables = ClientMapDirectoryTests.SampleFiles();
        _dir.Write(Path.Combine("extracted", "DBFilesClient", "Map.dbc"), tables.Read(ClientMapDirectory.MapTablePath)!);
        _dir.Write(Path.Combine("extracted", "DBFilesClient", "AreaTable.dbc"), tables.Read(ClientMapDirectory.AreaTablePath)!);
        using AtlasContext context = Compose(Path.Combine(_dir.Path, "extracted"));

        Assert.IsType<DirectoryClientFiles>(context.Services.Get<IClientFileSource>());
        Assert.Null(context.Services.Get<MpqFileSystem>());
        Assert.Equal("Westfall", context.Services.Require<IMapDirectory>().AreaName(40));
        CommandResult status = await Run(context, "client.status");
        Assert.Equal("directory", (string?)status.Data!["source"]);
        Assert.Equal(2, (int)status.Data["files"]!);
    }

    [Fact]
    public async Task WithoutAClient_TheCommandsSayWhy()
    {
        using AtlasContext context = Compose(null);
        Assert.Null(context.Services.Get<IClientFileSource>());
        Assert.Null(context.Services.Get<IMapDirectory>());

        CommandResult status = await Run(context, "client.status");
        Assert.True(status.Ok, status.ErrorMessage);
        Assert.Equal("none", (string?)status.Data!["source"]);
        Assert.Equal(CommandException.Unavailable, (await Run(context, "client.files")).ErrorCode);
        Assert.Equal(CommandException.Unavailable, (await Run(context, "client.maps")).ErrorCode);
        string read = new JsonObject { ["path"] = "a", ["out"] = Path.Combine(_dir.Path, "a.bin") }.ToJsonString();
        Assert.Equal(CommandException.Unavailable, (await Run(context, "client.read", read)).ErrorCode);
    }

    [Fact]
    public void ADamagedMapTable_IsAProblem_AndTheFilesStayOpen()
    {
        _dir.Write(
            Path.Combine("client", "Data", "dbc.MPQ"),
            MpqTestWriter.Build([new TestMpqFile(ClientMapDirectory.MapTablePath, Encoding.ASCII.GetBytes("WDBC but far too short"))]));
        using AtlasContext context = Compose(Path.Combine(_dir.Path, "client"));

        Assert.NotNull(context.Services.Get<IClientFileSource>());
        Assert.Null(context.Services.Get<IMapDirectory>());
        Assert.Contains(context.Problems, p => p.Contains("Client map tables could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void ADamagedArchive_IsAProblem_AndNothingIsRegistered()
    {
        _dir.Write(Path.Combine("client", "Data", "dbc.MPQ"), Encoding.ASCII.GetBytes("not an archive, only some text to fill the header"));
        using AtlasContext context = Compose(Path.Combine(_dir.Path, "client"));

        Assert.Null(context.Services.Get<IClientFileSource>());
        Assert.Contains(context.Problems, p => p.Contains("Client files could not be opened", StringComparison.Ordinal));
    }
}
