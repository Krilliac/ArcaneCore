using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class LogoutTrafficTests
{
    [Fact]
    public async Task LogoutRefusesExcessiveTrafficEvenBeforeTheDeadline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        byte[] packet = new byte[32768 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 32768 + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)WorldOpcode.SmsgPong);
        Task producer = Task.Run(async () =>
        {
            for (int i = 0; i < 33; i++)
                await server.GetStream().WriteAsync(packet, deadline.Token);
        }, deadline.Token);
        var connection = new ScenarioConnection(client);
        MockProtocolException error = await Assert.ThrowsAsync<MockProtocolException>(() =>
            LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutComplete, deadline.Token));
        Assert.Contains("one-megabyte", error.Message);
        await producer;
    }

    [Fact]
    public async Task NormalLogoutCompletionSurvivesMoreThan128NearbyWorldPackets()
    {
        // A populated world continues publishing traffic during its normal 20-second logout delay.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        byte[] packets = new byte[(130 + 1) * 4];
        for (int i = 0; i < 131; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packets.AsSpan(i * 4), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(packets.AsSpan(i * 4 + 2),
                (ushort)(i == 130 ? WorldOpcode.SmsgLogoutComplete : WorldOpcode.SmsgPong));
        }
        await server.GetStream().WriteAsync(packets, deadline.Token);
        var connection = new ScenarioConnection(client);
        byte[] complete = await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutComplete, deadline.Token);
        Assert.Empty(complete);
        Assert.Equal(131, connection.FramesReceived);
    }
}
