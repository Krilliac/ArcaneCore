using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>The worker endpoint's stop path must stop the acceptor even when the link host's disposal throws, and the reverse failure must still surface.</summary>
public sealed class GatewayWorkerEndpointStopTests
{
    [Fact]
    public async Task StopInOrder_WhenHostDisposeThrows_StillStopsAcceptor_AndRethrows()
    {
        bool acceptorStopped = false;

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GatewayWorkerEndpoint.StopInOrderAsync(
                () => throw new InvalidOperationException("host dispose failed"),
                () =>
                {
                    acceptorStopped = true;
                    return Task.CompletedTask;
                }));

        Assert.Equal("host dispose failed", ex.Message);
        Assert.True(acceptorStopped);
    }

    [Fact]
    public async Task StopInOrder_WhenAcceptorThrowsAfterHostStopped_Rethrows_AndHostWasStoppedFirst()
    {
        var order = new List<string>();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GatewayWorkerEndpoint.StopInOrderAsync(
                () =>
                {
                    order.Add("host");
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    order.Add("acceptor");
                    throw new InvalidOperationException("acceptor stop failed");
                }));

        Assert.Equal("acceptor stop failed", ex.Message);
        Assert.Equal(["host", "acceptor"], order);
    }

    [Fact]
    public async Task StopInOrder_WhenBothThrow_KeepsBothExceptions()
    {
        AggregateException ex = await Assert.ThrowsAsync<AggregateException>(() =>
            GatewayWorkerEndpoint.StopInOrderAsync(
                () => throw new InvalidOperationException("host dispose failed"),
                () => throw new TimeoutException("acceptor stop failed")));

        Assert.Collection(
            ex.InnerExceptions,
            host => Assert.Equal("host dispose failed", host.Message),
            acceptor => Assert.Equal("acceptor stop failed", acceptor.Message));
    }

    [Fact]
    public async Task StopInOrder_WithNothingToStop_Completes()
    {
        await GatewayWorkerEndpoint.StopInOrderAsync(null, null);
    }
}
