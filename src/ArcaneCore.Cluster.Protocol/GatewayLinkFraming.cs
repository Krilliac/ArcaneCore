using System.Buffers.Binary;
using System.Net.Sockets;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Length-prefixed frame IO and the session-packet envelope of the gateway-worker link: a 4-byte little-endian length, then the
/// frame (type byte first). Shared by both ends; the byte layout is pinned by GatewaySessionWireTests and must not change.
/// </summary>
internal static class GatewayLinkFraming
{
    /// <summary>Largest frame either end accepts or sends: one maximal server payload plus its envelope.</summary>
    public const int MaxFrameBytes = WorldFrameCodec.MaxServerPayload + 256;

    /// <summary>Bytes of the envelope header that precedes the payload (<see cref="WriteSessionPacketBody"/>).</summary>
    public const int SessionPacketHeaderBytes = 44;

    public static async ValueTask WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"frame payload ({payload.Length} B) exceeds MaxFrameBytes ({MaxFrameBytes} B)");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int read = await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"invalid gateway–worker frame length: {length}");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static async ValueTask<int> ReadExactlyOrEofAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException("truncated frame header on gateway–worker stream");
            }

            total += n;
        }

        return total;
    }

    public static void WriteSessionPacketBody(Span<byte> span, ClusterSessionPacket packet)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span[..8], packet.SessionGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), packet.AccountId);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(12, 8), packet.CharacterGuid);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(20, 8), packet.OwnerEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(28, 8), packet.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36, 4), packet.Opcode);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), packet.Payload.Length);
        packet.Payload.Span.CopyTo(span[44..]);
    }

    public static ClusterSessionPacket DeserializeSessionPacket(ReadOnlyMemory<byte> memory)
    {
        ReadOnlySpan<byte> span = memory.Span;
        if (span.Length < SessionPacketHeaderBytes)
        {
            throw new InvalidDataException($"truncated session packet envelope ({span.Length} B)");
        }

        long gen = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
        int accountId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(12, 8));
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
        long seq = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(28, 8));
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(36, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(40, 4));
        if (len < 0 || len > span.Length - SessionPacketHeaderBytes)
        {
            throw new InvalidDataException($"session packet payload length {len} does not fit the {span.Length} B envelope");
        }

        ReadOnlyMemory<byte> payload = memory.Slice(SessionPacketHeaderBytes, len);
        return new ClusterSessionPacket(gen, accountId, guid, epoch, seq, opcode, payload);
    }
}
