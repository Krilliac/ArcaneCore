using System.Globalization;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema.Upgrade.Copy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Schema.Upgrade.Cli;

public static partial class DbUpgradeCli
{
    private sealed partial class Run
    {
        /// <summary>Where <c>copy</c> reads ARCANE_DB_TARGET_* from; the process environment unless a test supplies one.</summary>
        public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

        /// <summary>Test seams (<see cref="DbCopyHooks"/>); null in production.</summary>
        public DbCopyHooks? Hooks { get; init; }

        private sealed record CopyEnd(DatabaseConnectionOptions Resolved, Target Target);

        private sealed record CopyJob(Spec Spec, CopyEnd Source, CopyEnd Target, IReadOnlyList<CopyTable> Tables, IReadOnlyList<CopyTable> Ordered);

        /// <summary>
        /// <c>copy</c>: move each selected component's rows from the source database to the target database (see docs/ops/database-upgrade.md).
        /// Everything that can refuse does so first, for every component, before the first write: source at the current schema version with
        /// exactly the model's tables and columns, no other process owning a SQLite characters file, target reachable and empty (or --replace),
        /// target not the source. Then per component: create or upgrade the target schema, copy in one transaction (the component's tables
        /// emptied in reverse foreign-key order, filled in order), reset key counters, verify counts and content. Last, the same drift check
        /// as <c>check</c> on every target.
        /// <para>
        /// The schema version table is never copied: the target's own bootstrap wrote it and the copy requires it to equal the source's.
        /// Rows the bootstrap seeds (<see cref="EnsureRowChange"/>) are replaced by the source's rows, so the target ends equal to the source.
        /// </para>
        /// </summary>
        public async Task<int> CopyAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            bool replace = a.Flag("--replace");
            bool allowSessions = a.Flag("--allow-active-sessions");
            TimeSpan lockTimeout = a.LockTimeout();
            IReadOnlyList<string> names = a.Components();
            if (names.Count != 1 && (a.Value("--source-connection") is not null || a.Value("--target-connection") is not null))
            {
                throw new UsageException(
                    "--source-connection and --target-connection name one database, so they need --component auth, characters or world; " +
                    "for --component all use the configured Database section and ARCANE_DB_TARGET_<COMPONENT>_CONNECTION variables");
            }

            var ends = new List<(Spec Spec, CopyEnd Source, CopyEnd Target)>();
            foreach (string name in names)
            {
                Spec spec = s_specs.Single(s => s.Name == name);
                CopyEnd source = ResolveCopyEnd(spec, a, isSource: true);
                CopyEnd target = ResolveCopyEnd(spec, a, isSource: false);
                if (SameDatabase(source.Target, target.Target))
                {
                    throw new UsageException($"the {name} source and target are the same database ({source.Target.Description}); copy would empty its own source");
                }

                ends.Add((spec, source, target));
            }

            var locks = new List<CharacterDatabaseWriterLock>();
            try
            {
                // A world daemon that owns a SQLite characters file holds this lock for its life; so does a copy, to keep one from starting midway.
                foreach ((Spec spec, CopyEnd source, CopyEnd target) in ends.Where(e => e.Spec.Component == DatabaseComponent.Characters))
                {
                    foreach (CopyEnd end in new[] { source, target })
                    {
                        try
                        {
                            if (CharacterDatabaseWriterLock.TryAcquire(end.Resolved) is { } held)
                            {
                                locks.Add(held);
                            }
                        }
                        catch (CharacterDatabaseAlreadyOwnedException ex)
                        {
                            await _err.WriteLineAsync($"error: {spec.Name}: refused: a world process owns the characters database ({Scrub(ex.Message)}); stop it first. Nothing was changed.").ConfigureAwait(false);
                            return DbUpgradeExitCodes.Refused;
                        }
                    }
                }

                string[] known = KnownTables();
                var jobs = new List<CopyJob>();
                var refusals = new List<string>();
                foreach ((Spec spec, CopyEnd source, CopyEnd target) in ends)
                {
                    CopyJob? job = await PreflightCopyAsync(spec, source, target, known, replace, allowSessions, refusals, ct).ConfigureAwait(false);
                    if (job is not null)
                    {
                        jobs.Add(job);
                    }
                }

                if (refusals.Count > 0)
                {
                    foreach (string refusal in refusals)
                    {
                        await _err.WriteLineAsync($"error: refused: {Scrub(refusal)}").ConfigureAwait(false);
                    }

                    await _err.WriteLineAsync("nothing was changed").ConfigureAwait(false);
                    return DbUpgradeExitCodes.Refused;
                }

                await _out.WriteLineAsync($"arcane-db copy (ArcaneCore.Data {typeof(DbUpgradeCli).Assembly.GetName().Version})").ConfigureAwait(false);
                var committed = new List<string>();
                foreach (CopyJob job in jobs)
                {
                    int code;
                    try
                    {
                        code = await RunCopyJobAsync(job, replace, allowSessions, lockTimeout, ct).ConfigureAwait(false);
                    }
                    catch (ComponentFailure failure) when (committed.Count > 0)
                    {
                        throw new ComponentFailure(failure.ExitCode, failure.Message + System.Environment.NewLine + CommittedNote(committed));
                    }

                    if (code != DbUpgradeExitCodes.Ok)
                    {
                        if (committed.Count > 0)
                        {
                            await _err.WriteLineAsync(CommittedNote(committed)).ConfigureAwait(false);
                        }

                        return code;
                    }

                    committed.Add(job.Spec.Name);
                }

                // The proof the target is a database the daemons accept.
                bool clean = true;
                foreach (CopyJob job in jobs)
                {
                    DriftReport report = await DriftOfAsync(job.Target.Target, known, ct).ConfigureAwait(false);
                    clean &= report.IsClean;
                    PlanFormatter.WriteText(_out, report);
                }

                if (!clean)
                {
                    await _err.WriteLineAsync("error: the target differs from the model after the copy; see the findings above").ConfigureAwait(false);
                    return DbUpgradeExitCodes.Drift;
                }

                await _out.WriteLineAsync("copy complete: every table matched (row counts and every value compared through the model column types). Switch Database:<Component>:Provider and ConnectionString to the target, then start the daemons.").ConfigureAwait(false);
                return DbUpgradeExitCodes.Ok;
            }
            finally
            {
                foreach (CharacterDatabaseWriterLock held in locks)
                {
                    held.Dispose();
                }
            }
        }

        /// <summary>Components commit one by one: after a later one fails the earlier ones stay in the target, and a re-run needs <c>--replace</c>.</summary>
        private static string CommittedNote(IReadOnlyList<string> committed)
            => $"error: already committed before this failure: {string.Join(", ", committed)}. Their rows stay in the target, so the target is no longer empty; run the copy again with --replace.";

        /// <summary>Refuses an inline password in a connection string given on the command line (it shows in the process list and shell history).</summary>
        private static void RefuseInlinePassword(string role, string componentName, string connectionString, DatabaseProvider provider)
        {
            string? password;
            try
            {
                password = provider switch
                {
                    DatabaseProvider.Sqlite => new SqliteConnectionStringBuilder(connectionString).Password,
                    DatabaseProvider.MariaDb or DatabaseProvider.MySql => new MySqlConnectionStringBuilder(connectionString).Password,
                    DatabaseProvider.PostgreSql => new NpgsqlConnectionStringBuilder(connectionString).Password,
                    _ => null,
                };
            }
            catch (ArgumentException)
            {
                return; // not parseable: Prepare reports it, without echoing the string
            }

            if (!string.IsNullOrEmpty(password))
            {
                string where = role == "target"
                    ? $"set ARCANE_DB_TARGET_{componentName.ToUpperInvariant()}_CONNECTION (or ARCANE_DB_TARGET_CONNECTION) in the environment"
                    : $"leave out --source-connection and put the connection string in the configured Database section ({componentName} component), which the daemons read too";
                throw new UsageException($"--{role}-connection contains a password; a command-line argument shows in the process list and shell history. Instead {where}.");
            }
        }

        private CopyEnd ResolveCopyEnd(Spec spec, DbUpgradeArguments a, bool isSource)
        {
            string role = isSource ? "source" : "target";
            string? connectionString = a.Value($"--{role}-connection");
            bool fromArgument = connectionString is not null;
            string? providerText = a.Value($"--{role}-provider");
            if (!isSource)
            {
                providerText ??= Environment("ARCANE_DB_TARGET_PROVIDER");
                connectionString ??= Environment($"ARCANE_DB_TARGET_{spec.Name.ToUpperInvariant()}_CONNECTION") ?? Environment("ARCANE_DB_TARGET_CONNECTION");
            }

            DatabaseConnectionOptions resolved;
            if (isSource && connectionString is null)
            {
                resolved = _database.Resolve(spec.Component);
                if (string.IsNullOrWhiteSpace(resolved.ConnectionString))
                {
                    throw new UsageException(
                        $"no connection string for the {spec.Name} source: set Database:{char.ToUpperInvariant(spec.Name[0])}{spec.Name[1..]}:ConnectionString " +
                        "(or Database:ConnectionString for a single database), or pass --source-provider and --source-connection");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new UsageException(
                        $"no {role} connection string for the {spec.Name} database: pass --{role}-connection " +
                        (isSource ? string.Empty : $"or set ARCANE_DB_TARGET_{spec.Name.ToUpperInvariant()}_CONNECTION "));
                }

                if (string.IsNullOrWhiteSpace(providerText))
                {
                    throw new UsageException($"no {role} provider: pass --{role}-provider sqlite, mariadb, mysql or postgresql" + (isSource ? string.Empty : " (or set ARCANE_DB_TARGET_PROVIDER)"));
                }

                DatabaseProvider provider = ParseProvider(providerText);
                if (fromArgument)
                {
                    RefuseInlinePassword(role, spec.Name, connectionString, provider);
                }

                resolved = new DatabaseConnectionOptions { Provider = provider, ConnectionString = connectionString };
                _secrets = [.. _secrets, connectionString];
            }

            DatabaseConnectionOptions prepared = Prepare(resolved, readOnly: isSource, $"{spec.Name} {role}");
            Spec named = spec with { Name = $"{spec.Name} {role}" };
            return new CopyEnd(resolved, new Target(named, prepared, Describe(prepared)));
        }

        private static DatabaseProvider ParseProvider(string text) => text.Trim().ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "mariadb" => DatabaseProvider.MariaDb,
            "mysql" => DatabaseProvider.MySql,
            "postgresql" or "postgres" or "pgsql" => DatabaseProvider.PostgreSql,
            _ => throw new UsageException($"unknown provider '{text}': use sqlite, mariadb, mysql or postgresql"),
        };

        private static bool SameDatabase(Target left, Target right)
        {
            static bool Server(DatabaseProvider p) => p is DatabaseProvider.MariaDb or DatabaseProvider.MySql;
            if (left.Connection.Provider != right.Connection.Provider && !(Server(left.Connection.Provider) && Server(right.Connection.Provider)))
            {
                return false;
            }

            BackupAdvisor.BackupInstructions a = BackupAdvisor.Describe(left.Connection);
            BackupAdvisor.BackupInstructions b = BackupAdvisor.Describe(right.Connection);
            if (left.Connection.Provider == DatabaseProvider.Sqlite)
            {
                return string.Equals(Path.GetFullPath(a.Database), Path.GetFullPath(b.Database), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }

            int defaultPort = left.Connection.Provider == DatabaseProvider.PostgreSql ? 5432 : 3306;
            return string.Equals(LoopbackName(a.Host), LoopbackName(b.Host), StringComparison.OrdinalIgnoreCase)
                && (a.Port ?? defaultPort) == (b.Port ?? defaultPort)
                && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One name for every spelling of this machine (localhost, 127.0.0.1, ::1, the dot), so the same database through two names is still caught.</summary>
        private static string LoopbackName(string? host)
        {
            string name = (host ?? string.Empty).Trim().Trim('[', ']');
            return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "::1" or "." or "0.0.0.0" || name.StartsWith("127.", StringComparison.Ordinal)
                ? "localhost"
                : name;
        }

        /// <summary>Read-only checks of one component; a problem is added to <paramref name="refusals"/> and the job is not returned.</summary>
        private async Task<CopyJob?> PreflightCopyAsync(
            Spec spec, CopyEnd source, CopyEnd target, string[] known, bool replace, bool allowSessions, List<string> refusals, CancellationToken ct)
        {
            int before = refusals.Count;
            IReadOnlyList<CopyTable> tables = [];
            IReadOnlyList<CopyTable> ordered = [];
            await GuardAsync(source.Target, ct, async sdb =>
            {
                SchemaPlan plan = await SchemaPlanner.PlanAsync(sdb, spec.Schema, includeScript: false, ct).ConfigureAwait(false);
                if (plan.State != SchemaState.Current)
                {
                    refusals.Add($"{spec.Name}: the source ({source.Target.Description}) is not at the current schema version: {PlanFormatter.Summary(plan).Trim()}. " +
                        "Run 'arcane-db upgrade' on it first.");
                    return 0;
                }

                tables = DatabaseCopy.TablesOf(sdb, spec.Schema);
                try
                {
                    ordered = CopyTableOrder.Sort(tables);
                }
                catch (CopyCycleException ex)
                {
                    refusals.Add($"{spec.Name}: {ex.Message}");
                }

                foreach (string problem in await DatabaseCopy.StructureProblemsAsync(sdb, tables, new HashSet<string>(known, StringComparer.OrdinalIgnoreCase), "source", ct).ConfigureAwait(false))
                {
                    refusals.Add($"{spec.Name}: {problem}");
                }

                if (!allowSessions && source.Target.Connection.Provider != DatabaseProvider.Sqlite
                    && await ServerProbe.CountOtherSessionsAsync(sdb, ct).ConfigureAwait(false) is int sessions and > 0)
                {
                    refusals.Add($"{spec.Name}: {sessions} other session(s) are connected to the source database; stop every ArcaneCore daemon first (or pass --allow-active-sessions)");
                }

                return 0;
            }).ConfigureAwait(false);

            await GuardAsync(target.Target, ct, async tdb =>
            {
                SchemaPlan plan = await SchemaPlanner.PlanAsync(tdb, spec.Schema, includeScript: false, ct).ConfigureAwait(false);
                if (plan.IsRefused)
                {
                    refusals.Add($"{spec.Name}: the target ({target.Target.Description}) cannot be brought to the current schema: {plan.FirstRefusal}");
                    return 0;
                }

                if (plan.State == SchemaState.Missing)
                {
                    return 0;
                }

                if (!allowSessions && target.Target.Connection.Provider != DatabaseProvider.Sqlite
                    && await ServerProbe.CountOtherSessionsAsync(tdb, ct).ConfigureAwait(false) is int sessions and > 0)
                {
                    refusals.Add($"{spec.Name}: {sessions} other session(s) are connected to the target database; stop every ArcaneCore daemon first (or pass --allow-active-sessions)");
                }

                if (!replace && tables.Count > 0)
                {
                    IReadOnlyList<(string Table, long Rows)> rows = await DatabaseCopy.DataRowsAsync(tdb, tables, spec.Schema, ct).ConfigureAwait(false);
                    if (rows.Count > 0)
                    {
                        refusals.Add($"{spec.Name}: the target ({target.Target.Description}) is not empty: " +
                            string.Join(", ", rows.Take(5).Select(r => $"{r.Table} has {r.Rows} row(s)")) + (rows.Count > 5 ? $" and {rows.Count - 5} more table(s)" : string.Empty) +
                            ". Pass --replace to empty this component's tables first, or use an empty database.");
                    }
                }

                return 0;
            }).ConfigureAwait(false);

            return refusals.Count == before ? new CopyJob(spec, source, target, tables, ordered) : null;
        }

        private async Task<int> RunCopyJobAsync(CopyJob job, bool replace, bool allowSessions, TimeSpan lockTimeout, CancellationToken ct)
        {
            string name = job.Spec.Name;
            await _out.WriteLineAsync($"{name}: {ProviderName(job.Source.Target.Connection)} {job.Source.Target.Description} -> {ProviderName(job.Target.Target.Connection)} {job.Target.Target.Description}" +
                (replace ? " (--replace)" : string.Empty)).ConfigureAwait(false);

            return await GuardAsync(job.Source.Target, ct, sdb => GuardAsync(job.Target.Target, ct, async tdb =>
            {
                SchemaPlan plan = await SchemaPlanner.PlanAsync(tdb, job.Spec.Schema, includeScript: false, ct).ConfigureAwait(false);
                if (plan.NeedsApply)
                {
                    await _out.WriteLineAsync($"  creating or upgrading the target schema: {PlanFormatter.Summary(plan).Trim()}").ConfigureAwait(false);
                    var options = new SchemaUpgradeOptions { LockTimeout = lockTimeout, RefuseActiveSessions = !allowSessions, Progress = new LineProgress(_out) };
                    await SchemaUpgrader.ApplyAsync(tdb, job.Spec.Schema, options, logger: null, ct).ConfigureAwait(false);
                }

                // The target schema now exists: its tables and columns must be the source's.
                var differences = new List<string>();
                int? sourceVersion = await ReadVersionAsync(sdb, job.Spec.Schema, ct).ConfigureAwait(false);
                int? targetVersion = await ReadVersionAsync(tdb, job.Spec.Schema, ct).ConfigureAwait(false);
                if (sourceVersion != targetVersion || targetVersion != job.Spec.Schema.CurrentVersion)
                {
                    differences.Add($"schema version differs: source {sourceVersion}, target {targetVersion}, this build {job.Spec.Schema.CurrentVersion}");
                }

                Dictionary<string, HashSet<string>> sourceColumns = await DatabaseCopy.ReadColumnsAsync(sdb, job.Tables.Select(t => t.Name), ct).ConfigureAwait(false);
                Dictionary<string, HashSet<string>> targetColumns = await DatabaseCopy.ReadColumnsAsync(tdb, job.Tables.Select(t => t.Name), ct).ConfigureAwait(false);
                foreach (CopyTable table in job.Tables)
                {
                    if (!targetColumns.TryGetValue(table.Name, out HashSet<string>? columns))
                    {
                        differences.Add($"the target lacks the table '{table.Name}'");
                    }
                    else if (sourceColumns.TryGetValue(table.Name, out HashSet<string>? have))
                    {
                        differences.AddRange(DatabaseCopy.ColumnDifferences(table.Name, have, "the source", columns, "the target"));
                    }
                }

                if (differences.Count > 0)
                {
                    foreach (string difference in differences)
                    {
                        await _err.WriteLineAsync($"error: {name}: refused: {Scrub(difference)}").ConfigureAwait(false);
                    }

                    await _err.WriteLineAsync($"no {name} rows were copied").ConfigureAwait(false);
                    return DbUpgradeExitCodes.Refused;
                }

                // The sessions were counted at preflight; the schema step above took time, so count again right before the first write.
                if (Hooks?.BeforeCopy is { } before)
                {
                    await before(job.Spec.Name, ct).ConfigureAwait(false);
                }

                if (!allowSessions)
                {
                    foreach ((DbContext db, CopyEnd end, string role) in new[] { (sdb, job.Source, "source"), (tdb, job.Target, "target") })
                    {
                        if (end.Target.Connection.Provider != DatabaseProvider.Sqlite
                            && await ServerProbe.CountOtherSessionsAsync(db, ct).ConfigureAwait(false) is int sessions and > 0)
                        {
                            await _err.WriteLineAsync($"error: {name}: refused: {sessions} other session(s) connected to the {role} database ({end.Target.Description}) since the preflight; stop every ArcaneCore daemon first (or pass --allow-active-sessions). No {name} rows were copied.").ConfigureAwait(false);
                            return DbUpgradeExitCodes.Refused;
                        }
                    }
                }

                CopyOutcome outcome;
                try
                {
                    outcome = await DatabaseCopy.CopyAsync(sdb, tdb, job.Ordered, Hooks, ct).ConfigureAwait(false);
                }
                catch (CopyDataException ex)
                {
                    throw new ComponentFailure(DbUpgradeExitCodes.Failure, $"{name}: the copy failed and was rolled back (the target holds none of this component's rows): {Scrub(ex.Message)}");
                }
                catch (System.Data.Common.DbException ex)
                {
                    // Inside the copy's transaction the connection was open: this is a failed statement, not an unreachable server.
                    throw new ComponentFailure(DbUpgradeExitCodes.Failure, $"{name}: the copy failed and was rolled back (the target holds none of this component's rows): {Scrub(ex.Message)}");
                }

                IReadOnlyList<CopyTableResult> results = outcome.Results;
                await _out.WriteLineAsync($"  {"table",-44} {"source",12} {"target",12}  content").ConfigureAwait(false);
                foreach (CopyTableResult result in results)
                {
                    await _out.WriteLineAsync($"  {result.Table,-44} {result.SourceRows.ToString(CultureInfo.InvariantCulture),12} {result.TargetRows.ToString(CultureInfo.InvariantCulture),12}  " +
                        (result.IsOk ? "ok" : result.SourceRows != result.TargetRows ? "ROW COUNT DIFFERS" : "CONTENT DIFFERS")).ConfigureAwait(false);
                }

                if (!outcome.Committed)
                {
                    await _err.WriteLineAsync($"error: {name}: verification failed for {results.Count(r => !r.IsOk)} table(s); the copy was rolled back and the target is as it was (PostgreSQL key counters are only moved after every table verified, immediately before the commit, so they are unchanged too). " +
                        "Fix the cause and run the copy again.").ConfigureAwait(false);
                    return DbUpgradeExitCodes.CopyVerifyFailed;
                }

                if (outcome.PostCommitProblem is { } problem)
                {
                    await _err.WriteLineAsync($"error: {name}: {Scrub(problem)}. Run the copy again with --replace.").ConfigureAwait(false);
                    return DbUpgradeExitCodes.Failure;
                }

                await _out.WriteLineAsync($"  {name}: {results.Count} table(s), {results.Sum(r => r.TargetRows)} row(s) copied; row counts and every value, read back inside the transaction through the model's column types, matched the source; {outcome.CountersSet} key counter(s) reset").ConfigureAwait(false);
                return DbUpgradeExitCodes.Ok;
            })).ConfigureAwait(false);
        }

        private static async Task<int?> ReadVersionAsync(DbContext db, SchemaDefinition schema, CancellationToken ct)
        {
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = $"SELECT {sql.DelimitIdentifier("Version")} FROM {sql.DelimitIdentifier(schema.VersionTable)} WHERE {sql.DelimitIdentifier("Id")} = 1";
                object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return value is null or DBNull ? null : System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
