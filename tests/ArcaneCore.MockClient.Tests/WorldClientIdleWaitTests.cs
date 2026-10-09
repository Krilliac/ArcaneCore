using System.Net;
using System.Net.Sockets;
using ArcaneCore.MockClient.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// A bounded <see cref="WorldClient.ReadAsync"/> gives up after five seconds and closes the
/// connection, which is wrong for a reader that is legitimately quiet for longer (a held
/// settlement under load). <see cref="WorldClient.WaitForTrafficAsync"/> waits out the silence
/// without consuming anything, so the following read still sees the whole frame.
/// </summary>
public sealed class WorldClientIdleWaitTests
{
    [Fact]
    public async Task WaitForTraffic_OutlastsTheReadDeadline_AndLeavesTheFrameIntact()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Task<TcpClient> acceptedWaiting = listener.AcceptTcpClientAsync();
        await using WorldClient waiting = await WorldClient.ConnectAsync(endpoint);
        using TcpClient serverOfWaiting = await acceptedWaiting;

        Task<TcpClient> acceptedBounded = listener.AcceptTcpClientAsync();
        await using WorldClient bounded = await WorldClient.ConnectAsync(endpoint);
        bounded.OperationTimeout = ProtocolIO.DefaultOperationTimeout; // the tool's deadline (this suite raises the shared one)
        using TcpClient serverOfBounded = await acceptedBounded;

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task wait = waiting.WaitForTrafficAsync(guard.Token);
        Task<WorldFrame> boundedRead = bounded.ReadAsync(guard.Token);

        // Longer than one bounded read survives: the plain read has timed out, the wait has not.
        await Task.Delay(bounded.OperationTimeout + TimeSpan.FromSeconds(1), guard.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => boundedRead);
        Assert.False(wait.IsCompleted, "the idle wait must not be bound by the per-read deadline");

        // Unencrypted header: u16 big-endian size (opcode + body), u16 little-endian opcode.
        byte[] frame = [0x00, 0x05, 0x34, 0x12, 0xAA, 0xBB, 0xCC];
        await serverOfWaiting.GetStream().WriteAsync(frame, guard.Token);
        await wait.WaitAsync(TimeSpan.FromSeconds(10), guard.Token);

        WorldFrame read = await waiting.ReadAsync(guard.Token);
        Assert.Equal((ushort)0x1234, read.Opcode);
        Assert.Equal([0xAA, 0xBB, 0xCC], read.Payload);
    }

    [Fact]
    public async Task WaitForTraffic_WakesWhenTheServerClosesTheConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        TcpClient server = await accepted;

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task wait = client.WaitForTrafficAsync(guard.Token);
        Assert.False(wait.IsCompleted);

        server.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(10), guard.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadAsync(guard.Token));
    }
}
