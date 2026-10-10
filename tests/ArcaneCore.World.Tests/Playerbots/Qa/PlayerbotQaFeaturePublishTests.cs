using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>A transport whose list call waits until released, like a GitHub that answered 429 with a long Retry-After.</summary>
internal sealed class StuckTransport : IQaIssueTransport
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lists;

    public bool SupportsSearch => false;

    public int Lists => Volatile.Read(ref _lists);

    public Task Entered => _entered.Task;

    public async Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lists);
        _entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return new QaIssueIndex(complete: true);
    }

    public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken) => Task.FromResult<(QaIssueRef, string)?>(null);

    public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

    public Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken) => throw new InvalidOperationException();

    public Task CommentAsync(int number, string body, CancellationToken cancellationToken) => throw new InvalidOperationException();

    public Task ReopenAsync(int number, CancellationToken cancellationToken) => throw new InvalidOperationException();

    public void Dispose()
    {
    }
}

/// <summary>The feature's pass only schedules publishing: a publisher stuck in a rate-limit wait must not stop the signal queue from being drained.</summary>
public sealed class PlayerbotQaFeaturePublishTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    [Fact]
    public async Task AStuckPublisher_DoesNotBlockDrain_NorTheNextPass_AndStopsCleanly()
    {
        var options = new PlayerbotQaOptions
        {
            Directory = _directory,
            GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo, MinOccurrences = 1, MinDistinctBots = 1 },
        };
        var bus = new PlayerbotQaSignalBus(64);
        var transport = new StuckTransport();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(bus)
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services)
        {
            EnvironmentReader = _ => QaData.FakeToken,
            TransportFactory = _ => transport,
        };
        feature.Attach(null!);
        Assert.True(bus.TryWrite(QaData.Relay(7)));
        Assert.Equal(1, feature.Drain());

        feature.RunPass(CancellationToken.None);
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(feature.Publishing.IsCompleted); // the publisher is stuck inside the list call
        Assert.True(bus.TryWrite(QaData.Relay(8)));
        Assert.Equal(1, feature.Drain()); // draining is not blocked
        feature.RunPass(CancellationToken.None); // a second pass while publishing is stuck returns at once and schedules nothing
        Assert.Equal(1, transport.Lists);
        Assert.Equal(2, feature.Aggregator!.Count);

        await feature.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(feature.Publishing.IsCompleted);
        Assert.True(File.Exists(Path.Combine(_directory, "findings.json")));
    }
}
