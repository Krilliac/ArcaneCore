using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// A gateway's request to open a session on a worker: the client already proved the session key to the gateway
/// (CMSG_AUTH_SESSION digest). <see cref="SessionGeneration"/> is the gateway's own monotonic id of the client connection and
/// keys every later frame of the session; <see cref="ClientEndpoint"/> is the client's address ("ip:port") for per-IP guards.
/// </summary>
public sealed record GatewaySessionOpenRequest(long SessionGeneration, int AccountId, string AccountName, uint Build, string ClientEndpoint);

/// <summary>
/// The worker's answer to <see cref="GatewaySessionOpenRequest"/>. Accepted: <see cref="OwnerEpoch"/> stamps every packet
/// of the session. Refused: <see cref="RefusalCode"/> is the SMSG_AUTH_RESPONSE code the gateway sends to the client in plain text.
/// </summary>
public readonly record struct GatewaySessionOpenReply(bool Accepted, AuthResponseCode RefusalCode, long OwnerEpoch, string? Reason)
{
    public static GatewaySessionOpenReply Accept(long ownerEpoch) => new(true, AuthResponseCode.Ok, ownerEpoch, null);

    public static GatewaySessionOpenReply Refuse(AuthResponseCode code, string reason) => new(false, code, 0, reason);
}

/// <summary>
/// The Warden posture a gateway reports with every verdict: what it enabled and which module, options and check revision it
/// runs. The worker compares it with its own expected posture (<c>GatewayWardenManifestVerifier.VerifyPeerGatewayManifest</c>)
/// and refuses a verdict from a gateway that does not match, so every gateway of a realm scans identically.
/// </summary>
public sealed record GatewayWardenPosture(bool Enabled, string ModuleDigestHex, string OptionsSummary, int CheckRevision);

/// <summary>
/// The login worker's request (worker to gateway) to move a session to the worker that owns the saved map of the character the client
/// asked to log in. Sent before any world state exists for the character: <see cref="MapId"/> and <see cref="InstanceId"/> name the map copy
/// the gateway resolves through its route table.
/// </summary>
public sealed record GatewaySessionRedirect(long SessionGeneration, int AccountId, uint MapId, uint InstanceId);

/// <summary>
/// The gateway's request (gateway to worker) to re-open a redirected session on the map owner: the usual open request (the new generation, the
/// authenticated account) plus the CMSG_PLAYER_LOGIN the client sent, which the gateway buffered. <see cref="LoginReceivedMs"/> is the gateway's
/// receive time of that packet (its monotonic millisecond clock, low 32 bits). The worker answers with a <see cref="GatewaySessionOpenReply"/>.
/// </summary>
public sealed record GatewaySessionAdoptRequest(GatewaySessionOpenRequest Open, uint LoginOpcode, ReadOnlyMemory<byte> LoginPayload, uint LoginReceivedMs);

/// <summary>What a worker reports about itself on <see cref="GatewayWorkerFrameType.WorkerStatus"/> frames.</summary>
[Flags]
public enum WorkerStatusFlags : byte
{
    None = 0,

    /// <summary>The worker is a standby that was not promoted: its ticks and sessions do not count for the realm.</summary>
    Standby = 1,

    /// <summary>A restart countdown is running on the worker.</summary>
    Restarting = 2,

    /// <summary>A shutdown countdown is running on the worker.</summary>
    ShuttingDown = 4,
}

/// <summary>
/// The worker's liveness report to the gateway (docs/ops/realm-liveness.md section 7). <see cref="TicksStarted"/> is the world loop's tick counter
/// (it advances while the loop runs), <see cref="Sessions"/> the sessions registered on this worker (bots included), <see cref="PlayerLimit"/> the
/// player limit in force (0: none; the realm population is 2 x total sessions / limit) and <see cref="Flags"/> the standby and countdown state.
/// </summary>
public readonly record struct WorkerStatusReport(long TicksStarted, int Sessions, int PlayerLimit, WorkerStatusFlags Flags)
{
    public bool Standby => (Flags & WorkerStatusFlags.Standby) != 0;

    public bool Restarting => (Flags & WorkerStatusFlags.Restarting) != 0;

    public bool ShuttingDown => (Flags & WorkerStatusFlags.ShuttingDown) != 0;
}

/// <summary>
/// Encoding of the Tier 3a session-link frames (<c>docs/design/hot-reload-tiers.md</c> §5.3). They ride the length-prefixed
/// framing and the manifest handshake of <c>LoopbackGatewayWorkerHost</c>, and every packet frame carries the same
/// <see cref="ClusterSessionPacket"/> envelope (generation, owner epoch, sequence). All integers little-endian. Every decoder
/// checks lengths and throws <see cref="InvalidDataException"/> on a frame that does not fit, which closes the link.
/// </summary>
public static class GatewaySessionWire
{
    /// <summary>Bytes of the link challenge nonce and of its HMAC-SHA256 proof.</summary>
    public const int NonceBytes = 32;

    /// <summary>Longest account name or endpoint text a SessionOpen may carry.</summary>
    public const int MaxTextBytes = 256;

    /// <summary>Longest buffered client packet a SessionAdopt may carry (CMSG_PLAYER_LOGIN is 8 bytes).</summary>
    public const int MaxAdoptPacketBytes = 256;

    internal static byte[] EncodeOpen(long requestId, GatewaySessionOpenRequest request) => EncodeOpenBody(GatewayWorkerFrameType.SessionOpen, requestId, request, 0);

    /// <summary>The open request layout shared by SessionOpen and SessionAdopt (which appends its tail in <paramref name="tailBytes"/> more bytes).</summary>
    private static byte[] EncodeOpenBody(GatewayWorkerFrameType type, long requestId, GatewaySessionOpenRequest request, int tailBytes)
    {
        byte[] name = Encoding.UTF8.GetBytes(request.AccountName);
        byte[] endpoint = Encoding.UTF8.GetBytes(request.ClientEndpoint);
        if (name.Length > MaxTextBytes || endpoint.Length > MaxTextBytes)
        {
            throw new ArgumentException("account name or client endpoint too long for a " + type + " frame", nameof(request));
        }

        byte[] buffer = new byte[1 + 8 + 8 + 4 + 4 + 4 + name.Length + 4 + endpoint.Length + tailBytes];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)type);
        writer.Int64(requestId);
        writer.Int64(request.SessionGeneration);
        writer.Int32(request.AccountId);
        writer.UInt32(request.Build);
        writer.Text(name);
        writer.Text(endpoint);
        return buffer;
    }

    internal static (long RequestId, GatewaySessionOpenRequest Request) DecodeOpen(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "SessionOpen");
        long requestId = reader.Int64();
        return (requestId, ReadOpenRequest(ref reader));
    }

    private static GatewaySessionOpenRequest ReadOpenRequest(ref FrameReader reader)
    {
        long generation = reader.Int64();
        int accountId = reader.Int32();
        uint build = reader.UInt32();
        string name = reader.Text();
        string endpoint = reader.Text();
        return new GatewaySessionOpenRequest(generation, accountId, name, build, endpoint);
    }

    /// <summary>
    /// A SessionAdopt frame (gateway to worker): the SessionOpen layout exactly as <see cref="EncodeOpen"/> writes it, then the buffered login
    /// packet: opcode (uint32), the gateway receive time (uint32), payload length (int32, at most <see cref="MaxAdoptPacketBytes"/>) and the payload.
    /// </summary>
    internal static byte[] EncodeAdopt(long requestId, GatewaySessionAdoptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlySpan<byte> payload = request.LoginPayload.Span;
        if (payload.Length > MaxAdoptPacketBytes)
        {
            throw new ArgumentException($"buffered login packet of {payload.Length} B is over {MaxAdoptPacketBytes} B", nameof(request));
        }

        int tail = 4 + 4 + 4 + payload.Length;
        byte[] buffer = EncodeOpenBody(GatewayWorkerFrameType.SessionAdopt, requestId, request.Open, tail);
        int offset = buffer.Length - tail;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), request.LoginOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4, 4), request.LoginReceivedMs);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset + 8, 4), payload.Length);
        payload.CopyTo(buffer.AsSpan(offset + 12));
        return buffer;
    }

    internal static (long RequestId, GatewaySessionAdoptRequest Request) DecodeAdopt(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "SessionAdopt");
        long requestId = reader.Int64();
        GatewaySessionOpenRequest open = ReadOpenRequest(ref reader);
        uint opcode = reader.UInt32();
        uint receivedMs = reader.UInt32();
        byte[] payload = reader.Bytes(MaxAdoptPacketBytes);
        reader.End();
        return (requestId, new GatewaySessionAdoptRequest(open, opcode, payload, receivedMs));
    }

    /// <summary>A SessionRedirect frame (worker to gateway): generation, account, map id and instance id of the map copy that owns the character.</summary>
    internal static byte[] EncodeRedirect(GatewaySessionRedirect redirect)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        byte[] buffer = new byte[1 + 8 + 4 + 4 + 4];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.SessionRedirect);
        writer.Int64(redirect.SessionGeneration);
        writer.Int32(redirect.AccountId);
        writer.UInt32(redirect.MapId);
        writer.UInt32(redirect.InstanceId);
        return buffer;
    }

    internal static GatewaySessionRedirect DecodeRedirect(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "SessionRedirect");
        long generation = reader.Int64();
        int accountId = reader.Int32();
        uint mapId = reader.UInt32();
        uint instanceId = reader.UInt32();
        reader.End();
        return new GatewaySessionRedirect(generation, accountId, mapId, instanceId);
    }

    internal static byte[] EncodeOpenReply(long requestId, GatewaySessionOpenReply reply)
    {
        byte[] reason = Encoding.UTF8.GetBytes(reply.Reason ?? string.Empty);
        if (reason.Length > MaxTextBytes)
        {
            reason = reason[..MaxTextBytes];
        }

        byte[] buffer = new byte[1 + 8 + 1 + 1 + 8 + 4 + reason.Length];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.SessionOpenReply);
        writer.Int64(requestId);
        writer.Byte(reply.Accepted ? (byte)1 : (byte)0);
        writer.Byte((byte)reply.RefusalCode);
        writer.Int64(reply.OwnerEpoch);
        writer.Text(reason);
        return buffer;
    }

    internal static (long RequestId, GatewaySessionOpenReply Reply) DecodeOpenReply(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "SessionOpenReply");
        long requestId = reader.Int64();
        bool accepted = reader.Byte() != 0;
        var code = (AuthResponseCode)reader.Byte();
        long epoch = reader.Int64();
        string reason = reader.Text();
        return (requestId, new GatewaySessionOpenReply(accepted, code, epoch, reason.Length == 0 ? null : reason));
    }

    /// <summary>A client packet (gateway to worker) or a server packet (worker to gateway) in the session envelope.</summary>
    internal static byte[] EncodePacket(GatewayWorkerFrameType type, ClusterSessionPacket packet)
    {
        byte[] buffer = new byte[1 + GatewayLinkFraming.SessionPacketHeaderBytes + packet.Payload.Length];
        buffer[0] = (byte)type;
        GatewayLinkFraming.WriteSessionPacketBody(buffer.AsSpan(1), packet);
        return buffer;
    }

    /// <summary><see cref="EncodePacket(GatewayWorkerFrameType, ClusterSessionPacket)"/> from a span payload (one copy, into the frame).</summary>
    internal static byte[] EncodePacket(
        GatewayWorkerFrameType type, long sessionGeneration, int accountId, ulong characterGuid, long ownerEpoch, long sequence,
        uint opcode, ReadOnlySpan<byte> payload)
    {
        byte[] buffer = new byte[1 + GatewayLinkFraming.SessionPacketHeaderBytes + payload.Length];
        Span<byte> span = buffer;
        span[0] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), sessionGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), accountId);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(13, 8), characterGuid);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(21, 8), ownerEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(29, 8), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(37, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(41, 4), payload.Length);
        payload.CopyTo(span[45..]);
        return buffer;
    }

    /// <summary>Bytes the gateway receive time adds after the payload of a stamped SessionClientPacket frame.</summary>
    public const int ReceiveStampBytes = 4;

    /// <summary>
    /// A SessionClientPacket frame (gateway to worker): the session envelope and payload exactly as <see cref="EncodePacket(GatewayWorkerFrameType, long, int, ulong, long, long, uint, ReadOnlySpan{byte})"/>
    /// writes them, then the gateway's receive time of the client packet (uint32, the gateway's monotonic millisecond clock) appended
    /// after the payload. The worker translates it with the offset of the <see cref="GatewayWorkerFrameType.LinkClock"/> frame. Gateway
    /// and worker agree on this layout by the link contract version (<see cref="GatewayLinkManifest.ContractVersion"/>).
    /// </summary>
    internal static byte[] EncodeClientPacket(
        long sessionGeneration, int accountId, ulong characterGuid, long ownerEpoch, long sequence, uint opcode, ReadOnlySpan<byte> payload,
        uint gatewayReceivedMs)
    {
        byte[] buffer = new byte[1 + GatewayLinkFraming.SessionPacketHeaderBytes + payload.Length + ReceiveStampBytes];
        Span<byte> span = buffer;
        span[0] = (byte)GatewayWorkerFrameType.SessionClientPacket;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), sessionGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), accountId);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(13, 8), characterGuid);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(21, 8), ownerEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(29, 8), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(37, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(41, 4), payload.Length);
        payload.CopyTo(span[45..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(45 + payload.Length)..], gatewayReceivedMs);
        return buffer;
    }

    /// <summary><see cref="EncodeClientPacket(long, int, ulong, long, long, uint, ReadOnlySpan{byte}, uint)"/> for an envelope already built (test seam and replays).</summary>
    internal static byte[] EncodeClientPacket(ClusterSessionPacket packet, uint gatewayReceivedMs) =>
        EncodeClientPacket(packet.SessionGeneration, packet.AccountId, packet.CharacterGuid, packet.OwnerEpoch, packet.Sequence, packet.Opcode,
            packet.Payload.Span, gatewayReceivedMs);

    /// <summary>
    /// The envelope and the gateway receive time of a SessionClientPacket frame body (the frame without its type byte). The frame must
    /// be exactly the envelope, its payload and the 4-byte stamp: anything shorter or longer is refused, which closes the link.
    /// </summary>
    internal static ClusterSessionPacket DecodeClientPacket(ReadOnlyMemory<byte> body, out uint gatewayReceivedMs)
    {
        ClusterSessionPacket packet = GatewayLinkFraming.DeserializeSessionPacket(body);
        int expected = GatewayLinkFraming.SessionPacketHeaderBytes + packet.Payload.Length + ReceiveStampBytes;
        if (body.Length != expected)
        {
            throw new InvalidDataException($"SessionClientPacket frame of {body.Length} B (expected {expected}: envelope, payload and the receive stamp)");
        }

        gatewayReceivedMs = BinaryPrimitives.ReadUInt32LittleEndian(body.Span[(expected - ReceiveStampBytes)..]);
        return packet;
    }

    /// <summary>
    /// The gateway's clock reading (gateway to worker), sent once right after the link is admitted and before any other frame. Body: the
    /// gateway's monotonic millisecond clock as uint32. The worker pairs it with its own clock at arrival to get the offset it translates
    /// every stamped client packet with.
    /// </summary>
    internal static byte[] EncodeLinkClock(uint gatewayMs)
    {
        byte[] buffer = new byte[1 + 4];
        buffer[0] = (byte)GatewayWorkerFrameType.LinkClock;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1), gatewayMs);
        return buffer;
    }

    internal static uint DecodeLinkClock(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
        {
            throw new InvalidDataException($"LinkClock frame of {body.Length} B (expected 4)");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(body);
    }

    /// <summary>
    /// The slot a worker link serves (gateway to worker), sent once right after the link is admitted, before any session or control frame. Body: the slot's <c>Gateway:Workers:N:NodeId</c> as length-prefixed
    /// UTF-8. The worker answers transfers under this name (journal rows, bundles and transfer verbs name the slot), whatever its own <c>Cluster:Worker:NodeId</c> is.
    /// </summary>
    internal static byte[] EncodeLinkSlot(string slotNode)
    {
        byte[] name = Encoding.UTF8.GetBytes(slotNode);
        if (name.Length is 0 or > MaxTextBytes)
        {
            throw new ArgumentException($"a slot name of {name.Length} B does not fit a LinkSlot frame (1 to {MaxTextBytes} B)", nameof(slotNode));
        }

        byte[] buffer = new byte[1 + 4 + name.Length];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.LinkSlot);
        writer.Text(name);
        return buffer;
    }

    internal static string DecodeLinkSlot(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "LinkSlot");
        string slotNode = reader.Text();
        reader.End();
        if (string.IsNullOrWhiteSpace(slotNode))
        {
            throw new InvalidDataException("LinkSlot frame names no slot");
        }

        return slotNode;
    }

    /// <summary>The envelope of a packet frame body; the payload is a slice of <paramref name="body"/> (no copy).</summary>
    internal static ClusterSessionPacket DecodePacket(ReadOnlyMemory<byte> body) => GatewayLinkFraming.DeserializeSessionPacket(body);

    internal static byte[] EncodeClose(long sessionGeneration, int accountId, ClusterSessionDetachReason reason)
    {
        byte[] buffer = new byte[1 + 8 + 4 + 1];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.SessionClose);
        writer.Int64(sessionGeneration);
        writer.Int32(accountId);
        writer.Byte((byte)reason);
        return buffer;
    }

    internal static (long SessionGeneration, int AccountId, ClusterSessionDetachReason Reason) DecodeClose(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "SessionClose");
        return (reader.Int64(), reader.Int32(), (ClusterSessionDetachReason)reader.Byte());
    }

    internal static byte[] EncodeNonceFrame(GatewayWorkerFrameType type, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != NonceBytes)
        {
            throw new ArgumentException($"expected {NonceBytes} bytes", nameof(bytes));
        }

        byte[] buffer = new byte[1 + NonceBytes];
        buffer[0] = (byte)type;
        bytes.CopyTo(buffer.AsSpan(1));
        return buffer;
    }

    internal static byte[] DecodeNonceFrame(ReadOnlySpan<byte> body, string frameName)
    {
        if (body.Length != NonceBytes)
        {
            throw new InvalidDataException($"{frameName} frame of {body.Length} B (expected {NonceBytes})");
        }

        return body.ToArray();
    }

    /// <summary>The gateway's answer to a worker's challenge: HMAC-SHA256 keyed by the shared link secret over the nonce.</summary>
    public static byte[] ComputeProof(string linkSecret, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(linkSecret);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(linkSecret), nonce);
    }

    /// <summary>
    /// A Warden verdict of the gateway (gateway to worker). Carries the session generation, the owner epoch the gateway holds for the
    /// session, the account, the verdict's key (<see cref="ClusterWardenVerdict.CharacterGuid"/>: the gateway has no character, so it
    /// sends the account id there), the check, the action, a reason clamped to <see cref="MaxTextBytes"/> bytes and the gateway's
    /// <see cref="GatewayWardenPosture"/>.
    /// </summary>
    internal static byte[] EncodeWardenVerdict(ClusterWardenVerdict verdict, GatewayWardenPosture posture)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(posture);
        byte[] reason = Encoding.UTF8.GetBytes(ClampText(verdict.Reason));
        byte[] digest = Encoding.UTF8.GetBytes(posture.ModuleDigestHex);
        byte[] options = Encoding.UTF8.GetBytes(posture.OptionsSummary);
        if (digest.Length > MaxTextBytes || options.Length > MaxTextBytes)
        {
            throw new ArgumentException("Warden posture text too long for a WardenVerdict frame", nameof(posture));
        }

        byte[] buffer = new byte[1 + 8 + 8 + 4 + 8 + 2 + 1 + 1 + 4 + 4 + reason.Length + 4 + digest.Length + 4 + options.Length];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.WardenVerdict);
        writer.Int64(verdict.SessionGeneration);
        writer.Int64(verdict.OwnerEpoch);
        writer.Int32(verdict.AccountId);
        writer.Int64(unchecked((long)verdict.CharacterGuid));
        writer.UInt16(verdict.CheckId);
        writer.Byte((byte)verdict.Action);
        writer.Byte(posture.Enabled ? (byte)1 : (byte)0);
        writer.Int32(posture.CheckRevision);
        writer.Text(reason);
        writer.Text(digest);
        writer.Text(options);
        return buffer;
    }

    internal static (ClusterWardenVerdict Verdict, GatewayWardenPosture Posture) DecodeWardenVerdict(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "WardenVerdict");
        long generation = reader.Int64();
        long epoch = reader.Int64();
        int accountId = reader.Int32();
        ulong key = unchecked((ulong)reader.Int64());
        ushort checkId = reader.UInt16();
        byte action = reader.Byte();
        bool enabled = reader.Byte() != 0;
        int revision = reader.Int32();
        string reason = reader.Text();
        string digest = reader.Text();
        string options = reader.Text();
        if (!Enum.IsDefined((ClusterWardenAction)action))
        {
            throw new InvalidDataException($"WardenVerdict action {action} is not defined");
        }

        return (new ClusterWardenVerdict(generation, epoch, accountId, key, reason, (ClusterWardenAction)action, checkId),
            new GatewayWardenPosture(enabled, digest, options, revision));
    }

    /// <summary>
    /// The gateway's Warden posture (gateway to worker), sent once right after the link is admitted and before any SessionOpen. The worker
    /// compares it with its own expected posture and refuses the link on a mismatch, so a gateway that scans differently (or not at all)
    /// never carries a session. Body: enabled byte, check revision, digest text, options text.
    /// </summary>
    internal static byte[] EncodeWardenPosture(GatewayWardenPosture posture)
    {
        ArgumentNullException.ThrowIfNull(posture);
        byte[] digest = Encoding.UTF8.GetBytes(posture.ModuleDigestHex);
        byte[] options = Encoding.UTF8.GetBytes(posture.OptionsSummary);
        if (digest.Length > MaxTextBytes || options.Length > MaxTextBytes)
        {
            throw new ArgumentException("Warden posture text too long for a WardenPosture frame", nameof(posture));
        }

        byte[] buffer = new byte[1 + 1 + 4 + 4 + digest.Length + 4 + options.Length];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.WardenPosture);
        writer.Byte(posture.Enabled ? (byte)1 : (byte)0);
        writer.Int32(posture.CheckRevision);
        writer.Text(digest);
        writer.Text(options);
        return buffer;
    }

    internal static GatewayWardenPosture DecodeWardenPosture(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "WardenPosture");
        bool enabled = reader.Byte() != 0;
        int revision = reader.Int32();
        string digest = reader.Text();
        string options = reader.Text();
        return new GatewayWardenPosture(enabled, digest, options, revision);
    }

    /// <summary>Bytes of a WorkerStatus frame body (without the type byte): ticks (int64), sessions (int32), player limit (int32), flags (byte).</summary>
    public const int WorkerStatusBodyBytes = 8 + 4 + 4 + 1;

    /// <summary>A WorkerStatus frame (worker to gateway): type, ticks started, sessions, player limit, flags. Negative counts are sent as 0.</summary>
    internal static byte[] EncodeWorkerStatus(WorkerStatusReport report)
    {
        byte[] buffer = new byte[1 + WorkerStatusBodyBytes];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.WorkerStatus);
        writer.Int64(Math.Max(report.TicksStarted, 0));
        writer.Int32(Math.Max(report.Sessions, 0));
        writer.Int32(Math.Max(report.PlayerLimit, 0));
        writer.Byte((byte)report.Flags);
        return buffer;
    }

    /// <summary>The report of a WorkerStatus frame body; the frame must be exactly <see cref="WorkerStatusBodyBytes"/> long, with only known flag bits and no negative count.</summary>
    internal static WorkerStatusReport DecodeWorkerStatus(ReadOnlySpan<byte> body)
    {
        var reader = new FrameReader(body, "WorkerStatus");
        long ticks = reader.Int64();
        int sessions = reader.Int32();
        int limit = reader.Int32();
        byte flags = reader.Byte();
        reader.End();
        const byte known = (byte)(WorkerStatusFlags.Standby | WorkerStatusFlags.Restarting | WorkerStatusFlags.ShuttingDown);
        if (ticks < 0 || sessions < 0 || limit < 0 || (flags & ~known) != 0)
        {
            throw new InvalidDataException("WorkerStatus frame with a negative count or an unknown flag");
        }

        return new WorkerStatusReport(ticks, sessions, limit, (WorkerStatusFlags)flags);
    }

    /// <summary>The text cut to at most <see cref="MaxTextBytes"/> UTF-8 bytes (whole characters only).</summary>
    private static string ClampText(string text)
    {
        string clamped = text.Length > MaxTextBytes ? text[..MaxTextBytes] : text;
        while (Encoding.UTF8.GetByteCount(clamped) > MaxTextBytes)
        {
            clamped = clamped[..^1];
        }

        return clamped;
    }

    private ref struct FrameWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _offset;

        public void Byte(byte value) => _buffer[_offset++] = value;

        public void UInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_offset, 2), value);
            _offset += 2;
        }

        public void Int32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_offset, 4), value);
            _offset += 4;
        }

        public void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_offset, 4), value);
            _offset += 4;
        }

        public void Int64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_offset, 8), value);
            _offset += 8;
        }

        public void Text(ReadOnlySpan<byte> utf8)
        {
            Int32(utf8.Length);
            utf8.CopyTo(_buffer.Slice(_offset));
            _offset += utf8.Length;
        }
    }

    private ref struct FrameReader(ReadOnlySpan<byte> body, string frameName)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        private readonly string _frameName = frameName;
        private int _offset;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _body.Length - _offset)
            {
                throw new InvalidDataException($"truncated {_frameName} frame ({_body.Length} B)");
            }

            ReadOnlySpan<byte> slice = _body.Slice(_offset, count);
            _offset += count;
            return slice;
        }

        public byte Byte() => Take(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        /// <summary>A length-prefixed (int32) byte block of at most <paramref name="maxLength"/> bytes, copied.</summary>
        public byte[] Bytes(int maxLength)
        {
            int length = Int32();
            if (length < 0 || length > maxLength)
            {
                throw new InvalidDataException($"{_frameName} byte block length {length} out of range");
            }

            return Take(length).ToArray();
        }

        /// <summary>Refuses a frame with bytes left after its last field.</summary>
        public readonly void End()
        {
            if (_offset != _body.Length)
            {
                throw new InvalidDataException($"{_frameName} frame has {_body.Length - _offset} trailing bytes");
            }
        }

        public string Text()
        {
            int length = Int32();
            if (length is < 0 or > MaxTextBytes)
            {
                throw new InvalidDataException($"{_frameName} text length {length} out of range");
            }

            return Encoding.UTF8.GetString(Take(length));
        }
    }
}
