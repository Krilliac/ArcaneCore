using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

public sealed class ConnectionAdmissionTests
{
    [Fact]
    public void PerIpCap_RejectsTheNextConnectionAndReleaseFreesASlot()
    {
        var limiter = new ConnectionLimiter(maxConnections: 100, maxPerIp: 2);
        var ip = IPAddress.Parse("10.0.0.5");
        IDisposable? a = limiter.TryAcquire(ip);
        IDisposable? b = limiter.TryAcquire(ip);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(limiter.TryAcquire(ip));
        Assert.NotNull(limiter.TryAcquire(IPAddress.Parse("10.0.0.6")));

        a!.Dispose();
        a.Dispose(); // double dispose must not free a second slot
        Assert.NotNull(limiter.TryAcquire(ip));
        Assert.Null(limiter.TryAcquire(ip));
    }

    [Fact]
    public void GlobalCap_AndZeroMeansUnlimited()
    {
        var capped = new ConnectionLimiter(maxConnections: 2, maxPerIp: 0);
        Assert.NotNull(capped.TryAcquire(IPAddress.Parse("10.0.0.1")));
        Assert.NotNull(capped.TryAcquire(IPAddress.Parse("10.0.0.2")));
        Assert.Null(capped.TryAcquire(IPAddress.Parse("10.0.0.3")));

        var open = new ConnectionLimiter(maxConnections: 0, maxPerIp: 0);
        for (int i = 0; i < 500; i++)
        {
            Assert.NotNull(open.TryAcquire(IPAddress.Loopback));
        }
    }

    [Fact]
    public void Ipv4MappedAddress_SharesTheBucketWithTheIpv4Address()
    {
        var limiter = new ConnectionLimiter(maxConnections: 0, maxPerIp: 1);
        Assert.NotNull(limiter.TryAcquire(IPAddress.Parse("192.168.1.9")));
        Assert.Null(limiter.TryAcquire(IPAddress.Parse("192.168.1.9").MapToIPv6()));
    }

    [Fact]
    public async Task AcceptLoop_SurvivesSocketExceptionsAndKeepsAccepting()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int calls = 0;
        var accepted = new TaskCompletionSource<TcpClient>();
        using var stop = new CancellationTokenSource();

        ValueTask<TcpClient> Accept(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                throw new SocketException((int)SocketError.TooManyOpenSockets);
            }

            return listener.AcceptTcpClientAsync(ct);
        }

        Task loop = AcceptLoop.RunAsync(Accept, c => accepted.TrySetResult(c), NullLogger.Instance, stop.Token,
            backoff: TimeSpan.FromMilliseconds(10));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(calls >= 3);

        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }
}
