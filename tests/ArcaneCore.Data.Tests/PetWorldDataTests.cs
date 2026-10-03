using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The pet world-schema step (<see cref="PetWorldDataModule"/>): <c>pet_levelstats</c> and
/// <c>petcreateinfo_spell</c>, their store on every provider and the vmangos level rules of
/// <see cref="PetContent"/> (ObjectMgr.cpp:4406-4516). Rows are invented test data, not dump rows.
/// </summary>
public sealed class PetWorldDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static PetLevelStats Row(uint entry, byte level, uint health, float min = 0, float max = 0)
        => new(entry, level, health, Mana: level * 10u, Armor: level, min, max, Strength: 1, Agility: 2, Stamina: 3, Intellect: 4, Spirit: 5);

    [Fact]
    public void WorldStep_IsThePetTables()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == PetWorldDataModule.Version);
        Assert.Equal(["pet_levelstats", "petcreateinfo_spell"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
    }

    [Fact]
    public void LevelStats_AreLookedUpByEntryAndLevel_RepeatTheLevelBelowForGaps_AndCapAtTheMaximum()
    {
        var content = new PetContent(
            [Row(416, 1, 50), Row(416, 10, 200), Row(416, 70, 999), Row(416, 0, 999), Row(417, 1, 80, 3, 5)],
            []);

        Assert.Equal(2, content.LevelStatsEntryCount);
        Assert.Equal(50u, content.FindLevelStats(416, 1)!.Health);

        // a level without data repeats the level below it (ObjectMgr.cpp:4505-4514)
        PetLevelStats gap = content.FindLevelStats(416, 5)!;
        Assert.Equal((50u, (byte)5), (gap.Health, gap.Level));
        Assert.Equal(200u, content.FindLevelStats(416, 10)!.Health);
        Assert.Equal(200u, content.FindLevelStats(416, 59)!.Health);

        // levels outside 1..60 are ignored, a lookup above 60 answers level 60 (GetPetLevelInfo)
        Assert.Equal(200u, content.FindLevelStats(416, 99)!.Health);
        Assert.Equal(50u, content.FindLevelStats(416, 0)!.Health);

        Assert.Equal((3f, 5f), (content.FindLevelStats(417, 30)!.MinDamage, content.FindLevelStats(417, 30)!.MaxDamage));
        Assert.Null(content.FindLevelStats(418, 1));
    }

    [Fact]
    public void ACreatureWithoutLevelOneData_IsAHardError()
    {
        // vmangos exits: "Creature %u does not have pet stats data for Level 1!"
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new PetContent([Row(416, 2, 50)], []));
        Assert.Contains("416", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => new PetContent([Row(416, 1, 0)], []));
    }

    [Fact]
    public void CreateSpells_StopAtTheFirstZero_AndAnEmptyListIsNotKept()
    {
        var content = new PetContent([], [new PetCreateSpells(416, [3110, 0, 7799, 0]), new PetCreateSpells(417, [0, 0, 0, 0]), new PetCreateSpells(1860, [3716, 7814, 7815, 7816])]);

        Assert.Equal([3110u], content.GetCreateSpells(416));
        Assert.Empty(content.GetCreateSpells(417));
        Assert.Equal([3716u, 7814u, 7815u, 7816u], content.GetCreateSpells(1860));
        Assert.Equal(2, content.CreateSpellEntryCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Store_RoundTripsTheTables_OnEveryProvider(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<PetLevelStatsRow>().AddRange(
                new PetLevelStatsRow { Entry = 416, Level = 1, Health = 70, Mana = 40, Armor = 5, DmgMin = 2.5f, DmgMax = 4.5f, Strength = 22, Agility = 23, Stamina = 24, Intellect = 25, Spirit = 26 },
                new PetLevelStatsRow { Entry = 416, Level = 20, Health = 300, Mana = 200, Armor = 90, Strength = 30, Agility = 31, Stamina = 32, Intellect = 33, Spirit = 34 });
            db.Set<PetCreateSpellRow>().Add(new PetCreateSpellRow { Entry = 416, Spell1 = 3110, Spell2 = 7799 });
            await db.SaveChangesAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            PetContent content = await new EfPetDataStore(db).LoadAsync();
            Assert.Equal(new PetLevelStats(416, 1, 70, 40, 5, 2.5f, 4.5f, 22, 23, 24, 25, 26), content.FindLevelStats(416, 1));
            Assert.Equal(300u, content.FindLevelStats(416, 20)!.Health);
            Assert.Equal(70u, content.FindLevelStats(416, 19)!.Health);                       // the gap repeats level 1
            Assert.Equal((byte)19, content.FindLevelStats(416, 19)!.Level);                       // ... under its own level number
            Assert.Equal([3110u, 7799u], content.GetCreateSpells(416));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsThePetTables_KeepingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

            // Recreate a database from before this step: no pet tables, the version one below it.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            // Fixed identifiers only (no input reaches this DDL).
            string[] ddl =
            [
                $"DROP TABLE {sql.DelimitIdentifier("pet_levelstats")}",
                $"DROP TABLE {sql.DelimitIdentifier("petcreateinfo_spell")}",
            ];
            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, PetWorldDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Empty(await db.Set<PetLevelStatsRow>().ToListAsync());
            if (pass == 0)
            {
                db.Set<PetCreateSpellRow>().Add(new PetCreateSpellRow { Entry = 1, Spell1 = 2 });
                await db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(2u, (await db.Set<PetCreateSpellRow>().SingleAsync()).Spell1);
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
