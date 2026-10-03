using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaneCore.Data.Schema.Upgrade.Cli;

/// <summary>One component of the machine-readable report (<c>--json</c>).</summary>
internal sealed record ComponentJson(
    string Component,
    string Provider,
    string Target,
    string State,
    int? DatabaseVersion,
    int CodeVersion,
    IReadOnlyList<int> PendingVersions,
    string? Refusal,
    IReadOnlyList<ActionJson> Actions,
    string? Server,
    bool? ServerQualified,
    int? OtherSessions,
    bool? SchemaLockHeld);

internal sealed record ActionJson(string Object, string Table, string? Name, string Decision, string Detail, long DuplicateGroups);

internal sealed record FindingJson(string Severity, string Kind, string? Table, string Detail);

internal sealed record DriftJson(string Component, bool Clean, int TablesExamined, int ColumnsExamined, int IndexesExamined, IReadOnlyList<FindingJson> Findings);

/// <summary>Text, SQL-script and JSON renderings of plans and drift reports.</summary>
internal static class PlanFormatter
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, s_json);

    public static ComponentJson ToJson(
        SchemaPlan plan, string provider, string target, ServerInfo? server, int? sessions, bool? lockHeld)
        => new(
            plan.Component, provider, target, plan.State.ToString(), plan.DatabaseVersion, plan.CodeVersion, plan.PendingVersions, plan.FirstRefusal,
            [.. plan.Actions.Select(a => new ActionJson(a.Object.ToString(), a.Table, a.Name, a.Decision.ToString(), a.Detail, a.DuplicateGroups))],
            server is null ? null : $"{server.Product} {server.Version}", server?.Qualified, sessions, lockHeld);

    public static DriftJson ToJson(DriftReport report)
        => new(
            report.Component, report.IsClean, report.TablesExamined, report.ColumnsExamined, report.IndexesExamined,
            [.. report.Findings.Select(f => new FindingJson(f.Severity.ToString(), f.Kind.ToString(), f.Table, f.Detail))]);

    public static string DecisionLabel(ChangeDecision decision) => decision switch
    {
        ChangeDecision.Create => "create",
        ChangeDecision.Satisfied => "present",
        ChangeDecision.SatisfiedUnderOtherName => "present (other name)",
        ChangeDecision.Conflict => "CONFLICT",
        ChangeDecision.Blocked => "BLOCKED",
        ChangeDecision.ModelMismatch => "MISMATCH",
        ChangeDecision.MissingTable => "MISSING TABLE",
        _ => decision.ToString(),
    };

    /// <summary>The one-line state of a component.</summary>
    public static string Summary(SchemaPlan plan)
    {
        string have = plan.DatabaseVersion is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
        string pending = plan.PendingVersions.Count == 0
            ? string.Empty
            : plan.State is SchemaState.Missing or SchemaState.Fresh or SchemaState.Creating or SchemaState.NoVersionRow
                ? $", will create schema version {plan.PendingVersions[^1]}"
                : $", pending: {string.Join(", ", plan.PendingVersions)}";
        return $"{plan.Component,-10} state {plan.State}, database version {have}, code version {plan.CodeVersion}{pending}";
    }

    /// <summary>The human plan: summary, every non-trivial action per step, and refusals.</summary>
    public static void WriteText(TextWriter writer, SchemaPlan plan)
    {
        writer.WriteLine(Summary(plan));
        foreach (PlannedStep step in plan.Steps)
        {
            int creates = step.Actions.Count(a => a.Decision == ChangeDecision.Create);
            writer.WriteLine($"  step to version {step.Version}: {step.Description} ({creates} change(s))");
            foreach (PlannedAction action in step.Actions.Where(a => a.Decision != ChangeDecision.Satisfied))
            {
                writer.WriteLine($"    {DecisionLabel(action.Decision),-20} {action.Object.ToString().ToLowerInvariant(),-7} {Subject(action)}");
                if (action.IsBlocking)
                {
                    writer.WriteLine($"      {action.Detail}");
                }
            }
        }

        if (plan.Refusal is not null)
        {
            writer.WriteLine($"  refused: {plan.Refusal}");
        }
    }

    /// <summary>
    /// The operator-applied SQL: every statement an apply would issue plus the version-row write per step,
    /// all prose as <c>--</c> comments so the whole output is a valid script. Database-level settings
    /// (a new database's character set) and the creation of the database itself are not included.
    /// </summary>
    public static void WriteScript(TextWriter writer, SchemaPlan plan)
    {
        writer.WriteLine($"-- {Summary(plan)}");
        if (plan.Refusal is not null)
        {
            writer.WriteLine($"-- REFUSED: {OneLine(plan.Refusal)}");
        }

        foreach (PlannedStep step in plan.Steps)
        {
            writer.WriteLine($"-- {plan.Component}: {step.Description} (version {step.Version})");
            foreach (PlannedAction action in step.Actions)
            {
                if (action.IsBlocking)
                {
                    writer.WriteLine($"-- {DecisionLabel(action.Decision)}: {OneLine(action.Detail)}");
                }
                else if (action.Decision == ChangeDecision.Create && action.Script is not null)
                {
                    foreach (string statement in action.Script)
                    {
                        string text = statement.Trim();
                        writer.WriteLine(text.EndsWith(';') ? text : text + ";");
                    }
                }
            }

            if (step.VersionStatement is not null)
            {
                writer.WriteLine(step.VersionStatement);
            }
        }
    }

    public static void WriteText(TextWriter writer, DriftReport report)
    {
        writer.WriteLine(
            $"{report.Component,-10} {(report.IsClean ? "clean" : "DRIFT")}: {report.TablesExamined} table(s), {report.ColumnsExamined} column(s), " +
            $"{report.IndexesExamined} index(es) examined, {report.Errors.Count} error(s), {report.Warnings.Count} warning(s)");
        foreach (DriftFinding finding in report.Findings.OrderByDescending(f => f.Severity))
        {
            writer.WriteLine($"  {finding.Severity.ToString().ToUpperInvariant(),-7} {finding.Kind}: {finding.Detail}");
        }
    }

    private static string Subject(PlannedAction action)
        => action.Object switch
        {
            ChangeObject.Table => action.Table,
            ChangeObject.Column => $"{action.Table}.{action.Name}",
            _ => $"{action.Name} on {action.Table}",
        };

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
