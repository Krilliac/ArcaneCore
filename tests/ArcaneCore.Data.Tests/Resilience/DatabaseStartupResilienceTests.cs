using System.Net;
using System.Net.Sockets;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// The start-up path with the guard registered: an unreachable server is retried the configured number of times with
/// backoff and then reported as one scrubbed line and exit code 6; a schema refusal is never retried; a reachable
/// database starts as before; the guard translates and counts runtime failures per component.
/// </summary>
public sealed class DatabaseStartupResilienceTests
{
    [Fact]
    public async Task ClosedPort_IsRetriedThenExitsUnreachable_WithoutLeakingThePassword()
    {
        int port = FreePort();
        var log = new ListLogger();
        await using ServiceProvider provider = Build(
            log,
            ("Database:Provider", "MariaDb"),
            ("Database:ConnectionString", $"Server=127.0.0.1;Port={port};Database=arcane;User=arcane;Password=Sup3rSecret!;ConnectionTimeout=1;"),
            ("Resilience:Database:Bootstrap:MaxAttempts", "3"),
            ("Resilience:Database:Bootstrap:BaseDelayMs", "10"),
            ("Resilience:Database:Bootstrap:MaxDelayMs", "20"));

        var error = new StringWriter();
        int exit = await DatabaseStartup.InitializeAsync(
            () => provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(), provider, error);

        Assert.Equal(DbUpgradeExitCodes.Unreachable, exit);
        string line = error.ToString();
        Assert.StartsWith("database unreachable:", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3rSecret", line, StringComparison.Ordinal);
        Assert.Equal(2, log.Lines.Count(l => l.Contains("database bootstrap attempt", StringComparison.Ordinal)));
        Assert.Contains(log.Lines, l => l.Contains("attempt 1 of 3", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("attempt 2 of 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnopenableSqliteFile_ExitsUnreachable()
    {
        string missing = Path.Combine(Path.GetTempPath(), "arcanecore-missing-" + Guid.NewGuid().ToString("N"), "auth.db");
        await using ServiceProvider provider = Build(
            new ListLogger(),
            ("Database:Provider", "Sqlite"),
            ("Database:ConnectionString", $"Data Source={missing}"),
            ("Resilience:Database:Bootstrap:MaxAttempts", "2"),
            ("Resilience:Database:Bootstrap:BaseDelayMs", "1"),
            ("Resilience:Database:Bootstrap:MaxDelayMs", "1"));
        var error = new StringWriter();
        int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(), provider, error);
        Assert.Equal(DbUpgradeExitCodes.Unreachable, exit);
        Assert.Contains("unable to open database file", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemaRefusal_IsNotRetried()
    {
        var log = new ListLogger();
        await using ServiceProvider provider = Build(log, ("Database:Provider", "Sqlite"), ("Database:ConnectionString", "Data Source=:memory:"));
        int calls = 0;
        var error = new StringWriter();
        int exit = await DatabaseStartup.InitializeAsync(
            () =>
            {
                calls++;
                throw new SchemaMismatchException("newer than this build");
            },
            provider,
            error);
        Assert.Equal(DbUpgradeExitCodes.Refused, exit);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(log.Lines, l => l.Contains("bootstrap attempt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReachableSqlite_BootstrapsAndServesGuardedCalls()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-resilience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string connectionString = $"Data Source={Path.Combine(directory, "auth.db")}";
        try
        {
            await using ServiceProvider provider = Build(
                new ListLogger(),
                ("Database:Provider", "Sqlite"),
                ("Database:ConnectionString", connectionString));
            int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(), provider, new StringWriter());
            Assert.Equal(DbUpgradeExitCodes.Ok, exit);

            DatabaseGuard guard = provider.GetRequiredService<DatabaseGuard>();
            using IServiceScope scope = provider.CreateScope();
            IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            Account? none = await guard.ExecuteAsync(
                DatabaseComponent.Auth, static (s, ct) => new ValueTask<Account?>(s.FindByUsernameAsync("NOBODY", ct)), accounts);
            Assert.Null(none);
            Assert.Equal(CircuitState.Closed, guard.Circuits.For(DatabaseComponent.Auth).State);
        }
        finally
        {
            // Only this test's pool: ClearAllPools would checkpoint and rewrite other classes' WAL files mid-test.
            TestDatabases.ClearSqlitePool(connectionString);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Guard_TranslatesTransientFailures_PassesDomainErrors_AndTripsOnlyOnTransient()
    {
        var log = new ListLogger();
        await using ServiceProvider provider = Build(
            log,
            ("Database:Provider", "Sqlite"),
            ("Database:ConnectionString", "Data Source=:memory:"),
            ("Resilience:Database:Breaker:FailureThreshold", "2"),
            ("Resilience:Database:Breaker:FailureRateThreshold", "0"));
        DatabaseGuard guard = provider.GetRequiredService<DatabaseGuard>();
        CircuitBreaker characters = guard.Circuits.For(DatabaseComponent.Characters);

        // A domain error passes through unchanged and does not count.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await guard.ExecuteAsync<int, int>(DatabaseComponent.Characters, static (_, _) => throw new InvalidOperationException("name taken"), 0));
        Assert.Equal(0, characters.ConsecutiveFailures);

        // A transient one is translated and counted; the second opens the circuit, with the ERROR line.
        DependencyUnavailableException translated = await Assert.ThrowsAsync<DependencyUnavailableException>(async () =>
            await guard.ExecuteAsync<int, int>(DatabaseComponent.Characters, static (_, _) => ValueTask.FromException<int>(new Microsoft.Data.Sqlite.SqliteException("busy", 5)), 0));
        Assert.Equal("Characters database", translated.Dependency);
        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(translated.InnerException);
        await Assert.ThrowsAsync<DependencyUnavailableException>(async () =>
            await guard.ExecuteAsync<int, int>(DatabaseComponent.Characters, static (_, _) => ValueTask.FromException<int>(new TimeoutException()), 0));
        Assert.Equal(CircuitState.Open, characters.State);
        Assert.Contains(log.Lines, l => l.Contains("Characters database circuit OPENED", StringComparison.Ordinal));

        // Other components are untouched; the open one refuses at once.
        Assert.Equal(CircuitState.Closed, guard.Circuits.For(DatabaseComponent.Auth).State);
        await Assert.ThrowsAsync<CircuitOpenException>(async () =>
            await guard.ExecuteAsync(DatabaseComponent.Characters, static (x, _) => new ValueTask<int>(x), 1));

        // A caller's own cancellation is neither translated nor counted.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await guard.ExecuteAsync<int, int>(DatabaseComponent.World, static (_, ct) => ValueTask.FromCanceled<int>(ct), 0, cts.Token));
        Assert.Equal(0, guard.Circuits.For(DatabaseComponent.World).ConsecutiveFailures);
    }

    [Fact]
    public async Task Guard_Disabled_PassesEverythingThrough()
    {
        await using ServiceProvider provider = Build(
            new ListLogger(),
            ("Database:Provider", "Sqlite"),
            ("Database:ConnectionString", "Data Source=:memory:"),
            ("Resilience:Database:Enabled", "false"),
            ("Resilience:Database:Breaker:FailureThreshold", "1"));
        DatabaseGuard guard = provider.GetRequiredService<DatabaseGuard>();
        Assert.False(guard.Enabled);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await guard.ExecuteAsync<int, int>(DatabaseComponent.Auth, static (_, _) => ValueTask.FromException<int>(new TimeoutException()), 0));
        Assert.Equal(CircuitState.Closed, guard.Circuits.For(DatabaseComponent.Auth).State);
    }

    private static ServiceProvider Build(ListLogger log, params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log));
        services.AddAuthDatabase(config);
        services.AddDatabaseResilience(config);
        return services.BuildServiceProvider();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ListLogger : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }

        public void Dispose()
        {
        }
    }
}
