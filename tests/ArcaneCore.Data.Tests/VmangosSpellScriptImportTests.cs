using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// vmangos <c>spell_scripts</c> through <see cref="DbScriptDumpImporter"/> and <see cref="VmangosSpellScriptTranslator"/>: the verified
/// translation, the whole-spell refusals, classic-db's precedence and the resolution of TEAM conditions by meaning. The rows are synthetic
/// unless named; the column lists are vmangos' and classic-db's.
/// </summary>
public sealed class VmangosSpellScriptImportTests
{
    private const string VmangosColumns =
        "`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`datalong4`,`target_param1`,`target_param2`,`target_type`,`data_flags`,"
        + "`dataint`,`dataint2`,`dataint3`,`dataint4`,`x`,`y`,`z`,`o`,`condition_id`,`comments`";

    private const string ClassicColumns =
        "`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,"
        + "`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`";

    /// <summary>Read <paramref name="dump"/>, write it into a fresh world database (after <paramref name="seed"/>) and read the spell rows back.</summary>
    private static async Task<(DbScriptDumpImporter, List<SpellScriptRow>)> WriteAsync(string dump, Action<WorldDbContext>? seed = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcane-spellscripts-data-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            DbScriptDumpImporter importer = Read(dump);
            await using (var db = new WorldDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
                seed?.Invoke(db);
                await db.SaveChangesAsync();
                await importer.ReplaceAsync(db);
            }

            await using var read = new WorldDbContext(options);
            return (importer, await read.Set<SpellScriptRow>().OrderBy(r => r.Id).ThenBy(r => r.Ordinal).ToListAsync());
        }
        finally
        {
            File.Delete(path); // Pooling=False: no pooled connection holds the file
        }
    }

    private static DbScriptDumpImporter Read(string dump)
    {
        var importer = new DbScriptDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public async Task Translation_SecondsToMilliseconds_SwapsToReverse_AndCastFlagsToTriggered()
    {
        (DbScriptDumpImporter importer, List<SpellScriptRow> rows) = await WriteAsync($"""
            INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES
            (1,2,0,15,100,6,0,0,0,0,0,3,0,0,0,0,0,0,0,0,0,'both swaps cancel; CF_TRIGGERED|CF_FORCE_CAST'),
            (1,0,0,15,101,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,'one swap'),
            (1,0,0,17,5000,2,0,0,0,0,0,4,0,0,0,0,0,0,0,0,0,'create item; target self'),
            (1,0,0,18,0,0,0,0,0,0,0,16,0,0,0,0,0,0,0,0,0,'despawn the creature source; skip missing');
            """);

        Assert.Empty(importer.VmangosSpellScriptRejections);
        Assert.Equal(
            [(2000u, 15u, 100u, 1u, 0u), (0u, 15u, 101u, 0u, 0x02u), (0u, 17u, 5000u, 2u, 0x04u), (0u, 18u, 0u, 0u, 0x04u)],
            rows.Select(r => (r.Delay, r.Command, r.DataLong, r.DataLong2, r.DataFlags)));
    }

    [Theory]
    [InlineData("0,0,0,0,1,0,0,0,0,0", "target_type 1")]                       // a vmangos target search
    [InlineData("0,0,0,0,0,8,0,0,0,0", "data_flags 0x8")]                       // SF_GENERAL_ABORT_ON_FAILURE
    [InlineData("0,0,0,0,0,0,7,0,0,0", "dataint")]                              // random alternatives in cmangos
    public void ARefusedRow_RefusesItsWholeSpell(string middle, string why)
    {
        // datalong3, datalong4, target_param1, target_param2, target_type, data_flags, dataint, dataint2, dataint3, dataint4
        string[] v = middle.Split(',');
        DbScriptDumpImporter importer = Read($"""
            INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES
            (2,0,0,15,100,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'fine'),
            (2,0,0,15,101,0,{v[0]},{v[1]},{v[2]},{v[3]},{v[4]},{v[5]},{v[6]},{v[7]},{v[8]},{v[9]},0,0,0,0,0,'refused');
            """);

        Assert.Empty(importer.VmangosSpellScriptIds);
        Assert.Contains(why, importer.VmangosSpellScriptRejections[2], StringComparison.Ordinal);
        Assert.Equal(0, importer.Counts[DbScriptDataModule.SpellTable]);
    }

    [Theory]
    [InlineData(0, "command 0")]     // TALK: vmangos broadcast text ids
    [InlineData(9, "command 9")]     // RESPAWN_GAMEOBJECT: vmangos spawn guids
    [InlineData(6, "command 6")]     // TELEPORT_TO: not verified
    public void UntranslatedCommands_AreRefused(int command, string why)
    {
        DbScriptDumpImporter importer = Read($"INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES (3,0,0,{command},1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'x');");

        Assert.Contains(why, importer.VmangosSpellScriptRejections[3], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCmangosScriptNameBinding_NamedSpellScriptsToo_IsNotReadAsSteps()
    {
        // classic-db z2815 has its own spell_scripts: the ScriptName binding (Id, ScriptName), no DB script steps; its INSERT names no
        // columns, so they come from the CREATE TABLE (its first rows).
        DbScriptDumpImporter importer = Read("""
            CREATE TABLE `spell_scripts` (
              `Id` int(11) NOT NULL,
              `ScriptName` char(64) NOT NULL,
              PRIMARY KEY (`Id`)
            );
            INSERT INTO `spell_scripts` VALUES (10,'spell_blizzard'),(433,'spell_food_animation');
            """);

        Assert.Empty(importer.VmangosSpellScriptIds);
        Assert.Empty(importer.VmangosSpellScriptRejections);
        Assert.False(importer.HasRows);
    }

    [Fact]
    public void ClassicDbRows_WinOverVmangosRowsOfTheSameSpell()
    {
        DbScriptDumpImporter importer = Read($"""
            INSERT INTO `dbscripts_on_spell` ({ClassicColumns}) VALUES
            (9096,0,0,15,9738,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'classic-db');
            INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES
            (9096,0,0,15,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'vmangos'),
            (9096,0,0,15,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'vmangos');
            """);

        Assert.Equal(1, importer.Counts[DbScriptDataModule.SpellTable]);
    }

    [Fact]
    public async Task Conditions_ResolveByMeaning_FromTheInputOrTheWorld_AndAnUnresolvedOneDropsItsSpell()
    {
        // vmangos 2 is TEAM 67 (Horde), resolved from the classic-db rows of the input (classic-db 3); vmangos 3 is TEAM 469 (Alliance),
        // which the input's classic-db rows lack, so the world's own conditions table answers (an existing classic-db 4). vmangos 9 is a
        // quest condition: not a type that means the same in both cores, so spell 12 is left out whole.
        string dump = $"""
            INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`,`comments`) VALUES
            (3,6,67,0,0,0,0,'Horde Player');
            INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES
            (2,6,67,0,0,0,0),(3,6,469,0,0,0,0),(9,9,5944,0,0,0,0);
            INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES
            (11,0,0,15,100,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2,'horde'),
            (11,0,0,15,101,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,3,'alliance'),
            (12,0,0,15,102,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,9,'quest');
            """;
        (DbScriptDumpImporter importer, List<SpellScriptRow> rows) = await WriteAsync(dump,
            db => db.Set<ConditionRow>().Add(new ConditionRow { ConditionEntry = 4, Type = 6, Value1 = 469 }));

        Assert.Equal([(11u, 100u, 3u), (11u, 101u, 4u)], rows.Select(r => (r.Id, r.DataLong, r.ConditionId)));
        Assert.Contains(importer.Warnings, w => w.Contains("spell_scripts 12", StringComparison.Ordinal) && w.Contains("condition 9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conditions_WithNegativeValues_DoNotAbortTheImport_AndATeamConditionStillResolves()
    {
        // vmangos conditions.value1..4 are signed int(11); these two rows are verbatim from its dump (mangos.sql) and used to throw an
        // OverflowException that aborted the whole import. Neither is a TEAM row, so spell 12 (condition 4866) is left out whole.
        string dump = $"""
            INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`,`comments`) VALUES
            (3,6,67,0,0,0,0,'Horde Player');
            INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES
            (4866,1,16468,-1,0,0,0),(487,54,-287,-1872,93,50,0),(2,6,67,0,0,0,0);
            INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES
            (11,0,0,15,100,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2,'horde'),
            (12,0,0,15,102,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,4866,'negative');
            """;
        (DbScriptDumpImporter importer, List<SpellScriptRow> rows) = await WriteAsync(dump, null);

        Assert.Equal([(11u, 100u, 3u)], rows.Select(r => (r.Id, r.DataLong, r.ConditionId)));
        Assert.Contains(importer.Warnings, w => w.Contains("spell_scripts 12", StringComparison.Ordinal) && w.Contains("condition 4866", StringComparison.Ordinal));
    }
}
