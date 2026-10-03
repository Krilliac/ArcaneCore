using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>What a schema change would do to the live database.</summary>
public enum ChangeDecision
{
    /// <summary>The object is missing and the change creates it.</summary>
    Create = 0,

    /// <summary>The object is already there as the model needs it; nothing is issued.</summary>
    Satisfied = 1,

    /// <summary>An index with the model's shape exists under another name; nothing is issued.</summary>
    SatisfiedUnderOtherName = 2,

    /// <summary>An index of the model's name exists with another shape; the change is refused.</summary>
    Conflict = 3,

    /// <summary>A unique index cannot be built because rows already share a value; the change is refused.</summary>
    Blocked = 4,

    /// <summary>An existing table does not have the model's columns; the change is refused.</summary>
    ModelMismatch = 5,

    /// <summary>The change needs a table that is neither in the database nor created by an earlier change; refused.</summary>
    MissingTable = 6,
}

/// <summary>
/// The pure decisions of the schema bootstrapper: given what the catalog says, what does one
/// change do. The bootstrapper (which then issues the DDL) and the read-only planner (which only
/// reports it) both call these, so the dry run cannot say something the apply does not do.
/// Messages are the ones operators have always seen (docs/integration/schema-index-repair.md).
/// </summary>
internal static class SchemaChangeDecider
{
    /// <summary>The outcome of deciding one model index against the indexes a table has.</summary>
    internal sealed record IndexDecision(ChangeDecision Decision, string Message, long DuplicateGroups);

    /// <summary>What the model differ produced, validated: the bootstrapper only ever creates tables and indexes.</summary>
    internal sealed record ModelOperations(
        IReadOnlyDictionary<string, CreateTableOperation> Tables,
        ILookup<string, CreateIndexOperation> IndexesByTable,
        IReadOnlyList<AlterDatabaseOperation> AlterDatabase);

    internal static ModelOperations ReadModel(DbContext db)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        IReadOnlyList<MigrationOperation> all = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);

        // A fresh model yields CREATE TABLE and CREATE INDEX operations (plus, with Pomelo, the database's
        // default character set, which only a fresh create applies). Anything else would be dropped
        // silently, which is how indexes were once lost, so refuse it loudly.
        MigrationOperation? other = all.FirstOrDefault(o => o is not (CreateTableOperation or CreateIndexOperation or AlterDatabaseOperation));
        if (other is not null)
        {
            throw new InvalidOperationException(
                $"the model differ produced an unsupported operation {other.GetType().Name}; the bootstrapper only creates tables and indexes");
        }

        return new ModelOperations(
            all.OfType<CreateTableOperation>().ToDictionary(t => t.Name, StringComparer.Ordinal),
            all.OfType<CreateIndexOperation>().ToLookup(i => i.Table, StringComparer.Ordinal),
            [.. all.OfType<AlterDatabaseOperation>()]);
    }

    /// <summary>The tables of the current model other than the component's version table, in creation order.</summary>
    internal static string[] ModelTables(DbContext db, SchemaDefinition definition)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        IReadOnlyList<MigrationOperation> all = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);
        return [.. all.OfType<CreateTableOperation>().Select(t => t.Name)
            .Where(n => !string.Equals(n, definition.VersionTable, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Columns that a later step adds may legitimately be missing from a table this step
    /// created on an earlier attempt (the table was made before the column was in the model).
    /// </summary>
    internal static HashSet<(string Table, string Column)> AddedLater(SchemaDefinition definition, SchemaStep step)
        => [.. definition.Steps.Where(s => s.Version > step.Version)
            .SelectMany(s => s.Changes).OfType<AddColumnChange>().Select(c => (c.Table, c.Column))];

    /// <summary>
    /// An existing table is kept only if it has exactly the model's columns (ignoring columns that
    /// a later step adds): anything else is another component's table, or damage, and is not adopted.
    /// Null when it matches; otherwise the refusal message.
    /// </summary>
    internal static string? ColumnMismatch(
        CreateTableOperation expected, IReadOnlyList<string> actual, IReadOnlySet<(string Table, string Column)> addedLater)
    {
        string[] model = [.. expected.Columns.Select(c => c.Name)];
        string[] missing = [.. model.Where(c => !addedLater.Contains((expected.Name, c)) && !actual.Contains(c, StringComparer.OrdinalIgnoreCase))];
        string[] unexpected = [.. actual.Where(c => !model.Contains(c, StringComparer.OrdinalIgnoreCase))];
        return missing.Length > 0 || unexpected.Length > 0
            ? $"table {expected.Name} already exists but does not match the model (missing columns: [{string.Join(", ", missing)}], " +
              $"unexpected columns: [{string.Join(", ", unexpected)}]). Refusing to adopt it; repair or recreate it."
            : null;
    }

    /// <summary>The model column as it is added to an existing table (NOT NULL columns get the CLR default).</summary>
    internal static AddColumnOperation PrepareAddColumn(CreateTableOperation table, string columnName)
    {
        AddColumnOperation column = table.Columns.FirstOrDefault(c => c.Name == columnName)
            ?? throw new InvalidOperationException($"column {table.Name}.{columnName} is not in the model");
        column.Table = table.Name;
        column.Schema = table.Schema;
        if (!column.IsNullable && column.DefaultValue is null && column.DefaultValueSql is null)
        {
            // Existing rows need a value; SQLite also refuses NOT NULL without a default.
            column.DefaultValue = column.ClrType.IsValueType ? Activator.CreateInstance(column.ClrType) : string.Empty;
        }

        return column;
    }

    internal static bool SameShape(CatalogIndex actual, CreateIndexOperation model)
        => actual.IsUnique == model.IsUnique && actual.Columns.SequenceEqual(model.Columns, StringComparer.OrdinalIgnoreCase);

    internal static string Describe(bool unique, IEnumerable<string> columns)
        => $"{(unique ? "a unique" : "a non-unique")} index on ({string.Join(", ", columns)})";

    /// <summary>
    /// Decide one model index against the indexes the table has (a read-only duplicate count included,
    /// skipped when <paramref name="tableIsEmpty"/>: a table the plan itself creates).
    /// </summary>
    internal static async Task<IndexDecision> DecideIndexAsync(
        DbContext db, CreateIndexOperation index, IReadOnlyList<CatalogIndex> existing, bool tableIsEmpty, CancellationToken ct)
    {
        CatalogIndex? sameName = existing.FirstOrDefault(i => string.Equals(i.Name, index.Name, StringComparison.Ordinal));
        if (sameName is not null)
        {
            if (SameShape(sameName, index))
            {
                return new IndexDecision(ChangeDecision.Satisfied, $"index {index.Name} on {index.Table} exists", 0);
            }

            return new IndexDecision(
                ChangeDecision.Conflict,
                $"index {index.Name} on {index.Table} exists but is {Describe(sameName.IsUnique, sameName.Columns)}; " +
                $"the model needs {Describe(index.IsUnique, index.Columns)}. Refusing to replace it; drop or rename it.",
                0);
        }

        CatalogIndex? other = existing.FirstOrDefault(i => SameShape(i, index));
        if (other is not null)
        {
            // The same index under another name (a DBA's, or an older build's) satisfies the model.
            return new IndexDecision(
                ChangeDecision.SatisfiedUnderOtherName, $"index {index.Name} on {index.Table} exists as {other.Name}", 0);
        }

        if (index.IsUnique && !tableIsEmpty)
        {
            long groups = await CountDuplicateGroupsAsync(db, index, ct).ConfigureAwait(false);
            if (groups > 0)
            {
                return new IndexDecision(
                    ChangeDecision.Blocked,
                    $"cannot create unique index {index.Name} on {index.Table}({string.Join(", ", index.Columns)}): " +
                    $"{groups} group(s) of rows share a value. Remove or fix the duplicates and start again; no rows were changed " +
                    "(docs/integration/schema-index-repair.md).",
                    groups);
            }
        }

        return new IndexDecision(ChangeDecision.Create, $"index {index.Name} on {index.Table} will be created", 0);
    }

    /// <summary>
    /// How many distinct values appear in more than one row of the index's columns. Rows with a
    /// NULL in any key column are ignored: unique indexes treat NULLs as distinct on every provider.
    /// A read-only SELECT.
    /// </summary>
    internal static async Task<long> CountDuplicateGroupsAsync(DbContext db, CreateIndexOperation index, CancellationToken ct)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string columns = string.Join(", ", index.Columns.Select(c => sql.DelimitIdentifier(c)));
        string notNull = string.Join(" AND ", index.Columns.Select(c => sql.DelimitIdentifier(c) + " IS NOT NULL"));
        string table = sql.DelimitIdentifier(index.Table, index.Schema);
        return await SchemaCatalog.ScalarAsync(
            db,
            $"SELECT COUNT(*) FROM (SELECT {columns} FROM {table} WHERE {notNull} GROUP BY {columns} HAVING COUNT(*) > 1) AS duplicate_groups",
            ct).ConfigureAwait(false);
    }

    /// <summary>The newer-than-code refusal text (shared by the bootstrapper and the upgrader).</summary>
    internal static string NewerMessage(SchemaDefinition definition, int version)
        => $"The {definition.Component} database is at schema version {version}, newer than this build " +
           $"({definition.CurrentVersion}). Run a newer ArcaneCore or restore a matching backup.";

    /// <summary>The unknown-state text of a version table without a row.</summary>
    internal static string NoRowMessage(SchemaDefinition definition, int present, int total)
        => $"{definition.VersionTable} exists but holds no version row; the {definition.Component} schema is in an unknown state " +
           $"({present} of {total} tables present).";

    /// <summary>The partial version-1 table set text.</summary>
    internal static string PartialV1Message(SchemaDefinition definition, int present)
        => $"The {definition.Component} database has {present} of the {definition.Version1Tables.Count} expected tables " +
           "and no version table. Refusing to guess; repair or recreate it.";
}
