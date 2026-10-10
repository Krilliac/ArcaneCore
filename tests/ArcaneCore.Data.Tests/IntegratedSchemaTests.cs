using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Honor;
using ArcaneCore.Data.Creatures;
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
            (typeof(AccountLoginSecurityDataModule), DatabaseComponent.Auth, AccountLoginSecurityDataModule.Version),
            (typeof(ArcaneCore.Data.Auth.Playerbots.ManagedPlayerbotProvisionDataModule), DatabaseComponent.Auth, ArcaneCore.Data.Auth.Playerbots.ManagedPlayerbotProvisionDataModule.Version),
            (typeof(CreatureDataModule), DatabaseComponent.World, 2),
            (typeof(MapDataModule), DatabaseComponent.World, 3),
            (typeof(ItemWorldDataModule), DatabaseComponent.World, 4),
            (typeof(SpellWorldDataModule), DatabaseComponent.World, 5),
            (typeof(QuestNpcWorldModule), DatabaseComponent.World, 6),
            (typeof(GameObjectLootDataModule), DatabaseComponent.World, 7),
            (typeof(CreatureAiDataModule), DatabaseComponent.World, CreatureAiDataModule.Version),
            (typeof(QuestReputationRewardWorldModule), DatabaseComponent.World, QuestReputationRewardWorldModule.Version),
            (typeof(QuestAdvancedWorldModule), DatabaseComponent.World, QuestAdvancedWorldModule.Version),
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
            (typeof(ArcaneCore.Data.World.Creatures.CreatureNpcMetadataDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Creatures.CreatureNpcMetadataDataModule.Version),
            (typeof(ReputationTemplatesWorldModule), DatabaseComponent.World, ReputationTemplatesWorldModule.Version),
            (typeof(CreatureMovementTemplateDataModule), DatabaseComponent.World, CreatureMovementTemplateDataModule.Version),
            (typeof(CreatureSpawnEntryDataModule), DatabaseComponent.World, CreatureSpawnEntryDataModule.Version),
            (typeof(ArcaneCore.Data.World.WorldState.GameEventDataModule), DatabaseComponent.World, ArcaneCore.Data.World.WorldState.GameEventDataModule.Version),
            (typeof(ArcaneCore.Data.World.Threat.SpellThreatDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Threat.SpellThreatDataModule.Version),
            (typeof(ArcaneCore.Data.Graveyards.GraveyardDataModule), DatabaseComponent.World, ArcaneCore.Data.Graveyards.GraveyardDataModule.Version),
            (typeof(AreaTriggerQuestWorldModule), DatabaseComponent.World, AreaTriggerQuestWorldModule.Version),
            (typeof(ArcaneCore.Data.World.Rest.AreaTriggerTavernDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Rest.AreaTriggerTavernDataModule.Version),
            (typeof(GameObjectTemplateGoldDataModule), DatabaseComponent.World, GameObjectTemplateGoldDataModule.Version),
            (typeof(ReservedNameWorldDataModule), DatabaseComponent.World, ReservedNameWorldDataModule.Version),
            (typeof(ItemEnchantmentWorldDataModule), DatabaseComponent.World, ItemEnchantmentWorldDataModule.Version),
            (typeof(CreatureDisplayScaleDataModule), DatabaseComponent.World, CreatureDisplayScaleDataModule.Version),
            (typeof(SpellEnchantChargesWorldDataModule), DatabaseComponent.World, SpellEnchantChargesWorldDataModule.Version),
            (typeof(StartingSkillWorldDataModule), DatabaseComponent.World, StartingSkillWorldDataModule.Version),
            (typeof(CreatureTextTemplateDataModule), DatabaseComponent.World, CreatureTextTemplateDataModule.Version),
            (typeof(ArcaneCore.Data.World.Procs.SpellProcEventDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Procs.SpellProcEventDataModule.Version),
            (typeof(RelayScriptDataModule), DatabaseComponent.World, RelayScriptDataModule.Version),
            (typeof(ArcaneCore.Data.World.Battlegrounds.BattlegroundWorldDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Battlegrounds.BattlegroundWorldDataModule.Version),
            (typeof(ArcaneCore.Data.World.Transports.TransportWorldDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Transports.TransportWorldDataModule.Version),
            (typeof(DbScriptDataModule), DatabaseComponent.World, DbScriptDataModule.Version),
            (typeof(CreatureScriptNameDataModule), DatabaseComponent.World, CreatureScriptNameDataModule.Version),
            (typeof(ArcaneCore.Data.World.SpawnGroups.SpawnGroupDataModule), DatabaseComponent.World, ArcaneCore.Data.World.SpawnGroups.SpawnGroupDataModule.Version),
            (typeof(MovementScriptDataModule), DatabaseComponent.World, MovementScriptDataModule.Version),
            (typeof(ArcaneCore.Data.World.Pools.PoolDataModule), DatabaseComponent.World, ArcaneCore.Data.World.Pools.PoolDataModule.Version),
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
            (typeof(ArcaneCore.Data.Characters.Bank.CharacterBankSlotsDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Bank.CharacterBankSlotsDataModule.Version),
            (typeof(CharacterTaxiFlightDataModule), DatabaseComponent.Characters, CharacterTaxiFlightDataModule.Version),
            (typeof(CharacterHonorDataModule), DatabaseComponent.Characters, CharacterHonorDataModule.Version),
            (typeof(CreatureRespawnDataModule), DatabaseComponent.Characters, CreatureRespawnDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.GameEventStatusDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.GameEventStatusDataModule.Version),
            (typeof(ArcaneCore.Data.Gm.GmAuditDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Gm.GmAuditDataModule.Version),
            (typeof(CharacterRestDataModule), DatabaseComponent.Characters, CharacterRestDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.Rename.CharacterRenameDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Rename.CharacterRenameDataModule.Version),
            (typeof(PersistentPetDataModule), DatabaseComponent.Characters, PersistentPetDataModule.Version),
            (typeof(ItemCooldownOwnerDataModule), DatabaseComponent.Characters, ItemCooldownOwnerDataModule.Version),
            (typeof(PetCooldownDataModule), DatabaseComponent.Characters, PetCooldownDataModule.Version),
            (typeof(PetNamingDataModule), DatabaseComponent.Characters, PetNamingDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.Playerbots.ManagedPlayerbotDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Playerbots.ManagedPlayerbotDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.Life.CharacterCorpseInstanceDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Life.CharacterCorpseInstanceDataModule.Version),
            (typeof(GroupDataModule), DatabaseComponent.Characters, GroupDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.Accounts.AccountAddressDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Accounts.AccountAddressDataModule.Version),
            (typeof(ItemGiftDataModule), DatabaseComponent.Characters, ItemGiftDataModule.Version),
            (typeof(GroupInstanceBindDataModule), DatabaseComponent.Characters, GroupInstanceBindDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.Battlegrounds.CharacterBattlegroundDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Battlegrounds.CharacterBattlegroundDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.Transports.CharacterTransportDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.Transports.CharacterTransportDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.AntiCheat.AntiCheatDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.AntiCheat.AntiCheatDataModule.Version),
            (typeof(InstanceScriptDataModule), DatabaseComponent.Characters, InstanceScriptDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.WarEffortDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.WarEffortDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.WarEffortBossDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.WarEffortBossDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.ScourgeInvasionDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.ScourgeInvasionDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.WarEffortGongDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.WarEffortGongDataModule.Version),
            (typeof(ArcaneCore.Data.Characters.WorldState.ScourgeInvasionCityDataModule), DatabaseComponent.Characters, ArcaneCore.Data.Characters.WorldState.ScourgeInvasionCityDataModule.Version),
        ];

        Assert.Equal(expected.OrderBy(m => m.Component).ThenBy(m => m.Version),
            DataModules.All.OrderBy(m => m.Component).ThenBy(m => m.SchemaVersion)
                .Select(m => (m.GetType(), m.Component, m.SchemaVersion)));
        // The forward index repair is the top step of characters and world (the constants are what an integrator renumbers).
        Assert.Equal(DataModules.For(DatabaseComponent.Auth).Max(m => m.SchemaVersion), AuthDbContext.Schema.CurrentVersion);
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

        // Wave 2 (docs/integration/wave2-20261007.md): every step is real. No placeholder is registered, and the lanes' modules
        // were renumbered down so the plan's unclaimed numbers left no gap: world 38-41, characters 35-40. The wave-6 anticheat lane
        // is characters 41 and the wave-7 instance-persist lane characters 42 (its v41 placeholder was removed at integration).
        // Realm-wide AQ war-effort state is characters 43.
        // World 42 is the wave-7 quest-scripts lane's DB script step (DbScriptDataModule); world 43 the script-engine lane's
        // CreatureScriptNameDataModule; world 44 the spawn-groups lane's SpawnGroupDataModule and world 45 the movement-scripts lane's MovementScriptDataModule (both allocated as 43, renumbered in wave 10); world 46 the pools lane's PoolDataModule (its v45 placeholder was removed at integration).
        Assert.DoesNotContain(DataModules.All, m => m is IReservedSchemaGap);
        Assert.Empty(CharacterDbContext.Schema.ReservedGapVersions);
        Assert.Empty(WorldDbContext.Schema.ReservedGapVersions);
        Assert.Equal(46, WorldDbContext.Schema.CurrentVersion); // creature_template.ScriptName (43), spawn groups (44), movement scripts (45), pools (46)
        Assert.Equal(47, CharacterDbContext.Schema.CurrentVersion); // anticheat (41), instance script data (42), AQ state (43-44), Scourge (45), AQ gong (46), Scourge city attacks (47)
        Assert.Equal(5, AuthDbContext.Schema.CurrentVersion); // realm PIN and integrity (5)

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
            // Query only v1 columns while later steps have not added every current-model column.
            Assert.Equal(("Existing", 123u), await characters.Characters
                .Select(c => new ValueTuple<string, uint>(c.Name, c.PlayedTime)).SingleAsync());
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

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorldDatabaseBeforeTheAreaTriggerStep_GainsTheRequirementColumnsAndTheRelationTable_AndKeepsRows(DatabaseProvider provider)
    {
        // A database that predates the step: a complete schema whose areatrigger_teleport loses the four requirement columns, whose
        // areatrigger_involvedrelation table does not exist and whose version row reads one step earlier.
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        string[] columns = [nameof(AreaTriggerTeleportRow.RequiredItem), nameof(AreaTriggerTeleportRow.RequiredItem2),
            nameof(AreaTriggerTeleportRow.RequiredQuestDone), nameof(AreaTriggerTeleportRow.RequiredCondition)];
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            world.Set<AreaTriggerTeleportRow>().Add(new AreaTriggerTeleportRow { Id = 78, Name = "Deadmines - Entering", RequiredLevel = 10, TargetMap = 36 });
            await world.SaveChangesAsync();
            ISqlGenerationHelper sql = world.GetService<ISqlGenerationHelper>();
            foreach (string column in columns)
            {
                string drop = $"ALTER TABLE {sql.DelimitIdentifier(MapDataModule.AreaTriggerTeleportTable)} DROP COLUMN {sql.DelimitIdentifier(column)}";
                await world.Database.ExecuteSqlRawAsync(drop);
            }

            string dropTable = $"DROP TABLE {sql.DelimitIdentifier(AreaTriggerQuestWorldModule.RelationTable)}";
            await world.Database.ExecuteSqlRawAsync(dropTable);
            SchemaVersionRow row = await world.Set<SchemaVersionRow>().SingleAsync();
            row.Version = AreaTriggerQuestWorldModule.Version - 1;
            await world.SaveChangesAsync();
            world.ChangeTracker.Clear();
        }

        // Two startups: the step runs once and a repeat has nothing left to add.
        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
            await EnsureAndInspectAsync(world, WorldDbContext.Schema);
            AreaTriggerTeleportRow kept = await world.Set<AreaTriggerTeleportRow>().SingleAsync(r => r.Id == 78);
            Assert.Equal((10, 36u), (kept.RequiredLevel, kept.TargetMap));
            Assert.Equal((0u, 0u, 0u, 0u), (kept.RequiredItem, kept.RequiredItem2, kept.RequiredQuestDone, kept.RequiredCondition));
            Assert.Empty(await world.Set<AreaTriggerQuestRow>().ToListAsync());
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
