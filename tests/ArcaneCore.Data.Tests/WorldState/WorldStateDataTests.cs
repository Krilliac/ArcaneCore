using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>A test that needs the (GPL, never committed) classic-db dump and is skipped, visibly, without it.</summary>
public sealed class ClassicDbDumpFactAttribute : FactAttribute
{
    public static string DumpPath { get; } = Environment.GetEnvironmentVariable("ARCANE_CLASSICDB_DUMP")
        ?? @"D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz";

    public ClassicDbDumpFactAttribute()
    {
        if (!File.Exists(DumpPath))
        {
            Skip = $"classic-db dump not found at {DumpPath} (set ARCANE_CLASSICDB_DUMP)";
        }
    }
}

/// <summary>The world-state tables (<see cref="WorldStateDataModule"/>) and the dump importer.</summary>
public sealed class WorldStateDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private const string SyntheticDump = """
        CREATE TABLE `exploration_basexp` (
          `basexp` mediumint NOT NULL DEFAULT '0',
          `comment` varchar(10) DEFAULT NULL,
          `level` tinyint NOT NULL DEFAULT '0',
          PRIMARY KEY (`level`)
        ) ENGINE=MyISAM;
        INSERT INTO `exploration_basexp` VALUES (0,0,0),(5,0,1),(660,0,60);
        CREATE TABLE `game_weather` (
          `winter_storm_chance` tinyint unsigned NOT NULL,
          `zone` mediumint unsigned NOT NULL DEFAULT '0',
          `spring_rain_chance` tinyint unsigned NOT NULL,
          `spring_snow_chance` tinyint unsigned NOT NULL,
          `spring_storm_chance` tinyint unsigned NOT NULL,
          `summer_rain_chance` tinyint unsigned NOT NULL,
          `summer_snow_chance` tinyint unsigned NOT NULL,
          `summer_storm_chance` tinyint unsigned NOT NULL,
          `fall_rain_chance` tinyint unsigned NOT NULL,
          `fall_snow_chance` tinyint unsigned NOT NULL,
          `fall_storm_chance` tinyint unsigned NOT NULL,
          `winter_rain_chance` tinyint unsigned NOT NULL,
          `winter_snow_chance` tinyint unsigned NOT NULL,
          PRIMARY KEY (`zone`)
        ) ENGINE=MyISAM;
        INSERT INTO `game_weather` VALUES (12,1,2,3,4,5,6,7,8,9,10,11,0),(99,2,0,0,0,0,0,0,0,0,0,0,0);
        """;

    [Fact]
    public void Module_IsDiscovered_AtItsConstant()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is WorldStateDataModule);
        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal(WorldStateDataModule.Version, module.SchemaVersion);
        Assert.Contains(WorldDbContext.Schema.Steps, s => s.Version == WorldStateDataModule.Version);
    }

    [Fact]
    public void Parse_MapsColumnsByName_NotByPosition()
    {
        WorldStateContent content = WorldStateDumpImporter.Parse(new StringReader(SyntheticDump));

        // game_weather columns are in a shuffled order in the dump: winter_storm first, zone second.
        Assert.Equal([2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u, 10u, 11u, 0u, 12u], content.Weather[0].Chances);
        Assert.Equal([1u, 2u], content.Weather.Select(w => w.Zone));
        Assert.Equal([0u, 1u, 60u], content.BaseXp.Select(b => b.Level));
        Assert.Equal([0u, 5u, 660u], content.BaseXp.Select(b => b.BaseXp));
    }

    [Theory]
    [InlineData("INSERT INTO `exploration_basexp` VALUES (1,2),(3,x);", "not a number")]
    [InlineData("INSERT INTO `exploration_basexp` VALUES (1,2),(3,4)", "expected ',' or the end")]
    [InlineData("INSERT INTO `exploration_basexp` VALUES (1,2),(1,5);", "duplicate")]
    [InlineData("INSERT INTO `exploration_basexp` VALUES (1,2,3);", "3 values but the table has 2 columns")]
    [InlineData("INSERT INTO `exploration_basexp` VALUES (1,NULL);", "not a number")]
    public void Parse_RejectsMangledRows(string insert, string message)
    {
        string dump = "CREATE TABLE `exploration_basexp` (\n  `level` tinyint NOT NULL,\n  `basexp` mediumint NOT NULL,\n  PRIMARY KEY (`level`)\n) ENGINE=MyISAM;\n" + insert;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WorldStateDumpImporter.Parse(new StringReader(dump)));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsRowsWithoutACreateTable_AndMissingColumns()
    {
        Assert.Contains("no CREATE TABLE", Assert.Throws<InvalidDataException>(
            () => WorldStateDumpImporter.Parse(new StringReader("INSERT INTO `exploration_basexp` VALUES (1,2);"))).Message, StringComparison.Ordinal);
        Assert.Contains("no column 'basexp'", Assert.Throws<InvalidDataException>(
            () => WorldStateDumpImporter.Parse(new StringReader("CREATE TABLE `exploration_basexp` (\n  `level` tinyint NOT NULL,\n  PRIMARY KEY (`level`)\n);\nINSERT INTO `exploration_basexp` VALUES (1);"))).Message, StringComparison.Ordinal);
    }

    [ClassicDbDumpFact]
    public void RealDump_HasThe33WeatherZonesAnd61BaseXpLevels()
    {
        using var file = File.OpenRead(ClassicDbDumpFactAttribute.DumpPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        WorldStateContent content = WorldStateDumpImporter.Parse(reader);

        Assert.Equal(33, content.Weather.Count);
        Assert.Equal(61, content.BaseXp.Count);
        Assert.Equal(660u, content.BaseXp.Single(b => b.Level == 60).BaseXp);
        Assert.Equal([0u, 60u], [content.BaseXp.Min(b => b.Level), content.BaseXp.Max(b => b.Level)]);
        // zone 12 (Elwynn): spring rain 20; zone 1377 (Silithus): spring storm 20
        Assert.Equal(20u, content.Weather.Single(w => w.Zone == 12).Chances[0]);
        Assert.Equal(20u, content.Weather.Single(w => w.Zone == 1377).Chances[2]);
        foreach (uint zone in new[] { 1u, 12u, 1377u, 3429u })
        {
            Assert.Contains(content.Weather, w => w.Zone == zone);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Import_RoundTrips_ThroughTheStore_AndReplacesPreviousRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        WorldStateContent content = WorldStateDumpImporter.Parse(new StringReader(SyntheticDump));
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            Assert.Empty((await new EfWorldStateDataStore(world).LoadAsync()).Weather);
            Assert.Equal((2, 3), await WorldStateDumpImporter.ImportAsync(world, content));
            // a second import replaces, it does not append or collide
            Assert.Equal((2, 3), await WorldStateDumpImporter.ImportAsync(world, content));
        }

        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            WorldStateContent loaded = await new EfWorldStateDataStore(world).LoadAsync();
            Assert.Equal(content.Weather.Select(w => (w.Zone, string.Join(',', w.Chances))), loaded.Weather.Select(w => (w.Zone, string.Join(',', w.Chances))));
            Assert.Equal(content.BaseXp.OrderBy(b => b.Level), loaded.BaseXp);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailedImport_LeavesThePreviousRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
        await WorldStateDumpImporter.ImportAsync(world, WorldStateDumpImporter.Parse(new StringReader(SyntheticDump)));

        var bad = new WorldStateContent([new GameWeatherRecord(5, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 300])], [new ExplorationBaseXpRecord(1, 1)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorldStateDumpImporter.ImportAsync(world, bad));

        // a failure inside the transaction (duplicate keys) rolls the delete back too
        var duplicates = new WorldStateContent([], [new ExplorationBaseXpRecord(7, 1), new ExplorationBaseXpRecord(7, 2)]);
        await Assert.ThrowsAnyAsync<Exception>(() => WorldStateDumpImporter.ImportAsync(world, duplicates));

        WorldStateContent after = await new EfWorldStateDataStore(world).LoadAsync();
        Assert.Equal([1u, 2u], after.Weather.Select(w => w.Zone));
        Assert.Equal(3, after.BaseXp.Count);
    }
}
