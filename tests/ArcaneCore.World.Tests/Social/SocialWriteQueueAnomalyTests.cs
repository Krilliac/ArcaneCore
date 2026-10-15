using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Tests.Persistence;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// The <c>persist/queue-backlog</c> anomaly of <see cref="SocialWriteQueue"/>: a new friend/ignore row refused because the character (or the realm)
/// already has the configured number of rows waiting. The queue is never started, so every accepted row stays pending and the count is exact.
/// </summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class SocialWriteQueueAnomalyTests
{
    private static SocialWriteQueue Queue(ServiceProvider provider, SocialWriteQueueOptions options)
        => new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, options);

    [Fact]
    public async Task QueueBacklog_FiresOnTheRefusedRow_WhenTheCharacterHasTooManyRowsWaiting()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        SocialWriteQueue queue = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 2, MaxPendingTotal = 100 });
        using AnomalyCapture capture = PersistAnomalies.Capture();

        Assert.True(queue.TrySetSocial(71, 1, SocialFlags.Friend));
        Assert.True(queue.TrySetSocial(71, 2, SocialFlags.Friend));
        Assert.Empty(capture.Notices); // at the limit but not over it

        Assert.False(queue.TrySetSocial(71, 3, SocialFlags.Friend));

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/queue-backlog", notice.Key);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Equal("queue SocialWriteQueue", notice.Subject);
        Assert.Contains("character=71", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("rowsTotal=2", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(2, queue.PendingRows);
    }

    [Fact]
    public async Task QueueBacklog_FiresOnTheRefusedRow_WhenTheRealmHasTooManyRowsWaiting()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        SocialWriteQueue queue = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 100, MaxPendingTotal = 2 });
        using AnomalyCapture capture = PersistAnomalies.Capture();

        Assert.True(queue.TrySetSocial(72, 1, SocialFlags.Friend));
        Assert.True(queue.TrySetSocial(73, 1, SocialFlags.Friend));
        Assert.False(queue.TrySetSocial(74, 1, SocialFlags.Friend)); // a third character, under its own limit, hits the realm one

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/queue-backlog", notice.Key);
        Assert.Contains("character=74", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueBacklog_IsSilent_ForRowsAlreadyTracked_AndForLimitsThatAreOff()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        SocialWriteQueue limited = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 1, MaxPendingTotal = 100 });
        SocialWriteQueue unlimited = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 0, MaxPendingTotal = 0 });
        using AnomalyCapture capture = PersistAnomalies.Capture();

        Assert.True(limited.TrySetSocial(75, 1, SocialFlags.Friend));
        Assert.True(limited.TrySetSocial(75, 1, SocialFlags.Ignored)); // the same row again merges into the waiting write: no new row
        for (int other = 1; other <= 300; other++)
        {
            Assert.True(unlimited.TrySetSocial(76, other, SocialFlags.Friend)); // 0 or less means unlimited
        }

        Assert.Equal(1, limited.PendingRows);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public async Task QueueBacklog_IsReportedOncePerCharacter_UntilTheCharacterIsBackUnderTheLimit()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        SocialWriteQueue queue = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 1, MaxPendingTotal = 100 });
        using AnomalyCapture capture = PersistAnomalies.Capture();

        Assert.True(queue.TrySetSocial(81, 1, SocialFlags.Friend));
        for (int other = 2; other <= 11; other++)
        {
            Assert.False(queue.TrySetSocial(81, other, SocialFlags.Friend)); // ten refusals, one episode
        }

        Assert.Single(capture.Notices);
        Assert.True(queue.TrySetSocial(82, 2, SocialFlags.Friend));
        Assert.False(queue.TrySetSocial(82, 3, SocialFlags.Friend));
        Assert.Equal(2, capture.Of("queue-backlog", nameof(SocialWriteQueue)).Length); // character 82 is its own episode
    }

    [Fact]
    public async Task QueueBacklog_IsReportedAgain_OnceTheCharacterWasBackUnderTheLimit()
    {
        var store = new GatedSocialStore();
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ISocialStore>(store).BuildServiceProvider();
        SocialWriteQueue queue = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 1, MaxPendingTotal = 100 });
        queue.Start();
        using AnomalyCapture capture = PersistAnomalies.Capture();

        try
        {
            Assert.True(queue.TrySetSocial(83, 1, SocialFlags.Friend));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the consumer holds the write
            Assert.False(queue.TrySetSocial(83, 2, SocialFlags.Friend));
            Assert.False(queue.TrySetSocial(83, 3, SocialFlags.Friend));
            Assert.Single(capture.Notices);

            store.Release.TrySetResult();
            await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10)); // row 1 is durable: the character is back under the limit
            Assert.Equal(0, queue.PendingRows);

            store.Hold = true;
            store.ResetEntered();
            Assert.True(queue.TrySetSocial(83, 4, SocialFlags.Friend));
            Assert.False(queue.TrySetSocial(83, 5, SocialFlags.Friend)); // over the limit again: a new episode

            Assert.Equal(2, capture.Of("queue-backlog", nameof(SocialWriteQueue)).Length);
        }
        finally
        {
            store.Hold = false;
            store.Release.TrySetResult();
            await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SocialReports_AreMadeAfterTheQueueGateWasReleased()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        SocialWriteQueue queue = Queue(provider, new SocialWriteQueueOptions { MaxPendingPerCharacter = 1, MaxPendingTotal = 100 });
        using var probe = new GateProbe(() => queue.HasRetainedFailure(84));

        Assert.True(queue.TrySetSocial(84, 1, SocialFlags.Friend));
        Assert.False(queue.TrySetSocial(84, 2, SocialFlags.Friend)); // queue-backlog
        queue.Start();
        await queue.StopAsync();
        Assert.False(queue.TrySetSocial(84, 3, SocialFlags.Friend)); // write-dropped-on-shutdown (stopped)
        queue.DeleteGuild(1); // write-dropped-on-shutdown (stopped queue: the channel is closed)

        Assert.True(probe.Reports >= 3, $"reports seen: {probe.Reports}");
        Assert.False(probe.GateHeld, "a report was made while the queue's gate was held");
    }

    private sealed class GatedSocialStore : ISocialStore
    {
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Hold = true;

        public TaskCompletionSource Entered => _entered;

        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetEntered()
        {
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
        {
            if (Hold)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
        }

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SocialEntry>>([]);
        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
