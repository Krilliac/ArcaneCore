using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Skills;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Data.Tests.Skills;

/// <summary>
/// Synthetic build-5875 images for the skill DBC readers and the content catalog built from them.
/// Layouts and rules: vmangos Database/DBCfmt.h:67-70, DBCStructure.h:507-556, DBCStores.cpp:589-602,
/// ObjectMgr.cpp:10450-10464, Spells/SpellMgr.h:256-275 and SpellMgr.cpp:1440-1650, 1850-1887.
/// </summary>
public sealed class SkillDbcReaderTests
{
    private readonly ITestOutputHelper _output;

    public SkillDbcReaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SkillLine_ReadsIdCategoryEnglishNameAndIcon()
    {
        // 22 fields: id, category, cost id, 8 locale names (+ flags), 8 locale descriptions (+ flags), icon.
        uint[] row = new uint[22];
        row[0] = 186;
        row[1] = 11;
        row[3] = 1; // enUS name offset into the string block
        row[21] = 136;
        IReadOnlyList<SkillLineRecord> lines = SkillDbcReaders.ReadSkillLines(DbcFile.Parse(Image(22, ["\0Mining\0"], row)));
        Assert.Equal(new SkillLineRecord(186, 11, "Mining", 136), Assert.Single(lines));
    }

    [Fact]
    public void SkillLine_RejectsAnyOtherFieldCount()
    {
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillLines(DbcFile.Parse(Image(21, ["\0"], new uint[21]))));
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillLines(DbcFile.Parse(Image(23, ["\0"], new uint[23]))));
    }

    [Fact]
    public void SkillRaceClassInfo_ReadsFieldsOneToSix_AndRejectsOtherWidths()
    {
        // id (unused), skill, race mask, class mask, flags, min level, tier, cost index (unused)
        IReadOnlyList<SkillRaceClassInfoRecord> rows = SkillDbcReaders.ReadSkillRaceClassInfos(DbcFile.Parse(Image(8, ["\0"],
            [99, 164, 0, 0, 0x20, 5, 21, 99])));
        Assert.Equal(new SkillRaceClassInfoRecord(164, 0, 0, 0x20, 5, 21), Assert.Single(rows));
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillRaceClassInfos(DbcFile.Parse(Image(7, ["\0"], new uint[7]))));
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillRaceClassInfos(DbcFile.Parse(Image(9, ["\0"], new uint[9]))));
    }

    [Fact]
    public void SkillTiers_ReadsSixteenCostsAndSixteenMaximums_AndRejectsOtherWidths()
    {
        uint[] row = new uint[33];
        row[0] = 21;
        for (int step = 0; step < 16; step++)
        {
            row[1 + step] = (uint)(100 * (step + 1));
            row[17 + step] = (uint)(75 * (step + 1));
        }

        SkillTierRecord tier = Assert.Single(SkillDbcReaders.ReadSkillTiers(DbcFile.Parse(Image(33, ["\0"], row))));
        Assert.Equal(21u, tier.Id);
        Assert.Equal(100u, tier.Cost[0]);
        Assert.Equal(1600u, tier.Cost[15]);
        Assert.Equal(75u, tier.MaxValue[0]);
        Assert.Equal(300u, tier.MaxValue[3]);
        Assert.Equal(1200u, tier.MaxValue[15]);
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillTiers(DbcFile.Parse(Image(32, ["\0"], new uint[32]))));
        Assert.Throws<InvalidDataException>(() => SkillDbcReaders.ReadSkillTiers(DbcFile.Parse(Image(34, ["\0"], new uint[34]))));
    }

    [Fact]
    public void Build_AssemblesTheCatalogFromTheFourFiles()
    {
        uint[] mining = new uint[22];
        mining[0] = 186;
        mining[1] = 11;
        mining[3] = 1;
        uint[] tier = new uint[33];
        tier[0] = 21;
        SkillCatalog catalog = SkillDbcReaders.Build(
            DbcFile.Parse(Image(22, ["\0Mining\0"], mining)),
            DbcFile.Parse(Image(8, ["\0"], [1, 186, 0, 0, 0, 0, 21, 0])),
            DbcFile.Parse(Image(33, ["\0"], tier)),
            DbcFile.Parse(Image(15, ["\0"], [7, 186, 2575, 0, 0, 0, 0, 1, 2576, 1, 300, 150, 0, 0, 0])));
        Assert.Equal("Mining", catalog.Line(186)?.Name);
        Assert.Equal(SkillRangeType.Rank, catalog.RangeType(186, catalog.RaceClassInfo(186, 1, 1)!));
        Assert.Equal(2575u, Assert.Single(catalog.AbilitiesOfSkill(186)).SpellId);
        Assert.Equal(2575u, Assert.Single(catalog.AbilitiesOfSpell(2575)).SpellId);
        Assert.Throws<ArgumentException>(() => new SkillCatalog(
            [new SkillLineRecord(1, 6, "a", 0), new SkillLineRecord(1, 6, "b", 0)], [], [], []));
    }

    [Fact]
    public void RaceClassInfo_FirstFittingRowInFileOrder_MaskZeroIsAny()
    {
        // Human = race 1 = bit 0, Warrior = class 1 = bit 0, Night Elf = race 4 = bit 3, Druid = class 11 = bit 10.
        var specific = new SkillRaceClassInfoRecord(40, 1u << 3, 1u << 10, 1, 0, 0);
        var anyRace = new SkillRaceClassInfoRecord(40, 0, 1u << 0, 2, 0, 0);
        var anyone = new SkillRaceClassInfoRecord(40, 0, 0, 3, 0, 0);
        var catalog = new SkillCatalog([], [specific, anyRace, anyone], [], []);
        Assert.Same(specific, catalog.RaceClassInfo(40, 4, 11));
        Assert.Same(anyRace, catalog.RaceClassInfo(40, 1, 1));   // second row: any race, warrior only
        Assert.Same(anyone, catalog.RaceClassInfo(40, 1, 2));    // paladin falls through to the catch-all
        Assert.Null(new SkillCatalog([], [specific], [], []).RaceClassInfo(40, 1, 1));
        Assert.Null(catalog.RaceClassInfo(41, 1, 1));
        Assert.Null(catalog.RaceClassInfo(40, 0, 1));            // race 0 would shift by -1: no row
        Assert.Null(catalog.RaceClassInfo(40, 33, 1));
    }

    [Fact]
    public void RangeType_TierWins_ThenArmorMono_LanguageLanguage_ElseLevel()
    {
        var catalog = new SkillCatalog(
            [
                new SkillLineRecord(SkillIds.Mining, SkillCategories.Profession, "Mining", 0),
                new SkillLineRecord(SkillIds.PlateMail, SkillCategories.Armor, "Plate", 0),
                new SkillLineRecord(SkillIds.LanguageCommon, SkillCategories.Languages, "Common", 0),
                new SkillLineRecord(SkillIds.Swords, SkillCategories.Weapon, "Swords", 0),
            ],
            [],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
            []);
        Assert.Equal(SkillRangeType.Rank, catalog.RangeType(SkillIds.Mining, Info(SkillIds.Mining, tier: 21)));
        Assert.Equal(SkillRangeType.Rank, catalog.RangeType(SkillIds.PlateMail, Info(SkillIds.PlateMail, tier: 21)));   // a tier beats the category
        Assert.Equal(SkillRangeType.Mono, catalog.RangeType(SkillIds.PlateMail, Info(SkillIds.PlateMail, tier: 0)));
        Assert.Equal(SkillRangeType.Language, catalog.RangeType(SkillIds.LanguageCommon, Info(SkillIds.LanguageCommon, tier: 0)));
        Assert.Equal(SkillRangeType.Level, catalog.RangeType(SkillIds.Swords, Info(SkillIds.Swords, tier: 0)));
        Assert.Equal(SkillRangeType.Level, catalog.RangeType(SkillIds.Mining, Info(SkillIds.Mining, tier: 99)));         // unknown tier id: category fallback
        Assert.Null(catalog.RangeType(12345, Info(12345, tier: 0)));
    }

    [Fact]
    public void ProfessionPredicates_Category11_PlusFishingCookingFirstAid_PlusRiding()
    {
        var catalog = new SkillCatalog(
            [
                new SkillLineRecord(SkillIds.Mining, SkillCategories.Profession, "Mining", 0),
                new SkillLineRecord(SkillIds.Fishing, SkillCategories.Secondary, "Fishing", 0),
                new SkillLineRecord(SkillIds.Swords, SkillCategories.Weapon, "Swords", 0),
            ],
            [], [], []);
        Assert.True(catalog.IsPrimaryProfessionSkill(SkillIds.Mining));
        Assert.False(catalog.IsPrimaryProfessionSkill(SkillIds.Fishing));
        Assert.False(catalog.IsPrimaryProfessionSkill(SkillIds.Swords));
        Assert.False(catalog.IsPrimaryProfessionSkill(SkillIds.Herbalism));   // not in this catalog: unknown line is not a profession
        Assert.True(catalog.IsProfessionSkill(SkillIds.Mining));
        Assert.True(catalog.IsProfessionSkill(SkillIds.Fishing));
        Assert.True(catalog.IsProfessionSkill(SkillIds.Cooking));              // id test needs no line row
        Assert.True(catalog.IsProfessionSkill(SkillIds.FirstAid));
        Assert.False(catalog.IsProfessionSkill(SkillIds.Swords));
        Assert.False(catalog.IsProfessionSkill(SkillIds.Riding));
        Assert.True(catalog.IsProfessionOrRidingSkill(SkillIds.Riding));
        Assert.False(catalog.IsProfessionOrRidingSkill(SkillIds.Swords));
    }

    [Fact]
    public void SpellLearnSkill_StepIsBasePlusDice_ValueOneExceptRiding_MaxIsStepTimes75()
    {
        // Apprentice Mining 2575: effect 1 SKILL, misc 186, basepoints 0, dice 1 => step 1 (SpellMgr.cpp:1850-1887).
        // Journeyman (step 2) / Expert (step 3); Riding Apprentice 33388 is step 1 and starts at 75.
        SpellLearnSkillTable table = SpellLearnSkillTable.Build(
        [
            new SpellSkillEffect(2575, 1, (int)SkillIds.Mining, 0, 1),
            new SpellSkillEffect(3564, 1, (int)SkillIds.Mining, 1, 1),
            new SpellSkillEffect(33388, 1, (int)SkillIds.Riding, 0, 1),
            new SpellSkillEffect(33391, 1, (int)SkillIds.Riding, 1, 1),
        ]);
        Assert.Equal(4, table.Count);
        Assert.True(table.TryGet(2575, out SpellLearnSkillNode apprentice));
        Assert.Equal(new SpellLearnSkillNode(186, 1, 1, 75), apprentice);
        Assert.True(table.TryGet(3564, out SpellLearnSkillNode journeyman));
        Assert.Equal(new SpellLearnSkillNode(186, 2, 1, 150), journeyman);
        Assert.True(table.TryGet(33388, out SpellLearnSkillNode riding));
        Assert.Equal(new SpellLearnSkillNode(762, 1, 75, 75), riding);
        Assert.True(table.TryGet(33391, out SpellLearnSkillNode riding2));
        Assert.Equal(new SpellLearnSkillNode(762, 2, 150, 150), riding2);
        Assert.False(table.TryGet(1, out _));
    }

    [Fact]
    public void SpellLearnSkill_LowestSkillEffectWins_AndOnlyEffectIndexOneFeedsTheProfessionPredicate()
    {
        // The reference breaks after the first SKILL effect (index order), but IsPrimaryProfessionSpell reads effect 1 only.
        SpellLearnSkillTable table = SpellLearnSkillTable.Build(
        [
            new SpellSkillEffect(900, 2, 164, 0, 1),
            new SpellSkillEffect(900, 0, 165, 1, 1),
            new SpellSkillEffect(901, 2, 186, 0, 1),
            new SpellSkillEffect(902, 1, 186, 0, 1),
        ]);
        Assert.True(table.TryGet(900, out SpellLearnSkillNode node));
        Assert.Equal((ushort)165, node.SkillId);
        Assert.Equal((ushort)2, node.Step);
        Assert.False(table.TryGetEffectOneSkill(900, out _));
        Assert.False(table.TryGetEffectOneSkill(901, out _));
        Assert.True(table.TryGetEffectOneSkill(902, out ushort skill));
        Assert.Equal((ushort)186, skill);

        var catalog = new SkillCatalog(
            [new SkillLineRecord(186, SkillCategories.Profession, "Mining", 0)], [], [], [], table);
        Assert.True(catalog.IsPrimaryProfessionSpell(902));
        Assert.False(catalog.IsPrimaryProfessionSpell(901));   // SKILL on effect 2 does not count
        Assert.False(catalog.IsPrimaryProfessionSpell(999));
    }

    [Fact]
    public void PrimaryProfessionFirstRank_NeedsRankOneInTheChain()
    {
        SpellLearnSkillTable learn = SpellLearnSkillTable.Build(
        [
            new SpellSkillEffect(2575, 1, 186, 0, 1),
            new SpellSkillEffect(2576, 1, 186, 1, 1),
        ]);
        var catalog = new SkillCatalog(
            [new SkillLineRecord(186, SkillCategories.Profession, "Mining", 0)],
            [], [],
            [Ability(1, 186, 2575, forward: 2576), Ability(2, 186, 2576)],
            learn);
        Assert.True(catalog.IsPrimaryProfessionFirstRankSpell(2575));
        Assert.False(catalog.IsPrimaryProfessionFirstRankSpell(2576));   // rank 2
        Assert.False(catalog.IsPrimaryProfessionFirstRankSpell(555));
    }

    [Fact]
    public void RankChains_FollowForwardLinks_FirstRankHasNoPrevious()
    {
        // Three ranks 100 -> 102 -> 104, plus an unrelated unranked ability (999).
        SpellRankChains chains = new(
        [
            Ability(1, 26, 100, forward: 102),
            Ability(2, 26, 102, forward: 104),
            Ability(3, 26, 104),
            Ability(4, 26, 999),
        ]);
        Assert.Equal((byte)1, chains.Rank(100));
        Assert.Equal((byte)2, chains.Rank(102));
        Assert.Equal((byte)3, chains.Rank(104));
        Assert.Equal((byte)0, chains.Rank(999));
        Assert.Equal(0u, chains.Previous(100));
        Assert.Equal(100u, chains.Previous(102));
        Assert.Equal(102u, chains.Previous(104));
        Assert.Equal(102u, chains.Next(100));
        Assert.Equal(0u, chains.Next(104));
        Assert.Equal(100u, chains.First(104));
        Assert.Equal(0u, chains.First(999));
        Assert.Equal(3, chains.Count);
        Assert.True(chains.IsHighRankOf(104, 100));
        Assert.True(chains.IsHighRankOf(104, 102));
        Assert.False(chains.IsHighRankOf(100, 104));
        Assert.False(chains.IsHighRankOf(102, 102));
        Assert.False(chains.IsHighRankOf(999, 100));
    }

    [Fact]
    public void RankChains_ForceHerbGatheringApprentice2366ToForward2368()
    {
        // SpellMgr.cpp:1559-1562: the 1.12 client data has no forward link for 2366.
        SpellRankChains chains = new(
        [
            Ability(1, SkillIds.Herbalism, 2366),
            Ability(2, SkillIds.Herbalism, 2368, forward: 3570),
            Ability(3, SkillIds.Herbalism, 3570),
        ]);
        Assert.Equal((byte)1, chains.Rank(2366));
        Assert.Equal((byte)2, chains.Rank(2368));
        Assert.Equal((byte)3, chains.Rank(3570));
        Assert.Equal(2368u, chains.Next(2366));
        Assert.Equal(2366u, chains.First(3570));
    }

    [Fact]
    public void RankChains_SealOfRighteousness20154NeverStartsAChain()
    {
        // SpellMgr.cpp:1564-1565: "Seal of Righteousness (20154) make double in spellbook".
        SpellRankChains chains = new(
        [
            Ability(1, 184, 20154, forward: 20287),
            Ability(2, 184, 20287),
        ]);
        Assert.Equal((byte)0, chains.Rank(20154));
        Assert.Equal((byte)0, chains.Rank(20287));
        Assert.Equal(0, chains.Count);
    }

    [Fact]
    public void RankChains_IgnoreForwardsWithoutAbilitiesOrWithoutSpells()
    {
        // Forward spell without an ability row of its own (SpellMgr.cpp:1575-1578), or a missing spell (:1553-1555, :1568-1572).
        SpellRankChains noAbility = new([Ability(1, 26, 100, forward: 102)]);
        Assert.Equal(0, noAbility.Count);

        SpellRankChains missing = new(
            [Ability(1, 26, 100, forward: 102), Ability(2, 26, 102)],
            spellId => spellId != 102);
        Assert.Equal(0, missing.Count);

        SpellRankChains selfForward = new([Ability(1, 26, 100, forward: 100)]);
        Assert.Equal(0, selfForward.Count);
    }

    [Fact]
    public void RankChains_AnOverlongOrCyclicChainIsDroppedRatherThanLooping()
    {
        SpellRankChains cycle = new(
        [
            Ability(1, 26, 10, forward: 11),
            Ability(2, 26, 11, forward: 10),
        ]);
        Assert.Equal(0, cycle.Count);
    }

    [Fact]
    public void SkillFlags_MatchVmangosDbcEnums()
    {
        // DBCEnums.h:172-181 / :159.
        Assert.Equal(0x2u, SkillRaceClassFlags.NoSkillUpMessage);
        Assert.Equal(0x10u, SkillRaceClassFlags.AlwaysMaxValue);
        Assert.Equal(0x20u, SkillRaceClassFlags.Unlearnable);
        Assert.Equal(0x80u, SkillRaceClassFlags.IncludeInSort);
        Assert.Equal(0x100u, SkillRaceClassFlags.NotTrainable);
        Assert.Equal(0x400u, SkillRaceClassFlags.MonoValue);
    }

    [Fact]
    public void SkillIds_MatchTheReferenceForTheIdsOtherCodeDependsOn()
    {
        // vmangos SharedDefines.h:941-1069.
        Assert.Equal(95u, SkillIds.Defense);
        Assert.Equal(118u, SkillIds.DualWield);
        Assert.Equal(129u, SkillIds.FirstAid);
        Assert.Equal(164u, SkillIds.Blacksmithing);
        Assert.Equal(182u, SkillIds.Herbalism);
        Assert.Equal(185u, SkillIds.Cooking);
        Assert.Equal(186u, SkillIds.Mining);
        Assert.Equal(356u, SkillIds.Fishing);
        Assert.Equal(393u, SkillIds.Skinning);
        Assert.Equal(633u, SkillIds.Lockpicking);
        Assert.Equal(762u, SkillIds.Riding);
        Assert.Equal(763u, SkillIds.MaxSkillType);
        Assert.Equal(11, SkillCategories.Profession);
        Assert.Equal(10, SkillCategories.Languages);
        Assert.Equal(8, SkillCategories.Armor);
    }

    [RealDbcFact]
    public void RealDbcProbe_LoadsTheDevelopersFiles_AndPrintsTheLayoutItFound()
    {
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        int probed = 0;
        DbcFile line = DbcFile.Load(Path.Combine(dir, "SkillLine.dbc"));
        DbcFile raceClass = DbcFile.Load(Path.Combine(dir, "SkillRaceClassInfo.dbc"));
        DbcFile tiers = DbcFile.Load(Path.Combine(dir, "SkillTiers.dbc"));
        DbcFile ability = DbcFile.Load(Path.Combine(dir, "SkillLineAbility.dbc"));
        _output.WriteLine($"SkillLine {line.FieldCount} fields/{line.RecordCount} rows; SkillRaceClassInfo {raceClass.FieldCount}/{raceClass.RecordCount}; "
            + $"SkillTiers {tiers.FieldCount}/{tiers.RecordCount}; SkillLineAbility {ability.FieldCount}/{ability.RecordCount}");
        SkillCatalog catalog = SkillDbcReaders.Build(line, raceClass, tiers, ability);
        probed++;
        Assert.True(catalog.IsPrimaryProfessionSkill(SkillIds.Mining));
        Assert.True(catalog.IsPrimaryProfessionSkill(SkillIds.Herbalism));
        Assert.False(catalog.IsPrimaryProfessionSkill(SkillIds.Cooking));
        probed++;
        Assert.NotNull(catalog.RaceClassInfo(SkillIds.Swords, 1, 1));
        Assert.True(ability.RecordCount > 0);
        probed++;
        _output.WriteLine($"{probed} probes ran / 0 skipped");
    }

    private static SkillRaceClassInfoRecord Info(uint skill, uint tier) => new(skill, 0, 0, 0, 0, tier);

    private static SkillLineAbilityRecord Ability(uint id, uint skill, uint spell, uint forward = 0)
        => new(id, skill, spell, 0, 0, 0, forward, 0, 0, 0);

    private static byte[] Image(int fields, string[] strings, params uint[][] records)
    {
        byte[] block = Encoding.UTF8.GetBytes(string.Concat(strings));
        if (block.Length == 0)
        {
            block = [0];
        }

        byte[] image = new byte[20 + (records.Length * fields * 4) + block.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        block.CopyTo(image.AsSpan(20 + (records.Length * fields * 4)));
        return image;
    }
}

/// <summary>
/// A fact that needs the developer's build-5875 DBC directory (<c>ARCANECORE_TEST_DBC_DIR</c>). Without it the
/// test is reported as Skipped (never as a silent pass), so the run summary shows how many probes did not run.
/// </summary>
public sealed class RealDbcFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_DBC_DIR";

    public RealDbcFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Skip = $"{Variable} is not set to a directory of build-5875 DBC files.";
        }
    }
}
