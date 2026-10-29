using System.Diagnostics.Metrics;
using System.Reflection;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Docs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// CI guards for "declared but not wired" (docs/ops/wiring-audit.md): the production service collection builds with scope and build
/// validation on and really contains what Program.cs adds, every <see cref="IConfigCheck"/> is run by the daemon, no documented options
/// type is asked for without being configured, unhandled client opcodes are counted and warned once, and <c>.server wiring</c> lists
/// what the audit finds.
/// </summary>
public sealed class WiringAuditTests
{
    /// <summary>
    /// Options types that a class asks for through <c>IOptions&lt;T&gt;</c> and whose section is in the catalog, yet that the production
    /// container does not configure, each with the reason it is acceptable. Add a row only with a reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> UnboundAllowlist = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DiagnosticsOptions"] = "bound by UseArcaneDiagnostics() in Program.cs, which this test does not call because it installs process-wide crash hooks",
    };

    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IHost BuildProductionHost(string directory) => ProductionHostKit.Build(directory);

    private static void AssertConfigured<TOptions>(IServiceProvider services)
        where TOptions : class
        => Assert.True(services.GetServices<IConfigureOptions<TOptions>>().Any(), $"nothing configures {typeof(TOptions).Name} in the production container");

    [Fact]
    public void ProductionServiceCollection_BuildsWithScopeAndBuildValidation_ContainsEveryAddCall_AndHasNoStaticWiringFindings()
    {
        string directory = NewTempDirectory();
        try
        {
            using IHost host = BuildProductionHost(directory);
            IServiceProvider services = host.Services;

            // The build validation above only fails on a dependency that is missing; a registration nothing depends on (the console, the
            // watchdog, the metrics store, the diagnostics services: all absent from the mock fixture) is checked by what it adds.
            AssertConfigured<WorldConsoleOptions>(services);
            AssertConfigured<ArcaneCore.World.Ops.Control.WorldControlPipeOptions>(services);
            AssertConfigured<NetProtectionOptions>(services);
            AssertConfigured<WatchdogOptions>(services);
            AssertConfigured<MetricsOptions>(services);
            Assert.NotNull(services.GetRequiredService<MetricsStore>());
            Assert.NotNull(services.GetRequiredService<OpsDiagnosticsOptions>());
            Assert.NotNull(services.GetRequiredService<AllocationProfiler>());
            Assert.NotNull(services.GetRequiredService<ProcessDumpService>());
            Assert.NotNull(services.GetRequiredService<ClientDataReport>());
            Assert.NotNull(services.GetRequiredService<UnhandledOpcodeTracker>());
            string[] hosted = [.. services.GetServices<IHostedService>().Select(s => s.GetType().Name)];
            Assert.Contains("WorldConsole", hosted);
            Assert.Contains("MetricsHost", hosted);
            Assert.Contains("WorldHost", hosted);
            Assert.Contains("WorldServer", hosted);
            // Hosted services stop in reverse order: the WAL checkpointer (registered by AddAuthDatabase) must stop, and run its final checkpoint, after WorldHost has drained the save queue.
            Assert.False(services.GetRequiredService<IOptions<HostOptions>>().Value.ServicesStopConcurrently, "concurrent stop would run the WAL checkpointer's final checkpoint before the save queue drains");
            Assert.Contains("SqliteCheckpointService", hosted);
            Assert.True(Array.IndexOf(hosted, "SqliteCheckpointService") < Array.IndexOf(hosted, "WorldHost"), "the WAL checkpointer must be registered before WorldHost so it stops after it");
            Assert.Equal("WiringAuditReporter", hosted[^1]);

            WiringReport report = services.GetRequiredService<WiringAudit>().Run();
            WiringFinding[] unexplained = [.. report.Findings
                .Where(f => f.Category != WiringAudit.UnhandledOpcode) // counted at run time; other tests in this process send unhandled opcodes
                .Where(f => !(f.Category == WiringAudit.OptionsUnbound && UnboundAllowlist.ContainsKey(f.Subject)))];
            Assert.True(unexplained.Length == 0, "wiring findings: " + string.Join(Environment.NewLine, unexplained.Select(f => f.ToString())));

            // The audit looked at something: a clean report over zero handlers or zero consumers would prove nothing.
            Assert.True(report.OpcodeHandlers > 100, $"{report.OpcodeHandlers} handlers");
            Assert.True(report.ConfigChecksRun >= 12, $"{report.ConfigChecksRun} config checks");
            Assert.True(report.OptionsConsumersChecked > 0, "no IOptions consumer was examined");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildValidation_RejectsACollectionWithAMissingDependency()
    {
        // The mechanism the production test relies on: with ValidateOnBuild a registration whose dependency was never added throws at build.
        var services = new ServiceCollection();
        services.AddSingleton<BrokenConsumer>();
        Assert.Throws<AggregateException>(() => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
    }

    private sealed class BrokenConsumer(NotRegisteredDependency dependency)
    {
        public NotRegisteredDependency Dependency { get; } = dependency;
    }

    private sealed class NotRegisteredDependency;

    [Fact]
    public void EveryConcreteConfigCheck_IsRunByTheWorldDaemonsValidation()
    {
        IReadOnlyList<Assembly> assemblies = WiringAudit.DefaultAssemblies();
        Assert.Contains(typeof(IConfigCheck).Assembly, assemblies);
        IReadOnlyList<Type> concrete = ConfigCheckDiscovery.ConcreteTypes(assemblies);
        Assert.True(concrete.Count >= 12, $"{concrete.Count} concrete checks were found");

        HashSet<Type> run = [.. OpsCli.CreateChecks().Select(c => c.GetType())];
        string[] unrun = [.. concrete.Where(t => !run.Contains(t)).Select(t => t.FullName ?? t.Name)];
        Assert.True(unrun.Length == 0, "config checks never run: " + string.Join(", ", unrun));

        // The two that used to be written but never run, and the one the daemon supplies itself.
        Assert.Contains(typeof(AntiCheatConfigChecks), run);
        Assert.Contains(typeof(PlayerbotQaConfigChecks), run);
        Assert.Contains(typeof(ConfKeyCheck), run);
        Assert.Empty(ConfigCheckDiscovery.NotRun(assemblies, OpsCli.CreateChecks()));
    }

    [Fact]
    public void ConfigCheckDiscovery_IsDeterministic_AndKeepsACheckThatNeedsArgumentsOutOfTheCreatedSet()
    {
        Assert.Equal(
            OpsCli.CreateChecks().Select(c => c.GetType().FullName),
            OpsCli.CreateChecks().Select(c => c.GetType().FullName));

        Assembly[] fixture = [typeof(WiringAuditTests).Assembly];
        Assert.Contains(typeof(NeedsArguments), ConfigCheckDiscovery.NotRun(fixture, []));
        Type[] created = [.. ConfigCheckDiscovery.Create(fixture).Select(c => c.GetType())];
        Assert.DoesNotContain(typeof(NeedsArguments), created);
        Assert.Contains(typeof(ParameterlessCheck), created);
    }

    private sealed class NeedsArguments(int value) : IConfigCheck
    {
        public IEnumerable<ConfigIssue> Check(IConfiguration configuration) => value == 0 ? [] : [];
    }

    private sealed class ParameterlessCheck : IConfigCheck
    {
        public IEnumerable<ConfigIssue> Check(IConfiguration configuration) => [];
    }

    [Fact]
    public void NewlyWiredChecks_ReportAnErrorForABadValue_AndNothingForTheDefaults()
    {
        IConfiguration defaults = new ConfigurationBuilder().Build();
        Assert.Empty(new AntiCheatConfigChecks().Check(defaults));
        Assert.Empty(new PlayerbotQaConfigChecks().Check(defaults));

        IConfiguration bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AntiCheat:Enabled"] = "perhaps" }).Build();
        ConfigReport report = OpsCli.Validate(bad);
        Assert.Contains(report.Issues, i => i.Severity == ConfigSeverity.Error && i.Key == "AntiCheat");
        Assert.True(report.IsInvalid);
    }

    [Fact]
    public void TheShippedAppsettings_AreNotInvalid_AndAMissingQaTokenIsOnlyAWarning()
    {
        string json = Path.Combine(RepoRoot.Find(), "src", "ArcaneCore.World", "appsettings.json");
        IConfiguration shipped = new ConfigurationBuilder().AddJsonFile(json, optional: false).Build();

        ConfigReport report = OpsCli.Validate(shipped);
        Assert.Equal(0, report.ErrorCount);
        Assert.False(report.IsInvalid, report.Issues.Count + " issue(s); a stock start must not exit 78: " + string.Join("; ", report.Issues.Select(i => i.ToString())));

        // Publishing enabled with the token variable unset is a warning (a stock profile has no token), never an error.
        IConfiguration publishing = new ConfigurationBuilder().AddJsonFile(json, optional: false).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Qa:GitHub:Enabled"] = "true",
            ["World:Playerbots:Qa:GitHub:Publish"] = "true",
            ["World:Playerbots:Qa:GitHub:Repository"] = "owner/repo",
            ["World:Playerbots:Qa:GitHub:TokenEnvironmentVariable"] = "ARCANECORE_TEST_TOKEN_THAT_IS_NOT_SET",
        }).Build();
        ConfigReport withQa = OpsCli.Validate(publishing);
        ConfigIssue token = Assert.Single(withQa.Issues, i => i.Key.EndsWith("GitHub:TokenEnvironmentVariable", StringComparison.Ordinal));
        Assert.Equal(ConfigSeverity.Warning, token.Severity);
        Assert.False(withQa.IsInvalid, string.Join("; ", withQa.Issues.Select(i => i.ToString())));
    }

    [Theory]
    [InlineData("GhCli", "false", false)] // gh uses its own login: no token needed, no warning
    [InlineData("GhCli", "true", true)]   // the token is handed to gh: it must exist
    [InlineData("Rest", "false", true)]
    public void QaTokenWarning_AppliesOnlyWhenThePublisherNeedsTheToken(string transport, string passToken, bool warns)
    {
        const string variable = "ARCANECORE_TEST_QA_TOKEN_NOT_SET";
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Qa:GitHub:Enabled"] = "true",
            ["World:Playerbots:Qa:GitHub:Publish"] = "true",
            ["World:Playerbots:Qa:GitHub:Repository"] = "owner/repo",
            ["World:Playerbots:Qa:GitHub:Transport"] = transport,
            ["World:Playerbots:Qa:GitHub:GhPassToken"] = passToken,
            ["World:Playerbots:Qa:GitHub:TokenEnvironmentVariable"] = variable,
        }).Build();
        ConfigIssue[] issues = [.. new PlayerbotQaConfigChecks().Check(config).Where(i => i.Key.EndsWith("TokenEnvironmentVariable", StringComparison.Ordinal))];
        Assert.Equal(warns, issues.Length == 1);
        Assert.All(issues, i => Assert.Equal(ConfigSeverity.Warning, i.Severity));
    }

    [Fact]
    public void Audit_UnhandledCountsStayLive_AcrossRuns()
    {
        var tracker = new UnhandledOpcodeTracker();
        var services = new ServiceCollection();
        services.AddSingleton(tracker);
        using ServiceProvider provider = services.BuildServiceProvider();
        var audit = new WiringAudit(provider, [typeof(WiringAuditTests).Assembly], FixtureCatalog());
        Assert.DoesNotContain(audit.Run().Findings, f => f.Category == WiringAudit.UnhandledOpcode);
        tracker.Record(WorldOpcode.CmsgNewSpellSlot);
        WiringReport second = audit.Run();
        Assert.Contains(second.Findings, f => f.Category == WiringAudit.UnhandledOpcode);
        Assert.Contains(second.Findings, f => f.Category == WiringAudit.OptionsUnbound); // still reported from the cached scan
    }

    /// <summary>Hot modules and <c>.hotcode refresh</c> swap the handler table at run time, so the handler audit is never served from the cache.</summary>
    [Fact]
    public void Audit_SeesAHandlerTableSwappedAfterTheFirstRun()
    {
        var table = new ArcaneCore.World.Handlers.OpcodeTable();
        table.OnSession(WorldOpcode.CmsgPing, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(new UnhandledOpcodeTracker());
        services.AddSingleton(table);
        using ServiceProvider provider = services.BuildServiceProvider();
        var audit = new WiringAudit(provider, [typeof(WiringAuditTests).Assembly], FixtureCatalog());
        WiringReport first = audit.Run();
        Assert.DoesNotContain(first.Findings, f => f.Category == WiringAudit.ServerOpcodeHandler);

        var swapped = new ArcaneCore.World.Handlers.OpcodeTable();
        swapped.OnSession(WorldOpcode.CmsgPing, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        swapped.OnSession(WorldOpcode.SmsgPong, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        table.Replace(swapped);

        WiringReport second = audit.Run();
        Assert.Contains(second.Findings, f => f is { Category: WiringAudit.ServerOpcodeHandler, Subject: "SMSG_PONG" });
        Assert.Equal(first.OpcodeHandlers + 1, second.OpcodeHandlers);
    }

    [Fact]
    public void UnhandledOpcodeTracker_CountsPerOpcode_BoundsUnnamedValues_AndIsThreadSafe()
    {
        var tracker = new UnhandledOpcodeTracker();
        Assert.Equal(1, tracker.Record(WorldOpcode.CmsgNewSpellSlot));
        Assert.Equal(2, tracker.Record(WorldOpcode.CmsgNewSpellSlot));
        Assert.Equal(1, tracker.Record(WorldOpcode.CmsgEmblazonTabardObsolete));

        // The 826 gap, and values past the last opcode, share one bucket however many distinct values arrive.
        Assert.Equal(1, tracker.Record((WorldOpcode)826));
        Assert.Equal(2, tracker.Record((WorldOpcode)0xFFFF));
        Assert.Equal(3, tracker.Record((WorldOpcode)5000));
        Assert.Equal(UnhandledOpcodeTracker.UnknownLabel, UnhandledOpcodeTracker.LabelOf((WorldOpcode)5000));
        Assert.Equal("CMSG_NEW_SPELL_SLOT", UnhandledOpcodeTracker.LabelOf(WorldOpcode.CmsgNewSpellSlot));

        Assert.Equal(
            [new UnhandledOpcodeCount(UnhandledOpcodeTracker.UnknownLabel, 3), new UnhandledOpcodeCount("CMSG_NEW_SPELL_SLOT", 2), new UnhandledOpcodeCount("CMSG_EMBLAZON_TABARD_OBSOLETE", 1)],
            tracker.Snapshot());
        Assert.Equal(6, tracker.Total);

        var parallel = new UnhandledOpcodeTracker();
        long firsts = 0;
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 1000; i++)
            {
                if (parallel.Record(WorldOpcode.CmsgEmblazonTabardObsolete) == 1)
                {
                    Interlocked.Increment(ref firsts);
                }
            }
        });
        Assert.Equal(8000, parallel.Total);
        Assert.Equal(1, firsts); // exactly one caller owns the warn-once
    }

    [Fact]
    public void UnhandledOpcodeMetric_ReportsTheSharedTrackerTaggedByOpcodeName()
    {
        UnhandledOpcodeTracker.Shared.Record(WorldOpcode.CmsgStandstatechange); // an opcode no other test sends unhandled; also forces static initialisation
        var seen = new List<(long Value, string? Protocol, string? Opcode)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == UnhandledOpcodeTracker.MetricName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? protocol = null;
            string? opcode = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "protocol")
                {
                    protocol = tag.Value as string;
                }

                if (tag.Key == "opcode")
                {
                    opcode = tag.Value as string;
                }
            }

            seen.Add((value, protocol, opcode));
        });
        listener.Start();
        listener.RecordObservableInstruments();
        Assert.Contains(seen, s => s.Opcode == "CMSG_STANDSTATECHANGE" && s.Protocol == "world" && s.Value >= 1);
    }

    private sealed class LevelLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task AnUnhandledClientOpcode_IsDropped_Counted_AndWarnedOncePerOpcode()
    {
        var tracker = new UnhandledOpcodeTracker();
        var log = new LevelLogger();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(tracker), sessionLogger: log);
        byte[] key = await host.AddAccountAsync("UNHANDLED");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("UNHANDLED", key);
        await client.CollectAsync();

        for (int i = 0; i < 3; i++)
        {
            await client.SendAsync(WorldOpcode.CmsgNewSpellSlot, []);
        }

        await client.SendAsync(WorldOpcode.CmsgEmblazonTabardObsolete, []);
        await client.SendAsync(WorldOpcode.CmsgPing, new byte[8]);
        await client.ReadUntilAsync(WorldOpcode.SmsgPong); // pings are answered in order: the four packets above were dispatched

        Assert.Equal(
            [new UnhandledOpcodeCount("CMSG_NEW_SPELL_SLOT", 3), new UnhandledOpcodeCount("CMSG_EMBLAZON_TABARD_OBSOLETE", 1)],
            tracker.Snapshot());

        string[] warnings;
        lock (log.Entries)
        {
            warnings = [.. log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("has no handler", StringComparison.Ordinal)).Select(e => e.Message)];
        }

        Assert.Equal(2, warnings.Length); // one per opcode, not one per packet
        Assert.Contains(warnings, m => m.Contains("client sent CMSG_NEW_SPELL_SLOT (0x12D) which has no handler; further occurrences are counted", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("client sent CMSG_EMBLAZON_TABARD_OBSOLETE (0x00D) which has no handler", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_ListsUnhandledOpcodes_UnconfiguredOptions_ServerOpcodeHandlers_AndUnrunChecks()
    {
        var tracker = new UnhandledOpcodeTracker();
        tracker.Record(WorldOpcode.CmsgNewSpellSlot);
        var table = new ArcaneCore.World.Handlers.OpcodeTable();
        table.OnSession(WorldOpcode.SmsgPong, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        table.OnSession(WorldOpcode.CmsgPing, SessionStates.Authenticated, (_, _) => Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(tracker);
        services.AddSingleton(table);
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            // The fixture assembly holds a consumer of an options type whose section is in the catalog; the container configures nothing for it.
            WiringReport report = new WiringAudit(provider, [typeof(WiringAuditTests).Assembly], FixtureCatalog()).Run();

            Assert.Contains(report.Findings, f => f is { Category: WiringAudit.UnhandledOpcode, Subject: "CMSG_NEW_SPELL_SLOT" });
            Assert.Contains(report.Findings, f => f is { Category: WiringAudit.ServerOpcodeHandler, Subject: "SMSG_PONG" });
            Assert.DoesNotContain(report.Findings, f => f.Subject == "CMSG_PING");
            WiringFinding unbound = Assert.Single(report.Findings, f => f.Category == WiringAudit.OptionsUnbound);
            Assert.Equal(nameof(FixtureOptions), unbound.Subject);
            Assert.Contains(nameof(UnconfiguredConsumer), unbound.Detail, StringComparison.Ordinal);
            Assert.Contains(report.Findings, f => f.Category == WiringAudit.ConfigCheckNotRun && f.Subject.Contains(nameof(NeedsArguments), StringComparison.Ordinal));
            Assert.False(report.IsClean);
            Assert.Contains("unhandled-opcode 1", report.Summary, StringComparison.Ordinal);
        }

        // Once the options are configured, the finding goes away.
        services.Configure<FixtureOptions>(_ => { });
        using ServiceProvider configured = services.BuildServiceProvider();
        WiringReport fixedReport = new WiringAudit(configured, [typeof(WiringAuditTests).Assembly], FixtureCatalog()).Run();
        Assert.DoesNotContain(fixedReport.Findings, f => f.Category == WiringAudit.OptionsUnbound);
    }

    private static ConfCatalog FixtureCatalog() => new(
    [
        new ConfKey("WiringFixture:Value", FixtureOptions.SectionName, "int", ConfKey.Scalar, "int", null, "0", [], ConfigDaemon.World, "start", "fixture", "a number", "docs"),
    ]);

    public sealed class FixtureOptions
    {
        public const string SectionName = "WiringFixture";

        public int Value { get; set; }
    }

    private sealed class UnconfiguredConsumer(IOptions<FixtureOptions> options)
    {
        public FixtureOptions Value => options.Value;
    }

    [Fact]
    public void WiringReport_SummaryIsOneLine_CleanOrCounted()
    {
        var clean = new WiringReport([], 10, 12, 3);
        Assert.Equal("wiring audit: clean (10 opcode handlers, 12 config checks, 3 IOptions consumers)", clean.Summary);

        var dirty = new WiringReport([new WiringFinding("a", "x", "d"), new WiringFinding("a", "y", "d"), new WiringFinding("b", "z", "d")], 1, 1, 1);
        Assert.Equal("wiring audit: 3 finding(s): a 2, b 1 (.server wiring lists them)", dirty.Summary);
        Assert.DoesNotContain('\n', dirty.Summary);
    }

    [Fact]
    public async Task ServerWiring_RunsAtTheConsole_AndListsTheFindings()
    {
        var tracker = new UnhandledOpcodeTracker();
        tracker.Record(WorldOpcode.CmsgNewSpellSlot);
        tracker.Record(WorldOpcode.CmsgNewSpellSlot);
        await using var host = WorldTestHost.Start(configureServices: c =>
        {
            c.AddSingleton(tracker);
            c.AddSingleton(sp => new WiringAudit(sp));
        });
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        var lines = new List<string>();
        WorldConsoleExecutor executor = WorldConsoleExecutor.Create(
            scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, new ListWriter(lines));

        await host.World.InvokeAsync(() =>
        {
            executor.Execute(".server wiring");
            return 0;
        });

        string[] output;
        lock (lines)
        {
            output = [.. lines];
        }

        Assert.StartsWith(WorldConsoleExecutor.Prefix + "wiring audit: ", output[0], StringComparison.Ordinal);
        Assert.Contains(output, l => l.Contains("[unhandled-opcode] CMSG_NEW_SPELL_SLOT: 2 client packet(s) arrived with no handler", StringComparison.Ordinal));

        // The formatter caps a long list and says how many it left out.
        var many = new WiringReport([.. Enumerable.Range(0, WiringCommands.MaxListed + 5).Select(i => new WiringFinding("c", "s" + i, "d"))], 0, 0, 0);
        IReadOnlyList<string> formatted = WiringCommands.Format(many);
        Assert.Equal(WiringCommands.MaxListed + 2, formatted.Count);
        Assert.Equal("... and 5 more.", formatted[^1]);
    }

    private sealed class ListWriter(List<string> lines) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (lines)
            {
                lines.Add(value ?? string.Empty);
            }
        }
    }
}
