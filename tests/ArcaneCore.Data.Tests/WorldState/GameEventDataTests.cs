using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>
/// The game-event world tables (<see cref="GameEventDataModule"/>) on the SQLite / MariaDB / PostgreSQL matrix. MariaDB and
/// PostgreSQL run only where their test connection strings exist (hosted CI); the local box ran SQLite. Semantics the
/// theories are written against: MariaDB DDL is not transactional and commits implicitly, so a schema step must be
/// resumable; PostgreSQL DDL is transactional and folds unquoted identifiers to lower case, so every name is lower snake case.
/// </summary>
public sealed class GameEventDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static GameEventContent Sample() => new(
        [
            new GameEventRecord(1, 1, 525600, 20160, 341, 0, "Midsummer Fire Festival"),
            new GameEventRecord(24, 0, 0, 0, 0, 12, "linked, serverside", null, null, false, false, 0, 10),
            new GameEventRecord(400, 1, 1440, 780, 0, 0, "DayTime 7AM to 8PM", "2006-01-01 07:00:00", "2035-12-30 20:00:00", true, true, 3, 9),
        ],
        [new GameEventTimeRecord(1, "2020-06-21 20:00:00", "2030-12-31 22:59:59")],
        [new GameEventSpawnRecord(100, 1), new GameEventSpawnRecord(101, -27), new GameEventSpawnRecord(100, -123)],
        [new GameEventSpawnRecord(5000, 1), new GameEventSpawnRecord(5001, -123)],
        [new GameEventCreatureDataRecord(100, 1, 5, 6, 7, 8, 9)],
        [new GameEventQuestRecord(7001, 2), new GameEventQuestRecord(7001, 12)],
        [new GameEventMailRecord(-17, 255, 0, 171, 16285), new GameEventMailRecord(17, 1, 8000, 1, 2)]);

    private static async Task SaveAsync(WorldDbContext db, GameEventContent c)
    {
        db.AddRange(c.Events.Select(r => new GameEventRow
        {
            Entry = r.Entry, ScheduleType = r.ScheduleType, Occurence = r.OccurenceMinutes, Length = r.LengthMinutes, Holiday = r.Holiday,
            LinkedTo = r.LinkedTo, Description = r.Description, StartTime = r.StartTime, EndTime = r.EndTime, Hardcoded = r.Hardcoded,
            Disabled = r.Disabled, PatchMin = r.PatchMin, PatchMax = r.PatchMax,
        }));
        db.AddRange(c.Times.Select(r => new GameEventTimeRow { Entry = r.Entry, StartTime = r.StartTime, EndTime = r.EndTime }));
        db.AddRange(c.Creatures.Select(r => new GameEventCreatureRow { Guid = r.Guid, Event = r.Event }));
        db.AddRange(c.GameObjects.Select(r => new GameEventGameObjectRow { Guid = r.Guid, Event = r.Event }));
        db.AddRange(c.CreatureData.Select(r => new GameEventCreatureDataRow
        {
            Guid = r.Guid, Event = r.Event, EntryId = r.EntryId, ModelId = r.ModelId, EquipmentId = r.EquipmentId, SpellStart = r.SpellStart, SpellEnd = r.SpellEnd,
        }));
        db.AddRange(c.Quests.Select(r => new GameEventQuestRow { Quest = r.Quest, Event = r.Event }));
        db.AddRange(c.Mails.Select(r => new GameEventMailRow { Event = r.Event, RaceMask = r.RaceMask, Quest = r.Quest, MailTemplateId = r.MailTemplateId, SenderEntry = r.SenderEntry }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static SchemaDefinition ThroughVersion(SchemaDefinition schema, int version) => new()
    {
        Component = schema.Component,
        CurrentVersion = version,
        Version1Tables = schema.Version1Tables,
        Steps = [.. schema.Steps.Where(s => s.Version <= version)],
    };

    [Fact]
    public void Module_IsDiscovered_AtItsConstant_WithSevenTables()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is GameEventDataModule);
        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal(GameEventDataModule.Version, module.SchemaVersion);
        Assert.Contains(WorldDbContext.Schema.Steps, s => s.Version == GameEventDataModule.Version);
        Assert.Equal(
            ["game_event", "game_event_time", "game_event_creature", "game_event_gameobject", "game_event_creature_data", "game_event_quest", "game_event_mail"],
            module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
    }

    [Fact]
    public void EveryTableAndColumnName_IsLowerSnakeCase()
    {
        using var db = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite("Data Source=:memory:").Options);
        foreach (Type type in new[]
        {
            typeof(GameEventRow), typeof(GameEventTimeRow), typeof(GameEventCreatureRow), typeof(GameEventGameObjectRow),
            typeof(GameEventCreatureDataRow), typeof(GameEventQuestRow), typeof(GameEventMailRow),
        })
        {
            Microsoft.EntityFrameworkCore.Metadata.IEntityType entity = db.Model.FindEntityType(type)!;
            Assert.Matches("^[a-z_]+$", entity.GetTableName()!);
            foreach (Microsoft.EntityFrameworkCore.Metadata.IProperty property in entity.GetProperties())
            {
                Assert.Matches("^[a-z_0-9]+$", property.GetColumnName());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EveryTable_RoundTrips_IncludingSignedEventsAndDateText(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        GameEventContent sample = Sample();
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            Assert.Equal(GameEventContent.Empty.Events, (await new EfGameEventDataStore(world).LoadAsync()).Events);
            await SaveAsync(world, sample);
        }

        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            GameEventContent loaded = await new EfGameEventDataStore(world).LoadAsync();
            Assert.Equal(sample.Events, loaded.Events);
            Assert.Equal(sample.Times, loaded.Times);
            // ordered by event then guid: the negative events come first
            Assert.Equal([(100u, -123), (101u, -27), (100u, 1)], loaded.Creatures.Select(c => (c.Guid, c.Event)));
            Assert.Equal([(5001u, -123), (5000u, 1)], loaded.GameObjects.Select(c => (c.Guid, c.Event)));
            Assert.Equal(sample.CreatureData, loaded.CreatureData);
            Assert.Equal(sample.Quests, loaded.Quests);
            Assert.Equal([-17, 17], loaded.Mails.Select(m => m.Event));
            Assert.Equal(sample.Mails.OrderBy(m => m.Event), loaded.Mails);
            Assert.Equal("2035-12-30 20:00:00", loaded.Events.Single(e => e.Entry == 400).EndTime);
            Assert.True(loaded.Events.Single(e => e.Entry == 400).Hardcoded);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SetDisabled_WritesTheFlag_OfThatEventOnly_AndIgnoresAnUnknownEvent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
        await SaveAsync(world, Sample());
        var store = new EfGameEventDataStore(world);

        await store.SetDisabledAsync(1, true);
        await store.SetDisabledAsync(9999, true); // no such event: nothing changes, nothing throws

        GameEventContent loaded = await store.LoadAsync();
        Assert.True(loaded.Events.Single(e => e.Entry == 1).Disabled);
        Assert.True(loaded.Events.Single(e => e.Entry == 400).Disabled);  // the sample had it disabled already
        Assert.False(loaded.Events.Single(e => e.Entry == 24).Disabled);
        Assert.Equal(3, loaded.Events.Count);

        await store.SetDisabledAsync(1, false);
        Assert.False((await store.LoadAsync()).Events.Single(e => e.Entry == 1).Disabled);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicateKeys_AreRefused_ByThePrimaryKeys(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
        await SaveAsync(world, Sample());

        // (guid, event) is the key of the spawn tables; (event, race_mask, quest) of the mails
        world.Add(new GameEventCreatureRow { Guid = 100, Event = 1 });
        await Assert.ThrowsAnyAsync<Exception>(() => world.SaveChangesAsync());
        world.ChangeTracker.Clear();
        world.Add(new GameEventMailRow { Event = -17, RaceMask = 255, Quest = 0 });
        await Assert.ThrowsAnyAsync<Exception>(() => world.SaveChangesAsync());
        world.ChangeTracker.Clear();

        // the same guid under a different event, and the same event under a different race mask, are different rows
        world.Add(new GameEventCreatureRow { Guid = 100, Event = 2 });
        world.Add(new GameEventMailRow { Event = -17, RaceMask = 1, Quest = 0 });
        await world.SaveChangesAsync();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromThePreviousVersion_KeepsRows_AndAHalfFinishedStepIsResumed(DatabaseProvider provider)
    {
        // MariaDB DDL commits implicitly: a start that died after creating some of the module's tables leaves exactly this.
        // Here: the database is at the previous version with game_event and game_event_time already created (and a row in one).
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        SchemaDefinition previous = ThroughVersion(WorldDbContext.Schema, GameEventDataModule.Version - 1);
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(world, previous);
            world.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await world.SaveChangesAsync();
            world.ChangeTracker.Clear();

            var partial = new SchemaDefinition
            {
                Component = previous.Component,
                CurrentVersion = GameEventDataModule.Version,
                Version1Tables = previous.Version1Tables,
                Steps = [.. previous.Steps, new SchemaStep(GameEventDataModule.Version, [new CreateTableChange(GameEventDataModule.EventTable), new CreateTableChange(GameEventDataModule.TimeTable)])],
            };
            await SchemaBootstrapper.EnsureAsync(world, partial);
            world.Set<GameEventRow>().Add(new GameEventRow { Entry = 9, Occurence = 10, Length = 5, Description = "kept" });
            await world.SaveChangesAsync();
            world.ChangeTracker.Clear();
            SchemaVersionRow version = await world.Set<SchemaVersionRow>().SingleAsync();
            version.Version = GameEventDataModule.Version - 1; // the version row is written after the step: the crash left it behind
            await world.SaveChangesAsync();
            world.ChangeTracker.Clear();
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(world, ThroughVersion(WorldDbContext.Schema, GameEventDataModule.Version));
            Assert.Equal(GameEventDataModule.Version, (await world.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            GameEventContent content = await new EfGameEventDataStore(world).LoadAsync(); // every table exists
            Assert.Equal("kept", Assert.Single(content.Events).Description);
            Assert.Equal(60u, (await world.ClassInfo.SingleAsync()).BaseHealth);
        }
    }
}
