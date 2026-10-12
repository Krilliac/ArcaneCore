using ArcaneCore.World.Clustering;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

public sealed class CharacterFencingOptionsTests
{
    [Fact]
    public void TakeoverRequiresBothCoordinatesAndEnabledAuthority()
    {
        new CharacterFencingOptions { Enabled = true }.Validate();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions
        {
            Enabled = true,
            TakeoverEpoch = 4,
        }.Validate());
        Assert.Contains("Cluster:Fencing", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions
        {
            Enabled = false,
            TakeoverEpoch = 4,
            TakeoverIncarnation = Guid.NewGuid(),
        }.Validate());
    }

    [Fact]
    public void OwnerNodeIsBounded()
    {
        Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions
        {
            OwnerNode = new string('x', 129),
        }.Validate());
    }

    [Fact]
    public void ClaimWatchIntervalDefaultsToTenSeconds_AndIsBounded()
    {
        var options = new CharacterFencingOptions();
        Assert.Equal(10, options.ClaimWatchIntervalSeconds);
        options.Validate();
        foreach (int ok in new[] { 1, 300 })
            new CharacterFencingOptions { ClaimWatchIntervalSeconds = ok }.Validate();
        foreach (int bad in new[] { 0, -1, 301 })
            Assert.Throws<InvalidOperationException>(() => new CharacterFencingOptions { ClaimWatchIntervalSeconds = bad }.Validate());
    }
}
