using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Invalid client movement is dropped, never stored or relayed to observers, and does not
/// disconnect the sender (vmangos HandleMovementOpcodes drops a packet failing
/// VerifyMovementInfo: MovementHandler.cpp:315, :489, :1042-1061).
/// </summary>
public sealed class MovementRelayTests
{
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;

    [Fact]
    public async Task NonFiniteJumpField_IsRelayedVerbatimByDefault()
    {
        // Retail behaviour: vmangos VerifyMovementInfo checks only position/orientation/transport.
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("PLAYERA", "Aaa");
        await using WorldTestClient b = await host.EnterWorldAsync("PLAYERB", "Bbb");
        await a.ReadUpdateAsync();
        await b.ReadUpdateAsync();

        MovementInfo odd = Valid(StartX + 1);
        odd.Flags |= MovementFlags.Jumping;
        odd.JumpZSpeed = float.NaN;
        await a.SendAsync(WorldOpcode.MsgMoveJump, Encode(odd));
        Assert.Equal(WorldOpcode.MsgMoveJump, (await b.ReadAsync()).Opcode);
    }

    [Fact]
    public async Task NonFiniteJumpField_WithStrictFiniteness_IsNotRelayedAndSenderStaysConnected()
    {
        await using var host = WorldTestHost.Start(
            sessionOptions: new ArcaneCore.World.Net.WorldSessionOptions { StrictMovementFiniteness = true });
        await using WorldTestClient a = await host.EnterWorldAsync("PLAYERA", "Aaa");
        await using WorldTestClient b = await host.EnterWorldAsync("PLAYERB", "Bbb");
        await a.ReadUpdateAsync();
        await b.ReadUpdateAsync();

        MovementInfo bad = Valid(StartX + 1);
        bad.Flags |= MovementFlags.Jumping;
        bad.JumpZSpeed = float.NaN;
        await a.SendAsync(WorldOpcode.MsgMoveJump, Encode(bad));
        await b.AssertSilentAsync(TimeSpan.FromMilliseconds(300));

        // The sender is still connected, and a valid packet afterwards is relayed.
        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Valid(StartX + 2)));
        (WorldOpcode op, _) = await b.ReadAsync();
        Assert.Equal(WorldOpcode.MsgMoveHeartbeat, op);
    }

    [Fact]
    public async Task OutOfBoundsPosition_IsDroppedNotKicked()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("PLAYERA", "Aaa");
        await using WorldTestClient b = await host.EnterWorldAsync("PLAYERB", "Bbb");
        await a.ReadUpdateAsync();
        await b.ReadUpdateAsync();

        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Valid(17066.2f)));   // > MAP_HALFSIZE - 0.5
        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Valid(float.PositiveInfinity)));
        await b.AssertSilentAsync(TimeSpan.FromMilliseconds(300));

        await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Valid(StartX + 3)));
        Assert.Equal(WorldOpcode.MsgMoveHeartbeat, (await b.ReadAsync()).Opcode);
    }

    private static MovementInfo Valid(float x) => new()
    {
        Flags = MovementFlags.None, Time = 50, X = x, Y = StartY, Z = 83.5f, Orientation = 0.5f,
    };

    private static byte[] Encode(MovementInfo info)
    {
        var writer = new PacketWriter(64);
        info.Write(writer);
        return writer.AsMemory().ToArray();
    }
}
