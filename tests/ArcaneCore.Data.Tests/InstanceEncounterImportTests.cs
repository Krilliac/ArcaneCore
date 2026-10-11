using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>instance_encounter</c> (<see cref="InstanceEncounterDataModule"/>) and its importer: cmangos classic-db <c>instance_dungeon_encounters</c>
/// (the DungeonEncounter.dbc export) joined on <c>Id = entry</c> to <c>instance_encounters</c>, difficulty 0 only. Rows are hand-written in the
/// classic-db column layout; no GPL rows are copied.
/// </summary>
public sealed class InstanceEncounterImportTests : IAsyncLifetime
{
    private const string Dump = """
        INSERT INTO `instance_dungeon_encounters` (`Id`,`MapId`,`Difficulty`,`EncounterData`,`EncounterIndex`,`CompleteWorldStateID`,`EncounterName`) VALUES
        (9001,990,0,0,1,0,'Second Boss'),
        (9002,990,0,0,0,0,'First Boss'),
        (9003,990,0,0,2,0,'Last Boss'),
        (9004,990,1,0,0,0,'Later difficulty copy'),
        (9005,990,0,0,3,0,'No credit rule'),
        (9006,991,0,0,0,0,'Spell credited');
        INSERT INTO `instance_encounters` (`entry`,`creditType`,`creditEntry`,`lastEncounterDungeon`) VALUES
        (9001,0,5002,0),(9002,0,5001,0),(9003,0,5003,77),(9004,0,5001,0),(9006,1,60001,0);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static InstanceEncounterDumpImporter Read(string dump)
    {
        var importer = new InstanceEncounterDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void WorldStep_IsTheEncounterTable()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == InstanceEncounterDataModule.Version);
        Assert.Equal(["instance_encounter"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= InstanceEncounterDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is InstanceEncounterDataModule);
    }

    [Fact]
    public void Importer_JoinsTheTwoTables_KeepsDifficultyZero_AndDropsEncountersWithoutACreditRule()
    {
        InstanceEncounterDumpImporter importer = Read(Dump);

        Assert.True(importer.SawTable);
        Assert.Equal([9002u, 9001u, 9003u, 9006u], importer.Rows.Select(r => r.EncounterId));
        InstanceEncounterRow last = importer.Rows.Single(r => r.EncounterId == 9003);
        Assert.Equal((990u, 2u, (byte)0, 5003u, true, "Last Boss"), (last.Map, last.EncounterIndex, last.CreditType, last.CreditEntry, last.LastEncounter, last.Name));
        InstanceEncounterRow spell = importer.Rows.Single(r => r.EncounterId == 9006);
        Assert.Equal(((byte)1, 60001u, false), (spell.CreditType, spell.CreditEntry, spell.LastEncounter));
    }

    [Fact]
    public void ADumpWithoutBothTables_ReadsNothing_AndLeavesTheDatabaseAlone()
    {
        InstanceEncounterDumpImporter onlyDungeon = Read("INSERT INTO `instance_dungeon_encounters` (`Id`,`MapId`,`Difficulty`,`EncounterData`,`EncounterIndex`,`CompleteWorldStateID`,`EncounterName`) VALUES (1,2,0,0,0,0,'X');");

        Assert.False(onlyDungeon.SawTable);
        Assert.Empty(onlyDungeon.Rows);
        Assert.False(Read("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);").SawTable);
    }

    [Fact]
    public void ABadNumber_FailsTheImport_InsteadOfBeingSkipped()
    {
        Assert.Throws<ImportSchemaException>(() => Read(
            "INSERT INTO `instance_encounters` (`entry`,`creditType`,`creditEntry`,`lastEncounterDungeon`) VALUES (1,'x',2,0);"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Encounters_RoundTripThroughTheStore_InDungeonOrder_AndAReplaceRemovesThePreviousRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(4, await Read(Dump).ReplaceAsync(db));
        }

        await using (WorldDbContext read = TestContexts.Create<WorldDbContext>(cs))
        {
            IReadOnlyList<InstanceEncounter> encounters = await new EfInstanceEncounterStore(read).LoadAsync();
            Assert.Equal(["First Boss", "Second Boss", "Last Boss", "Spell credited"], encounters.Select(e => e.Name));
            Assert.Equal([990u, 990u, 990u, 991u], encounters.Select(e => e.MapId));
            Assert.True(encounters[0].IsCreatureKill);
            Assert.False(encounters[3].IsCreatureKill);
            Assert.True(encounters[2].LastEncounter);
            Assert.Equal(5001u, encounters[0].CreditEntry);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            // A dump without the tables changes nothing; one with different rows replaces them.
            Assert.Equal(0, await Read("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);").ReplaceAsync(db));
            Assert.Equal(4, await db.Set<InstanceEncounterRow>().CountAsync());
            Assert.Equal(1, await Read("""
                INSERT INTO `instance_dungeon_encounters` (`Id`,`MapId`,`Difficulty`,`EncounterData`,`EncounterIndex`,`CompleteWorldStateID`,`EncounterName`) VALUES (1,2,0,0,0,0,'Only');
                INSERT INTO `instance_encounters` (`entry`,`creditType`,`creditEntry`,`lastEncounterDungeon`) VALUES (1,0,3,1);
                """).ReplaceAsync(db));
        }

        await using WorldDbContext check = TestContexts.Create<WorldDbContext>(cs);
        InstanceEncounterRow only = await check.Set<InstanceEncounterRow>().SingleAsync();
        Assert.Equal("Only", only.Name);
    }

    /// <summary>
    /// With <c>ARCANECORE_CLASSICDB_DUMP</c> set to the classic-db z2815 dump (.sql or .sql.gz) this imports the real encounters (skipped when the
    /// variable is unset; fails when it is set and the file is missing). z2815 lists 612 dungeon encounters (184 of a later difficulty, 188 of
    /// difficulty 0 with no credit rule) and 272 credit rules; 240 join, on 34 maps.
    /// </summary>
    [ClassicDbDumpFact]
    public void RealClassicDbDump_ImportsTheRaidBossesInOrder()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{ClassicDbDumpFactAttribute.Variable} is set but '{path}' does not exist");
        var importer = new InstanceEncounterDumpImporter();
        using TextReader reader = DumpFiles.OpenText(path);
        importer.Read(reader);

        IReadOnlyList<InstanceEncounterRow> rows = importer.Rows;
        Assert.Equal(240, rows.Count);
        Assert.Equal(34, rows.Select(r => r.Map).Distinct().Count());
        Assert.Equal(40, rows.Count(r => r.LastEncounter));

        // Molten Core: ten encounters in order, Ragnaros (11502) last.
        InstanceEncounterRow[] moltenCore = [.. rows.Where(r => r.Map == 409)];
        Assert.Equal(10, moltenCore.Length);
        Assert.Equal(["Lucifron", "Magmadar", "Gehennas", "Garr", "Shazzrah", "Baron Geddon", "Sulfuron Harbinger", "Golemagg the Incinerator", "Majordomo Executus", "Ragnaros"],
            moltenCore.Select(r => r.Name));
        Assert.Equal((11502u, true), (moltenCore[^1].CreditEntry, moltenCore[^1].LastEncounter));

        // Onyxia's Lair appears once (the later-difficulty copy is dropped); the Four Horsemen are credited by a spell, not a kill.
        Assert.Equal(10184u, Assert.Single(rows, r => r.Map == 249).CreditEntry);
        InstanceEncounterRow horsemen = Assert.Single(rows, r => r.Map == 533 && r.Name == "The Four Horsemen");
        Assert.Equal(((byte)1, 59450u), (horsemen.CreditType, horsemen.CreditEntry));
        Assert.Equal(15, rows.Count(r => r.Map == 533));
    }

    private sealed class ClassicDbDumpFactAttribute : FactAttribute
    {
        public const string Variable = "ARCANECORE_CLASSICDB_DUMP";

        public ClassicDbDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Set {Variable} to the classic-db z2815 dump (.sql or .sql.gz) to import the real data.";
            }
        }
    }
}
