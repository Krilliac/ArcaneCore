using System.Buffers.Binary;
using System.Text;
using ArcaneCore.AccountTool;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.ClientEffects;
using ArcaneCore.Data.Content.Creatures;
using ArcaneCore.Data.Content.GameObjects;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Content.Pets;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Data.Crafting;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Items;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Data.Skills;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Talents;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.PlayerStats;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Data.World.Threat;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.WorldData.Procs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Data.Tests.Fuzz;

/// <summary>
/// Exception-path and deterministic seeded fuzz tests across <c>ArcaneCore.Data</c> (DBC binary readers,
/// SQL dump/migration parsers, schema migration/upgrade/snapshot validators, database options/resilience
/// guards, CLI entry points) and <c>ArcaneCore.AccountTool</c> (<c>PasswordSource</c>).
/// </summary>
public sealed class DataAndAccountToolFuzzAndExceptionTests
{
    private const int DbcHeaderSize = 20;
    private const uint WdbcMagic = 0x43424457u;

    private sealed class DummyMappedRow
    {
        public uint Entry { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private static byte[] BuildSyntheticDbc(
        int recordCount,
        int fieldCount,
        int recordSize,
        ReadOnlySpan<byte> stringBlock,
        Action<Span<byte>>? fillRecords = null,
        uint magic = WdbcMagic)
    {
        byte[] buffer = new byte[DbcHeaderSize + (recordCount * recordSize) + stringBlock.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), magic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), recordCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), fieldCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12, 4), recordSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), stringBlock.Length);

        if (recordCount * recordSize > 0)
        {
            fillRecords?.Invoke(buffer.AsSpan(DbcHeaderSize, recordCount * recordSize));
        }

        stringBlock.CopyTo(buffer.AsSpan(DbcHeaderSize + (recordCount * recordSize)));
        return buffer;
    }

    private static DbcFile CreateValidDbc(int recordCount, int fieldCount, Action<int, Span<uint>>? fillRow = null)
    {
        byte[] stringBlock = [0, (byte)'A', (byte)'r', (byte)'c', (byte)'a', (byte)'n', (byte)'e', 0];
        byte[] bytes = BuildSyntheticDbc(
            recordCount,
            fieldCount,
            fieldCount * 4,
            stringBlock,
            records =>
            {
                for (int r = 0; r < recordCount; r++)
                {
                    Span<uint> row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                        records.Slice(r * fieldCount * 4, fieldCount * 4));
                    row[0] = (uint)(r + 1);
                    fillRow?.Invoke(r, row);
                }
            });
        return DbcFile.Parse(bytes);
    }

    // =========================================================================
    // 1. DbcFile & DbcTable<TRow> Exception Tests
    // =========================================================================

    [Fact]
    public void DbcFile_Parse_And_Load_ThrowOnInvalidHeadersAndArguments()
    {
        Assert.Throws<ArgumentNullException>(() => DbcFile.Parse(null!));
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(new byte[DbcHeaderSize - 1]));

        // Bad magic (not 'WDBC')
        byte[] badMagic = BuildSyntheticDbc(0, 1, 4, [0], magic: 0x12345678u);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(badMagic));

        // Negative record count
        byte[] negRecords = new byte[DbcHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(negRecords.AsSpan(0, 4), WdbcMagic);
        BinaryPrimitives.WriteInt32LittleEndian(negRecords.AsSpan(4, 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(negRecords.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(negRecords.AsSpan(12, 4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(negRecords.AsSpan(16, 4), 0);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(negRecords));

        // Negative field count
        byte[] negFields = new byte[DbcHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(negFields.AsSpan(0, 4), WdbcMagic);
        BinaryPrimitives.WriteInt32LittleEndian(negFields.AsSpan(8, 4), -1);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(negFields));

        // Record size smaller than fieldCount * 4
        byte[] smallRecordSize = new byte[DbcHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(smallRecordSize.AsSpan(0, 4), WdbcMagic);
        BinaryPrimitives.WriteInt32LittleEndian(smallRecordSize.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(smallRecordSize.AsSpan(12, 4), 4);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(smallRecordSize));

        // Negative string block size
        byte[] negStringBlock = new byte[DbcHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(negStringBlock.AsSpan(0, 4), WdbcMagic);
        BinaryPrimitives.WriteInt32LittleEndian(negStringBlock.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(negStringBlock.AsSpan(12, 4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(negStringBlock.AsSpan(16, 4), -1);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(negStringBlock));

        // Truncated payload
        byte[] truncated = new byte[DbcHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0, 4), WdbcMagic);
        BinaryPrimitives.WriteInt32LittleEndian(truncated.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(truncated.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(truncated.AsSpan(12, 4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(truncated.AsSpan(16, 4), 1);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(truncated));
    }

    [Fact]
    public void DbcFile_Accessors_ThrowOnOutOfRangeAndUnterminatedStrings()
    {
        byte[] stringBlock = [0, (byte)'O', (byte)'K', 0, (byte)'U', (byte)'N', (byte)'T', (byte)'E', (byte)'R', (byte)'M'];
        byte[] raw = BuildSyntheticDbc(
            recordCount: 1,
            fieldCount: 3,
            recordSize: 12,
            stringBlock,
            records =>
            {
                BinaryPrimitives.WriteUInt32LittleEndian(records.Slice(0, 4), 1u); // valid string offset 1 ("OK")
                BinaryPrimitives.WriteUInt32LittleEndian(records.Slice(4, 4), 4u); // unterminated string offset 4
                BinaryPrimitives.WriteUInt32LittleEndian(records.Slice(8, 4), 999u); // out-of-range string offset
            });

        DbcFile dbc = DbcFile.Parse(raw);

        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetUInt32(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetUInt32(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetUInt32(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetUInt32(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetInt32(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetFloat(0, 3));

        Assert.Equal("OK", dbc.GetString(0, 0));
        Assert.Equal("OK", dbc.GetStringStrict(0, 0));

        // Out-of-range string offset throws InvalidDataException in both GetString and GetStringStrict
        Assert.Throws<InvalidDataException>(() => dbc.GetString(0, 2));
        Assert.Throws<InvalidDataException>(() => dbc.GetStringStrict(0, 2));

        // Unterminated string returns tail in GetString, but throws InvalidDataException in GetStringStrict
        Assert.Equal("UNTERM", dbc.GetString(0, 1));
        Assert.Throws<InvalidDataException>(() => dbc.GetStringStrict(0, 1));

        // Empty string block throws on both GetString(offset 0) and GetStringStrict(offset 0)
        DbcFile emptyStringBlockDbc = DbcFile.Parse(BuildSyntheticDbc(1, 1, 4, []));
        Assert.Throws<InvalidDataException>(() => emptyStringBlockDbc.GetString(0, 0));
        Assert.Throws<InvalidDataException>(() => emptyStringBlockDbc.GetStringStrict(0, 0));
    }

    [Fact]
    public void DbcTable_Constructor_ThrowsOnInvalidArgumentsAndDuplicateKeys()
    {
        Assert.Throws<ArgumentException>(() => new DbcTable<string>("", ["a"], x => (uint)x.Length));
        Assert.Throws<ArgumentException>(() => new DbcTable<string>("   ", ["a"], x => (uint)x.Length));
        Assert.Throws<ArgumentNullException>(() => new DbcTable<string>("Test.dbc", null!, x => (uint)x.Length));
        Assert.Throws<ArgumentNullException>(() => new DbcTable<string>("Test.dbc", ["a"], null!));
        Assert.Throws<InvalidDataException>(() => new DbcTable<string>("Test.dbc", ["dup", "dup"], _ => 7u));
    }

    // =========================================================================
    // 2. DBC Readers Exception Tests Across All Content Subsystems
    // =========================================================================

    [Fact]
    public void DbcReaders_ThrowOnNullWrongFieldCountAndInvalidRows()
    {
        DbcFile wrongFields = CreateValidDbc(recordCount: 1, fieldCount: 2);

        // SpellDbcImporter
        Assert.ThrowsAny<Exception>(() => SpellDbcImporter.ReadDirectory(""));
        Assert.Throws<ArgumentNullException>(() => SpellDbcImporter.Read(null!, wrongFields, wrongFields, wrongFields, wrongFields));
        Assert.Throws<InvalidDataException>(() => SpellDbcImporter.Read(wrongFields, wrongFields, wrongFields, wrongFields, wrongFields));

        // ShapeshiftFormDbcReader (expects 14 fields, non-zero unique ID)
        Assert.Throws<ArgumentNullException>(() => ShapeshiftFormDbcReader.Load(null!));
        Assert.Throws<ArgumentNullException>(() => ShapeshiftFormDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => ShapeshiftFormDbcReader.Read(wrongFields));
        DbcFile zeroIdShapeshift = CreateValidDbc(1, 14, (_, row) => row[0] = 0u);
        Assert.Throws<InvalidDataException>(() => ShapeshiftFormDbcReader.Read(zeroIdShapeshift));
        DbcFile dupIdShapeshift = CreateValidDbc(2, 14, (_, row) => row[0] = 5u);
        Assert.Throws<InvalidDataException>(() => ShapeshiftFormDbcReader.Read(dupIdShapeshift));

        // CharacterAppearanceDbcReader (expects 10 and 9 fields)
        Assert.Throws<ArgumentException>(() => CharacterAppearanceDbcReader.Load("", "x"));
        Assert.Throws<ArgumentNullException>(() => CharacterAppearanceDbcReader.Build(null!, wrongFields));
        Assert.Throws<ArgumentNullException>(() => CharacterAppearanceDbcReader.ReadSections(null!));
        Assert.Throws<ArgumentNullException>(() => CharacterAppearanceDbcReader.ReadFacialHairStyles(null!));
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceDbcReader.ReadSections(wrongFields));
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceDbcReader.ReadFacialHairStyles(wrongFields));

        // CharStartOutfitDbcReader (expects 41 fields, 164-byte record size)
        Assert.Throws<ArgumentException>(() => CharStartOutfitDbcReader.Load(""));
        Assert.Throws<InvalidDataException>(() => CharStartOutfitDbcReader.Read(new byte[10]));
        Assert.Throws<InvalidDataException>(() => CharStartOutfitDbcReader.Read(BuildSyntheticDbc(1, 2, 8, [0])));

        // ItemSetDbcReader (expects 45 fields, unique ID)
        Assert.Throws<ArgumentException>(() => ItemSetDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => ItemSetDbcReader.Build(null!));
        Assert.Throws<InvalidDataException>(() => ItemSetDbcReader.Build(wrongFields));
        DbcFile dupItemSet = CreateValidDbc(2, 45, (_, row) => row[0] = 10u);
        Assert.Throws<InvalidDataException>(() => ItemSetDbcReader.Build(dupItemSet));

        // Maps: AreaTableDbcReader (25), AreaTriggerDbcReader (10), MapDbcReader (42), MapAreaDbcImporter
        Assert.Throws<ArgumentNullException>(() => AreaTableDbcReader.Load(null!));
        Assert.Throws<ArgumentNullException>(() => AreaTableDbcReader.Read((DbcFile)null!));
        Assert.Throws<InvalidDataException>(() => AreaTableDbcReader.Read(wrongFields));
        Assert.Throws<ArgumentNullException>(() => AreaTriggerDbcReader.Load(null!));
        Assert.Throws<ArgumentNullException>(() => AreaTriggerDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => AreaTriggerDbcReader.Read(wrongFields));
        Assert.Throws<ArgumentNullException>(() => MapDbcReader.Load(null!));
        Assert.Throws<ArgumentNullException>(() => MapDbcReader.Read((DbcFile)null!));
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(wrongFields));
        Assert.Throws<ArgumentNullException>(() => MapAreaDbcImporter.ReadSnapshot(null!, "AreaTable.dbc", null));

        // NamesDbcReader (2 fields)
        Assert.Throws<ArgumentException>(() => NamesDbcReader.Read("", "profane", out _));
        Assert.Throws<ArgumentNullException>(() => NamesDbcReader.Read((DbcFile)null!, "profane"));
        Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(CreateValidDbc(1, 3), "profane"));

        // CreatureFamilyDbcReader (18 fields)
        Assert.Throws<ArgumentNullException>(() => CreatureFamilyDbcReader.ReadFoodMasks(null!));
        Assert.Throws<ArgumentNullException>(() => CreatureFamilyDbcReader.ReadSkillLines(null!));
        Assert.Throws<InvalidDataException>(() => CreatureFamilyDbcReader.ReadFoodMasks(wrongFields));
        Assert.Throws<InvalidDataException>(() => CreatureFamilyDbcReader.ReadSkillLines(wrongFields));

        // CreatureDisplayModelDbcReader (12 and 16 fields)
        Assert.Throws<ArgumentException>(() => CreatureDisplayModelDbcReader.Load("", "model.dbc"));
        Assert.Throws<ArgumentNullException>(() => CreatureDisplayModelDbcReader.Read(null!, wrongFields));
        Assert.Throws<InvalidDataException>(() => CreatureDisplayModelDbcReader.Read(wrongFields, CreateValidDbc(1, 16)));
        Assert.Throws<InvalidDataException>(() => CreatureDisplayModelDbcReader.Read(CreateValidDbc(1, 12), wrongFields));

        // GameObjectDisplayInfoDbcReader (12 fields)
        Assert.Throws<ArgumentException>(() => GameObjectDisplayInfoDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => GameObjectDisplayInfoDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => GameObjectDisplayInfoDbcReader.Read(wrongFields));

        // TransportAnimationDbcReader (7 fields)
        Assert.Throws<ArgumentException>(() => TransportAnimationDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => TransportAnimationDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => TransportAnimationDbcReader.Read(wrongFields));

        // EnchantDbcReader (24 fields)
        Assert.Throws<ArgumentException>(() => EnchantDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => EnchantDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => EnchantDbcReader.Read(wrongFields));

        // WorldSafeLocsDbcReader (14 fields)
        Assert.Throws<ArgumentException>(() => WorldSafeLocsDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => WorldSafeLocsDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => WorldSafeLocsDbcReader.Read(wrongFields));

        // FactionTemplateDbcReader (14 fields) & FactionDbcReader (37 fields)
        Assert.Throws<ArgumentException>(() => FactionTemplateDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => FactionTemplateDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => FactionTemplateDbcReader.Read(wrongFields));
        Assert.Throws<ArgumentException>(() => FactionDbcReader.Load(""));
        Assert.Throws<ArgumentNullException>(() => FactionDbcReader.Read(null!));
        Assert.Throws<InvalidDataException>(() => FactionDbcReader.Read(wrongFields));

        // NpcServiceDbcReaders, SkillDbcReaders, ChatChannelsDbcReader, EmoteDbcReaders, TalentDbcReaders
        Assert.Throws<ArgumentNullException>(() => NpcServiceDbcReaders.ReadTaxiNodes(null!));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiNodes(wrongFields));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiPaths(wrongFields));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiPathNodes(wrongFields));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadSkillLineAbilities(wrongFields));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadRepairCosts(wrongFields, wrongFields));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadBankBagSlotPrices(CreateValidDbc(1, 3)));

        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillLines(wrongFields));
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillRaceClassInfos(wrongFields));
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillTiers(wrongFields));

        Assert.Throws<InvalidDataException>(() => ChatChannelsDbcReader.Read(wrongFields));
        Assert.Throws<InvalidDataException>(() => EmoteDbcReaders.ReadText(wrongFields));
        Assert.Throws<InvalidDataException>(() => EmoteDbcReaders.ReadEmotes(wrongFields));
        Assert.Throws<InvalidDataException>(() => TalentDbcReaders.ReadCatalog(wrongFields, wrongFields));

        // ClientEffects readers
        Assert.Throws<InvalidDataException>(() => CinematicSequencesDbcReader.Read(wrongFields));
        Assert.Throws<InvalidDataException>(() => SoundEntriesDbcReader.Read(wrongFields));
        Assert.Throws<InvalidDataException>(() => SpellVisualEffectNameDbcReader.Read(wrongFields));
        Assert.Throws<InvalidDataException>(() => SpellVisualKitDbcReader.Read(wrongFields));
        Assert.Throws<InvalidDataException>(() => WorldStateUIDbcReader.Read(wrongFields));
        Assert.Throws<InvalidDataException>(() => ZoneMusicDbcReader.Read(wrongFields));
    }

    // =========================================================================
    // 3. SQL Dump & Migration Parsers Exception Tests
    // =========================================================================

    [Fact]
    public void MySqlDumpReader_ThrowsFormatException_OnMalformedSqlAndExceededLimits()
    {
        Assert.ThrowsAny<Exception>(() => new MySqlDumpReader(null!).Read().ToList());

        // INSERT without table name
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO ;")).Read().ToList());

        // Empty quoted identifier
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO `` (`id`) VALUES (1);")).Read().ToList());

        // Unterminated backtick identifier
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO `unterminated (id) VALUES (1);")).Read().ToList());

        // Unterminated string literal
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO `t` (`a`) VALUES ('unterminated);")).Read().ToList());

        // INSERT without CREATE TABLE or explicit column list
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO `unknown_table` VALUES (1);")).Read().ToList());

        // Unterminated column list
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO `t` (`a`, `b` VALUES (1, 2);")).Read().ToList());

        // Unterminated tuple
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("INSERT INTO `t` (`a`) VALUES (1;")).Read().ToList());

        // Unexpected stray operator between statements
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("- invalid")).Read().ToList());
        Assert.Throws<FormatException>(() => new MySqlDumpReader(new StringReader("/ invalid")).Read().ToList());

        // Limits: MaxColumns, MaxStatementChars, MaxTokenChars
        var tightColumns = new MySqlDumpLimits { MaxColumns = 1 };
        Assert.Throws<FormatException>(() =>
            new MySqlDumpReader(new StringReader("INSERT INTO `t` (`a`, `b`) VALUES (1, 2);"), tightColumns).Read().ToList());

        var tightStatement = new MySqlDumpLimits { MaxStatementChars = 10 };
        Assert.Throws<FormatException>(() =>
            new MySqlDumpReader(new StringReader("CREATE TABLE `long_table_name` (`id` int);"), tightStatement).Read().ToList());

        var tightToken = new MySqlDumpLimits { MaxTokenChars = 4 };
        Assert.Throws<FormatException>(() =>
            new MySqlDumpReader(new StringReader("INSERT INTO `t` (`a`) VALUES ('123456789');"), tightToken).Read().ToList());
    }

    [Fact]
    public void VMangosMigrationReader_ThrowsOnNullAndUnsupportedStatements()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "player_levelstats", "player_classlevelstats" };

        Assert.Throws<ArgumentNullException>(() => VMangosMigrationReader.Parse(null!, "mig.sql", tracked));
        Assert.Throws<ArgumentNullException>(() => VMangosMigrationReader.Parse(new StringReader(""), "mig.sql", null!));

        // Unsupported verbs on tracked tables must fail closed with NotSupportedException
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("DELETE FROM `player_levelstats` WHERE `level` = 1;"), "del.sql", tracked));
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("DROP TABLE `player_levelstats`;"), "drop.sql", tracked));
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("TRUNCATE TABLE `player_levelstats`;"), "trunc.sql", tracked));
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("ALTER TABLE `player_levelstats` ADD COLUMN `extra` INT;"), "alter.sql", tracked));

        // UPDATE without SET/WHERE
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("UPDATE `player_levelstats` SET `str` = 10;"), "nowhere.sql", tracked));

        // Non-numeric assignment in SET
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("UPDATE `player_levelstats` SET `str` = `str` + 1 WHERE `level` = 1;"), "expr.sql", tracked));

        // INSERT without explicit column list
        Assert.Throws<NotSupportedException>(() =>
            VMangosMigrationReader.Parse(new StringReader("INSERT INTO `player_levelstats` VALUES (1, 1, 1, 1, 1, 1, 1, 1);"), "nocols.sql", tracked));
    }

    [Fact]
    public void DumpImporters_ThrowOnNullAndInvalidColumnValues()
    {
        // SpellProcEventDumpImporter
        Assert.Throws<ArgumentNullException>(() => SpellProcEventDumpImporter.Parse(null!));
        Assert.Throws<InvalidDataException>(() => SpellProcEventDumpImporter.Parse(
            new StringReader("INSERT INTO `spell_proc_event` (`entry`, `SchoolMask`) VALUES ('not_a_number', 0);")));

        // SpellThreatDumpImporter
        Assert.Throws<ArgumentNullException>(() => SpellThreatDumpImporter.Parse(null!));
        Assert.Throws<InvalidDataException>(() => SpellThreatDumpImporter.Parse(
            new StringReader("INSERT INTO `spell_threat` (`entry`, `Threat`) VALUES (-1, 10);")));

        // WorldStateDumpImporter
        Assert.Throws<ArgumentNullException>(() => WorldStateDumpImporter.Parse(null!));
        Assert.Throws<InvalidDataException>(() => WorldStateDumpImporter.Parse(
            new StringReader("CREATE TABLE `exploration_basexp` (\n`unterminated\n);\n")));

        // PlayerStatsDumpImporter
        var statsImporter = new PlayerStatsDumpImporter();
        Assert.Throws<ArgumentNullException>(() => statsImporter.Read(null!));
        Assert.Throws<ArgumentException>(() => statsImporter.ApplyMigration("", new StringReader("")));
        Assert.Throws<ArgumentNullException>(() => statsImporter.ApplyMigration("001.sql", null!));
        Assert.Throws<ArgumentException>(() => statsImporter.ApplyMigrationDirectory(""));

        // ContentScanner & TableSpec & RowMapper
        Assert.Throws<ArgumentNullException>(() => ContentScanner.Scan(null!));
        Assert.Throws<ArgumentException>(() => new RowMapper<DummyMappedRow>(aliases: new Dictionary<string, string> { ["bad"] = "NonExistentProperty" }));
        Assert.Throws<ArgumentException>(() => new RowMapper<DummyMappedRow>(signedAsUnsigned: ["NonExistentProperty"]));
        Assert.Throws<ArgumentNullException>(() => new RowMapper<DummyMappedRow>().Map(null!));
        Assert.Throws<ImportSchemaException>(() =>
            new RowMapper<DummyMappedRow>().Map(new DumpRow("creature_template", ["entry"], ["not_an_int"])));

        Assert.Throws<ArgumentException>(() => new TableSpec("", [new KeyColumn("entry")], [], []));
        var spec = new TableSpec("test_table", [new KeyColumn("entry")], ["entry"], [new DialectSignature(ContentDialect.CMangos, "col_a")]);
        Assert.Throws<ImportSchemaException>(() => spec.ResolveKey(["other_col"]));
        Assert.Throws<ImportSchemaException>(() => spec.DetectDialect(["entry", "other_col"]));

        // ChainedTextReader & DumpInput & DumpFiles
        Assert.Throws<ArgumentNullException>(() => ChainedTextReader.Create(null!));
        Assert.Throws<ArgumentException>(() => DumpInput.File(""));
        Assert.Throws<ArgumentException>(() => DumpInput.Text("", "SELECT 1;"));
        Assert.Throws<ArgumentNullException>(() => DumpInput.Text("test.sql", null!));
        Assert.Throws<ArgumentNullException>(() => DumpFiles.OpenText(null!));
        Assert.Throws<ArgumentException>(() => DumpFiles.OpenText(""));
        Assert.Throws<ArgumentException>(() => DumpFiles.Describe(""));
    }

    // =========================================================================
    // 4. Database Options, Resilience & Schema Validators Exception Tests
    // =========================================================================

    [Fact]
    public void DatabaseOptions_And_ResilienceValidators_ThrowOnInvalidSettings()
    {
        // DataServiceCollectionExtensions & DatabaseResilienceServiceCollectionExtensions
        Assert.Throws<InvalidOperationException>(() =>
            DataServiceCollectionExtensions.ConfigureProvider(
                new DbContextOptionsBuilder<AuthDbContext>(),
                new DatabaseConnectionOptions { ConnectionString = "" }));
        Assert.Throws<ArgumentNullException>(() =>
            DataServiceCollectionExtensions.IsCharacterWriteFenceRequired(null!));
        Assert.Throws<ArgumentNullException>(() =>
            DatabaseResilienceServiceCollectionExtensions.AddDatabaseResilience(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() =>
            DatabaseResilienceServiceCollectionExtensions.AddDatabaseResilience(new ServiceCollection(), null!));

        // SlowCommandInterceptor & SlowTransactionInterceptor
        Assert.Throws<ArgumentNullException>(() => new SlowCommandInterceptor(100, null!));
        Assert.Throws<ArgumentNullException>(() => new SlowTransactionInterceptor(100, null!));

        // SqliteConnectionPragmaInterceptor
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(-1));
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(SqliteConnectionPragmaInterceptor.MaxBusyTimeoutMs + 1));
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(5000, writeTimeoutMs: -1));
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(5000, synchronous: (SqliteSynchronousMode)99));
        Assert.Throws<InvalidOperationException>(() => SqliteConnectionPragmaInterceptor.For(5000, walJournalSizeLimit: SqliteCheckpointOptions.MaxBytes + 1));

        // SqliteCheckpointOptions.Validate
        var badIntervalLow = new SqliteCheckpointOptions { IntervalMs = SqliteCheckpointOptions.MinIntervalMs - 1 };
        Assert.Throws<InvalidOperationException>(() => badIntervalLow.Validate());
        var badIntervalHigh = new SqliteCheckpointOptions { IntervalMs = SqliteCheckpointOptions.MaxIntervalMs + 1 };
        Assert.Throws<InvalidOperationException>(() => badIntervalHigh.Validate());
        var badLimit = new SqliteCheckpointOptions { JournalSizeLimitBytes = -1 };
        Assert.Throws<InvalidOperationException>(() => badLimit.Validate());
        var hugeLimit = new SqliteCheckpointOptions { JournalSizeLimitBytes = SqliteCheckpointOptions.MaxBytes + 1 };
        Assert.Throws<InvalidOperationException>(() => hugeLimit.Validate());

        // SqliteSingleWriterOptions.Validate & SqliteWriteGateInterceptor.For
        var badSingleWriter = new SqliteSingleWriterOptions { StatsIntervalMs = -1 };
        Assert.Throws<InvalidOperationException>(() => badSingleWriter.Validate());
        var tooLargeSingleWriter = new SqliteSingleWriterOptions { StatsIntervalMs = SqliteSingleWriterOptions.MaxMs + 1 };
        Assert.Throws<InvalidOperationException>(() => tooLargeSingleWriter.Validate());
        Assert.Throws<ArgumentNullException>(() => SqliteWriteGateInterceptor.For(null!, null));
        Assert.Throws<InvalidOperationException>(() => SqliteWriteGateInterceptor.For(badSingleWriter, null));

        // DatabaseGuard & DatabaseCircuits
        IOptions<ResilienceOptions> resilienceOpts = Options.Create(new ResilienceOptions());
        IOptions<DatabaseOptions> dbOpts = Options.Create(new DatabaseOptions());
        var circuits = new DatabaseCircuits(resilienceOpts, NullLogger<DatabaseCircuits>.Instance);

        Assert.Throws<ArgumentNullException>(() => new DatabaseCircuits(null!, NullLogger<DatabaseCircuits>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DatabaseCircuits(resilienceOpts, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => circuits.For((DatabaseComponent)99));

        Assert.Throws<ArgumentNullException>(() => new DatabaseGuard(null!, dbOpts, circuits, NullLogger<DatabaseGuard>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DatabaseGuard(resilienceOpts, null!, circuits, NullLogger<DatabaseGuard>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DatabaseGuard(resilienceOpts, dbOpts, null!, NullLogger<DatabaseGuard>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DatabaseGuard(resilienceOpts, dbOpts, circuits, null!));
    }

    [Fact]
    public async Task SchemaAndUpgradeValidators_ThrowOnInvalidInputsAndForbiddenPoliciesAsync()
    {
        // DatabaseUpgradeOptions.ToSchemaOptions
        var badTimeout = new DatabaseUpgradeOptions { LockTimeoutSeconds = 0 };
        Assert.Throws<InvalidOperationException>(() => badTimeout.ToSchemaOptions());
        var hugeTimeout = new DatabaseUpgradeOptions { LockTimeoutSeconds = 86401 };
        Assert.Throws<InvalidOperationException>(() => hugeTimeout.ToSchemaOptions());
        var badPolicy = new DatabaseUpgradeOptions { Policy = (SchemaPolicy)99 };
        Assert.Throws<InvalidOperationException>(() => badPolicy.ToSchemaOptions());

        // DatabaseStartup
        Assert.Throws<ArgumentNullException>(() => DatabaseStartup.OptionsFrom(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DatabaseStartup.InitializeAsync(null!, null, TextWriter.Null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DatabaseStartup.InitializeAsync(() => Task.CompletedTask, null, null!));

        // SchemaSnapshot.Parse
        Assert.Throws<ArgumentNullException>(() => SchemaSnapshot.Parse(null!));
        Assert.Throws<InvalidDataException>(() => SchemaSnapshot.Parse("null"));
        Assert.Throws<System.Text.Json.JsonException>(() =>
            SchemaSnapshot.Parse("{\"component\":\"auth\",\"version\":1,\"providers\":{\"sqlite\":{\"tables\":[{\"name\":\"t\",\"columns\":[\"not_an_array\"],\"primaryKey\":[],\"indexes\":[]}],\"previous\":null}},\"files\":{}}"));

        // BackupAdvisor
        Assert.Throws<ArgumentNullException>(() => BackupAdvisor.Describe(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => BackupAdvisor.BackupSqliteAsync(null!, "dir"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BackupAdvisor.BackupSqliteAsync(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" }, ""));
        await Assert.ThrowsAsync<BackupException>(() =>
            BackupAdvisor.BackupSqliteAsync(new DatabaseConnectionOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=localhost" }, "dir"));
        await Assert.ThrowsAsync<BackupException>(() =>
            BackupAdvisor.BackupSqliteAsync(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" }, "dir"));

        // ForeignLine & ForeignLineDetector
        var line = new ForeignLine
        {
            Name = "codex",
            DivergedAfter = 1,
            Steps = [new ForeignLineStep(2, null, "ModA", [new SchemaObject("tbl_a")])],
        };
        Assert.Throws<InvalidOperationException>(() => line.MergedVersionAt(2));
        Assert.Throws<ArgumentNullException>(() => ForeignLineDetector.ObjectsOf(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ForeignLineDetector.DetectAsync(null!, AuthDbContext.Schema, 2));

        // ServerProbe
        Assert.Throws<ArgumentNullException>(() => ServerProbe.Qualify("Microsoft.EntityFrameworkCore.Sqlite", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ServerProbe.ReadServerInfoAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ServerProbe.CountOtherSessionsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ServerProbe.IsSchemaLockHeldAsync(null!, "auth"));
        using (var sqliteConn = new SqliteConnection("Data Source=:memory:"))
        {
            var opts = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(sqliteConn).Options;
            await using var authDb = new AuthDbContext(opts);
            await Assert.ThrowsAsync<ArgumentException>(() => ServerProbe.IsSchemaLockHeldAsync(authDb, ""));
        }

        // CLI entry points null checks
        await Assert.ThrowsAsync<ArgumentNullException>(() => ContentImporterCli.RunAsync(null!, TextWriter.Null, TextWriter.Null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DbUpgradeCli.RunAsync(null!, new DatabaseOptions(), TextWriter.Null, TextWriter.Null, CancellationToken.None));
    }

    // =========================================================================
    // 5. ArcaneCore.AccountTool PasswordSource Exception Tests
    // =========================================================================

    [Fact]
    public void AccountTool_PasswordSource_ThrowsOnNullArguments_AndRejectsInvalidCombinations()
    {
        Assert.Throws<ArgumentNullException>(() => PasswordSource.Parse(null!, _ => null, false));
        Assert.Throws<ArgumentNullException>(() => PasswordSource.Parse(["create"], null!, false));
        Assert.Throws<ArgumentNullException>(() => PasswordSource.Read(null!, _ => null, TextReader.Null, _ => null));

        // 0 positional arguments -> invalid
        PasswordRequest emptyReq = PasswordSource.Parse(["create"], _ => null, false);
        Assert.False(emptyReq.IsValid);
        Assert.NotNull(emptyReq.Error);

        // > 2 positional arguments -> invalid
        PasswordRequest tooManyReq = PasswordSource.Parse(["create", "alice", "secret", "extra"], _ => null, false);
        Assert.False(tooManyReq.IsValid);

        // --password-stdin combined with argv password -> invalid
        PasswordRequest conflictReq = PasswordSource.Parse(["create", "alice", "secret", "--password-stdin"], _ => null, false);
        Assert.False(conflictReq.IsValid);
        Assert.Contains("--password-stdin", conflictReq.Error, StringComparison.Ordinal);

        // Prompt mismatch returns null from Read
        PasswordRequest promptReq = PasswordSource.Parse(["create", "alice"], _ => null, stdinRedirected: false);
        Assert.True(promptReq.IsValid);
        Assert.Equal(PasswordMode.Prompt, promptReq.Mode);
        int callCount = 0;
        string? mismatched = PasswordSource.Read(promptReq, _ => null, TextReader.Null, _ => ++callCount == 1 ? "first" : "second");
        Assert.Null(mismatched);
    }

    // =========================================================================
    // 6. Deterministic Seeded Fuzz Tests
    // =========================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_DbcFile_ParseAndAccessors_MutatedHeadersRecordsAndStringBlocks(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 250; i++)
        {
            int length = rng.Next(0, 256);
            byte[] data = new byte[length];
            rng.NextBytes(data);

            // Half the time, stamp valid WDBC magic and plausible header values with random mutations
            if (length >= DbcHeaderSize && rng.Next(2) == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), WdbcMagic);
                int recCount = rng.Next(-2, 8);
                int fldCount = rng.Next(-2, 8);
                int recSize = rng.Next(2) == 0 && fldCount >= 0 ? fldCount * 4 : rng.Next(-4, 40);
                int strSize = rng.Next(-2, 64);

                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), recCount);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), fldCount);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), recSize);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), strSize);
            }

            DbcFile? dbc = null;
            try
            {
                dbc = DbcFile.Parse(data);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            // If Parse succeeded, invariants on header fields must hold
            Assert.True(dbc.RecordCount >= 0);
            Assert.True(dbc.FieldCount >= 0);
            Assert.True(dbc.RecordSize >= dbc.FieldCount * 4);

            // Probe in-bounds and out-of-bounds record/field indices
            for (int probe = 0; probe < 12; probe++)
            {
                int r = rng.Next(-2, dbc.RecordCount + 2);
                int f = rng.Next(-2, dbc.FieldCount + 2);
                bool inBounds = (uint)r < (uint)dbc.RecordCount && (uint)f < (uint)dbc.FieldCount;

                if (!inBounds)
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetUInt32(r, f));
                    Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetInt32(r, f));
                    Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetFloat(r, f));
                    continue;
                }

                uint u = dbc.GetUInt32(r, f);
                int s = dbc.GetInt32(r, f);
                Assert.Equal(unchecked((int)u), s);
                _ = dbc.GetFloat(r, f);

                try
                {
                    string str = dbc.GetString(r, f);
                    Assert.NotNull(str);
                }
                catch (InvalidDataException)
                {
                    // Expected when string offset is out of range
                }

                try
                {
                    string strict = dbc.GetStringStrict(r, f);
                    Assert.NotNull(strict);
                }
                catch (InvalidDataException)
                {
                    // Expected when string offset is out of range, unterminated, or invalid UTF-8
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_DbcReaders_MutatedValidAndInvalidLayouts(int seed)
    {
        var rng = new Random(seed);
        int[] fieldCounts = [1, 2, 5, 7, 8, 9, 10, 12, 14, 15, 16, 18, 19, 21, 22, 24, 25, 29, 33, 35, 37, 39, 41, 42, 45];

        for (int i = 0; i < 120; i++)
        {
            int fields = fieldCounts[rng.Next(fieldCounts.Length)];
            int records = rng.Next(0, 6);
            int stringLen = rng.Next(1, 32);
            byte[] strBlock = new byte[stringLen];
            rng.NextBytes(strBlock);
            strBlock[0] = 0;
            if (rng.Next(4) != 0)
            {
                strBlock[^1] = 0; // Usually null-terminate the string block
            }

            byte[] raw = BuildSyntheticDbc(
                records,
                fields,
                fields * 4,
                strBlock,
                recSpan =>
                {
                    rng.NextBytes(recSpan);
                    // Clamp some string offsets or IDs into small values to exercise deeper reader logic
                    Span<uint> uints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(recSpan);
                    for (int idx = 0; idx < uints.Length; idx++)
                    {
                        if (rng.Next(3) == 0)
                        {
                            uints[idx] = (uint)rng.Next(0, Math.Max(1, stringLen));
                        }
                    }
                });

            DbcFile dbc = DbcFile.Parse(raw);

            InvokeDbcReader(() => ShapeshiftFormDbcReader.Read(dbc));
            InvokeDbcReader(() => CharacterAppearanceDbcReader.ReadSections(dbc));
            InvokeDbcReader(() => CharacterAppearanceDbcReader.ReadFacialHairStyles(dbc));
            InvokeDbcReader(() => CharStartOutfitDbcReader.Read(raw));
            InvokeDbcReader(() => ItemSetDbcReader.Build(dbc));
            InvokeDbcReader(() => AreaTableDbcReader.Read(dbc));
            InvokeDbcReader(() => AreaTriggerDbcReader.Read(dbc));
            InvokeDbcReader(() => MapDbcReader.Read(dbc));
            InvokeDbcReader(() => NamesDbcReader.Read(dbc, "fuzz"));
            InvokeDbcReader(() => CreatureFamilyDbcReader.ReadFoodMasks(dbc));
            InvokeDbcReader(() => CreatureFamilyDbcReader.ReadSkillLines(dbc));
            InvokeDbcReader(() => GameObjectDisplayInfoDbcReader.Read(dbc));
            InvokeDbcReader(() => TransportAnimationDbcReader.Read(dbc));
            InvokeDbcReader(() => EnchantDbcReader.Read(dbc));
            InvokeDbcReader(() => WorldSafeLocsDbcReader.Read(dbc));
            InvokeDbcReader(() => FactionTemplateDbcReader.Read(dbc));
            InvokeDbcReader(() => FactionDbcReader.Read(dbc));
            InvokeDbcReader(() => NpcServiceDbcReaders.ReadTaxiNodes(dbc));
            InvokeDbcReader(() => NpcServiceDbcReaders.ReadTaxiPaths(dbc));
            InvokeDbcReader(() => NpcServiceDbcReaders.ReadTaxiPathNodes(dbc));
            InvokeDbcReader(() => NpcServiceDbcReaders.ReadSkillLineAbilities(dbc));
            InvokeDbcReader(() => NpcServiceDbcReaders.ReadBankBagSlotPrices(dbc));
            InvokeDbcReader(() => SkillDbcReaders.ReadSkillLines(dbc));
            InvokeDbcReader(() => SkillDbcReaders.ReadSkillRaceClassInfos(dbc));
            InvokeDbcReader(() => SkillDbcReaders.ReadSkillTiers(dbc));
            InvokeDbcReader(() => ChatChannelsDbcReader.Read(dbc));
            InvokeDbcReader(() => EmoteDbcReaders.ReadText(dbc));
            InvokeDbcReader(() => EmoteDbcReaders.ReadEmotes(dbc));
            InvokeDbcReader(() => CinematicSequencesDbcReader.Read(dbc));
            InvokeDbcReader(() => SoundEntriesDbcReader.Read(dbc));
            InvokeDbcReader(() => SpellVisualEffectNameDbcReader.Read(dbc));
            InvokeDbcReader(() => SpellVisualKitDbcReader.Read(dbc));
            InvokeDbcReader(() => WorldStateUIDbcReader.Read(dbc));
            InvokeDbcReader(() => ZoneMusicDbcReader.Read(dbc));
        }

        static void InvokeDbcReader(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                // Expected domain validation exceptions on mutated DBC records/headers
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_MySqlDumpReader_RandomSqlTokensAndMutations(int seed)
    {
        var rng = new Random(seed);
        string[] tokens =
        [
            "CREATE", "TABLE", "INSERT", "INTO", "VALUES", "LOCK", "UNLOCK", "DROP", "UPDATE", "DELETE",
            "`creature_template`", "`item_template`", "`col_a`", "`col_b`", "``", "`unterminated",
            "(", ")", ",", ";", "'hello'", "'escaped\'quote'", "'unterminated", "NULL", "12345", "-99", "0.5",
            "-- line comment\n", "/* block comment */", "/*!40101 SET NAMES utf8 */;", "-", "/", "\n", " "
        ];

        for (int i = 0; i < 200; i++)
        {
            var sb = new StringBuilder();
            int tokenCount = rng.Next(1, 35);
            for (int t = 0; t < tokenCount; t++)
            {
                sb.Append(tokens[rng.Next(tokens.Length)]);
                if (rng.Next(3) != 0)
                {
                    sb.Append(' ');
                }
            }

            var limits = new MySqlDumpLimits
            {
                MaxColumns = rng.Next(1, 64),
                MaxStatementChars = rng.Next(32, 4096),
                MaxTokenChars = rng.Next(8, 512),
            };

            try
            {
                var reader = new MySqlDumpReader(new StringReader(sb.ToString()), limits);
                foreach (object item in reader.Read())
                {
                    switch (item)
                    {
                        case DumpTable table:
                            Assert.NotEmpty(table.Name);
                            Assert.NotNull(table.Columns);
                            break;
                        case DumpRow row:
                            Assert.NotEmpty(row.Table);
                            Assert.NotNull(row.Columns);
                            Assert.NotNull(row.Values);
                            break;
                    }
                }
            }
            catch (FormatException)
            {
                // Expected for malformed fuzzed SQL syntax or limit violations
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_VMangosMigrationReader_RandomDmlAndDdlStatements(int seed)
    {
        var rng = new Random(seed);
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "player_levelstats",
            "player_classlevelstats",
            "player_xp_for_level",
        };

        string[] statements =
        [
            "UPDATE `player_levelstats` SET `str` = 20, `agi` = 15 WHERE `race` = 1 AND `class` = 1 AND `level` = 1;",
            "UPDATE `player_classlevelstats` SET `basehp` = 100 WHERE `class` IN (1, 2) AND `level` = 5;",
            "REPLACE INTO `player_xp_for_level` (`lvl`, `xp_for_next_level`) VALUES (1, 400), (2, 900);",
            "INSERT INTO `player_levelstats` (`race`, `class`, `level`, `str`, `agi`, `sta`, `inte`, `spi`) VALUES (1, 1, 1, 20, 20, 20, 20, 20);",
            "UPDATE `untracked_table` SET `foo` = 'bar' WHERE `id` = 1;",
            "DELETE FROM `untracked_table` WHERE `id` = 1;",
            "DELETE FROM `player_levelstats` WHERE `level` = 1;",
            "DROP TABLE `player_levelstats`;",
            "ALTER TABLE `player_classlevelstats` ADD COLUMN `foo` INT;",
            "TRUNCATE TABLE `player_xp_for_level`;",
            "UPDATE `player_levelstats` SET `str` = `str` + 1 WHERE `level` = 1;",
            "INSERT INTO `player_xp_for_level` VALUES (1, 400);",
            "/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE */;",
            "-- comment line\n",
        ];

        for (int i = 0; i < 150; i++)
        {
            var sb = new StringBuilder();
            int count = rng.Next(1, 8);
            for (int c = 0; c < count; c++)
            {
                string stmt = statements[rng.Next(statements.Length)];
                if (rng.Next(6) == 0 && stmt.Length > 8)
                {
                    // Truncate or corrupt statement randomly
                    stmt = stmt[..rng.Next(1, stmt.Length)];
                }

                sb.AppendLine(stmt);
            }

            try
            {
                IReadOnlyList<MigrationStatement> parsed =
                    VMangosMigrationReader.Parse(new StringReader(sb.ToString()), $"fuzz_{i}.sql", tracked);
                Assert.NotNull(parsed);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidDataException)
            {
                // Expected when fuzzed migration hits unsupported DML/DDL or malformed SQL
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_DumpImportersAndContentScanner_RandomDumps(int seed)
    {
        var rng = new Random(seed);
        string[] tables = ["spell_proc_event", "spell_threat", "game_weather", "exploration_basexp", "creature_template", "db_version"];
        string[] cols = ["entry", "SchoolMask", "SpellFamilyName", "SpellFamilyMask0", "ppmRate", "Cooldown", "build_min", "build_max", "Threat", "zone", "level", "basexp", "Entry", "Name", "ModelId1", "MinLevel", "version"];
        string[] vals = ["0", "1", "2", "5875", "-1", "100", "2.5", "'text'", "NULL", "'bad_number'"];

        for (int i = 0; i < 120; i++)
        {
            string table = tables[rng.Next(tables.Length)];
            int colCount = rng.Next(1, 6);
            var selectedCols = new List<string>(colCount);
            for (int c = 0; c < colCount; c++)
            {
                selectedCols.Add(cols[rng.Next(cols.Length)]);
            }

            var sb = new StringBuilder();
            sb.Append($"CREATE TABLE `{table}` (");
            sb.Append(string.Join(", ", selectedCols.Distinct().Select(c => $"`{c}` int")));
            sb.AppendLine(");");

            int rowCount = rng.Next(1, 5);
            sb.Append($"INSERT INTO `{table}` (");
            sb.Append(string.Join(", ", selectedCols.Select(c => $"`{c}`")));
            sb.Append(") VALUES ");
            for (int r = 0; r < rowCount; r++)
            {
                if (r > 0)
                {
                    sb.Append(", ");
                }

                sb.Append('(');
                for (int c = 0; c < colCount; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(vals[rng.Next(vals.Length)]);
                }

                sb.Append(')');
            }

            sb.AppendLine(";");
            string dumpSql = sb.ToString();

            try
            {
                ScanResult scan = ContentScanner.Scan([DumpInput.Text($"fuzz_{i}.sql", dumpSql)]);
                Assert.NotNull(scan);
            }
            catch (Exception ex) when (ex is FormatException or ImportSchemaException or InvalidDataException)
            {
            }

            try
            {
                _ = SpellProcEventDumpImporter.Parse(
                    new StringReader(dumpSql),
                    build: 5875,
                    cooldownUnit: rng.Next(2) == 0 ? ProcCooldownUnit.Milliseconds : ProcCooldownUnit.Seconds);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
            }

            try
            {
                _ = SpellThreatDumpImporter.Parse(new StringReader(dumpSql), build: 5875);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
            }

            try
            {
                _ = WorldStateDumpImporter.Parse(new StringReader(dumpSql));
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_AccountToolPasswordSource_RandomCliArgsAndInputs(int seed)
    {
        var rng = new Random(seed);
        string[] tokens = ["create", "set-password", "alice", "bob", "--password-stdin", "p@ssw0rd", "", "   ", "--unknown-flag", "extra"];

        for (int i = 0; i < 250; i++)
        {
            int argCount = rng.Next(0, 6);
            string[] args = new string[argCount];
            for (int a = 0; a < argCount; a++)
            {
                args[a] = tokens[rng.Next(tokens.Length)];
            }

            string? envValue = rng.Next(3) switch
            {
                0 => null,
                1 => "",
                _ => $"env_secret_{rng.Next(1000)}",
            };
            bool stdinRedirected = rng.Next(2) == 0;

            PasswordRequest request = PasswordSource.Parse(
                args,
                key => key == PasswordSource.EnvironmentVariable ? envValue : null,
                stdinRedirected);

            int positionalCount = args.Skip(1).Count(x => x != PasswordSource.StdinFlag);
            bool hasStdinFlag = args.Skip(1).Any(x => x == PasswordSource.StdinFlag);

            if (positionalCount is 0 or > 2 || (positionalCount == 2 && hasStdinFlag))
            {
                Assert.False(request.IsValid);
                Assert.NotNull(request.Error);
                continue;
            }

            Assert.True(request.IsValid);
            Assert.Null(request.Error);

            string stdinLine = rng.Next(3) switch
            {
                0 => "",
                1 => "stdin_pass\r\n",
                _ => "stdin_pass\n",
            };
            bool promptMatches = rng.Next(2) == 0;
            int promptCall = 0;

            string? resolved = PasswordSource.Read(
                request,
                key => key == PasswordSource.EnvironmentVariable ? envValue : null,
                new StringReader(stdinLine),
                _ =>
                {
                    promptCall++;
                    return promptMatches ? "prompt_pass" : $"mismatch_{promptCall}";
                });

            if (resolved is not null)
            {
                Assert.NotEmpty(resolved);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_ContentImporterCliAndDbUpgradeCli_RandomArgvAsync(int seed)
    {
        var rng = new Random(seed);
        string[] contentTokens =
        [
            "plan", "import", "import-dbc", "import-map-dbc", "verify", "class-masks", "proc-events", "refresh", "refresh-portals",
            "unknown-cmd", "--dialect", "auto", "cmangos", "vmangos", "bad-dialect", "--quest-xp", "derived", "none", "bad-xp",
            "--cooldown-unit", "ms", "seconds", "bad-unit", "--database", "nonexistent.db", "--provider", "sqlite", "bad-provider",
            "--connection-string", "Data Source=:memory:", "--dry-run", "--replace", "--verbose", "--unknown-opt", "missing_file.sql"
        ];

        string[] upgradeTokens =
        [
            "status", "plan", "check", "upgrade", "migrate-codex", "backup-info", "dbc", "export-updates", "unknown-cmd",
            "--component", "auth", "characters", "world", "all", "invalid-comp",
            "--lock-timeout", "0", "60", "999999", "not-an-int",
            "--confirm-backup", "--allow-active-sessions", "--script", "--json", "--no-fail-on-pending", "--apply",
            "--script=bad", "--unknown-flag", "positional-extra"
        ];

        var emptyDbOptions = new DatabaseOptions { ConnectionString = "" };

        for (int i = 0; i < 120; i++)
        {
            int cLen = rng.Next(0, 7);
            string[] cArgs = new string[cLen];
            for (int j = 0; j < cLen; j++)
            {
                cArgs[j] = contentTokens[rng.Next(contentTokens.Length)];
            }

            int contentExit = await ContentImporterCli.RunAsync(cArgs, TextWriter.Null, TextWriter.Null, CancellationToken.None);
            Assert.InRange(contentExit, 0, 10);

            int uLen = rng.Next(0, 7);
            string[] uArgs = new string[uLen];
            for (int j = 0; j < uLen; j++)
            {
                uArgs[j] = upgradeTokens[rng.Next(upgradeTokens.Length)];
            }

            int upgradeExit = await DbUpgradeCli.RunAsync(uArgs, emptyDbOptions, TextWriter.Null, TextWriter.Null, CancellationToken.None);
            Assert.InRange(upgradeExit, 0, 10);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_SchemaSnapshotAndConnectionStringRedactor_RandomJsonAndSecrets(int seed)
    {
        var rng = new Random(seed);
        string[] jsonFragments =
        [
            "{", "}", "[", "]", "\"component\":\"auth\"", "\"component\":\"",
            "\"version\":1", "\"version\":0", "\"version\":-5", "\"providers\":{}", "\"files\":{}", "\"seeds\":[]",
            "\"tables\":[]", "null", "true", "123", "\"random\"", ",", ":"
        ];

        for (int i = 0; i < 150; i++)
        {
            var sb = new StringBuilder();
            int parts = rng.Next(1, 14);
            for (int p = 0; p < parts; p++)
            {
                sb.Append(jsonFragments[rng.Next(jsonFragments.Length)]);
            }

            try
            {
                SchemaSnapshot snapshot = SchemaSnapshot.Parse(sb.ToString());
                Assert.NotNull(snapshot);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                // Expected for malformed or empty JSON snapshots
            }

            // Also verify ConnectionStringRedactor never leaks fuzzed secret values
            string secret = $"Sup3rS3cret_{rng.Next(100_000, 999_999)}!";
            string connStr = $"Server=127.0.0.1;Database=arcane;Uid=root;Pwd={secret};";
            string redacted = ConnectionStringRedactor.Redact(connStr);
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);

            string freeText = $"Connection failed for '{connStr}' with password={secret}";
            string scrubbed = ConnectionStringRedactor.Scrub(freeText, connStr);
            Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        }
    }
}
