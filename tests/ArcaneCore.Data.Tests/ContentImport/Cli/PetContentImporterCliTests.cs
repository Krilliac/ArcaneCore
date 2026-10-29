using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Pets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// Production CLI wiring for <c>pet_levelstats</c> and <c>petcreateinfo_spell</c> against SQLite. The importer itself is
/// covered by <see cref="Pets.PetDumpImporterTests"/>; this pins that <c>import</c> reads and writes the pet tables and
/// that <c>verify</c> counts them. The dump is hand-written in the cmangos classic-db column layout with invented rows.
/// </summary>
public sealed class PetContentImporterCliTests : IDisposable
{
    private const string Dump = """
        CREATE TABLE `pet_levelstats` (`creature_entry` mediumint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `hp` int unsigned NOT NULL, `mana` int unsigned NOT NULL, `armor` int unsigned NOT NULL, `str` int unsigned NOT NULL, `agi` int unsigned NOT NULL, `sta` int unsigned NOT NULL, `inte` int unsigned NOT NULL, `spi` int unsigned NOT NULL, PRIMARY KEY (`creature_entry`, `level`));
        INSERT INTO `pet_levelstats` VALUES (416,1,50,40,10,21,22,23,24,25),(416,20,300,260,90,31,32,33,34,35),(417,1,80,0,12,1,2,3,4,5);
        CREATE TABLE `petcreateinfo_spell` (`entry` mediumint unsigned NOT NULL, `Spell1` mediumint unsigned NOT NULL, `Spell2` mediumint unsigned NOT NULL, `Spell3` mediumint unsigned NOT NULL, `Spell4` mediumint unsigned NOT NULL, PRIMARY KEY (`entry`));
        INSERT INTO `petcreateinfo_spell` VALUES (416,3110,7799,0,0),(1860,3716,0,0,0);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-pet-cli-" + Guid.NewGuid().ToString("N"));

    public PetContentImporterCliTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Import_WritesPetTables_AndVerifyCountsThem()
    {
        string source = Path.Combine(_directory, "pets.sql");
        File.WriteAllText(source, Dump);
        string database = Path.Combine(_directory, "world.db");

        (int code, string output, string error) = await Run("import", source, "--database", database);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Empty(error);
        Assert.Contains("pet_levelstats  3", output, StringComparison.Ordinal);
        Assert.Contains("petcreateinfo_spell  2", output, StringComparison.Ordinal);

        await using (WorldDbContext db = Open(database))
        {
            Assert.Equal([(416u, 1), (416u, 20), (417u, 1)],
                (await db.Set<PetLevelStatsRow>().AsNoTracking().OrderBy(r => r.Entry).ThenBy(r => r.Level).ToListAsync())
                    .Select(r => (r.Entry, (int)r.Level)));
            PetCreateSpellRow spells = await db.Set<PetCreateSpellRow>().AsNoTracking().SingleAsync(r => r.Entry == 416);
            Assert.Equal([3110u, 7799u, 0u, 0u], [spells.Spell1, spells.Spell2, spells.Spell3, spells.Spell4]);
            Assert.Equal(2, await db.Set<PetCreateSpellRow>().CountAsync());
        }

        await VerifyWorldSeed.AddGeographyAsync(database);
        (int verifyCode, string verify, _) = await Run("verify", "--database", database);
        Assert.Equal(ExitCodes.Ok, verifyCode);
        Assert.Contains("pet_levelstats  3", verify, StringComparison.Ordinal);
        Assert.Contains("petcreateinfo_spell  2", verify, StringComparison.Ordinal);
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
