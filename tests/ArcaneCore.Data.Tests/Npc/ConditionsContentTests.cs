using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

/// <summary>
/// The <c>conditions</c> content: world schema step (<see cref="ConditionsWorldModule.Version"/>), the classic-db
/// (cmangos layout) importer and the EF store. Dump text is written by hand in the cmangos column layout; no GPL rows are
/// copied. The numbering the rows carry is cmangos's, so the vmangos layout is refused.
/// </summary>
public sealed class ConditionsContentTests : IAsyncLifetime
{
    private const string Dump = """
        -- cmangos classic-db layout (hand written)
        CREATE TABLE `db_version` (
          `version` varchar(120) DEFAULT NULL,
          `creature_ai_version` varchar(120) DEFAULT NULL
        ) ENGINE=MyISAM;
        INSERT INTO `db_version` VALUES ('ClassicDB test dump z0000','ACID test');
        INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`,`comments`) VALUES
        (1,14,0,2,0,0,0,'class warrior'),
        (2,14,0,128,0,0,0,'class mage'),
        (3,-3,2,0,0,0,0,'not mage'),
        (4,-1,1,3,0,0,0,'warrior and not mage'),
        (5,35,1,0,0,0,1,'gender 1, reversed'),
        (6,40,0,0,0,0,0,'world script');
        UPDATE `conditions` SET `value2` = 4 WHERE `condition_entry` = 1;
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void TheWorldStep_IsAssignedOnce_AndCreatesOnlyTheConditionsTable()
    {
        Assert.True(WorldDbContext.Schema.CurrentVersion >= ConditionsWorldModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == ConditionsWorldModule.Version);
        Assert.Equal("conditions", Assert.Single(step.Changes.Cast<CreateTableChange>()).Table);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is ConditionsWorldModule);
    }

    [Fact]
    public void TheImporterMapsTheCmangosColumnsByName_AndReportsTheDbVersion()
    {
        var importer = new ConditionsDumpImporter();
        importer.Read(new StringReader(Dump));

        ConditionsImportReport report = importer.BuildReport();
        Assert.Equal(6, report.Conditions);
        Assert.Equal(0, report.Replaced);
        Assert.Equal("ClassicDB test dump z0000", report.DbVersion);
        ConditionRow reversedGender = importer.Rows.Single(r => r.ConditionEntry == 5);
        Assert.Equal((35, 1u, 0u, (byte)1), (reversedGender.Type, reversedGender.Value1, reversedGender.Value2, reversedGender.Flags));
        ConditionRow not = importer.Rows.Single(r => r.ConditionEntry == 3);
        Assert.Equal(-3, not.Type);   // signed: NOT/OR/AND are negative
        Assert.Equal(2u, not.Value1);
        ConditionRow and = importer.Rows.Single(r => r.ConditionEntry == 4);
        Assert.Equal((-1, 1u, 3u), (and.Type, and.Value1, and.Value2));
    }

    [Fact]
    public void ALaterRowWithTheSameKeyReplacesTheEarlierOne_AndIsCounted()
    {
        var importer = new ConditionsDumpImporter();
        importer.Read(new StringReader("INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES (1,15,5,1,0,0,0);"));
        importer.Read(new StringReader("INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES (1,15,9,2,0,0,0);"));

        Assert.Equal(1, importer.BuildReport().Conditions);
        Assert.Equal(1, importer.BuildReport().Replaced);
        Assert.Equal(9u, Assert.Single(importer.Rows).Value1);
    }

    [Fact]
    public void AVmangosLayoutDump_IsRefused_BecauseItsNumberingDiffers()
    {
        // vmangos conditions: (condition_entry, type, value1, value2, flags) and a different type numbering
        // (D:\refs\vmangos\sql\custom: REPLACE INTO conditions VALUES (99900, 43, 0, 0, 3)).
        var importer = new ConditionsDumpImporter();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => importer.Read(new StringReader(
            "INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`flags`) VALUES (99900,43,0,0,3);")));
        Assert.Contains("vmangos", error.Message);
    }

    [Theory]
    [InlineData("(1,'x',0,0,0,0,0)")]
    [InlineData("(1,14,-1,0,0,0,0)")]
    [InlineData("(1,14,0,0,0,0,256)")]
    public void ABadValueIsAnErrorNotASilentZero(string tuple)
    {
        var importer = new ConditionsDumpImporter();
        Assert.ThrowsAny<Exception>(() => importer.Read(new StringReader(
            "INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES " + tuple + ";")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportThenLoad_RoundTrips_AndReimportReplacesAtomically(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new ConditionsDumpImporter();
        importer.Read(new StringReader(Dump));

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(6, (await importer.WriteAsync(db, replace: false)).Conditions);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            IReadOnlyList<ConditionRecord> loaded = await new EfConditionContentStore(db).LoadAsync();
            Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u], loaded.Select(r => r.Entry));
            Assert.Equal(new ConditionRecord(3, -3, 2, 0, 0, 0, 0), loaded[2]);
            Assert.Equal(new ConditionRecord(5, 35, 1, 0, 0, 0, 1), loaded[4]);
        }

        // A re-import without replace collides on the keys and must leave the first import untouched.
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => importer.WriteAsync(db, replace: false));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal(6, await db.Set<ConditionRow>().CountAsync());
        }

        // Replace semantics: the table holds exactly the new dump.
        var second = new ConditionsDumpImporter();
        second.Read(new StringReader("INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES (9,15,10,1,0,0,0);"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await second.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal([9u], (await new EfConditionContentStore(db).LoadAsync()).Select(r => r.Entry));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
