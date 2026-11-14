using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// <see cref="MapExtractor"/> and the <c>extract.maps</c> command over the in-memory client of <see cref="TerrainFixture"/>:
/// Azeroth lists three tiles of which two have an ADT, Kalimdor has no WDT and the dungeon's WDT lists no tile.
/// </summary>
public sealed class MapExtractorTests
{
    private static readonly AreaExploreFlags Areas = ExtractFixtures.Areas((TerrainFixture.Area, 0x0042));

    private static MapExtractor Extractor(TerrainFixture fixture) => new(fixture.Files, fixture.Maps, Areas);

    private static string MapFile(string root, TileKey tile) => Path.Combine(root, "maps", TerrainTile.FileName(tile.MapId, tile.X, tile.Y));

    [Fact]
    public void Extract_WritesOneFilePerTileThatHasAnAdt_UnderTheServersNames()
    {
        var fixture = new TerrainFixture();
        using var output = new TestDirectory();

        MapExtractReport report = Extractor(fixture).Extract(output.Path, null, overwrite: false, threads: 2, CancellationToken.None);

        Assert.Equal(Path.Combine(output.Path, "maps"), report.MapsDirectory);
        Assert.Equal(new[] { "0004832.map", "0004833.map" }, Directory.EnumerateFiles(report.MapsDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        byte[] goldshire = File.ReadAllBytes(MapFile(output.Path, TerrainFixture.Goldshire));
        Assert.Equal(MapTileEncoder.Encode(AdtFile.Parse(TerrainFixture.GoldshireAdt().Bytes), Areas), goldshire);
        Assert.Equal(0x0042, TerrainTile.Parse(goldshire).GetAreaFlag(0f, 0f));

        Assert.Equal(new uint[] { 0, 1, TerrainFixture.DungeonMap }, report.Maps.Select(m => m.Id));
        MapExtractMapReport azeroth = report.Maps[0];
        Assert.Equal(new MapExtractMapReport(0, "Azeroth", true, Listed: 3, Written: 2, Existing: 0, Absent: 1, Failed: 0, azeroth.Bytes), azeroth);
        Assert.Equal(goldshire.Length + new FileInfo(MapFile(output.Path, TerrainFixture.Flat)).Length, azeroth.Bytes);
        Assert.False(report.Maps[1].HasWdt);
        Assert.Equal(new MapExtractMapReport(TerrainFixture.DungeonMap, "DeadminesInstance", true, 0, 0, 0, 0, 0, 0), report.Maps[2]);
        Assert.Empty(report.Failures);
        Assert.Equal(2, report.Written);
        Assert.Equal(1, report.Absent);
    }

    [Fact]
    public void Extract_KeepsExistingFiles_UnlessToldToOverwrite()
    {
        var fixture = new TerrainFixture();
        using var output = new TestDirectory();
        MapExtractor extractor = Extractor(fixture);
        extractor.Extract(output.Path, null, overwrite: false, threads: 1, CancellationToken.None);
        string flat = MapFile(output.Path, TerrainFixture.Flat);
        byte[] real = File.ReadAllBytes(flat);
        File.WriteAllBytes(flat, [1, 2, 3]);

        MapExtractReport kept = extractor.Extract(output.Path, null, overwrite: false, threads: 1, CancellationToken.None);

        Assert.Equal(0, kept.Written);
        Assert.Equal(2, kept.Existing);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(flat));

        MapExtractReport rewritten = extractor.Extract(output.Path, null, overwrite: true, threads: 1, CancellationToken.None);

        Assert.Equal(2, rewritten.Written);
        Assert.Equal(0, rewritten.Existing);
        Assert.Equal(real, File.ReadAllBytes(flat));
        Assert.DoesNotContain(Directory.EnumerateFiles(rewritten.MapsDirectory), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ReportsADamagedTile_AndStillWritesTheOthers()
    {
        var fixture = new TerrainFixture();
        byte[] damaged = TerrainFixture.GoldshireAdt().Bytes[..2000];
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.ListedButAbsent), damaged);
        using var output = new TestDirectory();

        MapExtractReport report = Extractor(fixture).Extract(output.Path, [0], overwrite: false, threads: 2, CancellationToken.None);

        MapExtractMapReport azeroth = Assert.Single(report.Maps);
        Assert.Equal(2, azeroth.Written);
        Assert.Equal(0, azeroth.Absent);
        Assert.Equal(1, azeroth.Failed);
        MapExtractFailure failure = Assert.Single(report.Failures);
        Assert.Equal((0u, TerrainFixture.ListedButAbsent.X, TerrainFixture.ListedButAbsent.Y), (failure.Map, failure.TileX, failure.TileY));
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth_32_49.adt", failure.Path);
        Assert.NotEmpty(failure.Reason);
        Assert.False(File.Exists(MapFile(output.Path, TerrainFixture.ListedButAbsent)));
        Assert.True(File.Exists(MapFile(output.Path, TerrainFixture.Goldshire)));
    }

    [Fact]
    public void Extract_ReportsADamagedWdt_AsThatMapsFailure()
    {
        var fixture = new TerrainFixture();
        fixture.Files.Add(ClientMapPaths.WdtPath("Kalimdor"), [9, 9, 9, 9]);
        using var output = new TestDirectory();

        MapExtractReport report = Extractor(fixture).Extract(output.Path, [1], overwrite: false, threads: 1, CancellationToken.None);

        MapExtractMapReport kalimdor = Assert.Single(report.Maps);
        Assert.True(kalimdor.HasWdt);
        Assert.Equal(1, kalimdor.Failed);
        MapExtractFailure failure = Assert.Single(report.Failures);
        Assert.Equal((1u, -1, -1, "World\\Maps\\Kalimdor\\Kalimdor.wdt"), (failure.Map, failure.TileX, failure.TileY, failure.Path));
    }

    [Fact]
    public void Extract_RefusesAnUnknownMap_AndACancelledRun()
    {
        var fixture = new TerrainFixture();
        using var output = new TestDirectory();
        MapExtractor extractor = Extractor(fixture);

        Assert.Throws<ArgumentException>(() => extractor.Extract(output.Path, [7], overwrite: false, threads: 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.Extract(output.Path, null, overwrite: false, threads: 0, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => extractor.Extract(output.Path, null, overwrite: false, threads: 1, cancelled.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(output.Path, "maps")));
    }

    private static (CommandRegistry Registry, AtlasServices Services) Command(TerrainFixture? fixture, string? clientRoot = null)
    {
        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(new AtlasPaths(null, clientRoot));
        if (fixture is not null)
        {
            fixture.Files.Add(ClientMapDirectory.AreaTablePath, ExtractFixtures.AreaTable((TerrainFixture.Area, 0x0042)));
            services.Add<IClientFileSource>(fixture.Files);
            services.Add<IMapDirectory>(fixture.Maps);
        }

        return (registry, services);
    }

    private static async Task<CommandResult> Run((CommandRegistry Registry, AtlasServices Services) setup, JsonObject args)
        => await setup.Registry.InvokeAsync("extract.maps", args, setup.Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

    [Fact]
    public async Task Command_ExtractsEveryMap_AndReportsPerMapCounts()
    {
        using var output = new TestDirectory();
        var setup = Command(new TerrainFixture());

        CommandResult result = await Run(setup, new JsonObject { ["out"] = output.Path, ["threads"] = 2 });

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = result.Data!;
        Assert.Equal(Path.Combine(output.Path, "maps"), data["out"]!.GetValue<string>());
        Assert.Equal(2, data["written"]!.GetValue<int>());
        Assert.Equal(1, data["absent"]!.GetValue<int>());
        Assert.Equal(0, data["failed"]!.GetValue<int>());
        Assert.Equal(1, data["mapsWithTerrain"]!.GetValue<int>());
        Assert.Equal(3, data["maps"]!.AsArray().Count);
        Assert.Equal("Azeroth", data["maps"]![0]!["directory"]!.GetValue<string>());
        Assert.False(data["maps"]![1]!["hasWdt"]!.GetValue<bool>());
        Assert.Empty(data["failures"]!.AsArray());
        Assert.Equal(0x0042, TerrainTile.Parse(File.ReadAllBytes(MapFile(output.Path, TerrainFixture.Goldshire))).GetAreaFlag(0f, 0f));

        CommandResult onlyDungeon = await Run(setup, new JsonObject { ["out"] = output.Path, ["maps"] = "36", ["overwrite"] = true });
        Assert.True(onlyDungeon.Ok, onlyDungeon.ErrorMessage);
        Assert.Equal(0, onlyDungeon.Data!["written"]!.GetValue<int>());
        Assert.Single(onlyDungeon.Data!["maps"]!.AsArray());
    }

    [Fact]
    public async Task Command_RefusesBadDestinationsAndArguments_BeforeWritingAnything()
    {
        using var output = new TestDirectory();
        using var client = new TestDirectory();
        var setup = Command(new TerrainFixture(), client.Path);

        async Task<CommandResult> Refused(JsonObject args, string code)
        {
            CommandResult result = await Run(setup, args);
            Assert.False(result.Ok);
            Assert.Equal(code, result.ErrorCode);
            Assert.StartsWith("extract.maps:", result.ErrorMessage, StringComparison.Ordinal);
            return result;
        }

        await Refused(new JsonObject(), CommandException.BadArguments);
        await Refused(new JsonObject { ["out"] = "relative/folder" }, CommandException.BadArguments);
        CommandResult inRepository = await Refused(new JsonObject { ["out"] = Path.Combine(AppContext.BaseDirectory, "extracted") }, CommandException.BadArguments);
        Assert.Contains("git working tree", inRepository.ErrorMessage, StringComparison.Ordinal);
        CommandResult inClient = await Refused(new JsonObject { ["out"] = Path.Combine(client.Path, "Data") }, CommandException.BadArguments);
        Assert.Contains("read-only", inClient.ErrorMessage, StringComparison.Ordinal);
        await Refused(new JsonObject { ["out"] = client.Path }, CommandException.BadArguments);
        string file = output.Write("a-file", [1]);
        await Refused(new JsonObject { ["out"] = file }, CommandException.BadArguments);
        await Refused(new JsonObject { ["out"] = output.Path, ["maps"] = "7" }, CommandException.BadArguments);
        await Refused(new JsonObject { ["out"] = output.Path, ["maps"] = "zero" }, CommandException.BadArguments);
        await Refused(new JsonObject { ["out"] = output.Path, ["threads"] = 65 }, CommandException.BadArguments);
        await Refused(new JsonObject { ["out"] = output.Path, ["threads"] = -1 }, CommandException.BadArguments);

        Assert.False(Directory.Exists(Path.Combine(output.Path, "maps")));
    }

    [Fact]
    public async Task Command_IsUnavailableWithoutAClient_AndFailsWithoutTheAreaTable()
    {
        using var output = new TestDirectory();

        CommandResult noClient = await Run(Command(null), new JsonObject { ["out"] = output.Path });
        Assert.Equal(CommandException.Unavailable, noClient.ErrorCode);

        var fixture = new TerrainFixture();
        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);
        var services = new AtlasServices();
        services.Add<IClientFileSource>(fixture.Files);
        services.Add<IMapDirectory>(fixture.Maps);
        CommandResult noAreas = await Run((registry, services), new JsonObject { ["out"] = output.Path });
        Assert.Equal(CommandException.Failed, noAreas.ErrorCode);
        Assert.Contains("AreaTable.dbc", noAreas.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(output.Path, "maps")));
    }

    [Fact]
    public void Module_RegistersTheCommand_ForHeadlessAndInstanceUse()
    {
        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);

        Assert.True(registry.TryGet("extract.maps", out CommandDefinition definition));
        Assert.Equal(CommandSurfaces.Headless | CommandSurfaces.Instance, definition.Surfaces);
        Assert.Equal(new[] { "out", "maps", "overwrite", "threads" }, definition.Args.Select(a => a.Name));
        Assert.True(definition.Args[0].Required);
        Assert.Equal("extract", new ExtractModule().Name);
    }
}

/// <summary>
/// The extractor against the real 1.12.1 client and the reference tool's own output: every <c>.map</c> file the
/// reference extractor wrote must come out byte for byte. Skipped, with the reason, without both data roots.
/// </summary>
public sealed class MapExtractorRealDataTests
{
    [ClientTerrainRealFact]
    public void Encode_EveryTileOfEveryMap_EqualsTheReferenceExtractorsFile()
    {
        AreaExploreFlags areas = AreaExploreFlags.Load(ClientTerrainRealData.Files);
        int compared = 0;
        var different = new List<string>();
        foreach (uint mapId in ClientTerrainRealData.ServerMapIds())
        {
            Assert.True(ClientTerrainRealData.Maps.TryGetMap(mapId, out ClientMap map), $"The server has .map files for map {mapId}, which the client's Map.dbc does not list.");
            foreach (TileKey tile in ClientTerrainRealData.ServerTiles(mapId))
            {
                byte[] reference = File.ReadAllBytes(Path.Combine(ClientTerrainRealData.ServerMapsDirectory, TerrainTile.FileName(mapId, tile.X, tile.Y)));
                byte[]? adt = ClientTerrainRealData.Files.Read(ClientMapPaths.AdtPath(map.Directory, tile));
                Assert.True(adt is not null, $"The client has no ADT for {tile}, which the server has a .map for.");
                compared++;
                if (!MapTileEncoder.Encode(AdtFile.Parse(adt), areas).AsSpan().SequenceEqual(reference))
                {
                    different.Add(TerrainTile.FileName(mapId, tile.X, tile.Y));
                }
            }
        }

        Assert.True(compared > 2000, $"Only {compared} tiles were compared; a full 1.12.1 extraction has 2429.");
        Assert.True(different.Count == 0, $"{different.Count} of {compared} tiles differ from the reference extractor's file: {string.Join(", ", different.Take(20))}");
    }

    [ClientTerrainRealFact]
    public void Extract_WritesExactlyTheReferenceExtractorsFileSet()
    {
        using var output = new TestDirectory();
        AreaExploreFlags areas = AreaExploreFlags.Load(ClientTerrainRealData.Files);

        MapExtractReport report = new MapExtractor(ClientTerrainRealData.Files, ClientTerrainRealData.Maps, areas)
            .Extract(output.Path, null, overwrite: false, Environment.ProcessorCount, CancellationToken.None);

        Assert.Empty(report.Failures);
        string[] written = [.. Directory.EnumerateFiles(report.MapsDirectory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        string[] reference = [.. Directory.EnumerateFiles(ClientTerrainRealData.ServerMapsDirectory, "*.map").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        Assert.Equal(reference, written);
    }
}
