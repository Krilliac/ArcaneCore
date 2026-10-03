using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Loot;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Skills;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.PlayerStats;
using ArcaneCore.Data.World.Pets;
using ArcaneCore.Data.World.SpecialLoot;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The integrated fleet's schema allocation, fresh shared-database bootstrap, and every
/// additive feature upgrade. The existing engine matrix runs SQLite locally and MariaDB
/// and PostgreSQL when their test connection strings are available.
/// </summary>
public sealed class IntegratedSchemaTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void FeatureModules_HaveAssignedVersions_AndDistinctTables()
    {
        (Type Type, DatabaseComponent Component, int Version)[] expected =
        [
            (typeof(BanDataModule), DatabaseComponent.Auth, BanDataModule.Version),
            (typeof(CreatureDataModule), DatabaseComponent.World, 2),
            (typeof(MapDataModule), DatabaseComponent.World, 3),
            (typeof(ItemWorldDataModule), DatabaseComponent.World, 4),
            (typeof(SpellWorldDataModule), DatabaseComponent.World, 5),
            (typeof(QuestNpcWorldModule), DatabaseComponent.World, 6),
            (typeof(GameObjectLootDataModule), DatabaseComponent.World, 7),
            (typeof(CreatureAiDataModule), DatabaseComponent.World, CreatureAiDataModule.Version),
            (typeof(QuestReputationRewardWorldModule), DatabaseComponent.World, QuestReputationRewardWorldModule.Version),
            (typeof(PlayerStatsDataModule), DatabaseComponent.World, PlayerStatsDataModule.Version),
            (typeof(CreatureBehaviourDataModule), DatabaseComponent.World, CreatureBehaviourDataModule.Version),
            (typeof(ConditionsWorldModule), DatabaseComponent.World, ConditionsWorldModule.Version),
            (typeof(CreatureOnKillReputationWorldModule), DatabaseComponent.World, CreatureOnKillReputationWorldModule.Version),
            (typeof(ArcaneCore.Data.World.Totems.TotemWorldDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Totems.TotemWorldDataModule.Version),
            (typeof(PetWorldDataModule), DatabaseComponent.World, PetWorldDataModule.Version),
            (typeof(ArcaneCore.Data.World.WorldState.WorldStateDataModule), DatabaseComponent.World, ArcaneCore.Data.World.WorldState.WorldStateDataModule.Version),
            (typeof(GameObjectSpawnDataModule), DatabaseComponent.World, GameObjectSpawnDataModule.Version),
            (typeof(SpecialLootDataModule), DatabaseComponent.World, SpecialLootDataModule.Version),
            (typeof(StartActionWorldModule), DatabaseComponent.World, StartActionWorldModule.Version),
            (typeof(ArcaneCore.Data.World.Threat.SpellThreatDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Threat.SpellThreatDataModule.Version),
            (typeof(ItemCharacterDataModule), DatabaseComponent.Characters, 3),
            (typeof(CharacterSpellDataModule), DatabaseComponent.Characters, 4),
            (typeof(QuestNpcCharactersModule), DatabaseComponent.Characters, 5),
            (typeof(SocialDataModule), DatabaseComponent.Characters, 6),
            (typeof(CharacterReputationDataModule), DatabaseComponent.Characters, 7),
            (typeof(InstanceDataModule), DatabaseComponent.Characters, 8),
            (typeof(CharacterSpellStateDataModule), DatabaseComponent.Characters, 9),
            (typeof(EconomyDataModule), DatabaseComponent.Characters, EconomyDataModule.Version),
            (typeof(CharacterDeletionDataModule), DatabaseComponent.Characters, CharacterDeletionDataModule.Version),
            (typeof(LootStateDataModule), DatabaseComponent.Characters, LootStateDataModule.Version),
            (typeof(CharacterSkillsDataModule), DatabaseComponent.Characters, CharacterSkillsDataModule.Version),
            (typeof(CharacterLifeDataModule), DatabaseComponent.Characters, CharacterLifeDataModule.Version),
            (typeof(CharacterItemStateDataModule), DatabaseComponent.Characters, CharacterItemStateDataModule.Version),
            (typeof(CharacterTalentDataModule), DatabaseComponent.Characters, CharacterTalentDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.ExploredZonesDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.ExploredZonesDataModule.Version),
            (typeof(ItemLootDataModule), DatabaseComponent.Characters, ItemLootDataModule.Version),
            (typeof(PetitionDataModule), DatabaseComponent.Characters, PetitionDataModule.Version),
        ];

        Assert.Equal(expected.OrderBy(m => m.Component).ThenBy(m => m.Version),
            DataModules.All.OrderBy(m => m.Component).ThenBy(m => m.SchemaVersion)
                .Select(m => (m.GetType(), m.Component, m.SchemaVersion)));
        // The forward index repair is the top step of characters and world (the constants are what an integrator renumbers).
        Assert.Equal(BanDataModule.Version, AuthDbContext.Schema.CurrentVersion);
        // The current version is the highest of the modules and the inline repair; versions are contiguous (Compose throws on gaps).
        Assert.Equal(
            Math.Max(CharacterDbContext.IndexRepairVersion, DataModules.For(DatabaseComponent.Characters).Max(m => m.SchemaVersion)),
            CharacterDbContext.Schema.CurrentVersion);
        Assert.Equal(
            Math.Max(WorldDbContext.IndexRepairVersion, DataModules.For(DatabaseComponent.World).Max(m => m.SchemaVersion)),
            WorldDbContext.Schema.CurrentVersion);
        Assert.Equal(Enumerable.Range(2, CharacterDbContext.Schema.CurrentVersion - 1), CharacterDbContext.Schema.Steps.Select(s => s.Version));
        Assert.Equal(Enumerable.Range(2, WorldDbContext.Schema.CurrentVersion - 1), WorldDbContext.Schema.Steps.Select(s => s.Version));
        Assert.DoesNotContain(CharacterDbContext.IndexRepairVersion, DataModules.For(DatabaseComponent.Characters).Select(m => m.SchemaVersion));
        Assert.DoesNotContain(WorldDbContext.IndexRepairVersion, DataModules.For(DatabaseComponent.World).Select(m => m.SchemaVersion));

        foreach (DatabaseComponent component in new[] { DatabaseComponent.Characters, DatabaseComponent.World })
        {
            string[] tables = [.. DataModules.For(component).SelectMany(m => m.SchemaChanges)
                .OfType<CreateTableChange>().Select(c => c.Table)];
            Assert.Equal(tables.Length, tables.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshSharedDatabase_CreatesEveryFeatureTable_AndSecondBootstrapIsIdempotent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        for (int pass = 0; pass < 2; pass++)
        {
            await using AuthDbContext auth = TestContexts.Create<AuthDbContext>(connection);
            await using CharacterDbContext characters = TestContexts.Create<CharacterDbContext>(connection);
            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
            await EnsureAndInspectAsync(auth, AuthDbContext.Schema);
            await EnsureAndInspectAsync(characters, CharacterDbContext.Schema);
            await EnsureAndInspectAsync(world, WorldDbContext.Schema);
            Assert.Empty(await characters.Characters.ToListAsync());
            Assert.Empty(await world.ClassInfo.ToListAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegacySchemas_UpgradeEachFeatureStep_KeepRows_AndAllowRepeatedStartup(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
            legacy.Characters.Add(new CharacterV1Row { AccountId = 7, Name = "Existing", PlayedTime = 123 });
            await legacy.SaveChangesAsync();
        }

        await using (MapWorldV1Context legacy = TestContexts.Create<MapWorldV1Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, MapWorldV1Context.Schema);
            legacy.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await legacy.SaveChangesAsync();
        }

        await using CharacterDbContext characters = TestContexts.Create<CharacterDbContext>(connection);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
        foreach (SchemaStep step in CharacterDbContext.Schema.Steps)
        {
            // The database starts with the historical v1 model. Only this step's DDL runs;
            // using the current context merely supplies the exact provider model for it.
            SchemaDefinition prefix = ThroughVersion(CharacterDbContext.Schema, step.Version);
            await EnsureAndInspectAsync(characters, prefix, CharacterDbContext.Schema);
            await EnsureAndInspectAsync(characters, prefix, CharacterDbContext.Schema);
            Assert.Equal("Existing", (await characters.Characters.SingleAsync()).Name);
            Assert.Equal(123u, (await characters.Characters.SingleAsync()).PlayedTime);
        }

        foreach (SchemaStep step in WorldDbContext.Schema.Steps)
        {
            SchemaDefinition prefix = ThroughVersion(WorldDbContext.Schema, step.Version);
            await EnsureAndInspectAsync(world, prefix, WorldDbContext.Schema);
            await EnsureAndInspectAsync(world, prefix, WorldDbContext.Schema);
            Assert.Equal(60u, (await world.ClassInfo.SingleAsync()).BaseHealth);
        }

        await EnsureAndInspectAsync(characters, CharacterDbContext.Schema);
        await EnsureAndInspectAsync(world, WorldDbContext.Schema);
        Assert.Equal(0u, (await characters.Characters.SingleAsync()).Money);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorldDatabaseBeforeTheQuestReputationStep_UpgradesWithZeroDefaultsAndKeepsRows(DatabaseProvider provider)
    {
        // The v6 step creates quest_template from the current model, so the upgrade chain above already
        // has the columns. A database that predates them is rebuilt here: a complete schema whose
        // quest_template loses the ten columns and whose version row reads one step earlier.
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int previous = QuestReputationRewardWorldModule.Version - 1;
        string[] columns =
        [
            .. Enumerable.Range(1, 5).Select(i => $"RewRepFaction{i}"),
            .. Enumerable.Range(1, 5).Select(i => $"RewRepValue{i}"),
        ];
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            world.Set<ArcaneCore.Kernel.Quests.QuestTemplate>().Add(new ArcaneCore.Kernel.Quests.QuestTemplate { Entry = 4242, Method = 2 });
            await world.SaveChangesAsync();
            ISqlGenerationHelper sql = world.GetService<ISqlGenerationHelper>();
            foreach (string column in columns)
            {
                string statement = $"ALTER TABLE {sql.DelimitIdentifier("quest_template")} DROP COLUMN {sql.DelimitIdentifier(column)}";
                await world.Database.ExecuteSqlRawAsync(statement);
            }

            SchemaVersionRow row = await world.Set<SchemaVersionRow>().SingleAsync();
            row.Version = previous;
            await world.SaveChangesAsync();
            world.ChangeTracker.Clear();
        }

        // Two startups: the step runs once and a repeat has nothing left to add.
        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
            await EnsureAndInspectAsync(world, WorldDbContext.Schema);
            ArcaneCore.Kernel.Quests.QuestTemplate quest = await world.Set<ArcaneCore.Kernel.Quests.QuestTemplate>().SingleAsync(q => q.Entry == 4242);
            Assert.Equal((0u, 0u, 0u, 0u, 0u), (quest.RewRepFaction1, quest.RewRepFaction2, quest.RewRepFaction3, quest.RewRepFaction4, quest.RewRepFaction5));
            Assert.Equal((0, 0, 0, 0, 0), (quest.RewRepValue1, quest.RewRepValue2, quest.RewRepValue3, quest.RewRepValue4, quest.RewRepValue5));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static SchemaDefinition ThroughVersion(SchemaDefinition schema, int version) => new()
    {
        Component = schema.Component,
        CurrentVersion = version,
        Version1Tables = schema.Version1Tables,
        Steps = [.. schema.Steps.Where(s => s.Version <= version)],
    };

    /// <summary>
    /// Bootstrap <paramref name="schema"/> and read every model column of its tables. For a
    /// prefix of <paramref name="full"/>, columns that later steps add are not there yet.
    /// </summary>
    private static async Task EnsureAndInspectAsync(DbContext db, SchemaDefinition schema, SchemaDefinition? full = null)
    {
        HashSet<(string Table, string Column)> notYetAdded = [.. (full?.Steps ?? []).Where(s => s.Version > schema.CurrentVersion)
            .SelectMany(s => s.Changes).OfType<AddColumnChange>().Select(c => (c.Table, c.Column))];
        await SchemaBootstrapper.EnsureAsync(db, schema);
        Assert.Equal(schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);

        string[] expectedTables = [schema.VersionTable, .. schema.Version1Tables,
            .. schema.Steps.SelectMany(s => s.Changes).OfType<CreateTableChange>().Select(c => c.Table)];
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        await db.Database.OpenConnectionAsync();
        try
        {
            // Upgrades must leave the indexes a fresh database has (they once dropped every index of a created table).
            string[] tablesHere = [.. expectedTables.Distinct(StringComparer.Ordinal)];
            Assert.Equal(
                SchemaProbe.ModelIndexes(db).Where(i => tablesHere.Contains(i.Table, StringComparer.Ordinal)),
                await SchemaProbe.ActualIndexesAsync(db, tablesHere.Where(t => t != schema.VersionTable)));

            foreach (string name in expectedTables.Distinct(StringComparer.Ordinal))
            {
                ITable table = Assert.Single(model.Tables, t => t.Name == name);
                IColumn[] present = [.. table.Columns.Where(c => !notYetAdded.Contains((table.Name, c.Name)))];
                string columns = string.Join(", ", present.Select(c => sql.DelimitIdentifier(c.Name)));
                await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = $"SELECT {columns} FROM {sql.DelimitIdentifier(table.Name, table.Schema)} WHERE 1 = 0";
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                Assert.Equal(present.Length, reader.FieldCount);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
