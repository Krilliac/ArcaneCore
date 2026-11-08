using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>World:Playerbots:Summon</c> (<see cref="PlayerbotSummonOptions"/>, docs/areas/playbots-groups.md, Summons): the defaults (the non-vanilla meeting-stone
/// summon is off), the ranges, and the section's place in <see cref="PlayerbotOptions"/>. The keys' registration in <c>WorldConfigKeys</c> is checked by
/// <c>ConfigReloadTests</c>.
/// </summary>
public sealed class PlayerbotSummonOptionsTests
{
    [Fact]
    public void TheDefaults_HaveTheRitualOn_AndTheMeetingStoneOff()
    {
        var o = new PlayerbotSummonOptions();
        Assert.True(o.Enabled);
        Assert.False(o.MeetingStones);
        Assert.Equal(200f, o.FarYards);
        Assert.Equal(120, o.RetrySeconds);
        Assert.Equal(40f, o.HelperYards);
        Assert.True(o.AnnounceToPlayers);
        o.Validate();
    }

    [Fact]
    public void TheSection_HasExactlyTheDocumentedKeys()
    {
        string[] actual = [.. typeof(PlayerbotSummonOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }).Select(p => p.Name).Order()];
        Assert.Equal(new[] { "AnnounceToPlayers", "Enabled", "FarYards", "HelperYards", "MeetingStones", "RetrySeconds" }, actual);
    }

    [Theory]
    [InlineData(50f, 5000f, true)]
    [InlineData(49f, 5000f, false)]
    [InlineData(50f, 5001f, false)]
    public void FarYards_AcceptsItsBounds_AndRefusesJustOutsideThem(float low, float high, bool valid)
    {
        Assert.Equal(valid, PlayerbotSummonOptions.CheckFarYards(low) is null && PlayerbotSummonOptions.CheckFarYards(high) is null);
        Assert.NotNull(PlayerbotSummonOptions.CheckFarYards(float.NaN));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(3600, true)]
    [InlineData(9, false)]
    [InlineData(3601, false)]
    public void RetrySeconds_HasItsRange(int value, bool valid) => Assert.Equal(valid, PlayerbotSummonOptions.CheckRetrySeconds(value) is null);

    [Theory]
    [InlineData(5f, true)]
    [InlineData(100f, true)]
    [InlineData(4.9f, false)]
    [InlineData(100.1f, false)]
    public void HelperYards_HasItsRange(float value, bool valid) => Assert.Equal(valid, PlayerbotSummonOptions.CheckHelperYards(value) is null);

    [Fact]
    public void ValidateOfThePlayerbotSection_RefusesABadSummonKey_AndAMissingSection()
    {
        var o = new PlayerbotOptions();
        o.Validate();
        o.Summon.RetrySeconds = 1;
        Assert.Contains("RetrySeconds", Assert.Throws<InvalidOperationException>(o.Validate).Message, StringComparison.Ordinal);
        o.Summon = null!;
        Assert.Contains("Summon", Assert.Throws<InvalidOperationException>(o.Validate).Message, StringComparison.Ordinal);
    }
}
