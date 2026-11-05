using System.Diagnostics.Metrics;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>The run instruments' labels and tally slots (<see cref="PlayerbotRunMeters"/>).</summary>
public sealed class PlayerbotRunMetersTests
{
    [Theory]
    [InlineData(true, null, PlayerbotRunMeters.Completed)]
    [InlineData(false, "travel-timeout", PlayerbotRunMeters.Timeout)]
    [InlineData(false, "no-path-to-entrance", PlayerbotRunMeters.GivenUp)]
    [InlineData(false, null, PlayerbotRunMeters.GivenUp)]
    public void EveryOutcomeOfARun_HasADistinctTallySlotInsideTheTally(bool complete, string? reason, string expected)
    {
        string outcome = PlayerbotRunMeters.OutcomeOf(complete, reason);
        Assert.Equal(expected, outcome);
        int slot = PlayerbotRunMeters.IndexOf(outcome);
        Assert.InRange(slot, 0, PlayerbotRunMeters.OutcomeSlots - 1);
    }

    [Fact]
    public void TheThreeLabels_TakeThreeDistinctSlots_AndAnUnknownLabelTakesNone_WithoutThrowing()
    {
        int[] slots = [PlayerbotRunMeters.IndexOf(PlayerbotRunMeters.Completed), PlayerbotRunMeters.IndexOf(PlayerbotRunMeters.GivenUp), PlayerbotRunMeters.IndexOf(PlayerbotRunMeters.Timeout)];
        Assert.Equal(PlayerbotRunMeters.OutcomeSlots, slots.Distinct().Count());
        Assert.Equal(-1, PlayerbotRunMeters.IndexOf("not-a-label"));
    }

    [Fact]
    public void Count_AddsOneMeasurementTaggedWithTheOutcome_ToTheRunsCounter()
    {
        // The meter is process-wide and other tests count runs too: a unique outcome label (not one of the closed three, which no coordinator emits) isolates this test's measurement.
        const string label = "meter-test-only-label";
        var seen = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, PlayerbotRunMeters.Runs)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "outcome" && Equals(tag.Value, label)) lock (seen) seen.Add(value);
        });
        listener.Start();

        PlayerbotRunMeters.Count(label);

        lock (seen) Assert.Equal([1L], seen);
    }
}
