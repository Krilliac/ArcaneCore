using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The creature behaviour world-schema step (<see cref="CreatureBehaviourDataModule"/>) and the importer fidelity it
/// carries: full-width EventAI flags, parameters 5 and 6, guid-keyed rows, broadcast text, EventAI summon
/// locations, the template detection / call-for-help / leash columns and the per-dialect ExtraFlags decode.
/// Rows are hand-written in the cmangos-classic and vmangos column layouts (mangos.sql creature_template /
/// creature_ai_scripts / creature_ai_summons, classic-db broadcast_text); no GPL rows are copied.
/// </summary>
public sealed class CreatureBehaviourImportTests : IAsyncLifetime
{
    private const string ScriptColumns =
        "`id`,`creature_id`,`event_type`,`event_inverse_phase_mask`,`event_chance`,`event_flags`,`event_param1`,`event_param2`,`event_param3`,`event_param4`,`event_param5`,`event_param6`,"
        + "`action1_type`,`action1_param1`,`action1_param2`,`action1_param3`,`action2_type`,`action2_param1`,`action2_param2`,`action2_param3`,`action3_type`,`action3_param1`,`action3_param2`,`action3_param3`,`comment`";

    private const string CMangosDump = $"""
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`ExtraFlags`,`StaticFlags1`,`StaticFlags2`,`Detection`,`CallForHelp`,`Pursuit`,`Leash`,`Timeout`,`Civilian`,`AIName`) VALUES
        (920001,'Behaviour Boar','',5,5,32,1,134217728,0,10,8,15000,115,3000,0,'EventAI'),
        (920002,'Plain Rat','',1,1,32,65538,0,2,18,0,0,0,0,1,'');
        INSERT INTO `creature_ai_scripts` ({ScriptColumns}) VALUES
        (9200101,920001,33,0,100,1025,0,0,1800,9800,5,7,11,7159,1,0,0,0,0,0,0,0,0,0,'Boar - Facing Target Back'),
        (9200102,920001,2,0,100,1024,15,0,0,0,0,0,25,0,0,0,1,91150,0,0,22,1,0,0,'Boar - Flee at 15%'),
        (9200103,-4711,4,0,150,0,0,0,0,0,0,0,1,91151,0,0,0,0,0,0,0,0,0,0,'Guid scoped - Say on Aggro'),
        (9200104,920001,6,0,0,0,0,0,0,0,0,0,12,920002,0,60000,0,0,0,0,0,0,0,0,'Boar - never');
        INSERT INTO `creature_ai_summons` (`id`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecs`,`comment`) VALUES
        (1,10.5,-20.25,30,1.5,180000,'Boar - Rat on death'),(2,1,2,3,0,86400000,'second');
        INSERT INTO `broadcast_text` (`Id`,`Text`,`Text1`,`ChatTypeID`,`LanguageID`,`ConditionID`,`EmotesID`,`Flags`,`SoundEntriesID1`,`SoundEntriesID2`,`EmoteID1`,`EmoteID2`,`EmoteID3`,`EmoteDelay1`,`EmoteDelay2`,`EmoteDelay3`,`VerifiedBuild`) VALUES
        (91150,'%s squeals and bolts for cover!','%s squeals shrilly!',2,0,0,0,1,1234,0,431,0,0,0,0,0,5875),
        (91151,'Who goes there?','',0,7,0,0,0,0,0,0,0,0,0,0,0,5875);
        """;

    private const string VMangosDump = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`faction`,`detection_range`,`call_for_help_range`,`leash_range`,`static_flags1`,`static_flags2`,`flags_extra`) VALUES
        (920010,0,'Vmangos Boar','',10,10,14,10,5,115,134217728,2,1),
        (920011,0,'Vmangos Walker','',10,10,14,18,0,0,0,0,65632);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static CreatureDumpImporter Import(string dump, CreatureExtraFlagsDialect dialect = CreatureExtraFlagsDialect.Unknown)
    {
        var importer = new CreatureDumpImporter { ExtraFlagsDialect = dialect };
        importer.Read(new StringReader(dump));
        return importer;
    }

    private static async Task<CreatureContent> ImportAndLoadAsync(DatabaseConnectionOptions cs, string dump)
    {
        CreatureDumpImporter importer = Import(dump);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: false);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        return await new EfCreatureDataStore(read).LoadAsync();
    }

    // --- schema ----------------------------------------------------------------------------

    [Fact]
    public void WorldStep_IsTheBehaviourChanges()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureBehaviourDataModule.Version);
        Assert.Equal(["broadcast_text", "creature_ai_summons"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(
            [
                ("creature_ai_scripts", "CreatureGuid"), ("creature_ai_scripts", "EventFlags32"),
                ("creature_ai_scripts", "EventParam5"), ("creature_ai_scripts", "EventParam6"),
                ("creature_ai_texts", "Sound"), ("creature_ai_texts", "BroadcastTextId"),
                ("creature_template", "Detection"), ("creature_template", "CallForHelp"), ("creature_template", "Pursuit"),
                ("creature_template", "Leash"), ("creature_template", "Timeout"), ("creature_template", "StaticFlags1"),
                ("creature_template", "StaticFlags2"), ("creature_template", "ExtraFlagsDialect"),
            ],
            step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= CreatureBehaviourDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureBehaviourDataModule);
    }

    // --- EventAI rows ------------------------------------------------------------------------

    [Fact]
    public void Importer_KeepsFullWidthEventFlags_AndParameters5And6()
    {
        // cmangos reads event_flags with GetUInt32 and event_param1-6 (CreatureEventAIMgr.cpp:211-269); classic-db
        // rows carry 1024/1025 (EFLAG_COMBAT_ACTION), which a byte column turned into 255.
        CreatureDumpImporter importer = Import(CMangosDump);
        CreatureAiScriptRow facing = importer.AiSnapshot().Scripts.Single(s => s.Id == 9200101);
        Assert.Equal((1025u, 5, 7), (facing.EventFlags32, facing.EventParam5, facing.EventParam6));
        CreatureAiScriptRow flee = importer.AiSnapshot().Scripts.Single(s => s.Id == 9200102);
        Assert.Equal(1024u, flee.EventFlags32);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EventAi_RoundTripsFlags_Parameters_AndThreeActionRowsStayNonRandom(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), CMangosDump);

        IReadOnlyList<CreatureAiEvent> events = content.Ai.GetEvents(920001);
        CreatureAiEvent facing = events.Single(e => e.Id == 9200101);
        Assert.Equal((1025u, 5, 7), (facing.Flags, facing.Param5, facing.Param6));

        // Flags 1025 = repeatable | combat action. The old clamp produced 255, which also set RANDOM_ACTION (0x20).
        CreatureAiEvent flee = events.Single(e => e.Id == 9200102);
        Assert.Equal(1024u, flee.Flags);
        Assert.Equal(0u, flee.Flags & 0x20);
        Assert.Equal(0u, facing.Flags & 0x20);
        Assert.Equal(new CreatureAiAction(25, 0, 0, 0), flee.Action1);
        Assert.Equal(new CreatureAiAction(1, 91150, 0, 0), flee.Action2);
        Assert.Equal(new CreatureAiAction(22, 1, 0, 0), flee.Action3);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NegativeCreatureId_IsAGuidKeyedRow_NotCreatureZero(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), CMangosDump);

        CreatureAiEvent row = Assert.Single(content.Ai.GetGuidEvents(4711));
        Assert.Equal((9200103u, 0u, 4711u), (row.Id, row.CreatureId, row.CreatureGuid));
        Assert.Empty(content.Ai.GetEvents(0));
        Assert.DoesNotContain(content.Ai.GetEvents(920001), e => e.Id == 9200103);
    }

    [Fact]
    public void ChanceAbove100_IsAdjustedTo100_AndZeroChanceIsReported()
    {
        // cmangos CreatureEventAIMgr.cpp:272-279.
        CreatureDumpImporter importer = Import(CMangosDump);
        Assert.Equal((byte)100, importer.AiSnapshot().Scripts.Single(s => s.Id == 9200103).EventChance);
        Assert.Equal((byte)0, importer.AiSnapshot().Scripts.Single(s => s.Id == 9200104).EventChance);
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("9200103", StringComparison.Ordinal) && w.Contains("100", StringComparison.Ordinal));
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("9200104", StringComparison.Ordinal));
    }

    // --- broadcast text and summons ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task BroadcastText_ImportsAndResolvesPositiveEventAiTextIds(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), CMangosDump);

        BroadcastText flee = content.Ai.BroadcastTexts.Find(91150)!;
        Assert.Equal(("%s squeals and bolts for cover!", "%s squeals shrilly!", (byte)2, (byte)0), (flee.Text, flee.FemaleText, flee.ChatType, flee.Language));
        Assert.Equal((1234u, 431u), (flee.SoundId, flee.Emote));
        Assert.Equal(2, content.Ai.BroadcastTexts.Count);

        // A positive text id in an EventAI action is a broadcast text id (every text action in classic-db is).
        CreatureAiText text = content.Ai.FindText(91150)!;
        Assert.Equal(("%s squeals and bolts for cover!", (byte)2, 431u, 1234u), (text.Content, text.Type, text.Emote, text.Sound));
        Assert.Equal((byte)0, content.Ai.FindText(91151)!.Type);
        Assert.Equal(7u, content.Ai.FindText(91151)!.Language);
        Assert.Null(content.Ai.FindText(99999));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AiSummons_RoundTrip(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), CMangosDump);
        Assert.Equal(2, content.Ai.SummonCount);
        Assert.Equal(new CreatureAiSummon(1, 10.5f, -20.25f, 30f, 1.5f, 180000), content.Ai.FindSummon(1));
        Assert.Null(content.Ai.FindSummon(3));
    }

    [Fact]
    public void EmptyAiTextsTable_IsNotAnError()
    {
        // classic-db z2815 ships creature_ai_texts with zero rows: the texts live in broadcast_text.
        CreatureDumpImporter importer = Import("""
            CREATE TABLE `creature_ai_texts` (
              `entry` mediumint NOT NULL,
              `content_default` text NOT NULL,
              `sound` mediumint unsigned NOT NULL DEFAULT '0',
              `type` tinyint unsigned NOT NULL DEFAULT '0',
              `language` tinyint unsigned NOT NULL DEFAULT '0',
              `emote` smallint unsigned NOT NULL DEFAULT '0',
              `broadcast_text_id` int NOT NULL DEFAULT '0',
              PRIMARY KEY (`entry`)
            );
            LOCK TABLES `creature_ai_texts` WRITE;
            UNLOCK TABLES;
            """);
        CreatureImportReport report = importer.BuildReport();
        Assert.Equal(0, report.AiTexts);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void AiTexts_KeepSoundAndBroadcastId_AndPositiveEntriesStillSkipped()
    {
        CreatureDumpImporter importer = Import("""
            INSERT INTO `creature_ai_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`broadcast_text_id`) VALUES
            (-920301,'Legacy line',77,1,0,5,91151),(920302,'positive entry',0,0,0,0,0);
            """);
        CreatureAiTextRow text = Assert.Single(importer.AiSnapshot().Texts);
        Assert.Equal((-920301, 77u, 91151u), (text.Entry, text.Sound, text.BroadcastTextId));
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("920302", StringComparison.Ordinal));
    }

    // --- template behaviour columns ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CMangosTemplate_CarriesDetectionLeashAndDecodedFlags(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), CMangosDump);

        CreatureTemplate boar = content.FindTemplate(920001)!;
        Assert.Equal((10f, 8f, 15000u, 115f, 3000u), (boar.Detection, boar.CallForHelp, boar.Pursuit, boar.Leash, boar.Timeout));
        Assert.Equal(CreatureExtraFlagsDialect.CMangos, boar.ExtraFlagsDialect);

        // cmangos ExtraFlags 0x01 = INSTANCE_BIND (Creature.h:49); it is not vmangos NO_LEASH_EVADE.
        Assert.True(boar.Behaviour.HasFlag(CreatureBehaviourFlags.InstanceBind));
        Assert.False(boar.Behaviour.HasFlag(CreatureBehaviourFlags.NoLeashEvade));
        Assert.True(boar.Behaviour.HasFlag(CreatureBehaviourFlags.CallsGuards)); // static flag 0x08000000

        // 65538 = 0x10002: NO_AGGRO_ON_SIGHT | CIVILIAN in cmangos; static flags 2 = FORCE_PARTY_MEMBERS_INTO_COMBAT.
        CreatureTemplate rat = content.FindTemplate(920002)!;
        Assert.True(rat.Behaviour.HasFlag(CreatureBehaviourFlags.NoAggro));
        Assert.True(rat.Behaviour.HasFlag(CreatureBehaviourFlags.Civilian));
        Assert.False(rat.Behaviour.HasFlag(CreatureBehaviourFlags.NoAssist));
        Assert.True(rat.Behaviour.HasFlag(CreatureBehaviourFlags.AggroZone));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VMangosTemplate_ReadsDetectionRangeAndDecodesItsOwnFlagDialect(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), VMangosDump);

        CreatureTemplate boar = content.FindTemplate(920010)!;
        Assert.Equal((10f, 5f, 115f), (boar.Detection, boar.CallForHelp, boar.Leash));
        Assert.Equal(CreatureExtraFlagsDialect.VMangos, boar.ExtraFlagsDialect);

        // vmangos flags_extra 0x01 = NO_LEASH_EVADE (CreatureDefines.h:157); it is not cmangos INSTANCE_BIND.
        Assert.True(boar.Behaviour.HasFlag(CreatureBehaviourFlags.NoLeashEvade));
        Assert.False(boar.Behaviour.HasFlag(CreatureBehaviourFlags.InstanceBind));

        // 65632 = 0x10060: NO_ASSIST | ALWAYS_RUN | NO_MOVEMENT_PAUSE in vmangos; it is not cmangos CIVILIAN.
        Behaviour(content, 920011, CreatureBehaviourFlags.NoAssist, CreatureBehaviourFlags.AlwaysRun, CreatureBehaviourFlags.NoMovementPause);
        Assert.False(content.FindTemplate(920011)!.Behaviour.HasFlag(CreatureBehaviourFlags.Civilian));
    }

    private static void Behaviour(CreatureContent content, uint entry, params CreatureBehaviourFlags[] expected)
    {
        CreatureBehaviourFlags actual = content.FindTemplate(entry)!.Behaviour;
        foreach (CreatureBehaviourFlags flag in expected)
        {
            Assert.True(actual.HasFlag(flag), $"{entry}: expected {flag} in {actual}");
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ABareTemplateWithoutDetectionColumn_GetsTheVmangosDefault18(DatabaseProvider provider)
    {
        // vmangos CreatureDefines.h:250 detection_range = 18.0f; classic-db Detection default 18.
        const string bare = "INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (920020,'Bare',1,1);";
        Assert.Null(Assert.Single(Import(bare).Snapshot().Templates).Detection);

        CreatureContent content = await ImportAndLoadAsync(await _databases.CreateAsync(provider), bare);
        CreatureTemplate template = content.FindTemplate(920020)!;
        Assert.Equal((18f, 0f, 0f), (template.Detection, template.CallForHelp, template.Leash));
    }

    [Fact]
    public void AVMangosTemplateWithoutTheCallForHelpColumn_GetsTheVmangosDefault5_AndACMangosOneKeeps0()
    {
        // vmangos sql/old_migrations/20190123062532_world.sql:28: call_for_help_range FLOAT NOT NULL DEFAULT '5' (Creature.cpp:261 also
        // starts m_callForHelpDist at 5); cmangos mangos.sql:1258: CallForHelp DEFAULT '0'. An explicit 0 stays 0 in both dialects.
        const string vmangosBare = "INSERT INTO `creature_template` (`entry`,`patch`,`name`,`level_min`,`level_max`) VALUES (920040,0,'Bare Vmangos',1,1);";
        const string vmangosZero = "INSERT INTO `creature_template` (`entry`,`patch`,`name`,`level_min`,`level_max`,`call_for_help_range`) VALUES (920041,0,'Zero Vmangos',1,1,0);";
        const string cmangosBare = "INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (920042,'Bare Cmangos',1,1);";

        Assert.Equal(5f, Assert.Single(Import(vmangosBare).Snapshot().Templates).CallForHelp);
        Assert.Equal(0f, Assert.Single(Import(vmangosZero).Snapshot().Templates).CallForHelp);
        Assert.Equal(0f, Assert.Single(Import(cmangosBare).Snapshot().Templates).CallForHelp);
    }

    [Fact]
    public void ExtraFlagsDialect_CanBeForcedAtImportTime()
    {
        const string noFlagColumn = "INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`,`flags_extra`) VALUES (920030,'Forced',1,1,1);";
        CreatureTemplateRow auto = Assert.Single(Import(noFlagColumn).Snapshot().Templates);
        Assert.Equal((byte)CreatureExtraFlagsDialect.VMangos, auto.ExtraFlagsDialect);

        CreatureTemplateRow forced = Assert.Single(Import(noFlagColumn, CreatureExtraFlagsDialect.CMangos).Snapshot().Templates);
        Assert.Equal((byte)CreatureExtraFlagsDialect.CMangos, forced.ExtraFlagsDialect);
    }

    [Fact]
    public void MixedDumpDialects_AreStillRejected()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`) VALUES (1,'cmangos');"));
        Assert.Throws<FormatException>(() => importer.Read(new StringReader("INSERT INTO `creature_template` (`entry`,`patch`,`name`,`level_min`) VALUES (2,0,'vmangos',1);")));
    }

    // --- flag decode table (pure) ---------------------------------------------------------------

    [Theory]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x1u, CreatureBehaviourFlags.InstanceBind)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x2u, CreatureBehaviourFlags.NoAggro)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x8u, CreatureBehaviourFlags.NoParryHasten)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x20u, CreatureBehaviourFlags.RunDuringWander)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x200u, CreatureBehaviourFlags.AggroZone)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x400u, CreatureBehaviourFlags.Guard)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x800u, CreatureBehaviourFlags.NoCallAssist)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x10000u, CreatureBehaviourFlags.Civilian)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x20000u, CreatureBehaviourFlags.NoMelee)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x1u, CreatureBehaviourFlags.NoLeashEvade)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x2u, CreatureBehaviourFlags.NoAggro)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x8u, CreatureBehaviourFlags.NoUnreachableEvade)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x20u, CreatureBehaviourFlags.NoMovementPause)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x40u, CreatureBehaviourFlags.AlwaysRun)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x400u, CreatureBehaviourFlags.Guard)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x800u, CreatureBehaviourFlags.NoThreatList)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x1000u, CreatureBehaviourFlags.KeepPositiveAurasOnEvade)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x10000u, CreatureBehaviourFlags.NoAssist)]
    [InlineData(CreatureExtraFlagsDialect.VMangos, 0x20000u, CreatureBehaviourFlags.NoTarget)]
    [InlineData(CreatureExtraFlagsDialect.Unknown, 0x2u, CreatureBehaviourFlags.NoAggro)]
    [InlineData(CreatureExtraFlagsDialect.Unknown, 0x400u, CreatureBehaviourFlags.Guard)]
    public void ExtraFlagBits_DecodePerDialect(CreatureExtraFlagsDialect dialect, uint bits, CreatureBehaviourFlags expected)
        => Assert.Equal(expected, CreatureBehaviour.Normalize(dialect, bits, 0, 0, civilian: false));

    [Theory]
    [InlineData(CreatureExtraFlagsDialect.Unknown, 0x1u)]
    [InlineData(CreatureExtraFlagsDialect.Unknown, 0x20u)]
    [InlineData(CreatureExtraFlagsDialect.Unknown, 0x40u)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x40u)]
    [InlineData(CreatureExtraFlagsDialect.CMangos, 0x100u)]
    public void BitsWithoutAMeaningInTheDialect_DecodeToNothing(CreatureExtraFlagsDialect dialect, uint bits)
        => Assert.Equal(CreatureBehaviourFlags.None, CreatureBehaviour.Normalize(dialect, bits, 0, 0, civilian: false));

    [Fact]
    public void StaticFlags_AreDialectNeutral_AndCivilianColumnSetsCivilian()
    {
        const uint all1 = 0x100 | 0x400 | 0x100000 | 0x02000000 | 0x04000000 | 0x08000000;
        CreatureBehaviourFlags decoded = CreatureBehaviour.Normalize(CreatureExtraFlagsDialect.VMangos, 0, all1, 0x2, civilian: true);
        Assert.Equal(
            CreatureBehaviourFlags.Sessile | CreatureBehaviourFlags.NoAutomaticRegen | CreatureBehaviourFlags.NoMeleeFlee
            | CreatureBehaviourFlags.IgnoreCombat | CreatureBehaviourFlags.OnlyAttackPvpEnabling | CreatureBehaviourFlags.CallsGuards
            | CreatureBehaviourFlags.AggroZone | CreatureBehaviourFlags.Civilian,
            decoded);
    }

    // --- upgrade ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsTheBehaviourColumns_KeepingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1, Name = "Before" });
            db.Set<CreatureAiScriptRow>().Add(new CreatureAiScriptRow { Id = 5, CreatureId = 1, EventType = 4, EventFlags = 1, EventChance = 100 });
            await db.SaveChangesAsync();

            // Recreate a database from before this step: none of its tables or columns, version below it.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            List<string> ddl =
            [
                $"DROP TABLE {sql.DelimitIdentifier("broadcast_text")}",
                $"DROP TABLE {sql.DelimitIdentifier("creature_ai_summons")}",
            ];
            foreach (AddColumnChange change in WorldDbContext.Schema.Steps.Single(s => s.Version == CreatureBehaviourDataModule.Version).Changes.OfType<AddColumnChange>())
            {
                ddl.Add($"ALTER TABLE {sql.DelimitIdentifier(change.Table)} DROP COLUMN {sql.DelimitIdentifier(change.Column)}");
            }

            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, CreatureBehaviourDataModule.Version - 1));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            CreatureTemplate before = content.FindTemplate(1)!;

            // Rows from before the step: Detection was never stored, so the vmangos default applies; the dialect is unknown.
            Assert.Equal(("Before", 18f, CreatureExtraFlagsDialect.Unknown), (before.Name, before.Detection, before.ExtraFlagsDialect));

            // The world-8 byte flag still counts when the new column is empty.
            CreatureAiEvent row = Assert.Single(content.Ai.GetEvents(1));
            Assert.Equal(1u, row.Flags);
            Assert.Equal(0, content.Ai.BroadcastTexts.Count);
        }
    }

    // --- real dump (opt-in) -----------------------------------------------------------------------

    /// <summary>
    /// With <c>ARCANECORE_CLASSICDB_DUMP</c> set to the classic-db z2815 dump (.sql or .sql.gz) this imports the
    /// whole creature AI data and asserts the counts (reported as skipped when the variable is unset; it fails
    /// when the variable is set and the file is missing).
    /// </summary>
    [ClassicDbDumpFact]
    public async Task RealClassicDbDump_ImportsAllAiRows()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{ClassicDbDumpFactAttribute.Variable} is set but '{path}' does not exist");

        var importer = new CreatureDumpImporter();
        await using FileStream file = File.OpenRead(path);
        await using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        importer.Read(reader);

        CreatureImportReport report = importer.BuildReport();
        Assert.Equal(10843, report.AiEvents);
        Assert.Equal(11104, report.BroadcastTexts);
        Assert.Equal(10384, report.Templates);
        Assert.Equal(38, report.AiSummons);
        Assert.True(report.ScriptTexts > 0);
        Assert.Equal(report.ScriptTexts, report.AiTexts); // z2815 creature_ai_texts is empty
        Assert.Contains(importer.AiSnapshot().Texts, t => t.Entry == -1036000 && t.Content.Contains("noise", StringComparison.Ordinal));
        Assert.Contains(importer.PathSnapshot(), p => p.Entry == 3849 && p.PathId == 0x8000_0000u && p.Point == 12);
        // Disciple of Naralex has no script_waypoint rows: ScriptDev2 starts his escort on waypoint_path 3678 (79 points, 1 s stops at the
        // script's event points 12, 30 and 70), which imports under entry 0 (CreatureContent.WaypointPathBit).
        Assert.DoesNotContain(importer.PathSnapshot(), p => p.Entry == 3678 && (p.PathId & 0x8000_0000u) != 0);
        Assert.Equal(5393, report.WaypointPaths);
        Assert.Equal(170, importer.PathSnapshot().Where(p => p.Entry == CreatureContent.WaypointPathEntry).Select(p => p.PathId).Distinct().Count());
        CreatureMovementTemplateRow[] naralex = [.. importer.PathSnapshot()
            .Where(p => p.Entry == CreatureContent.WaypointPathEntry && p.PathId == (CreatureContent.WaypointPathBit | 3678u)).OrderBy(p => p.Point)];
        Assert.Equal(79, naralex.Length);
        Assert.Equal((1u, 79u), (naralex[0].Point, naralex[^1].Point));
        Assert.Equal((13_000u, 1_000u, 1_000u, 1_000u), (naralex[0].WaitTimeMs, naralex[11].WaitTimeMs, naralex[29].WaitTimeMs, naralex[69].WaitTimeMs));
        Assert.Equal((12u, 30u, 70u), (naralex[11].Point, naralex[29].Point, naralex[69].Point));

        // Almost every row carries 1024/1025 and 39 rows are keyed by spawn guid.
        IReadOnlyCollection<CreatureAiScriptRow> scripts = importer.AiSnapshot().Scripts;
        Assert.True(scripts.Count(s => s.EventFlags32 is 1024 or 1025) > 6000);
        Assert.Equal(39, scripts.Count(s => s.CreatureGuid != 0));
        Assert.Equal(0, scripts.Count(s => s.CreatureId == 0 && s.CreatureGuid == 0));
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

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
