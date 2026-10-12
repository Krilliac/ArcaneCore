using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// Names the single command that waited too long on the database. <see cref="SlowTransactionInterceptor"/> only sees transactions EF began, so a
/// write that autocommits (one-command <c>SaveChanges</c>, <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> outside a transaction, raw SQL) that sat in SQLite's
/// busy handler for seconds, or a command that held the write lock that long, was invisible. When a command's execution takes at least
/// the threshold (<c>Database:SlowCommandWarnMs</c>, default <see cref="DefaultWarnMs"/>, 0 disables) this logs one WARN with the <see cref="DbContext"/>
/// type, the duration, whether it ran inside a transaction or autocommitted, whether it was a write or a read, the first
/// <see cref="SqlPrefixLength"/> characters of the SQL and a caller hint (<see cref="SlowTransactionInterceptor.CallerHint"/>).
/// <para>
/// The duration is EF's own measurement of the execute call (<see cref="CommandEndEventData.Duration"/>), so this type stamps nothing per command and its own
/// check is one comparison; only an over-threshold command formats a message and captures a stack trace. Registering any command interceptor is not free
/// for EF itself, though: it no longer skips building command event data (its logging-suppression cache is off while an <c>IDbCommandInterceptor</c> is
/// present), so EF allocates that data for every command on every database, whatever the threshold. For a reader that is the time until
/// <c>ExecuteReader</c> returns (SQLite steps the first row there, so a lock wait is inside it), not the time spent reading rows. A failed command is
/// reported too (a busy-timeout failure is exactly the case wanted). Parameter values are never read: only <see cref="DbCommand.CommandText"/> is
/// logged, and since a caller could still have concatenated a value into it, the printed prefix masks single- and double-quoted text (a backslash escapes the next
/// character inside quotes, which over-masks standard-SQL strings rather than leak a MariaDB or <c>E'..'</c> one) and numeric literals as <c>?</c>, and drops
/// comments. A double-quoted name is kept only straight after FROM, INTO, UPDATE, JOIN or TABLE, where it can only be a table name (EF quotes those with
/// double quotes on SQLite and PostgreSQL); every other double-quoted name, e.g. a column, prints as <c>"?"</c>.
/// </para>
/// <para>
/// Identical warnings (same caller and same SQL prefix) are limited to one per <see cref="WindowSeconds"/> seconds; the next one that is let through
/// states how many were suppressed since the previous warning for that caller and SQL, so a lock storm cannot flood the log. The limiter is a small lock-protected table, touched only on the over-threshold path,
/// pruned when it grows. Immutable threshold and logger plus that table; thread-safe; one instance per host (a singleton, like the transaction interceptor).
/// Applies to every provider.
/// </para>
/// </summary>
public sealed class SlowCommandInterceptor : DbCommandInterceptor
{
    /// <summary>The shipped threshold (<c>Database:SlowCommandWarnMs</c>).</summary>
    public const int DefaultWarnMs = 2000;

    /// <summary>How much of the SQL is logged.</summary>
    public const int SqlPrefixLength = 80;

    /// <summary>Identical warnings are let through at most this often.</summary>
    public const int WindowSeconds = 10;

    private const int MaxTrackedKeys = 256;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Limit> _limits = new(StringComparer.Ordinal);

    public SlowCommandInterceptor(int thresholdMs, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ThresholdMs = Math.Max(0, thresholdMs);
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The configured threshold in milliseconds; 0 means never warn.</summary>
    public int ThresholdMs { get; }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(command, eventData, failed: false);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData, failed: false);
        return new ValueTask<DbDataReader>(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Check(command, eventData, failed: false);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData, failed: false);
        return new ValueTask<int>(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Check(command, eventData, failed: false);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData, failed: false);
        return new ValueTask<object?>(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Check(command, eventData, failed: true);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Check(command, eventData, failed: true);
        return Task.CompletedTask;
    }

    private void Check(DbCommand command, CommandEndEventData eventData, bool failed)
    {
        if (ThresholdMs <= 0 || eventData.Duration.TotalMilliseconds < ThresholdMs)
        {
            return;
        }

        string sql = command.CommandText ?? string.Empty;
        string prefix = SqlPrefix(sql, out string kind);
        string caller = SlowTransactionInterceptor.CallerHint();
        int suppressed = Admit(caller + "|" + prefix);
        if (suppressed < 0)
        {
            return;
        }

        _logger.LogWarning(
            "slow database command on {Context}: {DurationMs} ms ({Outcome}; {Mode}; {Kind}; warns at {ThresholdMs} ms): {Sql}. The wait is inside the database (a held write lock or slow I/O), so other writers may have failed as locked. Called from {Caller}. {Suppressed} identical warnings suppressed since the previous warning for this caller",
            eventData.Context?.GetType().Name ?? "(unknown context)", (long)eventData.Duration.TotalMilliseconds, failed ? "failed" : "completed",
            command.Transaction is null ? "autocommit, no transaction" : "inside a transaction", kind, ThresholdMs, prefix, caller, suppressed);
    }

    /// <summary>-1 when an identical warning was logged within the window (this one is counted and dropped); otherwise how many were dropped since the last one.</summary>
    private int Admit(string key)
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_limits.TryGetValue(key, out Limit? limit))
            {
                if (_time.GetElapsedTime(limit.LastTimestamp, now).TotalSeconds < WindowSeconds)
                {
                    limit.Suppressed++;
                    return -1;
                }

                int dropped = limit.Suppressed;
                limit.LastTimestamp = now;
                limit.Suppressed = 0;
                return dropped;
            }

            if (_limits.Count >= MaxTrackedKeys)
            {
                Prune(now);
            }

            _limits[key] = new Limit { LastTimestamp = now };
            return 0;
        }
    }

    /// <summary>Drops entries whose window has passed and that owe no suppressed count; if that frees nothing, forgets everything (a bounded, lossy table beats unbounded growth).</summary>
    private void Prune(long now)
    {
        List<string> expired = [];
        foreach (KeyValuePair<string, Limit> pair in _limits)
        {
            if (pair.Value.Suppressed == 0 && _time.GetElapsedTime(pair.Value.LastTimestamp, now).TotalSeconds >= WindowSeconds)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (string key in expired)
        {
            _limits.Remove(key);
        }

        if (_limits.Count >= MaxTrackedKeys)
        {
            _limits.Clear();
        }
    }

    /// <summary>
    /// The first <see cref="SqlPrefixLength"/> characters of the SQL with comments dropped, whitespace collapsed, quoted text and numeric literals masked as <c>?</c>
    /// (see the class remarks for the quoting rules); <paramref name="kind"/> is "write" (INSERT, UPDATE, DELETE, REPLACE, also behind a WITH clause), "read" (SELECT,
    /// also behind a WITH clause) or "other".
    /// </summary>
    internal static string SqlPrefix(string sql, out string kind)
    {
        int i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
            }
            else
            {
                break;
            }
        }

        kind = Classify(sql, i);
        var text = new StringBuilder(SqlPrefixLength + 8);
        string lastWord = string.Empty;
        bool pendingSpace = false;
        bool truncated = false;
        for (; i < sql.Length; i++)
        {
            if (text.Length >= SqlPrefixLength)
            {
                truncated = true;
                break;
            }

            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline;
                pendingSpace = text.Length > 0;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 1;
                pendingSpace = text.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            if (c == '\'')
            {
                i = SkipQuoted(sql, i);
                text.Append("'?'");
                lastWord = string.Empty;
            }
            else if (c == '"')
            {
                int end = SkipQuoted(sql, i);
                if (IsTablePosition(lastWord) && IsPlainName(sql, i + 1, end))
                {
                    text.Append(sql, i, end - i + 1);
                }
                else
                {
                    text.Append("\"?\"");
                }

                i = end;
                lastWord = string.Empty;
            }
            else if (IsNumberStart(sql, i))
            {
                while (i + 1 < sql.Length && (char.IsAsciiLetterOrDigit(sql[i + 1]) || sql[i + 1] == '.'))
                {
                    i++;
                }

                text.Append('?');
                lastWord = string.Empty;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i + 1 < sql.Length && (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    i++;
                }

                int take = Math.Min(i - start + 1, Math.Max(0, SqlPrefixLength - text.Length));
                text.Append(sql, start, take);
                truncated |= take < i - start + 1;
                lastWord = sql.Substring(start, i - start + 1).ToUpperInvariant();
            }
            else
            {
                text.Append(c);
                lastWord = string.Empty;
            }
        }

        if (truncated)
        {
            text.Append("...");
        }

        return text.ToString();
    }

    /// <summary>
    /// The index of the closing quote of the quoted text that opens at <paramref name="open"/> (the quote character), or the last index when unterminated.
    /// A doubled quote is one character of text; a backslash escapes the next character (MariaDB, PostgreSQL <c>E'..'</c>), so a standard-SQL string ending in a
    /// backslash over-masks what follows instead of leaking.
    /// </summary>
    private static int SkipQuoted(string sql, int open)
    {
        char quote = sql[open];
        int i = open + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '\\')
            {
                i += 2;
            }
            else if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                }
                else
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return sql.Length - 1;
    }

    private static bool IsTablePosition(string lastWord) => lastWord is "FROM" or "INTO" or "UPDATE" or "JOIN" or "TABLE";

    /// <summary>Whether sql[from..closingQuote) is a plain name (letters, digits, underscore) and the quote really closes.</summary>
    private static bool IsPlainName(string sql, int from, int closingQuote)
    {
        if (closingQuote <= from || closingQuote >= sql.Length || sql[closingQuote] != '"')
        {
            return false;
        }

        for (int k = from; k < closingQuote; k++)
        {
            if (!(char.IsAsciiLetterOrDigit(sql[k]) || sql[k] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A digit that does not continue a name, or a '.' followed by a digit that does not continue one (".5").</summary>
    private static bool IsNumberStart(string sql, int i)
    {
        char c = sql[i];
        if (char.IsAsciiDigit(c))
        {
            return i == 0 || !IsIdentifierPart(sql[i - 1]);
        }

        return c == '.' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]) && (i == 0 || !(IsIdentifierPart(sql[i - 1]) && sql[i - 1] != '.'));
    }

    /// <summary>Only the classification of <see cref="SqlPrefix"/>.</summary>
    internal static string SqlPrefixKind(string sql)
    {
        _ = SqlPrefix(sql, out string kind);
        return kind;
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '$' or ':' or '.' or '"' or '`' or '[';

    private static string Classify(string sql, int start)
    {
        ReadOnlySpan<char> rest = sql.AsSpan(start);
        if (StartsWithWord(rest, "WITH"))
        {
            return ClassifyAfterWith(sql, start);
        }

        return KindOf(rest);
    }

    private static string KindOf(ReadOnlySpan<char> rest)
    {
        if (StartsWithWord(rest, "SELECT"))
        {
            return "read";
        }

        return StartsWithWord(rest, "INSERT") || StartsWithWord(rest, "UPDATE") || StartsWithWord(rest, "DELETE") || StartsWithWord(rest, "REPLACE")
            ? "write"
            : "other";
    }

    /// <summary>
    /// For <c>WITH name AS (...), ... main-statement</c>: the kind of the first statement keyword outside every parenthesis and quote (the CTE bodies are skipped), or
    /// "other" when none is found.
    /// </summary>
    private static string ClassifyAfterWith(string sql, int start)
    {
        int depth = 0;
        for (int i = start; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i);
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 1;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth == 0 && char.IsLetter(c) && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_')))
            {
                string kind = KindOf(sql.AsSpan(i));
                if (kind != "other")
                {
                    return kind;
                }
            }
        }

        return "other";
    }

    private static bool StartsWithWord(ReadOnlySpan<char> text, string word)
        => text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (text.Length == word.Length || !(char.IsLetterOrDigit(text[word.Length]) || text[word.Length] == '_'));

    private sealed class Limit
    {
        public long LastTimestamp { get; set; }

        public int Suppressed { get; set; }
    }
}
