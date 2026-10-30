using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>The exit-code contract the cluster host supervisor classifies a child's exit by (docs/ops/watchdog.md).</summary>
public sealed class ExitCodesTests
{
    [Fact]
    public void ExitCodes_Contract_IsPinned()
    {
        Assert.Equal(0, ExitCodes.Success);
        Assert.Equal(1, ExitCodes.Failure);
        Assert.Equal(2, ExitCodes.Restart);
        Assert.Equal(64, ExitCodes.Usage);
        Assert.Equal(70, ExitCodes.UnhandledException);
        Assert.Equal(78, ExitCodes.InvalidConfiguration);
        Assert.Equal(125, ExitCodes.MaxRequested);
    }
}
