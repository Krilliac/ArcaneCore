using ArcaneCore.Data.Content;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.World.Tests.RealContent;

/// <summary>
/// Guards the real-content facts that run against the world.db named by <c>ARCANECORE_TEST_WORLD_DB</c>. Those tests open or copy the file
/// without applying the world schema upgrade, so a fixture older than the code's <see cref="WorldDbContext.Schema"/> fails with a raw
/// <c>SqliteException: no such column</c>, indistinguishable from a regression. This reads <c>world_schema.Version</c> read-only and
/// returns a Skip message instead. The fixture is never written or upgraded; a newer fixture is not skipped here.
/// </summary>
internal static class WorldFixtureSchema
{
    public const string Variable = "ARCANECORE_TEST_WORLD_DB";

    /// <summary>The skip message for the fixture named by <see cref="Variable"/>, or null when it is usable (or unset/missing: other gates report that).</summary>
    public static string? SkipReasonForEnvironment()
        => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path && File.Exists(path)
            ? SkipReason(path, WorldDbContext.Schema.CurrentVersion)
            : null;

    /// <summary>The skip message when the SQLite file at <paramref name="path"/> is older than <paramref name="codeVersion"/>, has no readable world_schema, or null when usable.</summary>
    public static string? SkipReason(string path, int codeVersion)
    {
        int? version;
        try
        {
            version = ReadVersion(path);
        }
        catch (SqliteException ex)
        {
            return $"{Variable} ({path}) has no readable world_schema table ({ex.Message}); refresh the fixture from a live world.db.";
        }

        if (version is null)
            return $"{Variable} ({path}) has no world_schema version row; refresh the fixture from a live world.db.";

        return version.Value < codeVersion
            ? $"{Variable} schema {version.Value} is older than the code's world schema {codeVersion}; refresh the fixture from a live world.db."
            : null;
    }

    private static int? ReadVersion(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Version) FROM world_schema";
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
