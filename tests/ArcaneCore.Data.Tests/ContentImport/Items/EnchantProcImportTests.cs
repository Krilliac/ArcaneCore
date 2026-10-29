using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Items;

/// <summary>
/// <c>spell_proc_item_enchant</c> (cmangos classic-db and vmangos) and <c>spell_enchant_charges</c> (vmangos only)
/// reach the world database through <c>import</c>. Dumps are hand-written with synthetic ids.
/// </summary>
public sealed class EnchantProcImportTests : IDisposable
{
    // Seven proc rows, the count the classic-db dump carries; the ppm values include integers, a trailing-zero float and a fraction.
    private const string ProcDump = """
        CREATE TABLE `spell_proc_item_enchant` (`entry` mediumint unsigned NOT NULL, `ppmRate` float NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `spell_proc_item_enchant` VALUES (8034,9),(13897,6.0),(20004,6.0),(20005,1.6),(21001,2.5),(21002,3),(21003,0.5);
        """;

    private const string ChargesDump = """
        CREATE TABLE `spell_enchant_charges` (`entry` smallint unsigned NOT NULL, `charges` int unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `spell_enchant_charges` VALUES (2823,60),(8679,40),(14792,15);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-enchant-proc-" + Guid.NewGuid().ToString("N"));

    public EnchantProcImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("spell_proc_item_enchant", "ppmRate")]
    [InlineData("spell_proc_item_enchant", "entry")]
    [InlineData("spell_enchant_charges", "charges")]
    [InlineData("spell_enchant_charges", "entry")]
    public void TheSpecNamesTheTablesAndTheColumnsTheImporterReads(string table, string column)
    {
        TableSpec spec = Assert.IsType<TableSpec>(ContentTableSpecs.Find(table));
        Assert.True(spec.IsMapped(column));
        Assert.False(spec.IsMapped("not_a_column"));
    }

    [Fact]
    public void ProcRows_MapByName_WithTheirPpmRates()
    {
        var importer = new EnchantProcDumpImporter();
        importer.Read(new StringReader(ProcDump));

        Assert.Equal(7, importer.BuildReport().ProcEnchants);
        Dictionary<uint, float> ppm = importer.ProcSnapshot().ToDictionary(r => r.Entry, r => r.PpmRate);
        Assert.Equal(9f, ppm[8034]);
        Assert.Equal(6f, ppm[13897]);
        Assert.Equal(1.6f, ppm[20005]);
        Assert.Equal(0.5f, ppm[21003]);
    }

    [Fact]
    public void ChargeRows_MapByName_AndALaterRowReplacesAnEarlierOne()
    {
        var importer = new EnchantProcDumpImporter();
        importer.Read(new StringReader(ChargesDump));
        importer.Read(new StringReader("INSERT INTO `spell_enchant_charges` (`entry`,`charges`) VALUES (2823,99);"));

        Assert.Equal(3, importer.BuildReport().EnchantCharges);
        Assert.Equal(99u, importer.ChargesSnapshot().Single(r => r.Entry == 2823).Charges);
        Assert.Equal(40u, importer.ChargesSnapshot().Single(r => r.Entry == 8679).Charges);
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError_NotEntryZero()
    {
        var importer = new EnchantProcDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `spell_proc_item_enchant` (`spell`,`ppmRate`) VALUES (8034,9);")));

        Assert.Equal("spell_proc_item_enchant", ex.Table);
        Assert.Equal("entry", ex.Column);
    }

    [Fact]
    public async Task Import_WritesTheSevenProcRows_AndTheStoresLoadThem()
    {
        string database = Path.Combine(_directory, "world.db");

        (int code, string output, string error) = await Run("import", Write("procs.sql", ProcDump), "--database", database);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Empty(error);
        Assert.Contains("spell_proc_item_enchant  7", output, StringComparison.Ordinal);
        Assert.Contains("spell_enchant_charges  0", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        IReadOnlyList<ItemEnchantProc> procs = await new EfItemEnchantProcStore(db).LoadAsync();
        Assert.Equal(7, procs.Count);
        Assert.Equal(new ItemEnchantProc(20005, 1.6f), procs.Single(p => p.SpellId == 20005));
        Assert.Equal(new ItemEnchantProc(8034, 9f), procs.Single(p => p.SpellId == 8034));
    }

    [Fact]
    public async Task Import_WritesTheCharges_WhichTheStoreLoadsPerEnchantSpell()
    {
        string database = Path.Combine(_directory, "world.db");

        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("charges.sql", ChargesDump), "--database", database)).Code);

        await using WorldDbContext db = Open(database);
        IReadOnlyList<SpellEnchantCharges> charges = await new EfSpellEnchantChargesStore(db).LoadAsync();
        Assert.Equal([(2823u, 60u), (8679u, 40u), (14792u, 15u)], charges.OrderBy(c => c.SpellId).Select(c => (c.SpellId, c.Charges)));
    }

    [Fact]
    public async Task Replace_RewritesTheTablesTheDumpCarries_AndKeepsTheOnesItDoesNot()
    {
        string database = Path.Combine(_directory, "world.db");
        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("charges.sql", ChargesDump), "--database", database)).Code);

        // A classic-db style dump has procs and no charges table: --replace must not empty the charges a vmangos import wrote.
        for (int run = 0; run < 2; run++)
        {
            Assert.Equal(ExitCodes.Ok, (await Run("import", Write("procs.sql", ProcDump), "--database", database, "--replace")).Code);
        }

        await using WorldDbContext db = Open(database);
        Assert.Equal(7, await db.Set<ItemEnchantProcRow>().CountAsync());
        Assert.Equal(3, await db.Set<SpellEnchantChargesRow>().CountAsync());

        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("charges2.sql", "INSERT INTO `spell_enchant_charges` (`entry`,`charges`) VALUES (2823,5);"), "--database", database, "--replace")).Code);
        await using WorldDbContext after = Open(database);
        SpellEnchantChargesRow only = Assert.Single(await after.Set<SpellEnchantChargesRow>().AsNoTracking().ToListAsync());
        Assert.Equal((2823u, 5u), (only.Entry, only.Charges));
        Assert.Equal(7, await after.Set<ItemEnchantProcRow>().CountAsync());
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
