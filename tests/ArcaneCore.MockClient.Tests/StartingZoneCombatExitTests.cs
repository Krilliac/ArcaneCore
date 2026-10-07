using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Real TCP update-field sequencing for the combat-exit wait.</summary>
public sealed class StartingZoneCombatExitTests
{
    [Fact]
    public async Task CombatExit_WaitsForPendingValuesUpdate_ThenReturnsAfterFlagClears()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        var connection = new ScenarioConnection(client);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 1024);
        ulong character = ObjectGuid.Player(77).Value;
        uint combat = (uint)UnitFlags.InCombat;
        await server.GetStream().WriteAsync(Concat(
            ValuesFrame(character, combat),
            ValuesFrame(character, 0)), deadline.Token);

        await StartingZoneCombat.WaitForCombatExitAsync(connection, character, budget, deadline.Token);

        Assert.Equal(2, budget.Frames);
        Assert.Equal(0u, connection.FieldsOf(character).GetValueOrDefault(UpdateFields.UnitFieldFlags) & (uint)UnitFlags.InCombat);
    }

    [Fact]
    public async Task CombatExit_PreservesUnknownFlags_WhenKnownCombatFlagClears()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        var connection = new ScenarioConnection(client);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 1024);
        ulong character = ObjectGuid.Player(78).Value;
        uint unknown = 0x40000000u;
        await server.GetStream().WriteAsync(Concat(
            ValuesFrame(character, (uint)UnitFlags.InCombat | unknown),
            ValuesFrame(character, unknown)), deadline.Token);

        await StartingZoneCombat.WaitForCombatExitAsync(connection, character, budget, deadline.Token);

        Assert.Equal(2, budget.Frames);
        Assert.Equal(unknown, connection.FieldsOf(character).GetValueOrDefault(UpdateFields.UnitFieldFlags));
    }

    [Fact]
    public async Task CombatExit_PropagatesDeadline_WhenCombatNeverClears()
    {
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(setup.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, setup.Token);
        using TcpClient server = await accepted;
        var connection = new ScenarioConnection(client);
        var budget = new StartingZoneCombat.CombatBudget(frameLimit: 4, byteLimit: 1024);
        ulong character = ObjectGuid.Player(79).Value;
        await server.GetStream().WriteAsync(ValuesFrame(character, (uint)UnitFlags.InCombat), setup.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StartingZoneCombat.WaitForCombatExitAsync(connection, character, budget, deadline.Token));
        Assert.Equal(1, budget.Frames);
        Assert.NotEqual(0u, connection.FieldsOf(character).GetValueOrDefault(UpdateFields.UnitFieldFlags) & (uint)UnitFlags.InCombat);
    }

    private static byte[] ValuesFrame(ulong guid, uint flags)
    {
        var body = new PacketWriter(32);
        body.WriteUInt32(1);
        body.WriteByte(0);
        body.WriteByte((byte)ObjectUpdateType.Values);
        body.WritePackedGuid(guid);
        body.WriteByte(2); // two mask words through UnitFieldFlags (index 0x2E)
        body.WriteUInt32(0); // mask word 0
        body.WriteUInt32(1u << (UpdateFields.UnitFieldFlags - 32));
        body.WriteUInt32(flags);
        return WorldFrameBytes(WorldOpcode.SmsgUpdateObject, body.ToArray());
    }

    private static byte[] WorldFrameBytes(WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)opcode);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static byte[] Concat(params byte[][] frames) => frames.SelectMany(static frame => frame).ToArray();
}
