using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The stripes that keep concurrent test processes' loopback ports apart (ArcaneCore.TestSupport.PortStripeClaim). Each test uses its own lock directory,
/// so it neither sees nor disturbs the stripes the running test process itself holds for LoopbackPorts.
/// </summary>
public sealed class PortStripeClaimTests
{
    [Fact]
    public void TwoClaimantsPreferringTheSameStripe_GetDisjointPortRanges()
    {
        using var directory = new TempDirectory("stripes-");
        using PortStripeClaim? first = PortStripeClaim.TryClaim(directory.Path, 3);
        using PortStripeClaim? second = PortStripeClaim.TryClaim(directory.Path, 3);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(3, first.Index);
        Assert.NotEqual(first.Index, second.Index);
        Assert.True(first.FirstPort + PortStripeClaim.StripeSize <= second.FirstPort || second.FirstPort + PortStripeClaim.StripeSize <= first.FirstPort, "the stripes overlap");
        Assert.InRange(first.FirstPort, PortStripeClaim.RangeStart, PortStripeClaim.RangeStart + (PortStripeClaim.StripeCount * PortStripeClaim.StripeSize) - PortStripeClaim.StripeSize);
        Assert.InRange(second.FirstPort, PortStripeClaim.RangeStart, PortStripeClaim.RangeStart + (PortStripeClaim.StripeCount * PortStripeClaim.StripeSize) - PortStripeClaim.StripeSize);
    }

    [Fact]
    public void WithEveryStripeHeld_AClaimantGetsNothing_AndAReleasedStripeIsClaimableAgain()
    {
        using var directory = new TempDirectory("stripes-");
        var held = new List<PortStripeClaim>();
        try
        {
            for (int i = 0; i < PortStripeClaim.StripeCount; i++)
            {
                PortStripeClaim? claim = PortStripeClaim.TryClaim(directory.Path, 7);
                Assert.NotNull(claim);
                held.Add(claim);
            }

            Assert.Equal(PortStripeClaim.StripeCount, held.Select(c => c.Index).Distinct().Count());
            Assert.Null(PortStripeClaim.TryClaim(directory.Path, 0));

            int released = held[5].Index;
            held[5].Dispose();
            using PortStripeClaim? again = PortStripeClaim.TryClaim(directory.Path, 0);
            Assert.NotNull(again);
            Assert.Equal(released, again.Index);
        }
        finally
        {
            foreach (PortStripeClaim claim in held)
            {
                claim.Dispose();
            }
        }
    }
}
