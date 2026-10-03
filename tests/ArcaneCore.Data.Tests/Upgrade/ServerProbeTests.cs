using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// What the server is, who else is connected and whether the schema lock is held. The classification is pure and
/// runs everywhere; the MariaDB/PostgreSQL queries (IS_USED_LOCK, pg_locks, the process lists) are asserted only
/// where ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES are set (hosted CI).
/// </summary>
public sealed class ServerProbeTests : IAsyncLifetime
{
    private const string Maria = "Pomelo.EntityFrameworkCore.MySql";
    private const string Postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [InlineData(Maria, "10.11.6-MariaDB-1:10.11.6+maria~ubu2204", "MariaDB", true)]
    [InlineData(Maria, "11.4.2-MariaDB", "MariaDB", true)]
    [InlineData(Maria, "5.5.5-10.11.6-MariaDB", "MariaDB", true)]
    [InlineData(Maria, "10.6.17-MariaDB", "MariaDB", false)]
    [InlineData(Maria, "10.5.0-MariaDB", "MariaDB", false)]
    [InlineData(Maria, "8.0.36", "MySQL", false)]
    [InlineData(Postgres, "16.2 (Debian 16.2-1.pgdg120+2)", "PostgreSQL", true)]
    [InlineData(Postgres, "17.0", "PostgreSQL", true)]
    [InlineData(Postgres, "15.6", "PostgreSQL", false)]
    [InlineData(Sqlite, "3.46.0", "SQLite", true)]
    public void Qualify_NamesTheProduct_AndWarnsWhenCiDoesNotRunIt(string provider, string version, string product, bool qualified)
    {
        ServerInfo info = ServerProbe.Qualify(provider, version);

        Assert.Equal(product, info.Product);
        Assert.Equal(version, info.Version);
        Assert.Equal(qualified, info.Qualified);
        Assert.Equal(qualified, info.Warning is null);
        if (!qualified)
        {
            Assert.Contains(version, info.Warning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Qualify_MySql8_IsExplicitlyUnqualified()
    {
        ServerInfo info = ServerProbe.Qualify(Maria, "8.0.36");
        Assert.Contains("not qualified", info.Warning, StringComparison.Ordinal);
        Assert.Contains("MariaDB 10.11", info.Warning, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ServerInfo_IsReadFromTheServer(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);

        ServerInfo info = await ServerProbe.ReadServerInfoAsync(db);

        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Equal(provider == DatabaseProvider.Sqlite ? "SQLite" : provider == DatabaseProvider.PostgreSql ? "PostgreSQL" : info.Product, info.Product);
        if (provider == DatabaseProvider.Sqlite)
        {
            Assert.Matches(@"^\d+\.\d+", info.Version);
        }
    }

    [Fact]
    public async Task Sqlite_CannotSayWhoElseIsConnectedOrWhoHoldsTheLock_AndSaysSoWithNull()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);

        Assert.Null(await ServerProbe.CountOtherSessionsAsync(db));
        Assert.Null(await ServerProbe.IsSchemaLockHeldAsync(db, "auth"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SchemaLockHeldByAnotherSession_IsReported_AndFreeAfterRelease(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
        {
            return; // not observable on SQLite; see the null test above
        }

        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        Assert.False(await ServerProbe.IsSchemaLockHeldAsync(db, "auth"));

        await using (await HoldSchemaLockAsync(connection, "auth"))
        {
            Assert.True(await ServerProbe.IsSchemaLockHeldAsync(db, "auth"));
            Assert.False(await ServerProbe.IsSchemaLockHeldAsync(db, "world")); // another component's lock is another lock
        }

        Assert.False(await ServerProbe.IsSchemaLockHeldAsync(db, "auth"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OtherSessions_AreCounted_NotTheProbeItself(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
        {
            return;
        }

        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        var unpooled = new DatabaseConnectionOptions
        {
            Provider = provider,
            ConnectionString = connection.ConnectionString + ";Pooling=false",
        };
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(unpooled);
        int baseline = await ServerProbe.CountOtherSessionsAsync(db) ?? throw new Xunit.Sdk.XunitException("the server did not answer");

        await using DbConnection other = provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(unpooled.ConnectionString)
            : new MySqlConnection(unpooled.ConnectionString);
        await other.OpenAsync();

        Assert.Equal(baseline + 1, await ServerProbe.CountOtherSessionsAsync(db));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Upgrader_RefusesActiveSessionsWhenAsked_HostedOnly(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
        {
            return; // SQLite sessions are not detectable
        }

        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            await UpgradeTestSupport.SetVersionAsync(db, "auth", AuthDbContext.Schema.CurrentVersion - 1);
        }

        var unpooled = new DatabaseConnectionOptions { Provider = provider, ConnectionString = connection.ConnectionString + ";Pooling=false" };
        await using DbConnection other = provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(unpooled.ConnectionString)
            : new MySqlConnection(unpooled.ConnectionString);
        await other.OpenAsync();

        await using AuthDbContext upgrader = TestContexts.Create<AuthDbContext>(unpooled);
        SchemaActiveSessionsException ex = await Assert.ThrowsAsync<SchemaActiveSessionsException>(
            () => SchemaUpgrader.ApplyAsync(upgrader, AuthDbContext.Schema, new SchemaUpgradeOptions { RefuseActiveSessions = true }));
        Assert.True(ex.Sessions >= 1);

        await SchemaUpgrader.ApplyAsync(upgrader, AuthDbContext.Schema, new SchemaUpgradeOptions { RefuseActiveSessions = false });
        Assert.Equal(AuthDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(upgrader, "auth"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>Take the component's schema lock from a separate, unpooled connection, the way another process would.</summary>
    private static async Task<IAsyncDisposable> HoldSchemaLockAsync(DatabaseConnectionOptions options, string component)
    {
        string cs = options.ConnectionString + ";Pooling=false";
        DbConnection connection = options.Provider == DatabaseProvider.PostgreSql ? new NpgsqlConnection(cs) : new MySqlConnection(cs);
        await connection.OpenAsync();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = options.Provider == DatabaseProvider.PostgreSql
                ? $"SELECT pg_advisory_lock({SchemaBootstrapper.AdvisoryLockKey(component)})"
                : $"SELECT GET_LOCK(SHA1(CONCAT('arcanecore_schema:', DATABASE(), ':{component}')), 0)";
            await command.ExecuteScalarAsync();
        }

        return new Held(connection);
    }

    private sealed class Held(DbConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
