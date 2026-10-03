using System.Globalization;
using ArcaneCore.Data.Content.Import;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>A backup could not be made or described.</summary>
public sealed class BackupException(string message) : Exception(message);

/// <summary>
/// How to back a database up before an upgrade. No external process is ever started and no
/// password is ever put into text: a MariaDB/MySQL or PostgreSQL backup is a command line for the
/// operator to run, with the password named as an environment variable to set (the way
/// classic-db's InstallFullDB.sh passes MYSQL_PWD, D:\refs\classic-db\InstallFullDB.sh:2196-2248,
/// and runs <c>mysqldump --quick --single-transaction --order-by-primary</c>). A SQLite database
/// is copied with <c>VACUUM INTO</c>, which writes a consistent snapshot even while the file is in use.
/// </summary>
public static class BackupAdvisor
{
    /// <summary>The operator instructions for one server database.</summary>
    /// <param name="Provider">The engine.</param>
    /// <param name="Host">Server host (null for SQLite).</param>
    /// <param name="Port">Server port, null when the connection string leaves the default.</param>
    /// <param name="Database">Database name, or the SQLite file path.</param>
    /// <param name="Command">The command line to run (no secret in it); null for SQLite, which this tool copies itself.</param>
    /// <param name="PasswordVariable">The environment variable to set to the database password before running <see cref="Command"/>; null when none.</param>
    /// <param name="Note">What to know about the command.</param>
    public sealed record BackupInstructions(
        DatabaseProvider Provider, string? Host, int? Port, string Database, string? Command, string? PasswordVariable, string Note);

    /// <summary>Describe how to back up the database <paramref name="connection"/> points at.</summary>
    public static BackupInstructions Describe(DatabaseConnectionOptions connection, string? destinationFile = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        switch (connection.Provider)
        {
            case DatabaseProvider.MariaDb or DatabaseProvider.MySql:
            {
                var builder = new MySqlConnectionStringBuilder(connection.ConnectionString);
                string database = builder.Database;
                string file = destinationFile ?? $"{Sanitize(database)}-{stamp}.sql";
                int? port = builder.Port == 3306 ? null : (int)builder.Port;
                string command = "mysqldump" +
                    $" --host={Quote(builder.Server)}" +
                    (port is null ? string.Empty : $" --port={port}") +
                    $" --user={Quote(builder.UserID)}" +
                    " --single-transaction --quick --order-by-primary --default-character-set=utf8mb4" +
                    $" --result-file={Quote(file)} {Quote(database)}";
                return new BackupInstructions(
                    connection.Provider, builder.Server, port ?? 3306, database, command, "MYSQL_PWD",
                    "Set MYSQL_PWD to the database password in the shell first (it is never shown here). --single-transaction gives a consistent InnoDB snapshot without locking.");
            }

            case DatabaseProvider.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
                string database = builder.Database ?? string.Empty;
                string file = destinationFile ?? $"{Sanitize(database)}-{stamp}.dump";
                int? port = builder.Port == 5432 ? null : builder.Port;
                string command = "pg_dump" +
                    $" --host={Quote(builder.Host ?? string.Empty)}" +
                    (port is null ? string.Empty : $" --port={port}") +
                    $" --username={Quote(builder.Username ?? string.Empty)}" +
                    $" --format=custom --file={Quote(file)} {Quote(database)}";
                return new BackupInstructions(
                    connection.Provider, builder.Host, port ?? 5432, database, command, "PGPASSWORD",
                    "Set PGPASSWORD to the database password in the shell first (it is never shown here). The custom format restores with pg_restore.");
            }

            case DatabaseProvider.Sqlite:
                return new BackupInstructions(
                    connection.Provider, null, null, new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource, null, null,
                    "arcane-db backup writes a consistent copy (VACUUM INTO) into the directory given with --backup-dir.");

            default:
                throw new ArgumentOutOfRangeException(nameof(connection));
        }
    }

    /// <summary>
    /// Write a consistent copy of a SQLite database file into <paramref name="directory"/> (created if missing)
    /// and verify it. Refuses a directory inside a git work tree that is not git-ignored (player data must
    /// not land in a repository; <see cref="RepositoryPathGuard"/>), a missing or in-memory database
    /// (an empty backup would be a lie), and an existing destination (never overwrites).
    /// </summary>
    /// <returns>The full path of the backup file.</returns>
    public static async Task<string> BackupSqliteAsync(
        DatabaseConnectionOptions connection, string directory, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (connection.Provider != DatabaseProvider.Sqlite)
        {
            throw new BackupException("only SQLite databases are copied by this tool; run the command from the backup instructions for other engines");
        }

        var source = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (source.Mode == SqliteOpenMode.Memory || source.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(source.DataSource))
        {
            throw new BackupException("an in-memory SQLite database cannot be backed up");
        }

        string sourcePath = Path.GetFullPath(source.DataSource);
        if (!File.Exists(sourcePath))
        {
            throw new BackupException($"the SQLite database file '{sourcePath}' does not exist; nothing to back up");
        }

        string name = fileName ?? $"{Sanitize(Path.GetFileNameWithoutExtension(sourcePath))}-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.db";
        if (name != Path.GetFileName(name))
        {
            throw new BackupException("the backup file name must not contain a directory");
        }

        string destination = Path.Combine(Path.GetFullPath(directory), name);
        string? refused = RepositoryPathGuard.Check(destination);
        if (refused is not null)
        {
            throw new BackupException(refused);
        }

        if (File.Exists(destination))
        {
            throw new BackupException($"'{destination}' already exists; a backup is never overwritten");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            var readOnly = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using (var db = new SqliteConnection(readOnly.ConnectionString))
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using SqliteCommand command = db.CreateCommand();
                command.CommandText = "VACUUM INTO @path";
                command.Parameters.AddWithValue("@path", destination);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var copy = new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using (var db = new SqliteConnection(copy.ConnectionString))
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using SqliteCommand check = db.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                string? verdict = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BackupException($"the backup '{destination}' failed its integrity check: {verdict}");
                }
            }
        }
        catch (SqliteException ex)
        {
            TryDelete(destination);
            throw new BackupException(
                ex.SqliteErrorCode is 5 or 6
                    ? $"the database is locked by another process (SQLite busy): {ex.Message}. Stop the daemons and try again"
                    : $"the SQLite backup failed: {ex.Message}");
        }
        catch (BackupException)
        {
            TryDelete(destination);
            throw;
        }

        return destination;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort: a partial backup file is removed so it is not mistaken for a good one
        }
    }

    private static string Sanitize(string name)
        => string.IsNullOrWhiteSpace(name) ? "database" : new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);

    /// <summary>Quote a command-line value only when it needs it; the password never passes through here.</summary>
    private static string Quote(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '/' or '\\' or '@')
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
