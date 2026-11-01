using System.Text;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Tests.Cluster.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// The unified control wire (C3 of _wf/handoff-control-DESIGN.md): frames 28-32 (<see cref="ClusterControlWire"/>), contract 5 and the shared
/// bundle JSON. Golden bytes pin the layout, every verb round-trips, every malformed shape is refused with <see cref="InvalidDataException"/>
/// (the exception a gateway link host's receive loop turns into a closed link once C4 dispatches these frames; no host calls the decoders yet), and a bundle crosses the journal and the wire identically.
/// </summary>
public sealed class ClusterControlWireTests : IAsyncLifetime
{
    private static readonly Guid Op = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Inc = new("ffeeddcc-bbaa-9988-7766-554433221100");
    private static readonly CharacterOwnershipToken Token = new("world", 7, Inc);
    private static readonly TransferPosition Position = new(1.5f, -2.5f, 3.25f, 0.5f);
    private static readonly WorkerNodeId NodeA = new("world-a");
    private static readonly WorkerNodeId NodeB = new("world-b");

    private readonly List<ServiceProvider> _providers = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-control-wire-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
            await provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory a straggling connection still holds is left for the OS to clean.
        }
    }

    // ------------------------------------------------------------------ pinned values

    [Fact]
    public void FrameTypeAndEnumValues_ArePinned()
    {
        Assert.Equal(28, (byte)GatewayWorkerFrameType.ControlRequest);
        Assert.Equal(29, (byte)GatewayWorkerFrameType.ControlReply);
        Assert.Equal(30, (byte)GatewayWorkerFrameType.ControlSubmit);
        Assert.Equal(31, (byte)GatewayWorkerFrameType.ControlSubmitReply);
        Assert.Equal(32, (byte)GatewayWorkerFrameType.ControlNotice);
        Assert.Equal(27, (byte)GatewayWorkerFrameType.TransferAck); // frames 24-27 stay as they are

        Assert.Equal(new[] { 1, 2 }, Values<ControlKind>());
        Assert.Equal(new[] { 1, 2, 3, 4 }, Values<ControlStatus>());
        Assert.Equal(new[] { 1, 2 }, Values<ControlSubmitKind>());
        Assert.Equal(new[] { 1, 2 }, Values<ControlNoticeKind>());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Values<ControlNoticeOutcome>());
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 17, 18, 19, 20, 21, 22, 23, 24 }, Values<ControlVerb>());
        Assert.Equal(1, (byte)ControlTransferOptions.NotLeaveTransport);

        Assert.Equal(ControlVerb.Describe, (ControlVerb)1);
        Assert.Equal(ControlVerb.Warm, (ControlVerb)2);
        Assert.Equal(ControlVerb.StopAdmissions, (ControlVerb)3);
        Assert.Equal(ControlVerb.Freeze, (ControlVerb)4);
        Assert.Equal(ControlVerb.Flush, (ControlVerb)5);
        Assert.Equal(ControlVerb.Resume, (ControlVerb)6);
        Assert.Equal(ControlVerb.ReleaseWriterLock, (ControlVerb)7);
        Assert.Equal(ControlVerb.Retire, (ControlVerb)8);
        Assert.Equal(ControlVerb.Promote, (ControlVerb)9);
        Assert.Equal(ControlVerb.TargetDescribe, (ControlVerb)17);
        Assert.Equal(ControlVerb.Reserve, (ControlVerb)18);
        Assert.Equal(ControlVerb.Install, (ControlVerb)19);
        Assert.Equal(ControlVerb.Activate, (ControlVerb)20);
        Assert.Equal(ControlVerb.Discard, (ControlVerb)21);
        Assert.Equal(ControlVerb.Suspend, (ControlVerb)22);
        Assert.Equal(ControlVerb.ResumeSource, (ControlVerb)23);
        Assert.Equal(ControlVerb.RetireSource, (ControlVerb)24);

        foreach (ControlVerb verb in Enum.GetValues<ControlVerb>())
        {
            ControlKind expected = (byte)verb < 17 ? ControlKind.WorldHandoff : ControlKind.PlayerTransfer;
            Assert.Equal(expected, ClusterControlWire.KindOf(verb));
        }

        Assert.Null(ClusterControlWire.KindOf((ControlVerb)0));
        Assert.Null(ClusterControlWire.KindOf((ControlVerb)10));
        Assert.Null(ClusterControlWire.KindOf((ControlVerb)16));
        Assert.Null(ClusterControlWire.KindOf((ControlVerb)25));
        Assert.Equal(16 * 1024, ClusterControlWire.MaxBundleBytes);
    }

    [Fact]
    public void ContractVersion5_RefusesAVersion4Peer_AtAdmission()
    {
        ClusterNodeManifest worker = GatewayLinkManifest.Create(ClusterRole.Worker, "worker", Guid.NewGuid(), 1, "dev", 1, 1);
        ClusterNodeManifest gateway = GatewayLinkManifest.Create(ClusterRole.Gateway, "gw", Guid.NewGuid(), 1, "dev", 1, 1);
        Assert.Equal(5, GatewayLinkManifest.ContractVersion);
        Assert.Equal(5, gateway.ContractVersion);
        Assert.True(worker.TryAdmit(gateway, out _));
        Assert.False(worker.TryAdmit(gateway with { ContractVersion = 4 }, out string? reason));
        Assert.Contains("contract version mismatch", reason);
        Assert.Contains("v4", reason);
        Assert.False(gateway.TryAdmit(worker with { ContractVersion = 4 }, out _));
    }

    // ------------------------------------------------------------------ golden bytes

    [Fact]
    public void ControlRequest_Warm_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(0x0102030405060708UL, ControlKind.WorldHandoff, Op, ControlVerb.Warm, new ControlWarmArguments(Inc)));
        Assert.Equal(Concat([28], [8, 7, 6, 5, 4, 3, 2, 1], [1], Op.ToByteArray(), [2], Inc.ToByteArray()), frame);
    }

    [Fact]
    public void ControlRequest_Reserve_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.PlayerTransfer, Op, ControlVerb.Reserve, new ControlReserveArguments(0x1112131415161718UL, 0x01020304u, 0)));
        Assert.Equal(
            Concat([28], [1, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [18], [0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11], [4, 3, 2, 1], [0, 0, 0, 0]),
            frame);
    }

    [Fact]
    public void ControlRequest_Promote_GoldenBytes_CarryTheTokenAsScopeEpochIncarnation()
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(2, ControlKind.WorldHandoff, Op, ControlVerb.Promote, new ControlTokenArguments(Token)));
        Assert.Equal(
            Concat([28], [2, 0, 0, 0, 0, 0, 0, 0], [1], Op.ToByteArray(), [9], [5, 0, 0, 0], "world"u8.ToArray(), [7, 0, 0, 0, 0, 0, 0, 0], Inc.ToByteArray()),
            frame);
    }

    [Fact]
    public void ControlRequest_Freeze_GoldenBytes_HaveNoArguments()
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(3, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, ControlNoArguments.Instance));
        Assert.Equal(Concat([28], [3, 0, 0, 0, 0, 0, 0, 0], [1], Op.ToByteArray(), [4]), frame);
    }

    [Fact]
    public void ControlReply_Reserve_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(9, ControlKind.PlayerTransfer, Op, ControlVerb.Reserve, ControlStatus.Ok, "", new ControlReservePayload(true, 0x0A0B0C0Du)));
        Assert.Equal(Concat([29], [9, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [18], [1], [0, 0, 0, 0], [1], [0x0D, 0x0C, 0x0B, 0x0A]), frame);
    }

    [Fact]
    public void ControlReply_Refused_GoldenBytes_CarryNoPayload()
    {
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(9, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, ControlStatus.Refused, "no", ControlNoPayload.Instance));
        Assert.Equal(Concat([29], [9, 0, 0, 0, 0, 0, 0, 0], [1], Op.ToByteArray(), [4], [2], [2, 0, 0, 0], "no"u8.ToArray()), frame);
    }

    [Fact]
    public void ControlReply_Suspend_GoldenBytes_CarryTheBundleAsALengthPrefixedBlock()
    {
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(4, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Ok, "", new ControlSuspendPayload([0x7B, 0x7D])));
        Assert.Equal(Concat([29], [4, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [22], [1], [0, 0, 0, 0], [2, 0, 0, 0], [0x7B, 0x7D]), frame);
    }

    [Fact]
    public void ControlRequest_Suspend_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(6, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, new ControlSuspendArguments(0x1112131415161718UL, "world-b", 530, 3, Position)));
        Assert.Equal(
            Concat(
                [28], [6, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [22],
                [0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11], [7, 0, 0, 0], "world-b"u8.ToArray(),
                [0x12, 0x02, 0, 0], [3, 0, 0, 0],
                BitConverter.GetBytes(1.5f), BitConverter.GetBytes(-2.5f), BitConverter.GetBytes(3.25f), BitConverter.GetBytes(0.5f)),
            frame);
    }

    [Theory]
    [InlineData(ControlVerb.Discard, 21)]
    [InlineData(ControlVerb.ResumeSource, 23)]
    public void ControlRequest_ReasonVerbs_GoldenBytes(ControlVerb verb, byte verbByte)
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(7, ControlKind.PlayerTransfer, Op, verb, new ControlReasonArguments("why")));
        Assert.Equal(Concat([28], [7, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [verbByte], [3, 0, 0, 0], "why"u8.ToArray()), frame);
    }

    [Theory]
    [InlineData(ControlVerb.Activate, 20)]
    [InlineData(ControlVerb.RetireSource, 24)]
    public void ControlRequest_TokenVerbs_GoldenBytes(ControlVerb verb, byte verbByte)
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(8, ControlKind.PlayerTransfer, Op, verb, new ControlTokenArguments(Token)));
        Assert.Equal(
            Concat([28], [8, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [verbByte], [5, 0, 0, 0], "world"u8.ToArray(), [7, 0, 0, 0, 0, 0, 0, 0], Inc.ToByteArray()),
            frame);
    }

    [Fact]
    public void ControlReply_Describe_GoldenBytes_WithAHeldToken()
    {
        var payload = new ControlDescribePayload("world-a", true, Inc, "node-1", "world", [1u, 530u], Token, 2, true);
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(10, ControlKind.WorldHandoff, Op, ControlVerb.Describe, ControlStatus.Ok, "", payload));
        Assert.Equal(
            Concat(
                [29], [10, 0, 0, 0, 0, 0, 0, 0], [1], Op.ToByteArray(), [1], [1], [0, 0, 0, 0],
                [7, 0, 0, 0], "world-a"u8.ToArray(), [1], Inc.ToByteArray(),
                [6, 0, 0, 0], "node-1"u8.ToArray(), [5, 0, 0, 0], "world"u8.ToArray(),
                [2, 0, 0, 0], [1, 0, 0, 0], [0x12, 0x02, 0, 0],
                [1], [5, 0, 0, 0], "world"u8.ToArray(), [7, 0, 0, 0, 0, 0, 0, 0], Inc.ToByteArray(),
                [2, 0, 0, 0], [1]),
            frame);
    }

    [Fact]
    public void ControlReply_Describe_GoldenBytes_WithoutAHeldToken()
    {
        var payload = new ControlDescribePayload("world-b", false, Inc, "n", "w", [], null, 0, false);
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(11, ControlKind.WorldHandoff, Op, ControlVerb.Describe, ControlStatus.Ok, "", payload));
        Assert.Equal(
            Concat(
                [29], [11, 0, 0, 0, 0, 0, 0, 0], [1], Op.ToByteArray(), [1], [1], [0, 0, 0, 0],
                [7, 0, 0, 0], "world-b"u8.ToArray(), [0], Inc.ToByteArray(),
                [1, 0, 0, 0], "n"u8.ToArray(), [1, 0, 0, 0], "w"u8.ToArray(),
                [0, 0, 0, 0], [0],
                [0, 0, 0, 0], [0]),
            frame);
    }

    [Fact]
    public void ControlReply_TargetDescribe_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(12, ControlKind.PlayerTransfer, Op, ControlVerb.TargetDescribe, ControlStatus.Ok, "", new ControlTargetDescribePayload(Inc, "worker:b", true, 12, 1)));
        Assert.Equal(
            Concat([29], [12, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(), [17], [1], [0, 0, 0, 0], Inc.ToByteArray(), [8, 0, 0, 0], "worker:b"u8.ToArray(), [1], [12, 0, 0, 0], [1, 0, 0, 0]),
            frame);
    }

    [Fact]
    public void ControlReply_TargetDescribe_AdmittedGatewayCount_RoundTripsAndIsChecked()
    {
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(3, ControlKind.PlayerTransfer, Op, ControlVerb.TargetDescribe, ControlStatus.Ok, "", new ControlTargetDescribePayload(Inc, "worker:b", false, 0, 2)));
        var decoded = Assert.IsType<ControlTargetDescribePayload>(ClusterControlWire.DecodeReply(frame.AsSpan(1)).Payload);
        Assert.Equal(2, decoded.AdmittedGateways);

        // A negative count is refused on both ends: the last four bytes of the frame are the count.
        byte[] negative = [.. frame];
        negative[^1] = 0x80;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(negative.AsSpan(1)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeReply(
            new ControlReply(3, ControlKind.PlayerTransfer, Op, ControlVerb.TargetDescribe, ControlStatus.Ok, "", new ControlTargetDescribePayload(Inc, "w", true, 0, -1))));

        // A frame of the old shape (no count) is truncated, so the link closes.
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(frame.AsSpan(1, frame.Length - 5)));
    }

    [Fact]
    public void ControlSubmit_Transfer_GoldenBytes()
    {
        var body = new ControlTransferSubmit(Op, 5, 6, 7, 1, 2, 3, 4, new TransferPosition(1f, 2f, 3f, 4f), ControlTransferOptions.NotLeaveTransport, Token);
        byte[] frame = ClusterControlWire.EncodeSubmit(new ControlSubmit(11, body));
        Assert.Equal(
            Concat(
                [30], [11, 0, 0, 0, 0, 0, 0, 0], [2], Op.ToByteArray(),
                [5, 0, 0, 0, 0, 0, 0, 0], [6, 0, 0, 0], [7, 0, 0, 0, 0, 0, 0, 0],
                [1, 0, 0, 0], [2, 0, 0, 0], [3, 0, 0, 0], [4, 0, 0, 0],
                BitConverter.GetBytes(1f), BitConverter.GetBytes(2f), BitConverter.GetBytes(3f), BitConverter.GetBytes(4f),
                [1], [5, 0, 0, 0], "world"u8.ToArray(), [7, 0, 0, 0, 0, 0, 0, 0], Inc.ToByteArray()),
            frame);
    }

    [Fact]
    public void ControlSubmit_Restart_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeSubmit(new ControlSubmit(12, new ControlRestartSubmit(3, 4, 2, "x")));
        Assert.Equal(Concat([30], [12, 0, 0, 0, 0, 0, 0, 0], [1], [3, 0, 0, 0], [4, 0, 0, 0, 0, 0, 0, 0], [2], [1, 0, 0, 0], "x"u8.ToArray()), frame);
    }

    [Fact]
    public void ControlSubmitReply_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(13, true, Op, "no"));
        Assert.Equal(Concat([31], [13, 0, 0, 0, 0, 0, 0, 0], [1], Op.ToByteArray(), [2, 0, 0, 0], "no"u8.ToArray()), frame);
    }

    [Fact]
    public void ControlNotice_GoldenBytes()
    {
        byte[] frame = ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.TransferOutcome, 5, 7, 9, Op, ControlNoticeOutcome.Aborted, "x"));
        Assert.Equal(
            Concat([32], [2], [5, 0, 0, 0, 0, 0, 0, 0], [7, 0, 0, 0], [9, 0, 0, 0, 0, 0, 0, 0], Op.ToByteArray(), [2], [1, 0, 0, 0], "x"u8.ToArray()),
            frame);
    }

    // ------------------------------------------------------------------ round trips

    public static IEnumerable<object[]> RequestCases()
    {
        foreach (ControlVerb verb in Enum.GetValues<ControlVerb>())
            yield return [verb];
    }

    [Theory]
    [MemberData(nameof(RequestCases))]
    public void ControlRequest_EveryVerb_RoundTrips(ControlVerb verb)
    {
        ControlKind kind = ClusterControlWire.KindOf(verb)!.Value;
        ControlArguments args = verb switch
        {
            ControlVerb.Warm => new ControlWarmArguments(Inc),
            ControlVerb.Promote or ControlVerb.Activate or ControlVerb.RetireSource => new ControlTokenArguments(Token),
            ControlVerb.Reserve => new ControlReserveArguments(42, 1, 0),
            ControlVerb.Discard or ControlVerb.ResumeSource => new ControlReasonArguments("reason"),
            ControlVerb.Suspend => new ControlSuspendArguments(42, "world-b", 1, 3, Position),
            _ => ControlNoArguments.Instance,
        };
        var request = new ControlRequest(ulong.MaxValue, kind, Op, verb, args);
        byte[] frame = ClusterControlWire.EncodeRequest(request);
        Assert.Equal((byte)GatewayWorkerFrameType.ControlRequest, frame[0]);
        Assert.Equal(request, ClusterControlWire.DecodeRequest(frame.AsSpan(1)));
    }

    [Fact]
    public void ControlReply_EveryVerbWithItsPayload_RoundTrips()
    {
        foreach (ControlVerb verb in Enum.GetValues<ControlVerb>())
        {
            ControlKind kind = ClusterControlWire.KindOf(verb)!.Value;
            ControlPayload payload = verb switch
            {
                ControlVerb.Describe => new ControlDescribePayload("world-a", false, Inc, "node-1", "world", [0u, 1u, 530u], Token, 1, true),
                ControlVerb.TargetDescribe => new ControlTargetDescribePayload(Inc, "worker:b", true, 12, 2),
                ControlVerb.Reserve => new ControlReservePayload(true, 77),
                ControlVerb.Suspend => new ControlSuspendPayload([1, 2, 3, 4]),
                _ => ControlNoPayload.Instance,
            };
            byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(5, kind, Op, verb, ControlStatus.Ok, "fine", payload));
            ControlReply decoded = ClusterControlWire.DecodeReply(frame.AsSpan(1));
            Assert.Equal((5UL, kind, Op, verb, ControlStatus.Ok, "fine"), (decoded.RequestId, decoded.Kind, decoded.OperationId, decoded.Verb, decoded.Status, decoded.Reason));
            switch (payload)
            {
                case ControlDescribePayload expected:
                    var describe = Assert.IsType<ControlDescribePayload>(decoded.Payload);
                    Assert.Equal(expected with { OwnedMaps = [] }, describe with { OwnedMaps = [] });
                    Assert.Equal(expected.OwnedMaps, describe.OwnedMaps);
                    break;
                case ControlSuspendPayload expected:
                    Assert.Equal(expected.BundleJson, Assert.IsType<ControlSuspendPayload>(decoded.Payload).BundleJson);
                    break;
                default:
                    Assert.Equal(payload, decoded.Payload);
                    break;
            }
        }
    }

    [Fact]
    public void ControlReply_DescribeWithoutAHeldToken_RoundTripsTheAbsence()
    {
        var payload = new ControlDescribePayload("world-b", true, Inc, "node-2", "world", [], null, 0, false);
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(5, ControlKind.WorldHandoff, Op, ControlVerb.Describe, ControlStatus.Ok, "", payload));
        var decoded = Assert.IsType<ControlDescribePayload>(ClusterControlWire.DecodeReply(frame.AsSpan(1)).Payload);
        Assert.Null(decoded.HeldSourceToken);
        Assert.Empty(decoded.OwnedMaps);
        Assert.True(decoded.IsStandby);
    }

    [Theory]
    [InlineData(ControlStatus.Refused)]
    [InlineData(ControlStatus.Failed)]
    [InlineData(ControlStatus.Disabled)]
    public void ControlReply_NonOkStatuses_RoundTripWithoutAPayload(ControlStatus status)
    {
        foreach (ControlVerb verb in Enum.GetValues<ControlVerb>())
        {
            ControlKind kind = ClusterControlWire.KindOf(verb)!.Value;
            var reply = new ControlReply(6, kind, Op, verb, status, "why", ControlNoPayload.Instance);
            Assert.Equal(reply, ClusterControlWire.DecodeReply(ClusterControlWire.EncodeReply(reply).AsSpan(1)));
        }
    }

    [Fact]
    public void ControlSubmit_BothBodies_RoundTrip()
    {
        var transfer = new ControlSubmit(1, new ControlTransferSubmit(Op, ulong.MaxValue, int.MaxValue, long.MaxValue, 0, 0, 1, 0, Position, ControlTransferOptions.None, Token));
        Assert.Equal(transfer, ClusterControlWire.DecodeSubmit(ClusterControlWire.EncodeSubmit(transfer).AsSpan(1)));
        var withOption = new ControlSubmit(2, new ControlTransferSubmit(Op, 1, 2, 3, 4, 5, 6, 7, Position, ControlTransferOptions.NotLeaveTransport, Token));
        Assert.Equal(withOption, ClusterControlWire.DecodeSubmit(ClusterControlWire.EncodeSubmit(withOption).AsSpan(1)));
        var restart = new ControlSubmit(3, new ControlRestartSubmit(9, 10, 3, "now"));
        Assert.Equal(restart, ClusterControlWire.DecodeSubmit(ClusterControlWire.EncodeSubmit(restart).AsSpan(1)));
    }

    [Fact]
    public void ControlSubmitReply_AndNotice_RoundTrip()
    {
        var reply = new ControlSubmitReply(8, false, Guid.Empty, "a transfer of this slot is unresolved");
        Assert.Equal(reply, ClusterControlWire.DecodeSubmitReply(ClusterControlWire.EncodeSubmitReply(reply).AsSpan(1)));
        foreach (ControlNoticeKind kind in Enum.GetValues<ControlNoticeKind>())
        {
            foreach (ControlNoticeOutcome outcome in Enum.GetValues<ControlNoticeOutcome>())
            {
                var notice = new ControlNotice(kind, -1, 12, 99, Op, outcome, "Cluster restart finished.");
                Assert.Equal(notice, ClusterControlWire.DecodeNotice(ClusterControlWire.EncodeNotice(notice).AsSpan(1)));
            }
        }
    }

    [Fact]
    public void FreeText_IsCutAtTheByteLimit_AndKeyedTextIsRefusedWhenTooLong()
    {
        string longReason = new('x', GatewaySessionWire.MaxTextBytes + 50);
        ControlReply cut = ClusterControlWire.DecodeReply(ClusterControlWire.EncodeReply(
            new ControlReply(1, ControlKind.WorldHandoff, Op, ControlVerb.Resume, ControlStatus.Failed, longReason, ControlNoPayload.Instance)).AsSpan(1));
        Assert.Equal(GatewaySessionWire.MaxTextBytes, cut.Reason.Length);

        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(
            1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, new ControlSuspendArguments(1, longReason, 1, 0, Position))));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(
            1, ControlKind.WorldHandoff, Op, ControlVerb.Promote, new ControlTokenArguments(new CharacterOwnershipToken(longReason, 1, Inc)))));
    }

    // ------------------------------------------------------------------ the encoder refuses a frame the decoder would refuse

    [Fact]
    public void Encoders_RefuseMismatchedKinds_Arguments_Payloads_AndOversizedBundles()
    {
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.PlayerTransfer, Op, ControlVerb.Freeze, ControlNoArguments.Instance)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, (ControlVerb)10, ControlNoArguments.Instance)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, ControlVerb.Warm, ControlNoArguments.Instance)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, new ControlWarmArguments(Inc))));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(
            1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, new ControlSuspendArguments(1, "b", 1, 0, new TransferPosition(float.NaN, 0, 0, 0)))));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeReply(
            new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Ok, "", ControlNoPayload.Instance)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeReply(
            new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Failed, "", new ControlSuspendPayload([1]))));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeReply(
            new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Ok, "", new ControlSuspendPayload(new byte[ClusterControlWire.MaxBundleBytes + 1]))));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeReply(
            new ControlReply(1, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, (ControlStatus)9, "", ControlNoPayload.Instance)));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeNotice(new ControlNotice((ControlNoticeKind)9, 1, 1, 1, Op, ControlNoticeOutcome.None, "")));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.OperatorText, 1, 1, 1, Op, (ControlNoticeOutcome)9, "")));

        // The largest bundle is accepted.
        byte[] max = ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Ok, "", new ControlSuspendPayload(new byte[ClusterControlWire.MaxBundleBytes])));
        Assert.Equal(ClusterControlWire.MaxBundleBytes, Assert.IsType<ControlSuspendPayload>(ClusterControlWire.DecodeReply(max.AsSpan(1)).Payload).BundleJson.Length);
    }

    // ------------------------------------------------------------------ malformed frames (an InvalidDataException closes the link)

    private static IEnumerable<(string Name, byte[] Frame, Action<byte[]> Decode)> SampleFrames()
    {
        yield return ("request-freeze", ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, ControlNoArguments.Instance)),
            b => ClusterControlWire.DecodeRequest(b));
        yield return ("request-suspend", ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, new ControlSuspendArguments(1, "b", 1, 0, Position))),
            b => ClusterControlWire.DecodeRequest(b));
        yield return ("request-promote", ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, ControlVerb.Promote, new ControlTokenArguments(Token))),
            b => ClusterControlWire.DecodeRequest(b));
        yield return ("reply-describe", ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.WorldHandoff, Op, ControlVerb.Describe, ControlStatus.Ok, "",
            new ControlDescribePayload("a", false, Inc, "n", "world", [1u], Token, 1, true))), b => ClusterControlWire.DecodeReply(b));
        yield return ("reply-suspend", ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Ok, "", new ControlSuspendPayload([1, 2, 3]))),
            b => ClusterControlWire.DecodeReply(b));
        yield return ("reply-refused", ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, ControlStatus.Refused, "r", ControlNoPayload.Instance)),
            b => ClusterControlWire.DecodeReply(b));
        yield return ("submit-transfer", ClusterControlWire.EncodeSubmit(new ControlSubmit(1, new ControlTransferSubmit(Op, 1, 2, 3, 4, 5, 6, 7, Position, ControlTransferOptions.None, Token))),
            b => ClusterControlWire.DecodeSubmit(b));
        yield return ("submit-restart", ClusterControlWire.EncodeSubmit(new ControlSubmit(1, new ControlRestartSubmit(1, 2, 3, "a"))), b => ClusterControlWire.DecodeSubmit(b));
        yield return ("submit-reply", ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(1, true, Op, "")), b => ClusterControlWire.DecodeSubmitReply(b));
        yield return ("notice", ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.OperatorText, 1, 2, 3, Op, ControlNoticeOutcome.None, "t")), b => ClusterControlWire.DecodeNotice(b));
    }

    public static IEnumerable<object[]> SampleNames() => SampleFrames().Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void EveryFrame_RefusesEveryTruncation_AndTrailingBytes(string name)
    {
        (_, byte[] frame, Action<byte[]> decode) = SampleFrames().Single(f => f.Name == name);
        byte[] body = frame[1..];
        decode(body); // the intact body decodes
        for (int length = 0; length < body.Length; length++)
        {
            byte[] cut = body[..length];
            Assert.Throws<InvalidDataException>(() => decode(cut));
        }

        Assert.Throws<InvalidDataException>(() => decode([.. body, 0]));
    }

    [Fact]
    public void ControlRequest_RefusesUnknownKind_UnknownVerb_AndAKindVerbMismatch()
    {
        byte[] frame = ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, ControlVerb.Freeze, ControlNoArguments.Instance));
        byte[] body = frame[1..];
        const int kindAt = 8, verbAt = 8 + 1 + 16;

        byte[] badKind = [.. body];
        badKind[kindAt] = 3;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(badKind));
        badKind[kindAt] = 0;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(badKind));

        byte[] badVerb = [.. body];
        badVerb[verbAt] = 10;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(badVerb));
        badVerb[verbAt] = 0;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(badVerb));

        byte[] mismatch = [.. body];
        mismatch[verbAt] = (byte)ControlVerb.Reserve; // a transfer verb under the handoff kind
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(mismatch));
    }

    [Fact]
    public void ControlReply_RefusesUnknownStatus_AndABooleanThatIsNotZeroOrOne()
    {
        byte[] ok = ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Reserve, ControlStatus.Ok, "", new ControlReservePayload(true, 1)))[1..];
        const int statusAt = 8 + 1 + 16 + 1;

        byte[] badStatus = [.. ok];
        badStatus[statusAt] = 0;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(badStatus));
        badStatus[statusAt] = 5;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(badStatus));

        byte[] badBool = [.. ok];
        badBool[statusAt + 1 + 4] = 2; // the reserved flag
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(badBool));

        // A non-Ok reply with payload bytes behind it has trailing bytes.
        byte[] refused = [.. ok];
        refused[statusAt] = (byte)ControlStatus.Refused;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(refused));
    }

    [Fact]
    public void Decoders_RefuseNegativeOversizedAndOutOfRangeLengths()
    {
        // Text length past the limit, and negative.
        byte[] reply = ClusterControlWire.EncodeSubmitReply(new ControlSubmitReply(1, true, Op, ""))[1..];
        int textAt = 8 + 1 + 16;
        byte[] tooLong = [.. reply];
        tooLong[textAt] = 0x01;
        tooLong[textAt + 1] = 0x01; // 257
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmitReply(tooLong));
        byte[] negative = [.. reply];
        negative[textAt + 3] = 0x80;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmitReply(negative));
        byte[] notABool = [.. reply];
        notABool[8] = 2;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmitReply(notABool));

        // A bundle length over the limit.
        byte[] suspend = ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, ControlStatus.Ok, "", new ControlSuspendPayload([1])))[1..];
        int lengthAt = 8 + 1 + 16 + 1 + 1 + 4;
        byte[] bigBundle = [.. suspend];
        bigBundle[lengthAt] = 0x01;
        bigBundle[lengthAt + 1] = 0x40; // 0x4001 = 16385
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(bigBundle));
        byte[] negativeBundle = [.. suspend];
        negativeBundle[lengthAt + 3] = 0x80;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(negativeBundle));

        // A Describe payload listing more maps than allowed, and a negative count.
        byte[] describe = ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.WorldHandoff, Op, ControlVerb.Describe, ControlStatus.Ok, "",
            new ControlDescribePayload("a", false, Inc, "n", "w", [], null, 0, false)))[1..];
        int countAt = 8 + 1 + 16 + 1 + 1 + 4 + (4 + 1) + 1 + 16 + (4 + 1) + (4 + 1);
        byte[] manyMaps = [.. describe];
        manyMaps[countAt + 1] = 0x04; // 1024 maps are allowed; 0x0401 = 1025 are not
        manyMaps[countAt] = 0x01;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(manyMaps));
        byte[] negativeMaps = [.. describe];
        negativeMaps[countAt + 3] = 0x80;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeReply(negativeMaps));
    }

    [Fact]
    public void ControlSuspend_RefusesANonFinitePosition_AndAnEpochBelowZero()
    {
        byte[] suspend = ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.PlayerTransfer, Op, ControlVerb.Suspend, new ControlSuspendArguments(1, "b", 1, 0, Position)))[1..];
        byte[] nan = [.. suspend];
        nan[^16] = 0x00;
        nan[^15] = 0x00;
        nan[^14] = 0xC0;
        nan[^13] = 0x7F; // float.NaN
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(nan));
        byte[] infinity = [.. suspend];
        infinity[^4] = 0x00;
        infinity[^3] = 0x00;
        infinity[^2] = 0x80;
        infinity[^1] = 0x7F; // +infinity orientation
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(infinity));

        byte[] promote = ClusterControlWire.EncodeRequest(new ControlRequest(1, ControlKind.WorldHandoff, Op, ControlVerb.Promote, new ControlTokenArguments(Token)))[1..];
        int epochAt = promote.Length - 16 - 8;
        byte[] negativeEpoch = [.. promote];
        negativeEpoch[epochAt + 7] = 0x80;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeRequest(negativeEpoch));
        Assert.Throws<ArgumentException>(() => ClusterControlWire.EncodeRequest(new ControlRequest(
            1, ControlKind.WorldHandoff, Op, ControlVerb.Promote, new ControlTokenArguments(new CharacterOwnershipToken("world", -1, Inc)))));
    }

    [Fact]
    public void ControlSubmit_RefusesAnUnknownKind_AndUnknownOptionBits()
    {
        byte[] transfer = ClusterControlWire.EncodeSubmit(new ControlSubmit(1, new ControlTransferSubmit(Op, 1, 2, 3, 4, 5, 6, 7, Position, ControlTransferOptions.None, Token)))[1..];
        byte[] badKind = [.. transfer];
        badKind[8] = 3;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmit(badKind));
        badKind[8] = 0;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmit(badKind));

        // The options byte sits before the token: scope (4 + 5), epoch (8), incarnation (16).
        int optionsAt = transfer.Length - (4 + 5 + 8 + 16) - 1;
        byte[] badOptions = [.. transfer];
        badOptions[optionsAt] = 0x02;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeSubmit(badOptions));
    }

    [Fact]
    public void ControlNotice_RefusesAnUnknownKind_AndAnUnknownOutcome()
    {
        byte[] notice = ClusterControlWire.EncodeNotice(new ControlNotice(ControlNoticeKind.OperatorText, 1, 2, 3, Op, ControlNoticeOutcome.None, ""))[1..];
        byte[] badKind = [.. notice];
        badKind[0] = 0;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeNotice(badKind));
        badKind[0] = 3;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeNotice(badKind));

        byte[] badOutcome = [.. notice];
        badOutcome[1 + 8 + 4 + 8 + 16] = 5;
        Assert.Throws<InvalidDataException>(() => ClusterControlWire.DecodeNotice(badOutcome));
    }

    // ------------------------------------------------------------------ the bundle crosses the journal and the wire identically

    [Fact]
    public async Task TheBundle_RoundTripsThroughTheJournalAndTheWire_Identically()
    {
        Directory.CreateDirectory(_directory);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={Path.Combine(_directory, "characters.db")}",
        }).Build();
        ServiceProvider provider = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider();
        _providers.Add(provider);
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        EfTransferDecisionStore store = provider.GetRequiredService<EfTransferDecisionStore>();

        const ulong character = 4242UL;
        Guid transfer = Guid.NewGuid();
        var source = new CharacterOwnershipToken($"char:{character}", 1, Guid.NewGuid());
        PlayerTransferBundle original = PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, character, NodeA, NodeB, source);
        await store.CreateReservationAsync(transfer, character, 1, 1, NodeA, NodeB, 0, 0, 1, 0, source);
        await store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, original);

        // Journal: what the store holds is what PlayerTransferBundleJson writes.
        TransferDecisionRecord record = (await store.GetDecisionAsync(transfer))!;
        PlayerTransferBundle fromJournal = Assert.IsType<PlayerTransferBundle>(record.CapturedBundle);
        string journalJson = PlayerTransferBundleJson.Serialize(fromJournal);
        Assert.Equal(PlayerTransferBundleJson.Serialize(original), journalJson);

        // Wire: the Suspend reply carries the same bytes, and decoding them gives the journal's bundle again.
        byte[] json = PlayerTransferBundleJson.SerializeToUtf8(original);
        Assert.Equal(Encoding.UTF8.GetBytes(journalJson), json);
        Assert.InRange(json.Length, 1, ClusterControlWire.MaxBundleBytes);
        byte[] frame = ClusterControlWire.EncodeReply(new ControlReply(1, ControlKind.PlayerTransfer, transfer, ControlVerb.Suspend, ControlStatus.Ok, "", new ControlSuspendPayload(json)));
        var payload = Assert.IsType<ControlSuspendPayload>(ClusterControlWire.DecodeReply(frame.AsSpan(1)).Payload);
        Assert.Equal(json, payload.BundleJson);
        PlayerTransferBundle fromWire = PlayerTransferBundleJson.Deserialize(payload.BundleJson);
        Assert.Equal(journalJson, PlayerTransferBundleJson.Serialize(fromWire));
        Assert.Equal(journalJson, PlayerTransferBundleJson.Serialize(PlayerTransferBundleJson.Deserialize(journalJson)));
        Assert.Equal(original.TransferId, fromWire.TransferId);
        Assert.Equal(original.CharacterGuid, fromWire.CharacterGuid);
        Assert.Equal(original.SourceOwnershipToken, fromWire.SourceOwnershipToken);
        Assert.Equal(original.Money, fromWire.Money);
        Assert.Equal(original.TargetPosition, fromWire.TargetPosition);
        Assert.Equal(original.KnownSpells, fromWire.KnownSpells);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static int[] Values<T>() where T : struct, Enum => [.. Enum.GetValues<T>().Select(v => Convert.ToInt32(v)).Order()];
}
