using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
/// Encoding of the Tier 3a session-link frames (<c>docs/design/hot-reload-tiers.md</c> §5.3). They ride the length-prefixed
/// framing and the manifest handshake of <see cref="LoopbackGatewayWorkerHost"/>, and every packet frame carries the same
/// <see cref="ClusterSessionPacket"/> envelope (generation, owner epoch, sequence). All integers little-endian. Every decoder
/// checks lengths and throws <see cref="InvalidDataException"/> on a frame that does not fit, which closes the link.
/// </summary>
public static class GatewaySessionWire
{
    /// <summary>Bytes of the link challenge nonce and of its HMAC-SHA256 proof.</summary>
    public const int NonceBytes = 32;

    /// <summary>Longest account name or endpoint text a SessionOpen may carry.</summary>
    public const int MaxTextBytes = 256;

    internal static byte[] EncodeOpen(long requestId, GatewaySessionOpenRequest request)
    {
        byte[] name = Encoding.UTF8.GetBytes(request.AccountName);
        byte[] endpoint = Encoding.UTF8.GetBytes(request.ClientEndpoint);
        if (name.Length > MaxTextBytes || endpoint.Length > MaxTextBytes)
        {
            throw new ArgumentException("account name or client endpoint too long for a SessionOpen frame", nameof(request));
        }

        byte[] buffer = new byte[1 + 8 + 8 + 4 + 4 + 4 + name.Length + 4 + endpoint.Length];
        var writer = new FrameWriter(buffer);
        writer.Byte((byte)GatewayWorkerFrameType.SessionOpen);
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
        long generation = reader.Int64();
        int accountId = reader.Int32();
        uint build = reader.UInt32();
        string name = reader.Text();
        string endpoint = reader.Text();
        return (requestId, new GatewaySessionOpenRequest(generation, accountId, name, build, endpoint));
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
        byte[] buffer = new byte[1 + LoopbackGatewayWorkerHost.SessionPacketHeaderBytes + packet.Payload.Length];
        buffer[0] = (byte)type;
        LoopbackGatewayWorkerHost.WriteSessionPacketBody(buffer.AsSpan(1), packet);
        return buffer;
    }

    /// <summary><see cref="EncodePacket(GatewayWorkerFrameType, ClusterSessionPacket)"/> from a span payload (one copy, into the frame).</summary>
    internal static byte[] EncodePacket(
        GatewayWorkerFrameType type, long sessionGeneration, int accountId, ulong characterGuid, long ownerEpoch, long sequence,
        uint opcode, ReadOnlySpan<byte> payload)
    {
        byte[] buffer = new byte[1 + LoopbackGatewayWorkerHost.SessionPacketHeaderBytes + payload.Length];
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

    /// <summary>The envelope of a packet frame body; the payload is a slice of <paramref name="body"/> (no copy).</summary>
    internal static ClusterSessionPacket DecodePacket(ReadOnlyMemory<byte> body) => LoopbackGatewayWorkerHost.DeserializeSessionPacket(body);

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

    private ref struct FrameWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _offset;

        public void Byte(byte value) => _buffer[_offset++] = value;

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

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

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
