using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// Process-local ownership for SQLite quest-reward writers. SQLite has one file writer; this
/// coordinator covers this reward store and normalized absolute file paths within this process.
/// URI, memory, hard-link and symbolic-link aliases are outside this ownership boundary.
/// </summary>
internal static class SqliteRewardWriterCoordinator
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(PathComparer);

    internal static bool TryGetKey(DbContext db, out string key)
    {
        // URI-style SQLite data sources and private in-memory modes are intentionally bypassed:
        // this coordinator cannot safely prove whether two URI names alias the same database.
        key = string.Empty;
        if (!db.Database.IsSqlite() || db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            return false;
        }

        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (builder.Mode == SqliteOpenMode.Memory || string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        key = Path.GetFullPath(builder.DataSource);
        return true;
    }

    internal static async ValueTask<Lease> AcquireAsync(DbContext db, CancellationToken cancellationToken)
    {
        if (!TryGetKey(db, out string key))
        {
            return Lease.Noop;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry = Entries.GetOrAdd(key, static value => new Entry(value));
            bool acquired;
            lock (entry.Sync)
            {
                acquired = !entry.Retired;
                if (acquired)
                {
                    entry.References++;
                }
            }

            if (!acquired)
            {
                continue;
            }

            try
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Lease(entry, ownsGate: true);
            }
            catch
            {
                ReleaseReference(entry);
                throw;
            }
        }
    }

    internal sealed class Lease : IAsyncDisposable
    {
        internal static Lease Noop { get; } = new(null, ownsGate: false);
        private Entry? _entry;
        private readonly bool _ownsGate;

        internal Lease(Entry? entry, bool ownsGate)
        {
            _entry = entry;
            _ownsGate = ownsGate;
        }

        public ValueTask DisposeAsync()
        {
            Entry? entry = Interlocked.Exchange(ref _entry, null);
            if (!_ownsGate || entry is null)
            {
                return ValueTask.CompletedTask;
            }

            entry.Gate.Release();
            ReleaseReference(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static void ReleaseReference(Entry entry)
    {
        bool retire;
        lock (entry.Sync)
        {
            retire = --entry.References == 0;
            if (retire)
            {
                entry.Retired = true;
            }
        }

        if (retire)
        {
            Entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry));
            entry.Gate.Dispose();
        }
    }

    internal sealed class Entry(string key)
    {
        internal string Key { get; } = key;
        internal object Sync { get; } = new();
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int References;
        internal bool Retired;
    }
}
