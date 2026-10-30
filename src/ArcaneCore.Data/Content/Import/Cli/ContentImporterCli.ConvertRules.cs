using System.Globalization;
using System.Text;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Data.Content.Import;

public static partial class ContentImporterCli
{
    /// <summary>How many findings <c>convert-rules</c> prints before pointing at <c>--report</c>.</summary>
    private const int ConvertRulesPrintLimit = 40;

    // --- convert-rules -----------------------------------------------------------------------

    /// <summary>
    /// <c>convert-rules</c>: <see cref="EventAiRuleConverter"/> from the command line. It reads the imported EventAI tables of an existing world
    /// database, converts every row, prints (and with <c>--report</c> writes) each row it did not convert plainly, by id and reason, and replaces
    /// the converter's id band of <c>rule</c>, <c>rule_action</c> and <c>rule_text</c> in one transaction. The converted rows are content of that
    /// database; they are never committed to the repository.
    /// </summary>
    private static async Task<int> ConvertRulesAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        if (a.Positional.Count != 0)
        {
            throw new UsageException("convert-rules takes no dump: it reads the EventAI tables already imported into the database");
        }

        bool dryRun = a.Flag("--dry-run");
        string? reportPath = a.Value("--report");
        GuardPath(reportPath);
        Target target = ResolveTarget(a);
        if (!dryRun)
        {
            GuardPath(target.FilePath);
        }

        o.WriteLine($"target: {target.Describe}");
        EventAiConversion conversion;
        EventAiRuleImportResult? written = null;
        try
        {
            await using WorldDbContext db = OpenWorld(target);
            await EnsureImporterSchemaAsync(db, a.Flag("--migrate"), o, ct, "convert-rules").ConfigureAwait(false);
            CreatureAiContent source = await EventAiRuleImporter.LoadSourceAsync(db, ct).ConfigureAwait(false);
            if (source.EventCount == 0)
            {
                throw new CliException(ExitCodes.Schema, "the database has no creature_ai_scripts rows to convert (import a world dump first)");
            }

            conversion = EventAiRuleConverter.Convert(source);
            if (!dryRun)
            {
                written = await EventAiRuleImporter.ImportAsync(db, conversion, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CliException))
        {
            throw DatabaseError(ex, target);
        }

        foreach (EventAiConversionIssue issue in conversion.Issues.Take(ConvertRulesPrintLimit))
        {
            o.WriteLine(issue.ToString());
        }

        if (conversion.Issues.Count > ConvertRulesPrintLimit)
        {
            o.WriteLine($"... {conversion.Issues.Count - ConvertRulesPrintLimit} more finding(s); give --report <file> for the full list");
        }

        if (reportPath is not null)
        {
            WriteConversionReport(reportPath, conversion);
        }

        string counts = string.Create(CultureInfo.InvariantCulture,
            $"creature_ai_scripts: {conversion.SourceRows} row(s), {conversion.Converted} converted, {conversion.Disabled} written disabled, {conversion.Unconvertible} not converted{AdjustedNote(conversion)}");
        if (written is null)
        {
            o.WriteLine($"{counts} (dry run: no database written)");
        }
        else
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{counts}; wrote {written.RulesWritten} rule(s), {written.ActionsWritten} action(s), {written.TextsWritten} text(s) (replaced {written.RulesRemoved}/{written.ActionsRemoved}/{written.TextsRemoved}); a running world picks them up with .reload rules"));
        }

        return ExitCodes.Ok;
    }

    private static string AdjustedNote(EventAiConversion conversion)
        => conversion.Adjusted > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({conversion.Adjusted} converted with an adjusted parameter)") : string.Empty;

    private static void WriteConversionReport(string path, EventAiConversion conversion)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# creature_ai_scripts: {conversion.SourceRows} row(s), {conversion.Converted} converted, {conversion.Disabled} written disabled, {conversion.Unconvertible} not converted\n");
        foreach (EventAiConversionIssue issue in conversion.Issues)
        {
            text.Append(CultureInfo.InvariantCulture, $"{issue.SourceId}\t{issue.Outcome}\t{issue.Reason}\n");
        }

        File.WriteAllText(path, text.ToString());
    }
}
