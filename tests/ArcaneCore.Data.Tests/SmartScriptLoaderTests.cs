using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The smart-script load path of the creature data store (smartai-2): <c>smart_scripts.ConditionId</c> (world schema step
/// <see cref="SmartScriptConditionDataModule"/>) round-tripped, and the rows whose creature or game object template or spawn, area trigger or
/// <c>conditions</c> row does not exist refused with a reason (AzerothCore SmartScriptMgr.cpp:141-210 checks the same existence). The provider
/// theories run on every provider <see cref="TestDatabases.AvailableProviders"/> offers (SQLite here; MariaDB and PostgreSQL on hosted CI).
/// </summary>
public sealed class SmartScriptLoaderTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static SmartScriptDbRow Db(int entryOrGuid, byte source, ushort id, byte ev, byte action, byte target, uint condition = 0, uint a1 = 1)
        => new()
        {
            EntryOrGuid = entryOrGuid, SourceType = source, Id = id, EventType = ev, ActionType = action, ActionParam1 = a1,
            TargetType = target, ConditionId = condition, Comment = $"{entryOrGuid}/{source}/{id}",
        };

    [Fact]
    public void WorldStep_IsTheConditionIdColumn()
    {
        Assert.Equal(51, SmartScriptConditionDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == SmartScriptConditionDataModule.Version);
        AddColumnChange change = Assert.IsType<AddColumnChange>(Assert.Single(step.Changes));
        Assert.Equal((SmartScriptDataModule.Table, nameof(SmartScriptDbRow.ConditionId)), (change.Table, change.Column));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= SmartScriptConditionDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_RoundTripsConditionId_AndRejectsRowsWhoseReferencesAreMissing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

            // What exists: creature template 1 with spawn 11, game object template 10 with spawn 20, area trigger 30 and condition 5.
            db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1, Name = "Smart Wolf" });
            db.Set<CreatureSpawnRow>().Add(new CreatureSpawnRow { Guid = 11, Entry = 1, MapId = 0 });
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 10, Name = "Smart Door" });
            db.Set<GameObjectSpawnRow>().Add(new GameObjectSpawnRow { Guid = 20, Entry = 10, MapId = 0 });
            db.Set<AreaTriggerTemplateRow>().Add(new AreaTriggerTemplateRow { Id = 30, Name = "Smart Trigger" });
            db.Set<ConditionRow>().Add(new ConditionRow { ConditionEntry = 5, Type = 36 });

            db.Set<SmartScriptDbRow>().AddRange(
                // Accepted: SMART_EVENT_AGGRO(4) / UPDATE(60) / AREATRIGGER_ONTRIGGER(46) / any event of a list, SMART_ACTION_SET_EVENT_PHASE(22) / TALK(1), self(1) / invoker(7).
                Db(1, 0, 0, 4, 22, 1, condition: 5),
                Db(-11, 0, 0, 4, 22, 1),
                Db(10, 1, 0, 60, 22, 1),
                Db(-20, 1, 0, 60, 22, 1),
                Db(30, 2, 0, 46, 1, 7, a1: 9001),
                Db(40, 9, 0, 0, 22, 1, condition: 5),
                // Refused: the template, the spawn, the trigger or the condition it names does not exist.
                Db(2, 0, 0, 4, 22, 1),
                Db(-12, 0, 0, 4, 22, 1),
                Db(11, 1, 0, 60, 22, 1),
                Db(-21, 1, 0, 60, 22, 1),
                Db(31, 2, 0, 46, 1, 7, a1: 9001),
                Db(1, 0, 1, 4, 22, 1, condition: 6));
            await db.SaveChangesAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            SmartScriptCatalog smart = content.Ai.SmartScripts;

            Assert.Equal((2, 2, 1, 1), (smart.Count, smart.GameObjectRowCount, smart.AreaTriggerRowCount, smart.TimedActionListRowCount));

            // ConditionId reaches the engine row, and a row without one reads 0.
            Assert.Equal(5u, Assert.Single(smart.For(1, 0)).ConditionId);
            Assert.Equal(0u, Assert.Single(smart.For(1, 11)).ConditionId);
            Assert.Equal(5u, Assert.Single(smart.TimedActionList(40)).ConditionId);
            Assert.Single(smart.ForGameObject(10, 0));
            Assert.Single(smart.ForAreaTrigger(30));

            string[] fragments =
            [
                "creature template 2 does not exist", "creature spawn 12 does not exist", "gameobject template 11 does not exist",
                "gameobject spawn 21 does not exist", "area trigger 31 does not exist", "condition 6 is not in the conditions table",
            ];
            Assert.Equal(fragments.Length, smart.Rejected.Count);
            foreach (string fragment in fragments)
            {
                Assert.Single(smart.Rejected, r => r.Reason.Contains(fragment, StringComparison.Ordinal));
            }

            Assert.Empty(smart.For(2, 0));
            Assert.Empty(smart.ForGameObject(11, 0));
            Assert.Empty(smart.ForAreaTrigger(31));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmptyTable_LoadsAnEmptyCatalog_WithoutReadingTheReferenceTables(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
        Assert.Equal((0, 0, 0, 0), (content.Ai.SmartScripts.Count, content.Ai.SmartScripts.GameObjectRowCount, content.Ai.SmartScripts.AreaTriggerRowCount,
            content.Ai.SmartScripts.Rejected.Count));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsTheConditionIdColumn_KeepingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1, Name = "Before" });
            db.Set<SmartScriptDbRow>().Add(Db(1, 0, 0, 4, 22, 1, condition: 7));
            await db.SaveChangesAsync();

            // Recreate a database from before this step: no ConditionId column, version below it. Fixed identifiers only (no input reaches this DDL).
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string dropColumn = $"ALTER TABLE {sql.DelimitIdentifier(SmartScriptDataModule.Table)} DROP COLUMN {sql.DelimitIdentifier(nameof(SmartScriptDbRow.ConditionId))}";
            await db.Database.ExecuteSqlRawAsync(dropColumn);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, SmartScriptConditionDataModule.Version - 1));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

            // The row is kept and reads as "no condition" (the column's default), so an old row runs exactly as before.
            SmartScriptDbRow row = await db.Set<SmartScriptDbRow>().AsNoTracking().SingleAsync();
            Assert.Equal((1, (byte)0, (ushort)0, (byte)4, (byte)22, 0u), (row.EntryOrGuid, row.SourceType, row.Id, row.EventType, row.ActionType, row.ConditionId));

            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Equal(0u, Assert.Single(content.Ai.SmartScripts.For(1, 0)).ConditionId);
        }
    }
}
