using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>Which operation a control frame belongs to (the <c>kind</c> byte of <see cref="GatewayWorkerFrameType.ControlRequest"/> and <see cref="GatewayWorkerFrameType.ControlReply"/>).</summary>
public enum ControlKind : byte
{
    /// <summary>A whole-World handoff.</summary>
    WorldHandoff = 1,

    /// <summary>A per-player cross-worker transfer.</summary>
    PlayerTransfer = 2,
}

/// <summary>
/// What a control request asks the worker to do. Handoff verbs are 1..9 and transfer verbs 17..24, so a verb alone names its <see cref="ControlKind"/>
/// (<see cref="ClusterControlWire.KindOf"/>); a frame whose kind and verb disagree is malformed. The values are wire format and never change.
/// </summary>
public enum ControlVerb : byte
{
    // Handoff (kind 1): sent to the active world A or the standby B.
    Describe = 1,
    Warm = 2,
    StopAdmissions = 3,
    Freeze = 4,
    Flush = 5,
    Resume = 6,
    ReleaseWriterLock = 7,
    Retire = 8,
    Promote = 9,

    // Transfer (kind 2): sent to the target or the source worker of one transfer.
    TargetDescribe = 17,
    Reserve = 18,
    Install = 19,
    Activate = 20,
    Discard = 21,
    Suspend = 22,
    ResumeSource = 23,
    RetireSource = 24,
}

/// <summary>The worker's verdict on one control request.</summary>
public enum ControlStatus : byte
{
    Ok = 1,

    /// <summary>The journal row or the worker's state does not allow the verb now (nothing was changed).</summary>
    Refused = 2,

    /// <summary>The verb ran and failed.</summary>
    Failed = 3,

    /// <summary>The worker's opt-in key for this kind is off.</summary>
    Disabled = 4,
}

/// <summary>What a worker asks the gateway to start (<see cref="GatewayWorkerFrameType.ControlSubmit"/>).</summary>
public enum ControlSubmitKind : byte
{
    /// <summary>An operator's <c>.cluster restart</c>.</summary>
    OperatorRestart = 1,

    /// <summary>A cross-worker transfer request from the player's source worker.</summary>
    TransferRequest = 2,
}

/// <summary>What a <see cref="GatewayWorkerFrameType.ControlNotice"/> tells the worker.</summary>
public enum ControlNoticeKind : byte
{
    /// <summary>Text for the operator who started a restart.</summary>
    OperatorText = 1,

    /// <summary>The outcome of a player transfer.</summary>
    TransferOutcome = 2,
}

/// <summary>The result a <see cref="ControlNotice"/> reports. <see cref="None"/> for plain operator text.</summary>
public enum ControlNoticeOutcome : byte
{
    None = 0,
    Completed = 1,
    Aborted = 2,
    Refused = 3,
    Failed = 4,
}

/// <summary>Option bits of a transfer request (<see cref="ControlTransferSubmit.Options"/>).</summary>
[Flags]
public enum ControlTransferOptions : byte
{
    None = 0,

    /// <summary>The player keeps the transport it stands on (the retail <c>TELE_TO_NOT_LEAVE_TRANSPORT</c> option).</summary>
    NotLeaveTransport = 1,
}

/// <summary>Verb arguments of a <see cref="ControlRequest"/>. The concrete type is fixed by the verb.</summary>
public abstract record ControlArguments;

/// <summary>No arguments: Describe, StopAdmissions, Freeze, Flush, Resume, ReleaseWriterLock, Retire, TargetDescribe, Install (the tokens and bundles come from the journal).</summary>
public sealed record ControlNoArguments : ControlArguments
{
    public static ControlNoArguments Instance { get; } = new();
}

/// <summary>Warm: the incarnation the standby is expected to have.</summary>
public sealed record ControlWarmArguments(Guid ExpectedIncarnation) : ControlArguments;

/// <summary>Promote, Activate and RetireSource: an ownership token (scope, epoch, incarnation).</summary>
public sealed record ControlTokenArguments(CharacterOwnershipToken Token) : ControlArguments;

/// <summary>Reserve: the character and the destination map and instance (instance 0 on an instanced map asks the target to allocate one).</summary>
public sealed record ControlReserveArguments(ulong CharacterGuid, uint MapId, uint InstanceId) : ControlArguments;

/// <summary>Discard and ResumeSource: a free-text reason for logs (cut to <see cref="GatewaySessionWire.MaxTextBytes"/> bytes).</summary>
public sealed record ControlReasonArguments(string Reason) : ControlArguments;

/// <summary>Suspend: the character, the target worker, the destination map and instance and the destination position.</summary>
public sealed record ControlSuspendArguments(ulong CharacterGuid, string TargetWorker, uint MapId, uint InstanceId, TransferPosition Position) : ControlArguments;

/// <summary>Gateway to worker: one verb of one handoff or transfer.</summary>
public sealed record ControlRequest(ulong RequestId, ControlKind Kind, Guid OperationId, ControlVerb Verb, ControlArguments Arguments);

/// <summary>Verb payload of an Ok <see cref="ControlReply"/>. The concrete type is fixed by the verb.</summary>
public abstract record ControlPayload;

/// <summary>No payload.</summary>
public sealed record ControlNoPayload : ControlPayload
{
    public static ControlNoPayload Instance { get; } = new();
}

/// <summary>Handoff Describe: the participant's state the coordinator reads before it begins.</summary>
public sealed record ControlDescribePayload(
    string WorkerId,
    bool IsStandby,
    Guid Incarnation,
    string OwnerNode,
    string OwnershipScope,
    IReadOnlyList<uint> OwnedMaps,
    CharacterOwnershipToken? HeldSourceToken,
    int AdmittedGateways,
    bool HandoffEnabled) : ControlPayload;

/// <summary>
/// TargetDescribe: the target's incarnation and ownership scope, whether it admits sessions, how many reservations it can still take and how many distinct gateways have an admitted link to it
/// (one gateway per realm is the contract: the coordinator refuses a transfer when a worker involved reports more than one).
/// </summary>
public sealed record ControlTargetDescribePayload(Guid Incarnation, string OwnershipScope, bool AdmissionOpen, int FreeReservations, int AdmittedGateways) : ControlPayload;

/// <summary>Reserve: whether the target reserved, and the instance id it reserved under (the target allocates one for an instanced map when the request named 0).</summary>
public sealed record ControlReservePayload(bool Reserved, uint InstanceId) : ControlPayload;

/// <summary>Suspend: the captured bundle as JSON bytes (<c>PlayerTransferBundleJson</c> in the data layer), at most <see cref="ClusterControlWire.MaxBundleBytes"/>.</summary>
public sealed record ControlSuspendPayload(byte[] BundleJson) : ControlPayload;

/// <summary>Worker to gateway: the answer to one <see cref="ControlRequest"/>. A payload rides only on <see cref="ControlStatus.Ok"/>.</summary>
public sealed record ControlReply(ulong RequestId, ControlKind Kind, Guid OperationId, ControlVerb Verb, ControlStatus Status, string Reason, ControlPayload Payload);

/// <summary>Body of a <see cref="ControlSubmit"/>.</summary>
public abstract record ControlSubmitBody
{
    public abstract ControlSubmitKind Kind { get; }
}

/// <summary>An operator restart: who asked (account, the link's session generation, the security level the worker claims) and the command's argument text.</summary>
public sealed record ControlRestartSubmit(int AccountId, long SessionGeneration, byte ClaimedSecurityLevel, string Arguments) : ControlSubmitBody
{
    public override ControlSubmitKind Kind => ControlSubmitKind.OperatorRestart;
}

/// <summary>A transfer request (design section 2.3). The gateway checks that the source node is the node of the link it arrived on and that the link carries <see cref="SessionGeneration"/>.</summary>
public sealed record ControlTransferSubmit(
    Guid TransferId,
    ulong CharacterGuid,
    int AccountId,
    long SessionGeneration,
    uint SourceMapId,
    uint SourceInstanceId,
    uint TargetMapId,
    uint TargetInstanceId,
    TransferPosition TargetPosition,
    ControlTransferOptions Options,
    CharacterOwnershipToken SourceToken) : ControlSubmitBody
{
    public override ControlSubmitKind Kind => ControlSubmitKind.TransferRequest;
}

/// <summary>Worker to gateway: starts an operation.</summary>
public sealed record ControlSubmit(ulong RequestId, ControlSubmitBody Body);

/// <summary>Gateway to worker: whether the gateway accepted a <see cref="ControlSubmit"/> and the operation id it runs under.</summary>
public sealed record ControlSubmitReply(ulong RequestId, bool Accepted, Guid OperationId, string Reason);

/// <summary>Gateway to worker: text for an operator or the outcome of a transfer, addressed by session generation, account and character.</summary>
public sealed record ControlNotice(
    ControlNoticeKind Kind, long SessionGeneration, int AccountId, ulong CharacterGuid, Guid OperationId, ControlNoticeOutcome Outcome, string Text);

/// <summary>
/// Encoding of the unified control frames (<see cref="GatewayWorkerFrameType.ControlRequest"/> to <see cref="GatewayWorkerFrameType.ControlNotice"/>, contract 5):
/// the conventions of <see cref="GatewayTransferWire"/> (little-endian fixed fields, Guid as 16 bytes, text as an int32 length plus UTF-8 of at most
/// <see cref="GatewaySessionWire.MaxTextBytes"/> bytes, the type byte first). The one addition is a length-prefixed byte block for the Suspend bundle
/// (at most <see cref="MaxBundleBytes"/>). Booleans are exactly 0 or 1, enums must be defined, positions finite and epochs not negative. Every decoder
/// checks lengths, refuses trailing bytes and throws <see cref="InvalidDataException"/>, which closes the link. Design: _wf/handoff-control-DESIGN.md section 2.
/// </summary>
public static class ClusterControlWire
{
    /// <summary>The largest bundle JSON a Suspend reply carries (design section 2.2).</summary>
    public const int MaxBundleBytes = 16 * 1024;

    /// <summary>The most maps a Describe payload lists.</summary>
    public const int MaxOwnedMaps = 1024;

    /// <summary>The kind a verb belongs to, or null for a value that is not a verb.</summary>
    public static ControlKind? KindOf(ControlVerb verb) => verb switch
    {
        >= ControlVerb.Describe and <= ControlVerb.Promote => ControlKind.WorldHandoff,
        >= ControlVerb.TargetDescribe and <= ControlVerb.RetireSource => ControlKind.PlayerTransfer,
        _ => null,
    };

    // ------------------------------------------------------------------ ControlRequest (28)

    internal static byte[] EncodeRequest(ControlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        CheckVerb(request.Kind, request.Verb);
        var w = new Writer(GatewayWorkerFrameType.ControlRequest);
        w.UInt64(request.RequestId);
        w.Byte((byte)request.Kind);
        w.Guid(request.OperationId);
        w.Byte((byte)request.Verb);
        switch (request.Verb)
        {
            case ControlVerb.Warm:
                w.Guid(Expect<ControlWarmArguments>(request).ExpectedIncarnation);
                break;
            case ControlVerb.Promote or ControlVerb.Activate or ControlVerb.RetireSource:
                w.Token(Expect<ControlTokenArguments>(request).Token);
                break;
            case ControlVerb.Reserve:
            {
                var args = Expect<ControlReserveArguments>(request);
                w.UInt64(args.CharacterGuid);
                w.UInt32(args.MapId);
                w.UInt32(args.InstanceId);
                break;
            }

            case ControlVerb.Discard or ControlVerb.ResumeSource:
                w.TextTruncated(Expect<ControlReasonArguments>(request).Reason);
                break;
            case ControlVerb.Suspend:
            {
                var args = Expect<ControlSuspendArguments>(request);
                w.UInt64(args.CharacterGuid);
                w.Text(args.TargetWorker);
                w.UInt32(args.MapId);
                w.UInt32(args.InstanceId);
                w.Position(args.Position);
                break;
            }

            default:
                Expect<ControlNoArguments>(request);
                break;
        }

        return w.ToArray();
    }

    /// <summary>Decodes the body (without the type byte) of a <see cref="GatewayWorkerFrameType.ControlRequest"/> frame.</summary>
    internal static ControlRequest DecodeRequest(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body, "ControlRequest");
        ulong requestId = r.UInt64();
        ControlKind kind = r.Kind();
        Guid operationId = r.Guid();
        ControlVerb verb = r.Verb(kind);
        ControlArguments args;
        switch (verb)
        {
            case ControlVerb.Warm:
                args = new ControlWarmArguments(r.Guid());
                break;
            case ControlVerb.Promote or ControlVerb.Activate or ControlVerb.RetireSource:
                args = new ControlTokenArguments(r.Token());
                break;
            case ControlVerb.Reserve:
                args = new ControlReserveArguments(r.UInt64(), r.UInt32(), r.UInt32());
                break;
            case ControlVerb.Discard or ControlVerb.ResumeSource:
                args = new ControlReasonArguments(r.Text());
                break;
            case ControlVerb.Suspend:
                args = new ControlSuspendArguments(r.UInt64(), r.Text(), r.UInt32(), r.UInt32(), r.Position());
                break;
            default:
                args = ControlNoArguments.Instance;
                break;
        }

        r.End();
        return new ControlRequest(requestId, kind, operationId, verb, args);
    }

    // ------------------------------------------------------------------ ControlReply (29)

    internal static byte[] EncodeReply(ControlReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(reply.Payload);
        CheckVerb(reply.Kind, reply.Verb);
        if (!Enum.IsDefined(reply.Status))
        {
            throw new ArgumentException($"unknown control status {(byte)reply.Status}", nameof(reply));
        }

        var w = new Writer(GatewayWorkerFrameType.ControlReply);
        w.UInt64(reply.RequestId);
        w.Byte((byte)reply.Kind);
        w.Guid(reply.OperationId);
        w.Byte((byte)reply.Verb);
        w.Byte((byte)reply.Status);
        w.TextTruncated(reply.Reason);
        if (reply.Status != ControlStatus.Ok)
        {
            if (reply.Payload is not ControlNoPayload)
            {
                throw new ArgumentException("only an Ok reply carries a payload", nameof(reply));
            }

            return w.ToArray();
        }

        switch (reply.Verb)
        {
            case ControlVerb.Describe:
            {
                var p = ExpectPayload<ControlDescribePayload>(reply);
                if (p.OwnedMaps.Count > MaxOwnedMaps)
                {
                    throw new ArgumentException($"{p.OwnedMaps.Count} owned maps is over {MaxOwnedMaps}", nameof(reply));
                }

                if (p.AdmittedGateways < 0)
                {
                    throw new ArgumentException("negative admitted gateway count", nameof(reply));
                }

                w.Text(p.WorkerId);
                w.Bool(p.IsStandby);
                w.Guid(p.Incarnation);
                w.Text(p.OwnerNode);
                w.Text(p.OwnershipScope);
                w.Int32(p.OwnedMaps.Count);
                foreach (uint map in p.OwnedMaps)
                {
                    w.UInt32(map);
                }

                w.Bool(p.HeldSourceToken is not null);
                if (p.HeldSourceToken is not null)
                {
                    w.Token(p.HeldSourceToken);
                }

                w.Int32(p.AdmittedGateways);
                w.Bool(p.HandoffEnabled);
                break;
            }

            case ControlVerb.TargetDescribe:
            {
                var p = ExpectPayload<ControlTargetDescribePayload>(reply);
                if (p.FreeReservations < 0)
                {
                    throw new ArgumentException("negative free reservation count", nameof(reply));
                }

                if (p.AdmittedGateways < 0)
                {
                    throw new ArgumentException("negative admitted gateway count", nameof(reply));
                }

                w.Guid(p.Incarnation);
                w.Text(p.OwnershipScope);
                w.Bool(p.AdmissionOpen);
                w.Int32(p.FreeReservations);
                w.Int32(p.AdmittedGateways);
                break;
            }

            case ControlVerb.Reserve:
            {
                var p = ExpectPayload<ControlReservePayload>(reply);
                w.Bool(p.Reserved);
                w.UInt32(p.InstanceId);
                break;
            }

            case ControlVerb.Suspend:
            {
                var p = ExpectPayload<ControlSuspendPayload>(reply);
                if (p.BundleJson.Length > MaxBundleBytes)
                {
                    throw new ArgumentException($"bundle of {p.BundleJson.Length} B is over {MaxBundleBytes} B", nameof(reply));
                }

                w.Int32(p.BundleJson.Length);
                w.Raw(p.BundleJson);
                break;
            }

            default:
                ExpectPayload<ControlNoPayload>(reply);
                break;
        }

        return w.ToArray();
    }

    /// <summary>Decodes the body (without the type byte) of a <see cref="GatewayWorkerFrameType.ControlReply"/> frame.</summary>
    internal static ControlReply DecodeReply(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body, "ControlReply");
        ulong requestId = r.UInt64();
        ControlKind kind = r.Kind();
        Guid operationId = r.Guid();
        ControlVerb verb = r.Verb(kind);
        ControlStatus status = r.Status();
        string reason = r.Text();
        ControlPayload payload = ControlNoPayload.Instance;
        if (status == ControlStatus.Ok)
        {
            switch (verb)
            {
                case ControlVerb.Describe:
                {
                    string workerId = r.Text();
                    bool standby = r.Bool();
                    Guid incarnation = r.Guid();
                    string owner = r.Text();
                    string scope = r.Text();
                    int mapCount = r.Int32();
                    if (mapCount is < 0 or > MaxOwnedMaps)
                    {
                        throw new InvalidDataException($"ControlReply owned map count {mapCount} out of range");
                    }

                    var maps = new uint[mapCount];
                    for (int i = 0; i < maps.Length; i++)
                    {
                        maps[i] = r.UInt32();
                    }

                    CharacterOwnershipToken? held = r.Bool() ? r.Token() : null;
                    int gateways = r.Int32();
                    if (gateways < 0)
                    {
                        throw new InvalidDataException($"ControlReply negative admitted gateway count {gateways}");
                    }

                    bool handoffEnabled = r.Bool();
                    payload = new ControlDescribePayload(workerId, standby, incarnation, owner, scope, maps, held, gateways, handoffEnabled);
                    break;
                }

                case ControlVerb.TargetDescribe:
                {
                    Guid incarnation = r.Guid();
                    string scope = r.Text();
                    bool open = r.Bool();
                    int free = r.Int32();
                    if (free < 0)
                    {
                        throw new InvalidDataException($"ControlReply negative free reservation count {free}");
                    }

                    int targetGateways = r.Int32();
                    if (targetGateways < 0)
                    {
                        throw new InvalidDataException($"ControlReply negative admitted gateway count {targetGateways}");
                    }

                    payload = new ControlTargetDescribePayload(incarnation, scope, open, free, targetGateways);
                    break;
                }

                case ControlVerb.Reserve:
                {
                    bool reserved = r.Bool();
                    payload = new ControlReservePayload(reserved, r.UInt32());
                    break;
                }

                case ControlVerb.Suspend:
                {
                    int length = r.Int32();
                    if (length is < 0 or > MaxBundleBytes)
                    {
                        throw new InvalidDataException($"ControlReply bundle length {length} out of range");
                    }

                    payload = new ControlSuspendPayload(r.Raw(length).ToArray());
                    break;
                }
            }
        }

        r.End();
        return new ControlReply(requestId, kind, operationId, verb, status, reason, payload);
    }

    // ------------------------------------------------------------------ ControlSubmit (30)

    internal static byte[] EncodeSubmit(ControlSubmit submit)
    {
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(submit.Body);
        var w = new Writer(GatewayWorkerFrameType.ControlSubmit);
        w.UInt64(submit.RequestId);
        w.Byte((byte)submit.Body.Kind);
        switch (submit.Body)
        {
            case ControlRestartSubmit restart:
                w.Int32(restart.AccountId);
                w.Int64(restart.SessionGeneration);
                w.Byte(restart.ClaimedSecurityLevel);
                w.TextTruncated(restart.Arguments);
                break;
            case ControlTransferSubmit transfer:
                if (transfer.SourceToken is null)
                {
                    throw new ArgumentException("a transfer request needs a source token", nameof(submit));
                }

                w.Guid(transfer.TransferId);
                w.UInt64(transfer.CharacterGuid);
                w.Int32(transfer.AccountId);
                w.Int64(transfer.SessionGeneration);
                w.UInt32(transfer.SourceMapId);
                w.UInt32(transfer.SourceInstanceId);
                w.UInt32(transfer.TargetMapId);
                w.UInt32(transfer.TargetInstanceId);
                w.Position(transfer.TargetPosition);
                w.Byte((byte)transfer.Options);
                w.Token(transfer.SourceToken);
                break;
            default:
                throw new ArgumentException("unknown control submit body " + submit.Body.GetType().Name, nameof(submit));
        }

        return w.ToArray();
    }

    /// <summary>Decodes the body (without the type byte) of a <see cref="GatewayWorkerFrameType.ControlSubmit"/> frame.</summary>
    internal static ControlSubmit DecodeSubmit(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body, "ControlSubmit");
        ulong requestId = r.UInt64();
        byte kind = r.Byte();
        ControlSubmitBody result;
        switch ((ControlSubmitKind)kind)
        {
            case ControlSubmitKind.OperatorRestart:
            {
                int accountId = r.Int32();
                long generation = r.Int64();
                byte security = r.Byte();
                result = new ControlRestartSubmit(accountId, generation, security, r.Text());
                break;
            }

            case ControlSubmitKind.TransferRequest:
            {
                Guid transferId = r.Guid();
                ulong characterGuid = r.UInt64();
                int accountId = r.Int32();
                long generation = r.Int64();
                uint sourceMap = r.UInt32();
                uint sourceInstance = r.UInt32();
                uint targetMap = r.UInt32();
                uint targetInstance = r.UInt32();
                TransferPosition position = r.Position();
                byte options = r.Byte();
                if ((options & ~(byte)ControlTransferOptions.NotLeaveTransport) != 0)
                {
                    throw new InvalidDataException($"ControlSubmit unknown transfer option bits 0x{options:X2}");
                }

                CharacterOwnershipToken token = r.Token();
                result = new ControlTransferSubmit(
                    transferId, characterGuid, accountId, generation, sourceMap, sourceInstance, targetMap, targetInstance, position,
                    (ControlTransferOptions)options, token);
                break;
            }

            default:
                throw new InvalidDataException($"ControlSubmit with unknown kind {kind}");
        }

        r.End();
        return new ControlSubmit(requestId, result);
    }

    // ------------------------------------------------------------------ ControlSubmitReply (31)

    internal static byte[] EncodeSubmitReply(ControlSubmitReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var w = new Writer(GatewayWorkerFrameType.ControlSubmitReply);
        w.UInt64(reply.RequestId);
        w.Bool(reply.Accepted);
        w.Guid(reply.OperationId);
        w.TextTruncated(reply.Reason);
        return w.ToArray();
    }

    internal static ControlSubmitReply DecodeSubmitReply(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body, "ControlSubmitReply");
        ulong requestId = r.UInt64();
        bool accepted = r.Bool();
        Guid operationId = r.Guid();
        string reason = r.Text();
        r.End();
        return new ControlSubmitReply(requestId, accepted, operationId, reason);
    }

    // ------------------------------------------------------------------ ControlNotice (32)

    internal static byte[] EncodeNotice(ControlNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!Enum.IsDefined(notice.Kind))
        {
            throw new ArgumentException($"unknown notice kind {(byte)notice.Kind}", nameof(notice));
        }

        if (!Enum.IsDefined(notice.Outcome))
        {
            throw new ArgumentException($"unknown notice outcome {(byte)notice.Outcome}", nameof(notice));
        }

        var w = new Writer(GatewayWorkerFrameType.ControlNotice);
        w.Byte((byte)notice.Kind);
        w.Int64(notice.SessionGeneration);
        w.Int32(notice.AccountId);
        w.UInt64(notice.CharacterGuid);
        w.Guid(notice.OperationId);
        w.Byte((byte)notice.Outcome);
        w.TextTruncated(notice.Text);
        return w.ToArray();
    }

    internal static ControlNotice DecodeNotice(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body, "ControlNotice");
        byte kind = r.Byte();
        if (!Enum.IsDefined((ControlNoticeKind)kind))
        {
            throw new InvalidDataException($"ControlNotice with unknown kind {kind}");
        }

        long generation = r.Int64();
        int accountId = r.Int32();
        ulong characterGuid = r.UInt64();
        Guid operationId = r.Guid();
        byte outcome = r.Byte();
        if (!Enum.IsDefined((ControlNoticeOutcome)outcome))
        {
            throw new InvalidDataException($"ControlNotice with unknown outcome {outcome}");
        }

        string text = r.Text();
        r.End();
        return new ControlNotice((ControlNoticeKind)kind, generation, accountId, characterGuid, operationId, (ControlNoticeOutcome)outcome, text);
    }

    // ------------------------------------------------------------------ helpers

    private static void CheckVerb(ControlKind kind, ControlVerb verb)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"unknown control kind {(byte)kind}");
        }

        if (KindOf(verb) != kind)
        {
            throw new ArgumentException($"verb {verb} does not belong to kind {kind}");
        }
    }

    private static T Expect<T>(ControlRequest request) where T : ControlArguments =>
        request.Arguments as T ?? throw new ArgumentException($"verb {request.Verb} takes {typeof(T).Name}, not {request.Arguments.GetType().Name}", nameof(request));

    private static T ExpectPayload<T>(ControlReply reply) where T : ControlPayload =>
        reply.Payload as T ?? throw new ArgumentException($"verb {reply.Verb} replies with {typeof(T).Name}, not {reply.Payload.GetType().Name}", nameof(reply));

    private sealed class Writer
    {
        private byte[] _buffer = new byte[64];
        private int _offset;

        public Writer(GatewayWorkerFrameType type) => Byte((byte)type);

        public byte[] ToArray() => _buffer.AsSpan(0, _offset).ToArray();

        private Span<byte> Reserve(int count)
        {
            if (_buffer.Length - _offset < count)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _offset + count));
            }

            Span<byte> slice = _buffer.AsSpan(_offset, count);
            _offset += count;
            return slice;
        }

        public void Byte(byte value) => Reserve(1)[0] = value;

        public void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

        public void Int32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

        public void UInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

        public void UInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

        public void Int64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

        public void Single(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

        public void Guid(Guid value) => value.TryWriteBytes(Reserve(16), bigEndian: false, out _);

        public void Raw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

        /// <summary>Text that must fit: an id, a scope, a node name. Longer text is a programming error.</summary>
        public void Text(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > GatewaySessionWire.MaxTextBytes)
            {
                throw new ArgumentException($"text of {bytes.Length} B is over {GatewaySessionWire.MaxTextBytes} B in a control frame", nameof(text));
            }

            Int32(bytes.Length);
            Raw(bytes);
        }

        /// <summary>Free text for logs and players: cut at the byte limit (a cut inside a multi-byte character decodes to a replacement character).</summary>
        public void TextTruncated(string? text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            if (bytes.Length > GatewaySessionWire.MaxTextBytes)
            {
                bytes = bytes[..GatewaySessionWire.MaxTextBytes];
            }

            Int32(bytes.Length);
            Raw(bytes);
        }

        public void Token(CharacterOwnershipToken token)
        {
            ArgumentNullException.ThrowIfNull(token);
            if (token.Epoch < 0)
            {
                throw new ArgumentException("negative ownership epoch", nameof(token));
            }

            Text(token.Scope);
            Int64(token.Epoch);
            Guid(token.Incarnation);
        }

        public void Position(TransferPosition position)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(position.Orientation))
            {
                throw new ArgumentException("a control frame position must be finite", nameof(position));
            }

            Single(position.X);
            Single(position.Y);
            Single(position.Z);
            Single(position.Orientation);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> body, string frameName)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        private readonly string _frameName = frameName;
        private int _offset;

        public ReadOnlySpan<byte> Raw(int count)
        {
            if (count < 0 || count > _body.Length - _offset)
            {
                throw new InvalidDataException($"truncated {_frameName} frame ({_body.Length} B)");
            }

            ReadOnlySpan<byte> slice = _body.Slice(_offset, count);
            _offset += count;
            return slice;
        }

        public byte Byte() => Raw(1)[0];

        public bool Bool()
        {
            byte value = Byte();
            return value switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException($"{_frameName} boolean byte {value} is not 0 or 1"),
            };
        }

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Raw(4));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Raw(4));

        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Raw(8));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Raw(8));

        public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Raw(4));

        public Guid Guid() => new(Raw(16), bigEndian: false);

        public string Text()
        {
            int length = Int32();
            if (length is < 0 or > GatewaySessionWire.MaxTextBytes)
            {
                throw new InvalidDataException($"{_frameName} text length {length} out of range");
            }

            return Encoding.UTF8.GetString(Raw(length));
        }

        public CharacterOwnershipToken Token()
        {
            string scope = Text();
            long epoch = Int64();
            if (epoch < 0)
            {
                throw new InvalidDataException($"{_frameName} negative ownership epoch {epoch}");
            }

            return new CharacterOwnershipToken(scope, epoch, Guid());
        }

        public TransferPosition Position()
        {
            float x = Single(), y = Single(), z = Single(), o = Single();
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(o))
            {
                throw new InvalidDataException($"{_frameName} position is not finite");
            }

            return new TransferPosition(x, y, z, o);
        }

        public ControlKind Kind()
        {
            byte value = Byte();
            if (value is not ((byte)ControlKind.WorldHandoff or (byte)ControlKind.PlayerTransfer))
            {
                throw new InvalidDataException($"{_frameName} with unknown kind {value}");
            }

            return (ControlKind)value;
        }

        public ControlVerb Verb(ControlKind kind)
        {
            byte value = Byte();
            var verb = (ControlVerb)value;
            if (KindOf(verb) != kind)
            {
                throw new InvalidDataException($"{_frameName} verb {value} does not belong to kind {kind}");
            }

            return verb;
        }

        public ControlStatus Status()
        {
            byte value = Byte();
            if (!Enum.IsDefined((ControlStatus)value))
            {
                throw new InvalidDataException($"{_frameName} with unknown status {value}");
            }

            return (ControlStatus)value;
        }

        public readonly void End()
        {
            if (_offset != _body.Length)
            {
                throw new InvalidDataException($"{_frameName} frame has {_body.Length - _offset} trailing bytes");
            }
        }
    }
}
