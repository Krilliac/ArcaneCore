using System.Data.Common;
using System.Globalization;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>One non-primary-key index, as a model declares it or a database holds it.</summary>
internal sealed record IndexShape(string Table, string Name, bool IsUnique, string Columns)
{
    public override string ToString() => $"{Table}.{Name}{(IsUnique ? " UNIQUE" : string.Empty)}({Columns})";
}

/// <summary>Raised by <see cref="CommandTap"/> to simulate a process dying right after a statement.</summary>
internal sealed class InjectedFaultException(string commandText) : Exception("injected fault after: " + commandText);

/// <summary>
/// Meets two startups at their first EF-issued command. Bounded, so a lock that keeps the
/// second task away from the barrier cannot deadlock the test.
/// </summary>
internal sealed class StartupBarrier(int parties, TimeSpan timeout)
{
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public int Arrived => Volatile.Read(ref _arrived);

    public async Task ArriveAsync()
    {
        if (Interlocked.Increment(ref _arrived) >= parties)
        {
            _all.TrySetResult();
        }

        await Task.WhenAny(_all.Task, Task.Delay(timeout)).ConfigureAwait(false);
    }
}

/// <summary>
/// Records every EF-issued command and can fail one of them after it ran (a crash after the
/// statement committed). Raw catalog reads in the production catalog reader bypass EF and are
/// therefore invisible here by design.
/// </summary>
internal sealed class CommandTap : DbCommandInterceptor
{
    private readonly object _gate = new();
    private readonly List<string> _commands = [];
    private readonly Func<string, bool>? _faultWhen;
    private readonly int _faultOn;
    private readonly StartupBarrier? _barrier;
    private readonly bool _faultBeforeStatement;
    private int _matches;
    private bool _arrived;

    /// <param name="faultBeforeStatement">Fail before the matching statement runs (a crash just before it) instead of after it committed.</param>
    public CommandTap(Func<string, bool>? faultWhen = null, int faultOn = 0, StartupBarrier? barrier = null, bool faultBeforeStatement = false)
    {
        _faultBeforeStatement = faultBeforeStatement;
        _faultWhen = faultWhen;
        _faultOn = faultOn;
        _barrier = barrier;
    }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    public int Ddl => Commands.Count(IsDdl);

    /// <summary>
    /// Whether any statement of the command text is one of the given DML verbs. Pomelo prefixes every
    /// SaveChanges batch with <c>SET AUTOCOMMIT = 1;</c>, so the verb is not always the first word.
    /// </summary>
    public static bool IsWrite(string sql, params string[] verbs)
        => verbs.Any(v => System.Text.RegularExpressions.Regex.IsMatch(
            sql, @"(^|;)\s*" + v + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    public static bool IsDdl(string sql)
    {
        string s = sql.TrimStart();

        // CREATE DATABASE runs before the bootstrapper has a connection to the database; a failure of it is
        // swallowed on purpose when the database now exists (another process may have created it), so it is not
        // a point where a startup can meaningfully die, and a startup that loses that race issues it too.
        if (s.StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return s.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("DROP", StringComparison.OrdinalIgnoreCase);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(command).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(command).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(command).ConfigureAwait(false);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        After(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        After(command, result);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        After(command);
        return ValueTask.FromResult(result);
    }

    private async Task BeforeAsync(DbCommand command)
    {
        lock (_gate)
        {
            _commands.Add(command.CommandText);
        }

        if (_barrier is not null && !_arrived)
        {
            _arrived = true;
            await _barrier.ArriveAsync().ConfigureAwait(false);
        }

        if (_faultBeforeStatement)
        {
            MaybeFault(command.CommandText);
        }
    }

    private void After(DbCommand command, DbDataReader? reader = null)
    {
        if (!_faultBeforeStatement)
        {
            try
            {
                MaybeFault(command.CommandText);
            }
            catch (InjectedFaultException)
            {
                // A process that dies takes its open reader with it. Throwing from this hook while the reader
                // is still open would leave the connection "in use" (MySqlConnector refuses the next command on
                // it), which is not a state a crashed startup can leave behind.
                reader?.Dispose();
                throw;
            }
        }
    }

    private void MaybeFault(string sql)
    {
        if (_faultWhen is null || !_faultWhen(sql))
        {
            return;
        }

        if (Interlocked.Increment(ref _matches) == _faultOn)
        {
            throw new InjectedFaultException(sql);
        }
    }
}

/// <summary>Index/column/row inspection written independently of the production catalog reader.</summary>
internal static class SchemaProbe
{
    public static SchemaDefinition ThroughVersion(SchemaDefinition schema, int version) => new()
    {
        Component = schema.Component,
        CurrentVersion = version,
        Version1Tables = schema.Version1Tables,
        Steps = [.. schema.Steps.Where(s => s.Version <= version)],
    };

    public static T CreateWith<T>(DatabaseConnectionOptions connection, params IInterceptor[] interceptors) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>(TestContexts.Options<T>(connection));
        builder.AddInterceptors(interceptors);
        return (T)Activator.CreateInstance(typeof(T), builder.Options)!;
    }

    /// <summary>The component names and their current schema, in startup order.</summary>
    public static IEnumerable<object[]> Components() =>
        [["auth"], ["characters"], ["world"]];

    /// <summary>Run the current bootstrap of one component through a context carrying <paramref name="interceptors"/>.</summary>
    public static async Task EnsureCurrentAsync(string component, DatabaseConnectionOptions connection, params IInterceptor[] interceptors)
    {
        switch (component)
        {
            case "auth":
                await using (AuthDbContext db = CreateWith<AuthDbContext>(connection, interceptors))
                {
                    await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
                }

                break;
            case "characters":
                await using (CharacterDbContext db = CreateWith<CharacterDbContext>(connection, interceptors))
                {
                    await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
                }

                break;
            case "world":
                await using (WorldDbContext db = CreateWith<WorldDbContext>(connection, interceptors))
                {
                    await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(component));
        }
    }

    public static SchemaDefinition SchemaOf(string component) => component switch
    {
        "auth" => AuthDbContext.Schema,
        "characters" => CharacterDbContext.Schema,
        "world" => WorldDbContext.Schema,
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    public static DbContext CreateContext(string component, DatabaseConnectionOptions connection) => component switch
    {
        "auth" => TestContexts.Create<AuthDbContext>(connection),
        "characters" => TestContexts.Create<CharacterDbContext>(connection),
        "world" => TestContexts.Create<WorldDbContext>(connection),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    public static IReadOnlyList<IndexShape> ModelIndexes(DbContext db)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        return [.. model.Tables
            .SelectMany(t => t.Indexes.Select(i => new IndexShape(t.Name, i.Name, i.IsUnique, string.Join(",", i.Columns.Select(c => c.Name)))))
            .OrderBy(s => s.ToString(), StringComparer.Ordinal)];
    }

    public static IReadOnlyList<string> ModelTables(DbContext db)
        => [.. db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Select(t => t.Name)];

    public static IReadOnlyDictionary<string, string[]> ModelColumns(DbContext db)
        => db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .ToDictionary(t => t.Name, t => t.Columns.Select(c => c.Name).ToArray(), StringComparer.Ordinal);

    public static async Task<IReadOnlyList<IndexShape>> ActualIndexesAsync(DbContext db, IEnumerable<string> tables)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        var shapes = new List<IndexShape>();
        foreach (string table in tables)
        {
            var rows = new List<(string Name, bool Unique, string Column)>();
            await using (DbCommand command = db.Database.GetDbConnection().CreateCommand())
            {
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = db.Database.ProviderName switch
                {
                    "Microsoft.EntityFrameworkCore.Sqlite" =>
                        "SELECT il.name, il.\"unique\", ii.name FROM pragma_index_list(@t) AS il " +
                        "JOIN pragma_index_info(il.name) AS ii WHERE il.origin = 'c' ORDER BY il.name, ii.seqno",
                    "Pomelo.EntityFrameworkCore.MySql" =>
                        "SELECT index_name, CASE WHEN non_unique = 0 THEN 1 ELSE 0 END, column_name FROM information_schema.statistics " +
                        "WHERE table_schema = DATABASE() AND table_name = @t AND index_name <> 'PRIMARY' ORDER BY index_name, seq_in_index",
                    "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                        "SELECT i.relname, ix.indisunique, pg_get_indexdef(ix.indexrelid, k.n, true) FROM pg_class t " +
                        "JOIN pg_namespace ns ON ns.oid = t.relnamespace JOIN pg_index ix ON ix.indrelid = t.oid " +
                        "JOIN pg_class i ON i.oid = ix.indexrelid CROSS JOIN LATERAL generate_series(1, ix.indnkeyatts::int) AS k(n) " +
                        "WHERE ns.nspname = current_schema() AND t.relname = @t AND NOT ix.indisprimary ORDER BY i.relname, k.n",
                    var other => throw new NotSupportedException(other),
                };
                DbParameter p = command.CreateParameter();
                p.ParameterName = "@t";
                p.Value = table;
                command.Parameters.Add(p);
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) != 0, reader.GetString(2).Trim('"', '`')));
                }
            }

            foreach (IGrouping<string, (string Name, bool Unique, string Column)> g in rows.GroupBy(r => r.Name))
            {
                shapes.Add(new IndexShape(table, g.Key, g.First().Unique, string.Join(",", g.Select(r => r.Column))));
            }
        }

        return [.. shapes.OrderBy(s => s.ToString(), StringComparer.Ordinal)];
    }

    public static async Task<string[]> ActualColumnsAsync(DbContext db, string table)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = db.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => "SELECT name FROM pragma_table_info(@t)",
            "Pomelo.EntityFrameworkCore.MySql" =>
                "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t",
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @t",
            var other => throw new NotSupportedException(other),
        };
        DbParameter p = command.CreateParameter();
        p.ParameterName = "@t";
        p.Value = table;
        command.Parameters.Add(p);
        var columns = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return [.. columns.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The database's non-primary-key indexes of every table of the context's model must be
    /// exactly the model's (name, uniqueness, ordered columns), and every table the model
    /// indexes must have at least one: an unreadable catalog cannot pass vacuously.
    /// </summary>
    public static async Task AssertIndexParityAsync(DbContext db, string label)
    {
        IReadOnlyList<IndexShape> expected = ModelIndexes(db);
        Assert.NotEmpty(expected);
        IReadOnlyList<IndexShape> actual = await ActualIndexesAsync(db, ModelTables(db));
        string[] missing = [.. expected.Except(actual).Select(s => s.ToString())];
        string[] extra = [.. actual.Except(expected).Select(s => s.ToString())];
        Assert.True(missing.Length == 0 && extra.Length == 0,
            $"{label}: indexes differ from the model. missing: [{string.Join("; ", missing)}] unexpected: [{string.Join("; ", extra)}]");
    }

    public static async Task AssertColumnParityAsync(DbContext db, string label)
    {
        foreach ((string table, string[] columns) in ModelColumns(db))
        {
            string[] actual = await ActualColumnsAsync(db, table);
            Assert.True(
                columns.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
                $"{label}: columns of {table} differ from the model. model [{string.Join(",", columns)}] database [{string.Join(",", actual)}]");
        }
    }

    /// <summary>Drop every secondary index of a table (what a database upgraded by the historic bootstrapper lacks).</summary>
    public static async Task DropIndexesAsync(DbContext db, string table)
    {
        foreach (IndexShape index in await ActualIndexesAsync(db, [table]))
        {
            string name = TestContexts.Quote(db, index.Name);
            await ExecuteAsync(db, db.Database.ProviderName == "Pomelo.EntityFrameworkCore.MySql"
                ? $"DROP INDEX {name} ON {TestContexts.Quote(db, table)}"
                : $"DROP INDEX {name}");
        }
    }

    public static async Task ExecuteAsync(DbContext db, string sql) => await db.Database.ExecuteSqlRawAsync(sql);

    /// <summary>Every row of <paramref name="table"/> (the given columns) as comparable text, ordered by all of them.</summary>
    public static async Task<string[]> SnapshotAsync(DbContext db, string table, IReadOnlyList<string> columns)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        string list = string.Join(", ", columns.Select(c => TestContexts.Quote(db, c)));
        string order = string.Join(", ", Enumerable.Range(1, columns.Count));
        command.CommandText = $"SELECT {list} FROM {TestContexts.Quote(db, table)} ORDER BY {order}";
        var rows = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) switch
            {
                null or DBNull => "NULL",
                byte[] b => Convert.ToHexString(b),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v => v.ToString(),
            })));
        }

        return [.. rows];
    }
}

/// <summary>Keeps a context's connection open (EF reference-counted) for a probe.</summary>
internal sealed class ConnectionScope : IAsyncDisposable
{
    private readonly DbContext _db;

    private ConnectionScope(DbContext db) => _db = db;

    public static async Task<ConnectionScope> OpenAsync(DbContext db)
    {
        await db.Database.OpenConnectionAsync();
        return new ConnectionScope(db);
    }

    public async ValueTask DisposeAsync() => await _db.Database.CloseConnectionAsync();
}