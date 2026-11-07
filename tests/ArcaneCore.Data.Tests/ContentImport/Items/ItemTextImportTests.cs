using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Items;

/// <summary>
/// <c>page_text</c> and <c>item_enchantment_template</c> (world schema 70) reach the world database through <c>import</c> and <c>refresh</c>, and the
/// world's stores read them back; the world no longer reads a dump at start. Dumps are hand-written with synthetic ids.
/// </summary>
public sealed class ItemTextImportTests : IDisposable
{
    private const string PageDump = """
        CREATE TABLE `page_text` (`entry` mediumint unsigned NOT NULL, `text` longtext NOT NULL, `next_page` mediumint unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `page_text` VALUES (1,'Chapter one.$B$BIt\'s a start.',2),(2,'Chapter two.',0),(7,'Loose leaf',99);
        """;

    // vmangos layout: the patch range keeps only the rows that hold patch 10; 6/1003 ends at patch 9 and 7/1004 starts at patch 11.
    private const string EnchantDump = """
        CREATE TABLE `item_enchantment_template` (`entry` int unsigned NOT NULL, `ench` int unsigned NOT NULL, `chance` float NOT NULL, `patch_min` tinyint unsigned NOT NULL DEFAULT '0', `patch_max` tinyint unsigned NOT NULL DEFAULT '10', PRIMARY KEY (`entry`,`ench`,`patch_min`));
        INSERT INTO `item_enchantment_template` VALUES (6,1001,25.5,0,10),(6,1002,74.5,0,10),(6,1003,10,0,9),(7,1004,100,11,12);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-item-text-" + Guid.NewGuid().ToString("N"));

    public ItemTextImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("page_text", "entry")]
    [InlineData("page_text", "text")]
    [InlineData("page_text", "next_page")]
    [InlineData("item_enchantment_template", "entry")]
    [InlineData("item_enchantment_template", "ench")]
    [InlineData("item_enchantment_template", "chance")]
    [InlineData("item_enchantment_template", "patch_max")]
    public void TheSpecNamesTheTablesAndTheColumnsTheImporterReads(string table, string column)
    {
        TableSpec spec = Assert.IsType<TableSpec>(ContentTableSpecs.Find(table));
        Assert.True(spec.IsMapped(column));
        Assert.False(spec.IsMapped("not_a_column"));
    }

    [Fact]
    public void Read_KeepsEveryPage_WithEscapesResolved_AndOnlyThePatchTenTemplateRows()
    {
        var importer = new ItemTextDumpImporter();
        importer.Read(new StringReader(PageDump + EnchantDump));

        Assert.Equal(3, importer.BuildReport().Pages);
        Assert.Equal(2, importer.BuildReport().EnchantmentTemplates);
        Assert.Equal((1u, "Chapter one.$B$BIt's a start.", 2u), importer.PageSnapshot().Select(p => (p.Entry, p.Text, p.NextPage)).First());
        Assert.Equal(99u, importer.PageSnapshot().Single(p => p.Entry == 7).NextPage); // a missing next page is the catalog's check, the row is kept
        Assert.Equal([(6u, 1001u, 25.5f), (6u, 1002u, 74.5f)], importer.TemplateSnapshot().Select(t => (t.Entry, t.Ench, t.Chance)));
    }

    [Fact]
    public void Read_ALaterRowReplacesAnEarlierOneWithTheSameKey()
    {
        var importer = new ItemTextDumpImporter();
        importer.Read(new StringReader(PageDump));
        importer.Read(new StringReader("INSERT INTO `page_text` (`entry`,`text`,`next_page`) VALUES (2,'Rewritten',0);"));

        Assert.Equal(3, importer.BuildReport().Pages);
        Assert.Equal("Rewritten", importer.PageSnapshot().Single(p => p.Entry == 2).Text);
    }

    [Fact]
    public void Read_ANonNumericEntryRefusesTheDump()
    {
        var importer = new ItemTextDumpImporter();

        Assert.Throws<InvalidDataException>(() => importer.Read(new StringReader(
            "CREATE TABLE `page_text` (`entry` int, `text` text, `next_page` int);\nINSERT INTO `page_text` VALUES ('x','a',0);")));
    }

    [Fact]
    public async Task Import_WritesBothTables_AndTheStoresLoadThem()
    {
        string database = Path.Combine(_directory, "world.db");

        (int code, string output, string error) = await Run("import", Write("text.sql", PageDump + EnchantDump), "--database", database);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Empty(error);
        Assert.Contains("page_text  3", output, StringComparison.Ordinal);
        Assert.Contains("item_enchantment_template  2", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        IReadOnlyList<PageTextRecord> pages = await new EfPageTextStore(db).LoadAsync();
        Assert.Equal([1u, 2u, 7u], pages.Select(p => p.Entry));
        Assert.Equal(new PageTextRecord(1, "Chapter one.$B$BIt's a start.", 2), pages[0]);
        IReadOnlyList<ItemEnchantmentChance> chances = await new EfItemEnchantmentTemplateStore(db).LoadAsync();
        Assert.Equal([new ItemEnchantmentChance(6, 1001, 25.5f), new ItemEnchantmentChance(6, 1002, 74.5f)], chances);
    }

    [Fact]
    public async Task Import_OfAnExistingKey_FailsWithoutReplace_AndReplaceKeepsTheTableTheDumpLacks()
    {
        string database = Path.Combine(_directory, "world.db");
        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("text.sql", PageDump + EnchantDump), "--database", database)).Code);

        Assert.NotEqual(ExitCodes.Ok, (await Run("import", Write("again.sql", PageDump), "--database", database)).Code);
        await using (WorldDbContext unchanged = Open(database))
        {
            Assert.Equal(3, await unchanged.Set<PageTextRow>().CountAsync());
        }

        // A dump with pages only must not empty the enchantment groups an earlier import wrote.
        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("pages.sql", "INSERT INTO `page_text` (`entry`,`text`,`next_page`) VALUES (9,'Only',0);"), "--database", database, "--replace")).Code);
        await using WorldDbContext db = Open(database);
        PageTextRow only = Assert.Single(await db.Set<PageTextRow>().AsNoTracking().ToListAsync());
        Assert.Equal((9u, "Only", 0u), (only.Entry, only.Text, only.NextPage));
        Assert.Equal(2, await db.Set<ItemEnchantmentTemplateRow>().CountAsync());
    }

    [Fact]
    public async Task Refresh_ReplacesTheTablesTheDumpCarries_AndLeavesTheOthers()
    {
        string database = Path.Combine(_directory, "world.db");
        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("text.sql", PageDump + EnchantDump), "--database", database)).Code);

        (int code, string output, string error) = await Run("refresh",
            Write("pages.sql", "INSERT INTO `page_text` (`entry`,`text`,`next_page`) VALUES (4,'Fresh',0);"), "--database", database);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("page_text  1", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        PageTextRow fresh = Assert.Single(await db.Set<PageTextRow>().AsNoTracking().ToListAsync());
        Assert.Equal((4u, "Fresh"), (fresh.Entry, fresh.Text));
        Assert.Equal(2, await db.Set<ItemEnchantmentTemplateRow>().CountAsync());
    }

    [Fact]
    public void TheWorldSchemaStepCreatesBothTables_OnTheNextWorldVersion()
    {
        IDataModule module = new ItemTextWorldDataModule();

        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, module.SchemaVersion);
        Assert.Equal(["item_enchantment_template", "page_text"], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table).Order(StringComparer.Ordinal));
    }

    private string Write(string name, string sql)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, sql);
        return path;
    }

    private static WorldDbContext Open(string database)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options);

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }
}
