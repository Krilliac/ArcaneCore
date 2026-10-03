namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>How serious a difference between the database and the model is.</summary>
public enum DriftSeverity
{
    /// <summary>Harmless and expected (an index under another name).</summary>
    Info = 0,

    /// <summary>Worth knowing, does not stop the server (an extra index, a foreign table, a non-InnoDB table).</summary>
    Warning = 1,

    /// <summary>The database does not match what the code needs.</summary>
    Error = 2,
}

/// <summary>What differs.</summary>
public enum DriftKind
{
    VersionMissing = 0,
    VersionBehind = 1,
    VersionNewer = 2,
    MissingTable = 3,
    MissingColumn = 4,
    UnexpectedColumn = 5,
    NullabilityMismatch = 6,
    MissingIndex = 7,
    ConflictingIndex = 8,
    IndexUnderOtherName = 9,
    ExtraIndex = 10,
    ForeignTable = 11,
    NonInnoDbTable = 12,
    DatabaseCharset = 13,
    DatabaseEncoding = 14,
}

/// <summary>One difference.</summary>
public sealed record DriftFinding(DriftSeverity Severity, DriftKind Kind, string? Table, string Detail)
{
    public override string ToString() => $"{Severity} {Kind}{(Table is null ? string.Empty : " " + Table)}: {Detail}";
}

/// <summary>
/// The outcome of <see cref="SchemaDriftChecker"/>. The examined counts exist so a checker that
/// looked at nothing cannot pass as clean.
/// </summary>
/// <param name="Component">auth, characters or world.</param>
/// <param name="Findings">Every difference, in table order.</param>
/// <param name="TablesExamined">Model tables looked for.</param>
/// <param name="ColumnsExamined">Model columns compared.</param>
/// <param name="IndexesExamined">Model indexes compared.</param>
public sealed record DriftReport(
    string Component,
    IReadOnlyList<DriftFinding> Findings,
    int TablesExamined,
    int ColumnsExamined,
    int IndexesExamined)
{
    public IReadOnlyList<DriftFinding> Errors => [.. Findings.Where(f => f.Severity == DriftSeverity.Error)];

    public IReadOnlyList<DriftFinding> Warnings => [.. Findings.Where(f => f.Severity == DriftSeverity.Warning)];

    /// <summary>No finding of severity <see cref="DriftSeverity.Error"/>.</summary>
    public bool IsClean => Findings.All(f => f.Severity != DriftSeverity.Error);
}
