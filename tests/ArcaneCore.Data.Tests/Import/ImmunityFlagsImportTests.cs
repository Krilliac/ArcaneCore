using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.Import;

/// <summary>
/// vmangos <c>creature_template.immunity_flags</c>: the content plan lists the column as read (its TAUNT bit becomes creature_immunities rows),
/// and a refresh of a vmangos dump with no <c>creature_immunities</c> table still counts those rows.
/// </summary>
public sealed class ImmunityFlagsImportTests
{
    private const string Dump = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`immunity_flags`) VALUES
        (930501,0,'Taunt Proof','',60,60,1,2),(930502,0,'Plain','',60,60,1,0);
        """;

    [Fact]
    public void Plan_ListsImmunityFlagsAsImported()
    {
        ScanResult scan = ContentScanner.Scan([DumpInput.Text("t.sql", Dump)]);

        Assert.Contains("immunity_flags", scan.Tables["creature_template"].MappedColumns);
        Assert.DoesNotContain("immunity_flags", scan.Tables["creature_template"].UnmappedColumns);
    }

    [Fact]
    public async Task Refresh_OfAVMangosDump_ReportsTheImmunityRowCount()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-immunity-flags-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            await File.WriteAllTextAsync(path, Dump);
            var output = new StringWriter();
            var error = new StringWriter();

            int code = await ContentImporterCli.RunAsync(["refresh", path, "--dry-run"], output, error, CancellationToken.None);

            Assert.True(code == ExitCodes.Ok, error + output.ToString());
            Assert.Contains("  creature_immunities  2", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
