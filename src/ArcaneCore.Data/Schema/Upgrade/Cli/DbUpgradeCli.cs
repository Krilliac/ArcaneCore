using System.Data.Common;
using System.Net.Sockets;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Schema.Upgrade.Cli;

/// <summary>
/// <c>arcane-db</c>: the operator's path for upgrading a production database between ArcaneCore versions.
/// <c>status</c> reports versions, <c>plan</c> is a read-only dry run (optionally the exact SQL), <c>check</c>
/// compares the database with the model, <c>upgrade</c> applies after a backup acknowledgement and re-checks, and
/// <c>backup-info</c> prints how to back up. The logic lives in the data library so the tests drive it in-process;
/// the tool project is a one-line host. Exit codes: <see cref="DbUpgradeExitCodes"/>.
/// <para>
/// Connections are opened without pooling (so the session count only sees other processes), SQLite is opened
/// read-only for everything except <c>upgrade</c>, and every line printed goes through
/// <see cref="ConnectionStringRedactor"/>: a password never reaches the output.
/// </para>
/// </summary>
public static class DbUpgradeCli
{
    public const string Usage = """
        usage: arcane-db <command> [options]

        commands:
          status        report, per component, the database version, the version this build needs and the pending steps
          plan          read-only dry run: what upgrade would create, change or refuse (--script prints the exact SQL)
          check         compare the database with the model (tables, columns, indexes, version row, engine settings)
          upgrade       back up (acknowledge it), apply every pending step in the order auth, characters, world, then check
          migrate-codex find databases the Codex line created (before the 2026-10-07 merge renumbered its schema steps) and
                        report their one-shot migration to this build's numbering; --apply runs only that migration
          backup-info   print how to back each database up

        options:
          --component auth|characters|world|all   which component(s) to act on (default all)
          --script                                plan: print the SQL (every non-SQL line is a -- comment)
          --json                                  status, plan, check: machine-readable output
          --no-fail-on-pending                    status, plan: exit 0 when an upgrade is pending (for set -e scripts)
          --apply                                 migrate-codex: migrate (without it the command is a read-only dry run)
          --confirm-backup                        upgrade, migrate-codex: you have a backup of every database with pending steps
          --backup-dir <directory>                upgrade, migrate-codex: also write a verified copy of each SQLite database there
          --allow-active-sessions                 upgrade, migrate-codex: do not refuse when other sessions are connected
          --lock-timeout <seconds>                upgrade, migrate-codex: how long to wait for another process's schema lock (default 60)

        configuration: the Database section (appsettings.json, environment variables such as
        Database__Characters__ConnectionString); a host option --config <file> names another JSON file.
        Characters is per realm: run once per realm configuration. Stop every daemon first; rolling upgrades are unsupported.

        exit codes: 0 ok, 1 unexpected failure, 2 usage or configuration, 3 upgrade pending (status, plan),
        4 refused (newer database, unknown state, blocker, active sessions), 5 drift, 6 database unreachable,
        7 schema lock timeout, 8 backup not confirmed
        """;

    /// <summary>The commands the tool accepts (the closed set the argument parser enforces; the runbook must name each).</summary>
    public static IReadOnlyList<string> Commands => DbUpgradeArguments.Commands;

    /// <summary>The options the tool accepts (the closed set the argument parser enforces; the runbook must name each).</summary>
    public static IReadOnlyList<string> Options => [.. DbUpgradeArguments.ValueOptions, .. DbUpgradeArguments.Flags];

    /// <summary>Run one command line. Never throws for expected failures; returns the exit code.</summary>
    public static async Task<int> RunAsync(
        string[] args, DatabaseOptions database, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0)
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return DbUpgradeExitCodes.Usage;
        }

        if (args.Any(a => a is "-h" or "--help" or "help"))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return DbUpgradeExitCodes.Ok;
        }

        var run = new Run(database, output, error);
        try
        {
            DbUpgradeArguments arguments = DbUpgradeArguments.Parse(args);
            return arguments.Command switch
            {
                "status" => await run.StatusAsync(arguments, cancellationToken).ConfigureAwait(false),
                "plan" => await run.PlanAsync(arguments, cancellationToken).ConfigureAwait(false),
                "check" => await run.CheckAsync(arguments, cancellationToken).ConfigureAwait(false),
                "upgrade" => await run.UpgradeAsync(arguments, cancellationToken).ConfigureAwait(false),
                "migrate-codex" => await run.MigrateCodexAsync(arguments, cancellationToken).ConfigureAwait(false),
                "backup-info" => run.BackupInfo(arguments),
                _ => throw new UsageException($"unknown command '{arguments.Command}'"),
            };
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync($"error: {run.Scrub(ex.Message)}").ConfigureAwait(false);
            await error.WriteLineAsync("run with --help for usage").ConfigureAwait(false);
            return DbUpgradeExitCodes.Usage;
        }
        catch (ComponentFailure failure)
        {
            await error.WriteLineAsync($"error: {failure.Message}").ConfigureAwait(false);
            return failure.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("error: cancelled; the database was left as it was at the last completed step").ConfigureAwait(false);
            return DbUpgradeExitCodes.Failure;
        }
    }

    /// <summary>A failure already phrased for the operator with the exit code it maps to.</summary>
    private sealed class ComponentFailure(int exitCode, string message) : Exception(message)
    {
        public int ExitCode { get; } = exitCode;
    }

    private sealed record Spec(string Name, SchemaDefinition Schema, Func<DatabaseConnectionOptions, DbContext> Create, DatabaseComponent Component);

    private static T Make<T>(DatabaseConnectionOptions connection, Func<DbContextOptions<T>, T> create) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, connection);
        return create(builder.Options);
    }

    private sealed record Target(Spec Spec, DatabaseConnectionOptions Connection, string Description);

    private sealed class Run
    {
        private static readonly Spec[] s_specs =
        [
            new("auth", AuthDbContext.Schema, c => Make<AuthDbContext>(c, o => new AuthDbContext(o)), DatabaseComponent.Auth),
            new("characters", CharacterDbContext.Schema, c => Make<CharacterDbContext>(c, o => new CharacterDbContext(o)), DatabaseComponent.Characters),
            new("world", WorldDbContext.Schema, c => Make<WorldDbContext>(c, o => new WorldDbContext(o)), DatabaseComponent.World),
        ];

        private readonly DatabaseOptions _database;
        private readonly TextWriter _out;
        private readonly TextWriter _err;
        private readonly string[] _secrets;

        public Run(DatabaseOptions database, TextWriter output, TextWriter error)
        {
            _database = database;
            _out = output;
            _err = error;
            _secrets = [.. new[] { database.ConnectionString, database.Auth?.ConnectionString, database.Characters?.ConnectionString, database.World?.ConnectionString }
                .Where(s => !string.IsNullOrEmpty(s))!];
        }

        /// <summary>Remove every configured password from free text (a driver's error message, a path).</summary>
        public string Scrub(string text)
        {
            foreach (string connectionString in _secrets)
            {
                text = ConnectionStringRedactor.Scrub(text, connectionString);
            }

            return ConnectionStringRedactor.Scrub(text, string.Empty);
        }

        // --- commands -----------------------------------------------------------------------

        public async Task<int> StatusAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            bool json = a.Flag("--json");
            var reports = new List<ComponentJson>();
            int code = DbUpgradeExitCodes.Ok;
            Target[] targets = [.. Targets(a, readOnly: true)]; // validates the options and the configuration before anything is printed
            if (!json)
            {
                await _out.WriteLineAsync($"arcane-db status (ArcaneCore.Data {typeof(DbUpgradeCli).Assembly.GetName().Version})").ConfigureAwait(false);
            }

            foreach (Target target in targets)
            {
                (SchemaPlan plan, ServerInfo? server, int? sessions, bool? lockHeld) = await GuardAsync(target, ct, async db =>
                {
                    SchemaPlan p = await SchemaPlanner.PlanAsync(db, target.Spec.Schema, includeScript: false, ct).ConfigureAwait(false);
                    ServerInfo? info = null;
                    int? others = null;
                    bool? held = null;
                    if (p.State != SchemaState.Missing)
                    {
                        info = await ServerProbe.ReadServerInfoAsync(db, ct).ConfigureAwait(false);
                        (others, held) = await BestEffortAsync(db, target.Spec.Name, ct).ConfigureAwait(false);
                    }

                    return (p, info, others, held);
                }).ConfigureAwait(false);

                reports.Add(PlanFormatter.ToJson(plan, ProviderName(target.Connection), target.Description, server, sessions, lockHeld));
                if (!json)
                {
                    await _out.WriteLineAsync(PlanFormatter.Summary(plan)).ConfigureAwait(false);
                    await _out.WriteLineAsync($"           {ProviderName(target.Connection)} {target.Description}" +
                        (server is null ? string.Empty : $", server {server.Product} {server.Version}") +
                        (sessions is null ? string.Empty : $", {sessions} other session(s)") +
                        (lockHeld == true ? ", schema lock HELD by another session" : string.Empty)).ConfigureAwait(false);
                    if (server?.Warning is not null)
                    {
                        await _out.WriteLineAsync($"           warning: {server.Warning}").ConfigureAwait(false);
                    }

                    if (plan.FirstRefusal is not null)
                    {
                        await _out.WriteLineAsync($"           refused: {plan.FirstRefusal}").ConfigureAwait(false);
                    }
                }

                code = Worse(code, ExitFor(plan, a));
            }

            if (json)
            {
                await _out.WriteLineAsync(PlanFormatter.ToJson(new { components = reports })).ConfigureAwait(false);
            }
            else if (code == DbUpgradeExitCodes.UpgradePending)
            {
                await _out.WriteLineAsync("an upgrade is pending: run 'arcane-db plan', back up, then 'arcane-db upgrade'").ConfigureAwait(false);
            }

            return code;
        }

        public async Task<int> PlanAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            bool script = a.Flag("--script");
            bool json = a.Flag("--json");
            if (script && json)
            {
                throw new UsageException("--script and --json cannot be combined");
            }

            var reports = new List<ComponentJson>();
            int code = DbUpgradeExitCodes.Ok;
            foreach (Target target in Targets(a, readOnly: true))
            {
                SchemaPlan plan = await GuardAsync(target, ct,
                    db => SchemaPlanner.PlanAsync(db, target.Spec.Schema, includeScript: script, ct)).ConfigureAwait(false);
                if (script)
                {
                    PlanFormatter.WriteScript(_out, plan);
                }
                else if (json)
                {
                    reports.Add(PlanFormatter.ToJson(plan, ProviderName(target.Connection), target.Description, null, null, null));
                }
                else
                {
                    PlanFormatter.WriteText(_out, plan);
                }

                code = Worse(code, ExitFor(plan, a));
            }

            if (json)
            {
                await _out.WriteLineAsync(PlanFormatter.ToJson(new { components = reports })).ConfigureAwait(false);
            }

            return code;
        }

        public async Task<int> CheckAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            bool json = a.Flag("--json");
            string[] known = KnownTables();
            var reports = new List<DriftJson>();
            bool clean = true;
            foreach (Target target in Targets(a, readOnly: true))
            {
                DriftReport report = await DriftOfAsync(target, known, ct).ConfigureAwait(false);
                clean &= report.IsClean;
                if (json)
                {
                    reports.Add(PlanFormatter.ToJson(report));
                }
                else
                {
                    PlanFormatter.WriteText(_out, report);
                }
            }

            if (json)
            {
                await _out.WriteLineAsync(PlanFormatter.ToJson(new { components = reports })).ConfigureAwait(false);
            }

            return clean ? DbUpgradeExitCodes.Ok : DbUpgradeExitCodes.Drift;
        }

        public async Task<int> UpgradeAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            TimeSpan lockTimeout = a.LockTimeout();
            string? backupDir = a.Value("--backup-dir");
            Target[] targets = [.. Targets(a, readOnly: false)];
            // 1. Plan everything read-only first: nothing is applied if any component would be refused.
            var plans = new List<(Target Target, SchemaPlan Plan)>();
            foreach (Target target in targets)
            {
                SchemaPlan plan = await GuardAsync(target, ct, db => SchemaPlanner.PlanAsync(db, target.Spec.Schema, includeScript: false, ct)).ConfigureAwait(false);
                plans.Add((target, plan));
            }

            foreach ((Target target, SchemaPlan plan) in plans.Where(p => p.Plan.IsRefused))
            {
                await _err.WriteLineAsync($"error: {target.Spec.Name}: refused: {Scrub(plan.FirstRefusal!)}").ConfigureAwait(false);
            }

            if (plans.Any(p => p.Plan.IsRefused))
            {
                await _err.WriteLineAsync("nothing was changed").ConfigureAwait(false);
                return DbUpgradeExitCodes.Refused;
            }

            var pending = plans.Where(p => p.Plan.NeedsApply).ToList();
            if (pending.Count == 0)
            {
                await _out.WriteLineAsync("nothing to do: every selected component is at the version this build needs").ConfigureAwait(false);
            }
            else
            {
                int gate = await BackupGateAsync(pending, a, backupDir, ct).ConfigureAwait(false);
                if (gate != DbUpgradeExitCodes.Ok)
                {
                    return gate;
                }

                // 2. Apply in order; the first failure stops the rest and names the component.
                foreach ((Target target, SchemaPlan plan) in pending)
                {
                    await _out.WriteLineAsync($"upgrading {target.Spec.Name}: {PlanFormatter.Summary(plan)}").ConfigureAwait(false);
                    var options = new SchemaUpgradeOptions
                    {
                        LockTimeout = lockTimeout,
                        RefuseActiveSessions = !a.Flag("--allow-active-sessions"),
                        Progress = new LineProgress(_out),
                    };
                    await GuardAsync(target, ct, async db =>
                    {
                        await SchemaUpgrader.ApplyAsync(db, target.Spec.Schema, options, logger: null, ct).ConfigureAwait(false);
                        return 0;
                    }).ConfigureAwait(false);
                    await _out.WriteLineAsync($"{target.Spec.Name}: now at schema version {target.Spec.Schema.CurrentVersion}").ConfigureAwait(false);
                }
            }

            // 3. The check that proves the result.
            string[] known = KnownTables();
            bool clean = true;
            foreach (Target target in targets)
            {
                DriftReport report = await DriftOfAsync(target, known, ct).ConfigureAwait(false);
                clean &= report.IsClean;
                PlanFormatter.WriteText(_out, report);
            }

            if (!clean)
            {
                await _err.WriteLineAsync("error: the database differs from the model after the upgrade; see the findings above").ConfigureAwait(false);
                return DbUpgradeExitCodes.Drift;
            }

            return DbUpgradeExitCodes.Ok;
        }

        /// <summary>
        /// The one-shot migration of Codex-line databases (<see cref="CodexLine"/>): a read-only report by default; with
        /// --apply, after the backup gate, only the migration to this build's numbering (the steps after it are the
        /// ordinary 'upgrade', which a daemon start with the Always policy also runs). A database that matches neither
        /// line, or any other refusal in a selected component's plan, stops everything before anything is written.
        /// </summary>
        public async Task<int> MigrateCodexAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            bool apply = a.Flag("--apply");
            TimeSpan lockTimeout = a.LockTimeout();
            string? backupDir = a.Value("--backup-dir");
            Target[] targets = [.. Targets(a, readOnly: !apply)];
            var plans = new List<(Target Target, SchemaPlan Plan)>();
            foreach (Target target in targets)
            {
                SchemaPlan plan = await GuardAsync(target, ct, db => SchemaPlanner.PlanAsync(db, target.Spec.Schema, includeScript: false, ct)).ConfigureAwait(false);
                plans.Add((target, plan));
                if (plan.ForeignLine is { } match)
                {
                    await _out.WriteLineAsync($"{target.Spec.Name}: Codex-line database: {match.Describe(target.Spec.Name)}").ConfigureAwait(false);
                    foreach (string evidence in match.Evidence)
                    {
                        await _out.WriteLineAsync($"  found {evidence}").ConfigureAwait(false);
                    }

                    PlanFormatter.WriteText(_out, plan);
                }
                else if (!plan.IsRefused)
                {
                    await _out.WriteLineAsync($"{target.Spec.Name}: not a Codex-line database ({PlanFormatter.Summary(plan).Trim()})").ConfigureAwait(false);
                }
            }

            foreach ((Target target, SchemaPlan plan) in plans.Where(p => p.Plan.IsRefused))
            {
                await _err.WriteLineAsync($"error: {target.Spec.Name}: refused: {Scrub(plan.FirstRefusal!)}").ConfigureAwait(false);
            }

            if (plans.Any(p => p.Plan.IsRefused))
            {
                await _err.WriteLineAsync("nothing was changed").ConfigureAwait(false);
                return DbUpgradeExitCodes.Refused;
            }

            var pending = plans.Where(p => p.Plan.ForeignLine is not null).ToList();
            if (pending.Count == 0)
            {
                await _out.WriteLineAsync("nothing to migrate: no selected database was created by the Codex line").ConfigureAwait(false);
                return DbUpgradeExitCodes.Ok;
            }

            if (!apply)
            {
                await _out.WriteLineAsync("dry run, nothing was changed: back up, then run 'arcane-db migrate-codex --apply' " +
                    "(or 'arcane-db upgrade', or start the server with Database:Upgrade:Policy Always: each migrates first)").ConfigureAwait(false);
                return DbUpgradeExitCodes.UpgradePending;
            }

            int gate = await BackupGateAsync(pending, a, backupDir, ct).ConfigureAwait(false);
            if (gate != DbUpgradeExitCodes.Ok)
            {
                return gate;
            }

            foreach ((Target target, SchemaPlan _) in pending)
            {
                var options = new SchemaUpgradeOptions { LockTimeout = lockTimeout, Progress = new LineProgress(_out) };
                ForeignLineMatch? migrated = await GuardAsync(target, ct, async db =>
                {
                    if (!a.Flag("--allow-active-sessions") && await ServerProbe.CountOtherSessionsAsync(db, ct).ConfigureAwait(false) is int others and > 0)
                    {
                        throw new SchemaActiveSessionsException(
                            $"{others} other session(s) are connected to the {target.Spec.Name} database. Stop every ArcaneCore daemon and tool first, " +
                            "or pass --allow-active-sessions if you know they are idle.", others);
                    }

                    return await SchemaBootstrapper.MigrateForeignLineAsync(db, target.Spec.Schema, options, logger: null, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
                await _out.WriteLineAsync(migrated is null
                    ? $"{target.Spec.Name}: nothing migrated (another process migrated it after the dry run)"
                    : $"{target.Spec.Name}: migrated from {migrated.Line.Name} schema version {migrated.ForeignVersion} to schema version {migrated.MergedVersion}; " +
                      $"'arcane-db upgrade' (or a server start) applies the steps after it").ConfigureAwait(false);
            }

            return DbUpgradeExitCodes.Ok;
        }

        public int BackupInfo(DbUpgradeArguments a)
        {
            foreach (IGrouping<string, Target> db in Targets(a, readOnly: true).GroupBy(t => t.Connection.ConnectionString, StringComparer.Ordinal))
            {
                BackupAdvisor.BackupInstructions info = BackupAdvisor.Describe(db.First().Connection);
                _out.WriteLine($"{string.Join(", ", db.Select(t => t.Spec.Name))}: {ProviderName(db.First().Connection)} {Describe(db.First().Connection)}");
                if (info.Command is not null)
                {
                    _out.WriteLine($"  set {info.PasswordVariable} to the database password, then run:");
                    _out.WriteLine($"  {Scrub(info.Command)}");
                }

                _out.WriteLine($"  {info.Note}");
            }

            return DbUpgradeExitCodes.Ok;
        }

        // --- backup gate --------------------------------------------------------------------

        private async Task<int> BackupGateAsync(
            List<(Target Target, SchemaPlan Plan)> pending, DbUpgradeArguments a, string? backupDir, CancellationToken ct)
        {
            // A database with no schema yet has nothing to lose; everything else holds data.
            var risky = pending.Where(p => p.Plan.State is not (SchemaState.Missing or SchemaState.Fresh)).ToList();
            if (risky.Count == 0)
            {
                return DbUpgradeExitCodes.Ok;
            }

            bool confirmed = a.Flag("--confirm-backup");
            if (backupDir is not null)
            {
                foreach (IGrouping<string, (Target Target, SchemaPlan Plan)> db in risky
                    .Where(p => p.Target.Connection.Provider == DatabaseProvider.Sqlite)
                    .GroupBy(p => p.Target.Connection.ConnectionString, StringComparer.Ordinal))
                {
                    try
                    {
                        string file = await BackupAdvisor.BackupSqliteAsync(db.First().Target.Connection, backupDir, cancellationToken: ct).ConfigureAwait(false);
                        await _out.WriteLineAsync($"backup written: {Scrub(file)}").ConfigureAwait(false);
                    }
                    catch (BackupException ex)
                    {
                        await _err.WriteLineAsync($"error: backup failed: {Scrub(ex.Message)}").ConfigureAwait(false);
                        return DbUpgradeExitCodes.BackupNotConfirmed;
                    }
                }
            }

            bool serverPending = risky.Any(p => p.Target.Connection.Provider != DatabaseProvider.Sqlite);
            bool sqlitePending = risky.Any(p => p.Target.Connection.Provider == DatabaseProvider.Sqlite);
            bool satisfied = confirmed || (!serverPending && (backupDir is not null || !sqlitePending));
            if (satisfied)
            {
                return DbUpgradeExitCodes.Ok;
            }

            await _err.WriteLineAsync("error: this upgrade changes databases that hold data and no backup was confirmed; nothing was changed.").ConfigureAwait(false);
            await _err.WriteLineAsync("back each one up, then run the upgrade again with --confirm-backup:").ConfigureAwait(false);
            foreach (IGrouping<string, (Target Target, SchemaPlan Plan)> db in risky.GroupBy(p => p.Target.Connection.ConnectionString, StringComparer.Ordinal))
            {
                BackupAdvisor.BackupInstructions info = BackupAdvisor.Describe(db.First().Target.Connection);
                await _err.WriteLineAsync($"  {string.Join(", ", db.Select(p => p.Target.Spec.Name))} ({ProviderName(db.First().Target.Connection)} {Describe(db.First().Target.Connection)})").ConfigureAwait(false);
                await _err.WriteLineAsync(info.Command is null
                    ? "    SQLite: pass --backup-dir <directory> and the tool writes a verified copy itself"
                    : $"    set {info.PasswordVariable}, then: {Scrub(info.Command)}").ConfigureAwait(false);
            }

            return DbUpgradeExitCodes.BackupNotConfirmed;
        }

        // --- plumbing -----------------------------------------------------------------------

        private IEnumerable<Target> Targets(DbUpgradeArguments a, bool readOnly)
        {
            foreach (string name in a.Components())
            {
                Spec spec = s_specs.Single(s => s.Name == name);
                DatabaseConnectionOptions resolved = _database.Resolve(spec.Component);
                if (string.IsNullOrWhiteSpace(resolved.ConnectionString))
                {
                    throw new UsageException(
                        $"no connection string for the {name} database: set Database:{char.ToUpperInvariant(name[0])}{name[1..]}:ConnectionString " +
                        "(or Database:ConnectionString for a single database), for example through the environment variable " +
                        $"Database__{char.ToUpperInvariant(name[0])}{name[1..]}__ConnectionString");
                }

                DatabaseConnectionOptions prepared = Prepare(resolved, readOnly, name);
                yield return new Target(spec, prepared, Describe(prepared));
            }
        }

        /// <summary>Unpooled connections (the session count then only sees other processes); SQLite read-only when only reading.</summary>
        private DatabaseConnectionOptions Prepare(DatabaseConnectionOptions options, bool readOnly, string component)
        {
            try
            {
                string cs = options.Provider switch
                {
                    DatabaseProvider.Sqlite => new SqliteConnectionStringBuilder(options.ConnectionString)
                    {
                        Pooling = false,
                        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                    }.ConnectionString,
                    DatabaseProvider.MariaDb or DatabaseProvider.MySql => new MySqlConnectionStringBuilder(options.ConnectionString) { Pooling = false }.ConnectionString,
                    DatabaseProvider.PostgreSql => new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false, ApplicationName = "arcane-db" }.ConnectionString,
                    _ => throw new UsageException($"unsupported provider {options.Provider}"),
                };
                return new DatabaseConnectionOptions { Provider = options.Provider, ConnectionString = cs };
            }
            catch (ArgumentException ex)
            {
                throw new UsageException($"the {component} connection string is not valid for {options.Provider}: {Scrub(ex.Message)}");
            }
        }

        private static string Describe(DatabaseConnectionOptions options)
        {
            BackupAdvisor.BackupInstructions info = BackupAdvisor.Describe(options);
            return options.Provider == DatabaseProvider.Sqlite ? info.Database : $"{info.Host}:{info.Port}/{info.Database}";
        }

        private static string ProviderName(DatabaseConnectionOptions options) => options.Provider switch
        {
            DatabaseProvider.Sqlite => "SQLite",
            DatabaseProvider.MariaDb => "MariaDB",
            DatabaseProvider.MySql => "MySQL",
            DatabaseProvider.PostgreSql => "PostgreSQL",
            _ => options.Provider.ToString(),
        };

        private static DbContext CreateContext(Target target) => target.Spec.Create(target.Connection);

        /// <summary>Open a context for the target, run the work, and turn a failure into a phrased <see cref="ComponentFailure"/>.</summary>
        private async Task<T> GuardAsync<T>(Target target, CancellationToken ct, Func<DbContext, Task<T>> work)
        {
            try
            {
                await using DbContext db = CreateContext(target);
                return await work(db).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or UsageException or ComponentFailure))
            {
                ct.ThrowIfCancellationRequested();
                throw Classify(target.Spec.Name, ex);
            }
        }

        private ComponentFailure Classify(string component, Exception ex)
        {
            string message = Scrub(ex.Message);
            if (ex is SchemaMismatchException mismatch)
            {
                int code = mismatch.Reason == SchemaMismatchReason.LockTimeout ? DbUpgradeExitCodes.LockTimeout : DbUpgradeExitCodes.Refused;
                return new ComponentFailure(code, $"{component}: {message}");
            }

            for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
            {
                if (inner is DbException or SocketException or TimeoutException)
                {
                    string detail = Scrub(inner.Message);
                    return new ComponentFailure(
                        DbUpgradeExitCodes.Unreachable,
                        $"{component}: the database could not be reached or refused the connection: {detail}");
                }
            }

            return new ComponentFailure(DbUpgradeExitCodes.Failure, $"{component}: unexpected {ex.GetType().Name}: {message}");
        }

        private async Task<DriftReport> DriftOfAsync(Target target, string[] known, CancellationToken ct)
            => await GuardAsync(target, ct, async db =>
            {
                var creator = (Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator)db.GetService<Microsoft.EntityFrameworkCore.Storage.IDatabaseCreator>();
                if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
                {
                    return new DriftReport(
                        target.Spec.Name,
                        [new DriftFinding(DriftSeverity.Error, DriftKind.MissingTable, null, $"the {target.Spec.Name} database does not exist")],
                        0, 0, 0);
                }

                return await SchemaDriftChecker.CheckAsync(db, target.Spec.Schema, known, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        /// <summary>Every table any component owns (their models need no connection), so another component's table is not "foreign".</summary>
        private string[] KnownTables()
        {
            var tables = new List<string>();
            foreach (Spec spec in s_specs)
            {
                using DbContext db = spec.Create(new DatabaseConnectionOptions
                {
                    Provider = DatabaseProvider.Sqlite,
                    ConnectionString = "Data Source=:memory:",
                });
                IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
                tables.AddRange(model.Tables.Select(t => t.Name));
            }

            return [.. tables.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private static async Task<(int? Sessions, bool? LockHeld)> BestEffortAsync(DbContext db, string component, CancellationToken ct)
        {
            try
            {
                return (await ServerProbe.CountOtherSessionsAsync(db, ct).ConfigureAwait(false),
                    await ServerProbe.IsSchemaLockHeldAsync(db, component, ct).ConfigureAwait(false));
            }
            catch (DbException)
            {
                return (null, null); // a role without PROCESS / pg_read_all_stats cannot see them
            }
        }

        private static int ExitFor(SchemaPlan plan, DbUpgradeArguments a)
        {
            if (plan.IsRefused)
            {
                return DbUpgradeExitCodes.Refused;
            }

            return plan.NeedsApply && !a.Flag("--no-fail-on-pending") ? DbUpgradeExitCodes.UpgradePending : DbUpgradeExitCodes.Ok;
        }

        /// <summary>The more severe of two exit codes: refused over pending over ok.</summary>
        private static int Worse(int current, int next)
        {
            static int Rank(int code) => code switch
            {
                DbUpgradeExitCodes.Refused => 2,
                DbUpgradeExitCodes.UpgradePending => 1,
                _ => 0,
            };

            return Rank(next) > Rank(current) ? next : current;
        }
    }

    private sealed class LineProgress(TextWriter writer) : IProgress<SchemaStepProgress>
    {
        public void Report(SchemaStepProgress value) => writer.WriteLine($"  {value.Component}: schema version {value.Version} written");
    }
}
