using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>The database server as it identifies itself, and whether ArcaneCore's CI qualifies it.</summary>
/// <param name="Product">SQLite, MariaDB, MySQL or PostgreSQL.</param>
/// <param name="Version">The server's own version text.</param>
/// <param name="Qualified">Whether this engine and version is one the continuous-integration suite runs against.</param>
/// <param name="Warning">What to know when it is not qualified; null when it is.</param>
public sealed record ServerInfo(string Product, string Version, bool Qualified, string? Warning);

/// <summary>
/// Read-only questions about the server an upgrade is about to touch: what it is, who else is
/// connected, and whether another process holds the schema lock. Best effort by nature: sessions of
/// other roles can be invisible without the PROCESS / pg_read_all_stats privilege, and SQLite
/// cannot answer either question beyond its write lock (the answers are then null).
/// The MariaDB and PostgreSQL queries are written from the providers' documentation and are proven
/// only by hosted CI (this repository's local test runs have no database server).
/// </summary>
public static partial class ServerProbe
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string PomeloProvider = "Pomelo.EntityFrameworkCore.MySql";
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>The qualified server lines: what the CI suite runs (.github/workflows/ci.yml: mariadb:10.11, postgres:16; SQLite always).</summary>
    public const string QualifiedSummary = "MariaDB 10.11 or newer, PostgreSQL 16 or newer, SQLite";

    /// <summary>Ask the server what it is.</summary>
    public static async Task<ServerInfo> ReadServerInfoAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        string provider = db.Database.ProviderName ?? string.Empty;
        string sql = provider switch
        {
            SqliteProvider => "SELECT sqlite_version()",
            PomeloProvider => "SELECT VERSION()",
            NpgsqlProvider => "SELECT current_setting('server_version')",
            _ => throw SchemaCatalog.Unsupported(db),
        };

        string version = string.Empty;
        await foreach (object?[] row in SchemaCatalog.RowsAsync(db, sql, cancellationToken).ConfigureAwait(false))
        {
            version = SchemaCatalog.Text(row[0]);
        }

        return Qualify(provider, version);
    }

    /// <summary>Classify a server from its provider and version text. Pure.</summary>
    public static ServerInfo Qualify(string providerName, string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        switch (providerName)
        {
            case SqliteProvider:
                return new ServerInfo("SQLite", version, true, null);

            case PomeloProvider:
                (int major, int minor) = Parse(version);
                if (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = major > 10 || (major == 10 && minor >= 11);
                    return new ServerInfo("MariaDB", version, ok,
                        ok ? null : $"MariaDB {version} is older than 10.11, the version CI qualifies ({QualifiedSummary}); upgrade at your own risk");
                }

                return new ServerInfo("MySQL", version, false,
                    $"MySQL {version} is supported but not qualified: CI runs MariaDB 10.11, not MySQL ({QualifiedSummary}); take the backup seriously");

            case NpgsqlProvider:
                (int pgMajor, _) = Parse(version);
                bool pgOk = pgMajor >= 16;
                return new ServerInfo("PostgreSQL", version, pgOk,
                    pgOk ? null : $"PostgreSQL {version} is older than 16, the version CI qualifies ({QualifiedSummary}); upgrade at your own risk");

            default:
                return new ServerInfo(providerName, version, false, $"provider {providerName} is not one ArcaneCore supports");
        }
    }

    /// <summary>
    /// How many sessions other than this one are connected to the same database; null when the engine cannot
    /// say (SQLite) or the role may not see them. A tool run with pooling disabled counts only real other processes.
    /// </summary>
    public static async Task<int?> CountOtherSessionsAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        string? sql = db.Database.ProviderName switch
        {
            PomeloProvider => "SELECT COUNT(*) FROM information_schema.processlist WHERE db = DATABASE() AND id <> CONNECTION_ID()",
            NpgsqlProvider => "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND backend_type = 'client backend'",
            SqliteProvider => null,
            _ => throw SchemaCatalog.Unsupported(db),
        };

        return sql is null ? null : (int)await SchemaCatalog.ScalarAsync(db, sql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether another session currently holds the component's schema lock (MariaDB <c>IS_USED_LOCK</c>, PostgreSQL
    /// <c>pg_locks</c>); null on SQLite, whose lock is the file's write lock and is not observable without taking it.
    /// </summary>
    public static async Task<bool?> IsSchemaLockHeldAsync(DbContext db, string component, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(component);
        switch (db.Database.ProviderName)
        {
            case PomeloProvider:
                return await SchemaCatalog.ScalarAsync(
                    db,
                    "SELECT CASE WHEN IS_USED_LOCK(SHA1(CONCAT(@prefix, DATABASE(), ':', @component))) IS NULL THEN 0 ELSE 1 END",
                    cancellationToken, ("@prefix", SchemaLock.NamePrefix), ("@component", component)).ConfigureAwait(false) > 0;

            case NpgsqlProvider:
                long key = SchemaLock.AdvisoryKey(component);
                long classId = (key >> 32) & 0xFFFFFFFFL;
                long objectId = key & 0xFFFFFFFFL;
                return await SchemaCatalog.ScalarAsync(
                    db,
                    "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 AND granted " +
                    "AND classid::bigint = @classid AND objid::bigint = @objid " +
                    "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())",
                    cancellationToken, ("@classid", classId), ("@objid", objectId)).ConfigureAwait(false) > 0;

            case SqliteProvider:
                return null;

            default:
                throw SchemaCatalog.Unsupported(db);
        }
    }

    private static (int Major, int Minor) Parse(string version)
    {
        // MariaDB behind some replication setups reports "5.5.5-10.11.6-MariaDB": the real version follows the prefix.
        const string replicationPrefix = "5.5.5-";
        Match match = VersionPattern().Match(version.StartsWith(replicationPrefix, StringComparison.Ordinal) ? version[replicationPrefix.Length..] : version);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0)
            : (0, 0);
    }

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
