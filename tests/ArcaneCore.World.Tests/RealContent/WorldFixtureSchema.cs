using ArcaneCore.Data.Content;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.World.Tests.RealContent;

/// <summary>
/// Guards the real-content facts that run against the world.db named by <c>ARCANECORE_TEST_WORLD_DB</c>. Those tests open or copy the file
/// without applying the world schema upgrade, so a fixture older than the code's <see cref="WorldDbContext.Schema"/> fails with a raw
/// <c>SqliteException: no such column</c>, indistinguishable from a regression. This reads <c>world_schema.Version</c> and returns a Skip
/// message instead. The fixture is never written or upgraded and no sidecar file is created next to it (the file is opened with SQLite's
/// <c>immutable=1</c> URI parameter, which takes no locks and so creates no -wal/-shm even for a WAL-mode database). A newer fixture is
/// not skipped. It runs in attribute constructors during test discovery, so it never throws and never waits on a lock.
/// </summary>
internal static class WorldFixtureSchema
{
    public const string Variable = "ARCANECORE_TEST_WORLD_DB";

    private const string Refresh = "refresh the fixture from a live world.db";

    /// <summary>The skip message for the fixture named by <see cref="Variable"/>, or null when it is usable (or unset/missing: other gates report that).</summary>
    public static string? SkipReasonForEnvironment()
        => SkipReasonForPath(Environment.GetEnvironmentVariable(Variable), WorldDbContext.Schema.CurrentVersion);

    /// <summary>As <see cref="SkipReasonForEnvironment"/> for an explicit path; null (no skip) when the path is unset or the file does not exist.</summary>
    public static string? SkipReasonForPath(string? path, int codeVersion)
        => path is { Length: > 0 } && File.Exists(path) ? SkipReason(path, codeVersion) : null;

    /// <summary>The skip message when the SQLite file at <paramref name="path"/> is older than <paramref name="codeVersion"/>, is unreadable, or null when usable.</summary>
    public static string? SkipReason(string path, int codeVersion)
    {
        int? version;
        try
        {
            version = ReadVersion(path);
        }
        catch (Exception ex)
        {
            return $"{Variable} ({path}) has no readable world_schema table ({ex.Message}); {Refresh}.";
        }

        if (version is null)
            return $"{Variable} ({path}) has no world_schema version row; {Refresh}.";

        if (version.Value <= 0)
            return $"{Variable} ({path}) is still being created (world_schema version {version.Value}); {Refresh}.";

        return version.Value < codeVersion
            ? $"{Variable} schema {version.Value} is older than the code's world schema {codeVersion}; {Refresh}."
            : null;
    }

    private static int? ReadVersion(string path)
    {
        string full = Path.GetFullPath(path);
        // A non-empty -wal may hold the newest version row, which an immutable open ignores; its sidecars already exist, so a plain
        // read-only open creates nothing. A UNC path cannot be written as a file: URI here, so it also takes the plain open.
        bool plain = full.StartsWith("\\\\", StringComparison.Ordinal) || (File.Exists(full + "-wal") && new FileInfo(full + "-wal").Length > 0);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = plain ? full : new Uri(full).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Version) FROM world_schema";
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
