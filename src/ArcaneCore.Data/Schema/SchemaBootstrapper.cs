using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Diagnostics;
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

    /// <summary>
    /// Other development lines whose databases this build migrates to its own numbering once (the Codex line before
    /// the 2026-10-07 merge, <see cref="CodexLine"/>); empty for a component no other line renumbered.
    /// </summary>
    public IReadOnlyList<ForeignLine> ForeignLines { get; init; } = [];

    /// <summary>
    /// Versions whose step is an empty <see cref="IReservedSchemaGap"/> placeholder for a module this build lacks.
    /// The bootstrapper and planner refuse to record them as applied (see <see cref="ReservedSchemaGaps"/>).
    /// </summary>
    public IReadOnlyList<int> ReservedGapVersions { get; init; } = [];

    public string VersionTable => Component + "_schema";
}

/// <summary>Raised when a database cannot be brought to the version the code requires.</summary>
/// <remarks>
/// Not sealed: the upgrade tooling (SchemaUpgrader, the startup policy) raises more specific subtypes (<see cref="SchemaDowngradeException"/>,
/// <see cref="SchemaBlockedException"/>, <see cref="SchemaPolicyException"/>) that every existing
/// <c>catch (SchemaMismatchException)</c> still handles.
/// </remarks>
public class SchemaMismatchException(string message) : Exception(message)
{
    /// <summary>Why the bootstrap failed, for callers that map failures to exit codes without parsing the message.</summary>
    public SchemaMismatchReason Reason { get; init; }
}

/// <summary>The cause behind a <see cref="SchemaMismatchException"/> where one is distinguished.</summary>
public enum SchemaMismatchReason
{
    /// <summary>Any other mismatch (unknown state, newer database, damaged table, refused index).</summary>
    Other = 0,

    /// <summary>The wait for another process's schema lock ran out.</summary>
    LockTimeout = 1,
}

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
    public static Task EnsureAsync(
        DbContext db, SchemaDefinition definition, TimeSpan lockTimeout, ILogger? logger = null, CancellationToken cancellationToken = default)
        => EnsureAsync(db, definition, new SchemaUpgradeOptions { LockTimeout = lockTimeout }, logger, cancellationToken);

    /// <summary>
    /// <see cref="EnsureAsync(DbContext, SchemaDefinition, TimeSpan, ILogger?, CancellationToken)"/> with the
    /// upgrade tooling's options: the startup policy (checked before the database is created and again, under
    /// the lock, against the state the lock holder sees), the lock wait and a per-step progress callback.
    /// </summary>
    public static async Task EnsureAsync(
        DbContext db, SchemaDefinition definition, SchemaUpgradeOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeSpan lockTimeout = options.LockTimeout;
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (options.Policy == SchemaPolicy.Never)
            {
                // Refuse before CreateAsync: policy Never creates nothing, not even an empty database.
                throw new SchemaPolicyException(SchemaPolicyException.MissingDatabaseMessage(definition), SchemaState.Missing, null, definition.CurrentVersion);
            }

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
            await using (SchemaLock schemaLock = await SchemaLock.AcquireAsync(db, definition.Component, lockTimeout, cancellationToken, logger)
                .ConfigureAwait(false))
            {
                await RunAsync(db, definition, options, logger, cancellationToken).ConfigureAwait(false);
                await schemaLock.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            // Only a database this build accepted: a refused one is left byte-for-byte untouched.
            await UseWriteAheadLogAsync(db, definition, logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Put a file-backed SQLite database in write-ahead-log mode (persistent in the file). In the default
    /// rollback-journal mode a writer cannot commit while another connection holds a read lock, and two
    /// connections that both read before writing make SQLite answer SQLITE_BUSY at once instead of waiting
    /// (its deadlock avoidance skips the busy handler): the server's background write queues and a character
    /// create on another connection then fail with "database is locked". WAL lets readers and the single
    /// writer proceed together. Best effort: if another connection holds the file, the mode stays as it is
    /// and is retried at the next start. In-memory databases are left alone.
    /// </summary>
    private static async Task UseWriteAheadLogAsync(DbContext db, SchemaDefinition definition, ILogger? logger, CancellationToken ct)
    {
        if (!db.Database.IsSqlite()
            || db.Database.GetDbConnection() is not Microsoft.Data.Sqlite.SqliteConnection connection
            || string.IsNullOrEmpty(connection.DataSource)
            || connection.DataSource == ":memory:"
            || new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection.ConnectionString).Mode
                is Microsoft.Data.Sqlite.SqliteOpenMode.Memory or Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        string? mode;
        try
        {
            mode = (await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            logger?.LogWarning(ex, "{Component}: could not switch the SQLite database to WAL mode; it keeps its journal mode", definition.Component);
            return;
        }

        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("{Component}: SQLite journal mode is {Mode}, not WAL (another connection may hold the file); concurrent writes can fail with 'database is locked'",
                definition.Component, mode);
        }
    }

    private static async Task RunAsync(DbContext db, SchemaDefinition definition, SchemaUpgradeOptions options, ILogger? logger, CancellationToken ct)
    {
        // The startup policy, judged on the read-only plan under the lock (before anything, including the
        // version-0 marker of a resumed create, is written): a process that waited sees the winner's work.
        if (options.Policy != SchemaPolicy.Always)
        {
            SchemaPlan plan = await SchemaPlanner.PlanAsync(db, definition, includeScript: false, options.AllowReservedSchemaGaps, ct).ConfigureAwait(false);
            SchemaPolicyException.ThrowIfForbidden(options.Policy, plan);
        }

        // Read only now that the lock is held: a process that waited sees the winner's work.
        int? version = await TryReadVersionAsync(db, definition, ct).ConfigureAwait(false);

        // Fail closed before any create, adoption or step: a reserved placeholder version recorded as applied would make
        // every later build skip the owner's real step (ReservedSchemaGaps).
        IReadOnlyList<int> pendingGaps = ReservedSchemaGaps.Pending(definition, version);
        if (pendingGaps.Count > 0 && !options.AllowReservedSchemaGaps)
        {
            throw new SchemaMismatchException(ReservedSchemaGaps.RefusalMessage(definition, version, pendingGaps));
        }

        if (version is null)
        {
            // A database without a version table was either empty (created fresh, at the current version)
            // or a pre-M5 layout adopted as version 1; CreateOrAdoptAsync throws for anything in between.
            version = await CreateOrAdoptAsync(db, definition, logger, ct).ConfigureAwait(false);
            Invariant.Assert(version == 1 || version == definition.CurrentVersion, $"{definition.Component}: create-or-adopt returned version {version}, expected 1 or {definition.CurrentVersion}");
            options.Report(definition, version.Value);
        }
        else if (version == CreatingVersion)
        {
            // A resumed create ends by writing CurrentVersion (CreateFreshAsync's last statement).
            version = await CreateFreshAsync(db, definition, logger, ct).ConfigureAwait(false);
            Invariant.Assert(version == definition.CurrentVersion, $"{definition.Component}: a resumed create returned version {version}, expected {definition.CurrentVersion}");
            options.Report(definition, version.Value);
        }

        if (version > CreatingVersion && definition.ForeignLines.Count > 0)
        {
            // A database another line created records that line's numbers; migrate it to ours once, before the step
            // loop reads those numbers as this build's steps (or refuse a database that matches neither line).
            ForeignLineDetection detection = await ForeignLineDetector.DetectAsync(db, definition, version.Value, ct).ConfigureAwait(false);
            if (detection.Refusal is not null)
            {
                throw new SchemaMismatchException(detection.Refusal);
            }

            if (detection.Match is { } match)
            {
                version = await MigrateForeignLineAsync(db, definition, match, logger, ct).ConfigureAwait(false);
                options.Report(definition, version.Value);
            }
        }

        if (version > definition.CurrentVersion)
        {
            throw new SchemaMismatchException(SchemaChangeDecider.NewerMessage(definition, version.Value));
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
            options.Report(definition, step.Version);
        }

        if (version != definition.CurrentVersion)
        {
            throw new SchemaMismatchException(
                $"The {definition.Component} schema is at version {version} but this build needs " +
                $"{definition.CurrentVersion} and has no upgrade path.");
        }
    }

    /// <summary>
    /// The operator's one-shot migration of a database another line created (<see cref="SchemaDefinition.ForeignLines"/>)
    /// to this build's numbering, without the ordinary upgrade that normally follows it (arcane-db migrate-codex --apply).
    /// Under the schema lock: detect again, then migrate. Returns what was migrated, or null when the database is not
    /// another line's (missing, fresh, without a version, or already on this build's numbering).
    /// </summary>
    /// <exception cref="SchemaMismatchException">The database matches neither line, or the lock wait ran out.</exception>
    public static async Task<ForeignLineMatch?> MigrateForeignLineAsync(
        DbContext db, SchemaDefinition definition, SchemaUpgradeOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (definition.ForeignLines.Count == 0 || !await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SchemaLock schemaLock = await SchemaLock.AcquireAsync(db, definition.Component, options.LockTimeout, cancellationToken, logger)
                .ConfigureAwait(false);
            ForeignLineMatch? migrated = null;
            int? version = await ReadRecordedVersionAsync(db, definition, cancellationToken).ConfigureAwait(false);
            if (version > CreatingVersion)
            {
                ForeignLineDetection detection = await ForeignLineDetector.DetectAsync(db, definition, version.Value, cancellationToken).ConfigureAwait(false);
                if (detection.Refusal is not null)
                {
                    throw new SchemaMismatchException(detection.Refusal);
                }

                if (detection.Match is { } match)
                {
                    int merged = await MigrateForeignLineAsync(db, definition, match, logger, cancellationToken).ConfigureAwait(false);
                    options.Report(definition, merged);
                    migrated = match;
                }
            }

            await schemaLock.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return migrated;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Apply this build's steps after the divergence up to the merged version (each change idempotent: the foreign
    /// line's tables are verified against the model, the steps it never had are created), run the data moves, then
    /// record the merged version. On SQLite all of it commits or none; elsewhere a rerun converges, because the
    /// version row is written last and the detection accepts this build's objects beside the foreign ones.
    /// </summary>
    private static async Task<int> MigrateForeignLineAsync(
        DbContext db, SchemaDefinition definition, ForeignLineMatch match, ILogger? logger, CancellationToken ct)
    {
        logger?.LogWarning(
            "The {Component} database was created by the {Line} line (schema version {ForeignVersion}); migrating it once to this build's numbering " +
            "(schema version {MergedVersion}). Found: {Evidence}",
            definition.Component, match.Line.Name, match.ForeignVersion, match.MergedVersion, string.Join("; ", match.Evidence));
        // A move whose step lies outside the migrated range would silently not run; the line definition forbids it.
        Invariant.Assert(match.DataMoves.All(m => match.Steps.Any(s => s.Version == m.AfterMergedVersion)),
            $"{definition.Component}: a {match.Line.Name} data move follows a step outside the migrated range");
        long moved = 0;
        foreach (SchemaStep step in match.Steps)
        {
            await ApplyStepAsync(db, definition, step, ct).ConfigureAwait(false);
            foreach (ForeignLineDataMove move in match.DataMoves.Where(m => m.AfterMergedVersion == step.Version))
            {
                long rows = await move.ApplyAsync(db, ct).ConfigureAwait(false);
                moved += rows;
                logger?.LogInformation("{Component} migration: {Move} ({Rows} rows)", definition.Component, move.Description, rows);
            }
        }

        await WriteVersionAsync(db, match.MergedVersion, ct).ConfigureAwait(false);
        logger?.LogWarning(
            "Migrated the {Component} database from {Line} schema version {ForeignVersion} to schema version {MergedVersion} " +
            "({Steps} steps verified or applied, {Rows} rows moved); every row was kept",
            definition.Component, match.Line.Name, match.ForeignVersion, match.MergedVersion, match.Steps.Count, moved);
        return match.MergedVersion;
    }

    /// <summary>The recorded version without the side effects of <see cref="TryReadVersionAsync"/> (it writes nothing); null without a row.</summary>
    private static async Task<int?> ReadRecordedVersionAsync(DbContext db, SchemaDefinition definition, CancellationToken ct)
    {
        if (!await SchemaCatalog.TableExistsAsync(db, definition.VersionTable, ct).ConfigureAwait(false))
        {
            return null;
        }

        SchemaVersionRow? row = await db.Set<SchemaVersionRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
        return row?.Version;
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
            // Versions are 0 (a create in progress) or a released version from 1 up; a negative row is a
            // damaged table, and the bootstrap fails closed on it like on any other unexpected state.
            if (!Invariant.Check(row.Version >= CreatingVersion, $"the {definition.VersionTable} row holds version {row.Version}"))
            {
                throw new SchemaMismatchException(
                    $"The {definition.Component} schema version table holds {row.Version}, which is not a version this build knows; the table is damaged.");
            }

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

        throw new SchemaMismatchException(SchemaChangeDecider.NoRowMessage(definition, present, modelTables.Length));
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

        throw new SchemaMismatchException(SchemaChangeDecider.PartialV1Message(definition, present));
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
        => ExecuteAsync(db, step.Changes, SchemaChangeDecider.AddedLater(definition, step), ct);

    /// <summary>The tables of the current model other than the component's version table, in creation order.</summary>
    private static string[] ModelTables(DbContext db, SchemaDefinition definition)
        => SchemaChangeDecider.ModelTables(db, definition);

    /// <summary>
    /// Turn model-derived changes into provider DDL with EF's own migrations SQL generator, one
    /// statement at a time, each after a catalog check, so that running a change again (after an
    /// interruption, or by a second process) converges instead of failing.
    /// </summary>
    private static async Task ExecuteAsync(
        DbContext db, IReadOnlyList<SchemaChange> changes, IReadOnlySet<(string Table, string Column)> addedLater, CancellationToken ct)
    {
        SchemaChangeDecider.ModelOperations ops = SchemaChangeDecider.ReadModel(db);
        IReadOnlyDictionary<string, CreateTableOperation> createTables = ops.Tables;
        ILookup<string, CreateIndexOperation> indexesByTable = ops.IndexesByTable;

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
                    if (!table.Columns.Any(c => c.Name == add.Column))
                    {
                        throw new InvalidOperationException($"column {add.Table}.{add.Column} is not in the model");
                    }

                    if (await SchemaCatalog.ColumnExistsAsync(db, table.Name, add.Column, ct).ConfigureAwait(false))
                    {
                        // A table created by an earlier step from the current model already
                        // has the column (an upgrade across both steps): nothing to add.
                        break;
                    }

                    await RunAsync(db, SchemaChangeDecider.PrepareAddColumn(table, add.Column), ct).ConfigureAwait(false);
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
    /// An existing table is kept only if it has exactly the model's columns (see
    /// <see cref="SchemaChangeDecider.ColumnMismatch"/>).
    /// </summary>
    private static async Task RequireModelColumnsAsync(
        DbContext db, CreateTableOperation expected, IReadOnlySet<(string Table, string Column)> addedLater, CancellationToken ct)
    {
        IReadOnlyList<string> actual = await SchemaCatalog.ReadColumnsAsync(db, expected.Name, ct).ConfigureAwait(false);
        string? mismatch = SchemaChangeDecider.ColumnMismatch(expected, actual, addedLater);
        if (mismatch is not null)
        {
            throw new SchemaMismatchException(mismatch);
        }
    }

    /// <summary>Create a model index unless the shared decision says it is satisfied; refuse a conflict or duplicates.</summary>
    private static async Task EnsureIndexAsync(DbContext db, CreateIndexOperation index, CancellationToken ct)
    {
        IReadOnlyList<CatalogIndex> existing = await SchemaCatalog.ReadIndexesAsync(db, index.Table, ct).ConfigureAwait(false);
        SchemaChangeDecider.IndexDecision decision = await SchemaChangeDecider.DecideIndexAsync(db, index, existing, tableIsEmpty: false, ct).ConfigureAwait(false);
        switch (decision.Decision)
        {
            case ChangeDecision.Create:
                await RunAsync(db, index, ct).ConfigureAwait(false);
                return;
            case ChangeDecision.Satisfied or ChangeDecision.SatisfiedUnderOtherName:
                return;
            default:
                throw new SchemaMismatchException(decision.Message);
        }
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
            // Monotonic: the row only ever moves up (each step writes version + 1, a fresh create writes
            // CurrentVersion after its 0 marker). The 0 marker may be rewritten while a create is resumed.
            // A downgrade write would make a newer database look older and let a later start "upgrade"
            // over tables it does not understand, so it is refused and the start fails closed.
            if (!Invariant.Check(version == CreatingVersion || version > row.Version, $"schema version row would move from {row.Version} to {version}"))
            {
                throw new SchemaMismatchException(
                    $"Refusing to move the schema version row from {row.Version} to {version}: versions only increase.");
            }

            row.Version = version;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }
}
