using System.Net;
using System.Net.Sockets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Security finding S2 (Codex security scan 2026-10-08): with the shipped World:WriterDrainGrace of 00:00:00 a closing
/// session awaited its writer with no bound, so a client that stopped reading (zero TCP window) parked the writer inside
/// WriteAsync and held the socket, the DI scope and every queued frame forever. vmangos never waits: WorldSocket::CloseSocket
/// shuts the socket down and closes it at once (AsyncSocket_windows.cpp:318-329), abandoning its send queue. A zero grace
/// now means the built-in <see cref="WorldSessionOptions.DefaultWriterDrainBound"/>; after it the write is cancelled and the
/// stream disposed.
/// </summary>
public sealed class WriterTeardownBoundTests
{
    [Fact]
    public async Task ARealClientThatStopsReading_WithTheShippedZeroGrace_IsStillTornDown()
    {
        await using var host = WorldTestHost.Start();
        var options = new WorldSessionOptions { MaxOutboundBytes = 64 * 1024 };
        Assert.Equal(TimeSpan.Zero, options.WriterDrainGrace);

        (Task run, WorldSession session, TcpClient client) = await StartOverLoopbackAsync(host, options);
        using (client)
        {
            await StallTheWriterUntilKickedAsync(session);

            // The default bound is a few seconds; on the unbounded base this never completed.
            await run.WaitAsync(WorldSessionOptions.DefaultWriterDrainBound + TimeSpan.FromSeconds(10));
            Assert.Equal(SessionState.Closed, session.State);
        }
    }

    /// <summary>
    /// A real loopback connection with tiny socket buffers whose client never reads, so the server's writer blocks once a
    /// few kilobytes are in flight. Returns the running session and the client socket (which must stay open: closing it
    /// would fail the pending write and hide the stall).
    /// </summary>
    private static async Task<(Task Run, WorldSession Session, TcpClient Client)> StartOverLoopbackAsync(
        WorldTestHost host, WorldSessionOptions options)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient { ReceiveBufferSize = 1024 };
            Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            TcpClient server = await listener.AcceptTcpClientAsync();
            await connect;
            server.SendBufferSize = 1024;
            NetworkStream stream = server.GetStream();
            var session = new WorldSession(
                stream, "127.0.0.1:50000", host.WorldServices, host.Opcodes, host.World, host.Registry, options, NullLogger.Instance);
            Task run = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(CancellationToken.None);
                }
                finally
                {
                    server.Dispose();
                }
            });
            return (run, session, client);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Queue frames until the outbound cap kicks the session: the cap is only exceeded once the writer stopped keeping up,
    /// so at the kick the writer is parked in a write the client will never read.
    /// </summary>
    private static async Task StallTheWriterUntilKickedAsync(WorldSession session)
    {
        byte[] payload = new byte[4096];
        Array.Fill(payload, (byte)'x');
        payload[^1] = 0; // valid SMSG_NOTIFICATION CString with the same queue pressure
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (session.State != SessionState.Closed)
        {
            Assert.True(DateTime.UtcNow < until, "the outbound cap never kicked the session");
            session.Send(WorldOpcode.SmsgNotification, payload);
            await Task.Delay(1);
        }
    }
}
