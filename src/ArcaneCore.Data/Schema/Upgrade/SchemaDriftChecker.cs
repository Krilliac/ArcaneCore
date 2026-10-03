using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>
/// Compares a live database with the EF model of a component's context: version row, tables,
/// columns (name and nullability), indexes (by shape, like the bootstrapper: an index under another
/// name satisfies the model), and the engine settings that make non-ASCII text or transactions
/// misbehave. Read-only and valid at any version: a database at an old version reports errors,
/// it does not throw. Closes the "an index dropped later is not noticed" limit of
/// docs/integration/schema-index-repair.md.
/// <para>
/// Limits, deliberately: column TYPES and defaults are not compared (EF store types and the
/// catalogs' type names are not portably comparable), so a database whose column types drifted
/// still reports clean; extra indexes and foreign tables are reported, never dropped.
/// </para>
/// </summary>
public static class SchemaDriftChecker
{
    /// <param name="db">The component's context.</param>
    /// <param name="definition">The schema the code needs (its version table and current version).</param>
    /// <param name="knownTables">
    /// Every table any ArcaneCore component owns (the union of the three models and their version
    /// tables); tables of the database outside it are reported as foreign. Null skips that check.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<DriftReport> CheckAsync(
        DbContext db, SchemaDefinition definition, IEnumerable<string>? knownTables = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);

        var findings = new List<DriftFinding>();
        int tablesExamined = 0;
        int columnsExamined = 0;
        int indexesExamined = 0;

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckVersionAsync(db, definition, findings, cancellationToken).ConfigureAwait(false);

            IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
            foreach (ITable table in model.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                tablesExamined++;
                if (!await SchemaCatalog.TableExistsAsync(db, table.Name, cancellationToken).ConfigureAwait(false))
                {
                    findings.Add(new DriftFinding(DriftSeverity.Error, DriftKind.MissingTable, table.Name, $"table {table.Name} is missing"));
                    columnsExamined += table.Columns.Count();
                    indexesExamined += table.Indexes.Count();
                    continue;
                }

                columnsExamined += await CheckColumnsAsync(db, table, findings, cancellationToken).ConfigureAwait(false);
                indexesExamined += await CheckIndexesAsync(db, table, findings, cancellationToken).ConfigureAwait(false);
            }

            if (knownTables is not null)
            {
                var known = new HashSet<string>(knownTables, StringComparer.OrdinalIgnoreCase);
                foreach (string table in (await SchemaCatalogInfo.ReadTablesAsync(db, cancellationToken).ConfigureAwait(false)).Order(StringComparer.Ordinal))
                {
                    if (!known.Contains(table))
                    {
                        findings.Add(new DriftFinding(
                            DriftSeverity.Warning, DriftKind.ForeignTable, table, $"table {table} belongs to no ArcaneCore component (left alone)"));
                    }
                }
            }

            DatabaseSettings settings = await SchemaCatalogInfo.ReadDatabaseSettingsAsync(db, cancellationToken).ConfigureAwait(false);
            findings.AddRange(CheckSettings(db.Database.ProviderName ?? string.Empty, settings, model.Tables.Select(t => t.Name)));
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return new DriftReport(definition.Component, findings, tablesExamined, columnsExamined, indexesExamined);
    }

    /// <summary>
    /// The settings findings of one engine: MariaDB/MySQL tables not on InnoDB and a default character
    /// set other than utf8mb4, PostgreSQL encodings other than UTF8. Pure, so every engine's rule is testable offline.
    /// </summary>
    public static IReadOnlyList<DriftFinding> CheckSettings(string providerName, DatabaseSettings settings, IEnumerable<string> modelTables)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelTables);
        var findings = new List<DriftFinding>();
        if (providerName == "Pomelo.EntityFrameworkCore.MySql")
        {
            foreach (string table in modelTables.Order(StringComparer.Ordinal))
            {
                if (settings.TableEngines.TryGetValue(table, out string? engine)
                    && !string.Equals(engine, "InnoDB", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new DriftFinding(
                        DriftSeverity.Warning, DriftKind.NonInnoDbTable, table,
                        $"table {table} uses the {engine} engine; only InnoDB gives transactions and row locks"));
                }
            }

            if (settings.DatabaseCharset is { Length: > 0 } charset
                && !string.Equals(charset, "utf8mb4", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DriftFinding(
                    DriftSeverity.Warning, DriftKind.DatabaseCharset, null,
                    $"the database's default character set is {charset}, not utf8mb4; tables created in it can reject non-ASCII mail and guild text"));
            }
        }
        else if (providerName == "Npgsql.EntityFrameworkCore.PostgreSQL"
            && settings.DatabaseEncoding is { Length: > 0 } encoding
            && !string.Equals(encoding, "UTF8", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DriftFinding(
                DriftSeverity.Warning, DriftKind.DatabaseEncoding, null,
                $"the database's encoding is {encoding}, not UTF8; non-ASCII text can be rejected"));
        }

        return findings;
    }

    private static async Task CheckVersionAsync(DbContext db, SchemaDefinition definition, List<DriftFinding> findings, CancellationToken ct)
    {
        int? version = null;
        if (await SchemaCatalog.TableExistsAsync(db, definition.VersionTable, ct).ConfigureAwait(false))
        {
            version = (await db.Set<SchemaVersionRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false))?.Version;
        }

        if (version is null)
        {
            findings.Add(new DriftFinding(DriftSeverity.Error, DriftKind.VersionMissing, definition.VersionTable, $"the {definition.Component} schema has no version row"));
        }
        else if (version < definition.CurrentVersion)
        {
            findings.Add(new DriftFinding(
                DriftSeverity.Error, DriftKind.VersionBehind, definition.VersionTable,
                $"the {definition.Component} database is at schema version {version}, this build needs {definition.CurrentVersion}"));
        }
        else if (version > definition.CurrentVersion)
        {
            findings.Add(new DriftFinding(
                DriftSeverity.Error, DriftKind.VersionNewer, definition.VersionTable,
                $"the {definition.Component} database is at schema version {version}, newer than this build ({definition.CurrentVersion})"));
        }
    }

    private static async Task<int> CheckColumnsAsync(DbContext db, ITable table, List<DriftFinding> findings, CancellationToken ct)
    {
        IReadOnlyList<CatalogColumn> actual = await SchemaCatalogInfo.ReadColumnInfoAsync(db, table.Name, ct).ConfigureAwait(false);
        var byName = actual.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var primaryKey = new HashSet<string>(table.PrimaryKey?.Columns.Select(c => c.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        int examined = 0;
        foreach (IColumn column in table.Columns)
        {
            examined++;
            if (!byName.TryGetValue(column.Name, out CatalogColumn? found))
            {
                findings.Add(new DriftFinding(DriftSeverity.Error, DriftKind.MissingColumn, table.Name, $"column {table.Name}.{column.Name} is missing"));
                continue;
            }

            // Primary-key columns are NOT NULL on every engine whatever the catalog's flag says (SQLite reports it per declaration).
            if (!primaryKey.Contains(column.Name) && found.IsNullable != column.IsNullable)
            {
                findings.Add(new DriftFinding(
                    DriftSeverity.Error, DriftKind.NullabilityMismatch, table.Name,
                    $"column {table.Name}.{column.Name} is {(found.IsNullable ? "nullable" : "NOT NULL")} in the database, the model needs {(column.IsNullable ? "nullable" : "NOT NULL")}"));
            }
        }

        var modelNames = new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (CatalogColumn extra in actual.Where(c => !modelNames.Contains(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            findings.Add(new DriftFinding(DriftSeverity.Error, DriftKind.UnexpectedColumn, table.Name, $"column {table.Name}.{extra.Name} is not in the model"));
        }

        return examined;
    }

    private static async Task<int> CheckIndexesAsync(DbContext db, ITable table, List<DriftFinding> findings, CancellationToken ct)
    {
        IReadOnlyList<CatalogIndex> actual = await SchemaCatalog.ReadIndexesAsync(db, table.Name, ct).ConfigureAwait(false);
        var accounted = new HashSet<string>(StringComparer.Ordinal);
        int examined = 0;
        foreach (ITableIndex index in table.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            examined++;
            string[] columns = [.. index.Columns.Select(c => c.Name)];
            bool SameShape(CatalogIndex i) => i.IsUnique == index.IsUnique && i.Columns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase);

            CatalogIndex? sameName = actual.FirstOrDefault(i => string.Equals(i.Name, index.Name, StringComparison.Ordinal));
            if (sameName is not null)
            {
                accounted.Add(sameName.Name);
                if (!SameShape(sameName))
                {
                    findings.Add(new DriftFinding(
                        DriftSeverity.Error, DriftKind.ConflictingIndex, table.Name,
                        $"index {index.Name} on {table.Name} is {SchemaChangeDecider.Describe(sameName.IsUnique, sameName.Columns)}, " +
                        $"the model needs {SchemaChangeDecider.Describe(index.IsUnique, columns)}"));
                }

                continue;
            }

            CatalogIndex? renamed = actual.FirstOrDefault(SameShape);
            if (renamed is not null)
            {
                accounted.Add(renamed.Name);
                findings.Add(new DriftFinding(
                    DriftSeverity.Info, DriftKind.IndexUnderOtherName, table.Name, $"index {index.Name} on {table.Name} exists as {renamed.Name}"));
            }
            else
            {
                findings.Add(new DriftFinding(
                    DriftSeverity.Error, DriftKind.MissingIndex, table.Name,
                    $"index {index.Name} on {table.Name} ({string.Join(", ", columns)}) is missing"));
            }
        }

        foreach (CatalogIndex extra in actual.Where(i => !accounted.Contains(i.Name)).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            findings.Add(new DriftFinding(
                DriftSeverity.Warning, DriftKind.ExtraIndex, table.Name,
                $"index {extra.Name} on {table.Name} is not in the model (left alone)"));
        }

        return examined;
    }
}
