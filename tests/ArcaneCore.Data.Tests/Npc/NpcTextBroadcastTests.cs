using System.IO;
using ArcaneCore.Data;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

/// <summary>
/// <c>npc_text_broadcast_text</c>: gossip texts whose variants are <c>broadcast_text</c> ids (classic-db, vmangos). The store resolves
/// them into the same <see cref="NpcText"/> the inline <c>npc_text</c> rows produce.
/// </summary>
public sealed class NpcTextBroadcastTests : IAsyncLifetime
{
    private const string BroadcastDump =
        "INSERT INTO `npc_text_broadcast_text` (`Id`,`Prob0`,`Prob1`,`BroadcastTextId0`,`BroadcastTextId1`) VALUES (9001,1,0.5,701,702);";

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void TheDumpTableIsTrackedAndTheSpecMapsItsColumns()
    {
        Assert.Contains("npc_text_broadcast_text", NpcDumpImporter.TrackedTables);
        Assert.True(NpcDumpImporter.ReadsColumn("npc_text_broadcast_text", "BroadcastTextId7"));
        Assert.True(NpcDumpImporter.ReadsColumn("npc_text_broadcast_text", "Prob3"));
        TableSpec spec = Assert.Single(ContentTableSpecs.All, s => s.Table == "npc_text_broadcast_text");
        Assert.True(spec.IsMapped("BroadcastTextId0"));
    }

    [Fact]
    public void TheDumpRowsAreReadAndCounted()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(BroadcastDump));
        Assert.Equal(1, importer.BuildReport().TextBroadcasts);
        Assert.True(importer.SawTextBroadcastTable);
    }

    [Fact]
    public void ADumpRowWithoutAnIdIsSkippedWithADiagnostic()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `npc_text_broadcast_text` (`Id`,`Prob0`,`BroadcastTextId0`) VALUES (0,1,701);"));
        NpcImportReport report = importer.BuildReport();
        Assert.Equal(0, report.TextBroadcasts);
        Assert.Equal(1, report.Skipped);
    }

    [Fact]
    public async Task ABroadcastOnlyTextIdResolvesToTheBroadcastTextsWithTheirEmotes()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(BroadcastDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<BroadcastTextRow>().AddRange(
                new BroadcastTextRow
                {
                    Id = 701, Text = "Well met, traveler.", FemaleText = "Well met, traveler (f).", Language = 7,
                    EmoteId1 = 1, EmoteId2 = 2, EmoteId3 = 3, EmoteDelay1 = 10, EmoteDelay2 = 20, EmoteDelay3 = 30,
                },
                new BroadcastTextRow { Id = 702, Text = "Second line.", FemaleText = string.Empty, Language = 0 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await importer.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            NpcContent content = await new EfNpcContentStore(db).LoadAsync();
            NpcText text = Assert.Single(content.NpcTexts, t => t.Id == 9001);
            Assert.Equal(8, text.Options.Count);
            Assert.Equal(new NpcTextOption(1f, "Well met, traveler.", "Well met, traveler (f).", 7, 10, 1, 20, 2, 30, 3), text.Options[0]);
            Assert.Equal(new NpcTextOption(0.5f, "Second line.", string.Empty, 0, 0, 0, 0, 0, 0, 0), text.Options[1]);
            for (int i = 2; i < 8; i++)
            {
                Assert.Equal(0f, text.Options[i].Probability);
                Assert.Equal(string.Empty, text.Options[i].Text0);
            }
        }
    }

    [Fact]
    public async Task AnInlineTextIdIsUnchangedEvenWhenTheBroadcastTableHasTheSameId()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<NpcTextRow>().Add(new NpcTextRow { Id = 9001, Text0_0 = "Inline male", Text0_1 = "Inline female", Prob0 = 1, Lang0 = 1 });
        db.Set<NpcTextBroadcastRow>().Add(new NpcTextBroadcastRow { Id = 9001, Prob0 = 1, BroadcastTextId0 = 701 });
        db.Set<BroadcastTextRow>().Add(new BroadcastTextRow { Id = 701, Text = "Broadcast", FemaleText = "Broadcast f" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        NpcContent content = await new EfNpcContentStore(db).LoadAsync();
        NpcText text = Assert.Single(content.NpcTexts, t => t.Id == 9001);
        Assert.Equal(new NpcTextOption(1f, "Inline male", "Inline female", 1, 0, 0, 0, 0, 0, 0), text.Options[0]);
    }

    [Fact]
    public async Task AVariantWhoseBroadcastTextIsMissingIsNeverChosen()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<NpcTextBroadcastRow>().Add(new NpcTextBroadcastRow { Id = 9002, Prob0 = 1, BroadcastTextId0 = 999 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        NpcContent content = await new EfNpcContentStore(db).LoadAsync();
        NpcText text = Assert.Single(content.NpcTexts, t => t.Id == 9002);
        Assert.All(text.Options, o => Assert.Equal(0f, o.Probability));
    }

    [Fact]
    public async Task RefreshReplacesTheBroadcastRowsAndLeavesTheInlineTextsAlone()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<NpcTextRow>().Add(new NpcTextRow { Id = 1, Text0_0 = "keep" });
        db.Set<NpcTextBroadcastRow>().Add(new NpcTextBroadcastRow { Id = 5, BroadcastTextId0 = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(BroadcastDump));
        Assert.Equal(1, await importer.RefreshTextBroadcastsAsync(db));
        Assert.Equal(9001u, (await db.Set<NpcTextBroadcastRow>().SingleAsync()).Id);
        Assert.Equal("keep", (await db.Set<NpcTextRow>().SingleAsync()).Text0_0);

        var partial = new NpcDumpImporter();
        partial.Read(new StringReader("INSERT INTO `npc_gossip` (`npc_guid`,`textid`) VALUES (1,1);"));
        Assert.Equal(0, await partial.RefreshTextBroadcastsAsync(db));
        Assert.Equal(9001u, (await db.Set<NpcTextBroadcastRow>().SingleAsync()).Id);
    }

    /// <summary>The whole classic-db z2815 dump: every text id an npc_gossip or gossip_menu row names must resolve to a text.</summary>
    [ClassicDbDumpFact]
    public void RealClassicDbDump_LeavesNoReferencedTextIdUnresolved()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), "the dump variable is set but the file does not exist");
        var importer = new NpcDumpImporter();
        using TextReader reader = DumpFiles.OpenText(path);
        importer.Read(reader);
        Assert.True(importer.SawTextBroadcastTable);
        Assert.True(importer.BuildReport().TextBroadcasts > 0);
        Assert.Empty(importer.UnresolvedTextIds());
    }

    [Fact]
    public void ATextIdNamedByAGossipRowResolvesFromEitherTextTable()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(
            "INSERT INTO `npc_text_broadcast_text` (`Id`,`Prob0`,`BroadcastTextId0`) VALUES (9001,1,701);\n" +
            "INSERT INTO `gossip_menu` (`entry`,`text_id`,`condition_id`) VALUES (1,9001,0),(2,9500,0);\n" +
            "INSERT INTO `npc_gossip` (`npc_guid`,`textid`) VALUES (7,9001);"));
        Assert.Equal([9500u], importer.UnresolvedTextIds());
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
}
