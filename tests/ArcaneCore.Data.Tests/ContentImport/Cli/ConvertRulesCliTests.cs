using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.Rules.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// <c>convert-rules</c> (inventory 12): EventAI tables of an imported world into creature-host rule rows. The dump rows are hand-written for the
/// test; converted rows live only in the temporary database.
/// </summary>
public sealed class ConvertRulesCliTests : IDisposable
{
    private const string Dump = """
        INSERT INTO `creature_ai_scripts` (`id`,`creature_id`,`event_type`,`event_inverse_phase_mask`,`event_chance`,`event_flags`,`event_param1`,`event_param2`,`event_param3`,`event_param4`,`action1_type`,`action1_param1`,`action1_param2`,`action1_param3`,`action2_type`,`action2_param1`,`action2_param2`,`action2_param3`,`action3_type`,`action3_param1`,`action3_param2`,`action3_param3`,`comment`) VALUES
        (9300101,930001,4,0,100,0,0,0,0,0,1,-9301,0,0,0,0,0,0,0,0,0,0,'say on aggro'),
        (9300102,930001,2,4,100,1,30,0,5000,8000,11,133,0,0,22,2,0,0,0,0,0,0,'cast then phase'),
        (9300103,930001,19,0,100,0,0,0,0,0,1,-9301,0,0,0,0,0,0,0,0,0,0,'unsupported event'),
        (9300104,930001,0,4294967295,100,1,1000,2000,3000,4000,20,1,0,0,0,0,0,0,0,0,0,0,'hidden in every phase'),
        (9300105,930001,0,0,100,1,1000,2000,3000,86400001,20,1,0,0,0,0,0,0,0,0,0,0,'timer over the cap');
        INSERT INTO `creature_ai_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`comment`) VALUES (-9301,'Grunt!',0,0,0,0,'aggro');
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-convert-rules-" + Guid.NewGuid().ToString("N"));

    public ConvertRulesCliTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ConvertRules_WritesTheBand_ReportsTheRest_AndIsIdempotent()
    {
        string db = await ImportAsync();
        await AddAuthoredRuleAsync(db);

        (int exit, string text) = await RunAsync("convert-rules", "--database", db);
        Assert.True(exit == ExitCodes.Ok, text);
        Assert.Contains("5 row(s), 2 converted, 1 written disabled, 2 not converted", text, StringComparison.Ordinal);
        Assert.Contains("creature_ai_scripts 9300103: Unconvertible: event type 19", text, StringComparison.Ordinal);
        Assert.Contains("creature_ai_scripts 9300105: Unconvertible: repeat timer 3000..86400001 ms is over the 24 h cap", text, StringComparison.Ordinal);
        Assert.Contains("creature_ai_scripts 9300104: Disabled: inverse phase mask hides the row in all 32 phases", text, StringComparison.Ordinal);

        string first = await SnapshotAsync(db);
        Assert.Contains("rule 5 authored", first, StringComparison.Ordinal);
        foreach (int id in new[] { RuleIdBands.CustomStart + 2, RuleIdBands.EventAi - 1, RuleIdBands.DbScripts, RuleIdBands.SmartAi + 9 })
        {
            Assert.Contains($"rule {id} foreign", first, StringComparison.Ordinal);
            Assert.Contains($"action {id}/0 ", first, StringComparison.Ordinal);
            Assert.Contains($"text {id} Foreign text", first, StringComparison.Ordinal);
        }

        Assert.Contains($"rule {(1 << 30) + 9300101} ", first, StringComparison.Ordinal);
        Assert.DoesNotContain($"rule {(1 << 30) + 9300103} ", first, StringComparison.Ordinal);
        Assert.Contains($"text {(1 << 30) + 500_000 + 9301} Grunt!", first, StringComparison.Ordinal);

        (int again, string secondText) = await RunAsync("convert-rules", "--database", db);
        Assert.True(again == ExitCodes.Ok, secondText);
        Assert.Equal(first, await SnapshotAsync(db));
    }

    [Fact]
    public async Task DryRun_ConvertsAndReports_ButWritesNothing()
    {
        string db = await ImportAsync();
        string report = Path.Combine(_directory, "report.txt");

        (int exit, string text) = await RunAsync("convert-rules", "--database", db, "--dry-run", "--report", report);

        Assert.True(exit == ExitCodes.Ok, text);
        Assert.Contains("dry run: no database written", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rule 1073", await SnapshotAsync(db), StringComparison.Ordinal);
        string[] lines = await File.ReadAllLinesAsync(report);
        Assert.StartsWith("# creature_ai_scripts: 5 row(s)", lines[0], StringComparison.Ordinal);
        Assert.Contains("9300103\tUnconvertible\tevent type 19 is not supported by the EventAI executor", lines);
    }

    [Fact]
    public async Task ConvertRules_WithoutEventAiRows_FailsWithTheSchemaExitCode_AndWritesNothing()
    {
        string db = Path.Combine(_directory, "empty.db");
        string source = Path.Combine(_directory, "empty.sql");
        await File.WriteAllTextAsync(source, "INSERT INTO `creature_ai_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`comment`) VALUES (-9302,'Only a text',0,0,0,0,'t');");
        (int imported, string importText) = await RunAsync("import", source, "--database", db);
        Assert.True(imported == ExitCodes.Ok, importText);

        (int exit, string text) = await RunAsync("convert-rules", "--database", db);

        Assert.Equal(ExitCodes.Schema, exit);
        Assert.Contains("no creature_ai_scripts rows", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertRules_TakesNoDump_AndNeedsADatabase()
    {
        (int withDump, _) = await RunAsync("convert-rules", "x.sql", "--database", Path.Combine(_directory, "x.db"));
        (int noTarget, _) = await RunAsync("convert-rules");

        Assert.Equal(ExitCodes.Usage, withDump);
        Assert.Equal(ExitCodes.Usage, noTarget);
    }

    private async Task<string> ImportAsync()
    {
        string source = Path.Combine(_directory, "input.sql");
        string db = Path.Combine(_directory, "world.db");
        await File.WriteAllTextAsync(source, Dump);
        (int exit, string text) = await RunAsync("import", source, "--database", db);
        Assert.True(exit == ExitCodes.Ok, text);
        return db;
    }

    private static async Task<(int Exit, string Text)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output + error.ToString());
    }

    private static WorldDbContext Open(string path)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        return new WorldDbContext(options);
    }

    private static async Task AddAuthoredRuleAsync(string path)
    {
        await using WorldDbContext db = Open(path);
        db.Set<RuleRow>().Add(new RuleRow { Id = 5, Host = CreatureRuleIds.Host, OwnerKind = CreatureRuleIds.OwnerEntry, OwnerId = 930001, EventType = CreatureRuleIds.EventAggro, Comment = "authored" });
        db.Set<RuleTextRow>().Add(new RuleTextRow { Id = 5, ContentDefault = "Authored text" });
        // Rows a converter does not own: custom content below the converted range, and the reserved DB-script and SmartAI sub-bands.
        foreach (int id in new[] { RuleIdBands.CustomStart + 2, RuleIdBands.EventAi - 1, RuleIdBands.DbScripts, RuleIdBands.SmartAi + 9 })
        {
            db.Set<RuleRow>().Add(new RuleRow { Id = id, Host = CreatureRuleIds.Host, OwnerKind = CreatureRuleIds.OwnerEntry, OwnerId = 930001, EventType = CreatureRuleIds.EventAggro, Comment = "foreign" });
            db.Set<RuleActionRow>().Add(new RuleActionRow { RuleId = id, Seq = 0, ActionType = 1, TargetType = 1, Param1 = 1 });
            db.Set<RuleTextRow>().Add(new RuleTextRow { Id = id, ContentDefault = "Foreign text" });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<string> SnapshotAsync(string path)
    {
        await using WorldDbContext db = Open(path);
        List<RuleRow> rules = await db.Set<RuleRow>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        List<RuleActionRow> actions = await db.Set<RuleActionRow>().AsNoTracking().OrderBy(a => a.RuleId).ThenBy(a => a.Seq).ToListAsync();
        List<RuleTextRow> texts = await db.Set<RuleTextRow>().AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        return string.Join('\n',
            rules.Select(r => $"rule {r.Id} {r.Comment} ev={r.EventType} p={r.EventParam1},{r.EventParam2} mask={r.PhaseMask} cd={r.CooldownMinMs}-{r.CooldownMaxMs} on={r.Enabled}")
                .Concat(actions.Select(a => $"action {a.RuleId}/{a.Seq} t={a.ActionType} {a.Param1},{a.Param2},{a.Param3} target={a.TargetType}"))
                .Concat(texts.Select(t => $"text {t.Id} {t.ContentDefault}")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless.
        }
    }
}
