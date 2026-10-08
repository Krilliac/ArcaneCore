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

    private sealed class Session
    {
        private readonly DbContext _db;
        private readonly SchemaDefinition _definition;
        private readonly bool _script;
        private readonly bool _offline;
        private readonly bool _allowReservedGaps;
        private readonly SchemaChangeDecider.ModelOperations _ops;
        private readonly HashSet<string> _virtualTables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _virtualColumns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogIndex>> _indexes = new(StringComparer.Ordinal);

        public Session(DbContext db, SchemaDefinition definition, bool script, bool offline, bool allowReservedGaps)
        {
            _db = db;
            _definition = definition;
            _script = script;
            _offline = offline;
            _allowReservedGaps = allowReservedGaps;
            _ops = SchemaChangeDecider.ReadModel(db);
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
        /// the merged version and runs the data moves (counted, not run), then the ordinary steps after it.
        /// </summary>
        private async Task<SchemaPlan> PlanForeignLineAsync(ForeignLineMatch match, CancellationToken ct)
        {
            var actions = new List<PlannedAction>();
            foreach (SchemaStep step in match.Steps)
            {
                HashSet<(string Table, string Column)> addedLater = SchemaChangeDecider.AddedLater(_definition, step);
                foreach (SchemaChange change in step.Changes)
                {
                    await PlanChangeAsync(change, addedLater, actions, ct).ConfigureAwait(false);
                }
            }

            var moves = new List<string>();
            foreach (ForeignLineDataMove move in match.DataMoves)
            {
                long rows = await move.CountAsync(_db, ct).ConfigureAwait(false);
                moves.Add($"{move.Description} ({rows} rows)");
            }

            string description = $"migrate from the {match.Line.Name} numbering (schema version {match.ForeignVersion}): verify or apply steps " +
                                 $"{match.Line.DivergedAfter + 1}-{match.MergedVersion}" + (moves.Count == 0 ? string.Empty : "; " + string.Join("; ", moves));
            var first = new PlannedStep(match.MergedVersion, description, actions, UpdateStatement(match.MergedVersion));
            SchemaPlan plan = await PlanStepsAsync(SchemaState.Behind, match.MergedVersion, [first], ct).ConfigureAwait(false);
            return plan with { DatabaseVersion = match.ForeignVersion, ForeignLine = match };
        }

        private async Task<SchemaPlan> PlanStepsAsync(SchemaState state, int from, List<PlannedStep> prefix, CancellationToken ct)
        {
            if (RefuseReservedGaps(state, state == SchemaState.AdoptV1 ? null : from, from) is { } refused)
            {
                return refused;
            }

            var steps = new List<PlannedStep>(prefix);
            int version = from;
            foreach (SchemaStep step in _definition.Steps.Where(s => s.Version > from).OrderBy(s => s.Version))
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

            if (version != _definition.CurrentVersion)
            {
                return new SchemaPlan(
                    _definition.Component, SchemaState.Unknown, from, _definition.CurrentVersion, steps,
                    $"The {_definition.Component} schema is at version {version} but this build needs " +
                    $"{_definition.CurrentVersion} and has no upgrade path.");
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
                        || await SchemaCatalog.ColumnExistsAsync(_db, add.Table, add.Column, ct).ConfigureAwait(false))
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

                default:
                    throw new InvalidOperationException($"unsupported schema change {change}");
            }
        }

        private async Task CreateTableAsync(
            string table, IReadOnlySet<(string Table, string Column)> addedLater, List<PlannedAction> actions, CancellationToken ct)
        {
            CreateTableOperation op = _ops.Tables.GetValueOrDefault(table)
                ?? throw new InvalidOperationException($"table {table} is not in the model");
            if (await TableExistsAsync(table, ct).ConfigureAwait(false))
            {
                if (_virtualTables.Contains(table))
                {
                    actions.Add(new PlannedAction(ChangeObject.Table, table, null, ChangeDecision.Satisfied, $"table {table} is created by an earlier change"));
                }
                else
                {
                    IReadOnlyList<string> actual = await SchemaCatalog.ReadColumnsAsync(_db, table, ct).ConfigureAwait(false);
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
            bool tableIsNew = _virtualTables.Contains(index.Table);
            if (!_indexes.TryGetValue(index.Table, out List<CatalogIndex>? existing))
            {
                existing = [.. await SchemaCatalog.ReadIndexesAsync(_db, index.Table, ct).ConfigureAwait(false)];
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
                || (!_offline && await SchemaCatalog.TableExistsAsync(_db, table, ct).ConfigureAwait(false));

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
