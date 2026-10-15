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
}
