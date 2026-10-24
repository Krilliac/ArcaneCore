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
}
