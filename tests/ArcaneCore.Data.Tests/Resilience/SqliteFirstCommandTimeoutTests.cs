using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// The FIRST command on a CharacterDbContext whose connection EF opens itself must honour <c>Database:SqliteWriteTimeoutMs</c>.
/// Microsoft.Data.Sqlite's SqliteConnection.CreateCommand copies DefaultTimeout into the command when it is created, and EF creates
/// the command before it opens the connection, so a Default Timeout lowered only at ConnectionOpened (the interceptor) is too late for that command:
/// it kept the connection string's 30 s. The bound therefore has to be in the connection string EF builds the connection from.
/// </summary>
public sealed class SqliteFirstCommandTimeoutTests : IDisposable
{
    // The write bound is 1 s; the command this guards against keeps the connection string's 30 s. The limits split the two at the middle:
    // a loaded runner has taken over 10 s to finish a 1 s wait (thread pool and busy-handler sleeps), never anywhere near 30 s, and the
    // 30 s command still runs into the guard (25 s, under its own timeout) and fails as "still waiting".
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-first-command-" + Guid.NewGuid().ToString("N"));

    public SqliteFirstCommandTimeoutTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    public static TheoryData<string> Scenarios => new()
    {
        "SaveChanges", "SaveChangesAsync", "ExecuteDelete", "ExecuteDeleteAsync",
        "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteScalar", "ExecuteScalarAsync", "WriteQuery", "WriteQueryAsync", "BeginTransaction", "BeginTransactionAsync",
    };

    [Trait("Category", "Timing")]
    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task TheFirstCommandOnAFreshContext_FailsLockedWithinTheWriteBound(string scenario) => FailsWithinBound(scenario, 30);

    [Theory]
    [InlineData("ExecuteSqlRawAsync")]
    [InlineData("ExecuteDelete")]
    [InlineData("SaveChangesAsync")]
    public Task AnUnlimitedDefaultTimeoutInTheConnectionString_IsReplacedByTheWriteBound(string scenario) => FailsWithinBound(scenario, 0);

    private async Task FailsWithinBound(string scenario, int stringTimeoutSeconds)
    {
        string connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "characters.db"), Pooling = false, DefaultTimeout = stringTimeoutSeconds }.ToString();
        await using ServiceProvider provider = Build(connection);
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        // Run the scenario once with no lock held, in its own scope, so the timed run below is a fresh context and connection but pays no
        // one-time cost (JIT, EF's query and command pipeline): on a loaded runner that cold start alone took longer than the bound.
        Assert.Null(await RunFreshAsync(provider, scenario));

        await using var holder = new SqliteConnection(connection);
        await holder.OpenAsync();
        await Scalar(holder, "BEGIN IMMEDIATE");
        Task<Exception?> run = Task.Run(() => RunFreshAsync(provider, scenario));
        var watch = Stopwatch.StartNew();
        Exception? thrown;
        try
        {
            thrown = await run.WaitAsync(Guard);
        }
        catch (TimeoutException)
        {
            watch.Stop();
            await Scalar(holder, "ROLLBACK");
            await run;
            Assert.Fail($"{scenario}: still waiting for the lock after {watch.Elapsed} (the write bound is 1 s)");
            return;
        }

        watch.Stop();
        await Scalar(holder, "ROLLBACK");
        Assert.NotNull(thrown);
        Assert.True(Contains(thrown, "database is locked"), $"{scenario}: expected 'database is locked', got {thrown}");
        Assert.True(watch.Elapsed < Bound, $"{scenario}: failed after {watch.Elapsed}, bound {Bound}");
    }

    [Fact]
    public void ConfigureProvider_PutsTheWriteBoundInTheConnectionString_ButNeverRaisesAShorterOne()
    {
        string Resolve(string connectionString, int writeMs)
        {
            var builder = new DbContextOptionsBuilder<CharacterDbContext>();
            DataServiceCollectionExtensions.ConfigureProvider(
                builder, new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = connectionString }, null, writeMs);
            using var context = new CharacterDbContext(builder.Options);
            return context.Database.GetConnectionString()!;
        }

        Assert.Equal(1, new SqliteConnectionStringBuilder(Resolve("Data Source=a.db;Default Timeout=30", 1000)).DefaultTimeout);
        Assert.Equal(2, new SqliteConnectionStringBuilder(Resolve("Data Source=a.db", 1001)).DefaultTimeout);
        Assert.Equal(2, new SqliteConnectionStringBuilder(Resolve("Data Source=a.db;Default Timeout=2", 5000)).DefaultTimeout);
        Assert.Equal(30, new SqliteConnectionStringBuilder(Resolve("Data Source=a.db", 0)).DefaultTimeout);
        Assert.Equal(1, new SqliteConnectionStringBuilder(Resolve("Data Source=a.db;Default Timeout=0", 1000)).DefaultTimeout);
        Assert.Equal(0, new SqliteConnectionStringBuilder(Resolve("Data Source=a.db;Default Timeout=0", 0)).DefaultTimeout);
        Assert.Equal(1, SqliteConnectionPragmaInterceptor.ApplyWriteBound(0, 1));
        Assert.Equal(2, SqliteConnectionPragmaInterceptor.ApplyWriteBound(30, 1500));
    }

    private static async Task<Exception?> RunFreshAsync(ServiceProvider provider, string scenario)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        try
        {
            switch (scenario)
            {
                case "SaveChanges":
                    db.Characters.Add(New());
                    db.SaveChanges();
                    break;
                case "SaveChangesAsync":
                    db.Characters.Add(New());
                    await db.SaveChangesAsync();
                    break;
                case "ExecuteDelete":
                    db.Characters.Where(c => c.Id < 0).ExecuteDelete();
                    break;
                case "ExecuteDeleteAsync":
                    await db.Characters.Where(c => c.Id < 0).ExecuteDeleteAsync();
                    break;
                case "ExecuteSqlRaw":
                    db.Database.ExecuteSqlRaw("DELETE FROM characters WHERE id < 0");
                    break;
                case "ExecuteSqlRawAsync":
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM characters WHERE id < 0");
                    break;
                case "ExecuteScalar":
                    {
                        using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "DELETE FROM characters WHERE id < 0 RETURNING id";
                        db.Database.OpenConnection(); // the command above copied the string's timeout before EF's open, as EF's own commands do
                        command.ExecuteScalar();
                        break;
                    }

                case "ExecuteScalarAsync":
                    {
                        await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "DELETE FROM characters WHERE id < 0 RETURNING id";
                        await db.Database.OpenConnectionAsync();
                        await command.ExecuteScalarAsync();
                        break;
                    }

                case "WriteQuery":
                    _ = db.Database.SqlQueryRaw<int>("DELETE FROM characters WHERE id < 0 RETURNING id AS Value").ToList();
                    break;
                case "WriteQueryAsync":
                    _ = await db.Database.SqlQueryRaw<int>("DELETE FROM characters WHERE id < 0 RETURNING id AS Value").ToListAsync();
                    break;
                case "BeginTransaction":
                    db.Database.BeginTransaction().Dispose();
                    break;
                case "BeginTransactionAsync":
                    await (await db.Database.BeginTransactionAsync()).DisposeAsync();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static CharacterRecord New() => new() { AccountId = 1, Name = "Firstcmd", Race = 1, Class = 1, Level = 1 };

    private static bool Contains(Exception? ex, string text)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task Scalar(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteScalarAsync();
    }

    private static ServiceProvider Build(string connection)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = connection,
            ["Database:SqliteBusyTimeoutMs"] = "300",
            ["Database:SqliteWriteTimeoutMs"] = "1000",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(config);
        return services.BuildServiceProvider();
    }
}
