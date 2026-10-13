using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>
/// The read-only dry run of <see cref="SchemaBootstrapper"/>: classifies a component's database
/// and lists what an apply would do, change by change, evaluated against the live catalog with
/// the tables and indexes that earlier pending changes will create tracked virtually.
/// <para>
/// It never creates the database, never takes the schema lock and never writes (in particular it
/// does not write the version-0 marker that the bootstrapper's own version read writes). Every
/// per-change decision comes from <see cref="SchemaChangeDecider"/>, the code the apply runs, so
/// the plan cannot say something the apply does not do. Not provable here: MariaDB's implicit DDL
/// commit means an apply that is refused midway can leave earlier steps applied; the plan
/// front-loads every refusal it can see so that normally nothing is issued.
/// </para>
/// </summary>
public static class SchemaPlanner
{
    /// <param name="db">The component's context. A SQLite file that does not exist is reported <see cref="SchemaState.Missing"/>, never created.</param>
    /// <param name="definition">The schema the code needs.</param>
    /// <param name="includeScript">Also render the DDL of every <see cref="ChangeDecision.Create"/> and the version-row statements.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static Task<SchemaPlan> PlanAsync(
        DbContext db, SchemaDefinition definition, bool includeScript = false, CancellationToken cancellationToken = default)
        => PlanAsync(db, definition, includeScript, ReservedSchemaGaps.AllowedInThisProcess, cancellationToken);

    /// <summary>
    /// <see cref="PlanAsync(DbContext, SchemaDefinition, bool, CancellationToken)"/> with an explicit answer to whether a
    /// create or upgrade may pass through a reserved placeholder version (<see cref="ReservedSchemaGaps"/>); when it may not,
    /// such a plan is refused like the apply.
    /// </summary>
    public static async Task<SchemaPlan> PlanAsync(
        DbContext db, SchemaDefinition definition, bool includeScript, bool allowReservedGaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);

        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            var offline = new Session(db, definition, includeScript, offline: true, allowReservedGaps);
            return await offline.PlanCreateAsync(SchemaState.Missing, null, cancellationToken).ConfigureAwait(false);
        }

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await new Session(db, definition, includeScript, offline: false, allowReservedGaps).PlanAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The plan with no database at all: a component's baseline (<paramref name="from"/> null: an empty database to the current
    /// schema) or the steps after <paramref name="fromVersion"/> up to <paramref name="throughVersion"/>, evaluated against
    /// <paramref name="from"/>, a virtual catalog (a snapshot of the schema after the previous update file). It never calls
    /// <c>ExistsAsync</c>, never opens a connection and reads nothing from <paramref name="db"/> but its model and SQL
    /// generator, so the context may carry a placeholder connection string (the update files are exported without a server).
    /// Reserved placeholder versions are refused. Unique indexes are decided as on an empty table: no rows exist to collide.
    /// </summary>
    internal static async Task<SchemaPlan> PlanOfflineAsync(
        DbContext db, SchemaDefinition definition, SnapshotCatalog? from, int fromVersion, int throughVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);
        var session = new Session(db, definition, script: true, offline: true, allowReservedGaps: false, from ?? SnapshotCatalog.Empty());
        return from is null
            ? await session.PlanCreateAsync(SchemaState.Missing, null, cancellationToken).ConfigureAwait(false)
            : await session.PlanStepsAsync(SchemaState.Behind, fromVersion, [], cancellationToken, throughVersion).ConfigureAwait(false);
    }

    private sealed class Session
    {
        private readonly DbContext _db;
        private readonly SchemaDefinition _definition;
        private readonly bool _script;
        private readonly bool _offline;
        private readonly bool _allowReservedGaps;
        private readonly SnapshotCatalog? _catalog;
        private readonly SchemaChangeDecider.ModelOperations _ops;
        private readonly HashSet<string> _virtualTables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _virtualColumns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogIndex>> _indexes = new(StringComparer.Ordinal);

        public Session(DbContext db, SchemaDefinition definition, bool script, bool offline, bool allowReservedGaps, SnapshotCatalog? catalog = null)
        {
            _catalog = catalog ?? (offline ? SnapshotCatalog.Empty() : null);
            _db = db;
            _definition = definition;
            _script = script;
            _offline = offline;
            _allowReservedGaps = allowReservedGaps;
            _ops = SchemaChangeDecider.ReadModel(db, definition);
        }

        public async Task<SchemaPlan> PlanAsync(CancellationToken ct)
        {
            int? version = await ReadVersionAsync(ct).ConfigureAwait(false);
            if (version is null)
            {
                return await PlanNoVersionAsync(ct).ConfigureAwait(false);
            }

            if (version == SchemaBootstrapper.CreatingVersion)
            {
                return await PlanCreateAsync(SchemaState.Creating, version, ct).ConfigureAwait(false);
            }

            if (version > SchemaBootstrapper.CreatingVersion && _definition.ForeignLines.Count > 0)
            {
                ForeignLineDetection detection = await ForeignLineDetector.DetectAsync(_db, _definition, version.Value, ct).ConfigureAwait(false);
                if (detection.Refusal is not null)
                {
                    return Refused(SchemaState.Unknown, version, detection.Refusal);
                }

                if (detection.Match is { } match)
                {
                    return await PlanForeignLineAsync(match, ct).ConfigureAwait(false);
                }
            }

            if (version > _definition.CurrentVersion)
            {
                return Refused(SchemaState.Newer, version, SchemaChangeDecider.NewerMessage(_definition, version.Value));
            }

            if (version == _definition.CurrentVersion)
            {
                return new SchemaPlan(_definition.Component, SchemaState.Current, version, _definition.CurrentVersion, [], null);
            }

            return await PlanStepsAsync(SchemaState.Behind, version.Value, [], ct).ConfigureAwait(false);
        }

        /// <summary>The recorded version, or null when there is no version table or it holds no row (nothing is written).</summary>
        private async Task<int?> ReadVersionAsync(CancellationToken ct)
        {
            if (!await SchemaCatalog.TableExistsAsync(_db, _definition.VersionTable, ct).ConfigureAwait(false))
            {
                return null;
            }

            SchemaVersionRow? row = await _db.Set<SchemaVersionRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
            return row?.Version;
        }

        private async Task<SchemaPlan> PlanNoVersionAsync(CancellationToken ct)
        {
            if (await SchemaCatalog.TableExistsAsync(_db, _definition.VersionTable, ct).ConfigureAwait(false))
            {
                // A version table without a row: resumable only if the model tables are all absent or all present.
                string[] modelTables = SchemaChangeDecider.ModelTables(_db, _definition);
                int presentModel = await CountPresentAsync(modelTables, ct).ConfigureAwait(false);
                return presentModel == 0 || presentModel == modelTables.Length
                    ? await PlanCreateAsync(SchemaState.NoVersionRow, null, ct).ConfigureAwait(false)
                    : Refused(SchemaState.Unknown, null, SchemaChangeDecider.NoRowMessage(_definition, presentModel, modelTables.Length));
            }

            int present = await CountPresentAsync(_definition.Version1Tables, ct).ConfigureAwait(false);
            if (present == 0)
            {
                return await PlanCreateAsync(SchemaState.Fresh, null, ct).ConfigureAwait(false);
            }

            if (present == _definition.Version1Tables.Count)
            {
                // Pre-M5 database: the bootstrapper creates the version table, records version 1, then runs the steps.
                var adopt = new List<PlannedAction>();
                await CreateTableAsync(_definition.VersionTable, new HashSet<(string, string)>(), adopt, ct).ConfigureAwait(false);
                var first = new PlannedStep(1, "adopt the existing tables as schema version 1", adopt, InsertStatement(1));
                return await PlanStepsAsync(SchemaState.AdoptV1, 1, [first], ct).ConfigureAwait(false);
            }

            return Refused(SchemaState.Unknown, null, SchemaChangeDecider.PartialV1Message(_definition, present));
        }

        /// <summary>Fresh create (or resume): the version table, then every model table, then the current version.</summary>
        public async Task<SchemaPlan> PlanCreateAsync(SchemaState state, int? version, CancellationToken ct)
        {
            if (RefuseReservedGaps(state, version, version) is { } refused)
            {
                return refused;
            }

            var noLater = new HashSet<(string, string)>();
            var actions = new List<PlannedAction>();
            await CreateTableAsync(_definition.VersionTable, noLater, actions, ct).ConfigureAwait(false);
            foreach (string table in SchemaChangeDecider.ModelTables(_db, _definition))
            {
                await CreateTableAsync(table, noLater, actions, ct).ConfigureAwait(false);
            }

            // Fresh creation creates model tables directly rather than replaying every module step;
            // retain each module's idempotent seed in the plan so the SQL manifest and readiness
            // review show the same row the bootstrapper applies after the tables.
            foreach (EnsureRowChange seed in _definition.Steps.SelectMany(s => s.Changes).OfType<EnsureRowChange>())
            {
                await PlanChangeAsync(seed, noLater, actions, ct).ConfigureAwait(false);
            }

            string description = state == SchemaState.Missing
                ? "create the database and the current schema"
                : "create (or resume creating) the current schema";
            string? statement = state is SchemaState.Creating ? UpdateStatement(_definition.CurrentVersion) : InsertStatement(_definition.CurrentVersion);
            return new SchemaPlan(
                _definition.Component, state, version, _definition.CurrentVersion,
                [new PlannedStep(_definition.CurrentVersion, description, actions, statement)], null);
        }

        /// <summary>
        /// Another line's database: one step that verifies or applies this build's steps after the divergence up to
        /// the merged version and runs the data moves (counted, not run; with a script, rendered right after the step
        /// they follow, where the bootstrapper runs them), then the ordinary steps after it.
        /// </summary>
        private async Task<SchemaPlan> PlanForeignLineAsync(ForeignLineMatch match, CancellationToken ct)
        {
            var rows = new Dictionary<ForeignLineDataMove, long>(ReferenceEqualityComparer.Instance);
            var moves = new List<string>();
            foreach (ForeignLineDataMove move in match.DataMoves)
            {
                rows[move] = await move.CountAsync(_db, ct).ConfigureAwait(false);
                moves.Add($"{move.Description} ({rows[move]} rows)");
            }

            var actions = new List<PlannedAction>();
            var placed = new List<PlannedDataMove>();
            foreach (SchemaStep step in match.Steps)
            {
                HashSet<(string Table, string Column)> addedLater = SchemaChangeDecider.AddedLater(_definition, step);
                foreach (SchemaChange change in step.Changes)
                {
                    await PlanChangeAsync(change, addedLater, actions, ct).ConfigureAwait(false);
                }

                // Where SchemaBootstrapper.MigrateForeignLineAsync runs it: right after the step it follows, before any later step.
                placed.AddRange(match.DataMoves.Where(m => m.AfterMergedVersion == step.Version)
                    .Select(m => new PlannedDataMove(actions.Count, m.Description, rows[m], _script ? m.Sql?.Invoke(_db) : null)));
            }

            // A move after a step outside the migrated range is a line definition the bootstrapper refuses to run: listed
            // without statements, so that a script of the plan is refused rather than written without it.
            placed.AddRange(match.DataMoves.Where(m => match.Steps.All(s => s.Version != m.AfterMergedVersion))
                .Select(m => new PlannedDataMove(actions.Count, m.Description, rows[m])));

            string description = $"migrate from the {match.Line.Name} numbering (schema version {match.ForeignVersion}): verify or apply steps " +
                                 $"{match.Line.DivergedAfter + 1}-{match.MergedVersion}" + (moves.Count == 0 ? string.Empty : "; " + string.Join("; ", moves));
            var first = new PlannedStep(match.MergedVersion, description, actions, UpdateStatement(match.MergedVersion)) { DataMoves = placed };
            SchemaPlan plan = await PlanStepsAsync(SchemaState.Behind, match.MergedVersion, [first], ct).ConfigureAwait(false);
            return plan with { DatabaseVersion = match.ForeignVersion, ForeignLine = match };
        }

        public async Task<SchemaPlan> PlanStepsAsync(SchemaState state, int from, List<PlannedStep> prefix, CancellationToken ct, int? through = null)
        {
            int target = through ?? _definition.CurrentVersion;
            if (RefuseReservedGaps(state, state == SchemaState.AdoptV1 ? null : from, from) is { } refused)
            {
                return refused;
            }

            var steps = new List<PlannedStep>(prefix);
            int version = from;
            foreach (SchemaStep step in _definition.Steps.Where(s => s.Version > from && s.Version <= target).OrderBy(s => s.Version))
            {
                if (step.Version != version + 1)
                {
                    return new SchemaPlan(
                        _definition.Component, SchemaState.Unknown, from, _definition.CurrentVersion, steps,
                        $"No upgrade path for the {_definition.Component} schema from version {version} to {step.Version}.");
                }

                var actions = new List<PlannedAction>();
                HashSet<(string Table, string Column)> addedLater = SchemaChangeDecider.AddedLater(_definition, step);
                foreach (SchemaChange change in step.Changes)
                {
                    await PlanChangeAsync(change, addedLater, actions, ct).ConfigureAwait(false);
                }

                steps.Add(new PlannedStep(step.Version, $"upgrade to schema version {step.Version}", actions, UpdateStatement(step.Version)));
                version = step.Version;
            }

            if (version != target)
            {
                return new SchemaPlan(
                    _definition.Component, SchemaState.Unknown, from, _definition.CurrentVersion, steps,
                    $"The {_definition.Component} schema is at version {version} but this build needs " +
                    $"{target} and has no upgrade path.");
            }

            return new SchemaPlan(_definition.Component, state, state == SchemaState.AdoptV1 ? null : from, _definition.CurrentVersion, steps, null);
        }

        private async Task PlanChangeAsync(
            SchemaChange change, IReadOnlySet<(string Table, string Column)> addedLater, List<PlannedAction> actions, CancellationToken ct)
        {
            switch (change)
            {
                case CreateTableChange create:
                    await CreateTableAsync(create.Table, addedLater, actions, ct).ConfigureAwait(false);
                    break;

                case AddColumnChange add:
                    CreateTableOperation table = _ops.Tables.GetValueOrDefault(add.Table)
                        ?? throw new InvalidOperationException($"table {add.Table} is not in the model");
                    if (!table.Columns.Any(c => c.Name == add.Column))
                    {
                        throw new InvalidOperationException($"column {add.Table}.{add.Column} is not in the model");
                    }

                    if (!await TableExistsAsync(add.Table, ct).ConfigureAwait(false))
                    {
                        actions.Add(new PlannedAction(
                            ChangeObject.Column, add.Table, add.Column, ChangeDecision.MissingTable,
                            $"cannot add column {add.Table}.{add.Column}: the table does not exist in the database."));
                    }
                    else if (_virtualTables.Contains(add.Table) || (_virtualColumns.TryGetValue(add.Table, out HashSet<string>? added) && added.Contains(add.Column))
                        || await ColumnExistsAsync(add.Table, add.Column, ct).ConfigureAwait(false))
                    {
                        actions.Add(new PlannedAction(ChangeObject.Column, add.Table, add.Column, ChangeDecision.Satisfied, $"column {add.Table}.{add.Column} exists"));
                    }
                    else
                    {
                        AddColumnOperation column = SchemaChangeDecider.PrepareAddColumn(table, add.Column);
                        if (!_virtualColumns.TryGetValue(add.Table, out HashSet<string>? set))
                        {
                            _virtualColumns[add.Table] = set = new HashSet<string>(StringComparer.Ordinal);
                        }

                        set.Add(add.Column);
                        actions.Add(new PlannedAction(
                            ChangeObject.Column, add.Table, add.Column, ChangeDecision.Create, $"column {add.Table}.{add.Column} will be added",
                            Script: Render(column)) { Operation = column });
                    }

                    break;

                case EnsureIndexesChange ensure:
                    if (!_ops.Tables.ContainsKey(ensure.Table))
                    {
                        throw new InvalidOperationException($"table {ensure.Table} is not in the model");
                    }

                    if (!await TableExistsAsync(ensure.Table, ct).ConfigureAwait(false))
                    {
                        actions.Add(new PlannedAction(
                            ChangeObject.Table, ensure.Table, null, ChangeDecision.MissingTable,
                            $"cannot repair the indexes of table {ensure.Table}: the table does not exist in the database."));
                        break;
                    }

                    foreach (CreateIndexOperation index in _ops.IndexesByTable[ensure.Table])
                    {
                        actions.Add(await DecideIndexAsync(index, ct).ConfigureAwait(false));
                    }

                    break;

                case EnsureRowChange seed:
                    if (!_ops.Tables.ContainsKey(seed.Table))
                        throw new InvalidOperationException($"table {seed.Table} is not in the model");
                    bool seedTableExists = await TableExistsAsync(seed.Table, ct).ConfigureAwait(false);
                    actions.Add(new PlannedAction(
                        ChangeObject.Row, seed.Table, seed.KeyColumn,
                        seedTableExists ? ChangeDecision.Create : ChangeDecision.MissingTable,
                        seedTableExists
                            ? $"row {seed.KeyColumn}={seed.KeyValue} will be ensured"
                            : $"cannot ensure row {seed.KeyColumn}={seed.KeyValue}: the table does not exist in the database.",
                        Script: _script && seedTableExists ? Render(seed) : null));
                    break;

                default:
                    throw new InvalidOperationException($"unsupported schema change {change}");
            }
        }

        private async Task CreateTableAsync(
            string table, IReadOnlySet<(string Table, string Column)> addedLater, List<PlannedAction> actions, CancellationToken ct)
        {
            CreateTableOperation op = _ops.Tables.GetValueOrDefault(table)
                ?? throw new InvalidOperationException($"table {table} is not in the model");
            if (_offline && !_virtualTables.Contains(table) && !_catalog!.HasTable(table))
            {
                op = WithoutLaterColumns(op, addedLater);
            }

            if (await TableExistsAsync(table, ct).ConfigureAwait(false))
            {
                if (_virtualTables.Contains(table))
                {
                    actions.Add(new PlannedAction(ChangeObject.Table, table, null, ChangeDecision.Satisfied, $"table {table} is created by an earlier change"));
                }
                else
                {
                    IReadOnlyList<string> actual = _offline
                        ? _catalog!.ColumnNames(table)
                        : await SchemaCatalog.ReadColumnsAsync(_db, table, ct).ConfigureAwait(false);
                    string? mismatch = SchemaChangeDecider.ColumnMismatch(op, actual, addedLater);
                    if (mismatch is not null)
                    {
                        actions.Add(new PlannedAction(ChangeObject.Table, table, null, ChangeDecision.ModelMismatch, mismatch));
                        return;
                    }

                    actions.Add(new PlannedAction(ChangeObject.Table, table, null, ChangeDecision.Satisfied, $"table {table} exists with the model's columns"));
                }
            }
            else
            {
                _virtualTables.Add(table);
                _indexes[table] = [];
                actions.Add(new PlannedAction(
                    ChangeObject.Table, table, null, ChangeDecision.Create, $"table {table} will be created", Script: Render(op)) { Operation = op });
            }

            foreach (CreateIndexOperation index in _ops.IndexesByTable[table])
            {
                actions.Add(await DecideIndexAsync(index, ct).ConfigureAwait(false));
            }
        }

        private async Task<PlannedAction> DecideIndexAsync(CreateIndexOperation index, CancellationToken ct)
        {
            bool tableIsNew = _virtualTables.Contains(index.Table) || _offline;
            if (!_indexes.TryGetValue(index.Table, out List<CatalogIndex>? existing))
            {
                existing = _offline
                    ? [.. _catalog!.IndexesOf(index.Table)]
                    : [.. await SchemaCatalog.ReadIndexesAsync(_db, index.Table, ct).ConfigureAwait(false)];
                _indexes[index.Table] = existing;
            }

            // A table the plan creates is empty: its unique indexes cannot meet duplicates.
            SchemaChangeDecider.IndexDecision decision = await SchemaChangeDecider.DecideIndexAsync(_db, index, existing, tableIsNew, ct).ConfigureAwait(false);
            if (decision.Decision == ChangeDecision.Create)
            {
                existing.Add(new CatalogIndex(index.Name, index.IsUnique, [.. index.Columns]));
            }

            return new PlannedAction(
                ChangeObject.Index, index.Table, index.Name, decision.Decision, decision.Message, decision.DuplicateGroups,
                decision.Decision == ChangeDecision.Create ? Render(index) : null)
            {
                Operation = decision.Decision == ChangeDecision.Create ? index : null,
            };
        }

        private async Task<bool> TableExistsAsync(string table, CancellationToken ct)
            => _virtualTables.Contains(table)
                || (_offline
                    ? _catalog?.HasTable(table) == true
                    : await SchemaCatalog.TableExistsAsync(_db, table, ct).ConfigureAwait(false));

        private async Task<bool> ColumnExistsAsync(string table, string column, CancellationToken ct)
            => _offline
                ? _catalog?.HasColumn(table, column) == true
                : await SchemaCatalog.ColumnExistsAsync(_db, table, column, ct).ConfigureAwait(false);

        /// <summary>
        /// The table as it exists at this step when a later step adds some of its columns (the bootstrapper tolerates such a
        /// table lacking them): a file written for step N must not carry what step N+1 adds, or a database that reached N by
        /// another route would skip N+1's column. Refuses a table whose key or an index uses a column that is held back.
        /// </summary>
        private CreateTableOperation WithoutLaterColumns(CreateTableOperation op, IReadOnlySet<(string Table, string Column)> addedLater)
        {
            string[] held = [.. op.Columns.Select(c => c.Name).Where(c => addedLater.Contains((op.Name, c)))];
            if (held.Length == 0)
            {
                return op;
            }

            if ((op.PrimaryKey?.Columns.Any(held.Contains) ?? false) || _ops.IndexesByTable[op.Name].Any(i => i.Columns.Any(held.Contains)))
            {
                throw new NotSupportedException(
                    $"table {op.Name} is created by one step and gets {string.Join(", ", held)} from a later step, but its key or an index uses that column; " +
                    "export the steps in separate releases.");
            }

            var trimmed = new CreateTableOperation { Name = op.Name, Schema = op.Schema, Comment = op.Comment, PrimaryKey = op.PrimaryKey };
            trimmed.Columns.AddRange(op.Columns.Where(c => !held.Contains(c.Name)));
            trimmed.UniqueConstraints.AddRange(op.UniqueConstraints);
            trimmed.ForeignKeys.AddRange(op.ForeignKeys);
            trimmed.CheckConstraints.AddRange(op.CheckConstraints);
            foreach (var annotation in op.GetAnnotations())
            {
                trimmed.AddAnnotation(annotation.Name, annotation.Value);
            }

            return trimmed;
        }

        private async Task<int> CountPresentAsync(IEnumerable<string> tables, CancellationToken ct)
        {
            int present = 0;
            foreach (string table in tables)
            {
                if (await SchemaCatalog.TableExistsAsync(_db, table, ct).ConfigureAwait(false))
                {
                    present++;
                }
            }

            return present;
        }

        /// <summary>The apply's refusal to record a reserved placeholder version as applied (<see cref="ReservedSchemaGaps"/>), or null.</summary>
        private SchemaPlan? RefuseReservedGaps(SchemaState state, int? version, int? from)
        {
            IReadOnlyList<int> pending = ReservedSchemaGaps.Pending(_definition, from);
            return pending.Count == 0 || _allowReservedGaps
                ? null
                : Refused(state, version, ReservedSchemaGaps.RefusalMessage(_definition, from, pending));
        }

        private SchemaPlan Refused(SchemaState state, int? version, string message)
            => new(_definition.Component, state, version, _definition.CurrentVersion, [], message);

        private IReadOnlyList<string>? Render(MigrationOperation operation)
            => _script
                ? [.. _db.GetService<IMigrationsSqlGenerator>().Generate([operation], _db.GetService<IDesignTimeModel>().Model).Select(c => c.CommandText)]
                : null;

        private string? UpdateStatement(int version)
        {
            if (!_script)
            {
                return null;
            }

            ISqlGenerationHelper sql = _db.GetService<ISqlGenerationHelper>();
            return $"UPDATE {sql.DelimitIdentifier(_definition.VersionTable)} SET {sql.DelimitIdentifier("Version")} = {version} " +
                   $"WHERE {sql.DelimitIdentifier("Id")} = 1;";
        }

        private IReadOnlyList<string> Render(EnsureRowChange seed)
        {
            ISqlGenerationHelper sql = _db.GetService<ISqlGenerationHelper>();
            IRelationalTypeMappingSource mappings = _db.GetService<IRelationalTypeMappingSource>();
            Dictionary<string, object?> values = new(StringComparer.Ordinal) { [seed.KeyColumn] = seed.KeyValue };
            foreach ((string column, object? value) in seed.Values)
                values.Add(column, value);
            string table = sql.DelimitIdentifier(seed.Table);
            string[] columns = [.. values.Keys];
            string list = string.Join(", ", columns.Select(sql.DelimitIdentifier));
            string Literal(object? value)
            {
                RelationalTypeMapping? mapping = mappings.FindMapping(value?.GetType() ?? typeof(string));
                return mapping?.GenerateSqlLiteral(value)
                    ?? throw new InvalidOperationException($"No relational type mapping exists for schema seed value type '{value?.GetType().FullName ?? "null"}'.");
            }

            string literals = string.Join(", ", values.Values.Select(Literal));
            string key = sql.DelimitIdentifier(seed.KeyColumn);
            string keyLiteral = Literal(seed.KeyValue);
            string from = _db.Database.ProviderName == "Pomelo.EntityFrameworkCore.MySql" ? " FROM DUAL" : ""; // MariaDB/MySQL need it before a WHERE
            return [$"INSERT INTO {table} ({list}) SELECT {literals}{from} WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE {key} = {keyLiteral});"];
        }

        private string? InsertStatement(int version)
        {
            if (!_script)
            {
                return null;
            }

            ISqlGenerationHelper sql = _db.GetService<ISqlGenerationHelper>();
            return $"INSERT INTO {sql.DelimitIdentifier(_definition.VersionTable)} ({sql.DelimitIdentifier("Id")}, {sql.DelimitIdentifier("Version")}) " +
                   $"VALUES (1, {version});";
        }
    }
}
