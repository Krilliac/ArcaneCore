using System.Net.Sockets;
using ArcaneCore.Game;
using ArcaneCore.Game.Loot;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class StartingZoneLootMoneyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForMoney_AcceptsWalletDeltaAndClearInEitherOrder(bool clearFirst)
    {
        await using TestPipe pipe = await TestPipe.OpenAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        try
        {
            const ulong player = 77;
            const uint before = 1000;
            const uint gold = 37;
            await pipe.Server.WriteAsync(UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before), deadline.Token);
            await pipe.Server.WriteAsync(clearFirst
                ? Frame(WorldOpcode.SmsgLootClearMoney, [])
                : UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before + gold), deadline.Token);
            if (clearFirst)
            {
                await pipe.Server.WriteAsync(UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before + gold), deadline.Token);
            }
            else
            {
                await pipe.Server.WriteAsync(Frame(WorldOpcode.SmsgLootClearMoney, []), deadline.Token);
            }

            var connection = new ScenarioConnection(pipe.Client);
            var budget = new StartingZoneCombat.CombatBudget(frameLimit: 8, byteLimit: 4096);
            uint result = await StartingZoneLoot.WaitForMoneyAsync(connection, player, before, gold, budget, deadline.Token);
            Assert.Equal(gold, result);
            Assert.Equal(before + gold, connection.FieldsOf(player)[UpdateFields.PlayerFieldCoinage]);
            Assert.Equal(3, budget.Frames);
        }
        finally
        {
            deadline.Cancel();
        }
    }

    [Fact]
    public async Task WaitForMoney_RejectsMalformedClearAndWrongOrMalformedNotify()
    {
        await using TestPipe pipe = await TestPipe.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const ulong player = 77;
        const uint before = 1000;
        const uint gold = 37;
        await pipe.Server.WriteAsync(UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before), deadline.Token);
        await pipe.Server.WriteAsync(Frame(WorldOpcode.SmsgLootClearMoney, [1]), deadline.Token);
        var connection = new ScenarioConnection(pipe.Client);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 4096);
        await Assert.ThrowsAsync<MockProtocolException>(() => StartingZoneLoot.WaitForMoneyAsync(connection, player, before, gold, budget, deadline.Token));

        await using TestPipe wrongPipe = await TestPipe.OpenAsync();
        await wrongPipe.Server.WriteAsync(UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before), deadline.Token);
        await wrongPipe.Server.WriteAsync(Frame(WorldOpcode.SmsgLootMoneyNotify, LootPackets.MoneyNotify(gold + 1)), deadline.Token);
        var wrongConnection = new ScenarioConnection(wrongPipe.Client);
        await Assert.ThrowsAsync<MockProtocolException>(() => StartingZoneLoot.WaitForMoneyAsync(wrongConnection, player, before, gold,
            new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 4096), deadline.Token));

        await using TestPipe malformedPipe = await TestPipe.OpenAsync();
        await malformedPipe.Server.WriteAsync(UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before), deadline.Token);
        await malformedPipe.Server.WriteAsync(Frame(WorldOpcode.SmsgLootMoneyNotify, [1]), deadline.Token);
        var malformedConnection = new ScenarioConnection(malformedPipe.Client);
        await Assert.ThrowsAsync<MockProtocolException>(() => StartingZoneLoot.WaitForMoneyAsync(malformedConnection, player, before, gold,
            new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 4096), deadline.Token));
    }

    [Fact]
    public async Task WaitForMoney_TimesOutWhenClearArrivesWithoutWalletDelta()
    {
        await using TestPipe pipe = await TestPipe.OpenAsync();
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const ulong player = 77;
        const uint before = 1000;
        await pipe.Server.WriteAsync(UpdateFrame(player, UpdateFields.PlayerFieldCoinage, before), setup.Token);
        await pipe.Server.WriteAsync(Frame(WorldOpcode.SmsgLootClearMoney, []), setup.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var connection = new ScenarioConnection(pipe.Client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartingZoneLoot.WaitForMoneyAsync(connection, player, before, 37,
            new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 4096), deadline.Token));
    }

    private static byte[] Frame(WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(payload.Length + 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)opcode);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static byte[] UpdateFrame(ulong guid, int field, uint value)
    {
        var body = new PacketWriter(180);
        body.WriteUInt32(1);
        body.WriteByte(0);
        body.WriteByte((byte)ObjectUpdateType.Values);
        body.WritePackedGuid(guid);
        int words = (field / 32) + 1;
        body.WriteByte((byte)words);
        for (int i = 0; i < words; i++)
            body.WriteUInt32(i == field / 32 ? 1u << (field % 32) : 0);
        body.WriteUInt32(value);
        return Frame(WorldOpcode.SmsgUpdateObject, body.ToArray());
    }

    private sealed class TestPipe(WorldClient client, TcpClient server, TcpListener listener) : IAsyncDisposable
    {
        public WorldClient Client { get; } = client;
        public NetworkStream Server { get; } = server.GetStream();
        public static async Task<TestPipe> OpenAsync()
        {
            using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync(setup.Token).AsTask();
            WorldClient client = await WorldClient.ConnectAsync((System.Net.IPEndPoint)listener.LocalEndpoint, setup.Token);
            TcpClient server = await accepted;
            return new TestPipe(client, server, listener);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Server.Close();
            server.Dispose();
            listener.Stop();
        }
    }
}
