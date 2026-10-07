using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Real loopback TCP coverage for the shared combat frame and byte budgets.</summary>
public sealed class StartingZoneCombatBudgetTests
{
    [Fact]
    public async Task DefaultBudget_AllowsSixHundredSmallMovementFrames()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        var connection = new ScenarioConnection(client);
        var budget = new StartingZoneCombat.CombatBudget();
        byte[] frames = Concat(Enumerable.Range(0, 600).Select(i => Frame((ushort)(0x1200 + (i % 8)), [(byte)i])));
        await server.GetStream().WriteAsync(frames, deadline.Token);

        for (int i = 0; i < 600; i++)
            _ = await budget.ReadAsync(connection, deadline.Token);

        Assert.Equal(600, budget.Frames);
        Assert.Equal(600 * 5, budget.Bytes);
    }

    [Fact]
    public async Task QuietDrain_UsesSharedBudgetAndCancellationLeavesNextFrameIntact()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        var connection = new ScenarioConnection(client);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 2, byteLimit: 10);
        await server.GetStream().WriteAsync(Frame(0x1201, [1]), deadline.Token);
        await budget.DrainQuietAsync(connection, deadline.Token);
        Assert.Equal(1, budget.Frames);
        Assert.Equal(5, budget.Bytes);

        await server.GetStream().WriteAsync(Frame(0x1202, [2]), deadline.Token);
        WorldFrame next = await budget.ReadAsync(connection, deadline.Token);
        Assert.Equal((ushort)0x1202, next.Opcode);
        Assert.Equal(2, next.Payload[0]);
        Assert.Equal(2, budget.Frames);
        Assert.Equal(10, budget.Bytes);
    }

    [Fact]
    public async Task ExactFrameAndByteBounds_RefuseReadAndQuietDrainPaths()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        var connection = new ScenarioConnection(client);
        var frameBound = new StartingZoneCombat.CombatBudget(frameLimit: 1, byteLimit: 100);
        await server.GetStream().WriteAsync(Concat([Frame(0x1210, [1]), Frame(0x1211, [2])]), deadline.Token);
        _ = await frameBound.ReadAsync(connection, deadline.Token);
        await Assert.ThrowsAsync<MockProtocolException>(() => frameBound.ReadAsync(connection, deadline.Token));

        var byteBound = new StartingZoneCombat.CombatBudget(frameLimit: 10, byteLimit: 5);
        await server.GetStream().WriteAsync(Concat([Frame(0x1212, [3]), Frame(0x1213, [4])]), deadline.Token);
        await Assert.ThrowsAsync<MockProtocolException>(() => byteBound.DrainQuietAsync(connection, deadline.Token));
        // The frame-limited read left its second frame intact; the drain consumes it
        // and then refuses the next frame when their combined bytes exceed five.
        Assert.Equal(2, byteBound.Frames);
        Assert.Equal(10, byteBound.Bytes);
    }

    private static byte[] Frame(ushort opcode, byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), opcode);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static byte[] Concat(IEnumerable<byte[]> frames)
    {
        byte[] result = frames.SelectMany(static f => f).ToArray();
        return result;
    }
}
