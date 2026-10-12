using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArcaneCore.Data.Stores;

/// <summary>A write reached the characters database outside a transaction while the ownership write fence is on.</summary>
public sealed class CharacterWriteFenceException(string message) : InvalidOperationException(message);

/// <summary>
/// Fail-closed half of the write fence. The transaction interceptor can only verify a transaction EF begins, so a bare
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/raw INSERT/UPDATE/DELETE (autocommitted, no transaction, no verification) would slip through
/// unfenced; with the fence on this refuses it before it reaches the database. A statement inside a transaction passes (the transaction was
/// verified when it began), as does one under an ambient <see cref="System.Transactions.Transaction"/> (its caller owns the verification,
/// like <c>EfCharacterStore</c>), and code marked <see cref="CharacterWriteFence.Exempt"/>. Reads are never refused. Stateless; thread-safe; the
/// per-command cost is one scan of the first few characters of the SQL.
/// </summary>
public sealed class CharacterWriteFenceCommandInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return new(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return new(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return new(result);
    }

    private static void Check(DbCommand command)
    {
        if (command.Transaction is not null || CharacterWriteFence.IsExempt || !IsWrite(command.CommandText)
            || System.Transactions.Transaction.Current is not null)
        {
            return;
        }

        throw new CharacterWriteFenceException(
            "a characters-database write ran outside a transaction while Cluster:Fencing:Enabled is on; "
            + "it cannot be verified against the ownership token. Run it inside a transaction.");
    }

    /// <summary>Whether the statement's first keyword (after whitespace and comments, such as EF query tags) is INSERT, UPDATE, DELETE or REPLACE.</summary>
    internal static bool IsWrite(string sql)
    {
        ReadOnlySpan<char> text = sql;
        while (true)
        {
            text = text.TrimStart();
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                int end = text.IndexOf('\n');
                if (end < 0)
                    return false;
                text = text[(end + 1)..];
            }
            else if (text.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = text.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                    return false;
                text = text[(end + 2)..];
            }
            else
            {
                break;
            }
        }

        return text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
    }
}
