using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Skills;
using ArcaneCore.Kernel.Skills;
using Xunit;

namespace ArcaneCore.Data.Tests.Skills;

public sealed class StartingSkillImporterTests
{
    private const string Dump = """
        CREATE TABLE `playercreateinfo_skills` (`raceMask` int unsigned NOT NULL, `classMask` int unsigned NOT NULL, `skill` smallint unsigned NOT NULL, `step` smallint unsigned NOT NULL, `note` varchar(255) NOT NULL, PRIMARY KEY (`raceMask`,`classMask`,`skill`));
        INSERT INTO `playercreateinfo_skills` VALUES (0,0,95,0,'Defense'),(1,1,43,0,'Swords'),(0,1,171,0,'Alchemy'),(512,1,43,0,'bad race'),(1,32,43,0,'bad class'),(1,1,43,17,'bad step'),(1,1,0,0,'bad skill');
        """;

    [Fact]
    public void Module_UsesNextFreeWorldVersion()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is StartingSkillWorldDataModule);
        Assert.Equal(25, module.SchemaVersion);
        Assert.Equal([StartingSkillWorldDataModule.Table], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
    }

    [Fact]
    public void Importer_PreservesMasksAndSkipsInvalidRows()
    {
        var importer = new StartingSkillDumpImporter();
        importer.Read(new StringReader(Dump));

        StartingSkillRow[] rows = [.. importer.Snapshot().OrderBy(r => r.Skill)];
        Assert.Equal([(1u, 1u, (ushort)43, (ushort)0), (0u, 0u, (ushort)95, (ushort)0), (0u, 1u, (ushort)171, (ushort)0)],
            rows.Select(r => (r.RaceMask, r.ClassMask, r.Skill, r.Step)));
        StartingSkillImportReport report = importer.BuildReport();
        Assert.Equal(3, report.Rows);
        Assert.Equal(4, report.SkippedRows);
    }

    [Fact]
    public void SkillOverflowAndNegativeStepAreRejectedAndDuplicateKeysAreReported()
    {
        var importer = new StartingSkillDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `playercreateinfo_skills` (`raceMask`,`classMask`,`skill`,`step`,`note`) VALUES
            (0,0,95,1,'first'),(0,0,95,2,'replacement'),(0,0,70000,1,'overflow'),
            (0,0,43,-1,'negative'),(0,0,171,16,'last valid tier');
            """));

        Assert.Equal(2, importer.BuildReport().Rows);
        Assert.Equal(2, importer.BuildReport().SkippedRows);
        Assert.Equal((ushort)2, Assert.Single(importer.Snapshot(), row => row.Skill == 95).Step);
        Assert.Equal((ushort)16, Assert.Single(importer.Snapshot(), row => row.Skill == 171).Step);
        Assert.DoesNotContain(importer.Snapshot(), row => row.Skill == ushort.MaxValue);
        Assert.Contains(importer.BuildReport().Warnings, warning => warning.Contains("duplicate", StringComparison.Ordinal));
    }
}
