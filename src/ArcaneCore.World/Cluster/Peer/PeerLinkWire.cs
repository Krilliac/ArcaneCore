using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster.Gateway;

namespace ArcaneCore.World.Cluster.Peer;

/// <summary>One realm authority lease a node holds: its scope (<c>social</c>, <c>economy</c>, <c>instances</c>) and the fencing token it was granted.</summary>
public readonly record struct PeerLeaseToken(string Scope, long FencingToken);

/// <summary>What one end of a peer link says about itself in Hello / HelloAck: the link protocol version, its node manifest (node id and incarnation included) and the lease tokens it holds.</summary>
public sealed record PeerHelloInfo(ushort ProtocolVersion, ClusterNodeManifest Manifest, IReadOnlyList<PeerLeaseToken> Leases);

/// <summary>
/// Encoding of the peer link frames (<see cref="RealmServiceFrameType"/>). Every frame starts with its type byte; integers are little-endian; text is an int32
/// byte count then UTF-8 (at most <see cref="MaxTextBytes"/>); the layouts are pinned by <c>PeerLinkWireTests</c>. Decoders check every length and throw
/// <see cref="InvalidDataException"/> on a frame that does not fit, which closes the link. Pure functions: no state, safe from any thread.
/// </summary>
internal static class PeerLinkWire
{
    /// <summary>Largest echo payload a Ping or Pong may carry.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Longest text (scope name, refusal reason) in a frame.</summary>
    public const int MaxTextBytes = 256;

    /// <summary>Most lease tokens one Hello or HelloAck lists.</summary>
    public const int MaxLeases = 16;

    private const int MaxManifestBytes = 2048;

    public static byte[] EncodeNonce(RealmServiceFrameType type, ReadOnlySpan<byte> bytes)
    {
        if (type is not (RealmServiceFrameType.Challenge or RealmServiceFrameType.Proof))
        {
            throw new ArgumentException("only Challenge and Proof carry a nonce or proof", nameof(type));
        }

        if (bytes.Length != GatewaySessionWire.NonceBytes)
        {
            throw new ArgumentException($"expected {GatewaySessionWire.NonceBytes} bytes", nameof(bytes));
        }

        byte[] frame = new byte[1 + bytes.Length];
        frame[0] = (byte)type;
        bytes.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] DecodeNonce(ReadOnlySpan<byte> body, string frameName)
    {
        if (body.Length != GatewaySessionWire.NonceBytes)
        {
            throw new InvalidDataException($"{frameName} frame of {body.Length} B (expected {GatewaySessionWire.NonceBytes})");
        }

        return body.ToArray();
    }

    public static byte[] EncodeHello(PeerHelloInfo info)
    {
        var w = new Writer(RealmServiceFrameType.Hello);
        w.Info(info);
        return w.ToArray();
    }

    public static PeerHelloInfo DecodeHello(ReadOnlyMemory<byte> body)
    {
        var r = new Reader(body, "Hello");
        PeerHelloInfo info = r.Info();
        r.End();
        return info;
    }

    /// <summary>HelloAck: admitted byte, reason text, and (admitted only) the holder's info.</summary>
    public static byte[] EncodeHelloAck(bool accepted, string reason, PeerHelloInfo? info)
    {
        if (accepted && info is null)
        {
            throw new ArgumentException("an accepted HelloAck carries the holder's info", nameof(info));
        }

        var w = new Writer(RealmServiceFrameType.HelloAck);
        w.Byte(accepted ? (byte)1 : (byte)0);
        w.Text(reason);
        if (accepted)
        {
            w.Info(info!);
        }

        return w.ToArray();
    }

    public static (bool Accepted, string Reason, PeerHelloInfo? Info) DecodeHelloAck(ReadOnlyMemory<byte> body)
    {
        var r = new Reader(body, "HelloAck");
        bool accepted = r.Byte() != 0;
        string reason = r.Text();
        PeerHelloInfo? info = accepted ? r.Info() : null;
        r.End();
        return (accepted, reason, info);
    }

    /// <summary>A Ping (client to holder) or a Pong (holder to client): request id, idempotency id, payload.</summary>
    public static byte[] EncodeEcho(RealmServiceFrameType type, long requestId, long idempotencyId, ReadOnlySpan<byte> payload)
    {
        if (type is not (RealmServiceFrameType.Ping or RealmServiceFrameType.Pong))
        {
            throw new ArgumentException("only Ping and Pong carry an echo", nameof(type));
        }

        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"echo payload of {payload.Length} B exceeds {MaxPayloadBytes} B", nameof(payload));
        }

        byte[] frame = new byte[1 + 8 + 8 + 4 + payload.Length];
        Span<byte> span = frame;
        span[0] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), requestId);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(9, 8), idempotencyId);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(17, 4), payload.Length);
        payload.CopyTo(span[21..]);
        return frame;
    }

    public static (long RequestId, long IdempotencyId, byte[] Payload) DecodeEcho(ReadOnlyMemory<byte> body, string frameName)
    {
        var r = new Reader(body, frameName);
        long requestId = r.Int64();
        long idempotencyId = r.Int64();
        byte[] payload = r.Bytes(MaxPayloadBytes);
        r.End();
        return (requestId, idempotencyId, payload);
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public Writer(RealmServiceFrameType type) => _stream.WriteByte((byte)type);

        public void Byte(byte value) => _stream.WriteByte(value);

        public void UInt16(ushort value)
        {
            Span<byte> span = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            _stream.Write(span);
        }

        public void Int32(int value)
        {
            Span<byte> span = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            _stream.Write(span);
        }

        public void Int64(long value)
        {
            Span<byte> span = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
            _stream.Write(span);
        }

        public void Text(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > MaxTextBytes)
            {
                throw new ArgumentException($"text of {bytes.Length} B exceeds {MaxTextBytes} B");
            }

            Int32(bytes.Length);
            _stream.Write(bytes);
        }

        public void Info(PeerHelloInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (info.Leases.Count > MaxLeases)
            {
                throw new ArgumentException($"{info.Leases.Count} lease tokens exceed {MaxLeases}");
            }

            // The manifest body is the HelloRegister layout of the gateway link without its type byte, so both links read manifests the same way.
            byte[] manifest = LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(info.Manifest)[1..];
            if (manifest.Length > MaxManifestBytes)
            {
                throw new ArgumentException($"manifest of {manifest.Length} B exceeds {MaxManifestBytes} B");
            }

            UInt16(info.ProtocolVersion);
            Int32(manifest.Length);
            _stream.Write(manifest);
            Int32(info.Leases.Count);
            foreach (PeerLeaseToken lease in info.Leases)
            {
                Text(lease.Scope);
                Int64(lease.FencingToken);
            }
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private sealed class Reader(ReadOnlyMemory<byte> body, string frameName)
    {
        private readonly ReadOnlyMemory<byte> _body = body;
        private int _offset;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _body.Length - _offset)
            {
                throw new InvalidDataException($"{frameName} frame is truncated or has a length that does not fit ({count} B at offset {_offset} of {_body.Length})");
            }

            ReadOnlySpan<byte> slice = _body.Span.Slice(_offset, count);
            _offset += count;
            return slice;
        }

        public byte Byte() => Take(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public string Text()
        {
            int length = Int32();
            if (length > MaxTextBytes)
            {
                throw new InvalidDataException($"{frameName} text of {length} B exceeds {MaxTextBytes} B");
            }

            return Encoding.UTF8.GetString(Take(length));
        }

        public byte[] Bytes(int max)
        {
            int length = Int32();
            if (length > max)
            {
                throw new InvalidDataException($"{frameName} payload of {length} B exceeds {max} B");
            }

            return Take(length).ToArray();
        }

        public PeerHelloInfo Info()
        {
            ushort version = UInt16();
            int manifestLength = Int32();
            if (manifestLength > MaxManifestBytes)
            {
                throw new InvalidDataException($"{frameName} manifest of {manifestLength} B exceeds {MaxManifestBytes} B");
            }

            ClusterNodeManifest manifest;
            try
            {
                manifest = LoopbackGatewayWorkerHost.DeserializeManifest(Take(manifestLength));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"{frameName} manifest is malformed", ex);
            }

            int count = Int32();
            if (count < 0 || count > MaxLeases)
            {
                throw new InvalidDataException($"{frameName} lists {count} lease tokens (at most {MaxLeases})");
            }

            var leases = new List<PeerLeaseToken>(count);
            for (int i = 0; i < count; i++)
            {
                string scope = Text();
                leases.Add(new PeerLeaseToken(scope, Int64()));
            }

            return new PeerHelloInfo(version, manifest, leases);
        }

        public void End()
        {
            if (_offset != _body.Length)
            {
                throw new InvalidDataException($"{frameName} frame has {_body.Length - _offset} trailing bytes");
            }
        }
    }
}
