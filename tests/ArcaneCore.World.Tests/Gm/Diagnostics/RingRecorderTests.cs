using ArcaneCore.World.Gm.Diagnostics;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Diagnostics;

public sealed class RingRecorderTests
{
    [Fact]
    public void WhenNothingIsEnabled_RecordIsANoOp_AndTheRecorderIsInactive()
    {
        var ring = new RingRecorder<string, int>();

        ring.Record("a", 1);

        Assert.False(ring.IsActive);
        Assert.False(ring.IsRecording("a"));
        Assert.Empty(ring.Snapshot("a"));
        Assert.Equal(0, ring.TargetCount);
    }

    [Fact]
    public void RecordsOnlyEnabledTargets_OldestFirst()
    {
        var ring = new RingRecorder<string, int>();
        Assert.True(ring.Enable("a", 4));

        ring.Record("a", 1);
        ring.Record("a", 2);
        ring.Record("b", 99);

        Assert.True(ring.IsActive);
        Assert.Equal([1, 2], ring.Snapshot("a"));
        Assert.Empty(ring.Snapshot("b"));
    }

    [Fact]
    public void WhenFull_TheOldestEntriesAreOverwritten_AndCounted()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 3);

        for (int i = 1; i <= 5; i++)
        {
            ring.Record("a", i);
        }

        Assert.Equal([3, 4, 5], ring.Snapshot("a"));
        Assert.Equal(2, ring.Overwritten("a"));
    }

    [Fact]
    public void Disable_FreesTheRing_AndTheLastDisableTurnsTheRecorderOff()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 2);
        ring.Enable("b", 2);

        Assert.True(ring.Disable("a"));
        Assert.True(ring.IsActive);
        Assert.False(ring.Disable("a"));
        Assert.True(ring.Disable("b"));

        Assert.False(ring.IsActive);
        Assert.Empty(ring.Snapshot("a"));
    }

    [Fact]
    public void Enable_IsRefusedPastMaxTargets_ButAnEnabledTargetCanStillBeResized()
    {
        var ring = new RingRecorder<int, int>(maxTargets: 2, maxCapacity: 8);
        Assert.True(ring.Enable(1, 4));
        Assert.True(ring.Enable(2, 4));

        Assert.False(ring.Enable(3, 4));
        Assert.True(ring.Enable(1, 2));
        Assert.Equal(2, ring.TargetCount);
    }

    [Fact]
    public void Capacity_IsClampedToMaxCapacity()
    {
        var ring = new RingRecorder<string, int>(maxTargets: 4, maxCapacity: 3);
        ring.Enable("a", 1000);

        for (int i = 0; i < 10; i++)
        {
            ring.Record("a", i);
        }

        Assert.Equal([7, 8, 9], ring.Snapshot("a"));
        Assert.Equal(("a", 3, 3), ring.Targets().Single());
    }

    [Fact]
    public void Resizing_KeepsTheNewestEntriesThatFit()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 4);
        for (int i = 1; i <= 4; i++)
        {
            ring.Record("a", i);
        }

        ring.Enable("a", 2);

        Assert.Equal([3, 4], ring.Snapshot("a"));
        ring.Record("a", 5);
        Assert.Equal([4, 5], ring.Snapshot("a"));
        ring.Enable("a", 5);
        ring.Record("a", 6);
        Assert.Equal([4, 5, 6], ring.Snapshot("a"));
    }

    [Fact]
    public void Clear_EmptiesTheRing_ButKeepsRecording()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 3);
        ring.Record("a", 1);

        Assert.True(ring.Clear("a"));
        Assert.False(ring.Clear("missing"));
        Assert.Empty(ring.Snapshot("a"));
        ring.Record("a", 2);
        Assert.Equal([2], ring.Snapshot("a"));
    }

    [Fact]
    public void DisableAll_TurnsTheRecorderOff()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 2);
        ring.Enable("b", 2);

        ring.DisableAll();

        Assert.False(ring.IsActive);
        Assert.Equal(0, ring.TargetCount);
    }

    [Fact]
    public async Task ConcurrentRecording_NeverExceedsCapacity_AndCountsTheOverwrites()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 16);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                ring.Record("a", (t * 1000) + i);
            }
        })));

        Assert.Equal(16, ring.Snapshot("a").Length);
        Assert.Equal(4000 - 16, ring.Overwritten("a"));
    }

    [Fact]
    public void Enable_RejectsANonPositiveCapacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RingRecorder<string, int>().Enable("a", 0));

    [Fact]
    public void Enable_RejectsANegativeCapacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RingRecorder<string, int>().Enable("a", -1));

    [Fact]
    public void TheCaps_MustBePositive_AndDefaultTo64Targets4096Entries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingRecorder<string, int>(maxTargets: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingRecorder<string, int>(maxCapacity: 0));
        var defaults = new RingRecorder<string, int>();
        Assert.Equal(64, defaults.MaxTargets);
        Assert.Equal(4096, defaults.MaxCapacity);
    }

    [Fact]
    public void ReEnablingWithTheSameCapacity_KeepsTheRecordedEntries()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 3);
        ring.Record("a", 1);
        ring.Record("a", 2);

        Assert.True(ring.Enable("a", 3));

        Assert.Equal([1, 2], ring.Snapshot("a"));
        Assert.Equal(1, ring.TargetCount);
    }

    [Fact]
    public void ADisabledTarget_StartsEmptyWhenEnabledAgain()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 2);
        ring.Record("a", 1);
        ring.Disable("a");

        ring.Enable("a", 2);

        Assert.Empty(ring.Snapshot("a"));
        Assert.Equal(0, ring.Overwritten("a"));
    }

    [Fact]
    public void Shrinking_CountsTheEntriesItDropped_AsOverwritten()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 4);
        for (int i = 1; i <= 6; i++)
        {
            ring.Record("a", i); // 1 and 2 already overwritten
        }

        ring.Enable("a", 2);

        Assert.Equal([5, 6], ring.Snapshot("a"));
        Assert.Equal(4, ring.Overwritten("a")); // 2 by wrapping + 2 by shrinking
    }

    [Fact]
    public void Clear_ResetsTheOverwrittenCount()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 1);
        ring.Record("a", 1);
        ring.Record("a", 2);
        Assert.Equal(1, ring.Overwritten("a"));

        ring.Clear("a");

        Assert.Equal(0, ring.Overwritten("a"));
        Assert.Equal(0, ring.Overwritten("never-enabled"));
    }

    [Fact]
    public void RecordingADisabledKey_WhileAnotherIsEnabled_NeverCreatesARing()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 2);

        ring.Record("b", 5);

        Assert.False(ring.IsRecording("b"));
        Assert.Equal(1, ring.TargetCount);
        Assert.Equal([("a", 2, 0)], ring.Targets());
    }

    [Fact]
    public void ACapacityOfOne_KeepsOnlyTheNewest()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 1);

        ring.Record("a", 1);
        ring.Record("a", 2);

        Assert.Equal([2], ring.Snapshot("a"));
    }

    [Fact]
    public void StructEntries_AreCopiedIn_NotAliased()
    {
        var ring = new RingRecorder<string, (int Id, string Tag)>();
        ring.Enable("a", 2);
        var entry = (Id: 1, Tag: "x");

        ring.Record("a", entry);
        entry.Id = 99;

        Assert.Equal([(1, "x")], ring.Snapshot("a"));
    }

    [Fact]
    public void ASnapshot_IsACopy_ThatLaterRecordsDoNotChange()
    {
        var ring = new RingRecorder<string, int>();
        ring.Enable("a", 3);
        ring.Record("a", 1);
        int[] taken = ring.Snapshot("a");

        ring.Record("a", 2);

        Assert.Equal([1], taken);
    }

    [Fact]
    public async Task ConcurrentEnableDisableAndRecord_StaysConsistent()
    {
        var ring = new RingRecorder<int, int>(maxTargets: 4, maxCapacity: 8);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                switch (i % 4)
                {
                    case 0:
                        ring.Enable(t, 8);
                        break;
                    case 1:
                        ring.Record(t, i);
                        break;
                    case 2:
                        _ = ring.Snapshot(t);
                        break;
                    default:
                        ring.Disable(t);
                        break;
                }
            }
        })));

        ring.DisableAll();
        Assert.False(ring.IsActive);
        Assert.Equal(0, ring.TargetCount);
    }
}
