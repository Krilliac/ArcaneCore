using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// Net:Protection:World* (docs/ops/netguard.md): the per-opcode token buckets drop a packet over budget and keep the
/// connection; the per-second flood cap closes it. Driven through a real socket and an authenticated session.
/// </summary>
public sealed class PacketBudgetTests
{
    private const WorldOpcode Unhandled = (WorldOpcode)0x0001;

    private static async Task<(WorldTestClient Client, Task Session, CancellationTokenSource Stop, TcpListener Listener)> ConnectAsync(WorldTestHost host, NetGuard guard)
    {
        byte[] key = await host.AddAccountAsync("BUDGET");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stop = new CancellationTokenSource();
        Task session = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(stop.Token);
            await using NetworkStream stream = server.GetStream();
            await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
            var worldSession = new WorldSession(stream, "127.0.0.1:41000", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
                new WorldSessionOptions(), NullLogger.Instance, guard);
            await worldSession.RunAsync(stop.Token);
        });

        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var client = new WorldTestClient(socket);
        await client.AuthenticateAsync("BUDGET", key);
        await client.CollectAsync();
        return (client, session, stop, listener);
    }

    private static byte[] Ping(uint sequence)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sequence);
        return payload;
    }

    [Fact]
    public async Task APacketOverItsOpcodesBucket_IsDropped_AndTheConnectionStays()
    {
        await using var host = WorldTestHost.Start();
        var guard = new NetGuard(new NetProtectionOptions { WorldOpcodeBurst = 5, WorldOpcodeRefillPerSecond = 0, WorldPacketsPerSecond = 0, WorldFloodPacketsPerSecond = 0 },
            () => 0, () => 0, NullLogger.Instance);
        (WorldTestClient client, Task session, CancellationTokenSource stop, TcpListener listener) = await ConnectAsync(host, guard);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                await client.SendAsync(Unhandled, []);
            }

            // Pings are answered in any state and in order: once the pong is back, the eight packets were dispatched.
            await client.SendAsync(WorldOpcode.CmsgPing, Ping(7));
            await client.ReadUntilAsync(WorldOpcode.SmsgPong);
            Assert.Equal(3, guard.DroppedPackets);
            Assert.Equal(0, guard.FloodDisconnects);
            Assert.False(session.IsCompleted);
        }
        finally
        {
            await client.DisposeAsync();
            stop.Cancel();
            listener.Stop();
            await session.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task AFloodOverThePerSecondCap_ClosesTheConnection()
    {
        await using var host = WorldTestHost.Start();
        var guard = new NetGuard(new NetProtectionOptions { WorldOpcodeBurst = 0, WorldPacketsPerSecond = 0, WorldFloodPacketsPerSecond = 20 },
            () => 0, () => 0, NullLogger.Instance);
        (WorldTestClient client, Task session, CancellationTokenSource stop, TcpListener listener) = await ConnectAsync(host, guard);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    await client.SendAsync(Unhandled, []);
                }
                catch (IOException)
                {
                    break; // already closed
                }
            }

            Assert.True(await client.IsClosedByServerAsync());
            Assert.Equal(1, guard.FloodDisconnects);
        }
        finally
        {
            await client.DisposeAsync();
            stop.Cancel();
            listener.Stop();
            await session.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
