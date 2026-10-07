using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>Where a component's database stands relative to the code that is about to use it.</summary>
public enum SchemaState
{
    /// <summary>The database does not exist; an apply creates it and the current schema.</summary>
    Missing = 0,

    /// <summary>The database exists but holds none of the component's tables; an apply creates the current schema.</summary>
    Fresh = 1,

    /// <summary>A create was interrupted (version row 0); an apply resumes it.</summary>
    Creating = 2,

    /// <summary>The version table has no row but the table set is consistent; an apply resumes the create.</summary>
    NoVersionRow = 3,

    /// <summary>All version-1 tables and no version table (a pre-M5 database); an apply adopts it as version 1 and upgrades.</summary>
    AdoptV1 = 4,

    /// <summary>The database is at the version the code needs.</summary>
    Current = 5,

    /// <summary>The database is at an older version; an apply runs the pending steps.</summary>
    Behind = 6,

    /// <summary>The database is newer than the code; refused.</summary>
    Newer = 7,

    /// <summary>A partial table set, or a version history with a gap; refused.</summary>
    Unknown = 8,
}

/// <summary>Which kind of database object a planned action touches.</summary>
public enum ChangeObject
{
    Table = 0,
    Column = 1,
    Index = 2,
}

/// <summary>One schema change evaluated against the live catalog.</summary>
/// <param name="Object">Table, column or index.</param>
/// <param name="Table">The table concerned.</param>
/// <param name="Name">The column or index name; null for a table.</param>
/// <param name="Decision">What an apply would do.</param>
/// <param name="Detail">A human sentence; for a refusal, the exact text an apply fails with.</param>
/// <param name="DuplicateGroups">For <see cref="ChangeDecision.Blocked"/>, how many value groups share rows.</param>
/// <param name="Script">With a script requested, the DDL an apply would issue for a <see cref="ChangeDecision.Create"/>.</param>
public sealed record PlannedAction(
    ChangeObject Object,
    string Table,
    string? Name,
    ChangeDecision Decision,
    string Detail,
    long DuplicateGroups = 0,
    IReadOnlyList<string>? Script = null)
{
    /// <summary>Whether an apply refuses this change.</summary>
    public bool IsBlocking => Decision is ChangeDecision.Conflict or ChangeDecision.Blocked
        or ChangeDecision.ModelMismatch or ChangeDecision.MissingTable;

    /// <summary>The operation the DDL comes from (planner internal).</summary>
    internal MigrationOperation? Operation { get; init; }
}

/// <summary>The changes that bring the database to <see cref="Version"/>.</summary>
/// <param name="Version">The schema version written after the step.</param>
/// <param name="Description">What the step is (create, adopt, upgrade).</param>
/// <param name="Actions">The changes in the order an apply runs them.</param>
/// <param name="VersionStatement">With a script requested, the statement that records the version.</param>
public sealed record PlannedStep(int Version, string Description, IReadOnlyList<PlannedAction> Actions, string? VersionStatement = null);

/// <summary>
/// What bringing one component's database to the current code would do, computed without
/// changing anything (no database creation, no lock, no version-row write).
/// </summary>
/// <param name="Component">auth, characters or world.</param>
/// <param name="State">Where the database stands.</param>
/// <param name="DatabaseVersion">The recorded version, or null when there is none (missing, fresh, adopt).</param>
/// <param name="CodeVersion">The version the code needs.</param>
/// <param name="Steps">The pending steps in order; empty when the database is current or refused.</param>
/// <param name="Refusal">Why an apply refuses regardless of the actions (newer, unknown state, version gap); null otherwise.</param>
public sealed record SchemaPlan(
    string Component,
    SchemaState State,
    int? DatabaseVersion,
    int CodeVersion,
    IReadOnlyList<PlannedStep> Steps,
    string? Refusal)
{
    /// <summary>
    /// Set when the database was created by another line (<see cref="SchemaDefinition.ForeignLines"/>): the first step
    /// is its one-shot migration to this build's numbering, and <see cref="DatabaseVersion"/> is the other line's number.
    /// </summary>
    public ForeignLineMatch? ForeignLine { get; init; }

    /// <summary>The schema versions an apply writes, in order.</summary>
    public IReadOnlyList<int> PendingVersions => [.. Steps.Select(s => s.Version)];

    /// <summary>Every planned action of every step.</summary>
    public IEnumerable<PlannedAction> Actions => Steps.SelectMany(s => s.Actions);

    /// <summary>The actions an apply refuses.</summary>
    public IReadOnlyList<PlannedAction> Blockers => [.. Actions.Where(a => a.IsBlocking)];

    /// <summary>Whether an apply would change the database.</summary>
    public bool NeedsApply => State is not (SchemaState.Current or SchemaState.Newer or SchemaState.Unknown);

    /// <summary>Whether an apply would be refused.</summary>
    public bool IsRefused => Refusal is not null || Blockers.Count > 0;

    /// <summary>The number of DDL statements-worth of changes (actions that create something).</summary>
    public int CreateCount => Actions.Count(a => a.Decision == ChangeDecision.Create);

    /// <summary>The first refusal text: the state refusal, else the first blocking action's.</summary>
    public string? FirstRefusal => Refusal ?? Blockers.FirstOrDefault()?.Detail;
}
