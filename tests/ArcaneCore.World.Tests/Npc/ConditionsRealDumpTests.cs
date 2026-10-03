using System.IO.Compression;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Kernel.Npc;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>A fact that skips with an explicit message unless the real classic-db dump is available.</summary>
internal sealed class ClassicDbDumpFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_CLASSICDB_SQL";

    public ClassicDbDumpFactAttribute()
    {
        string? path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip = $"{Variable} is not set to an existing classic-db dump (.sql or .sql.gz): the real-data conditions check did NOT run.";
        }
    }
}

/// <summary>
/// The real classic-db <c>conditions</c> table through the importer and the table validation. The dump is read from the
/// path in <c>ARCANECORE_CLASSICDB_SQL</c> (never copied into the repository); the counts are printed as sizing, not asserted.
/// </summary>
public sealed class ConditionsRealDumpTests(ITestOutputHelper output)
{
    [ClassicDbDumpFact]
    public void TheRealConditionsTable_ImportsAndValidatesAgainstTheCmangosRules()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        using Stream file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);

        var importer = new ConditionsDumpImporter();
        importer.Read(reader);
        ConditionsImportReport report = importer.BuildReport();
        Assert.True(report.Conditions > 0, "the dump has a conditions table");

        ConditionTable table = ConditionTable.Build(importer.Rows.Select(r =>
            new ConditionRecord(r.ConditionEntry, r.Type, r.Value1, r.Value2, r.Value3, r.Value4, r.Flags)));
        var evaluator = new ConditionEvaluator(table, new ConditionContext());
        ConditionSummary summary = evaluator.Summarize();

        output.WriteLine($"db_version: {report.DbVersion}");
        output.WriteLine($"rows {report.Conditions}, valid {table.Count}, rejected {table.Rejected.Count}");
        foreach (IGrouping<string, ConditionRejection> g in table.Rejected.GroupBy(r => r.Reason.Split(' ')[0]).OrderByDescending(g => g.Count()).Take(10))
        {
            output.WriteLine($"  rejected: {g.Key} x{g.Count()}");
        }

        output.WriteLine("types without any collaborator (fail closed): " + string.Join(", ", summary.UnavailableByType.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}")));

        // Every row is either valid or rejected with a reason; no row is lost.
        Assert.Equal(report.Conditions, table.Count + table.Rejected.Count);

        // Every type the dump uses is one the cmangos classic enum defines (the numbering assumption of the importer).
        Assert.All(importer.Rows, r => Assert.True(ConditionTable.IsKnownType(r.Type), $"condition {r.ConditionEntry} has type {r.Type}"));
    }
}
