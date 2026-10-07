using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Loot;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class StartingZoneLootApproachTests
{
    private static readonly ObjectGuid Corpse = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79994);

    [Fact]
    public async Task KnownCorpseWithinRange_EmitsMonotonicBoundedMoveStops()
    {
        await using TestPipe pipe = await TestPipe.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = new ScenarioConnection(pipe.Client);
        await SeedStopAsync(pipe, connection, Corpse, 10, 0, 0, deadline.Token);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 16, byteLimit: 4096);

        await StartingZoneLootApproach.ApproachAsync(connection, budget, Corpse.Value, (0, 0, 0), 100, deadline.Token);

        Assert.Equal(0, budget.Frames); // Quiet checks do not consume absent server frames.
        uint previousTime = 100;
        for (int index = 0; index < 2; index++)
        {
            (ushort opcode, byte[] body) = await ReadClientFrameAsync(pipe.Server, deadline.Token);
            Assert.Equal((ushort)WorldOpcode.MsgMoveStop, opcode);
            Assert.Equal(28, body.Length);
            uint clientTime = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            Assert.True(clientTime > previousTime);
            previousTime = clientTime;
            float x = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(8));
            float y = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(12));
            float z = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(16));
            Assert.Equal((index + 1) * 5f, x);
            Assert.Equal(0f, y);
            Assert.Equal(0f, z);
            Assert.True(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z));
            Assert.InRange(MathF.Abs(x - (index == 0 ? 0f : 5f)), 0f, 7f);
        }
    }

    [Fact]
    public async Task UnknownOrFarCorpse_RefusesWithoutClientMutation()
    {
        await using TestPipe unknown = await TestPipe.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var unknownConnection = new ScenarioConnection(unknown.Client);
        await Assert.ThrowsAsync<MockProtocolException>(() => StartingZoneLootApproach.ApproachAsync(
            unknownConnection, new StartingZoneCombat.CombatBudget(), Corpse.Value, (0, 0, 0), 100, deadline.Token));
        Assert.False(unknown.Server.DataAvailable);

        await using TestPipe far = await TestPipe.OpenAsync();
        var farConnection = new ScenarioConnection(far.Client);
        await SeedStopAsync(far, farConnection, Corpse, 26, 0, 0, deadline.Token);
        await Assert.ThrowsAsync<MockProtocolException>(() => StartingZoneLootApproach.ApproachAsync(
            farConnection, new StartingZoneCombat.CombatBudget(), Corpse.Value, (0, 0, 0), 100, deadline.Token));
        Assert.False(far.Server.DataAvailable);
    }

    [Fact]
    public async Task CorpseWithinThreeYards_RequiresNoMovementPacket()
    {
        await using TestPipe pipe = await TestPipe.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = new ScenarioConnection(pipe.Client);
        await SeedStopAsync(pipe, connection, Corpse, 2, 0, 0, deadline.Token);

        await StartingZoneLootApproach.ApproachAsync(connection, new StartingZoneCombat.CombatBudget(), Corpse.Value,
            (0, 0, 0), 100, deadline.Token);

        Assert.False(pipe.Server.DataAvailable);
    }

    private static async Task SeedStopAsync(TestPipe pipe, ScenarioConnection connection, ObjectGuid guid,
        float x, float y, float z, CancellationToken token)
    {
        await pipe.Server.WriteAsync(Frame(WorldOpcode.SmsgMonsterMove,
            CreatureMovePackets.BuildStop(guid, x, y, z, 1)), token);
        _ = await connection.ReadAsync(token);
    }

    [Fact]
    public async Task LootOpen_ReportsExplicitRefusalWithoutWaitingForDeadline()
    {
        await using TestPipe pipe = await TestPipe.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = new ScenarioConnection(pipe.Client);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 2, byteLimit: 128);
        await pipe.Server.WriteAsync(Frame(WorldOpcode.SmsgLootReleaseResponse,
            LootPackets.ReleaseResponse(Corpse)), deadline.Token);

        MockProtocolException error = await Assert.ThrowsAsync<MockProtocolException>(() =>
            StartingZoneLoot.OpenAsync(connection, budget, Corpse.Value, deadline.Token));
        Assert.Contains("refused", error.Message);
        Assert.Equal(1, budget.Frames);
        (ushort opcode, byte[] body) = await ReadClientFrameAsync(pipe.Server, deadline.Token);
        Assert.Equal((ushort)WorldOpcode.CmsgLoot, opcode);
        Assert.Equal(Corpse.Value, BinaryPrimitives.ReadUInt64LittleEndian(body));
    }

    private static byte[] Frame(WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)(payload.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)opcode);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static async Task<(ushort Opcode, byte[] Body)> ReadClientFrameAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[6];
        await ReadExactAsync(stream, header, token);
        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header);
        ushort opcode = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2)));
        byte[] body = new byte[size - 4];
        await ReadExactAsync(stream, body, token);
        return (opcode, body);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read == 0) throw new EndOfStreamException();
            buffer = buffer[read..];
        }
    }

    private sealed class TestPipe(WorldClient client, TcpClient server, TcpListener listener) : IAsyncDisposable
    {
        public WorldClient Client { get; } = client;
        public NetworkStream Server { get; } = server.GetStream();

        public static async Task<TestPipe> OpenAsync()
        {
            using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync(setup.Token).AsTask();
            WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, setup.Token);
            TcpClient server = await accepted;
            return new TestPipe(client, server, listener);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Server.Close();
            Server.Dispose();
            server.Dispose();
            listener.Stop();
        }
    }
}
