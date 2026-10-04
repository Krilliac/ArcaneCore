using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Data;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.Resilience;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// The daemon composed as Program.cs composes it (AddAuthDatabase + AddRealmResilience), against an auth database
/// that is not there: an SQLite file in a directory that does not exist (SQLITE_CANTOPEN on the first query) and a
/// MariaDB connection string pointing at a closed loopback port (connection refused while the context is built).
/// A real 1.12.1-shaped challenge must be answered WOW_FAIL_DB_BUSY within the configured time, the circuit must
/// open after the configured failures and refuse the next client without touching the database, with the log lines.
/// </summary>
public sealed class AuthDatabaseOutageTests
{
    private const int QueryTimeoutMs = 2000;
    private const int FailureThreshold = 2;

    [Fact]
    public async Task UnopenableSqliteFile_RefusesLogonWithDbBusy_AndOpensTheCircuit()
    {
        string missing = Path.Combine(Path.GetTempPath(), "arcanecore-missing-" + Guid.NewGuid().ToString("N"), "auth.db");
        await RunOutageAsync(("Database:Provider", "Sqlite"), ("Database:ConnectionString", $"Data Source={missing}"));
    }

    [Fact]
    public async Task ClosedPort_RefusesLogonWithDbBusy_AndOpensTheCircuit()
    {
        int closedPort = FreePort();
        await RunOutageAsync(
            ("Database:Provider", "MariaDb"),
            ("Database:ConnectionString", $"Server=127.0.0.1;Port={closedPort};Database=arcane;User=arcane;Password=arcane;ConnectionTimeout=1;"));
    }

    [Fact]
    public async Task AddRealmResilience_BeforeAddAuthDatabase_IsRefusedLoudly()
    {
        var services = new ServiceCollection();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => services.AddRealmResilience(Config()));
        Assert.Contains("after AddAuthDatabase", ex.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void InvalidResilienceSection_FailsValidation_WithEveryProblem()
    {
        IConfiguration config = Config(
            ("Database:Provider", "Sqlite"),
            ("Database:ConnectionString", "Data Source=:memory:"),
            ("Resilience:Database:QueryTimeoutMs", "-1"),
            ("Resilience:Database:Bootstrap:MaxAttempts", "0"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthDatabase(config);
        services.AddRealmResilience(config);
        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ResilienceOptions>>().Value);
        Assert.Contains(ex.Failures, f => f.StartsWith("Resilience:Database:QueryTimeoutMs", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("Resilience:Database:Bootstrap:MaxAttempts", StringComparison.Ordinal));
    }

    private static async Task RunOutageAsync(params (string Key, string? Value)[] database)
    {
        IConfiguration config = Config(
        [
            .. database,
            ("Resilience:Database:QueryTimeoutMs", QueryTimeoutMs.ToString()),
            ("Resilience:Database:Breaker:FailureThreshold", FailureThreshold.ToString()),
            ("Resilience:Database:Breaker:FailureRateThreshold", "0"),
            ("Resilience:Database:Breaker:OpenDurationMs", "60000"),
        ]);

        var log = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Debug));
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
        services.AddAuthDatabase(config);
        services.AddRealmResilience(config);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // The decorators are in place and resolving them touches no database.
        using (IServiceScope scope = provider.CreateScope())
        {
            Assert.Equal("GuardedAccountStore", scope.ServiceProvider.GetRequiredService<IAccountStore>().GetType().Name);
            Assert.Equal("GuardedRealmStore", scope.ServiceProvider.GetRequiredService<IRealmStore>().GetType().Name);
            Assert.Equal("GuardedBanStore", scope.ServiceProvider.GetRequiredService<IBanStore>().GetType().Name);
        }

        CircuitBreaker auth = provider.GetRequiredService<DatabaseCircuits>().For(DatabaseComponent.Auth);
        int port = FreePort();
        var options = new AuthOptions { BindAddress = "127.0.0.1", Port = port, MaxSessionDurationSeconds = 0 };
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<ILogger<LogonServer>>());
        await server.StartAsync(CancellationToken.None);
        try
        {
            // Every client gets the fail-closed refusal within the query timeout (plus slack for the test box).
            for (int i = 0; i < FailureThreshold; i++)
            {
                var watch = Stopwatch.StartNew();
                Assert.Equal((byte)AuthResult.FailDbBusy, await ChallengeResultAsync(port, "TESTER" + i));
                Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(QueryTimeoutMs + 3000), $"refusal took {watch.Elapsed}");
            }

            Assert.Equal(CircuitState.Open, auth.State);
            long rejectedBefore = auth.Rejected;

            // The next client is refused by the open circuit: same answer, no database attempt.
            var fast = Stopwatch.StartNew();
            Assert.Equal((byte)AuthResult.FailDbBusy, await ChallengeResultAsync(port, "LATER"));
            Assert.True(fast.Elapsed < TimeSpan.FromSeconds(2), $"open-circuit refusal took {fast.Elapsed}");
            Assert.Equal(rejectedBefore + 1, auth.Rejected);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }

        Assert.Contains(log.Lines, l => l.Contains("Auth database circuit OPENED", StringComparison.Ordinal) && l.Contains("fail closed", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("auth database unavailable", StringComparison.Ordinal) && l.Contains("refusing logon (fail closed)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("circuit 'Auth database' is open", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("session error", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("Password=arcane", StringComparison.Ordinal));
    }

    private static async Task<byte> ChallengeResultAsync(int port, string username)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(BuildChallenge(username));
        byte[] head = new byte[3];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await stream.ReadExactlyAsync(head, cts.Token);
        Assert.Equal((byte)AuthCommand.LogonChallenge, head[0]);
        return head[2];
    }

    private static byte[] BuildChallenge(string username)
    {
        byte[] name = Encoding.ASCII.GetBytes(username.ToUpperInvariant());
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());           // gamename[4]
        body.AddRange([1, 12, 1]);                     // version 1.12.1
        body.AddRange(BitConverter.GetBytes((ushort)ClientBuild.Vanilla1121));
        body.AddRange("68x\0"u8.ToArray());            // platform[4]
        body.AddRange("niW\0"u8.ToArray());            // os[4]
        body.AddRange("SUne"u8.ToArray());             // country[4]
        body.AddRange(new byte[4]);                    // timezone
        body.AddRange(new byte[4]);                    // ip
        body.Add((byte)name.Length);
        body.AddRange(name);

        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(BitConverter.GetBytes((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
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

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string line = $"{logLevel} {category}: {formatter(state, exception)}" + (exception is null ? string.Empty : " | " + exception.Message);
                lock (owner._lines)
                {
                    owner._lines.Add(line);
                }
            }
        }
    }
}
