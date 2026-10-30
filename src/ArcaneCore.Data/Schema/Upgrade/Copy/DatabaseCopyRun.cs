using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Schema.Upgrade.Copy;

/// <summary>Test seams of <c>copy</c>; production passes none.</summary>
internal sealed class DbCopyHooks
{
    /// <summary>Runs inside the target transaction after every row was inserted and before verification (a test corrupts a row here to force exit 9).</summary>
    public Func<DbContext, CancellationToken, Task>? AfterInsert { get; init; }

    /// <summary>Overrides <see cref="DatabaseCopy.MaxBatchRows"/>.</summary>
    public int? MaxBatchRows { get; init; }

    /// <summary>Overrides <see cref="DatabaseCopy.DefaultMaxBatchBytes"/>.</summary>
    public int? MaxBatchBytes { get; init; }
}

/// <summary>What a copy of one component did.</summary>
/// <param name="Results">Per table, source and target rows and whether the model-typed content agreed (read inside the transaction, before the commit).</param>
/// <param name="Committed">False when verification found a difference: the target transaction was rolled back and the target is as it was.</param>
/// <param name="CountersSet">How many auto-generated key counters were moved past the copied keys.</param>
/// <param name="PostCommitProblem">Set when everything was committed and verified but resetting the MariaDB/MySQL AUTO_INCREMENT counters failed afterwards (that DDL cannot run inside the transaction).</param>
internal sealed record CopyOutcome(IReadOnlyList<CopyTableResult> Results, bool Committed, int CountersSet, string? PostCommitProblem);

internal static partial class DatabaseCopy
{
    /// <summary>
    /// Copy every table of <paramref name="ordered"/> (parents first) from <paramref name="source"/> to <paramref name="target"/>. The source is read in
    /// ONE read transaction (a consistent snapshot, also for the verification). The target is changed in ONE transaction: the component's tables are
    /// emptied in reverse order, filled in order, the key counters are reset (PostgreSQL; MariaDB's DDL cannot run in a transaction and follows the
    /// commit), and every table is verified by reading both sides back through the model's CLR types. Only an all-equal result commits; a difference
    /// rolls everything back, and so does any exception.
    /// </summary>
    public static async Task<CopyOutcome> CopyAsync(
        DbContext source, DbContext target, IReadOnlyList<CopyTable> ordered, DbCopyHooks? hooks, CancellationToken ct)
    {
        Dictionary<string, Dictionary<string, CopyKind>> kinds = await ReadKindsAsync(target, ordered, ct).ConfigureAwait(false);
        ISqlGenerationHelper sourceSql = source.GetService<ISqlGenerationHelper>();
        ISqlGenerationHelper targetSql = target.GetService<ISqlGenerationHelper>();
        bool postgres = target.Database.ProviderName == NpgsqlProvider;
        bool truncate = target.Database.ProviderName != SqliteProvider; // PostgreSQL and MariaDB keep microseconds (see CopyValues.ToTimestamp)
        int maxRows = Math.Max(1, hooks?.MaxBatchRows ?? MaxBatchRows);
        int maxBytes = Math.Max(1, hooks?.MaxBatchBytes ?? DefaultMaxBatchBytes);
        var results = new List<CopyTableResult>();
        int counters = 0;
        string? postCommit = null;
        bool committed;

        await source.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await target.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            DbConnection sourceConnection = source.Database.GetDbConnection();
            DbConnection targetConnection = target.Database.GetDbConnection();
            if (target.Database.ProviderName == PomeloProvider)
            {
                // Without it MariaDB turns an explicit 0 in an AUTO_INCREMENT column into the next id.
                await using DbCommand mode = targetConnection.CreateCommand();
                mode.CommandText = "SET SESSION sql_mode = CONCAT(@@SESSION.sql_mode, ',NO_AUTO_VALUE_ON_ZERO')";
                await mode.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using IDbContextTransaction sourceTx = source.Database.ProviderName == SqliteProvider
                ? await source.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                : await source.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
            await using IDbContextTransaction targetTx = await target.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            DbTransaction sourceTransaction = sourceTx.GetDbTransaction();
            DbTransaction targetTransaction = targetTx.GetDbTransaction();

            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                await using DbCommand delete = targetConnection.CreateCommand();
                delete.Transaction = targetTransaction;
                delete.CommandText = "DELETE FROM " + targetSql.DelimitIdentifier(ordered[i].Name);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (CopyTable table in ordered)
            {
                CopyKind[] columnKinds = [.. table.Columns.Select(c => kinds.TryGetValue(table.Name, out var byColumn) && byColumn.TryGetValue(c, out CopyKind k) ? k : CopyKind.Native)];
                await CopyTableAsync(sourceConnection, sourceTransaction, sourceSql, targetConnection, targetTransaction, targetSql, table, columnKinds, postgres, maxRows, maxBytes, ct).ConfigureAwait(false);
            }

            if (hooks?.AfterInsert is { } after)
            {
                await after(target, ct).ConfigureAwait(false);
            }

            if (postgres)
            {
                counters = await ResetPostgresSequencesAsync(targetConnection, targetTransaction, targetSql, ordered, ct).ConfigureAwait(false);
            }

            foreach (CopyTable table in ordered)
            {
                (long sourceRows, TableDigest sourceDigest) = await ModelDigestAsync(sourceConnection, sourceTransaction, sourceSql, table, "source", truncate, ct).ConfigureAwait(false);
                (long targetRows, TableDigest targetDigest) = await ModelDigestAsync(targetConnection, targetTransaction, targetSql, table, "target", truncate, ct).ConfigureAwait(false);
                results.Add(new CopyTableResult(table.Name, sourceRows, targetRows, sourceDigest.Equals(targetDigest)));
            }

            committed = results.All(r => r.IsOk);
            if (committed)
            {
                await targetTx.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await targetTx.RollbackAsync(ct).ConfigureAwait(false);
            }

            await sourceTx.RollbackAsync(ct).ConfigureAwait(false); // read-only: nothing to keep
        }
        finally
        {
            await target.Database.CloseConnectionAsync().ConfigureAwait(false);
            await source.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        if (committed && target.Database.ProviderName == PomeloProvider)
        {
            try
            {
                counters = await ResetAutoIncrementAsync(target, ordered, ct).ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                postCommit = $"the rows are committed and verified, but resetting the AUTO_INCREMENT counters failed: {ex.Message}";
            }
        }

        return new CopyOutcome(results, committed, counters, postCommit);
    }

    private static string RowKey(DbDataReader reader, CopyTable table)
    {
        if (table.PrimaryKey.Count == 0)
        {
            return "(table has no primary key)";
        }

        var parts = new List<string>();
        foreach (string key in table.PrimaryKey)
        {
            int at = table.Columns.ToList().FindIndex(c => string.Equals(c, key, StringComparison.OrdinalIgnoreCase));
            string text;
            try
            {
                text = at < 0 || reader.IsDBNull(at) ? "NULL" : System.Convert.ToString(reader.GetValue(at), CultureInfo.InvariantCulture) ?? "?";
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or InvalidOperationException)
            {
                text = "?";
            }

            parts.Add($"{key}={text}");
        }

        return string.Join(", ", parts);
    }

    private static async Task CopyTableAsync(
        DbConnection sourceConnection, DbTransaction sourceTransaction, ISqlGenerationHelper sourceSql,
        DbConnection targetConnection, DbTransaction targetTransaction, ISqlGenerationHelper targetSql,
        CopyTable table, CopyKind[] kinds, bool postgres, int maxRows, int maxBytes, CancellationToken ct)
    {
        string columnList = string.Join(", ", table.Columns.Select(targetSql.DelimitIdentifier));
        await using DbCommand select = sourceConnection.CreateCommand();
        select.Transaction = sourceTransaction;
        select.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(sourceSql.DelimitIdentifier))} FROM {sourceSql.DelimitIdentifier(table.Name)}";
        await using DbDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);

        int perBatch = Math.Max(1, Math.Min(maxRows, MaxBatchParameters / Math.Max(1, table.Columns.Count)));
        var batch = new List<object[]>(perBatch);
        var keys = new List<string>(perBatch);
        long bytes = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object[kinds.Length];
            for (int i = 0; i < row.Length; i++)
            {
                try
                {
                    object lifted = CopyValues.Lift(reader.IsDBNull(i) ? null : reader.GetValue(i), table.ColumnTypes?[i]);
                    row[i] = CopyValues.Convert(lifted, kinds[i]);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    throw new CopyDataException($"table '{table.Name}', column '{table.Columns[i]}', row [{RowKey(reader, table)}]: {ex.Message}", ex);
                }

                if (postgres && row[i] is string text && text.Contains('\0', StringComparison.Ordinal))
                {
                    throw new CopyDataException(
                        $"table '{table.Name}', column '{table.Columns[i]}', row [{RowKey(reader, table)}]: the text contains a NUL character, which PostgreSQL cannot store", null);
                }

                bytes += row[i] switch { string s => s.Length * 2L, byte[] b => b.Length, _ => 8 };
            }

            batch.Add(row);
            keys.Add(RowKey(reader, table));
            if (batch.Count >= perBatch || bytes >= maxBytes)
            {
                await InsertBatchAsync(targetConnection, targetTransaction, targetSql, table, columnList, batch, keys, postgres, ct).ConfigureAwait(false);
                batch.Clear();
                keys.Clear();
                bytes = 0;
            }
        }

        if (batch.Count > 0)
        {
            await InsertBatchAsync(targetConnection, targetTransaction, targetSql, table, columnList, batch, keys, postgres, ct).ConfigureAwait(false);
        }
    }

    private static DbCommand InsertCommand(DbConnection connection, DbTransaction transaction, ISqlGenerationHelper sql, CopyTable table, string columnList, IReadOnlyList<object[]> rows)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        var text = new StringBuilder("INSERT INTO ").Append(sql.DelimitIdentifier(table.Name)).Append(" (").Append(columnList).Append(") VALUES ");
        int p = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            text.Append(r == 0 ? "(" : ", (");
            for (int c = 0; c < rows[r].Length; c++)
            {
                string name = "@p" + p++;
                text.Append(c == 0 ? string.Empty : ", ").Append(name);
                AddParameter(command, name, rows[r][c]);
            }

            text.Append(')');
        }

        command.CommandText = text.ToString();
        return command;
    }

    private static async Task InsertBatchAsync(
        DbConnection connection, DbTransaction transaction, ISqlGenerationHelper sql, CopyTable table, string columnList,
        List<object[]> rows, List<string> keys, bool postgres, CancellationToken ct)
    {
        if (postgres)
        {
            await ExecuteAsync(connection, transaction, "SAVEPOINT arcane_copy_batch", ct).ConfigureAwait(false); // a failed statement aborts a PostgreSQL transaction
        }

        try
        {
            await using DbCommand command = InsertCommand(connection, transaction, sql, table, columnList, rows);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (postgres)
            {
                await ExecuteAsync(connection, transaction, "RELEASE SAVEPOINT arcane_copy_batch", ct).ConfigureAwait(false);
            }

            return;
        }
        catch (DbException batchFailure)
        {
            if (postgres)
            {
                await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT arcane_copy_batch", ct).ConfigureAwait(false);
            }

            // The multi-row statement does not say which row it was: replay the batch one row at a time to name it.
            for (int r = 0; r < rows.Count; r++)
            {
                if (postgres)
                {
                    await ExecuteAsync(connection, transaction, "SAVEPOINT arcane_copy_row", ct).ConfigureAwait(false);
                }

                try
                {
                    await using DbCommand single = InsertCommand(connection, transaction, sql, table, columnList, [rows[r]]);
                    await single.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    if (postgres)
                    {
                        await ExecuteAsync(connection, transaction, "RELEASE SAVEPOINT arcane_copy_row", ct).ConfigureAwait(false);
                    }
                }
                catch (DbException rowFailure)
                {
                    string column = rowFailure switch
                    {
                        PostgresException { ColumnName: { Length: > 0 } name } => $", column '{name}'",
                        MySqlException m when m.Message.Contains("column '", StringComparison.Ordinal) => string.Empty, // MySQL's own text names it
                        _ => ", column not reported by the server",
                    };
                    throw new CopyDataException($"table '{table.Name}'{column}, row [{keys[r]}]: the insert failed: {rowFailure.Message}", rowFailure);
                }
            }

            throw new CopyDataException($"table '{table.Name}': inserting a batch of {rows.Count} row(s) failed: {batchFailure.Message}", batchFailure);
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every row of the table read through the model's CLR types (<c>GetFieldValue&lt;T&gt;</c> with the model property's type, the engine's own
    /// decoding), as an order-independent digest. Does not use <see cref="CopyValues.Convert"/>, so a bug in the conversion shows as a difference.
    /// </summary>
    private static async Task<(long Rows, TableDigest Digest)> ModelDigestAsync(
        DbConnection connection, DbTransaction transaction, ISqlGenerationHelper sql, CopyTable table, string side, bool truncateMicroseconds, CancellationToken ct)
    {
        var digest = new TableDigest();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(sql.DelimitIdentifier))} FROM {sql.DelimitIdentifier(table.Name)}";
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var canonical = new StringBuilder();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            canonical.Clear();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                try
                {
                    AppendModelValue(canonical, reader.IsDBNull(i) ? null : ReadModelValue(reader, i, table.ColumnTypes?[i] ?? typeof(object)), truncateMicroseconds);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or NotSupportedException)
                {
                    throw new CopyDataException(
                        $"table '{table.Name}', column '{table.Columns[i]}', row [{RowKey(reader, table)}]: the {side} value cannot be read as {table.ColumnTypes?[i].Name ?? "its type"}: {ex.Message}", ex);
                }
            }

            digest.Add(canonical.ToString());
        }

        return (digest.Rows, digest);
    }

    /// <summary>An integer column as a long, whatever width the provider reports (Npgsql refuses unsigned and one-byte reads; SQLite returns long).</summary>
    private static long Whole(DbDataReader r, int i) =>
        r.GetValue(i) switch
        {
            long l => l,
            int n => n,
            short s => s,
            byte b => b,
            sbyte sb => sb,
            ushort us => us,
            uint ui => ui,
            decimal d => (long)d,
            bool f => f ? 1 : 0,
            var other => System.Convert.ToInt64(other, CultureInfo.InvariantCulture),
        };

    private static object ReadModelValue(DbDataReader r, int i, Type type)
    {
        if (type == typeof(ulong))
        {
            return r.GetValue(i) switch
            {
                ulong u => u,
                long l => unchecked((ulong)l), // SQLite keeps the 64 bits in a signed integer
                decimal d => (ulong)d,
                _ => r.GetFieldValue<ulong>(i),
            };
        }

        if (type == typeof(bool)) return r.GetFieldValue<bool>(i);
        if (type == typeof(byte)) return checked((byte)Whole(r, i));
        if (type == typeof(sbyte)) return checked((sbyte)Whole(r, i));
        if (type == typeof(short)) return checked((short)Whole(r, i));
        if (type == typeof(ushort)) return checked((ushort)Whole(r, i));
        if (type == typeof(int)) return checked((int)Whole(r, i));
        if (type == typeof(uint)) return checked((uint)Whole(r, i));
        if (type == typeof(long)) return Whole(r, i);
        if (type == typeof(float)) return System.Convert.ToSingle(r.GetValue(i), CultureInfo.InvariantCulture);
        if (type == typeof(double)) return r.GetFieldValue<double>(i);
        if (type == typeof(decimal)) return r.GetFieldValue<decimal>(i);
        if (type == typeof(string)) return r.GetFieldValue<string>(i);
        if (type == typeof(char)) return r.GetValue(i) is string cs && cs.Length == 1 ? cs[0] : r.GetFieldValue<char>(i);
        if (type == typeof(DateTime)) return r.GetFieldValue<DateTime>(i);
        if (type == typeof(DateTimeOffset)) return r.GetFieldValue<DateTimeOffset>(i);
        if (type == typeof(TimeSpan)) return r.GetFieldValue<TimeSpan>(i);
        if (type == typeof(Guid)) return r.GetFieldValue<Guid>(i);
        if (type == typeof(byte[])) return r.GetFieldValue<byte[]>(i);
        if (type == typeof(DateOnly)) return r.GetFieldValue<DateOnly>(i);
        if (type == typeof(TimeOnly)) return r.GetFieldValue<TimeOnly>(i);
        return r.GetValue(i);
    }

    /// <summary>A text equal for equal model values. A float is compared at float precision (a real column cannot hold more); timestamps at microseconds when the target keeps no more.</summary>
    private static void AppendModelValue(StringBuilder text, object? value, bool truncateMicroseconds)
    {
        switch (value)
        {
            case null: text.Append('N'); break;
            case string s: text.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s); break;
            case bool b: text.Append(b ? "b1" : "b0"); break;
            case float f: text.Append('f').Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
            case double d: text.Append('F').Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case decimal m: text.Append('d').Append((m / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)); break;
            case DateTime t: text.Append('t').Append((truncateMicroseconds ? t.Ticks / 10 : t.Ticks).ToString(CultureInfo.InvariantCulture)); break;
            case DateTimeOffset o: text.Append('t').Append((truncateMicroseconds ? o.UtcTicks / 10 : o.UtcTicks).ToString(CultureInfo.InvariantCulture)); break;
            case TimeSpan s: text.Append('T').Append(s.Ticks.ToString(CultureInfo.InvariantCulture)); break;
            case Guid g: text.Append('g').Append(g.ToString("D", CultureInfo.InvariantCulture)); break;
            case byte[] bytes: text.Append('x').Append(System.Convert.ToHexString(bytes)); break;
            case char c: text.Append('c').Append((int)c); break;
            default: text.Append('i').Append(System.Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }

        text.Append('\u001f');
    }

    /// <summary>PostgreSQL: <c>setval</c> every identity or serial sequence of a single-column key past the copied maximum (inside the target transaction).</summary>
    private static async Task<int> ResetPostgresSequencesAsync(
        DbConnection connection, DbTransaction transaction, ISqlGenerationHelper sql, IReadOnlyList<CopyTable> tables, CancellationToken ct)
    {
        int set = 0;
        foreach (CopyTable table in tables.Where(t => t.PrimaryKey.Count == 1))
        {
            string column = table.PrimaryKey[0];
            string quotedTable = sql.DelimitIdentifier(table.Name);
            string quotedColumn = sql.DelimitIdentifier(column);

            // Asked first and alone: only an identity or serial column has a sequence, and MAX of a text key must not be parsed into setval.
            await using DbCommand find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = "SELECT pg_get_serial_sequence(@table, @column)";
            AddParameter(find, "@table", quotedTable);
            AddParameter(find, "@column", column);
            if (await find.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string sequence)
            {
                continue;
            }

            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT setval(@sequence::regclass, COALESCE((SELECT MAX({quotedColumn}) FROM {quotedTable}), 1), (SELECT MAX({quotedColumn}) FROM {quotedTable}) IS NOT NULL)";
            AddParameter(command, "@sequence", sequence);
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            set++;
        }

        return set;
    }

    /// <summary>MariaDB/MySQL: <c>AUTO_INCREMENT</c> past the copied maximum for every single-column auto-increment key. DDL, so it commits implicitly and runs after the copy's commit.</summary>
    private static async Task<int> ResetAutoIncrementAsync(DbContext target, IReadOnlyList<CopyTable> tables, CancellationToken ct)
    {
        ISqlGenerationHelper sql = target.GetService<ISqlGenerationHelper>();
        int set = 0;
        await target.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            DbConnection connection = target.Database.GetDbConnection();
            foreach (CopyTable table in tables.Where(t => t.PrimaryKey.Count == 1))
            {
                string column = table.PrimaryKey[0];
                string quotedTable = sql.DelimitIdentifier(table.Name);
                string quotedColumn = sql.DelimitIdentifier(column);
                await using DbCommand probe = connection.CreateCommand();
                probe.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @table " +
                    "AND column_name = @column AND extra LIKE '%auto_increment%'";
                AddParameter(probe, "@table", table.Name);
                AddParameter(probe, "@column", column);
                if (System.Convert.ToInt64(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                {
                    continue;
                }

                await using DbCommand max = connection.CreateCommand();
                max.CommandText = $"SELECT COALESCE(MAX({quotedColumn}), 0) + 1 FROM {quotedTable}";
                object? next = await max.ExecuteScalarAsync(ct).ConfigureAwait(false);
                await using DbCommand alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {quotedTable} AUTO_INCREMENT = {System.Convert.ToUInt64(next, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                set++;
            }
        }
        finally
        {
            await target.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return set;
    }
}
