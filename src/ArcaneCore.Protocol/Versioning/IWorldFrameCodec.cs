namespace ArcaneCore.Protocol.Versioning;

/// <summary>
/// Build-specific world framing. Implementations are pure byte codecs; callers own
/// transport I/O, cipher ordering and allocation after a successful size check.
/// </summary>
public interface IWorldFrameCodec
{
    int OutgoingHeaderLength { get; }
    int IncomingHeaderLength { get; }
    int MaxClientPayloadBytes { get; }
    int MaxServerPayloadBytes { get; }

    /// <summary>Parses a decrypted client header before payload allocation.</summary>
    bool TryParseClientHeader(ReadOnlySpan<byte> header, out int sizeField, out uint rawOpcode, out int payloadLength);

    /// <summary>Writes a server header; the caller must have checked the payload limit.</summary>
    void WriteServerHeader(Span<byte> header, int payloadLength, ushort wireOpcode);
}

/// <summary>Adapter preserving ArcaneCore's 1.12.1 framing byte-for-byte.</summary>
public sealed class Vanilla112FrameCodec : IWorldFrameCodec
{
    public static readonly Vanilla112FrameCodec Instance = new();

    private Vanilla112FrameCodec() { }

    public int OutgoingHeaderLength => 4;
    public int IncomingHeaderLength => 6;
    public int MaxClientPayloadBytes => WorldFrameCodec.MaxClientPacketSize - 4;
    public int MaxServerPayloadBytes => WorldFrameCodec.MaxServerPayload;

    public bool TryParseClientHeader(ReadOnlySpan<byte> header, out int sizeField, out uint rawOpcode, out int payloadLength)
    {
        if (header.Length < IncomingHeaderLength)
        {
            sizeField = 0;
            rawOpcode = 0;
            payloadLength = 0;
            return false;
        }
        bool valid = WorldFrameCodec.TryParseClientHeader(header, out ushort size, out rawOpcode);
        sizeField = size;
        payloadLength = valid ? size - 4 : 0;
        return valid;
    }

    public void WriteServerHeader(Span<byte> header, int payloadLength, ushort wireOpcode)
    {
        WorldFrameCodec.WriteServerSize(header, payloadLength);
        WorldFrameCodec.WriteServerOpcode(header, wireOpcode);
    }
}

/// <summary>
/// Lanternfall's TLS frame header from Game/shared/Runtime/Wire/FrameHeader.cs.
/// This codec is deliberately not registered as a client profile until its auth,
/// opcode map, field layout and typed payload handlers are available.
/// </summary>
public sealed class LanternfallFrameCodec : IWorldFrameCodec
{
    public static readonly LanternfallFrameCodec Instance = new();
    public const int HeaderBytes = 6;
    public const int MaxClientFrameBytes = 16 * 1024;
    public const int MaxServerFrameBytes = 1024 * 1024;

    private LanternfallFrameCodec() { }

    public int OutgoingHeaderLength => HeaderBytes;
    public int IncomingHeaderLength => HeaderBytes;
    public int MaxClientPayloadBytes => MaxClientFrameBytes - HeaderBytes;
    public int MaxServerPayloadBytes => MaxServerFrameBytes - HeaderBytes;

    public bool TryParseClientHeader(ReadOnlySpan<byte> header, out int sizeField, out uint rawOpcode, out int payloadLength)
    {
        sizeField = 0;
        rawOpcode = 0;
        payloadLength = 0;
        if (header.Length < HeaderBytes) return false;
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        ushort opcode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        sizeField = size > int.MaxValue ? int.MaxValue : (int)size;
        rawOpcode = opcode;
        if (size < 2 || size - 2 > (uint)MaxClientPayloadBytes) return false;
        payloadLength = (int)size - 2;
        return true;
    }

    public void WriteServerHeader(Span<byte> header, int payloadLength, ushort wireOpcode)
    {
        if (header.Length < HeaderBytes) throw new ArgumentException("Destination is shorter than a Lanternfall frame header.", nameof(header));
        if ((uint)payloadLength > (uint)MaxServerPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)(payloadLength + 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), wireOpcode);
    }
}
