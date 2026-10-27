using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Cluster.Tooling;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Resilience;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using ArcaneCore.Kernel.WorldData.Pools;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.Kernel.WorldData.Threat;
using ArcaneCore.Kernel.WorldData.Totems;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Fuzz;

/// <summary>
/// Exception-path and deterministic seeded fuzz tests across <c>ArcaneCore.Kernel</c>.
/// </summary>
public sealed class KernelFuzzAndExceptionTests
{
    // =========================================================================
    // 1. Configuration & .conf Parser Exception Tests
    // =========================================================================

    [Fact]
    public void ConfCatalog_FromJson_ThrowsOnNullEmptyOrMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() => ConfCatalog.FromJson("null"));
        Assert.Throws<InvalidDataException>(() => ConfCatalog.FromJson("{\"keys\":null}"));
        Assert.ThrowsAny<JsonException>(() => ConfCatalog.FromJson("{malformed"));
    }

    [Fact]
    public void ConfConfigurationProvider_Load_ThrowsFileNotFoundWhenRequiredFileIsMissing()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), "arcanecore-missing-" + Guid.NewGuid().ToString("N") + ".conf");
        var requiredSource = new ConfConfigurationSource
        {
            Path = missingPath,
            Optional = false,
            ReloadOnChange = false,
        };
        using var requiredProvider = new ConfConfigurationProvider(requiredSource);
        Assert.Throws<FileNotFoundException>(() => requiredProvider.Load());

        var optionalSource = new ConfConfigurationSource
        {
            Path = missingPath,
            Optional = true,
            ReloadOnChange = false,
        };
        using var optionalProvider = new ConfConfigurationProvider(optionalSource);
        optionalProvider.Load();
        Assert.False(optionalProvider.TryGet("World:Port", out _));
    }

    [Fact]
    public void ConfDocument_Parse_InvalidUtf8Bytes_ThrowsConfParseException()
    {
        byte[] invalidUtf8 = [0xFF, 0xFE, (byte)'W', (byte)'o', (byte)'r', (byte)'l', (byte)'d', (byte)'=', (byte)'1'];
        ConfParseException ex = Assert.Throws<ConfParseException>(() => ConfDocument.Parse(invalidUtf8, "broken.conf"));
        Assert.Single(ex.Errors);
        Assert.StartsWith("broken.conf:", ex.Errors[0], StringComparison.Ordinal);
        Assert.Contains("UTF-8", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Section]", "section headers are not supported")]
    [InlineData("World.Port 8085", "no '='")]
    [InlineData("= 8085", "the key is empty")]
    [InlineData("   = 8085", "the key is empty")]
    [InlineData("World Port = 8085", "contains whitespace or a quote")]
    [InlineData("World\tPort = 8085", "contains whitespace or a quote")]
    [InlineData("World\"Port = 8085", "contains whitespace or a quote")]
    [InlineData(".World.Port = 8085", "empty segment")]
    [InlineData("World.Port. = 8085", "empty segment")]
    [InlineData("World..Port = 8085", "empty segment")]
    [InlineData(":World:Port = 8085", "empty segment")]
    [InlineData("World:Port: = 8085", "empty segment")]
    [InlineData("World::Port = 8085", "empty segment")]
    [InlineData("World.Name = \"unterminated", "is not closed")]
    [InlineData("World.Name = \"escaped\\\"", "is not closed")]
    [InlineData("World.Name = \"closed\" extra", "after the closing quote")]
    public void ConfDocument_ParseText_MalformedLines_ThrowsConfParseException(string line, string expectedFragment)
    {
        ConfParseException ex = Assert.Throws<ConfParseException>(() => ConfDocument.ParseText(line, "test.conf"));
        Assert.StartsWith("test.conf:", ex.Errors[0], StringComparison.Ordinal);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfDocument_ParseText_DuplicateKeyAcrossDotAndColonAndCase_ThrowsConfParseException()
    {
        const string text = "World.Port = 8085\nworld:port = 8086\n";
        ConfParseException ex = Assert.Throws<ConfParseException>(() => ConfDocument.ParseText(text, "dup.conf"));
        Assert.Contains("duplicate key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfFiles_Attach_ThrowsOnMissingExplicitPathOrMissingJsonSource()
    {
        string missingFile = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".conf");
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        ConfLocationException locEx = Assert.Throws<ConfLocationException>(
            () => ConfFiles.Attach(builder, ConfigDaemon.World, missingFile));
        Assert.Contains("--config", locEx.Message, StringComparison.Ordinal);
        Assert.Contains(missingFile, locEx.Message, StringComparison.Ordinal);

        string tempFile = Path.Combine(Path.GetTempPath(), "arcanecore-test-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(tempFile, "World.Port = 8085\n", Encoding.UTF8);
        try
        {
            HostApplicationBuilder emptySourcesBuilder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            emptySourcesBuilder.Configuration.Sources.Clear();
            Assert.Throws<InvalidOperationException>(
                () => ConfFiles.Attach(emptySourcesBuilder, ConfigDaemon.World, tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    [InlineData("line1\r\nline2")]
    public void ConfValue_Quote_And_ConfWriter_ThrowFormatExceptionOnMultilineValue(string multiline)
    {
        Assert.Throws<FormatException>(() => ConfValue.Quote(multiline));

        var catalog = new ConfCatalog(
        [
            new ConfKey(
                Path: "World:Motd",
                Section: "World",
                Type: "string",
                Kind: ConfKey.Scalar,
                ElementType: string.Empty,
                EnumValues: null,
                Default: "Welcome",
                DefaultEntries: [],
                Daemons: ConfigDaemon.World,
                Reload: "live",
                Meaning: "Message of the day.",
                Allowed: "text",
                Docs: "docs/guide/configuration.md"),
        ]);

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["World:Motd"] = multiline })
            .Build();

        Assert.Throws<FormatException>(() => ConfWriter.WriteCurrent(catalog, ConfigDaemon.World, config));
    }

    [Fact]
    public void ConfigChecks_And_ConfigCheckDiscovery_ThrowArgumentNullExceptionOnNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new AnnounceConfigChecks().Check(null!));
        Assert.Throws<ArgumentNullException>(() => new ResilienceConfigChecks().Check(null!));
        Assert.Throws<ArgumentNullException>(() => ConfigCheckDiscovery.ConcreteTypes((IEnumerable<Assembly>)null!));
        Assert.Throws<ArgumentNullException>(() => ConfigCheckDiscovery.ConcreteTypes((CodeAssemblies)null!));

        CodeAssemblies assemblies = CodeAssemblies.Of(typeof(ConfCatalog).Assembly);
        Assert.Throws<ArgumentNullException>(() => ConfigCheckDiscovery.NotRun((IEnumerable<Assembly>)null!, []));
        Assert.Throws<ArgumentNullException>(() => ConfigCheckDiscovery.NotRun((CodeAssemblies)null!, []));
        Assert.Throws<ArgumentNullException>(() => ConfigCheckDiscovery.NotRun(assemblies, null!));
    }

    [Fact]
    public void LoggingConfigChecks_And_AddArcaneLogging_ThrowOnNullOrInvalidOptions()
    {
        Assert.Throws<ArgumentNullException>(() => LoggingConfigChecks.ThrowIfInvalid(null!));
        Assert.Throws<ArgumentNullException>(() => ArcaneLoggingServiceCollectionExtensions.AddArcaneCoreLogging(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() => ArcaneLoggingServiceCollectionExtensions.AddArcaneCoreLogging(new ServiceCollection(), null!));
        Assert.Throws<ArgumentNullException>(() => ArcaneLoggerProvider.BuildSinks(null!));
        Assert.Throws<OptionsValidationException>(() => ArcaneLoggerProvider.BuildSinks(new ArcaneLoggingOptions { Console = { QueueCapacity = 0 } }));

        var invalidOptions = new ArcaneLoggingOptions
        {
            Timestamps = (TimestampKind)99,
        };
        invalidOptions.Console.Mode = (ConsoleMode)99;
        invalidOptions.Console.QueueCapacity = 0;
        invalidOptions.File.Enabled = true;
        invalidOptions.File.Path = "   ";
        invalidOptions.File.RollSizeMb = -1;
        invalidOptions.File.Retain = -2;
        invalidOptions.File.QueueCapacity = LoggingConfigChecks.MaxQueueCapacity + 1;
        Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(invalidOptions));

        var collidingPaths = new ArcaneLoggingOptions();
        collidingPaths.File.Enabled = true;
        collidingPaths.File.Path = "logs/same.log";
        collidingPaths.Json.Enabled = true;
        collidingPaths.Json.Path = "logs/same.log";
        Assert.Throws<OptionsValidationException>(() => LoggingConfigChecks.ThrowIfInvalid(collidingPaths));

        IConfigurationRoot badConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:ArcaneCore:Console:QueueCapacity"] = "0",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddArcaneCoreLogging(badConfig));
    }

    // =========================================================================
    // 2. Logging & Diagnostics Exception Tests
    // =========================================================================

    [Fact]
    public void LoggingWritersAndGates_ThrowOnInvalidConstructorArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogGate(TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueuedLineWriter(NullLineWriter.Instance, 0, "bad", _ => string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueuedLineWriter(NullLineWriter.Instance, -5, "bad", _ => string.Empty));

        RollingFilePolicy policy = RollingFilePolicy.FromOptions(10, true, 5);
        Assert.Throws<ArgumentNullException>(() => new RollingFileWriter(null!, policy));
        Assert.Throws<ArgumentException>(() => new RollingFileWriter(string.Empty, policy));
        Assert.Throws<ArgumentException>(() => new RollingFileWriter("   ", policy));
    }

    [Fact]
    public void DiagnosticsTypes_ThrowArgumentExceptionsOnNullOrOutOfRangeArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AnomalyLedger(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new AnomalyLedger(new AnomalyLedgerLimits(), null!));

        Assert.Throws<ArgumentNullException>(() => CrashHandler.Install(null!, StandardErrorCrashSink.Instance));
        Assert.Throws<ArgumentNullException>(() => CrashHandler.Install(new DiagnosticsOptions(), null!));
        Assert.Throws<ArgumentNullException>(() => DiagnosticsHostingExtensions.UseArcaneDiagnostics(null!));

        Assert.Throws<ArgumentNullException>(() => new FaultLedger(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new FaultLedger(new FaultLedgerLimits(), null!));

        var ledger = new FaultLedger();
        Assert.Throws<ArgumentNullException>(() => ledger.Configure(null!));
        Assert.Throws<ArgumentNullException>(() => ledger.Record("site", null!));
        Assert.Throws<ArgumentNullException>(() => ledger.RecordIfUnexpected("site", null!));
        Assert.Throws<ArgumentNullException>(() => ledger.Log(null!, LogLevel.Error, "site", new InvalidOperationException("boom"), "msg"));
        Assert.Throws<ArgumentNullException>(() => FaultLedger.SignatureOf("site", null!, out _, out _));
        Assert.Throws<ArgumentNullException>(() => FaultLedger.FormatCondensedStack(null!));
        Assert.Throws<ArgumentNullException>(() => FaultLedger.FormatExceptionChain(null!));

        Assert.Throws<ArgumentNullException>(() => Invariant.Configure(null!, null));
        Assert.Throws<ArgumentNullException>(() => SelfDiagnosisReport.Format((SelfDiagnosisValues)null!));

        Assert.Throws<ArgumentNullException>(() => new LatencyHistogram(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram(TimeProvider.System, TimeSpan.FromSeconds(1), slices: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram(TimeProvider.System, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram(TimeProvider.System, TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>(() => new LatencyRegistry(null!));
    }

    // =========================================================================
    // 3. Resilience Exception Tests
    // =========================================================================

    [Fact]
    public async Task Bulkhead_ThrowsOnInvalidOptionsNullDelegatesAndCapacityExhaustion()
    {
        Assert.Throws<ArgumentNullException>(() => new Bulkhead(null!));
        Assert.Throws<ArgumentException>(() => new Bulkhead(new BulkheadOptions { Name = "  " }));
        Assert.Throws<ArgumentException>(() => new Bulkhead(new BulkheadOptions { Name = "bh", MaxConcurrency = 0 }));
        Assert.Throws<ArgumentException>(() => new Bulkhead(new BulkheadOptions { Name = "bh", MaxConcurrency = 1, MaxQueue = -1 }));

        using var bulkhead = new Bulkhead(new BulkheadOptions { Name = "db", MaxConcurrency = 1, MaxQueue = 0 });
        await Assert.ThrowsAsync<ArgumentNullException>(() => bulkhead.ExecuteAsync<int, int>(null!, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => bulkhead.ExecuteAsync<int>(null!, 0).AsTask());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = bulkhead.ExecuteAsync(
            static async (s, _) =>
            {
                s.entered.SetResult();
                await s.release.Task;
            },
            (entered, release)).AsTask();

        await entered.Task;
        BulkheadRejectedException rejected = await Assert.ThrowsAsync<BulkheadRejectedException>(
            () => bulkhead.ExecuteAsync(static (s, _) => ValueTask.FromResult(s), 42).AsTask());
        Assert.Equal("db", rejected.BulkheadName);
        Assert.Equal(1, bulkhead.Rejected);

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task CircuitBreaker_ThrowsOnInvalidOptionsNullDelegatesAndOpenHalfOpenIsolatedStates()
    {
        Assert.Throws<ArgumentNullException>(() => new CircuitBreaker(null!));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { Name = "" }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureThreshold = -1 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureRateThreshold = -0.1 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureRateThreshold = 1.1 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureRateThreshold = double.NaN }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureThreshold = 0, FailureRateThreshold = 0 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 0 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { SamplingWindow = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { SamplingWindow = TimeSpan.FromDays(2) }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { SamplingBuckets = 0 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { SamplingBuckets = 1001 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { OpenDuration = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { OpenDuration = TimeSpan.FromDays(2) }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { HalfOpenMaxProbes = 0 }));

        var clock = new ManualTimeProvider();
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                Name = "redis",
                FailureThreshold = 2,
                FailureRateThreshold = 0,
                OpenDuration = TimeSpan.FromSeconds(10),
                HalfOpenMaxProbes = 1,
            },
            clock);

        Assert.Throws<ArgumentNullException>(() => breaker.OnFailure(null!));
        Assert.Throws<ArgumentNullException>(() => breaker.Execute<int, int>(null!, 0));
        Assert.Throws<ArgumentNullException>(() => breaker.Execute<int>((Action<int>)null!, 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => breaker.ExecuteAsync<int, int>(null!, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => breaker.ExecuteAsync<int>(null!, 0).AsTask());

        // Trip the breaker by recording FailureThreshold (2) consecutive failures.
        Assert.True(breaker.TryEnter());
        breaker.OnFailure(new TimeoutException("fail-1"));
        Assert.True(breaker.TryEnter());
        breaker.OnFailure(new TimeoutException("fail-2"));
        Assert.Equal(CircuitState.Open, breaker.State);

        CircuitOpenException openEx = Assert.Throws<CircuitOpenException>(() => breaker.Execute(static s => s, 1));
        Assert.Equal("redis", openEx.CircuitName);
        Assert.True(openEx.RetryAfter > TimeSpan.Zero);

        // Advance past OpenDuration so the first probe transitions to HalfOpen and holds the probe slot.
        clock.Advance(TimeSpan.FromSeconds(11));
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task probeTask = breaker.ExecuteAsync(
            static async (s, _) =>
            {
                s.probeEntered.SetResult();
                await s.probeRelease.Task;
            },
            (probeEntered, probeRelease)).AsTask();

        await probeEntered.Task;
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        CircuitOpenException halfOpenEx = await Assert.ThrowsAsync<CircuitOpenException>(
            () => breaker.ExecuteAsync(static (s, _) => ValueTask.FromResult(s), 2).AsTask());
        Assert.Equal("redis", halfOpenEx.CircuitName);
        Assert.Equal(TimeSpan.Zero, halfOpenEx.RetryAfter);

        probeRelease.SetResult();
        await probeTask;
        Assert.Equal(CircuitState.Closed, breaker.State);

        breaker.Isolate();
        Assert.Equal(CircuitState.Isolated, breaker.State);
        clock.Advance(TimeSpan.FromHours(1));
        CircuitOpenException isolatedEx = Assert.Throws<CircuitOpenException>(() => breaker.Execute(static s => s, 1));
        Assert.Equal("redis", isolatedEx.CircuitName);
        Assert.Equal(Timeout.InfiniteTimeSpan, isolatedEx.RetryAfter);
    }

    [Fact]
    public async Task PolicyRetryTimeoutAndTransientFailure_ThrowOnInvalidArgumentsAndTimeouts()
    {
        Assert.Throws<ArgumentNullException>(() => Policy.Builder(null!));
        Assert.Throws<ArgumentException>(() => Policy.Builder("   "));

        PolicyBuilder builder = Policy.Builder("test-pipe");
        Assert.Throws<ArgumentNullException>(() => builder.WithBulkhead(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithRetry(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithCircuitBreaker(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithTimeout(null!));

        ResiliencePipeline pipeline = builder.Build();
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.ExecuteAsync<int, int>(null!, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.ExecuteAsync<int>(null!, 0).AsTask());

        Assert.Throws<ArgumentNullException>(() => new RetryPolicy(null!));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { MaxAttempts = 0 }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.FromHours(2), MaxDelay = TimeSpan.FromHours(2) }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(5) }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { MaxDelay = TimeSpan.FromHours(2) }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { MaxTotalDuration = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { MaxTotalDuration = TimeSpan.FromDays(2) }));

        Assert.Throws<ArgumentNullException>(() => RetryPolicy.ComputeDelay(null!, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.ComputeDelay(new RetryOptions(), 0, 0.5));

        var retry = new RetryPolicy(new RetryOptions { MaxAttempts = 2, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero });
        await Assert.ThrowsAsync<ArgumentNullException>(() => retry.ExecuteAsync<int, int>(null!, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => retry.ExecuteAsync<int>(null!, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => retry.ExecuteUntilAsync<int>(null!, 0).AsTask());

        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutPolicy(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutPolicy(TimeSpan.FromMilliseconds(-10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutPolicy(TimeSpan.FromDays(2)));

        var cooperativeTimeout = new TimeoutPolicy(TimeSpan.FromMilliseconds(20), strategy: TimeoutStrategy.Cooperative);
        await Assert.ThrowsAsync<ArgumentNullException>(() => cooperativeTimeout.ExecuteAsync<int, int>(null!, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => cooperativeTimeout.ExecuteAsync<int>(null!, 0).AsTask());

        TimeoutRejectedException coopEx = await Assert.ThrowsAsync<TimeoutRejectedException>(
            // Only the deadline can end the operation: a finite delay raced the 20 ms timer on a stalled CI runner and won (no exception thrown).
            () => cooperativeTimeout.ExecuteAsync(static async (_, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct), 0).AsTask());
        Assert.Equal(TimeSpan.FromMilliseconds(20), coopEx.Timeout);

        var pessimisticTimeout = new TimeoutPolicy(TimeSpan.FromMilliseconds(20), strategy: TimeoutStrategy.Pessimistic);
        var neverCompletes = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeoutRejectedException pessEx = await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => pessimisticTimeout.ExecuteAsync(static (tcs, _) => new ValueTask<int>(tcs.Task), neverCompletes).AsTask());
        Assert.Equal(TimeSpan.FromMilliseconds(20), pessEx.Timeout);
        neverCompletes.SetResult(0);

        Assert.Throws<ArgumentNullException>(() => TransientFailure.IsTransient(null!));
    }

    // =========================================================================
    // 4. Reflection, Net, and Ops Exception Tests
    // =========================================================================

    [Fact]
    public void CodeAssemblies_ThrowsOnNullEmptyDuplicateOrUnregisteredAssemblies()
    {
        Assembly kernelAsm = typeof(CodeAssemblies).Assembly;
        Assembly testAsm = typeof(KernelFuzzAndExceptionTests).Assembly;

        Assert.Throws<ArgumentNullException>(() => CodeAssemblies.Of(null!));
        Assert.Throws<ArgumentException>(() => CodeAssemblies.Of());
        Assert.Throws<ArgumentNullException>(() => CodeAssemblies.Of(kernelAsm, null!));
        Assert.Throws<ArgumentException>(() => CodeAssemblies.Of(kernelAsm, kernelAsm));

        CodeAssemblies set = CodeAssemblies.Of(kernelAsm);
        Assert.Throws<ArgumentNullException>(() => set.WithPack(null!));
        Assert.Throws<ArgumentException>(() => set.WithPack(kernelAsm));

        CodeAssemblies withPack = set.WithPack(testAsm);
        Assert.Throws<ArgumentNullException>(() => withPack.WithoutPack(null!));
        Assert.Throws<ArgumentException>(() => withPack.WithoutPack("   "));
        Assert.Throws<ArgumentException>(() => withPack.WithoutPack("ArcaneCore.NonExistentPack"));

        Assert.Throws<ArgumentNullException>(() => CodeAssemblies.GetLoadableTypes(null!));
        Assert.Throws<ArgumentNullException>(() => new CodeAssemblyLoadException(null!, new ReflectionTypeLoadException([], [])));
        Assert.Throws<ArgumentNullException>(() => new CodeAssemblyLoadException(kernelAsm, null!));
    }

    [Fact]
    public void NetProtectionTypes_ThrowOnNullAddressesAndOutOfRangeBudgets()
    {
        Assert.Throws<ArgumentNullException>(() => IpKey.From(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpRateTable(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpRateTable(-1, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpRateTable(16, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpRateTable(16, TimeSpan.FromSeconds(-1)));

        var table = new IpRateTable(16, TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Configure(RateBucket.Connections, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Configure(RateBucket.AuthFailures, 10, -1));

        var netOptions = new NetProtectionOptions();
        ILogger<NetGuard> logger = NullLogger<NetGuard>.Instance;
        Assert.Throws<ArgumentNullException>(() => new NetGuard(null!, () => 100, () => 10, logger));
        Assert.Throws<ArgumentNullException>(() => new NetGuard(netOptions, null!, () => 10, logger));
        Assert.Throws<ArgumentNullException>(() => new NetGuard(netOptions, () => 100, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new NetGuard(netOptions, () => 100, () => 10, null!));

        var guard = new NetGuard(netOptions, () => 100, () => 10, logger);
        Assert.Throws<ArgumentNullException>(() => guard.IsExempt(null!));
        Assert.Throws<ArgumentNullException>(() => guard.TryAdmit(null!));
        Assert.Throws<ArgumentNullException>(() => NetGuard.CreatePacketLimiter(null!));
    }

    [Fact]
    public void OpsTypes_ThrowOnNullInvalidNamesDuplicateRegistrationsAndEmptySpans()
    {
        Assert.Throws<ArgumentNullException>(() => ConsoleCommandRedactor.Redact(null!));

        var reader = new ConsoleLineReader(new StringReader(string.Empty), "test-cli");
        Assert.Throws<ArgumentNullException>(() => reader.Start(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => reader.Start(_ => { }, null!));
        reader.Start(_ => { }, NullLogger.Instance);
        Assert.Throws<InvalidOperationException>(() => reader.Start(_ => { }, NullLogger.Instance));
        reader.Stop();

        Assert.Throws<ArgumentNullException>(() => new OpcodeTrafficMeter(null!));
        using var sysMeter = new Meter("arcanecore.test.opcodes");
        var meter = new OpcodeTrafficMeter(sysMeter);
        Assert.Throws<ArgumentNullException>(() => meter.Register("world", null!));
        Assert.Throws<ArgumentException>(() => meter.Register("  ", new Dictionary<int, string>()));
        Assert.Throws<ArgumentException>(() => meter.Register("world", [new KeyValuePair<int, string>(-1, "CMSG_PING")]));
        Assert.Throws<ArgumentException>(() => meter.Register("world", [new KeyValuePair<int, string>(70000, "CMSG_PING")]));
        Assert.Throws<ArgumentException>(() => meter.Register("world", [new KeyValuePair<int, string>(1, "  ")]));
        Assert.Throws<ArgumentException>(() => meter.Register("world", [new KeyValuePair<int, string>(1, "unknown")]));
        Assert.Throws<ArgumentException>(() => meter.Register("world", [new KeyValuePair<int, string>(1, "A"), new KeyValuePair<int, string>(1, "B")]));

        meter.Register("world", [new KeyValuePair<int, string>(1, "CMSG_PING")]);
        Assert.Throws<ArgumentException>(() => meter.Register("world", [new KeyValuePair<int, string>(2, "CMSG_AUTH")]));

        var registry = new CounterRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.GetOrAdd(null!));
        Assert.Throws<ArgumentException>(() => registry.GetOrAdd("   "));
        Assert.Throws<ArgumentException>(() => registry.GetOrAdd(new string('a', 65)));
        Assert.Throws<ArgumentException>(() => registry.GetOrAdd("Invalid.Upper"));
        Assert.Throws<ArgumentException>(() => registry.GetOrAdd("invalid-dash"));

        registry.GetOrAdd("valid.metric_1", CounterKind.Counter);
        Assert.Throws<InvalidOperationException>(() => registry.GetOrAdd("valid.metric_1", CounterKind.Gauge));

        Assert.Throws<ArgumentNullException>(() => new FileHeartbeatSink(null!, 1234));
        Assert.Throws<ArgumentException>(() => new FileHeartbeatSink("   ", 1234));
        Assert.Throws<ArgumentNullException>(() => new SystemdNotifySink(null!));
        Assert.Throws<ArgumentException>(() => new SystemdNotifySink("   "));

        var hist = new LatencyHistogram();
        var tickMonitor = new TickMonitor(new WatchdogOptions(), WatchdogClock.System, new CounterRegistry(), NullLogger<TickMonitor>.Instance);
        Assert.Throws<ArgumentNullException>(() => tickMonitor.AttachLatency(null!, hist, []));
        Assert.Throws<ArgumentNullException>(() => tickMonitor.AttachLatency(hist, null!, []));
        Assert.Throws<ArgumentNullException>(() => tickMonitor.AttachLatency(hist, hist, null!));

        Assert.Throws<ArgumentException>(() => RingStats.NearestRank(ReadOnlySpan<long>.Empty, 50));
    }

    // =========================================================================
    // 5. Domain Value Types & Catalogs Exception Tests
    // =========================================================================

    [Fact]
    public void AccountAndCharacterDomainTypes_ThrowOnInvalidArgumentsAndExhaustedRanges()
    {
        var claims = new InMemoryAccountSessionClaimStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => claims.Claim(0, Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => claims.Invalidate(0));
        Assert.Throws<ArgumentNullException>(() => claims.TryRelease(null!));
        Assert.Throws<ArgumentNullException>(() => claims.SubscribeRevocations(null!));

        Assert.Throws<ArgumentNullException>(() => AccountBanEvaluator.IsActive((AccountBanRecord)null!, 0));
        Assert.Throws<ArgumentNullException>(() => AccountBanEvaluator.IsActive((IpBanRecord)null!, 0));
        Assert.Throws<ArgumentNullException>(() => BanTime.TryTimeStringToSecs(null!, out _));
        Assert.Throws<ArgumentNullException>(() => BanTime.TimeStringToSecs(null!));

        Assert.Throws<ArgumentNullException>(() => new CharacterAppearanceCatalog(null!, []));
        Assert.Throws<ArgumentNullException>(() => new CharacterAppearanceCatalog([], null!));

        Assert.Throws<ArgumentNullException>(() => IdRangeScopes.GetMinFirstId(null!));
        Assert.Throws<ArgumentException>(() => IdRangeScopes.GetMinFirstId("   "));
        Assert.Throws<ArgumentNullException>(() => IdRangeScopes.GetMaxId(null!));
        Assert.Throws<ArgumentException>(() => IdRangeScopes.GetMaxId("   "));

        Assert.Throws<ArgumentException>(() => new IdRangeBlock("  ", 1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdRangeBlock(IdRangeScopes.Item, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdRangeBlock(IdRangeScopes.Item, 10, 5));

        Assert.Throws<ArgumentException>(() => new DurableIdBlockAllocator("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableIdBlockAllocator(IdRangeScopes.Item, blockSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableIdBlockAllocator(IdRangeScopes.Item, blockSize: 10, lowWatermark: 10));
        Assert.Throws<ArgumentNullException>(() => new DurableIdBlockAllocator(IdRangeScopes.Item, store: null!));

        var allocator = new DurableIdBlockAllocator(IdRangeScopes.Item, blockSize: 4, lowWatermark: 1, maxId: 10);
        Assert.Throws<InvalidOperationException>(() => allocator.ReserveAndEnqueueAsync().GetAwaiter().GetResult());
        Assert.Throws<IdRangeExhaustedException>(() => allocator.Next());
        Assert.Throws<ArgumentException>(() => allocator.EnqueueBlock(new IdRangeBlock(IdRangeScopes.Mail, 1, 2)));
        Assert.Throws<IdRangeExhaustedException>(() => allocator.EnqueueBlock(new IdRangeBlock(IdRangeScopes.Item, 9, 12)));

        var highAllocator = new DurableIdBlockAllocator(IdRangeScopes.Item, blockSize: 2, lowWatermark: 0);
        highAllocator.EnqueueBlock(new IdRangeBlock(IdRangeScopes.Item, (uint)int.MaxValue + 1u, (uint)int.MaxValue + 2u));
        Assert.Throws<IdRangeExhaustedException>(() => highAllocator.NextInt32());

        Assert.Throws<ArgumentNullException>(() => ExploredZonesText.Format(null!));
        Assert.Throws<ArgumentException>(() => ExploredZonesText.Format([1u, 2u]));
        Assert.Throws<ArgumentNullException>(() => ExploredZonesText.Parse(null!));
        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse("1 2 3"));

        string badWords = string.Join(' ', Enumerable.Repeat("1", 63).Append("not-a-uint"));
        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse(badWords));
    }

    [Fact]
    public void ClusterTypes_ThrowOnInvalidKeysLeasesPresenceAndCompatibilityPolicies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerMailIdempotencyKey.Derive(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerMailIdempotencyKey.Derive(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerMailIdempotencyKey.Derive(1, 1, 0));

        var leaseStore = new InMemoryRealmAuthorityLeaseStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => leaseStore.TryAcquire("", "node-1", Guid.NewGuid(), TimeSpan.FromSeconds(10), now));
        Assert.Throws<ArgumentException>(() => leaseStore.TryAcquire("economy", "  ", Guid.NewGuid(), TimeSpan.FromSeconds(10), now));
        Assert.Throws<ArgumentOutOfRangeException>(() => leaseStore.TryAcquire("economy", "node-1", Guid.NewGuid(), TimeSpan.Zero, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => leaseStore.TryRenew("economy", "node-1", Guid.NewGuid(), 1, TimeSpan.FromSeconds(-1), now));
        Assert.Throws<ArgumentException>(() => leaseStore.TryRelease("", "node-1", Guid.NewGuid(), 1));

        var presence = new InMemoryRealmPresenceDirectory();
        Assert.Throws<ArgumentNullException>(() => presence.TryUpsert(null!, out _));
        Assert.Throws<ArgumentNullException>(() => presence.QueryWho(null!));

        var baseEntry = new RealmPresenceEntry(
            CharacterGuid: 1,
            CharacterName: "Thrall",
            AccountId: 1,
            WorkerId: "w1",
            GatewayId: "g1",
            MapId: 1,
            ZoneId: 14,
            AreaId: 14,
            Level: 60,
            Class: 7,
            Race: 2,
            Gender: 0,
            GuildId: 0,
            GroupId: 0,
            PresenceVersion: 1,
            SessionGeneration: 1,
            UpdatedAtUtc: now);

        Assert.Throws<ArgumentOutOfRangeException>(() => presence.TryUpsert(baseEntry with { CharacterGuid = 0 }, out _));
        Assert.Throws<ArgumentException>(() => presence.TryUpsert(baseEntry with { CharacterName = "  " }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => presence.TryUpsert(baseEntry with { PresenceVersion = 0 }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => presence.TryUpsert(baseEntry with { SessionGeneration = 0 }, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentRevisionCompatibilityPolicy(0, 1, 1, 1, "rev"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentRevisionCompatibilityPolicy(1, 0, 1, 1, "rev"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentRevisionCompatibilityPolicy(1, 1, 0, 1, "rev"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentRevisionCompatibilityPolicy(1, 1, 1, 0, "rev"));
        Assert.Throws<ArgumentException>(() => new ContentRevisionCompatibilityPolicy(1, 1, 1, 1, "  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentRevisionCompatibilityPolicy(1, 1, 2, 1, "rev", minimumCompatibleCharactersSchemaVersion: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentRevisionCompatibilityPolicy(1, 1, 2, 1, "rev", minimumCompatibleCharactersSchemaVersion: 3));

        var policy = new ContentRevisionCompatibilityPolicy(1, 1, 1, 1, "rev");
        Assert.Throws<ArgumentNullException>(() => policy.EvaluateNodeAdmission(null!));
        Assert.Throws<ArgumentNullException>(() => policy.EvaluateFleetCompatibility(null!));

        var guard = new ClusterMaintenanceJobGuard();
        Assert.Throws<ArgumentNullException>(() => guard.Evaluate(null!, []));
        Assert.Throws<ArgumentNullException>(() => guard.Evaluate(new ClusterMaintenanceJobRequest(ClusterMaintenanceJobKind.AccountAdmin, 1, "admin"), null!));
    }

    [Fact]
    public void ContentCatalogs_ThrowOnNullDuplicatesAndMalformedRecords()
    {
        Assert.Throws<ArgumentNullException>(() => new EnchantCatalog(null!));
        var ench = new SpellItemEnchantment(1, [0, 0, 0], [0, 0, 0], [0, 0, 0], "Test", 0, 0);
        Assert.Throws<InvalidDataException>(() => new EnchantCatalog([ench, ench]));

        Assert.Throws<ArgumentNullException>(() => new ItemRandomPropertyCatalog(null!, []));
        Assert.Throws<ArgumentNullException>(() => new ItemRandomPropertyCatalog([], null!));

        uint[] eight = [0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<ArgumentNullException>(() => new ItemSetRecord(1, null!, eight, eight, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ItemSetRecord(1, "Set", null!, eight, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ItemSetRecord(1, "Set", eight, null!, 0, 0));
        Assert.Throws<ArgumentException>(() => new ItemSetRecord(1, "Set", [1, 2], eight, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ItemSetCatalog(null!));

        Assert.Throws<ArgumentNullException>(() => new FactionTemplateCatalog(null!));
        Assert.Throws<ArgumentException>(() => new FactionTemplateCatalog([new FactionTemplateRecord(0, 1, 0, 1, 1, 1)]));

        Assert.Throws<ArgumentNullException>(() => new TaxiPathNodeCatalog(null!));
        Assert.Throws<ArgumentNullException>(() => new SkillLineAbilityCatalog(null!));
        Assert.Throws<ArgumentNullException>(() => new BankBagSlotPriceTable(null!));
        Assert.Throws<ArgumentNullException>(() => new RepairCostTable(null!, []));
        Assert.Throws<ArgumentNullException>(() => new RepairCostTable([], null!));
        Assert.Throws<ArgumentException>(() => new RepairCostTable([(1u, new uint[10])], []));

        uint[] fourU = [0, 0, 0, 0];
        int[] fourI = [0, 0, 0, 0];
        Assert.Throws<ArgumentNullException>(() => new FactionRecord(1, 0, null!, fourU, fourI, fourU));
        Assert.Throws<ArgumentNullException>(() => new FactionRecord(1, 0, fourU, null!, fourI, fourU));
        Assert.Throws<ArgumentNullException>(() => new FactionRecord(1, 0, fourU, fourU, null!, fourU));
        Assert.Throws<ArgumentNullException>(() => new FactionRecord(1, 0, fourU, fourU, fourI, null!));
        Assert.Throws<ArgumentException>(() => new FactionRecord(1, 0, [1], fourU, fourI, fourU));

        Assert.Throws<ArgumentNullException>(() => new FactionCatalog(null!));
        Assert.Throws<ArgumentNullException>(() => new FactionCatalog([null!]));
        Assert.Throws<ArgumentException>(() => new FactionCatalog([new FactionRecord(0, -1, fourU, fourU, fourI, fourU)]));
        var f1 = new FactionRecord(1, 0, fourU, fourU, fourI, fourU);
        Assert.Throws<ArgumentException>(() => new FactionCatalog([f1, f1]));
        Assert.Throws<ArgumentException>(() => new FactionCatalog([new FactionRecord(1, FactionCatalog.ReputationListSize, fourU, fourU, fourI, fourU)]));
        Assert.Throws<ArgumentException>(() => new FactionCatalog([f1, new FactionRecord(2, 0, fourU, fourU, fourI, fourU)]));

        Assert.Throws<ArgumentNullException>(() => ReputationContent.Create(null!, FactionCatalog.Empty));
        Assert.Throws<ArgumentNullException>(() => ReputationContent.Create(ReputationContentRows.Empty, null!));

        Assert.Throws<ArgumentNullException>(() => SpellLearnSkillTable.Build(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellRankChains(null!));

        uint[] sixteen = new uint[SkillTierRecord.StepCount];
        Assert.Throws<ArgumentNullException>(() => new SkillTierRecord(1, null!, sixteen));
        Assert.Throws<ArgumentNullException>(() => new SkillTierRecord(1, sixteen, null!));
        Assert.Throws<ArgumentException>(() => new SkillTierRecord(1, [1], sixteen));

        Assert.Throws<ArgumentNullException>(() => new SkillCatalog(null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => new SkillCatalog([], null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new SkillCatalog([], [], null!, []));
        Assert.Throws<ArgumentNullException>(() => new SkillCatalog([], [], [], null!));
        var line = new SkillLineRecord(1, 1, "Swords", 0);
        Assert.Throws<ArgumentException>(() => new SkillCatalog([line, line], [], [], []));
        Assert.Throws<ArgumentNullException>(() => SkillCatalog.Empty.RangeType(1, null!));
    }

    [Fact]
    public void TalentAndWorldDataCatalogs_ThrowOnInvalidGraphCyclesAndBounds()
    {
        Assert.Throws<ArgumentNullException>(() => new TalentCatalog(null!, []));
        Assert.Throws<ArgumentNullException>(() => new TalentCatalog([], null!));

        var tab = new TalentTabRecord(1, 1, 0);
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab, tab], []));

        uint[] fiveRanks = [100, 101, 0, 0, 0];
        var validTalent = new TalentRecord(1, 1, 0, 0, fiveRanks, 0, 0, 0);
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent, validTalent]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent with { RankSpells = [100, 101] }]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent with { TabId = 99 }]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent with { Row = TalentCatalog.MaxRow + 1 }]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent with { RankSpells = [100, 0, 102, 0, 0] }]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent, validTalent with { Id = 2 }]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([tab], [validTalent with { DependsOn = 999 }]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(
            [tab],
            [
                validTalent with { Id = 1, RankSpells = [100, 0, 0, 0, 0], DependsOn = 2 },
                validTalent with { Id = 2, RankSpells = [200, 0, 0, 0, 0], DependsOn = 1 },
            ]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(
            [tab],
            [
                validTalent with { Id = 1, RankSpells = [100, 0, 0, 0, 0] },
                validTalent with { Id = 2, RankSpells = [200, 0, 0, 0, 0], DependsOn = 1, DependsOnRank = 1 },
            ]));

        Assert.Throws<ArgumentNullException>(() => new CreatureAiContent(null!, []));
        Assert.Throws<ArgumentNullException>(() => new CreatureAiContent([], null!));
        Assert.Throws<ArgumentException>(() => new CreatureAiContent(
            [],
            [],
            textTemplates: [new CreatureAiTextChoice(1, -10, 50), new CreatureAiTextChoice(1, -10, 50)]));

        var ai = new CreatureAiContent([], [], textTemplates: [new CreatureAiTextChoice(1, -10, 0)]);
        Assert.Throws<ArgumentNullException>(() => ai.SelectTemplateText(1, 50f, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ai.SelectTemplateText(1, -1f, _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ai.SelectTemplateText(1, 101f, _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ai.SelectTemplateText(1, float.NaN, _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ai.SelectTemplateText(1, 50f, _ => 5));

        Assert.Throws<ArgumentNullException>(() => CreatureContent.Empty.SwapDefinitions(null!));
        Assert.Throws<InvalidOperationException>(() => CreatureContent.Empty.SwapDefinitions(CreatureContent.Empty));
        Assert.Throws<ArgumentNullException>(() => CreatureContent.Empty.RestoreDefinitions(null!));

        Assert.Throws<ArgumentNullException>(() => new DbScriptCatalog(null!));
        Assert.Throws<ArgumentNullException>(() => new RelayScriptCatalog(null!, []));
        Assert.Throws<ArgumentNullException>(() => new RelayScriptCatalog([], null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelayScriptCatalog.EventRelayId(uint.MaxValue));
        Assert.Throws<ArgumentNullException>(() => RelayScriptCatalog.Empty.SelectFromTemplate(1, 50f, null!));
        Assert.Throws<ArgumentNullException>(() => new SmartScriptCatalog(null!));

        Assert.Throws<ArgumentNullException>(() => new ItemEnchantmentDefinition(1, null!));
        Assert.Throws<ArgumentNullException>(() => new ItemEnchantmentDefinition(1, null!, [], 0, 0, 0));

        Assert.Throws<ArgumentNullException>(() => new PetContent(null!, []));
        Assert.Throws<ArgumentNullException>(() => new PetContent([], null!));
        Assert.Throws<InvalidDataException>(() => new PetContent(
            [new PetLevelStats(1, 2, 100, 0, 10, 1, 2, 5, 5, 5, 5, 5)],
            []));

        Assert.Throws<ArgumentNullException>(() => new PlayerStatsContent(null!, [], [], [], []));
        Assert.Throws<ArgumentNullException>(() => new PlayerStatsContent([], null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => new PlayerStatsContent([], [], null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new PlayerStatsContent([], [], [], null!, []));
        Assert.Throws<ArgumentNullException>(() => new PlayerStatsContent([], [], [], [], null!));
        Assert.Throws<ArgumentNullException>(() => PlayerStatsContent.Empty.Validate(null!, 60));

        var emptySpawns = new Dictionary<uint, (uint Entry, uint MapId)>();
        Assert.Throws<ArgumentNullException>(() => PoolCatalog.Build(null!, [], [], [], emptySpawns));
        Assert.Throws<ArgumentNullException>(() => PoolCatalog.Build([], null!, [], [], emptySpawns));
        Assert.Throws<ArgumentNullException>(() => PoolCatalog.Build([], [], null!, [], emptySpawns));
        Assert.Throws<ArgumentNullException>(() => PoolCatalog.Build([], [], [], null!, emptySpawns));
        Assert.Throws<ArgumentNullException>(() => PoolCatalog.Build([], [], [], [], null!));

        Assert.Throws<ArgumentNullException>(() => new SpellProcEventContent(null!));
        var procRow = new SpellProcEventRecord(100, 1, 0, 0, 0, 0, 1, 0, 1f, 10f, 0);
        Assert.Throws<InvalidDataException>(() => new SpellProcEventContent([procRow, procRow]));
        Assert.Throws<ArgumentNullException>(() => SpellProcEventContent.Empty.Resolve(null!));

        Assert.Throws<ArgumentNullException>(() => new ShapeshiftFormCatalog(null!));
        var form = new ShapeshiftFormInfo(1, 0, 1);
        Assert.Throws<ArgumentException>(() => new ShapeshiftFormCatalog([form, form]));

        Assert.Throws<ArgumentNullException>(() => new SpellThreatContent(null!));
        var threatRow = new SpellThreatRecord(100, 10, 1f, 0);
        Assert.Throws<InvalidDataException>(() => new SpellThreatContent([threatRow, threatRow]));
        Assert.Throws<ArgumentNullException>(() => SpellThreatContent.Empty.Resolve(null!));

        Assert.Throws<ArgumentNullException>(() => new TotemContent(null!));
        Assert.Throws<ArgumentNullException>(() => new TransportAnimationCatalog(null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => WarEffortCatalog.BossDeathEvent(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WarEffortCatalog.BossDeathEvent(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => WarEffortPileCatalog.GroupOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WarEffortPileCatalog.GroupOf(WarEffortCatalog.ResourceCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => WarEffortPileCatalog.TotalField(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WarEffortPileCatalog.TotalField(WarEffortCatalog.ResourceCount));
    }

    // =========================================================================
    // 6. Deterministic Seeded Fuzz Tests
    // =========================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_ConfDocument_ValidAndQuotedLines_RoundTripsIdentically(int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder();
        var expected = new List<(string Path, string Value, bool Quoted)>();

        int lineCount = rng.Next(40, 90);
        for (int i = 0; i < lineCount; i++)
        {
            int kind = rng.Next(5);
            if (kind == 0)
            {
                sb.Append(rng.Next(2) == 0 ? "# comment " : "; comment ")
                  .Append( RandomToken(rng, 10))
                  .Append(rng.Next(2) == 0 ? "\n" : "\r\n");
                continue;
            }

            if (kind == 1)
            {
                sb.Append("   \t  ").Append(rng.Next(2) == 0 ? "\n" : "\r\n");
                continue;
            }

            string seg1 = "Sec" + i;
            string seg2 = "Sub" + rng.Next(100);
            string seg3 = "Key" + rng.Next(1000);
            bool useColon = rng.Next(2) == 0;
            string rawKey = useColon ? $"{seg1}:{seg2}:{seg3}" : $"{seg1}.{seg2}.{seg3}";
            string expectedPath = $"{seg1}:{seg2}:{seg3}";

            bool quoted = rng.Next(2) == 0;
            string rawValue;
            string expectedValue;
            if (quoted)
            {
                expectedValue = RandomQuotedPayload(rng, 24);
                rawValue = ConfValue.Quote(expectedValue);
            }
            else
            {
                expectedValue = RandomUnquotedPayload(rng, 20);
                rawValue = expectedValue;
            }

            sb.Append("  ").Append(rawKey).Append(" = ").Append(rawValue).Append("  ")
              .Append(rng.Next(2) == 0 ? "\n" : "\r\n");
            expected.Add((expectedPath, expectedValue, quoted));
        }

        string text = sb.ToString();
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        ConfDocument fromText = ConfDocument.ParseText(text, "fuzz.conf");
        ConfDocument fromBytes = ConfDocument.Parse(utf8, "fuzz.conf");

        Assert.Equal(expected.Count, fromText.Entries.Count);
        Assert.Equal(expected.Count, fromBytes.Entries.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Path, fromText.Entries[i].Path);
            Assert.Equal(expected[i].Value, fromText.Entries[i].Value);
            Assert.Equal(expected[i].Quoted, fromText.Entries[i].Quoted);
            Assert.Equal(fromText.Entries[i], fromBytes.Entries[i]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_ConfDocument_ArbitraryCorruptedBytesAndLines_ThrowsOnlyConfParseException(int seed)
    {
        var rng = new Random(seed);
        char[] alphabet = ['a', 'b', '0', '9', '.', ':', '=', '"', '\\', '#', ';', '[', ']', ' ', '\t', '\r', '\n', '\0'];

        for (int iter = 0; iter < 250; iter++)
        {
            int len = rng.Next(0, 80);
            var chars = new char[len];
            for (int i = 0; i < len; i++)
            {
                chars[i] = alphabet[rng.Next(alphabet.Length)];
            }

            string candidate = new(chars);
            try
            {
                ConfDocument doc = ConfDocument.ParseText(candidate, "fuzz-corrupt.conf");
                Assert.NotNull(doc.Entries);
            }
            catch (ConfParseException ex)
            {
                Assert.NotEmpty(ex.Errors);
                Assert.StartsWith("fuzz-corrupt.conf:", ex.Errors[0], StringComparison.Ordinal);
            }

            byte[] rawBytes = new byte[rng.Next(0, 64)];
            rng.NextBytes(rawBytes);
            try
            {
                ConfDocument doc = ConfDocument.Parse(rawBytes, "fuzz-bytes.conf");
                Assert.NotNull(doc.Entries);
            }
            catch (ConfParseException ex)
            {
                Assert.NotEmpty(ex.Errors);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_ConfValueValidate_AllScalarTypesAndEnums_MaintainsInvariants(int seed)
    {
        var rng = new Random(seed);
        string[] types =
        [
            "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
            "float", "double", "decimal", "TimeSpan", "DateTime", "DateTimeOffset", "Guid", "char", "string",
        ];
        string[] sampleEnums = ["Alpha", "Beta", "Gamma"];

        for (int i = 0; i < 200; i++)
        {
            int n = rng.Next(-100_000, 100_000);
            Assert.Null(ConfValue.Validate(n.ToString(System.Globalization.CultureInfo.InvariantCulture), "int", null));
            if (n >= 0)
            {
                Assert.Null(ConfValue.Validate(n.ToString(System.Globalization.CultureInfo.InvariantCulture), "uint", null));
                Assert.Null(ConfValue.Validate(n.ToString(System.Globalization.CultureInfo.InvariantCulture), "ulong", null));
                Assert.Null(ConfValue.Validate($"0x{n:X}", "uint", null));
            }

            Assert.NotNull(ConfValue.Validate("not-a-float", "float", null));
            Assert.NotNull(ConfValue.Validate("not-a-double", "double", null));
            Assert.Null(ConfValue.Validate("alpha", "MyEnum", sampleEnums));
            Assert.NotNull(ConfValue.Validate("delta", "MyEnum", sampleEnums));

            string randomToken = RandomToken(rng, rng.Next(0, 24));
            foreach (string type in types)
            {
                string? problem = ConfValue.Validate(randomToken, type, null);
                if (type == "string")
                {
                    Assert.Null(problem);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_CircuitBreaker_RandomCommandAndOperationStreams_PreservesStateMachineInvariants(int seed)
    {
        var rng = new Random(seed);
        var clock = new ManualTimeProvider();
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                Name = $"fuzz-cb-{seed}",
                FailureThreshold = 3,
                FailureRateThreshold = 0.5,
                MinimumThroughput = 4,
                SamplingWindow = TimeSpan.FromSeconds(10),
                SamplingBuckets = 5,
                OpenDuration = TimeSpan.FromSeconds(5),
                HalfOpenMaxProbes = 2,
            },
            clock);

        for (int step = 0; step < 250; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(rng.Next(0, 2500)));
            int action = rng.Next(10);

            if (action == 0)
            {
                if (breaker.TryEnter())
                {
                    breaker.OnFailure(new TimeoutException("fuzz-failure"));
                }
            }
            else if (action == 1)
            {
                breaker.Isolate();
                Assert.Equal(CircuitState.Isolated, breaker.State);
            }
            else if (action == 2)
            {
                breaker.Reset();
                Assert.Equal(CircuitState.Closed, breaker.State);
            }
            else
            {
                bool shouldFail = rng.Next(2) == 0;
                bool useAsync = rng.Next(2) == 0;
                try
                {
                    if (useAsync)
                    {
                        int res = await breaker.ExecuteAsync(
                            static (s, _) => s.shouldFail
                                ? ValueTask.FromException<int>(new TimeoutException("transient"))
                                : ValueTask.FromResult(s.step),
                            (shouldFail, step));
                        Assert.Equal(step, res);
                    }
                    else
                    {
                        int res = breaker.Execute(
                            static s => s.shouldFail ? throw new TimeoutException("transient") : s.step,
                            (shouldFail, step));
                        Assert.Equal(step, res);
                    }
                }
                catch (CircuitOpenException openEx)
                {
                    Assert.Equal($"fuzz-cb-{seed}", openEx.CircuitName);
                }
                catch (TimeoutException)
                {
                    Assert.True(shouldFail);
                }
            }

            Assert.True(Enum.IsDefined(breaker.State));
            Assert.True(breaker.ConsecutiveFailures >= 0);
            Assert.True(breaker.Rejected >= 0);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_RetryBulkheadAndPipeline_RandomParametersAndTransientFailures_PreservesInvariants(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 150; i++)
        {
            TimeSpan baseDelay = TimeSpan.FromMilliseconds(rng.Next(0, 500));
            TimeSpan maxDelay = baseDelay + TimeSpan.FromMilliseconds(rng.Next(0, 2000));
            var opts = new RetryOptions
            {
                MaxAttempts = rng.Next(1, 8),
                BaseDelay = baseDelay,
                MaxDelay = maxDelay,
                Jitter = rng.Next(2) == 0 ? RetryJitter.None : RetryJitter.Full,
            };

            int attempt = rng.Next(1, 20);
            double sample = rng.NextDouble() * 2.0 - 0.5;
            TimeSpan delay = RetryPolicy.ComputeDelay(opts, attempt, sample);
            Assert.True(delay >= TimeSpan.Zero);
            Assert.True(delay <= maxDelay);
        }

        var clock = new ManualTimeProvider();
        using var bulkhead = new Bulkhead(new BulkheadOptions { Name = "fuzz-bh", MaxConcurrency = 4, MaxQueue = 2 });
        var retry = new RetryPolicy(
            new RetryOptions
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
            },
            clock,
            sample: rng.NextDouble);
        var breaker = new CircuitBreaker(
            new CircuitBreakerOptions
            {
                Name = "fuzz-pipe-cb",
                FailureThreshold = 5,
                FailureRateThreshold = 0,
                OpenDuration = TimeSpan.FromSeconds(2),
            },
            clock);
        var timeout = new TimeoutPolicy(TimeSpan.FromSeconds(5));

        ResiliencePipeline pipeline = Policy.Builder("fuzz-pipeline")
            .WithBulkhead(bulkhead)
            .WithRetry(retry)
            .WithCircuitBreaker(breaker)
            .WithTimeout(timeout)
            .Build();

        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(rng.Next(10, 1000)));
            int failuresBeforeSuccess = rng.Next(0, 5);
            int calls = 0;
            try
            {
                int value = await pipeline.ExecuteAsync(
                    (_, _) =>
                    {
                        calls++;
                        if (calls <= failuresBeforeSuccess)
                        {
                            throw new TimeoutException("simulated transient failure");
                        }

                        return ValueTask.FromResult(i);
                    },
                    0);
                Assert.Equal(i, value);
            }
            catch (Exception ex) when (ex is TimeoutException or CircuitOpenException or BulkheadRejectedException or TimeoutRejectedException)
            {
                // Expected resilience pipeline rejection/failure outcome.
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_RateLimiters_BurstyAndNonMonotonicTimestamps_NeverCorruptsOrThrows(int seed)
    {
        var rng = new Random(seed);
        long nowMs = 10_000;
        var ipTable = new IpRateTable(capacity: 32, idleEviction: TimeSpan.FromSeconds(30), clock: () => nowMs);
        ipTable.Configure(RateBucket.Connections, burst: 10, perMinute: 120);
        ipTable.Configure(RateBucket.AuthFailures, burst: 5, perMinute: 60);

        var opcodeLimiter = new OpcodeRateLimiter(
            burst: 8,
            refillPerSecond: 4.0,
            packetsPerSecond: 25,
            floodPacketsPerSecond: 50);

        var tokenBucket = new TokenBucket(burst: 10, refillPerMinute: 60);
        var logGate = new LogGate(TimeSpan.FromMilliseconds(100), () => nowMs);

        IPAddress[] addresses = new IPAddress[48];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = i % 3 == 0
                ? IPAddress.Parse($"10.0.{i / 10}.{i + 1}").MapToIPv6()
                : IPAddress.Parse($"192.168.1.{i + 1}");
        }

        for (int step = 0; step < 350; step++)
        {
            // Include backward jumps, zero-delta bursts, and large forward jumps.
            int jumpKind = rng.Next(6);
            nowMs = jumpKind switch
            {
                0 => nowMs,
                1 => nowMs - rng.Next(1, 5_000),
                2 => nowMs + rng.Next(1, 50),
                3 => nowMs + rng.Next(500, 120_000),
                _ => nowMs + rng.Next(0, 500),
            };

            IPAddress ip = addresses[rng.Next(addresses.Length)];
            IpKey ipKey = IpKey.From(ip);
            RateBucket bucket = rng.Next(2) == 0 ? RateBucket.Connections : RateBucket.AuthFailures;

            RateVerdict rateVerdict = rng.Next(3) == 0
                ? ipTable.Peek(ipKey, bucket)
                : ipTable.TryTake(ipKey, bucket);
            Assert.True(Enum.IsDefined(rateVerdict));
            double tokens = ipTable.TokensOf(ipKey, bucket);
            Assert.True(double.IsFinite(tokens));
            Assert.InRange(ipTable.Count, 0, 32);

            ushort opcode = (ushort)rng.Next(0, 64);
            OpcodeRateVerdict verdict = opcodeLimiter.OnPacket(opcode, nowMs);
            Assert.True(Enum.IsDefined(verdict));

            if (tokenBucket.TryTake(nowMs, out long bucketSuppressed))
            {
                Assert.True(bucketSuppressed >= 0);
            }
            Assert.True(tokenBucket.Suppressed >= 0);

            if (logGate.TryEnter(out int suppressed))
            {
                Assert.True(suppressed >= 0);
            }
            Assert.True(logGate.Suppressed >= 0);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_OptionValidators_BoundaryAndRandomMutations_NeverThrowsUnexpectedException(int seed)
    {
        var rng = new Random(seed);
        var resilienceChecks = new ResilienceConfigChecks();
        var announceChecks = new AnnounceConfigChecks();
        var loggingChecks = new LoggingConfigChecks();
        var metricsValidation = new MetricsOptionsValidation();
        var watchdogValidation = new WatchdogOptionsValidation();

        for (int i = 0; i < 120; i++)
        {
            var resOptions = new ResilienceOptions
            {
                Database =
                {
                    QueryTimeoutMs = rng.Next(-100, 700_000),
                    Breaker =
                    {
                        FailureThreshold = rng.Next(-5, 20),
                        FailureRateThreshold = rng.Next(5) == 0 ? double.NaN : rng.NextDouble() * 2.0 - 0.5,
                        MinimumThroughput = rng.Next(-2, 20),
                        SamplingWindowMs = rng.Next(-100, 90_000_000),
                        OpenDurationMs = rng.Next(-100, 90_000_000),
                        HalfOpenMaxProbes = rng.Next(-2, 10),
                    },
                    Bulkhead =
                    {
                        MaxConcurrency = rng.Next(-5, 64),
                        MaxQueue = rng.Next(-5, 64),
                    },
                    Bootstrap =
                    {
                        MaxAttempts = rng.Next(-2, 10),
                        BaseDelayMs = rng.Next(-10, 5_000_000),
                        MaxDelayMs = rng.Next(-10, 5_000_000),
                        MaxTotalDurationMs = rng.Next(-10, 90_000_000),
                    },
                },
            };
            IReadOnlyList<string> resIssues = resOptions.Validate();
            Assert.NotNull(resIssues);

            var dict = new Dictionary<string, string?>
            {
                ["Resilience:Database:QueryTimeoutMs"] = rng.Next(2) == 0 ? "abc" : rng.Next(-10, 5000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["World:Announce:FaultIntervalSeconds"] = rng.Next(2) == 0 ? "bad" : rng.Next(-5, 86_500).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Logging:ArcaneCore:Console:Mode"] = rng.Next(2) == 0 ? "Color" : "InvalidMode",
                ["Logging:ArcaneCore:Console:QueueCapacity"] = rng.Next(-10, 2_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            IConfigurationRoot cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
            Assert.NotNull(resilienceChecks.Check(cfg).ToList());
            Assert.NotNull(announceChecks.Check(cfg).ToList());
            Assert.NotNull(loggingChecks.Check(cfg).ToList());

            var metricsOpts = new MetricsOptions
            {
                Enabled = rng.Next(2) == 0,
                Exporter = (MetricsExporter)rng.Next(-1, 5),
                PrometheusPrefix = rng.Next(3) switch { 0 => MetricsOptions.WorldDefaultPrometheusPrefix, 1 => "", _ => "not-a-prefix" },
                RealmPrometheusPrefix = rng.Next(3) switch { 0 => MetricsOptions.RealmDefaultPrometheusPrefix, 1 => "", _ => "bad-prefix" },
                OtlpEndpoint = rng.Next(2) == 0 ? "http://localhost:4318/v1/metrics" : "not-a-uri",
                OtlpIntervalSeconds = rng.Next(-5, 7200),
                MapSampleIntervalSeconds = rng.Next(-5, 7200),
            };
            ValidateOptionsResult mResult = metricsValidation.Validate(Options.DefaultName, metricsOpts);
            Assert.NotNull(mResult);

            var watchdogOpts = new WatchdogOptions
            {
                CheckIntervalMs = rng.Next(-10, 70_000),
                TickMonitor =
                {
                    RingCapacity = rng.Next(-5, 2_000_000),
                    BudgetMs = rng.Next(-5, 500),
                    HangMs = rng.Next(-5, 5000),
                    ActionHangMs = rng.Next(-5, 10_000),
                    Action = (TickHangAction)rng.Next(-1, 6),
                },
                Heartbeat =
                {
                    Mode = (HeartbeatMode)rng.Next(-1, 6),
                    IntervalSeconds = rng.Next(-5, 120),
                    FilePath = rng.Next(2) == 0 ? string.Empty : "logs/alive.txt",
                },
            };
            ValidateOptionsResult wResult = watchdogValidation.Validate(Options.DefaultName, watchdogOpts);
            Assert.NotNull(wResult);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_BanTimeAndDiagnosticsLedgers_RandomInputs_MaintainsInvariants(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 150; i++)
        {
            uint secs = (uint)rng.Next(0, 10_000_000);
            string formatted = BanTime.SecsToTimeString(secs);
            Assert.True(BanTime.IsWellFormed(formatted));
            Assert.True(BanTime.TryTimeStringToSecs(formatted, out uint parsed));
            Assert.Equal(secs, parsed);
            Assert.Equal(secs, BanTime.TimeStringToSecs(formatted));
        }

        // Overflow and bad-format cases for BanTime.
        Assert.False(BanTime.TryTimeStringToSecs("99999999999999999999s", out _));
        Assert.Equal(uint.MaxValue, BanTime.TimeStringToSecs("99999999999999999999s"));
        Assert.False(BanTime.TryTimeStringToSecs("60000d", out _));
        Assert.True(BanTime.TryTimeStringToSecs("10x", out uint badUnitSecs));
        Assert.Equal(0u, badUnitSecs);

        var clock = new ManualTimeProvider();
        var anomalyLedger = new AnomalyLedger(new AnomalyLedgerLimits(MaxKeys: 8, LogLimit: 2), clock);
        var faultLedger = new FaultLedger(new FaultLedgerLimits(MaxSignatures: 8, LogLimit: 2), clock);

        using AnomalyCapture capture = anomalyLedger.Capture();
        for (int i = 0; i < 120; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(rng.Next(0, 30)));
            string sub = $"sub-{rng.Next(0, 6)}";
            string code = $"code-{rng.Next(0, 6)}";
            string subj = $"player-{rng.Next(0, 300)}";
            string detail = $"detail-{rng.Next(0, 10)}";
            AnomalyRecording rec = anomalyLedger.Report(sub, code, subj, detail, (AnomalySeverity)(i % 3));
            Assert.True(rec.Count >= 1);

            var ex = new InvalidOperationException($"fault-msg-{rng.Next(0, 25)}");
            FaultRecording faultRec = faultLedger.Record($"site-{rng.Next(0, 12)}", ex);
            Assert.True(faultRec.Count >= 1);
            Assert.True(FaultLedger.WasRecorded(ex));
        }

        Assert.Equal(120, anomalyLedger.Total);
        Assert.True(anomalyLedger.Distinct <= 8);
        Assert.NotEmpty(anomalyLedger.Snapshot());
        Assert.Equal(120, capture.Notices.Count);

        Assert.Equal(120, faultLedger.Total);
        Assert.True(faultLedger.Distinct <= 8);
        Assert.NotEmpty(faultLedger.Snapshot());
        Assert.NotEmpty(faultLedger.BySubsystem());
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string RandomToken(Random rng, int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.";
        var buffer = new char[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i] = chars[rng.Next(chars.Length)];
        }

        return new string(buffer);
    }

    private static string RandomQuotedPayload(Random rng, int maxLength)
    {
        string[] fragments = ["a", "b", " ", "  ", "=", "#", ";", "\"", "\\", ":\\", "Zażółć", "ü"];
        int count = rng.Next(0, maxLength);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append(fragments[rng.Next(fragments.Length)]);
        }

        return sb.ToString();
    }

    private static string RandomUnquotedPayload(Random rng, int maxLength)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789_:/.-#;=";
        int len = rng.Next(1, maxLength + 1);
        var buffer = new char[len];
        for (int i = 0; i < len; i++)
        {
            buffer[i] = chars[rng.Next(chars.Length)];
        }

        return new string(buffer);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp = 1_000_000;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => 1_000_000;

        public void Advance(TimeSpan delta)
        {
            _now = _now.Add(delta);
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
        }
    }

}
