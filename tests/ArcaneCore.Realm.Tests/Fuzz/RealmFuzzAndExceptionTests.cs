using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.Resilience;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Ops.Cli;
using ArcaneCore.Realm.Ops.ServerConsole;
using ArcaneCore.Realm.Protocol;
using ArcaneCore.Realm.Protocol.Versioning;
using ArcaneCore.Realm.Tests.Bans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Fuzz;

/// <summary>
/// Comprehensive exception-path tests and deterministic seeded fuzzers (<c>1</c>, <c>42</c>, <c>20251004</c>)
/// for <c>ArcaneCore.Realm</c> protocol decoders, packet writers, session state machine, resilience decorators,
/// configuration validators, patch catalog, and server console.
/// </summary>
public sealed class RealmFuzzAndExceptionTests : IDisposable
{
    private const string Username = "FUZZUSER";
    private const string Password = "FUZZPASS";
    private static readonly TimeSpan IoBudget = TimeSpan.FromSeconds(5);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ac-realm-fuzz-" + Guid.NewGuid().ToString("N")[..8]);

    public RealmFuzzAndExceptionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ignore transient file locks during test directory cleanup.
        }
    }

    // =================================================================================================
    // 1. ARGUMENT NULL, OUT-OF-RANGE, AND ARGUMENT EXCEPTION TESTS
    // =================================================================================================

    /// <summary>
    /// Verifies that all service registration extensions in <c>ArcaneCore.Realm</c> throw
    /// <c>ArgumentNullException</c> when given a null <c>IServiceCollection</c> or <c>IConfiguration</c>.
    /// </summary>
    [Fact]
    public void ServiceRegistrationExtensions_WithNullArguments_ThrowArgumentNullException()
    {
        IServiceCollection services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => RealmProductionServices.AddRealmProductionServices(null!, config));
        Assert.Throws<ArgumentNullException>(() => RealmProductionServices.AddRealmProductionServices(services, null!));

        Assert.Throws<ArgumentNullException>(() => RealmResilienceServiceCollectionExtensions.AddRealmResilience(null!, config));
        Assert.Throws<ArgumentNullException>(() => RealmResilienceServiceCollectionExtensions.AddRealmResilience(services, null!));

        Assert.Throws<ArgumentNullException>(() => NetProtectionServiceExtensions.AddNetProtection(null!, config));
        Assert.Throws<ArgumentNullException>(() => NetProtectionServiceExtensions.AddNetProtection(services, null!));

        Assert.Throws<ArgumentNullException>(() => RealmWatchdogServiceCollectionExtensions.AddRealmWatchdog(null!, config));
        Assert.Throws<ArgumentNullException>(() => RealmWatchdogServiceCollectionExtensions.AddRealmWatchdog(services, null!));
    }

    /// <summary>
    /// Verifies that <c>PacketWriter</c> rejects negative capacities with <c>ArgumentOutOfRangeException</c>
    /// and rejects null C-strings with <c>ArgumentNullException</c>.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-128)]
    [InlineData(int.MinValue)]
    public void PacketWriter_NegativeCapacity_ThrowsArgumentOutOfRangeException(int invalidCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketWriter(invalidCapacity));
    }

    /// <summary>
    /// Verifies that <c>PacketWriter.WriteCString</c> throws <c>ArgumentNullException</c> on null input,
    /// and zero-capacity <c>PacketWriter</c> grows dynamically across all write primitives.
    /// </summary>
    [Fact]
    public void PacketWriter_NullCStringThrows_AndZeroCapacityGrowsCleanly()
    {
        var writer = new PacketWriter(0);
        Assert.Throws<ArgumentNullException>(() => writer.WriteCString(null!));

        writer.WriteByte(0x10);
        writer.WriteUInt16(0x1234);
        writer.WriteUInt32(0x89ABCDEFu);
        writer.WriteSingle(1.5f);
        writer.WriteBytes([1, 2, 3]);
        writer.WriteCString("ABC");
        Assert.Equal(1 + 2 + 4 + 4 + 3 + 4, writer.Length);
    }

    /// <summary>
    /// Verifies that <c>RealmListWriter.Build</c> throws <c>ArgumentNullException</c> when the realm list is null
    /// and <c>ArgumentException</c> when the list contains a null <c>RealmEntry</c> or null strings.
    /// </summary>
    [Fact]
    public void RealmListWriter_NullRealmsOrNullEntry_ThrowsExpectedException()
    {
        Assert.Throws<ArgumentNullException>(() => RealmListWriter.Build(null!, 0));
        Assert.Throws<ArgumentException>(() => RealmListWriter.Build([null!], 0));
        Assert.Throws<ArgumentNullException>(() => RealmListWriter.Build([new RealmEntry { Name = null!, Address = "127.0.0.1:8085" }], 0));
        Assert.Throws<ArgumentNullException>(() => RealmListWriter.Build([new RealmEntry { Name = "Arcane", Address = null! }], 0));
    }

    /// <summary>
    /// Verifies that <c>PatchCatalog</c> throws <c>ArgumentNullException</c> for null options provider or null locale,
    /// throws <c>ArgumentException</c> when a configured path contains a NUL character, and rejects traversal or invalid locales.
    /// </summary>
    [Fact]
    public void PatchCatalog_NullArgumentsInvalidPathsAndBoundaryInputs_BehaveAsExpected()
    {
        Assert.Throws<ArgumentNullException>(() => new PatchCatalog(null!));

        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _tempDir });
        Assert.Throws<ArgumentNullException>(() => catalog.Find(5464, null!));

        // Build 0, wrong-length locale, or non-ASCII locale returns null without touching disk.
        Assert.Null(catalog.Find(0, "enUS"));
        Assert.Null(catalog.Find(5464, ""));
        Assert.Null(catalog.Find(5464, "en"));
        Assert.Null(catalog.Find(5464, "enUSX"));
        Assert.Null(catalog.Find(5464, "en12"));
        Assert.Null(catalog.Find(5464, "en\0S"));

        // Empty file (0 bytes) must be rejected.
        string emptyPatch = Path.Combine(_tempDir, "5464enUS.mpq");
        File.WriteAllBytes(emptyPatch, []);
        Assert.Null(catalog.Find(5464, "enUS"));

        // NUL byte in configured patch file path triggers ArgumentException in Path.Combine/GetFullPath.
        var badFileCatalog = new PatchCatalog(() => new AutoPatchOptions
        {
            Enabled = true,
            Directory = _tempDir,
            Patches = [new AutoPatchEntry { Build = 5464, Locale = "enUS", File = "bad\0file.mpq" }],
        });
        Assert.Throws<ArgumentException>(() => badFileCatalog.Find(5464, "enUS"));
    }

    /// <summary>
    /// Verifies that <c>RealmIpBanCache.IsBannedAsync</c> throws <c>ArgumentNullException</c> when
    /// <c>address</c> or <c>store</c> is null, and propagates cancellation when the caller cancels.
    /// </summary>
    [Fact]
    public async Task RealmIpBanCache_NullArgumentsAndCancelledToken_ThrowExpectedExceptions()
    {
        var cache = new RealmIpBanCache(TimeSpan.FromSeconds(60));
        var store = new InMemoryBanStore();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IsBannedAsync(null!, store));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IsBannedAsync("127.0.0.1", null!));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cache.IsBannedAsync("127.0.0.1", store, cancelled.Token));
    }

    /// <summary>
    /// Verifies that <c>RealmOpsCli.Validate</c> and <c>RealmOpsCli.Run</c> throw <c>ArgumentNullException</c>
    /// when any parameter is null.
    /// </summary>
    [Fact]
    public void RealmOpsCli_WithNullArguments_ThrowsArgumentNullException()
    {
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        DaemonCommand command = DaemonCommandLine.Parse(["check-config"], ConfigDaemon.Realm, ConfCatalog.Embedded);
        var writer = new StringWriter();

        Assert.Throws<ArgumentNullException>(() => RealmOpsCli.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => RealmOpsCli.Run(null!, config, writer, writer));
        Assert.Throws<ArgumentNullException>(() => RealmOpsCli.Run(command, null!, writer, writer));
        Assert.Throws<ArgumentNullException>(() => RealmOpsCli.Run(command, config, null!, writer));
        Assert.Throws<ArgumentNullException>(() => RealmOpsCli.Run(command, config, writer, null!));
    }

    /// <summary>
    /// Verifies that <c>RealmConsoleCommands</c> and <c>RealmConsole</c> validate all constructor and
    /// method arguments with <c>ArgumentNullException</c>, and handle faulted/cancelled commands cleanly.
    /// </summary>
    [Fact]
    public async Task RealmConsole_And_RealmConsoleCommands_NullGuardsAndFaultHandling()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IAccountStore>(new InMemoryAccountStore())
            .AddSingleton<IRealmStore>(new InMemoryRealmStore([]))
            .BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var lifetime = new FakeHostApplicationLifetime();
        var source = new ConsoleInputSource(new StringReader(string.Empty), new StringWriter(), IsInputRedirected: true, IsUserInteractive: false);
        IOptions<RealmConsoleOptions> options = Options.Create(new RealmConsoleOptions { Enabled = true });
        var logon = new LogonServer(scopes, Options.Create(new AuthOptions()), NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);

        Assert.Throws<ArgumentNullException>(() => new RealmConsoleCommands(null!, () => 0, TimeProvider.System, lifetime));
        Assert.Throws<ArgumentNullException>(() => new RealmConsoleCommands(scopes, null!, TimeProvider.System, lifetime));
        Assert.Throws<ArgumentNullException>(() => new RealmConsoleCommands(scopes, () => 0, null!, lifetime));
        Assert.Throws<ArgumentNullException>(() => new RealmConsoleCommands(scopes, () => 0, TimeProvider.System, null!));

        var commands = new RealmConsoleCommands(scopes, () => 0, TimeProvider.System, lifetime);
        await Assert.ThrowsAsync<ArgumentNullException>(() => commands.ExecuteAsync(null!, new StringWriter(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => commands.ExecuteAsync("help", null!, CancellationToken.None));

        Assert.Throws<ArgumentNullException>(() => new RealmConsole(null!, source, commands, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new RealmConsole(options, null!, commands, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new RealmConsole(options, source, null!, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new RealmConsole(options, source, commands, null!));
        Assert.Throws<ArgumentNullException>(() => new RealmConsole(options, source, null!, logon, TimeProvider.System, lifetime, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new RealmConsole(options, source, scopes, null!, TimeProvider.System, lifetime, NullLoggerFactory.Instance));

        // Verify Submit handles a throwing store by logging and writing the fallback error message without escaping.
        using ServiceProvider throwingProvider = new ServiceCollection()
            .AddSingleton<IAccountStore>(new ThrowingAccountStore(new InvalidOperationException("simulated store boom")))
            .AddSingleton<IRealmStore>(new ThrowingRealmStore(new InvalidOperationException("simulated realm boom")))
            .BuildServiceProvider();
        var outWriter = new StringWriter();
        var throwingSource = new ConsoleInputSource(new StringReader(string.Empty), outWriter, IsInputRedirected: true, IsUserInteractive: false);
        var throwingCommands = new RealmConsoleCommands(throwingProvider.GetRequiredService<IServiceScopeFactory>(), () => 0, TimeProvider.System, lifetime);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => throwingCommands.ExecuteAsync("realmlist", outWriter, CancellationToken.None));

        MethodInfo? submitMethod = typeof(RealmConsole).GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(submitMethod);

        var console = new RealmConsole(options, throwingSource, throwingCommands, NullLoggerFactory.Instance);
        submitMethod!.Invoke(console, ["realmlist"]);
        Assert.Contains("The command failed; see the server log.", outWriter.ToString(), StringComparison.Ordinal);

        // After StopAsync cancels _stopping, Submit catches OperationCanceledException and reports stopping.
        var cancelOut = new StringWriter();
        var cancelSource = new ConsoleInputSource(new StringReader(string.Empty), cancelOut, IsInputRedirected: true, IsUserInteractive: false);
        using ServiceProvider cancelProvider = new ServiceCollection()
            .AddSingleton<IAccountStore>(new InMemoryAccountStore())
            .AddSingleton<IRealmStore>(new CancellingRealmStore())
            .BuildServiceProvider();
        var cancelCommands = new RealmConsoleCommands(cancelProvider.GetRequiredService<IServiceScopeFactory>(), () => 0, TimeProvider.System, lifetime);
        var cancelConsole = new RealmConsole(options, cancelSource, cancelCommands, NullLoggerFactory.Instance);
        await cancelConsole.StopAsync(CancellationToken.None);
        submitMethod.Invoke(cancelConsole, ["realmlist"]);
        Assert.Contains("the realm is stopping; command not run", cancelOut.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <c>LogonServer</c> validates its dependencies, throws <c>FormatException</c> on an invalid
    /// bind IP, throws <c>ArgumentOutOfRangeException</c> on an out-of-range port, and throws
    /// <c>InvalidOperationException</c> if <c>ExecuteAsync</c> is invoked before <c>StartAsync</c>.
    /// </summary>
    [Fact]
    public async Task LogonServer_InvalidOptionsAndUnstartedExecuteAsync_ThrowExpectedExceptions()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var nullScopesServer = new LogonServer(null!, Options.Create(new AuthOptions()), NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullScopesServer.StartAsync(CancellationToken.None));

        var nullOptionsServer = new LogonServer(scopes, null!, NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullOptionsServer.StartAsync(CancellationToken.None));

        var nullFactoryServer = new LogonServer(scopes, Options.Create(new AuthOptions()), null!, NullLogger<LogonServer>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullFactoryServer.StartAsync(CancellationToken.None));

        var nullLoggerServer = new LogonServer(scopes, Options.Create(new AuthOptions()), NullLoggerFactory.Instance, null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullLoggerServer.StartAsync(CancellationToken.None));

        var badIpServer = new LogonServer(
            scopes,
            Options.Create(new AuthOptions { BindAddress = "not-an-ip", Port = 0 }),
            NullLoggerFactory.Instance,
            NullLogger<LogonServer>.Instance);
        await Assert.ThrowsAsync<FormatException>(() => badIpServer.StartAsync(CancellationToken.None));

        var badPortServer = new LogonServer(
            scopes,
            Options.Create(new AuthOptions { BindAddress = "127.0.0.1", Port = 70000 }),
            NullLoggerFactory.Instance,
            NullLogger<LogonServer>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => badPortServer.StartAsync(CancellationToken.None));

        var unstartedServer = new LogonServer(
            scopes,
            Options.Create(new AuthOptions { BindAddress = "127.0.0.1", Port = 0 }),
            NullLoggerFactory.Instance,
            NullLogger<LogonServer>.Instance);
        MethodInfo? executeMethod = typeof(LogonServer).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(executeMethod);
        var executeTask = (Task)executeMethod!.Invoke(unstartedServer, [CancellationToken.None])!;
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executeTask);
        Assert.Contains("StartAsync", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <c>LogonSession.RunAsync</c> throws <c>ArgumentNullException</c> when any required
    /// constructor dependency is null, and that <c>GuardedAuthDiagnostics.DescribeStoreFault</c> throws
    /// <c>ArgumentNullException</c> on a null exception.
    /// </summary>
    [Fact]
    public async Task LogonSession_And_GuardedAuthDiagnostics_NullDependencies_ThrowArgumentNullException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        Task connectTask = client.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient server = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();
        await using NetworkStream stream = server.GetStream();

        var accounts = new InMemoryAccountStore();
        var realms = new InMemoryRealmStore([]);
        var options = new AuthOptions();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LogonSession(null!, accounts, realms, options, NullLogger.Instance, "127.0.0.1:1").RunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LogonSession(stream, null!, realms, options, NullLogger.Instance, "127.0.0.1:1").RunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LogonSession(stream, accounts, null!, options, NullLogger.Instance, "127.0.0.1:1").RunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LogonSession(stream, accounts, realms, null!, NullLogger.Instance, "127.0.0.1:1").RunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LogonSession(stream, accounts, realms, options, null!, "127.0.0.1:1").RunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new LogonSession(stream, accounts, realms, options, NullLogger.Instance, null!).RunAsync(CancellationToken.None));

        Type? diagType = typeof(LogonSession).Assembly.GetType("ArcaneCore.Realm.Resilience.GuardedAuthDiagnostics");
        Assert.NotNull(diagType);
        MethodInfo? describeMethod = diagType!.GetMethod("DescribeStoreFault", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(describeMethod);
        TargetInvocationException tie = Assert.Throws<TargetInvocationException>(() => describeMethod!.Invoke(null, ["FindByUsername", null!]));
        Assert.IsType<ArgumentNullException>(tie.InnerException);
    }

    // =================================================================================================
    // 2. INVALID OPERATION, OPTIONS VALIDATION, AND RESILIENCE EXCEPTION TESTS
    // =================================================================================================

    /// <summary>
    /// Verifies that <c>AddRealmResilience</c> throws <c>InvalidOperationException</c> when either
    /// <c>IAccountStore</c> or <c>IRealmStore</c> has not been registered yet, and is idempotent when called twice.
    /// </summary>
    [Fact]
    public void AddRealmResilience_MissingRequiredStores_ThrowsInvalidOperationException_AndDoubleCallIsIdempotent()
    {
        IConfiguration config = new ConfigurationBuilder().Build();

        var emptyServices = new ServiceCollection();
        InvalidOperationException missingAccount = Assert.Throws<InvalidOperationException>(() => emptyServices.AddRealmResilience(config));
        Assert.Contains(nameof(IAccountStore), missingAccount.Message, StringComparison.Ordinal);

        var onlyAccountServices = new ServiceCollection();
        onlyAccountServices.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        InvalidOperationException missingRealm = Assert.Throws<InvalidOperationException>(() => onlyAccountServices.AddRealmResilience(config));
        Assert.Contains(nameof(IRealmStore), missingRealm.Message, StringComparison.Ordinal);

        // When both required stores are registered (using factory and instance registrations), double decoration is a safe no-op.
        var completeServices = new ServiceCollection();
        completeServices.AddLogging();
        completeServices.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        completeServices.AddSingleton<IRealmStore>(_ => new InMemoryRealmStore([]));
        completeServices.AddRealmResilience(config);
        completeServices.AddRealmResilience(config);

        using ServiceProvider provider = completeServices.BuildServiceProvider();
        Assert.Equal("GuardedAccountStore", provider.GetRequiredService<IAccountStore>().GetType().Name);
        Assert.Equal("GuardedRealmStore", provider.GetRequiredService<IRealmStore>().GetType().Name);
    }

    /// <summary>
    /// Verifies that invalid <c>ResilienceOptions</c>, <c>WatchdogOptions</c>, and <c>MetricsOptions</c>
    /// throw <c>OptionsValidationException</c> when resolved from the Realm DI container, and cause
    /// <c>RealmOpsCli.Validate</c> and <c>RealmOpsCli.Run</c> to report exit code 78 (<c>ExitCodes.InvalidConfiguration</c>).
    /// </summary>
    [Fact]
    public void InvalidOptions_ThrowOptionsValidationException_AndFailRealmOpsCliWithExit78()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Auth:Provider"] = "Sqlite",
                ["Database:Auth:ConnectionString"] = "Data Source=:memory:",
                ["Resilience:Database:QueryTimeoutMs"] = "-5",
                ["Resilience:Database:Breaker:FailureThreshold"] = "0",
                ["Resilience:Database:Breaker:FailureRateThreshold"] = "0",
                ["Resilience:Database:Bootstrap:BaseDelayMs"] = "2000",
                ["Resilience:Database:Bootstrap:MaxDelayMs"] = "500",
                ["Ops:Watchdog:CheckIntervalMs"] = "10",
                ["Ops:Watchdog:TickMonitor:RingCapacity"] = "1",
                ["Ops:Watchdog:Heartbeat:Mode"] = "File",
                ["Ops:Watchdog:Heartbeat:FilePath"] = "",
                ["Ops:Metrics:Enabled"] = "true",
                ["Ops:Metrics:Exporter"] = "Both",
                ["Ops:Metrics:OtlpIntervalSeconds"] = "0",
                ["Ops:Metrics:RealmPrometheusPrefix"] = "ftp://invalid-prefix",
                ["Ops:Metrics:OtlpEndpoint"] = "not-a-valid-url",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddRealmProductionServices(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException resilienceEx = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<ResilienceOptions>>().Value);
        Assert.Contains(resilienceEx.Failures, f => f.Contains("Resilience:Database:QueryTimeoutMs", StringComparison.Ordinal));

        OptionsValidationException watchdogEx = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<WatchdogOptions>>().Value);
        Assert.Contains(watchdogEx.Failures, f => f.Contains("Ops:Watchdog:CheckIntervalMs", StringComparison.Ordinal));
        Assert.Contains(watchdogEx.Failures, f => f.Contains("Ops:Watchdog:Heartbeat:FilePath", StringComparison.Ordinal));

        OptionsValidationException metricsEx = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<MetricsOptions>>().Value);
        Assert.Contains(metricsEx.Failures, f => f.Contains("Ops:Metrics:OtlpIntervalSeconds", StringComparison.Ordinal));
        Assert.Contains(metricsEx.Failures, f => f.Contains("Ops:Metrics:OtlpEndpoint", StringComparison.Ordinal));

        ConfigReport report = RealmOpsCli.Validate(config);
        Assert.True(report.IsInvalid);
        Assert.True(report.ErrorCount >= 5);

        DaemonCommand checkCmd = DaemonCommandLine.Parse(["check-config"], ConfigDaemon.Realm, ConfCatalog.Embedded);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode = RealmOpsCli.Run(checkCmd, (IConfigurationRoot)config, stdout, stderr);
        Assert.Equal(ExitCodes.InvalidConfiguration, exitCode);
        Assert.Contains("ERROR", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <c>LogonSession</c> catches <c>ResilienceException</c> across all logon commands
    /// (<c>LogonChallenge</c>, <c>LogonProof</c>, <c>ReconnectChallenge</c>, and <c>RealmList</c>),
    /// answers <c>AuthResult.FailDbBusy</c> where applicable, and closes the connection fail-closed.
    /// </summary>
    [Fact]
    public async Task LogonSession_WhenStoreThrowsResilienceException_FailsClosedWithFailDbBusy()
    {
        var circuitEx = new CircuitOpenException("Auth database", TimeSpan.FromSeconds(10));

        // 1. LogonChallenge database outage -> FailDbBusy (0x08).
        await using (NetworkStream c1 = await StartSessionAsync(new ThrowingAccountStore(circuitEx), new InMemoryRealmStore([])))
        {
            await c1.WriteAsync(BuildChallengePacket(Username));
            byte[] reply = await ReadExactAsync(c1, 3);
            Assert.Equal([(byte)AuthCommand.LogonChallenge, 0x00, (byte)AuthResult.FailDbBusy], reply);
            Assert.True(await ReadsEofAsync(c1));
        }

        // 2. ReconnectChallenge database outage -> FailDbBusy (0x08).
        await using (NetworkStream c2 = await StartSessionAsync(new ThrowingAccountStore(circuitEx), new InMemoryRealmStore([])))
        {
            await c2.WriteAsync(BuildReconnectChallengePacket(Username));
            byte[] reply = await ReadExactAsync(c2, 2);
            Assert.Equal([(byte)AuthCommand.ReconnectChallenge, (byte)AuthResult.FailDbBusy], reply);
            Assert.True(await ReadsEofAsync(c2));
        }

        // 3. LogonProof database outage on UpdateLoginAsync -> FailDbBusy (0x08).
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, Username, Password);
        var account = new Account
        {
            Username = Username,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength),
            Status = AccountStatus.Active,
        };
        var failOnUpdateStore = new FailOnUpdateAccountStore(account, new TimeoutRejectedException(TimeSpan.FromSeconds(2)));
        await using (NetworkStream c3 = await StartSessionAsync(failOnUpdateStore, new InMemoryRealmStore([])))
        {
            await c3.WriteAsync(BuildChallengePacket(Username));
            (byte result, byte[] b, byte[] s) = await ReadChallengeReplyAsync(c3);
            Assert.Equal((byte)AuthResult.Success, result);

            byte[] proofPacket = BuildValidProofPacket(Username, Password, s, b);
            await c3.WriteAsync(proofPacket);
            byte[] proofReply = await ReadExactAsync(c3, 2);
            Assert.Equal([(byte)AuthCommand.LogonProof, (byte)AuthResult.FailDbBusy], proofReply);
            Assert.True(await ReadsEofAsync(c3));
        }

        // 4. RealmList database outage after authentication -> closes connection without sending realm list.
        var goodAccounts = new InMemoryAccountStore();
        await goodAccounts.CreateAsync(account);
        var failingRealms = new ThrowingRealmStore(new BulkheadRejectedException("Auth database", 8, 16));
        await using (NetworkStream c4 = await StartSessionAsync(goodAccounts, failingRealms))
        {
            await c4.WriteAsync(BuildChallengePacket(Username));
            (byte result, byte[] b, byte[] s) = await ReadChallengeReplyAsync(c4);
            Assert.Equal((byte)AuthResult.Success, result);

            await c4.WriteAsync(BuildValidProofPacket(Username, Password, s, b));
            byte[] proofHead = await ReadExactAsync(c4, 2);
            Assert.Equal((byte)AuthResult.Success, proofHead[1]);
            await ReadExactAsync(c4, 24); // M2[20] + surveyId[4]

            await c4.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0 });
            Assert.True(await ReadsEofAsync(c4));
        }
    }

    /// <summary>
    /// Verifies that unexpected exceptions (<c>InvalidOperationException</c>, <c>CryptographicException</c>,
    /// <c>FormatException</c>, <c>ArgumentOutOfRangeException</c>) thrown inside a session are tagged by
    /// <c>RecordSessionFault</c> with stage, opcode, endpoint, and faultId and rethrown.
    /// </summary>
    [Fact]
    public async Task LogonSession_UnexpectedStoreExceptions_AreTaggedWithFaultMetadataAndRethrown()
    {
        Exception[] unexpected =
        [
            new InvalidOperationException("unexpected state fault"),
            new CryptographicException("unexpected crypto fault"),
            new FormatException("unexpected format fault"),
            new ArgumentOutOfRangeException("param", "unexpected bounds fault"),
        ];

        foreach (Exception fault in unexpected)
        {
            (NetworkStream client, Task serverTask) = await StartRawSessionWithTaskAsync(
                new ThrowingAccountStore(fault),
                new InMemoryRealmStore([]));
            await using (client)
            {
                await client.WriteAsync(BuildChallengePacket(Username));
                Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => serverTask.WaitAsync(IoBudget));
                Assert.Same(fault, thrown);
                Assert.Equal("logon-challenge", thrown.Data["arcanecore.logon.stage"]);
                Assert.Equal("0x00/LogonChallenge", thrown.Data["arcanecore.logon.opcode"]);
                Assert.NotNull(thrown.Data["arcanecore.logon.recorded"]);
                Assert.NotNull(thrown.Data["arcanecore.logon.stack"]);
            }
        }
    }

    // =================================================================================================
    // 3. PROTOCOL BOUNDARIES, ACCOUNT NAME VALIDATION, STATE TRANSITIONS & STREAM CLOSURE TESTS
    // =================================================================================================

    /// <summary>
    /// Verifies boundary conditions on <c>LogonChallengeRequest.TryParse</c>, <c>LogonProofRequest.TryParse</c>,
    /// and <c>AuthProtocols.TryGet</c>.
    /// </summary>
    [Fact]
    public void ProtocolDecoders_And_AuthProtocols_RejectBoundaryInvalidInputs()
    {
        // LogonChallengeRequest: empty, < 30 bytes, username_len == 0, and 30 + username_len > body.Length.
        Assert.False(LogonChallengeRequest.TryParse([], out LogonChallengeRequest? req0));
        Assert.Null(req0);

        for (int len = 1; len < 30; len++)
        {
            Assert.False(LogonChallengeRequest.TryParse(new byte[len], out _));
        }

        byte[] zeroLenUser = new byte[31];
        zeroLenUser[29] = 0;
        Assert.False(LogonChallengeRequest.TryParse(zeroLenUser, out _));

        byte[] truncatedUser = new byte[35];
        truncatedUser[29] = 6; // needs 36 bytes total, only 35 present
        Assert.False(LogonChallengeRequest.TryParse(truncatedUser, out _));

        // LogonProofRequest: empty and 1..73 bytes all return false; 74 bytes succeeds.
        Assert.False(LogonProofRequest.TryParse([], out LogonProofRequest? proof0));
        Assert.Null(proof0);
        for (int len = 1; len < LogonProofRequest.BodyLength; len++)
        {
            Assert.False(LogonProofRequest.TryParse(new byte[len], out _));
        }

        Assert.True(LogonProofRequest.TryParse(new byte[LogonProofRequest.BodyLength], out LogonProofRequest? proofOk));
        Assert.NotNull(proofOk);

        // AuthProtocols.TryGet: only build 5875 is supported.
        foreach (uint unsupported in new uint[] { 0, 1, 5464, 5874, 5876, 6005, 8606, 12340, ushort.MaxValue, uint.MaxValue })
        {
            Assert.False(AuthProtocols.TryGet(unsupported, out _));
        }

        Assert.True(AuthProtocols.TryGet(ClientBuild.Vanilla1121, out IAuthProtocol protocol));
        Assert.Same(Build5875AuthProtocol.Instance, protocol);
        Assert.Equal(ClientBuild.Vanilla1121, protocol.Build);
    }

    /// <summary>
    /// Verifies that <c>LogonSession</c> rejects out-of-order state transitions, invalid account characters,
    /// degenerate SRP credentials, malformed integrity hashes, bad PIN configurations, and out-of-bounds patch resumes.
    /// </summary>
    [Fact]
    public async Task LogonSession_StateTransitions_DegenerateCredentials_AndInvalidInputs_AreRejectedCleanly()
    {
        var accounts = new InMemoryAccountStore();
        // Account with degenerate zero verifier (rejected by Srp6Server.TryCreate -> FailNoAccess).
        await accounts.CreateAsync(new Account
        {
            Id = 1,
            Username = "DEGENERATE",
            Salt = WowSrp6.GenerateSalt(),
            Verifier = new byte[32],
            Status = AccountStatus.Active,
        });

        // Account with invalid FixedPin SecurityInfo ("12a4" contains non-digit).
        byte[] pinSalt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Id = 2,
            Username = "BADPIN",
            Salt = pinSalt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(pinSalt, "BADPIN", Password), WowSrp6.KeyLength),
            LockFlags = AccountLockFlags.FixedPin | AccountLockFlags.AlwaysEnforce,
            SecurityInfo = "12a4",
            Status = AccountStatus.Active,
        });

        // Valid account for testing malformed IntegrityHashes and securityFlags.
        byte[] goodSalt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Id = 3,
            Username = Username,
            Salt = goodSalt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(goodSalt, Username, Password), WowSrp6.KeyLength),
            Status = AccountStatus.Active,
        });

        var options = new AuthOptions
        {
            StrictUsernameCharset = true,
            StrictVersionCheck = true,
            IntegrityHashes =
            [
                new ClientIntegrityHashOptions
                {
                    Build = ClientBuild.Vanilla1121,
                    Os = "Win",
                    Platform = "x86",
                    Hash = new string('Z', 40), // 40 chars but invalid hex -> triggers FormatException catch in VerifyVersion
                },
            ],
        };

        await using (NetworkStream client = await StartSessionAsync(accounts, new InMemoryRealmStore([]), options))
        {
            // 1. Out-of-order LogonProof before any challenge -> UnknownAccount.
            byte[] orphanProof = new byte[1 + LogonProofRequest.BodyLength];
            orphanProof[0] = (byte)AuthCommand.LogonProof;
            await client.WriteAsync(orphanProof);
            byte[] orphanReply = await ReadExactAsync(client, 2);
            Assert.Equal([(byte)AuthCommand.LogonProof, (byte)AuthResult.UnknownAccount], orphanReply);

            // 2. Out-of-order RealmList before authentication -> ignored without reply.
            await client.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0 });

            // 3. Degenerate SRP account -> FailNoAccess.
            await client.WriteAsync(BuildChallengePacket("DEGENERATE"));
            byte[] degReply = await ReadExactAsync(client, 3);
            Assert.Equal((byte)AuthResult.FailNoAccess, degReply[2]);

            // 4. Account with invalid FixedPin digits -> challenge succeeds with PIN prompt, proof fails with UnknownAccount.
            await client.WriteAsync(BuildChallengePacket("BADPIN"));
            (byte pinChalResult, byte[] pinB, byte[] pinS) = await ReadChallengeReplyWithPinAsync(client);
            Assert.Equal((byte)AuthResult.Success, pinChalResult);
            byte[] proofWithPin = BuildValidProofPacket("BADPIN", Password, pinS, pinB, securityFlags: 1, pinData: new byte[36]);
            await client.WriteAsync(proofWithPin);
            byte[] pinProofReply = await ReadExactAsync(client, 2);
            Assert.Equal([(byte)AuthCommand.LogonProof, (byte)AuthResult.UnknownAccount], pinProofReply);

            // 5. Challenge + Proof with unsupported SecurityFlags (0x02) -> UnknownAccount.
            await client.WriteAsync(BuildChallengePacket(Username));
            (byte chalRes1, byte[] b1, byte[] s1) = await ReadChallengeReplyAsync(client);
            Assert.Equal((byte)AuthResult.Success, chalRes1);
            byte[] badFlagsProof = BuildValidProofPacket(Username, Password, s1, b1, securityFlags: 0x02);
            await client.WriteAsync(badFlagsProof);
            byte[] badFlagsReply = await ReadExactAsync(client, 2);
            Assert.Equal([(byte)AuthCommand.LogonProof, (byte)AuthResult.UnknownAccount], badFlagsReply);

            // 6. Challenge + Proof with valid SRP math but malformed IntegrityHashes hex under StrictVersionCheck -> VersionInvalid.
            await client.WriteAsync(BuildChallengePacket(Username));
            (byte chalRes2, byte[] b2, byte[] s2) = await ReadChallengeReplyAsync(client);
            Assert.Equal((byte)AuthResult.Success, chalRes2);
            byte[] validProof = BuildValidProofPacket(Username, Password, s2, b2);
            await client.WriteAsync(validProof);
            byte[] badIntegrityReply = await ReadExactAsync(client, 2);
            Assert.Equal([(byte)AuthCommand.LogonProof, (byte)AuthResult.VersionInvalid], badIntegrityReply);

            // 7. Out-of-order XferCancel without an offered patch -> closes connection.
            await client.WriteAsync(new byte[] { (byte)AuthCommand.XferCancel });
            Assert.True(await ReadsEofAsync(client));
        }

        // 8. Out-of-bounds XferResume offset (>= patch.Size) closes connection cleanly.
        string patchFile = Path.Combine(_tempDir, "5464enUS.mpq");
        File.WriteAllBytes(patchFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _tempDir });
        await using (NetworkStream patchClient = await StartSessionAsync(accounts, new InMemoryRealmStore([]), new AuthOptions(), catalog))
        {
            await patchClient.WriteAsync(BuildChallengePacket(Username, build: 5464));
            await ReadChallengeReplyAsync(patchClient);
            byte[] dummyProof = new byte[1 + LogonProofRequest.BodyLength];
            dummyProof[0] = (byte)AuthCommand.LogonProof;
            await patchClient.WriteAsync(dummyProof);
            await ReadExactAsync(patchClient, 33); // 2-byte proof VersionUpdate + 31-byte XferInitiate

            byte[] badResume = new byte[9];
            badResume[0] = (byte)AuthCommand.XferResume;
            BinaryPrimitives.WriteUInt64LittleEndian(badResume.AsSpan(1), 8UL); // offset == patch.Size (out of bounds)
            await patchClient.WriteAsync(badResume);
            Assert.True(await ReadsEofAsync(patchClient));
        }
    }

    /// <summary>
    /// Verifies that truncated frames / peer disconnects at every read boundary in <c>LogonSession</c>
    /// are classified as expected disconnects by <c>LogonSession.IsExpectedDisconnect</c>.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x00, 0x08 })] // truncated challenge header
    [InlineData(new byte[] { 0x00, 0x08, 35, 0, 1, 2, 3 })] // truncated challenge body
    [InlineData(new byte[] { 0x01, 1, 2, 3, 4 })] // truncated logon proof body
    [InlineData(new byte[] { 0x02, 0x08, 35, 0, 1, 2 })] // truncated reconnect challenge body
    [InlineData(new byte[] { 0x03, 1, 2, 3 })] // truncated reconnect proof body
    [InlineData(new byte[] { 0x10, 0, 0 })] // truncated realm list request
    [InlineData(new byte[] { 0xFF })] // unknown command opcode
    public async Task LogonSession_TruncatedFramesAndUnknownOpcodes_CloseCleanly(byte[] partialPayload)
    {
        (NetworkStream client, Task serverTask) = await StartRawSessionWithTaskAsync(
            new InMemoryAccountStore(),
            new InMemoryRealmStore([]));
        await using (client)
        {
            await client.WriteAsync(partialPayload);
        }

        client.Dispose();
        try
        {
            await serverTask.WaitAsync(IoBudget);
        }
        catch (Exception ex)
        {
            Assert.True(IsExpectedDisconnect(ex), $"Unexpected exception escaped for payload {Convert.ToHexString(partialPayload)}: {ex}");
        }
    }

    // =================================================================================================
    // 4. DETERMINISTIC SEEDED FUZZ TESTS (Seeds: 1, 42, 20251004)
    // =================================================================================================

    /// <summary>
    /// Deterministic seeded fuzz test for <c>CMD_AUTH_LOGON_CHALLENGE</c> (<c>LogonChallengeRequest.TryParse</c>
    /// and <c>LogonSession</c> wire handling).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_CmdAuthLogonChallenge_DecoderAndSession_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        byte[] validPacket = BuildChallengePacket(Username);
        byte[] validBody = validPacket[4..];

        // 1. Fuzz LogonChallengeRequest.TryParse over 1,500 deterministic mutations.
        for (int i = 0; i < 1_500; i++)
        {
            byte[] mutatedBody = MutateBytes(validBody, rng);
            bool parsed = LogonChallengeRequest.TryParse(mutatedBody, out LogonChallengeRequest? request);
            if (parsed)
            {
                Assert.NotNull(request);
                Assert.True(mutatedBody.Length >= 30);
                Assert.Equal(mutatedBody[29], request!.Username.Length);
                Assert.True(30 + request.Username.Length <= mutatedBody.Length);
            }
        }

        // 2. Fuzz live LogonSession with mutated CMD_AUTH_LOGON_CHALLENGE packets.
        var accounts = await CreateSeededAccountStoreAsync();
        for (int s = 0; s < 25; s++)
        {
            byte[] mutatedBody = MutateBytes(validBody, rng);
            byte[] wire = new byte[4 + mutatedBody.Length];
            wire[0] = (byte)AuthCommand.LogonChallenge;
            wire[1] = (byte)rng.Next(0, 256);
            ushort declaredSize = rng.Next(4) == 0 ? (ushort)rng.Next(0, 80) : (ushort)mutatedBody.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(2, 2), declaredSize);
            mutatedBody.CopyTo(wire.AsSpan(4));

            await RunFuzzStreamAgainstSessionAsync(accounts, wire);
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test for <c>CMD_AUTH_LOGON_PROOF</c> (<c>LogonProofRequest.TryParse</c>
    /// and <c>LogonSession</c> wire handling after a valid challenge).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_CmdAuthLogonProof_DecoderAndSession_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        byte[] baseBody = new byte[LogonProofRequest.BodyLength];
        rng.NextBytes(baseBody);

        // 1. Fuzz LogonProofRequest.TryParse over 1,500 deterministic mutations.
        for (int i = 0; i < 1_500; i++)
        {
            byte[] mutated = MutateBytes(baseBody, rng);
            bool ok = LogonProofRequest.TryParse(mutated, out LogonProofRequest? request);
            Assert.Equal(mutated.Length >= LogonProofRequest.BodyLength, ok);
            if (ok)
            {
                Assert.NotNull(request);
                Assert.Equal(LogonProofRequest.PublicKeyLength, request!.ClientPublicKey.Length);
                Assert.Equal(LogonProofRequest.ProofLength, request.ClientProof.Length);
                Assert.Equal(LogonProofRequest.CrcLength, request.CrcHash.Length);
            }
        }

        // 2. Fuzz live LogonSession by sending a valid challenge followed by mutated CMD_AUTH_LOGON_PROOF frames.
        var accounts = await CreateSeededAccountStoreAsync();
        byte[] validChallenge = BuildChallengePacket(Username);
        for (int s = 0; s < 25; s++)
        {
            byte[] mutatedProof = MutateBytes(baseBody, rng);
            byte[] streamBytes = [.. validChallenge, (byte)AuthCommand.LogonProof, .. mutatedProof];
            await RunFuzzStreamAgainstSessionAsync(accounts, streamBytes);
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test for <c>CMD_AUTH_RECONNECT_CHALLENGE</c> frames fed into <c>LogonSession</c>.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_CmdAuthReconnectChallenge_Session_NeverEscapes(int seed)
    {
        var rng = new Random(seed);
        var accounts = await CreateSeededAccountStoreAsync();
        byte[] validReconnectChallenge = BuildReconnectChallengePacket(Username);
        byte[] validBody = validReconnectChallenge[4..];

        for (int s = 0; s < 30; s++)
        {
            byte[] mutatedBody = MutateBytes(validBody, rng);
            byte[] wire = new byte[4 + mutatedBody.Length];
            wire[0] = (byte)AuthCommand.ReconnectChallenge;
            wire[1] = (byte)rng.Next(0, 256);
            ushort declaredSize = rng.Next(4) == 0 ? (ushort)rng.Next(0, 80) : (ushort)mutatedBody.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(2, 2), declaredSize);
            mutatedBody.CopyTo(wire.AsSpan(4));

            await RunFuzzStreamAgainstSessionAsync(accounts, wire);
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test for <c>CMD_AUTH_RECONNECT_PROOF</c> frames fed into <c>LogonSession</c>
    /// both with and without a preceding <c>CMD_AUTH_RECONNECT_CHALLENGE</c>.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_CmdAuthReconnectProof_Session_NeverEscapes(int seed)
    {
        var rng = new Random(seed);
        var accounts = await CreateSeededAccountStoreAsync();
        byte[] validReconnectChallenge = BuildReconnectChallengePacket(Username);
        byte[] baseProofBody = new byte[57];
        rng.NextBytes(baseProofBody);
        baseProofBody[^1] = 0;

        for (int s = 0; s < 30; s++)
        {
            byte[] mutatedProof = MutateBytes(baseProofBody, rng);
            byte[] wire = (s & 1) == 0
                ? [.. validReconnectChallenge, (byte)AuthCommand.ReconnectProof, .. mutatedProof]
                : [(byte)AuthCommand.ReconnectProof, .. mutatedProof];

            await RunFuzzStreamAgainstSessionAsync(accounts, wire);
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test for <c>CMD_REALM_LIST</c> requests and <c>RealmListWriter.Build</c>.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_CmdRealmList_WriterAndSession_NeverEscapes(int seed)
    {
        var rng = new Random(seed);

        // 1. Fuzz RealmListWriter.Build with randomized realm entries and character counts.
        for (int i = 0; i < 200; i++)
        {
            int count = rng.Next(0, 16);
            var realms = new List<RealmEntry>(count);
            for (int r = 0; r < count; r++)
            {
                realms.Add(new RealmEntry
                {
                    Id = r + 1,
                    Name = "Realm_" + rng.Next(1, 10000),
                    Address = $"127.0.0.{rng.Next(1, 255)}:{rng.Next(1024, 65535)}",
                    Type = (RealmType)rng.Next(0, 10),
                    Flags = (RealmFlags)rng.Next(0, 256),
                    Population = (float)(rng.NextDouble() * 4.0),
                    Category = (byte)rng.Next(0, 256),
                });
            }

            ReadOnlyMemory<byte> built = RealmListWriter.Build(realms, (byte)rng.Next(0, 256));
            Assert.True(built.Length >= 10);
            Assert.Equal((byte)AuthCommand.RealmList, built.Span[0]);
            ushort bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(built.Span.Slice(1, 2));
            Assert.Equal(built.Length - 3, bodyLen);
            Assert.Equal((byte)count, built.Span[7]);
        }

        // 2. Fuzz CMD_REALM_LIST wire bytes against LogonSession.
        var accounts = await CreateSeededAccountStoreAsync();
        for (int s = 0; s < 25; s++)
        {
            byte[] payload = MutateBytes([0, 0, 0, 0], rng);
            byte[] wire = [(byte)AuthCommand.RealmList, .. payload];
            await RunFuzzStreamAgainstSessionAsync(accounts, wire);
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test feeding arbitrary multi-command byte streams into <c>LogonSession</c>,
    /// verifying that no unhandled exceptions escape and invalid frames are cleanly rejected.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_ArbitraryByteStreams_IntoLogonSession_NeverEscapes(int seed)
    {
        var rng = new Random(seed);
        var accounts = await CreateSeededAccountStoreAsync();

        byte[][] templates =
        [
            BuildChallengePacket(Username),
            BuildReconnectChallengePacket(Username),
            [(byte)AuthCommand.LogonProof, .. new byte[LogonProofRequest.BodyLength]],
            [(byte)AuthCommand.ReconnectProof, .. new byte[57]],
            [(byte)AuthCommand.RealmList, 0, 0, 0, 0],
            [(byte)AuthCommand.XferAccept],
            [(byte)AuthCommand.XferResume, 0, 0, 0, 0, 0, 0, 0, 0],
            [(byte)AuthCommand.XferCancel],
        ];

        for (int s = 0; s < 40; s++)
        {
            var streamBytes = new List<byte>();
            int segments = rng.Next(1, 6);
            for (int seg = 0; seg < segments; seg++)
            {
                if (rng.Next(3) == 0)
                {
                    byte[] noise = new byte[rng.Next(0, 96)];
                    rng.NextBytes(noise);
                    streamBytes.AddRange(noise);
                }
                else
                {
                    byte[] template = templates[rng.Next(templates.Length)];
                    streamBytes.AddRange(MutateBytes(template, rng));
                }
            }

            await RunFuzzStreamAgainstSessionAsync(accounts, [.. streamBytes]);
        }
    }

    // =================================================================================================
    // HELPERS & TEST DOUBLES
    // =================================================================================================

    private static bool IsExpectedDisconnect(Exception exception)
        => exception is OperationCanceledException
            or EndOfStreamException
            or SocketException
            or ObjectDisposedException
            or (IOException and not (FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException or PathTooLongException or FileLoadException));

    private static async Task<InMemoryAccountStore> CreateSeededAccountStoreAsync()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, Username, Password);
        await accounts.CreateAsync(new Account
        {
            Id = 1,
            Username = Username,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength),
            SessionKey = Enumerable.Range(1, 40).Select(i => (byte)i).ToArray(),
            Status = AccountStatus.Active,
        });
        return accounts;
    }

    private static async Task RunFuzzStreamAgainstSessionAsync(IAccountStore accounts, byte[] wireBytes)
    {
        var realms = new InMemoryRealmStore([new RealmEntry { Id = 1, Name = "FuzzRealm", Address = "127.0.0.1:8085" }]);
        var bans = new InMemoryBanStore();
        var options = new AuthOptions
        {
            AutocreateAccounts = true,
            StrictUsernameCharset = true,
            MaxSessionDurationSeconds = 5,
            ReadTimeoutSeconds = 2,
        };

        (NetworkStream client, Task serverTask) = await StartRawSessionWithTaskAsync(accounts, realms, options, bans);
        try
        {
            await using (client)
            {
                await client.WriteAsync(wireBytes);
                client.Socket.Shutdown(SocketShutdown.Send);

                byte[] drain = new byte[256];
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                while (await client.ReadAsync(drain, cts.Token) > 0)
                {
                }
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            // Expected when the server resets/closes the socket while the fuzzer writes or drains.
        }

        try
        {
            await serverTask.WaitAsync(IoBudget);
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedDisconnect(ex),
                $"Unhandled exception escaped LogonSession for fuzzed stream ({wireBytes.Length} bytes): {ex}");
        }
    }

    private static byte[] MutateBytes(byte[] shape, Random random)
    {
        byte[] data = (byte[])shape.Clone();
        switch (random.Next(8))
        {
            case 0: // truncate
                return data[..random.Next(data.Length + 1)];
            case 1: // flip bits
                for (int n = random.Next(1, 5); n > 0 && data.Length > 0; n--)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                return data;
            case 2: // extreme byte values (0x00, 0x7F, 0x80, 0xFF)
                if (data.Length > 0)
                {
                    ReadOnlySpan<byte> extremes = [0x00, 0x01, 0x10, 0x11, 0x1E, 0x1F, 0x20, 0x2F, 0x30, 0x7F, 0x80, 0xFF];
                    data[random.Next(data.Length)] = extremes[random.Next(extremes.Length)];
                }

                return data;
            case 3: // append random tail
                {
                    byte[] tail = new byte[random.Next(1, 48)];
                    random.NextBytes(tail);
                    return [.. data, .. tail];
                }
            case 4: // delete slice
                if (data.Length > 1)
                {
                    int at = random.Next(data.Length);
                    int count = random.Next(1, Math.Min(8, data.Length - at) + 1);
                    return [.. data[..at], .. data[(at + count)..]];
                }

                return data;
            case 5: // insert slice
                {
                    int at = random.Next(data.Length + 1);
                    byte[] ins = new byte[random.Next(1, 8)];
                    random.NextBytes(ins);
                    return [.. data[..at], .. ins, .. data[at..]];
                }
            case 6: // replace NULs with printable or control chars
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] == 0)
                    {
                        data[i] = (byte)random.Next(1, 128);
                    }
                }

                return data;
            default: // purely random buffer
                {
                    byte[] noise = new byte[random.Next(0, (data.Length * 2) + 1)];
                    random.NextBytes(noise);
                    return noise;
                }
        }
    }

    private static async Task<NetworkStream> StartSessionAsync(
        IAccountStore accounts,
        IRealmStore realms,
        AuthOptions? options = null,
        PatchCatalog? patches = null)
    {
        (NetworkStream client, _) = await StartRawSessionWithTaskAsync(accounts, realms, options, patches: patches, swallowDisconnect: true);
        return client;
    }

    private static async Task<(NetworkStream Client, Task ServerTask)> StartRawSessionWithTaskAsync(
        IAccountStore accounts,
        IRealmStore realms,
        AuthOptions? options = null,
        IBanStore? bans = null,
        PatchCatalog? patches = null,
        bool swallowDisconnect = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = accepted.GetStream();
            var session = new LogonSession(
                stream,
                accounts,
                realms,
                options ?? new AuthOptions(),
                NullLogger.Instance,
                "127.0.0.1:50000",
                banStore: bans,
                patches: patches);
            try
            {
                await session.RunAsync(CancellationToken.None);
            }
            catch (Exception ex) when (swallowDisconnect && IsExpectedDisconnect(ex))
            {
            }
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return (new NetworkStream(client.Client, ownsSocket: true), serverTask);
    }

    private static byte[] BuildChallengePacket(string username, ushort build = ClientBuild.Vanilla1121)
        => BuildChallengeFrame(AuthCommand.LogonChallenge, username, build);

    private static byte[] BuildReconnectChallengePacket(string username, ushort build = ClientBuild.Vanilla1121)
        => BuildChallengeFrame(AuthCommand.ReconnectChallenge, username, build);

    private static byte[] BuildChallengeFrame(AuthCommand command, string username, ushort build)
    {
        byte[] name = Encoding.ASCII.GetBytes(username);
        var body = new List<byte>(30 + name.Length);
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 12, 1]);
        byte[] buildBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buildBytes, build);
        body.AddRange(buildBytes);
        body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray());
        body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[8]);
        body.Add((byte)name.Length);
        body.AddRange(name);

        byte[] packet = new byte[4 + body.Count];
        packet[0] = (byte)command;
        packet[1] = 0x08;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), (ushort)body.Count);
        body.CopyTo(packet, 4);
        return packet;
    }

    private static byte[] BuildValidProofPacket(
        string username,
        string password,
        byte[] salt,
        byte[] serverB,
        byte securityFlags = 0,
        byte[]? pinData = null)
    {
        byte[] aBytes = RandomNumberGenerator.GetBytes(19);
        BigInteger a = new(aBytes, isUnsigned: true, isBigEndian: false);
        BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
        BigInteger b = WowSrp6.FromLittleEndian(serverB);
        BigInteger x = WowSrp6.ComputeX(salt, username, password);
        BigInteger u = Srp6Math.Scrambler(publicA, b);
        BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
        BigInteger numerator = ((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
        BigInteger s = BigInteger.ModPow(numerator, a + (u * x), WowSrp6.N);
        byte[] sessionKey = Srp6Math.Interleave(s);
        byte[] m1 = Srp6Math.ClientProof(username, salt, publicA, b, sessionKey);

        var packet = new List<byte>(1 + LogonProofRequest.BodyLength + (pinData?.Length ?? 0))
        {
            (byte)AuthCommand.LogonProof,
        };
        packet.AddRange(WowSrp6.ToFixedLittleEndian(publicA, WowSrp6.KeyLength));
        packet.AddRange(m1);
        packet.AddRange(new byte[20]); // crc_hash
        packet.Add(0); // number_of_keys
        packet.Add(securityFlags);
        if (pinData is not null)
        {
            packet.AddRange(pinData);
        }

        return [.. packet];
    }

    private static async Task<(byte Result, byte[] B, byte[] Salt)> ReadChallengeReplyAsync(NetworkStream stream)
    {
        byte[] head = await ReadExactAsync(stream, 3);
        if (head[2] != (byte)AuthResult.Success)
        {
            return (head[2], [], []);
        }

        byte[] rest = await ReadExactAsync(stream, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        return (head[2], rest[..32], rest.AsSpan(32 + 1 + 1 + 1 + 32, 32).ToArray());
    }

    private static async Task<(byte Result, byte[] B, byte[] Salt)> ReadChallengeReplyWithPinAsync(NetworkStream stream)
    {
        (byte result, byte[] b, byte[] salt) = await ReadChallengeReplyAsync(stream);
        if (result == (byte)AuthResult.Success)
        {
            await ReadExactAsync(stream, 20); // gridSeed(4) + pinSalt(16)
        }

        return (result, b, salt);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        using var cts = new CancellationTokenSource(IoBudget);
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task<bool> ReadsEofAsync(NetworkStream stream)
    {
        byte[] one = new byte[1];
        using var cts = new CancellationTokenSource(IoBudget);
        try
        {
            return await stream.ReadAsync(one, cts.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class ThrowingAccountStore(Exception exception) : IAccountStore
    {
        public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromException<Account?>(exception);

        public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default)
            => Task.FromException<Account>(exception);

        public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task UpdateLoginAsync(string username, byte[] sessionKey, string? address, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(exception);
    }

    private sealed class FailOnUpdateAccountStore(Account account, Exception updateException) : IAccountStore
    {
        public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<Account?>(account);

        public Task<Account> CreateAsync(Account newAccount, CancellationToken cancellationToken = default)
            => Task.FromException<Account>(updateException);

        public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
            => Task.FromException(updateException);

        public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default)
            => Task.FromException(updateException);

        public Task UpdateLoginAsync(string username, byte[] sessionKey, string? address, CancellationToken cancellationToken = default)
            => Task.FromException(updateException);

        public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(updateException);
    }

    private sealed class ThrowingRealmStore(Exception exception) : IRealmStore
    {
        public Task<IReadOnlyList<RealmEntry>> GetRealmsAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<RealmEntry>>(exception);
    }

    private sealed class CancellingRealmStore : IRealmStore
    {
        public Task<IReadOnlyList<RealmEntry>> GetRealmsAsync(CancellationToken cancellationToken = default)
            => Task.FromCanceled<IReadOnlyList<RealmEntry>>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));
    }
}
