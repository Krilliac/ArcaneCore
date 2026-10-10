using System.Buffers.Binary;
using ArcaneCore.Data.Content.Pets;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Tests.Skills;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

/// <summary>
/// The DBC side of beast training: CreatureFamily.dbc skillLine[0] (vmangos DBCStructure.h:250) and SkillLineAbility.dbc reqtrainpoints
/// (field 14, DBCStructure.h:555), against synthetic images and the developer's real build-5875 files.
/// </summary>
public sealed class PetTrainingDbcTests
{
    [Fact]
    public void CreatureFamily_ReadsTheFirstSkillLine_ByFamilyId()
    {
        uint[] wolf = new uint[CreatureFamilyDbcReader.FieldCount];
        wolf[0] = 1;
        wolf[CreatureFamilyDbcReader.SkillLineField] = 208;
        wolf[CreatureFamilyDbcReader.SkillLineField + 1] = 270;
        uint[] felhunter = new uint[CreatureFamilyDbcReader.FieldCount];
        felhunter[0] = 15;
        felhunter[CreatureFamilyDbcReader.SkillLineField] = 189;

        IReadOnlyDictionary<uint, uint> lines = CreatureFamilyDbcReader.ReadSkillLines(DbcFile.Parse(Image(CreatureFamilyDbcReader.FieldCount, wolf, felhunter)));

        Assert.Equal(208u, lines[1]);
        Assert.Equal(189u, lines[15]);
        Assert.Equal(2, lines.Count);

        uint[] zero = new uint[CreatureFamilyDbcReader.FieldCount];
        Assert.Throws<InvalidDataException>(() => CreatureFamilyDbcReader.ReadSkillLines(DbcFile.Parse(Image(CreatureFamilyDbcReader.FieldCount, zero))));
        Assert.Throws<InvalidDataException>(() => CreatureFamilyDbcReader.ReadSkillLines(DbcFile.Parse(Image(CreatureFamilyDbcReader.FieldCount, wolf, wolf))));
        Assert.Throws<InvalidDataException>(() => CreatureFamilyDbcReader.ReadSkillLines(DbcFile.Parse(Image(17, new uint[17]))));
    }

    [Fact]
    public void FirstInChain_WalksThePreviousRanks_AndStopsOnACycle()
    {
        var catalog = new SkillLineAbilityCatalog(
        [
            new SkillLineAbilityRecord(1, 208, 10, 0, 0, 0, 11, 0, 0, 0, 1),
            new SkillLineAbilityRecord(2, 208, 11, 0, 0, 0, 12, 0, 0, 0, 4),
            new SkillLineAbilityRecord(3, 208, 12, 0, 0, 0, 0, 0, 0, 0, 9),
            new SkillLineAbilityRecord(4, 208, 20, 0, 0, 0, 21, 0, 0, 0, 0),
            new SkillLineAbilityRecord(5, 208, 21, 0, 0, 0, 20, 0, 0, 0, 0),
        ], hasTrainingPoints: true);

        Assert.Equal(10u, catalog.FirstInChain(12));
        Assert.Equal(10u, catalog.FirstInChain(10));
        Assert.Equal(77u, catalog.FirstInChain(77));
        _ = catalog.FirstInChain(20); // a corrupt loop terminates
    }

    private static string RealDbc(string file) => Path.Combine(Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!, file);

    [RealDbcFact]
    public void RealDbc_PetAbilitiesCarryTrainingPoints()
    {
        SkillLineAbilityCatalog catalog = NpcServiceDbcReaders.LoadSkillLineAbilities(RealDbc("SkillLineAbility.dbc"));

        Assert.True(catalog.HasTrainingPoints);
        Assert.Equal(1u, catalog.TrainingPoints(17253));  // Bite rank 1
        Assert.Equal(4u, catalog.TrainingPoints(17255));
        Assert.Equal(25u, catalog.TrainingPoints(17261));
        Assert.Equal(5u, catalog.TrainingPoints(4187));   // SKILL_PET_TALENTS (270)
        Assert.Equal(0u, catalog.TrainingPoints(2649));   // Growl rank 1
        Assert.Equal(17253u, catalog.FirstInChain(17261));
    }

    [RealDbcFact]
    public void RealDbc_FamilySkillLines()
    {
        IReadOnlyDictionary<uint, uint> lines = CreatureFamilyDbcReader.LoadSkillLines(RealDbc("CreatureFamily.dbc"));

        Assert.Equal(208u, lines[1]);   // Wolf
        Assert.Equal(203u, lines[3]);   // Spider
        Assert.Equal(656u, lines[27]);  // Wind Serpent
        Assert.Equal(189u, lines[15]);  // Felhunter
    }

    [RealDbcFact]
    public void RealDbc_TrainingPointsAgreeAcrossSkillLines()
    {
        IReadOnlyList<SkillLineAbilityRecord> rows = NpcServiceDbcReaders.ReadSkillLineAbilityRecords(DbcFile.Load(RealDbc("SkillLineAbility.dbc")));

        foreach (IGrouping<uint, SkillLineAbilityRecord> spell in rows.GroupBy(r => r.SpellId))
        {
            Assert.True(spell.Select(r => r.ReqTrainPoints).Distinct().Count() == 1, $"spell {spell.Key} has several reqtrainpoints values");
        }
    }

    [RealDbcFact]
    public void RealDbc_CostedPetAbilitiesWithOneNameShareOneChain()
    {
        SkillLineAbilityCatalog catalog = NpcServiceDbcReaders.LoadSkillLineAbilities(RealDbc("SkillLineAbility.dbc"));
        DbcFile spells = DbcFile.Load(RealDbc("Spell.dbc"));
        var nameById = new Dictionary<uint, string>();
        for (int row = 0; row < spells.RecordCount; row++)
        {
            nameById[spells.GetUInt32(row, 0)] = spells.GetString(row, 120);
        }

        IReadOnlyList<SkillLineAbilityRecord> rows = NpcServiceDbcReaders.ReadSkillLineAbilityRecords(DbcFile.Load(RealDbc("SkillLineAbility.dbc")));
        foreach (IGrouping<string, uint> family in rows.Where(r => r.ReqTrainPoints != 0).Select(r => r.SpellId).Distinct()
                     .GroupBy(id => nameById[id]))
        {
            Assert.True(family.Select(catalog.FirstInChain).Distinct().Count() == 1, $"costed pet ability '{family.Key}' splits into several rank chains");
        }

        Assert.Equal(7371u, catalog.FirstInChain(27685));  // Boar Charge rank 6, linked from spell_chain
        Assert.Equal(26201u, catalog.PreviousRank(27685));
    }

    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + (records.Length * fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        return image;
    }
}
