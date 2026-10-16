using ArcaneCore.World.Cluster.Tooling;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// A World with no cluster nodes running used to retry every node on every 5 s pass, each attempt ending in a cancelled connect
/// (a steady stream of first-chance OperationCanceledExceptions under the debugger). Unanswered nodes now back off.
/// </summary>
public sealed class ClusterNodeBridgeBackoffTests
{
    [Fact]
    public void ConnectBackoff_IsZeroBeforeAnyFailure_ThenDoublesFromFiveSeconds()
    {
        Assert.Equal(TimeSpan.Zero, ClusterNodeBridgeFeature.ConnectBackoff(0));
        Assert.Equal(TimeSpan.FromSeconds(5), ClusterNodeBridgeFeature.ConnectBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(10), ClusterNodeBridgeFeature.ConnectBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(80), ClusterNodeBridgeFeature.ConnectBackoff(5));
    }

    [Fact]
    public void ConnectBackoff_IsCappedAtMaxBackoff_EvenAfterManyFailures()
    {
        Assert.Equal(ClusterNodeBridgeFeature.MaxConnectBackoff, ClusterNodeBridgeFeature.ConnectBackoff(7));
        Assert.Equal(ClusterNodeBridgeFeature.MaxConnectBackoff, ClusterNodeBridgeFeature.ConnectBackoff(int.MaxValue));
    }
}
