using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Spells;

/// <summary>The spell tables on every engine, and the DBC importer.</summary>
public sealed class SpellDataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Modules_AreDiscovered_InTheirDatabaseComponents()
    {
        IDataModule world = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is SpellWorldDataModule);
        IDataModule characters = Assert.Single(DataModules.For(DatabaseComponent.Characters), m => m is CharacterSpellDataModule);
        Assert.Contains(WorldDbContext.Schema.Steps, s => s.Version == world.SchemaVersion && s.Changes.SequenceEqual(world.SchemaChanges));
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == characters.SchemaVersion && s.Changes.SequenceEqual(characters.SchemaChanges));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorldV1Database_UpgradesAndKeepsItsRows_ThenSpellContentRoundTrips(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldV1Context v1 = TestContexts.Create<WorldV1Context>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(v1, WorldV1Context.Schema);
            v1.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await v1.SaveChangesAsync();
        }

        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
        Assert.Equal(60u, (await db.ClassInfo.SingleAsync()).BaseHealth);

        // Content rows (playercreateinfo_spell, spell_target_position) come from the content importer.
        db.Set<PlayerCreateSpellRow>().Add(new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = 78, Note = "Heroic Strike" });
        db.Set<SpellTargetPositionRow>().Add(new SpellTargetPositionRow { Id = 8690, TargetMap = 0, TargetPositionX = 1.5f, TargetPositionY = -2f, TargetPositionZ = 3f, TargetOrientation = 0.5f });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new EfSpellContentStore(db);
        await store.ReplaceDbcTablesAsync(new SpellDbcContent(
            [Template(78, "Heroic Strike", "Rank 1"), Template(133, "Fireball", null)],
            [new SpellCastTimeRow { Id = 1, CastTime = 0 }, new SpellCastTimeRow { Id = 5, CastTime = 3500, CastTimePerLevel = 0, MinCastTime = 3500 }],
            [new SpellDurationRow { Id = 21, Duration = -1, MaxDuration = -1 }],
            [new SpellRangeRow { Id = 1, MinRange = 0, MaxRange = 0 }, new SpellRangeRow { Id = 4, MinRange = 0, MaxRange = 30, Flags = 0 }],
            [new SpellRadiusRow { Id = 7, Radius = 2, RadiusPerLevel = 0, RadiusMax = 2 }]));

        SpellContent content = await store.LoadAsync();
        Assert.Equal([78u, 133u], content.Spells.Select(s => s.Id).Order());
        SpellTemplateRow fireball = content.Spells.Single(s => s.Id == 133);
        Assert.Equal(("Fireball", (string?)null, 0xFFFFFFFEu, -7, 0x8000_0000_0000_0001ul), (fireball.SpellName, fireball.Rank, fireball.PowerType, fireball.EffectBasePoints1, fireball.SpellFamilyFlags));
        Assert.Equal(3500, content.CastTimes.Single(c => c.Id == 5).CastTime);
        Assert.Equal(-1, content.Durations.Single().Duration);
        Assert.Equal(30f, content.Ranges.Single(r => r.Id == 4).MaxRange);
        Assert.Equal(2f, content.Radii.Single().Radius);
        Assert.Equal("Heroic Strike", content.CreateSpells.Single().Note);
        Assert.Equal(1.5f, content.TargetPositions.Single().TargetPositionX);

        // A second import replaces the DBC tables and leaves the content tables alone.
        await store.ReplaceDbcTablesAsync(new SpellDbcContent([Template(116, "Frostbolt", "Rank 1")], [], [], [], []));
        content = await store.LoadAsync();
        Assert.Equal(116u, content.Spells.Single().Id);
        Assert.Empty(content.CastTimes);
        Assert.Single(content.CreateSpells);
        Assert.Single(content.TargetPositions);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterSpellStore_AddsIdempotently_RemovesAndDeletes(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterSpellStore(db);

        await store.AddAsync(1, [78, 6603, 78]);
        await store.AddAsync(1, [78, 2457]);
        await store.AddAsync(2, [133]);
        await store.AddAsync(2, []);

        Assert.Equal([78u, 2457u, 6603u], await store.GetAsync(1));
        Assert.Equal(4, (await store.GetAllAsync()).Count);

        await store.RemoveAsync(1, 2457);
        await store.RemoveAsync(1, 99999);
        Assert.Equal([78u, 6603u], await store.GetAsync(1));

        await store.DeleteCharacterAsync(1);
        Assert.Empty(await store.GetAsync(1));
        Assert.Equal([(2, 133u)], (await store.GetAllAsync()).Select(r => (r.CharacterId, r.Spell)));
    }

    [Fact]
    public void DbcFile_ReadsFieldsAndStrings()
    {
        DbcFile dbc = DbcFile.Parse(BuildDbc(4, [[7u, 0xFFFFFFFFu, BitConverter.SingleToUInt32Bits(2.5f), 1u]], "\0Hello\0"));
        Assert.Equal((1, 4), (dbc.RecordCount, dbc.FieldCount));
        Assert.Equal(7u, dbc.GetUInt32(0, 0));
        Assert.Equal(-1, dbc.GetInt32(0, 1));
        Assert.Equal(2.5f, dbc.GetFloat(0, 2));
        Assert.Equal("Hello", dbc.GetString(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetUInt32(1, 0));
    }

    [Fact]
    public void DbcFile_RejectsMalformedFiles()
    {
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse([1, 2, 3]));
        byte[] good = BuildDbc(1, [[1u]], "\0");
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(good[..^1]));
        byte[] badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(badMagic));
    }

    [Fact]
    public void SpellDbcImporter_MapsTheCmangosFieldIndices()
    {
        var spell = new uint[SpellDbcImporter.SpellFieldCount];
        spell[0] = 133;          // Id
        spell[1] = 2;            // School
        spell[18] = 5;           // CastingTimeIndex
        spell[30] = 21;          // DurationIndex
        spell[31] = 0xFFFFFFFE;  // powerType (health)
        spell[36] = 4;           // rangeIndex
        spell[37] = BitConverter.SingleToUInt32Bits(24f); // speed
        spell[61] = 2;           // Effect1 = SCHOOL_DAMAGE
        spell[76] = unchecked((uint)-1); // EffectBasePoints1
        spell[82] = 6;           // EffectImplicitTargetA1
        spell[106] = unchecked((uint)-3); // EffectMiscValue1
        spell[113] = BitConverter.SingleToUInt32Bits(0.5f); // EffectPointsPerComboPoint2
        spell[115] = 7;          // SpellVisual
        spell[117] = 185;        // SpellIconID
        spell[120] = 1;          // SpellName (string offset)
        spell[129] = 10;         // Rank
        spell[156] = 5;          // ManaCostPercentage
        spell[157] = 133;        // StartRecoveryCategory
        spell[158] = 1500;       // StartRecoveryTime
        spell[161] = 0x10;       // SpellFamilyFlags low
        spell[162] = 0x2;        // SpellFamilyFlags high
        spell[164] = 1;          // DmgClass
        spell[167] = BitConverter.SingleToUInt32Bits(1f); // DmgMultiplier1
        byte[] spellDbc = BuildDbc(SpellDbcImporter.SpellFieldCount, [spell], "\0Fireball\0Rank 1\0");

        SpellDbcContent content = SpellDbcImporter.Read(
            DbcFile.Parse(spellDbc),
            DbcFile.Parse(BuildDbc(4, [[5u, 3500u, 0u, 3500u]], "\0")),
            DbcFile.Parse(BuildDbc(4, [[21u, unchecked((uint)-1), 0u, unchecked((uint)-1)]], "\0")),
            DbcFile.Parse(BuildDbc(22, [[4u, 0u, BitConverter.SingleToUInt32Bits(30f), 0u, .. new uint[18]]], "\0")),
            DbcFile.Parse(BuildDbc(4, [[7u, BitConverter.SingleToUInt32Bits(2f), 0u, BitConverter.SingleToUInt32Bits(2f)]], "\0")));

        SpellTemplateRow row = content.Spells.Single();
        Assert.Equal((133u, 2u, 5u, 21u, 0xFFFFFFFEu, 4u, 24f), (row.Id, row.School, row.CastingTimeIndex, row.DurationIndex, row.PowerType, row.RangeIndex, row.Speed));
        Assert.Equal((2u, -1, 6u, -3), (row.Effect1, row.EffectBasePoints1, row.EffectImplicitTargetA1, row.EffectMiscValue1));
        Assert.Equal(0.5f, row.EffectPointsPerComboPoint2);
        Assert.Equal((7u, 185u, "Fireball", "Rank 1"), (row.SpellVisual, row.SpellIconID, row.SpellName, row.Rank));
        Assert.Equal((5u, 133u, 1500u, 0x2_0000_0010ul, 1u, 1f), (row.ManaCostPercentage, row.StartRecoveryCategory, row.StartRecoveryTime, row.SpellFamilyFlags, row.DmgClass, row.DmgMultiplier1));
        Assert.Equal(3500, content.CastTimes.Single().CastTime);
        Assert.Equal(-1, content.Durations.Single().MaxDuration);
        Assert.Equal(30f, content.Ranges.Single().MaxRange);
        Assert.Equal(2f, content.Radii.Single().RadiusMax);
    }

    [Fact]
    public void SpellDbcImporter_RejectsAnotherBuildsLayout()
        => Assert.Throws<InvalidDataException>(() => SpellDbcImporter.Read(
            DbcFile.Parse(BuildDbc(174, [new uint[174]], "\0")),
            DbcFile.Parse(BuildDbc(4, [], "\0")),
            DbcFile.Parse(BuildDbc(4, [], "\0")),
            DbcFile.Parse(BuildDbc(22, [], "\0")),
            DbcFile.Parse(BuildDbc(4, [], "\0"))));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static SpellTemplateRow Template(uint id, string name, string? rank) => new()
    {
        Id = id,
        SpellName = name,
        Rank = rank,
        PowerType = 0xFFFFFFFE,
        EffectBasePoints1 = -7,
        SpellFamilyFlags = 0x8000_0000_0000_0001ul,
        Speed = 24f,
    };

    /// <summary>A WDBC image: header, records of four-byte fields, string block.</summary>
    private static byte[] BuildDbc(int fields, IReadOnlyList<uint[]> records, string strings)
    {
        byte[] block = Encoding.UTF8.GetBytes(strings);
        var data = new byte[20 + (records.Count * fields * 4) + block.Length];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)(fields * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)block.Length);
        for (int r = 0; r < records.Count; r++)
        {
            for (int f = 0; f < records[r].Length; f++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20 + (((r * fields) + f) * 4)), records[r][f]);
            }
        }

        block.CopyTo(data, 20 + (records.Count * fields * 4));
        return data;
    }
}

/// <summary>The world database as M3–M6 created it (schema version 1, no spell tables).</summary>
internal sealed class WorldV1Context(DbContextOptions<WorldV1Context> options) : DbContext(options)
{
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "world",
        CurrentVersion = 1,
        Version1Tables = ["player_create_info", "race_info", "class_info"],
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
        modelBuilder.Entity<PlayerCreateInfoRow>(e =>
        {
            e.ToTable("player_create_info");
            e.HasKey(r => new { r.Race, r.Class });
        });
        modelBuilder.Entity<RaceInfoRow>(e =>
        {
            e.ToTable("race_info");
            e.HasKey(r => new { r.Race, r.Gender });
        });
        modelBuilder.Entity<ClassInfoRow>(e =>
        {
            e.ToTable("class_info");
            e.HasKey(r => r.Class);
        });
    }
}
