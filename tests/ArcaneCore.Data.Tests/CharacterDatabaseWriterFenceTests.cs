using System.Data.Common;
using System.Globalization;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="CharacterDatabaseWriterFence"/>: once tripped, the characters context refuses everything (connection open, transaction start, every statement reads included, commit) and nothing
/// reaches the database. Each case is checked from an independent connection (SQLite <c>PRAGMA data_version</c>, which only other connections' commits move; a row count and a checksum
/// on the server providers). Built the way the hosts do, through <c>AddCharacterDatabase</c>.
/// </summary>
public sealed class CharacterDatabaseWriterFenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> ServerProviders() => CharacterDatabaseWriterLockTests.ServerProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Texts { get { lock (_gate) return [.. _texts]; } }

        private void Record(DbCommand command) { lock (_gate) _texts.Add(command.CommandText); }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return new(result); }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Record(command); return new(result); }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Record(command); return new(result); }
    }

    private static async Task<ServiceProvider> BuildAsync(DatabaseConnectionOptions connection, CommandLog? log = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(configuration);
        if (log is not null)
            services.ConfigureDbContext<CharacterDbContext>(builder => builder.AddInterceptors(log));
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return provider;
    }

    /// <summary>Reads the database from a connection of its own, so what this process does through the context is seen as a stranger's change.</summary>
    private sealed class Observer : IAsyncDisposable
    {
        private readonly DbConnection _connection;
        private readonly bool _sqlite;
        private readonly string _quote;

        private Observer(DbConnection connection, bool sqlite, string quote)
        {
            _connection = connection;
            _sqlite = sqlite;
            _quote = quote;
        }

        public static async Task<Observer> OpenAsync(DatabaseConnectionOptions options)
        {
            DbConnection connection = options.Provider switch
            {
                DatabaseProvider.Sqlite => new SqliteConnection(options.ConnectionString),
                DatabaseProvider.PostgreSql => new NpgsqlConnection(options.ConnectionString),
                _ => new MySqlConnection(options.ConnectionString),
            };
            await connection.OpenAsync();
            return new Observer(connection, options.Provider == DatabaseProvider.Sqlite, options.Provider is DatabaseProvider.Sqlite or DatabaseProvider.PostgreSql ? "\"" : "`");
        }

        public async Task<string> SnapshotAsync()
        {
            string version = string.Empty;
            if (_sqlite)
                version = Convert.ToString(await ScalarAsync("PRAGMA data_version"), CultureInfo.InvariantCulture) + "/";
            // A row count and a checksum over every name: a change to any row moves one of them.
            return version + Convert.ToString(await ScalarAsync("SELECT COUNT(*) FROM characters"), CultureInfo.InvariantCulture)
                + "/" + Convert.ToString(await ScalarAsync($"SELECT COALESCE(SUM(LENGTH({_quote}Name{_quote}) * 31 + {_quote}Id{_quote}), 0) FROM characters"), CultureInfo.InvariantCulture);
        }

        private async Task<object?> ScalarAsync(string sql)
        {
            await using DbCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private static async Task<int> CreateCharacterAsync(IServiceProvider services, string name)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
    }

    private static async Task AssertFencedAsync(Func<Task> action)
    {
        Exception? thrown = await Record.ExceptionAsync(action);
        Assert.NotNull(thrown);
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException) return;
        }

        Assert.Fail("expected a CharacterOwnershipRejectedException but got " + thrown);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Tripped_RefusesEveryPath_AndNothingLands(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection);
        int id = await CreateCharacterAsync(services, "Fenced");
        await using Observer observer = await Observer.OpenAsync(connection);
        var fence = services.GetRequiredService<CharacterDatabaseWriterFence>();

        // (f) begins before the trip, writes inside the transaction, and is committed after it.
        await using AsyncServiceScope openScope = services.CreateAsyncScope();
        var openDb = openScope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await openDb.Database.BeginTransactionAsync();
        await openDb.Characters.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Midtx"));

        string before = await observer.SnapshotAsync();
        fence.Trip("another world holds the lock");

        async Task CaseAsync(Func<CharacterDbContext, Task> body)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await AssertFencedAsync(() => body(scope.ServiceProvider.GetRequiredService<CharacterDbContext>()));
            Assert.Equal(before, await observer.SnapshotAsync());
        }

        await CaseAsync(db => new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Late", Race = 1, Class = 1, Level = 1 })); // (a)
        await CaseAsync(db => db.Characters.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Renamed"))); // (b)
        await CaseAsync(db => db.Database.ExecuteSqlRawAsync("WITH t AS (SELECT 1) UPDATE characters SET name = 'Cte'")); // (c)
        await CaseAsync(db => db.Database.ExecuteSqlRawAsync("/* tag */ (SELECT 1)")); // (d) a read is refused too
        await CaseAsync(db => db.Database.OpenConnectionAsync()); // (e)

        // (f) the commit of the transaction that began before the trip is refused, and the row keeps its old name once it is rolled back.
        await AssertFencedAsync(() => transaction.CommitAsync());
        await transaction.RollbackAsync(); // never refused
        Assert.Equal(before, await observer.SnapshotAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Untripped_IssuesTheSameStatements_AsWithoutTheInterceptor(DatabaseProvider provider)
    {
        static async Task BodyAsync(CharacterDbContext db)
        {
            var store = new EfCharacterStore(db);
            CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Same", Race = 1, Class = 1, Level = 1 });
            await store.GetByIdAsync(created.Id);
            await db.Characters.Where(c => c.Id == created.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Level, (byte)2));
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
            await db.Characters.Where(c => c.Id == created.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Level, (byte)3));
            await tx.CommitAsync();
        }

        // With the interceptor: the container's context (the log is attached after the schema is initialised, so only the body is logged).
        DatabaseConnectionOptions fenced = await _databases.CreateAsync(provider);
        var fencedLog = new CommandLog();
        await using (ServiceProvider services = await BuildAsync(fenced, fencedLog))
        {
            int already = fencedLog.Texts.Count;
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await BodyAsync(scope.ServiceProvider.GetRequiredService<CharacterDbContext>());
            Assert.True(fencedLog.Texts.Count > already);
        }

        // Without it: a context configured by ConfigureProvider alone, on a database the container initialised.
        DatabaseConnectionOptions plain = await _databases.CreateAsync(provider);
        await using (await BuildAsync(plain)) { }
        var plainLog = new CommandLog();
        var bareOptions = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(bareOptions, plain);
        bareOptions.AddInterceptors(plainLog);
        await using (var bare = new CharacterDbContext(bareOptions.Options))
            await BodyAsync(bare);

        // The fenced run's log also holds the initialiser's statements; the body starts at the first INSERT of a character.
        static string[] FromFirstInsert(IReadOnlyList<string> texts) => [.. texts.SkipWhile(t => !System.Text.RegularExpressions.Regex.IsMatch(t, "INSERT INTO [`\"]?characters[`\"]? "))];
        string[] fencedBody = FromFirstInsert(fencedLog.Texts);
        string[] plainBody = FromFirstInsert(plainLog.Texts);
        Assert.NotEmpty(plainBody);
        Assert.Equal(plainBody, fencedBody);
    }

    [Fact]
    public void TheFirstReasonStays_AndTheMessageCarriesIt()
    {
        var fence = new CharacterDatabaseWriterFence();
        Assert.False(fence.IsTripped);
        Assert.Null(fence.Reason);
        fence.ThrowIfTripped(); // open: nothing

        fence.Trip("first reason");
        fence.Trip("second reason");

        Assert.True(fence.IsTripped);
        Assert.Equal("first reason", fence.Reason);
        CharacterDatabaseFencedException thrown = Assert.Throws<CharacterDatabaseFencedException>(fence.ThrowIfTripped);
        Assert.Equal("first reason", thrown.Reason);
        Assert.Contains("first reason", thrown.Message);
        Assert.DoesNotContain("second reason", thrown.Message);
        Assert.IsAssignableFrom<CharacterOwnershipRejectedException>(thrown);
        Assert.Throws<ArgumentException>(() => new CharacterDatabaseWriterFence().Trip(" "));
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task TheLockConnection_StaysUsable_AfterTheTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions options = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(options);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;
        using ServerAdvisoryWriterLock held = ServerAdvisoryWriterLock.TryAcquire(options, name, _ => { }, TimeSpan.FromHours(1))!;

        services.GetRequiredService<CharacterDatabaseWriterFence>().Trip("test");
        held.CheckHealth();

        Assert.False(held.IsLost);
        Assert.True(held.IsHeld());
    }
}
