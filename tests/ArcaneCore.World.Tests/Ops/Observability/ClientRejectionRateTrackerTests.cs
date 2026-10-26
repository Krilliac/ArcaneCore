using ArcaneCore.Game.ClientErrors;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Observability;

/// <summary><c>client/rejection-spike</c>: the cross-account refusal counter, driven by a fake clock.</summary>
public sealed class ClientRejectionRateTrackerTests
{
    private const ClientRejectionKind Kind = ClientRejectionKind.BuyFailed;

    private static int Fire(ClientRejectionRateTracker tracker, ClientRejectionKind kind, uint reason, int refusals, uint firstAccount, uint accounts)
    {
        int fired = 0;
        for (int i = 0; i < refusals; i++)
        {
            if (tracker.Note(kind, reason, firstAccount + ((uint)i % accounts), out _, out _))
            {
                fired++;
            }
        }

        return fired;
    }

    [Fact]
    public void ThreeHundredRefusalsFromEightAccounts_FireOnce()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);

        Assert.Equal(1, Fire(tracker, Kind, 5, ClientRejectionRateTracker.Threshold + 50, 1000, 16));
        Assert.False(tracker.Note(ClientRejectionKind.SellFailed, 3, 1, out _, out _)); // another pair is counted on its own
    }

    [Fact]
    public void ManyRefusalsFromFewAccounts_AreNotASpike_AndFewRefusalsFromManyAccountsAreNot()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);

        Assert.Equal(0, Fire(tracker, Kind, 5, ClientRejectionRateTracker.Threshold * 3, 1000, 2)); // one noisy client pair
        Assert.Equal(0, Fire(tracker, Kind, 6, ClientRejectionRateTracker.Threshold - 1, 1000, 40)); // wide but thin
    }

    [Fact]
    public void TheSpikeFiresAtTheRefusalThatCompletesIt_WithTheTotals()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);
        for (int i = 0; i < ClientRejectionRateTracker.Threshold - 1; i++)
        {
            Assert.False(tracker.Note(Kind, 5, 2000 + ((uint)i % 20), out _, out _));
        }

        Assert.True(tracker.Note(Kind, 5, 2000, out long count, out int accounts));
        Assert.Equal(ClientRejectionRateTracker.Threshold, count);
        Assert.InRange(accounts, ClientRejectionRateTracker.MinAccounts, 20);
    }

    [Fact]
    public void TheWindowSlides_SoSlowRefusalsNeverAccumulate()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);
        int fired = 0;
        for (int i = 0; i < ClientRejectionRateTracker.Threshold * 2; i++)
        {
            now += ClientRejectionRateTracker.WindowMs / 100; // a hundred refusals per window
            if (tracker.Note(Kind, 5, 3000 + ((uint)i % 30), out _, out _))
            {
                fired++;
            }
        }

        Assert.Equal(0, fired); // 100 per window at most, below 300
    }

    [Fact]
    public void ASpikeReArmsAfterAQuietWindow_ButNotWhileItKeepsGoing()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);
        Assert.Equal(1, Fire(tracker, Kind, 5, 400, 1000, 16));

        now += ClientRejectionRateTracker.WindowMs; // the spike carries on into the next window: the same spike
        Assert.Equal(0, Fire(tracker, Kind, 5, 400, 1000, 16));

        now += ClientRejectionRateTracker.WindowMs * 3; // a quiet stretch with nothing sent
        Assert.Equal(1, Fire(tracker, Kind, 5, 400, 1000, 16)); // a new spike
    }

    [Fact]
    public void ASpikeReArmsAfterAWindowBelowTheThreshold()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);
        Assert.Equal(1, Fire(tracker, Kind, 5, 400, 1000, 16));

        now += ClientRejectionRateTracker.WindowMs; // next window: a trickle
        Assert.Equal(0, Fire(tracker, Kind, 5, 10, 1000, 16));
        now += ClientRejectionRateTracker.WindowMs; // the quiet window has ended: re-armed
        Assert.Equal(1, Fire(tracker, Kind, 5, 400, 1000, 16));
    }

    [Fact]
    public void ReasonsAreIndependent_AndAnOutOfRangeReasonIsIgnored()
    {
        long now = 0;
        var tracker = new ClientRejectionRateTracker(() => now);

        Assert.Equal(1, Fire(tracker, Kind, 5, 400, 1000, 16));
        Assert.Equal(1, Fire(tracker, Kind, 6, 400, 1000, 16));
        Assert.Equal(1, Fire(tracker, ClientRejectionKind.SellFailed, 5, 400, 1000, 16));
        Assert.Equal(0, Fire(tracker, Kind, 4096, 400, 1000, 16));
    }
}
