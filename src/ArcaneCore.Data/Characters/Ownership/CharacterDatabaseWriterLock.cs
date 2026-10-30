using Microsoft.Data.Sqlite;

namespace ArcaneCore.Data.Characters.Ownership;

/// <summary>
/// Another world process already holds the exclusive writer lock of this characters database (a second world started on the same file while the first is alive).
/// </summary>
public sealed class CharacterDatabaseAlreadyOwnedException(string lockPath, Exception? inner = null)
    : InvalidOperationException($"the characters database is already owned by another world process (could not take the exclusive writer lock '{lockPath}')", inner)
{
    public string LockPath { get; } = lockPath;
}

/// <summary>
/// The single-writer guard of a file-backed SQLite characters database: a sidecar <c>&lt;database&gt;.world.lock</c> opened with
/// <see cref="FileShare.None"/> and held for the life of the world process. The operating system releases it when the process exits or crashes, so there is no lease and no stale
/// lock to clean up. It works whether or not <c>Cluster:Fencing</c> is on, which the durable ownership row alone cannot do (an unfenced world refuses only a fenced owner).
/// MariaDB, MySQL and PostgreSQL get the same process-lifetime exclusivity from a session-level database advisory lock on a dedicated connection (<see cref="ServerAdvisoryWriterLock"/>);
/// in-memory, read-only and <c>file:</c> URI SQLite databases have no lock (the durable fencing claim is their guard).
/// Owned by the world host that took it; released once, by <see cref="Dispose"/>, from any thread.
/// </summary>
public sealed class CharacterDatabaseWriterLock : IDisposable
{
    public const string Suffix = ".world.lock";

    private IDisposable? _holder;

    private CharacterDatabaseWriterLock(string path, IDisposable holder)
    {
        Path = path;
        _holder = holder;
    }

    /// <summary>The lock file's full path (SQLite), or the advisory lock's identity (MariaDB, MySQL, PostgreSQL).</summary>
    public string Path { get; }

    /// <summary>
    /// The lock file path for a resolved characters component, or null when the database is not a plain file-backed read-write SQLite database
    /// (or its directory does not exist, in which case SQLite cannot open it either).
    /// </summary>
    public static string? LockPathFor(DatabaseConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Provider != DatabaseProvider.Sqlite || string.IsNullOrWhiteSpace(options.ConnectionString)) return null;
        SqliteConnectionStringBuilder builder;
        try { builder = new SqliteConnectionStringBuilder(options.ConnectionString); }
        catch (ArgumentException) { return null; }

        string source = builder.DataSource;
        if (string.IsNullOrWhiteSpace(source) || source == ":memory:" || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly)
            return null;
        string full = System.IO.Path.GetFullPath(source);
        try
        {
            // A database file reached through a file symlink locks the same sidecar as its target. Directory symlinks, bind mounts and subst drives are not resolved.
            if (new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target) full = target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        string? directory = System.IO.Path.GetDirectoryName(full);
        return directory is not null && Directory.Exists(directory) ? full + Suffix : null;
    }

    /// <summary>
    /// Takes the lock for <paramref name="options"/>; null when the database has none (see <see cref="LockPathFor"/>; a server provider with no database name in its connection string).
    /// Throws <see cref="CharacterDatabaseAlreadyOwnedException"/> when another process (or another lock object in this one) holds it. A server database that cannot be reached
    /// propagates the driver's <see cref="System.Data.Common.DbException"/>.
    /// </summary>
    public static CharacterDatabaseWriterLock? TryAcquire(DatabaseConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Provider != DatabaseProvider.Sqlite)
        {
            if (ServerAdvisoryWriterLock.DatabaseNameOf(options) is not { } name) return null;
            ServerAdvisoryWriterLock advisory = ServerAdvisoryWriterLock.TryAcquire(options, name);
            return new CharacterDatabaseWriterLock(advisory.Description, advisory);
        }

        if (LockPathFor(options) is not { } path) return null;
        try
        {
            return new CharacterDatabaseWriterLock(path, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new CharacterDatabaseAlreadyOwnedException(path, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not a contended lock (disk full, permissions, a read-only directory): say so instead of claiming another world owns the database.
            throw new IOException($"could not create the characters database writer lock '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Whether the open failed because another holder has the lock. Windows: ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33) in the low word of the HResult.
    /// Unix: .NET emulates <see cref="FileShare.None"/> with a non-blocking flock and a refusal arrives as an <see cref="IOException"/> whose HResult is the errno
    /// (EWOULDBLOCK = EAGAIN: 11 on Linux, 35 on macOS and the BSDs).
    /// </summary>
    internal static bool IsSharingViolation(IOException ex)
    {
        if (OperatingSystem.IsWindows()) return (ex.HResult & 0xFFFF) is 32 or 33;
        if (OperatingSystem.IsLinux()) return ex.HResult == 11;
        return (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) && ex.HResult == 35;
    }

    public void Dispose() => Interlocked.Exchange(ref _holder, null)?.Dispose();
}
