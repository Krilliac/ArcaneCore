using ArcaneCore.Game;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

public sealed class PlayerbotUnreachableTargetsTests
{
    private static ObjectGuid Guid(ulong n) => new(n);

    [Fact]
    public void AMarkedTarget_IsSkippedUntilItExpires_ThenForgotten()
    {
        var targets = new PlayerbotUnreachableTargets();
        targets.Mark(Guid(1), 1000, 500);
        Assert.True(targets.Has(Guid(1), 1000));
        Assert.True(targets.Has(Guid(1), 1499));
        Assert.False(targets.Has(Guid(1), 1500));
        Assert.Equal(0, targets.Count);
        Assert.False(targets.Has(Guid(2), 1000));
    }

    [Fact]
    public void MarkingAgain_MovesTheEnd_AndClearForgetsAll()
    {
        var targets = new PlayerbotUnreachableTargets();
        targets.Mark(Guid(1), 0, 100);
        targets.Mark(Guid(1), 50, 100);
        Assert.True(targets.Has(Guid(1), 149));
        Assert.Equal(1, targets.Count);
        targets.Clear();
        Assert.False(targets.Has(Guid(1), 149));
    }

    [Fact]
    public void WhenFull_TheSoonestExpiringEntryGoes_AndTheNewOneIsKept()
    {
        var targets = new PlayerbotUnreachableTargets();
        for (ulong i = 1; i <= PlayerbotUnreachableTargets.Capacity; i++)
            targets.Mark(Guid(i), 0, (uint)(1000 + (i * 10)));
        targets.Mark(Guid(1000), 0, 5000);

        Assert.Equal(PlayerbotUnreachableTargets.Capacity, targets.Count);
        Assert.False(targets.Has(Guid(1), 1)); // expired soonest (1010)
        Assert.True(targets.Has(Guid(2), 1));
        Assert.True(targets.Has(Guid(1000), 1));

        // Re-marking one already held never evicts.
        targets.Mark(Guid(2), 0, 9000);
        Assert.Equal(PlayerbotUnreachableTargets.Capacity, targets.Count);
        Assert.True(targets.Has(Guid(3), 1));
    }

    [Fact]
    public void TheClockWrapping_DoesNotMakeAMarkExpireEarlyOrLate()
    {
        var targets = new PlayerbotUnreachableTargets();
        uint now = uint.MaxValue - 100;
        targets.Mark(Guid(1), now, 300); // ends at 199 after the wrap
        Assert.True(targets.Has(Guid(1), uint.MaxValue));
        Assert.True(targets.Has(Guid(1), 0));
        Assert.True(targets.Has(Guid(1), 198));
        Assert.False(targets.Has(Guid(1), 199));
    }

    [Fact]
    public void Eviction_ComparesAcrossTheWrap()
    {
        var targets = new PlayerbotUnreachableTargets();
        uint now = uint.MaxValue - 10;
        targets.Mark(Guid(1), now, 100); // ends after the wrap: later than the others
        for (ulong i = 2; i <= PlayerbotUnreachableTargets.Capacity; i++)
            targets.Mark(Guid(i), now, 20); // ends before the wrap
        targets.Mark(Guid(500), now, 50);

        Assert.True(targets.Has(Guid(1), now));
        Assert.True(targets.Has(Guid(500), now));
        Assert.Equal(PlayerbotUnreachableTargets.Capacity, targets.Count);
    }
}
