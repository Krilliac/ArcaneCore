using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Schema;

/// <summary>The single row of a component's schema-version table.</summary>
public sealed class SchemaVersionRow
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// The schema version, or <see cref="SchemaBootstrapper.CreatingVersion"/> (0) while a fresh
    /// database is being created. 0 is never a released version; a build that does not know it
    /// finds no upgrade path and fails closed.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>A change that brings a component's schema to <see cref="SchemaStep.Version"/>.</summary>
public abstract record SchemaChange;

/// <summary>
/// Create a table exactly as the current model defines it, together with the indexes the model
/// declares on it. Idempotent: a table that already exists with the model's columns is kept (its
/// rows untouched) and only its missing indexes are created.
/// </summary>
public sealed record CreateTableChange(string Table) : SchemaChange;

/// <summary>Add a column exactly as the current model defines it (NOT NULL columns get the CLR default).</summary>
public sealed record AddColumnChange(string Table, string Column) : SchemaChange;

/// <summary>
/// Create every index the current model declares on an existing table that the database lacks
/// (a forward repair for tables created by builds that dropped indexes). An index that is already
/// there under any name, with the same columns and uniqueness, satisfies it. A unique index over
/// rows that already hold duplicates is refused with <see cref="SchemaMismatchException"/>; rows
/// are never deleted or merged.
/// </summary>
public sealed record EnsureIndexesChange(string Table) : SchemaChange;

/// <summary>The changes that move a schema from <c>Version - 1</c> to <see cref="Version"/>.</summary>
public sealed record SchemaStep(int Version, IReadOnlyList<SchemaChange> Changes);

/// <summary>What a component's schema looks like and how it evolved.</summary>
public sealed class SchemaDefinition
{
    /// <summary>Component name, also the version table prefix ("auth" → auth_schema).</summary>
    public required string Component { get; init; }

    /// <summary>The version the current code requires.</summary>
    public required int CurrentVersion { get; init; }

    /// <summary>
    /// Tables of the version-1 schema. A database that has all of them but no version table
    /// was created by M1–M4 (EnsureCreated) and is adopted as version 1.
    /// </summary>
    public required IReadOnlyList<string> Version1Tables { get; init; }

    /// <summary>Upgrade steps for versions 2..CurrentVersion, in order.</summary>
    public IReadOnlyList<SchemaStep> Steps { get; init; } = [];

    public string VersionTable => Component + "_schema";
}

/// <summary>Raised when a database cannot be brought to the version the code requires.</summary>
public sealed class SchemaMismatchException(string message) : Exception(message);

/// <summary>
/// Creates, adopts and upgrades one component's schema inside a database that other
/// components may share. Each component owns a <c>&lt;component&gt;_schema</c> table with one
/// version row; anything unexpected fails closed (ROADMAP § Persistence).
/// <para>
/// This replaces <c>EnsureCreated</c>, which skips a context entirely as soon as the database
/// has any table — so a second context sharing the database never got its tables.
/// </para>
/// <para>
/// Every change is idempotent (a table that exists with the model's columns is kept, an index
/// that exists is kept) and the version row is written after the step, so a startup that dies
/// halfway — which DDL on MariaDB, not being transactional, can leave behind — converges on the
/// next start. A per-component lock (<c>SchemaLock</c>) serializes concurrent starts.
/// Details: docs/integration/schema-index-repair.md.
/// </para>
/// </summary>
public static class SchemaBootstrapper
{
    /// <summary>The version-row value of a fresh database whose creation has begun but not finished.</summary>
    public const int CreatingVersion = 0;

    /// <summary>How long a start waits for another process's schema work before failing closed.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The PostgreSQL advisory-lock key of a component's schema lock: the 64-bit FNV-1a hash of
    /// <c>arcanecore_schema:&lt;component&gt;</c>. Documented so operators can see who holds it (<c>pg_locks</c>) and tests can hold it.
    /// MariaDB/MySQL use the named lock <c>SHA1('arcanecore_schema:&lt;database&gt;:&lt;component&gt;')</c>.
    /// </summary>
    public static long AdvisoryLockKey(string component) => SchemaLock.AdvisoryKey(component);

    /// <summary>Configure the version table for a context's model.</summary>
    public static void MapVersionTable(ModelBuilder modelBuilder, SchemaDefinition definition)
    {
        modelBuilder.Entity<SchemaVersionRow>(entity =>
        {
            entity.ToTable(definition.VersionTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });
    }

    public static Task EnsureAsync(
        DbContext db, SchemaDefinition definition, ILogger? logger = null, CancellationToken cancellationToken = default)
        => EnsureAsync(db, definition, DefaultLockTimeout, logger, cancellationToken);

    /// <param name="db">The component's context; its connection is kept open for the whole call.</param>
    /// <param name="definition">The schema the code needs.</param>
    /// <param name="lockTimeout">How long to wait for another process that is changing the same component's schema.</param>
    /// <param name="logger">Optional progress log.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock and the work.</param>
    public static async Task EnsureAsync(
        DbContext db, SchemaDefinition definition, TimeSpan lockTimeout, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Another process may have created it between the check and the create.
                if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }

        // One connection for the whole bootstrap: the session lock belongs to it.
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SchemaLock schemaLock = await SchemaLock.AcquireAsync(db, definition.Component, lockTimeout, cancellationToken)
                .ConfigureAwait(false);
            await RunAsync(db, definition, logger, cancellationToken).ConfigureAwait(false);
            await schemaLock.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunAsync(DbContext db, SchemaDefinition definition, ILogger? logger, CancellationToken ct)
    {
        // Read only now that the lock is held: a process that waited sees the winner's work.
        int? version = await TryReadVersionAsync(db, definition, ct).ConfigureAwait(false);
        if (version is null)
        {
            version = await CreateOrAdoptAsync(db, definition, logger, ct).ConfigureAwait(false);
        }
        else if (version == CreatingVersion)
        {
            version = await CreateFreshAsync(db, definition, logger, ct).ConfigureAwait(false);
        }

        if (version > definition.CurrentVersion)
        {
            throw new SchemaMismatchException(
                $"The {definition.Component} database is at schema version {version}, newer than this build " +
                $"({definition.CurrentVersion}). Run a newer ArcaneCore or restore a matching backup.");
        }

        foreach (SchemaStep step in definition.Steps.Where(s => s.Version > version).OrderBy(s => s.Version))
        {
            if (step.Version != version + 1)
            {
                throw new SchemaMismatchException(
                    $"No upgrade path for the {definition.Component} schema from version {version} to {step.Version}.");
            }

            await ApplyStepAsync(db, definition, step, ct).ConfigureAwait(false);
            await WriteVersionAsync(db, step.Version, ct).ConfigureAwait(false);
            logger?.LogInformation("Upgraded {Component} schema to version {Version}", definition.Component, step.Version);
            version = step.Version;
        }

        if (version != definition.CurrentVersion)
        {
            throw new SchemaMismatchException(
                $"The {definition.Component} schema is at version {version} but this build needs " +
                $"{definition.CurrentVersion} and has no upgrade path.");
        }
    }

    private static async Task<int?> TryReadVersionAsync(DbContext db, SchemaDefinition definition, CancellationToken ct)
    {
        if (!await SchemaCatalog.TableExistsAsync(db, definition.VersionTable, ct).ConfigureAwait(false))
        {
            return null;
        }

        SchemaVersionRow? row = await db.Set<SchemaVersionRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
        if (row is not null)
        {
            return row.Version;
        }

        // A version table without a row is what a fresh create that died before recording its
        // version leaves. Resumable when the rest of the component's tables are either all
        // absent (it died right after creating the version table) or all present (older builds
        // created the tables first and wrote the version afterwards).
        string[] modelTables = ModelTables(db, definition);
        int present = 0;
        foreach (string table in modelTables)
        {
            if (await SchemaCatalog.TableExistsAsync(db, table, ct).ConfigureAwait(false))
            {
                present++;
            }
        }

        if (present == 0 || present == modelTables.Length)
        {
            await WriteVersionAsync(db, CreatingVersion, ct).ConfigureAwait(false);
            return CreatingVersion;
        }

        throw new SchemaMismatchException(
            $"{definition.VersionTable} exists but holds no version row; the {definition.Component} schema is in an unknown state " +
            $"({present} of {modelTables.Length} tables present).");
    }

    private static async Task<int> CreateOrAdoptAsync(DbContext db, SchemaDefinition definition, ILogger? logger, CancellationToken ct)
    {
        int present = 0;
        foreach (string table in definition.Version1Tables)
        {
            if (await SchemaCatalog.TableExistsAsync(db, table, ct).ConfigureAwait(false))
            {
                present++;
            }
        }

        if (present == 0)
        {
            return await CreateFreshAsync(db, definition, logger, ct).ConfigureAwait(false);
        }

        if (present == definition.Version1Tables.Count)
        {
            // Pre-M5 database (EnsureCreated, no version table): its layout is version 1.
            await ExecuteAsync(db, [new CreateTableChange(definition.VersionTable)], new HashSet<(string, string)>(), ct).ConfigureAwait(false);
            await WriteVersionAsync(db, 1, ct).ConfigureAwait(false);
            logger?.LogInformation("Adopted existing {Component} tables as schema version 1", definition.Component);
            return 1;
        }

        throw new SchemaMismatchException(
            $"The {definition.Component} database has {present} of the {definition.Version1Tables.Count} expected tables " +
            "and no version table. Refusing to guess; repair or recreate it.");
    }

    /// <summary>
    /// Create (or resume creating) every table of the current model, then record the current
    /// version. The version row says 0 while this runs, so an interrupted create is recognised
    /// and resumed instead of being mistaken for a damaged database.
    /// </summary>
    private static async Task<int> CreateFreshAsync(DbContext db, SchemaDefinition definition, ILogger? logger, CancellationToken ct)
    {
        // What EF's CreateTables always ran first: the engine's database-level settings (Pomelo: the default
        // character set). Repeating it changes nothing, so a resumed create may run it again.
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        foreach (AlterDatabaseOperation alter in db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model).OfType<AlterDatabaseOperation>())
        {
            await RunAsync(db, alter, ct).ConfigureAwait(false);
        }

        var noLaterColumns = new HashSet<(string, string)>();
        await ExecuteAsync(db, [new CreateTableChange(definition.VersionTable)], noLaterColumns, ct).ConfigureAwait(false);
        await WriteVersionAsync(db, CreatingVersion, ct).ConfigureAwait(false);
        SchemaChange[] tables = [.. ModelTables(db, definition).Select(t => new CreateTableChange(t))];
        await ExecuteAsync(db, tables, noLaterColumns, ct).ConfigureAwait(false);
        await WriteVersionAsync(db, definition.CurrentVersion, ct).ConfigureAwait(false);
        logger?.LogInformation("Created {Component} schema version {Version}", definition.Component, definition.CurrentVersion);
        return definition.CurrentVersion;
    }

    private static Task ApplyStepAsync(DbContext db, SchemaDefinition definition, SchemaStep step, CancellationToken ct)
    {
        // Columns that a later step adds may legitimately be missing from a table this step
        // created on an earlier attempt (the table was made before the column was in the model).
        HashSet<(string Table, string Column)> addedLater = [.. definition.Steps.Where(s => s.Version > step.Version)
            .SelectMany(s => s.Changes).OfType<AddColumnChange>().Select(c => (c.Table, c.Column))];
        return ExecuteAsync(db, step.Changes, addedLater, ct);
    }

    /// <summary>The tables of the current model other than the component's version table, in creation order.</summary>
    private static string[] ModelTables(DbContext db, SchemaDefinition definition)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        IReadOnlyList<MigrationOperation> all = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);
        return [.. all.OfType<CreateTableOperation>().Select(t => t.Name)
            .Where(n => !string.Equals(n, definition.VersionTable, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Turn model-derived changes into provider DDL with EF's own migrations SQL generator, one
    /// statement at a time, each after a catalog check, so that running a change again (after an
    /// interruption, or by a second process) converges instead of failing.
    /// </summary>
    private static async Task ExecuteAsync(
        DbContext db, IReadOnlyList<SchemaChange> changes, IReadOnlySet<(string Table, string Column)> addedLater, CancellationToken ct)
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

        var createTables = all.OfType<CreateTableOperation>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        ILookup<string, CreateIndexOperation> indexesByTable = all.OfType<CreateIndexOperation>()
            .ToLookup(i => i.Table, StringComparer.Ordinal);

        foreach (SchemaChange change in changes)
        {
            switch (change)
            {
                case CreateTableChange create:
                    CreateTableOperation createOp = createTables.GetValueOrDefault(create.Table)
                        ?? throw new InvalidOperationException($"table {create.Table} is not in the model");
                    if (await SchemaCatalog.TableExistsAsync(db, createOp.Name, ct).ConfigureAwait(false))
                    {
                        await RequireModelColumnsAsync(db, createOp, addedLater, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunAsync(db, createOp, ct).ConfigureAwait(false);
                    }

                    foreach (CreateIndexOperation index in indexesByTable[createOp.Name])
                    {
                        await EnsureIndexAsync(db, index, ct).ConfigureAwait(false);
                    }

                    break;

                case AddColumnChange add:
                    CreateTableOperation table = createTables.GetValueOrDefault(add.Table)
                        ?? throw new InvalidOperationException($"table {add.Table} is not in the model");
                    AddColumnOperation column = table.Columns.FirstOrDefault(c => c.Name == add.Column)
                        ?? throw new InvalidOperationException($"column {add.Table}.{add.Column} is not in the model");
                    if (await SchemaCatalog.ColumnExistsAsync(db, table.Name, add.Column, ct).ConfigureAwait(false))
                    {
                        // A table created by an earlier step from the current model already
                        // has the column (an upgrade across both steps): nothing to add.
                        break;
                    }

                    column.Table = table.Name;
                    column.Schema = table.Schema;
                    if (!column.IsNullable && column.DefaultValue is null && column.DefaultValueSql is null)
                    {
                        // Existing rows need a value; SQLite also refuses NOT NULL without a default.
                        column.DefaultValue = column.ClrType.IsValueType ? Activator.CreateInstance(column.ClrType) : string.Empty;
                    }

                    await RunAsync(db, column, ct).ConfigureAwait(false);
                    break;

                case EnsureIndexesChange ensure:
                    if (!createTables.ContainsKey(ensure.Table))
                    {
                        throw new InvalidOperationException($"table {ensure.Table} is not in the model");
                    }

                    if (!await SchemaCatalog.TableExistsAsync(db, ensure.Table, ct).ConfigureAwait(false))
                    {
                        throw new SchemaMismatchException(
                            $"cannot repair the indexes of table {ensure.Table}: the table does not exist in the database.");
                    }

                    foreach (CreateIndexOperation index in indexesByTable[ensure.Table])
                    {
                        await EnsureIndexAsync(db, index, ct).ConfigureAwait(false);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"unsupported schema change {change}");
            }
        }
    }

    /// <summary>
    /// An existing table is kept only if it has exactly the model's columns (ignoring columns that
    /// a later step adds): anything else is another component's table, or damage, and is not adopted.
    /// </summary>
    private static async Task RequireModelColumnsAsync(
        DbContext db, CreateTableOperation expected, IReadOnlySet<(string Table, string Column)> addedLater, CancellationToken ct)
    {
        IReadOnlyList<string> actual = await SchemaCatalog.ReadColumnsAsync(db, expected.Name, ct).ConfigureAwait(false);
        string[] model = [.. expected.Columns.Select(c => c.Name)];
        string[] missing = [.. model.Where(c => !addedLater.Contains((expected.Name, c)) && !actual.Contains(c, StringComparer.OrdinalIgnoreCase))];
        string[] unexpected = [.. actual.Where(c => !model.Contains(c, StringComparer.OrdinalIgnoreCase))];
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new SchemaMismatchException(
                $"table {expected.Name} already exists but does not match the model (missing columns: [{string.Join(", ", missing)}], " +
                $"unexpected columns: [{string.Join(", ", unexpected)}]). Refusing to adopt it; repair or recreate it.");
        }
    }

    private static async Task EnsureIndexAsync(DbContext db, CreateIndexOperation index, CancellationToken ct)
    {
        IReadOnlyList<CatalogIndex> existing = await SchemaCatalog.ReadIndexesAsync(db, index.Table, ct).ConfigureAwait(false);
        bool SameShape(CatalogIndex i) => i.IsUnique == index.IsUnique && i.Columns.SequenceEqual(index.Columns, StringComparer.OrdinalIgnoreCase);

        CatalogIndex? sameName = existing.FirstOrDefault(i => string.Equals(i.Name, index.Name, StringComparison.Ordinal));
        if (sameName is not null)
        {
            if (SameShape(sameName))
            {
                return;
            }

            throw new SchemaMismatchException(
                $"index {index.Name} on {index.Table} exists but is {Describe(sameName.IsUnique, sameName.Columns)}; " +
                $"the model needs {Describe(index.IsUnique, index.Columns)}. Refusing to replace it; drop or rename it.");
        }

        if (existing.Any(SameShape))
        {
            // The same index under another name (a DBA's, or an older build's) satisfies the model.
            return;
        }

        if (index.IsUnique)
        {
            long groups = await CountDuplicateGroupsAsync(db, index, ct).ConfigureAwait(false);
            if (groups > 0)
            {
                throw new SchemaMismatchException(
                    $"cannot create unique index {index.Name} on {index.Table}({string.Join(", ", index.Columns)}): " +
                    $"{groups} group(s) of rows share a value. Remove or fix the duplicates and start again; no rows were changed " +
                    "(docs/integration/schema-index-repair.md).");
            }
        }

        await RunAsync(db, index, ct).ConfigureAwait(false);
    }

    private static string Describe(bool unique, IEnumerable<string> columns)
        => $"{(unique ? "a unique" : "a non-unique")} index on ({string.Join(", ", columns)})";

    /// <summary>
    /// How many distinct values appear in more than one row of the index's columns. Rows with a
    /// NULL in any key column are ignored: unique indexes treat NULLs as distinct on every provider.
    /// </summary>
    private static async Task<long> CountDuplicateGroupsAsync(DbContext db, CreateIndexOperation index, CancellationToken ct)
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

    private static async Task RunAsync(DbContext db, MigrationOperation operation, CancellationToken ct)
    {
        IReadOnlyList<MigrationCommand> commands = db.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], db.GetService<IDesignTimeModel>().Model);
        foreach (MigrationCommand command in commands)
        {
            await db.Database.ExecuteSqlRawAsync(command.CommandText, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteVersionAsync(DbContext db, int version, CancellationToken ct)
    {
        DbSet<SchemaVersionRow> set = db.Set<SchemaVersionRow>();
        SchemaVersionRow? row = await set.FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
        if (row is null)
        {
            set.Add(new SchemaVersionRow { Id = 1, Version = version });
        }
        else
        {
            row.Version = version;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }
}
