using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

/// <summary>
/// Characters schema 76 (<c>character_pet.CreatedBySpell</c>, <c>ResetTalentsCost</c>, <c>ResetTalentsTime</c>; vmangos created_by_spell, reset_talents_cost,
/// reset_talents_time, Pet.cpp:580-583) and world schema 70 (<c>pet_name_generation</c>, vmangos ObjectMgr::LoadPetNames, ObjectMgr.cpp:8530). The rows are
/// invented test data.
/// </summary>
public sealed class PetDemonAndUntrainPersistenceTests
{
    private const string NameDump = """
        CREATE TABLE `pet_name_generation` (`id` int unsigned NOT NULL, `word` varchar(100) NOT NULL, `entry` int unsigned NOT NULL, `half` tinyint(1) NOT NULL, PRIMARY KEY (`id`));
        INSERT INTO `pet_name_generation` VALUES (1,'Zor',416,0),(2,'Bix',416,0),(3,'ax',416,1),(4,'Only',417,0);
        """;

    [Fact]
    public async Task Sqlite_CurrentPet_RoundTripsTheDemonSpellAndTheUntrainPriceAndTime()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using (CharacterDbContext db = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await new EfPersistentPetStore(db).SaveCurrentAsync(new PersistentPetSnapshot(
                31, 7001, 416, 12, 0, 90, 55, 0, 1, [], [], Name: "Zorax", RenameAllowed: false,
                CreatedBySpell: 688, ResetTalentsCost: 5_000, ResetTalentsTime: 1_800_000_000L));
        }

        await using CharacterDbContext verify = new(options);
        PersistentPetSnapshot? actual = await new EfPersistentPetStore(verify).LoadCurrentAsync(31);
        Assert.NotNull(actual);
        Assert.Equal((688u, 5_000u, 1_800_000_000L), (actual!.CreatedBySpell, actual.ResetTalentsCost, actual.ResetTalentsTime));
        Assert.Equal("Zorax", actual.Name);
    }

    [Fact]
    public async Task Sqlite_V75PetRow_UpgradesToTheDemonAndUntrainColumnsWithoutLosingTheRow()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        await new EfPersistentPetStore(db).SaveCurrentAsync(new PersistentPetSnapshot(32, 3, 9, 4, 0, 20, 2, 3, 1, [], []));
        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, PetUntrainAndDemonDataModule.Version - 1));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE character_pet DROP COLUMN CreatedBySpell");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE character_pet DROP COLUMN ResetTalentsCost");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE character_pet DROP COLUMN ResetTalentsTime");

        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

        PersistentPetSnapshot? upgraded = await new EfPersistentPetStore(db).LoadCurrentAsync(32);
        Assert.NotNull(upgraded);
        Assert.Equal((0u, 0u, 0L), (upgraded!.CreatedBySpell, upgraded.ResetTalentsCost, upgraded.ResetTalentsTime));
        Assert.Equal(9u, upgraded.Entry);
    }

    [Fact]
    public void Step76_IsThreeAddedColumnsOfCharacterPet()
    {
        SchemaStep step = Assert.Single(CharacterDbContext.Schema.Steps, s => s.Version == PetUntrainAndDemonDataModule.Version);
        Assert.Equal(
            [nameof(PersistentPetRow.CreatedBySpell), nameof(PersistentPetRow.ResetTalentsCost), nameof(PersistentPetRow.ResetTalentsTime)],
            step.Changes.OfType<AddColumnChange>().Select(c => c.Column));
        Assert.Empty(step.Changes.OfType<CreateTableChange>());
    }

    [Fact]
    public void Step70_IsThePetNameGenerationTable()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == PetNameGenerationWorldDataModule.Version);
        Assert.Equal(["pet_name_generation"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
    }

    [Fact]
    public async Task ImportedNameParts_LoadIntoPetContent_AndGenerateAFirstHalfPlusASecondHalf()
    {
        var importer = new PetDumpImporter();
        importer.Read(new StringReader(NameDump));
        Assert.Equal(4, importer.NameParts.Count);
        Assert.Equal(4, importer.BuildReport().NameParts);

        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<WorldDbContext> options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options;
        await using (WorldDbContext db = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: true);
        }

        await using WorldDbContext verify = new(options);
        PetContent content = await new EfPetDataStore(verify).LoadAsync();
        Assert.Equal(1, content.NameEntryCount); // 417 has only a first half
        string name = content.GeneratePetName(416, new Random(5))!;
        Assert.True(name is "Zorax" or "Bixax", name);
        Assert.Null(content.GeneratePetName(417, new Random(5))); // vmangos falls back to the creature name when a list is empty
        Assert.Null(content.GeneratePetName(9999, new Random(5)));
    }

    [Fact]
    public void PetNameGenerationSpec_ReadsTheVMangosColumns()
    {
        TableSpec spec = ContentTableSpecs.Find("pet_name_generation")!;
        Assert.True(spec.IsMapped("word") && spec.IsMapped("entry") && spec.IsMapped("half"));
    }
}
