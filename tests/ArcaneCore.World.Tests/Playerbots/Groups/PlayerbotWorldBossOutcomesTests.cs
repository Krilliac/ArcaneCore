using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The map from a group's Finish reason to the outcome label of <c>arcanecore.playerbot.worldboss_raids</c>
/// (<see cref="PlayerbotWorldBossOutcomes"/>, p7b design section 8): every reason the coordinator can end a group with has a label, a reason the
/// table does not know is <c>other</c> and never a new label, and the instruments carry the documented names.
/// </summary>
[Collection(WorldBossTestKit.MeterCollection)] // one test counts into the process-wide meter that the world tests read
public sealed class PlayerbotWorldBossOutcomesTests
{
    [Theory]
    [InlineData("killed", true, "killed")]
    [InlineData("wiped", true, "wiped")]
    [InlineData("boss-taken", true, "boss-taken")]
    [InlineData("boss-gone", true, "boss-gone")]
    [InlineData("objective-missing", true, "boss-gone")]
    [InlineData("not-enough", true, "not-enough")]
    [InlineData("too-few", false, "not-enough")]
    [InlineData("too-few", true, "not-enough")]
    [InlineData("gather-timeout", false, "timeout")]
    [InlineData("travel-timeout", true, "timeout")]
    [InlineData("regroup-timeout", true, "timeout")]
    [InlineData("timeout", true, "timeout")]
    [InlineData("stalled", true, "stalled")]
    [InlineData("no-path-to-entrance", true, "no-path")]
    [InlineData("no-path-to-boss", false, "no-path")]
    [InlineData("lead:no-path-leader", true, "no-path")]
    [InlineData("coordinator-detached", true, "other")]
    [InlineData("coordinator-detached", false, "other")]
    public void EveryFinishReason_HasItsDocumentedLabel(string reason, bool reachedTravelling, string expected)
        => Assert.Equal(expected, PlayerbotWorldBossOutcomes.Of(reason, reachedTravelling));

    /// <summary>The reasons that depend on whether the raid had left for the boss: a forming failure before Travelling, an abandonment after it.</summary>
    [Theory]
    [InlineData("invites-unanswered")]
    [InlineData("leader-gone")]
    [InlineData("leader-stopped")]
    [InlineData("leader-left")]
    public void ALostLeaderOrUnansweredInvites_IsAFormingFailureBeforeTravelling_AndAnAbandonmentAfter(string reason)
    {
        Assert.Equal("forming-failed", PlayerbotWorldBossOutcomes.Of(reason, reachedTravelling: false));
        Assert.Equal("abandoned", PlayerbotWorldBossOutcomes.Of(reason, reachedTravelling: true));
    }

    [Theory]
    [InlineData("a-reason-added-later")]
    [InlineData("")]
    [InlineData("KILLED")]   // the labels are exact: no case folding that could hide a typo at the call site
    [InlineData("disbanded")]
    public void AnUnknownReason_IsOther_NeverANewLabel(string reason)
    {
        Assert.Equal("other", PlayerbotWorldBossOutcomes.Of(reason, false));
        Assert.Equal("other", PlayerbotWorldBossOutcomes.Of(reason, true));
    }

    [Fact]
    public void ANullReason_IsOther()
        => Assert.Equal("other", PlayerbotWorldBossOutcomes.Of(null, true));

    [Fact]
    public void TheLabelSet_IsClosed_AndHoldsFormedAndMusterFailedToo()
    {
        string[] expected =
        [
            "formed", "killed", "wiped", "boss-taken", "boss-gone", "not-enough", "forming-failed", "abandoned", "timeout", "stalled", "no-path",
            "muster-failed", "other",
        ];
        Assert.Equal(expected.Order(), PlayerbotWorldBossOutcomes.All.Order());
        Assert.Equal("formed", PlayerbotWorldBossOutcomes.Formed);
        Assert.Equal("muster-failed", PlayerbotWorldBossOutcomes.MusterFailed);

        // Whatever the input, the label comes from the closed set.
        string[] inputs = ["killed", "wiped", "x", "", "leader-gone", "no-path-q", "lead:no-path-q", "gather-timeout", "siege-complete", "player-gone", "mentor-timeout"];
        foreach (string reason in inputs)
            foreach (bool travelled in new[] { false, true })
                Assert.Contains(PlayerbotWorldBossOutcomes.Of(reason, travelled), PlayerbotWorldBossOutcomes.All);
    }

    [Fact]
    public void TheInstruments_CarryTheDocumentedNames_UnderTheCollectedPrefix()
    {
        Assert.Equal("arcanecore.playerbot.worldboss_raids", PlayerbotWorldBossMeters.Raids.Name);
        Assert.Equal("arcanecore.playerbot.worldboss_active", PlayerbotWorldBossMeters.Active.Name);
        Assert.StartsWith(ArcaneMeters.Prefix, PlayerbotWorldBossMeters.Meter.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void CountingAnOutcome_ReachesAListener_WithTheOutcomeLabel()
    {
        var seen = new List<(long Value, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "ArcaneCore.Playerbots.WorldBosses" && instrument.Name == "arcanecore.playerbot.worldboss_raids") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags) if (tag.Key == "outcome") outcome = tag.Value as string;
            lock (seen) seen.Add((value, outcome));
        });
        listener.Start();

        PlayerbotWorldBossMeters.Count(PlayerbotWorldBossOutcomes.Killed);
        PlayerbotWorldBossMeters.Count(PlayerbotWorldBossOutcomes.Of("wiped", true));

        lock (seen) Assert.Equal([(1L, "killed"), (1L, "wiped")], seen);
    }
}
