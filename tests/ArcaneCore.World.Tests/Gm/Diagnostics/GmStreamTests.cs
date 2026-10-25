using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Gm.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Diagnostics;

public sealed class GmStreamTests
{
    private static readonly ObjectGuid GmA = new(1);
    private static readonly ObjectGuid GmB = new(2);

    private static List<GmStreamLine> Drain(GmStream stream)
    {
        var lines = new List<GmStreamLine>();
        stream.Drain(lines.Add);
        return lines;
    }

    [Fact]
    public void Publish_WithNoSubscribers_QueuesNothing_AndIsInactive()
    {
        var stream = new GmStream();

        Assert.False(stream.IsActive);
        Assert.Equal(0, stream.Publish("damagelog", "x"));
        Assert.Empty(Drain(stream));
    }

    [Fact]
    public void Publish_GoesOnlyToGmsFollowingTheChannel_Tagged()
    {
        var stream = new GmStream();
        stream.Subscribe(GmA, "damagelog");
        stream.Subscribe(GmB, "moves");

        Assert.Equal(1, stream.Publish("DamageLog", "boar hit you for 5"));

        Assert.Equal([new GmStreamLine(GmA, "[DamageLog] boar hit you for 5")], Drain(stream));
        Assert.True(stream.HasSubscribers("moves"));
        Assert.False(stream.HasSubscribers("proc"));
    }

    [Fact]
    public void Unsubscribe_StopsDelivery_AndTheStreamGoesInactive()
    {
        var stream = new GmStream();
        stream.Subscribe(GmA, "moves");

        Assert.True(stream.Unsubscribe(GmA, "moves"));
        Assert.False(stream.Unsubscribe(GmA, "moves"));

        Assert.False(stream.IsActive);
        Assert.Equal(0, stream.Publish("moves", "x"));
    }

    [Fact]
    public void RateLimit_DropsTheExcessPerGm_AndReportsItOnTheNextSecond()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 2 }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Subscribe(GmB, "moves");

        for (int i = 0; i < 5; i++)
        {
            stream.Publish("moves", "m" + i);
        }

        List<GmStreamLine> first = Drain(stream);
        Assert.Equal(2, first.Count(l => l.Gm == GmA));
        Assert.Equal(2, first.Count(l => l.Gm == GmB));

        clock.Advance(1);
        stream.Publish("moves", "next");

        List<GmStreamLine> second = Drain(stream);
        Assert.Contains(new GmStreamLine(GmA, "[stream] 3 lines suppressed (limit 2/s)"), second);
        Assert.Contains(new GmStreamLine(GmA, "[moves] next"), second);
    }

    [Fact]
    public void RateLimit_IsPerGm_NotShared()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Publish("moves", "a");
        stream.Subscribe(GmB, "moves");

        Assert.Equal(1, stream.Publish("moves", "b"));
    }

    [Fact]
    public void Sweep_EndsTimedOutSubscriptions_AndTellsTheGm()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { AutoOffAfter = TimeSpan.FromMinutes(5) }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Subscribe(GmA, "proc");

        clock.Advance(4 * 60);
        Assert.Equal(0, stream.Sweep());
        stream.Subscribe(GmA, "proc"); // renewed
        clock.Advance(2 * 60);

        Assert.Equal(1, stream.Sweep());
        Assert.Equal([new GmStreamLine(GmA, "[stream] moves turned off (timed out after 5 min)")], Drain(stream));
        Assert.Equal(["proc"], stream.Subscriptions(GmA).Select(s => s.Channel));
        Assert.True(stream.IsActive);

        clock.Advance(10 * 60);
        Assert.Equal(1, stream.Sweep());
        Assert.False(stream.IsActive);
    }

    [Fact]
    public void UnsubscribeAll_DropsEverySubscriptionOfThatGm()
    {
        var stream = new GmStream();
        stream.Subscribe(GmA, "moves");
        stream.Subscribe(GmA, "proc");
        stream.Subscribe(GmB, "moves");

        Assert.Equal(2, stream.UnsubscribeAll(GmA));
        Assert.Equal(0, stream.UnsubscribeAll(GmA));

        Assert.Empty(stream.Subscriptions(GmA));
        Assert.Equal(1, stream.Publish("moves", "x"));
        Assert.True(stream.IsActive);
    }

    [Fact]
    public void Subscribe_IsCappedPerGm_ButRenewingAnExistingChannelIsNot()
    {
        var stream = new GmStream(new GmStreamOptions { MaxChannelsPerGm = 2 });
        Assert.True(stream.Subscribe(GmA, "a"));
        Assert.True(stream.Subscribe(GmA, "b"));

        Assert.False(stream.Subscribe(GmA, "c"));
        Assert.True(stream.Subscribe(GmA, "A"));
        Assert.Equal(2, stream.Subscriptions(GmA).Count);
    }

    [Fact]
    public void Outbox_IsBounded_WhenNobodyDrains()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 10_000 }, clock);
        stream.Subscribe(GmA, "moves");

        for (int i = 0; i < 5000; i++)
        {
            stream.Publish("moves", "m" + i);
        }

        List<GmStreamLine> lines = Drain(stream);
        Assert.Equal(1024, lines.Count);
        Assert.Equal("[moves] m4999", lines[^1].Text);
    }

    [Fact]
    public void Subscribe_RejectsABlankChannel_AndPublishRejectsANullLine()
    {
        var stream = new GmStream();

        Assert.Throws<ArgumentException>(() => stream.Subscribe(GmA, " "));
        Assert.Throws<ArgumentNullException>(() => stream.Subscribe(GmA, null!));
        stream.Subscribe(GmA, "moves");
        Assert.Throws<ArgumentNullException>(() => stream.Publish("moves", null!));
        Assert.Throws<ArgumentNullException>(() => stream.Drain(null!));
    }

    [Fact]
    public void TheOptions_MustBePositive_AndDefaultToFivePerSecondEightChannelsTenMinutes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GmStream(new GmStreamOptions { MaxLinesPerSecond = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GmStream(new GmStreamOptions { MaxChannelsPerGm = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GmStream(new GmStreamOptions { AutoOffAfter = TimeSpan.Zero }));
        var defaults = new GmStreamOptions();
        Assert.Equal(5, defaults.MaxLinesPerSecond);
        Assert.Equal(8, defaults.MaxChannelsPerGm);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.AutoOffAfter);
    }

    [Fact]
    public void ChannelNames_AreCaseInsensitive_ForUnsubscribeAndHasSubscribers()
    {
        var stream = new GmStream();
        stream.Subscribe(GmA, "DamageLog");

        Assert.True(stream.HasSubscribers("damagelog"));
        Assert.True(stream.Unsubscribe(GmA, "DAMAGELOG"));
        Assert.False(stream.HasSubscribers("damagelog"));
        Assert.False(stream.IsActive);
    }

    [Fact]
    public void RateLimit_AllowsExactlyTheLimit_ThenARefreshAtTheNextSecondBoundary()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 3 }, clock);
        stream.Subscribe(GmA, "moves");

        int accepted = Enumerable.Range(0, 3).Sum(i => stream.Publish("moves", "m" + i));
        Assert.Equal(3, accepted);
        Assert.Equal(0, stream.Publish("moves", "over")); // the fourth in the same second is dropped

        clock.Advance(1); // exactly one second later opens a new window
        Assert.Equal(1, stream.Publish("moves", "fresh"));
    }

    [Fact]
    public void TheSuppressedNotice_IsSentOnceBeforeTheNextBatch_ThenForgotten()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Publish("moves", "a");
        stream.Publish("moves", "b");
        stream.Publish("moves", "c");
        Drain(stream);

        clock.Advance(1);
        stream.Publish("moves", "d");
        clock.Advance(1);
        stream.Publish("moves", "e");

        Assert.Equal(
            [
                new GmStreamLine(GmA, "[stream] 2 lines suppressed (limit 1/s)"),
                new GmStreamLine(GmA, "[moves] d"),
                new GmStreamLine(GmA, "[moves] e"),
            ],
            Drain(stream));
    }

    [Fact]
    public void Sweep_EndsASubscription_ExactlyAtItsExpiry_NotBefore()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { AutoOffAfter = TimeSpan.FromSeconds(60) }, clock);
        stream.Subscribe(GmA, "moves");

        clock.Advance(59);
        Assert.Equal(0, stream.Sweep());
        clock.Advance(1);
        Assert.Equal(1, stream.Sweep());
        Assert.Empty(stream.Subscriptions(GmA));
    }

    [Fact]
    public void Subscribing_Again_PushesTheExpiryOut()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { AutoOffAfter = TimeSpan.FromMinutes(10) }, clock);
        stream.Subscribe(GmA, "moves");
        DateTimeOffset first = stream.Subscriptions(GmA).Single().ExpiresAt;

        clock.Advance(120);
        stream.Subscribe(GmA, "moves");

        Assert.Equal(first.AddSeconds(120), stream.Subscriptions(GmA).Single().ExpiresAt);
    }

    [Fact]
    public void ARefusedSubscribe_LeavesTheGmsExistingSubscriptionsUntouched()
    {
        var stream = new GmStream(new GmStreamOptions { MaxChannelsPerGm = 1 });
        stream.Subscribe(GmA, "a");

        Assert.False(stream.Subscribe(GmA, "b"));

        Assert.Equal(["a"], stream.Subscriptions(GmA).Select(s => s.Channel));
        Assert.True(stream.IsActive);
        Assert.True(stream.Subscribe(GmB, "b")); // the cap is per GM
    }

    [Fact]
    public void Publish_ToAChannelNobodyFollows_QueuesNothing_WhileAnotherIsActive()
    {
        var stream = new GmStream();
        stream.Subscribe(GmA, "moves");

        Assert.Equal(0, stream.Publish("proc", "x"));
        Assert.Empty(Drain(stream));
    }

    [Fact]
    public void Drain_HandsOverTheLinesOnce_AndAConsumerMayPublishWithoutDeadlock()
    {
        var stream = new GmStream();
        stream.Subscribe(GmA, "moves");
        stream.Publish("moves", "first");
        var seen = new List<string>();

        int delivered = stream.Drain(line =>
        {
            seen.Add(line.Text);
            stream.Publish("moves", "from-consumer"); // runs outside the lock
        });

        Assert.Equal(1, delivered);
        Assert.Equal(["[moves] first"], seen);
        Assert.Equal(1, stream.Drain(_ => { })); // the consumer's line waits for the next drain
        Assert.Equal(0, stream.Drain(_ => { }));
    }

    [Fact]
    public async Task ConcurrentSubscribeAndUnsubscribe_LeavesTheStreamInactive()
    {
        var stream = new GmStream(new GmStreamOptions { MaxChannelsPerGm = 64 });

        await Task.WhenAll(Enumerable.Range(1, 4).Select(g => Task.Run(() =>
        {
            var gm = new ObjectGuid((ulong)g);
            for (int i = 0; i < 500; i++)
            {
                stream.Subscribe(gm, "c" + (i % 8));
                stream.Publish("c" + (i % 8), "x");
                stream.Unsubscribe(gm, "c" + (i % 8));
            }
        })));

        Assert.False(stream.IsActive);
        Assert.Equal(0, stream.UnsubscribeAll(new ObjectGuid(1)));
    }

    [Fact]
    public async Task Feature_SendsOnlyToTheSubscribedGm()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldTestClient gmA = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        WorldTestClient gmB = await host.EnterWorldAsync("GMB", "Gmbbb", AccountSecurity.GameMaster);
        await gmA.CollectAsync();
        await gmB.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        ObjectGuid guidA = (await host.PlayerAsync("Gmaaa")).Guid;
        ObjectGuid guidB = (await host.PlayerAsync("Gmbbb")).Guid;
        stream.Subscribe(guidA, "alpha");
        stream.Subscribe(guidB, "beta");
        stream.Subscribe(new ObjectGuid(987654321), "alpha"); // nobody online: delivery must skip it, not throw

        stream.Publish("alpha", "for A");
        stream.Publish("beta", "for B");

        Assert.Equal(["[alpha] for A"], await gmA.CollectChatLinesAsync(1));
        Assert.Equal(["[beta] for B"], await gmB.CollectChatLinesAsync(1));
        await gmA.DisposeAsync();
        await gmB.DisposeAsync();
    }

    [Fact]
    public async Task Feature_SendsQueuedLinesToTheGm_AndLogoutEndsTheSubscription()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        ObjectGuid guid = (await host.PlayerAsync("Gmaaa")).Guid;

        Assert.True(stream.Subscribe(guid, "moves"));
        stream.Publish("moves", "pos 1 2 3");

        Assert.Equal(["[moves] pos 1 2 3"], await gm.CollectChatLinesAsync(1));

        await gm.DisposeAsync();
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (stream.Subscriptions(guid).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Empty(stream.Subscriptions(guid));
    }
    private sealed class TickClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void TokenBucket_NeverLetsMoreThanTheLimitThroughInAnyOneSecond_AcrossAWindowBoundary()
    {
        var clock = new TickClock(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 4 }, clock);
        stream.Subscribe(GmA, "moves");

        int first = Enumerable.Range(0, 10).Sum(i => stream.Publish("moves", "a" + i)); // the bucket starts full
        clock.Advance(TimeSpan.FromMilliseconds(900));
        int second = Enumerable.Range(0, 10).Sum(i => stream.Publish("moves", "b" + i)); // 3.6 tokens came back
        clock.Advance(TimeSpan.FromMilliseconds(200));
        int third = Enumerable.Range(0, 10).Sum(i => stream.Publish("moves", "c" + i)); // 0.8 more: one line

        Assert.Equal(4, first);
        Assert.Equal(3, second);
        Assert.Equal(1, third);
    }

    [Fact]
    public void TheSuppressedNotice_IsSentBySweep_WhenPublishingStops()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Publish("moves", "a");
        stream.Publish("moves", "b");
        stream.Publish("moves", "c");
        Drain(stream);

        stream.Sweep(); // the bucket is still empty: nothing to say yet
        Assert.Empty(Drain(stream));

        clock.Advance(1);
        stream.Sweep(); // nobody publishes any more, the notice still goes out
        Assert.Equal([new GmStreamLine(GmA, "[stream] 2 lines suppressed (limit 1/s)")], Drain(stream));

        clock.Advance(1);
        stream.Sweep();
        Assert.Empty(Drain(stream)); // once only
    }

    [Fact]
    public void ATimeoutNotice_SurvivesAFullOutbox_AndTheDroppedLinesAreCounted()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 10_000, AutoOffAfter = TimeSpan.FromMinutes(1) }, clock);
        stream.Subscribe(GmA, "moves");
        for (int i = 0; i < 1100; i++)
        {
            stream.Publish("moves", "m" + i);
        }

        clock.Advance(120);
        stream.Sweep();

        List<GmStreamLine> lines = Drain(stream);
        Assert.Equal(new GmStreamLine(GmA, "[stream] moves turned off (timed out after 1 min)"), lines[^1]); // after the lines published before the timeout
        Assert.Equal(1024, lines.Count - 1);
        Assert.Equal(1100 - 1024, stream.OutboxDropped);
    }

    [Fact]
    public void Sweep_StillReportsSuppressedLines_WhenTheLastChannelTimesOut()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1, AutoOffAfter = TimeSpan.FromMinutes(5) }, clock);
        stream.Subscribe(GmA, "moves");
        for (int i = 0; i < 3; i++)
        {
            stream.Publish("moves", "m" + i);
        }

        Drain(stream);
        clock.Advance(6 * 60);
        Assert.Equal(1, stream.Sweep());

        List<GmStreamLine> lines = Drain(stream);
        Assert.Contains(new GmStreamLine(GmA, "[stream] 2 lines suppressed (limit 1/s)"), lines);
        Assert.Contains(new GmStreamLine(GmA, "[stream] moves turned off (timed out after 5 min)"), lines);
        Assert.False(stream.IsActive);
    }

    [Fact]
    public void TurningTheStreamOffAndOn_DoesNotRefillTheGmsBucket()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(GmA, "moves");
        Assert.Equal(1, stream.Publish("moves", "a"));

        Assert.True(stream.Unsubscribe(GmA, "moves"));
        stream.Subscribe(GmA, "moves");
        Assert.Equal(0, stream.Publish("moves", "b")); // the bucket is still empty

        stream.UnsubscribeAll(GmA);
        stream.Subscribe(GmA, "moves");
        Assert.Equal(0, stream.Publish("moves", "c"));

        clock.Advance(1);
        Assert.Equal(1, stream.Publish("moves", "d")); // refilled by time alone
    }

    [Fact]
    public void ParkedBuckets_AreDroppedOnceFull_SoTheStreamKeepsNoStateForIdleGms()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Publish("moves", "a");
        stream.Unsubscribe(GmA, "moves");

        clock.Advance(2);
        stream.Sweep();
        stream.Subscribe(GmA, "moves");

        Assert.Equal(1, stream.Publish("moves", "b"));
    }

    [Fact]
    public void ASuppressedNotice_NeverOvertakesALinePublishedBeforeIt()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(GmA, "moves");
        stream.Publish("moves", "a");
        stream.Publish("moves", "dropped");
        clock.Advance(1);
        stream.Publish("moves", "c");

        Assert.Equal(
            [
                new GmStreamLine(GmA, "[moves] a"),
                new GmStreamLine(GmA, "[stream] 1 lines suppressed (limit 1/s)"),
                new GmStreamLine(GmA, "[moves] c"),
            ],
            Drain(stream));
    }
}
