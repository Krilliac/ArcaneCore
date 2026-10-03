using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.GameObjects;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport;

/// <summary>
/// The special loot tables are no longer "not imported": the spec names them, their key and the columns the importer
/// reads, and the importer really reads them (a column the spec claims must change what it writes).
/// </summary>
public sealed class SpecialLootSpecTests
{
    [Theory]
    [InlineData("fishing_loot_template")]
    [InlineData("pickpocketing_loot_template")]
    [InlineData("disenchant_loot_template")]
    [InlineData("skill_fishing_base_level")]
    public void TheTableHasASpec(string table) => Assert.NotNull(ContentTableSpecs.Find(table));

    [Fact]
    public void CreatureTemplateSpec_ClaimsBothPickpocketColumnNames()
    {
        TableSpec spec = ContentTableSpecs.Find("creature_template")!;
        Assert.True(spec.IsMapped("PickpocketLootId"));
        Assert.True(spec.IsMapped("pickpocket_loot_id"));
    }

    [Fact]
    public void FishingBaseSkill_SkillColumnChangesWhatIsWritten_AndKeepsTheSign()
    {
        var withSkill = new GameObjectLootDumpImporter();
        withSkill.Read(new StringReader("INSERT INTO `skill_fishing_base_level` (`entry`,`skill`) VALUES (7,-70);"));
        var without = new GameObjectLootDumpImporter();
        without.Read(new StringReader("INSERT INTO `skill_fishing_base_level` (`entry`) VALUES (7);"));
        Assert.Equal(1, withSkill.BuildReport().FishingBaseLevels);
        Assert.Equal(-70, withSkill.FishingBaseRows.Single().Skill);
        Assert.Equal(0, without.FishingBaseRows.Single().Skill);
    }

    [Fact]
    public void PickpocketColumn_InBothDialects_IsRead()
    {
        var cmangos = new GameObjectLootDumpImporter();
        cmangos.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`PickpocketLootId`) VALUES (5,77);"));
        var vmangos = new GameObjectLootDumpImporter();
        vmangos.Read(new StringReader("INSERT INTO `creature_template` (`entry`,`patch`,`level_min`,`pickpocket_loot_id`) VALUES (5,0,1,78);"));
        Assert.Equal(77u, cmangos.PickpocketRows.Single().LootId);
        Assert.Equal(78u, vmangos.PickpocketRows.Single().LootId);
    }
}