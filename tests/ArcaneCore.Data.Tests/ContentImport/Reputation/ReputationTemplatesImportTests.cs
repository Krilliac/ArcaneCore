using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Reputation;

/// <summary>
/// <c>reputation_spillover_template</c> and <c>reputation_reward_rate</c> import (backlog item 43): 12 + 3 synthetic rows in the
/// classic-db column layout (<c>rate_1</c>, <c>rank_1</c>, <c>quest_rate</c>). Dumps are hand-written; no GPL data is copied.
/// </summary>
public sealed class ReputationTemplatesImportTests : IAsyncLifetime
{
    public const string Dump = """
        CREATE TABLE `reputation_spillover_template` (`faction` smallint unsigned NOT NULL, `faction1` smallint unsigned, `rate_1` float, `rank_1` tinyint unsigned, `faction2` smallint unsigned, `rate_2` float, `rank_2` tinyint unsigned, `faction3` smallint unsigned, `rate_3` float, `rank_3` tinyint unsigned, `faction4` smallint unsigned, `rate_4` float, `rank_4` tinyint unsigned, PRIMARY KEY (`faction`));
        INSERT INTO `reputation_spillover_template` VALUES (21,72,0.25,7,99,0.5,3,87,0.5,7,0,0,0),(1002,1,0.5,7,0,0,0,0,0,0,0,0,0),(1003,1,0.5,7,0,0,0,0,0,0,0,0,0),(1004,1,0.5,7,0,0,0,0,0,0,0,0,0),(1005,1,0.5,7,0,0,0,0,0,0,0,0,0),(1006,1,0.5,7,0,0,0,0,0,0,0,0,0),(1007,1,0.5,7,0,0,0,0,0,0,0,0,0),(1008,1,0.5,7,0,0,0,0,0,0,0,0,0),(1009,1,0.5,7,0,0,0,0,0,0,0,0,0),(1010,1,0.5,7,0,0,0,0,0,0,0,0,0),(1011,1,0.5,7,0,0,0,0,0,0,0,0,0),(1012,1,0.5,7,2,0.125,6,3,0.75,5,4,1,4);
        CREATE TABLE `reputation_reward_rate` (`faction` smallint unsigned NOT NULL, `quest_rate` float, `creature_rate` float, `spell_rate` float, PRIMARY KEY (`faction`));
        INSERT INTO `reputation_reward_rate` VALUES (99,0,2,1),(529,1,1,1),(576,1.5,0.5,0.25);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    internal static ReputationTemplatesDumpImporter Import(string dump)
    {
        var importer = new ReputationTemplatesDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void BothTables_HaveASpec_ThatMapsTheClassicDbColumns()
    {
        TableSpec spillover = Assert.IsType<TableSpec>(ContentTableSpecs.Find("reputation_spillover_template"));
        TableSpec rate = Assert.IsType<TableSpec>(ContentTableSpecs.Find("reputation_reward_rate"));

        Assert.All(new[] { "faction1", "rate_1", "rank_1", "faction4", "rate_4", "rank_4" }, c => Assert.True(spillover.IsMapped(c), c));
        Assert.All(new[] { "quest_rate", "creature_rate", "spell_rate" }, c => Assert.True(rate.IsMapped(c), c));
        Assert.False(spillover.IsMapped("comment"));
    }

    [Fact]
    public void TheDump_MapsTwelveSpilloverRows_AndThreeRewardRates()
    {
        ReputationTemplatesDumpImporter importer = Import(Dump);

        ReputationTemplatesImportReport report = importer.BuildReport();
        Assert.Equal((12, 3), (report.Spillovers, report.RewardRates));
        Assert.Empty(report.Warnings);
        ReputationSpilloverTemplateRow bootyBay = importer.SpilloverSnapshot().Single(r => r.Faction == 21);
        Assert.Equal((72u, 0.25f, (byte)7, 99u, 0.5f, (byte)3, 87u, 0.5f, (byte)7, 0u), (bootyBay.Faction1, bootyBay.Rate1, bootyBay.Rank1,
            bootyBay.Faction2, bootyBay.Rate2, bootyBay.Rank2, bootyBay.Faction3, bootyBay.Rate3, bootyBay.Rank3, bootyBay.Faction4));
        ReputationRewardRateRow tuned = importer.RateSnapshot().Single(r => r.Faction == 576);
        Assert.Equal((1.5f, 0.5f, 0.25f), (tuned.QuestRate, tuned.CreatureRate, tuned.SpellRate));
    }

    [Fact]
    public void ContentRows_CarryTheTemplateRates_ToTheReputationFeature()
    {
        ReputationContentRows rows = Import(Dump).ToContentRows();

        ReputationSpilloverTemplate template = rows.Spillovers.Single(s => s.Faction == 21);
        Assert.Equal(0.25f, template.Targets[0].Rate);
        Assert.Equal(72u, template.Targets[0].Faction);
        Assert.Equal(new ReputationRewardRate(99, 0f, 2f, 1f), rows.Rates.Single(r => r.Faction == 99));
    }

    [Fact]
    public void ARepeatedKey_KeepsTheLaterRow_AndWarns()
    {
        ReputationTemplatesDumpImporter importer = Import(Dump + "\nINSERT INTO `reputation_reward_rate` (`faction`,`quest_rate`) VALUES (529,3);");

        Assert.Equal(3f, importer.RateSnapshot().Single(r => r.Faction == 529).QuestRate);
        Assert.Contains("1 duplicate key row(s)", Assert.Single(importer.BuildReport().Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError_NotFactionZero()
    {
        var importer = new ReputationTemplatesDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `reputation_reward_rate` (`fac`,`quest_rate`) VALUES (529,3);")));

        Assert.Equal("reputation_reward_rate", ex.Table);
        Assert.Equal("faction", ex.Column);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_StoresBothTables_AndTheSourceLoadsThem(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new ReputationRewardRateRow { Faction = 9999 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            ReputationTemplatesImportReport report = await Import(Dump).WriteAsync(db, replace: true);

            Assert.Equal((12, 3), (report.Spillovers, report.RewardRates));
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        ReputationContentRows rows = await new EfReputationContentSource(verify).LoadAsync();
        Assert.Equal(12, rows.Spillovers.Count);
        Assert.Equal([99u, 529u, 576u], rows.Rates.Select(r => r.Faction));
        Assert.Equal(0.125f, rows.Spillovers.Single(s => s.Faction == 1012).Targets[1].Rate);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Import(Dump).WriteAsync(db, replace: false);
            await Assert.ThrowsAsync<DbUpdateException>(() => Import(Dump).WriteAsync(db, replace: false));
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(12, await verify.Set<ReputationSpilloverTemplateRow>().CountAsync());
        Assert.Equal(3, await verify.Set<ReputationRewardRateRow>().CountAsync());
    }
}
