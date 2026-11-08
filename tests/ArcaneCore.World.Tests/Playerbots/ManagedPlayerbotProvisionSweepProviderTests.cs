using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Auth.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Two split holders sweep the same stale provisioning row at the same time: the whole reconcile (the private <c>ReconcileProvisioningAsync</c>, reached through the internal
/// <see cref="ManagedPlayerbotFeature.SweepStaleProvisionsAsync"/>) over the real <see cref="EfManagedPlayerbotProvisionStore"/> and <see cref="EfAccountStore"/>, each worker with its own
/// auth context on one database (what the database sees from two processes). The registry and character stores are the shared in-memory ones: only the auth side is the subject.
/// SQLite always runs; MariaDB and PostgreSQL run where <c>ARCANECORE_TEST_MARIADB</c> / <c>ARCANECORE_TEST_POSTGRES</c> name a server (a server connection string without a database).
/// Without those variables the MariaDB and PostgreSQL rows do not exist, so a green run proves SQLite only.
/// </summary>
public sealed class ManagedPlayerbotProvisionSweepProviderTests : IAsyncLifetime
{
    private const string MariaDbVariable = "ARCANECORE_TEST_MARIADB";
    private const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";

    private readonly List<DatabaseConnectionOptions> _created = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-provision-sweep-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Providers()
    {
        yield return [DatabaseProvider.Sqlite];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MariaDbVariable)))
            yield return [DatabaseProvider.MariaDb];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
            yield return [DatabaseProvider.PostgreSql];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (DatabaseConnectionOptions options in _created.Where(o => o.Provider != DatabaseProvider.Sqlite))
        {
            try { await DropAsync(options); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or TimeoutException) { failures.Add(ex); }
        }

        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory is reclaimed by the OS temp cleanup.
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} scratch database(s) could not be dropped and are left on the server", failures);
    }

    private static async Task DropAsync(DatabaseConnectionOptions options)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (options.Provider == DatabaseProvider.PostgreSql)
                {
                    using (var pooled = new NpgsqlConnection(options.ConnectionString)) NpgsqlConnection.ClearPool(pooled);
                    var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false };
                    string name = builder.Database!;
                    builder.Database = "postgres";
                    await using var connection = new NpgsqlConnection(builder.ConnectionString);
                    await connection.OpenAsync();
                    await using NpgsqlCommand drop = connection.CreateCommand();
                    drop.CommandTimeout = 120;
                    drop.CommandText = "DROP DATABASE IF EXISTS \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" WITH (FORCE)";
                    await drop.ExecuteNonQueryAsync();
                }
                else
                {
                    using (var pooled = new MySqlConnection(options.ConnectionString)) MySqlConnection.ClearPool(pooled);
                    var builder = new MySqlConnectionStringBuilder(options.ConnectionString) { Pooling = false };
                    string name = builder.Database;
                    builder.Database = string.Empty;
                    await using var connection = new MySqlConnection(builder.ConnectionString);
                    await connection.OpenAsync();
                    await using MySqlCommand drop = connection.CreateCommand();
                    drop.CommandTimeout = 120;
                    drop.CommandText = "DROP DATABASE IF EXISTS `" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
                    await drop.ExecuteNonQueryAsync();
                }

                return;
            }
            catch (Exception ex) when (attempt < 4 && (ex is System.Data.Common.DbException || ex is TimeoutException))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    private async Task<DatabaseConnectionOptions> CreateDatabaseAsync(DatabaseProvider provider)
    {
        string name = "arcane_ps_" + Guid.NewGuid().ToString("N")[..16];
        string connectionString = provider switch
        {
            DatabaseProvider.Sqlite => $"Data Source={SqlitePath(name)};Pooling=False",
            DatabaseProvider.MariaDb or DatabaseProvider.MySql => Environment.GetEnvironmentVariable(MariaDbVariable)!.TrimEnd(';') + $";Database={name}",
            DatabaseProvider.PostgreSql => Environment.GetEnvironmentVariable(PostgresVariable)!.TrimEnd(';') + $";Database={name}",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        var options = new DatabaseConnectionOptions { Provider = provider, ConnectionString = connectionString };
        _created.Add(options);
        await using AuthDbContext db = NewContext(options);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        return options;
    }

    private string SqlitePath(string name)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, name + ".db");
    }

    private static AuthDbContext NewContext(DatabaseConnectionOptions database)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, database);
        return new AuthDbContext(builder.Options);
    }

    /// <summary>A split holder of map <paramref name="ownedMap"/> whose account and provisioning stores are the real EF ones over <paramref name="db"/> (one worker's context).</summary>
    private static WorldTestHost StartHolder(AuthDbContext db, InMemoryCharacterStore characters, ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners, uint ownedMap)
    {
        var accounts = new EfAccountStore(db);
        var provisions = new EfManagedPlayerbotProvisionStore(db);
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            var options = new PlayerbotOptions
            {
                Enabled = true, RestoreOnStartup = false, MaxBots = 4, ThinkIntervalMs = 100,
                MaxActionsPerTick = 2, MaxPathPoints = 32, MaxRouteYards = 100, AllowedMaps = [0, 1],
            };
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(provisions);
        });
        host.World.Ownership = new MapOwnership([new MapOwnership.Range(ownedMap, ownedMap)], allowInstanceMaps: false);
        return host;
    }

    private static ManagedPlayerbotFeature BotsOf(WorldTestHost host) => host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

    /// <summary>A journal row of a dead create: written by the real store, then aged past the grace.</summary>
    private static async Task<(Guid BotId, Account Account)> StaleRowAsync(DatabaseConnectionOptions database, string username)
    {
        await using AuthDbContext db = NewContext(database);
        Guid botId = Guid.NewGuid();
        Account account = await new EfManagedPlayerbotProvisionStore(db).CreateAsync(botId,
            new Account { Username = username, Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        long old = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5000;
        await db.Set<ManagedPlayerbotProvisionRow>().Where(row => row.BotId == botId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CreatedUnix, old));
        return (botId, account);
    }

    private static async Task SweepBothAtOnceAsync(WorldTestHost a, WorldTestHost b)
    {
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Sweep(WorldTestHost host) => Task.Run(async () =>
        {
            await go.Task.ConfigureAwait(false);
            await BotsOf(host).SweepStaleProvisionsAsync(CancellationToken.None).ConfigureAwait(false);
        });
        Task first = Sweep(a);
        Task second = Sweep(b);
        go.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMinutes(2)); // a sweep never throws: a row it cannot take is logged and left pending
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoSplitHolders_SweepingTheSameStaleRowAtOnce_RollItBackOnce_AndLeaveNoHalfState(DatabaseProvider provider)
    {
        DatabaseConnectionOptions database = await CreateDatabaseAsync(provider);
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using AuthDbContext dbA = NewContext(database);
        await using AuthDbContext dbB = NewContext(database);
        await using WorldTestHost hostA = StartHolder(dbA, characters, owners, 0);
        await using WorldTestHost hostB = StartHolder(dbB, characters, owners, 1);
        (_, Account account) = await StaleRowAsync(database, "PBSWEEPEF1");

        await SweepBothAtOnceAsync(hostA, hostB);

        await using AuthDbContext check = NewContext(database);
        Assert.Empty(await new EfManagedPlayerbotProvisionStore(check).LoadPendingAsync()); // the row is gone, taken by one of them
        Assert.False(await check.Accounts.AsNoTracking().AnyAsync(row => row.Id == account.Id), "the empty owner account was rolled back with its journal row");
        Assert.Empty(await owners.LoadAllAsync());
        Assert.Empty(await characters.GetByAccountAsync(account.Id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoSplitHolders_SweepingARowWhoseRegistryRowExists_CompleteItOnce_AndKeepTheBot(DatabaseProvider provider)
    {
        DatabaseConnectionOptions database = await CreateDatabaseAsync(provider);
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using AuthDbContext dbA = NewContext(database);
        await using AuthDbContext dbB = NewContext(database);
        await using WorldTestHost hostA = StartHolder(dbA, characters, owners, 0);
        await using WorldTestHost hostB = StartHolder(dbB, characters, owners, 1);
        (Guid botId, Account account) = await StaleRowAsync(database, "PBSWEEPEF2");
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord { AccountId = account.Id, Name = "Sweepcomplete", Race = 1, Class = 1, Level = 10 });
        await owners.CreateAsync(new ManagedPlayerbot(botId, account.Id, character.Id, account.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1)); // the create stored its registry row and died before completing the journal row

        await SweepBothAtOnceAsync(hostA, hostB);

        await using AuthDbContext check = NewContext(database);
        Assert.Empty(await new EfManagedPlayerbotProvisionStore(check).LoadPendingAsync()); // completed once
        Assert.True(await check.Accounts.AsNoTracking().AnyAsync(row => row.Id == account.Id), "the bot's account stays");
        Assert.NotNull(owners.Peek(botId));
        Assert.Single(await characters.GetByAccountAsync(account.Id));
    }
}
