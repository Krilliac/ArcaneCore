using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Gateway;

/// <summary>A transient accept failure must not end the worker's gateway-link accept loop (audit cid 33).</summary>
public sealed class GatewaySessionLinkHostAcceptTests
{
    private const string Secret = "link-secret-0123456789";

    [Fact]
    public async Task TransientAcceptError_DoesNotStopTheAcceptLoop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = new GatewaySessionLinkHost(
            GatewayLinkManifest.Create(ClusterRole.Worker, "worker", Guid.NewGuid(), 1, "dev", 1, 1),
            new RefusingAcceptor(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0));
        int calls = 0;
        host.BeforeAcceptForTests = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            return ValueTask.CompletedTask;
        };
        await using (host)
        {
            host.Start();
            var client = new GatewaySessionLinkClient(
                GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway-a", Guid.NewGuid(), 1, "dev", 1, 1), host.BoundEndPoint, Secret);
            await using (client)
            {
                await client.ConnectAsync(cts.Token);
                Assert.True(client.IsConnected);
                while (host.AdmittedLinkCount != 1)
                {
                    await Task.Delay(10, cts.Token);
                }

                Assert.True(calls >= 2);
            }
        }
    }

    private sealed class RefusingAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(ArcaneCore.Protocol.AuthResponseCode.Failed, "not used"));
    }
}
