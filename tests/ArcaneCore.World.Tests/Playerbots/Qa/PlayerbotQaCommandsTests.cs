using System.Text;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary><c>.playerbot qa</c> run as the console on a real in-process world: listing, evidence, reset, and the inert case.</summary>
public sealed class PlayerbotQaCommandsTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private sealed class Sink : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate) _lines.Add((value ?? string.Empty).Replace(WorldConsoleExecutor.Prefix, string.Empty, StringComparison.Ordinal));
        }

        public string[] Take()
        {
            lock (_gate)
            {
                string[] lines = [.. _lines];
                _lines.Clear();
                return lines;
            }
        }
    }

    private static async Task<string[]> RunAsync(WorldTestHost host, string line)
    {
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        var sink = new Sink();
        WorldConsoleExecutor executor = WorldConsoleExecutor.Create(scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, sink);
        await host.World.InvokeAsync(() =>
        {
            executor.Execute(line);
            return 0;
        });
        return sink.Take();
    }

    private WorldTestHost StartWithQa(Action<PlayerbotQaOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions { Directory = _directory };
        configure?.Invoke(options);
        return WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options));
            services.AddSingleton(new PlayerbotQaSignalBus(64));
        });
    }

    private static string Ingest(PlayerbotQaFeature feature, QaSignal signal)
        => feature.Aggregator!.Ingest(signal, NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    [Fact]
    public async Task Qa_WhenDisabled_SaysItIsNotRunning()
    {
        await using WorldTestHost host = StartWithQa(o => o.Enabled = false);

        string[] lines = await RunAsync(host, ".playerbot qa");

        Assert.Equal(["Bot QA is not running (World:Playerbots:Qa:Enabled)."], lines);
    }

    [Fact]
    public async Task Qa_ListsTheRankedFindings_AndTheReporterState()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string id = Ingest(feature, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));
        Ingest(feature, QaData.Stall(QaData.Bot2, -5610f, -440f));

        string[] lines = await RunAsync(host, ".playerbot qa");

        Assert.StartsWith("Bot QA: 1 findings", lines[0], StringComparison.Ordinal);
        Assert.Contains("GitHub: off", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("ghp_", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("#1 " + PlayerbotQaFingerprint.ShortId(id), StringComparison.Ordinal));
        Assert.Equal(lines[1..], await RunAsync(host, ".playerbot qa top"));
    }

    [Fact]
    public async Task Qa_Status_ShowsTheTokenAsSetOrNotSet_NeverTheValue()
    {
        await using WorldTestHost host = StartWithQa(o =>
        {
            o.GitHub.Enabled = true;
            o.GitHub.Repository = QaData.Repo;
            o.GitHub.TokenEnvironmentVariable = "ARCANECORE_QA_TEST_TOKEN_UNSET_VARIABLE";
        });

        string[] lines = await RunAsync(host, ".playerbot qa");

        Assert.Contains("GitHub: dry-run, ARCANECORE_QA_TEST_TOKEN_UNSET_VARIABLE not set.", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QaTop_BadNumber_ShowsUsage_AndTopClamps()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        for (uint quest = 1; quest <= 30; quest++) Ingest(feature, QaData.Stall(QaData.Bot1, 1, 1, quest));

        string[] many = await RunAsync(host, ".playerbot qa top 999");
        string[] bad = await RunAsync(host, ".playerbot qa top banana");

        Assert.Equal(PlayerbotQaReport.MaxChatFindings + 1, many.Length);
        Assert.Contains(bad, l => l.Contains("Syntax", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QaShow_ResolvesAPrefix_AndExplainsABadOne()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string id = Ingest(feature, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));

        string[] shown = await RunAsync(host, ".playerbot qa show " + id[..6]);
        string[] tooShort = await RunAsync(host, ".playerbot qa show ab");
        string[] missing = await RunAsync(host, ".playerbot qa show " + (id[0] == '0' ? "ffff" : "0000"));

        Assert.StartsWith(id + " stall:", shown[0], StringComparison.Ordinal);
        Assert.Contains(shown, l => l.StartsWith("go there: .go xyz", StringComparison.Ordinal));
        Assert.Contains("at least 4", tooShort[0], StringComparison.Ordinal);
        Assert.Contains("No finding", missing[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QaReset_ForgetsOneOrAll_LocallyOnly()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string first = Ingest(feature, QaData.Stall(QaData.Bot1, 1, 1, quest: 1));
        Ingest(feature, QaData.Stall(QaData.Bot1, 1, 1, quest: 2));
        Ingest(feature, QaData.Stall(QaData.Bot1, 1, 1, quest: 3));

        string[] one = await RunAsync(host, ".playerbot qa reset " + first[..8]);
        Assert.Contains("forgotten (GitHub is untouched)", one[0], StringComparison.Ordinal);
        Assert.Equal(2, feature.Aggregator!.Count);

        string[] all = await RunAsync(host, ".playerbot qa reset");
        Assert.Contains("2 findings forgotten", all[0], StringComparison.Ordinal);
        Assert.Equal(0, feature.Aggregator.Count);
    }
}
