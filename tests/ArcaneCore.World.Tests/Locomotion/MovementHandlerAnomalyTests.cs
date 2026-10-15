using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// <c>move/non-finite</c> as raised by the real movement handler (MovementHandlers.IsAcceptable -> ReportNonFiniteMovement), driven by
/// packets from a connected client. A dropped packet leaves no side effect to wait for, so every silence is asserted only after a later
/// valid packet from the same client has been stored (the handler is in-order): the earlier packet has certainly been judged by then.
/// The ledger is shared static state, so each test captures only its own player name.
/// </summary>
public sealed class MovementHandlerAnomalyTests
{
    private const string NonFinite = "move/non-finite";
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;
    private const float StartZ = 83.5312f;

    private static AnomalyCapture CaptureFor(string name)
        => AnomalyLedger.Shared.Capture(n => n.Key == NonFinite && n.Subject.StartsWith($"player {name} ", StringComparison.Ordinal));

    private static MovementInfo Block(float x, float y = StartY, float z = StartZ) => new()
    {
        Flags = MovementFlags.None, Time = 50, X = x, Y = y, Z = z, Orientation = 0.5f,
    };

    private static byte[] Encode(MovementInfo info)
    {
        var writer = new PacketWriter(64);
        info.Write(writer);
        return writer.AsMemory().ToArray();
    }

    /// <summary>Send a valid heartbeat at <paramref name="x"/> and wait until the server has stored it.</summary>
    private static async Task SyncAsync(WorldTestHost host, WorldTestClient client, string name, float x)
    {
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Block(x)));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.Movement.X == x, "the valid heartbeat to be stored");
    }

    [Fact]
    public async Task APacketWithANonFinitePosition_IsDroppedAndReported_AsAnError()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NFACCOUNTA", "Nfplayera");
        await client.CollectAsync();
        using AnomalyCapture capture = CaptureFor("Nfplayera");

        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Block(float.NaN)));
        await WorldTestHost.WaitForAsync(() => capture.Any(NonFinite), "the non-finite report");

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Contains("Non-finite world coordinates", notice.Detail);
        Assert.Equal(StartX, await host.PlayerStateAsync("Nfplayera", p => p.Movement.X), 2); // dropped: the block was not stored
    }

    [Fact]
    public async Task APacketWithANonFiniteOrientation_OrTransportOffset_IsReported()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NFACCOUNTB", "Nfplayerb");
        await client.CollectAsync();
        using AnomalyCapture capture = CaptureFor("Nfplayerb");

        MovementInfo badFacing = Block(StartX + 1);
        badFacing.Orientation = float.PositiveInfinity;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(badFacing));
        await WorldTestHost.WaitForAsync(() => capture.Count(NonFinite) == 1, "the orientation report");

        MovementInfo badOffset = Block(StartX + 1);
        badOffset.Flags = MovementFlags.OnTransport;
        badOffset.TransportGuid = 0x1FC0_0000_0000_0001UL;
        badOffset.TransportX = float.NaN;
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(badOffset));
        await WorldTestHost.WaitForAsync(() => capture.Count(NonFinite) == 2, "the transport offset report");

        Assert.Contains(capture.Notices, n => n.Detail.Contains("transport coordinates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APitchOrJumpFieldThatIsNaN_IsReportedOnlyUnderStrictFiniteness()
    {
        await using var strict = WorldTestHost.Start(sessionOptions: new WorldSessionOptions { StrictMovementFiniteness = true });
        await using WorldTestClient strictClient = await strict.EnterWorldAsync("NFACCOUNTC", "Nfplayerc");
        await strictClient.CollectAsync();
        using AnomalyCapture strictCapture = CaptureFor("Nfplayerc");
        MovementInfo odd = Block(StartX + 1);
        odd.Flags = MovementFlags.Jumping;
        odd.JumpZSpeed = float.NaN;

        await strictClient.SendAsync(WorldOpcode.MsgMoveJump, Encode(odd));
        await WorldTestHost.WaitForAsync(() => strictCapture.Any(NonFinite), "the strict-finiteness report");
        Assert.Contains("pitch/jump/spline", Assert.Single(strictCapture.Notices).Detail);

        // Retail default: the same packet is accepted and relayed verbatim, so it is not an anomaly.
        await using var relaxed = WorldTestHost.Start();
        await using WorldTestClient relaxedClient = await relaxed.EnterWorldAsync("NFACCOUNTD", "Nfplayerd");
        await relaxedClient.CollectAsync();
        using AnomalyCapture relaxedCapture = CaptureFor("Nfplayerd");
        await relaxedClient.SendAsync(WorldOpcode.MsgMoveJump, Encode(odd));
        await SyncAsync(relaxed, relaxedClient, "Nfplayerd", StartX + 2);
        Assert.False(relaxedCapture.Any(NonFinite), relaxedCapture.Describe());
    }

    [Fact]
    public async Task ValidMovement_AndABadPacketWhileAnomaliesAreSuppressed_ReportNothing()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NFACCOUNTE", "Nfplayere");
        await client.CollectAsync();
        using AnomalyCapture capture = CaptureFor("Nfplayere");

        await SyncAsync(host, client, "Nfplayere", StartX + 1);
        await SyncAsync(host, client, "Nfplayere", StartX + 2);
        Assert.False(capture.Any(NonFinite), capture.Describe());

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Nfplayere")!.Locomotion.SuppressAnomalies = true);
        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, Encode(Block(float.NaN)));
        await SyncAsync(host, client, "Nfplayere", StartX + 3);

        Assert.False(capture.Any(NonFinite), capture.Describe());
    }
}
