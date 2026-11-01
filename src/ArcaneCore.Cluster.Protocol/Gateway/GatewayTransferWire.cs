using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>Which step of a cross-worker transfer a <see cref="GatewayTransferControl"/> frame or its <see cref="GatewayTransferAck"/> belongs to.</summary>
public enum GatewayTransferPhase : byte
{
    /// <summary>Stage 4: the gateway starts buffering the client's packets and opens the session on the target worker.</summary>
    Stage = 1,

    /// <summary>Pre-commit abort: the gateway resumes routing to the source and replays what it buffered.</summary>
    Cancel = 2,

    /// <summary>Post-commit: the gateway re-points the session to the target, replays what it buffered and drops stale-generation packets.</summary>
    Commit = 3,
}

/// <summary>A transfer control frame from the source worker's coordinator to the gateway. Every frame names the transfer, the character and the gateway session generation it concerns.</summary>
public abstract record GatewayTransferControl(Guid TransferId, ulong CharacterGuid, long SessionGeneration)
{
    public abstract GatewayTransferPhase Phase { get; }
}

/// <summary>Stage the world-port: buffer the session's client packets (bounded), open the session on <see cref="TargetNode"/> and tell the client a map change is pending.</summary>
public sealed record GatewayTransferStage(
    Guid TransferId,
    ulong CharacterGuid,
    long SessionGeneration,
    string SourceNode,
    string TargetNode,
    uint TargetMapId,
    uint TargetInstanceId,
    TransferPosition TargetPosition) : GatewayTransferControl(TransferId, CharacterGuid, SessionGeneration)
{
    public override GatewayTransferPhase Phase => GatewayTransferPhase.Stage;
}

/// <summary>Cancel a staged world-port and resume routing to <see cref="RestoredNode"/>.</summary>
public sealed record GatewayTransferCancel(
    Guid TransferId,
    ulong CharacterGuid,
    long SessionGeneration,
    string RestoredNode,
    string Reason) : GatewayTransferControl(TransferId, CharacterGuid, SessionGeneration)
{
    public override GatewayTransferPhase Phase => GatewayTransferPhase.Cancel;
}

/// <summary>Commit the route switch to <see cref="TargetNode"/> under the new ownership token (<see cref="TokenScope"/>, <see cref="TokenEpoch"/>, <see cref="TokenIncarnation"/>).</summary>
public sealed record GatewayTransferCommit(
    Guid TransferId,
    ulong CharacterGuid,
    long SessionGeneration,
    string TargetNode,
    string TokenScope,
    long TokenEpoch,
    Guid TokenIncarnation) : GatewayTransferControl(TransferId, CharacterGuid, SessionGeneration)
{
    public override GatewayTransferPhase Phase => GatewayTransferPhase.Commit;
}

/// <summary>The gateway's answer (gateway to worker) to one control frame. <see cref="Reason"/> is empty when accepted.</summary>
public sealed record GatewayTransferAck(Guid TransferId, GatewayTransferPhase Phase, bool Accepted, string Reason);

/// <summary>
/// Encoding of the cross-worker transfer control frames (<see cref="GatewayWorkerFrameType.TransferStage"/> and its siblings). They ride the existing
/// session link: the link's length prefix and header carry the frame type byte, and the body is little-endian fixed fields in the order of the
/// records above (Guid as 16 bytes, text as an int32 length plus UTF-8 of at most <see cref="GatewaySessionWire.MaxTextBytes"/> bytes). Every decoder
/// checks lengths, refuses trailing bytes and throws <see cref="InvalidDataException"/>, which closes the link.
/// </summary>
public static class GatewayTransferWire
{
    internal static byte[] Encode(GatewayTransferControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        switch (control)
        {
            case GatewayTransferStage stage:
            {
                byte[] source = Utf8(stage.SourceNode);
                byte[] target = Utf8(stage.TargetNode);
                var writer = new Writer(1 + 16 + 8 + 8 + 4 + source.Length + 4 + target.Length + 4 + 4 + 16, GatewayWorkerFrameType.TransferStage);
                writer.Header(stage);
                writer.Text(source);
                writer.Text(target);
                writer.UInt32(stage.TargetMapId);
                writer.UInt32(stage.TargetInstanceId);
                writer.Single(stage.TargetPosition.X);
                writer.Single(stage.TargetPosition.Y);
                writer.Single(stage.TargetPosition.Z);
                writer.Single(stage.TargetPosition.Orientation);
                return writer.Buffer;
            }

            case GatewayTransferCancel cancel:
            {
                byte[] restored = Utf8(cancel.RestoredNode);
                byte[] reason = Utf8Truncated(cancel.Reason);
                var writer = new Writer(1 + 16 + 8 + 8 + 4 + restored.Length + 4 + reason.Length, GatewayWorkerFrameType.TransferCancel);
                writer.Header(cancel);
                writer.Text(restored);
                writer.Text(reason);
                return writer.Buffer;
            }

            case GatewayTransferCommit commit:
            {
                byte[] target = Utf8(commit.TargetNode);
                byte[] scope = Utf8(commit.TokenScope);
                var writer = new Writer(1 + 16 + 8 + 8 + 4 + target.Length + 4 + scope.Length + 8 + 16, GatewayWorkerFrameType.TransferCommit);
                writer.Header(commit);
                writer.Text(target);
                writer.Text(scope);
                writer.Int64(commit.TokenEpoch);
                writer.Guid(commit.TokenIncarnation);
                return writer.Buffer;
            }

            default:
                throw new ArgumentException("unknown transfer control frame " + control.GetType().Name, nameof(control));
        }
    }

    /// <summary>Decodes the body (without the type byte) of a <see cref="GatewayWorkerFrameType.TransferStage"/>, <see cref="GatewayWorkerFrameType.TransferCancel"/> or <see cref="GatewayWorkerFrameType.TransferCommit"/> frame.</summary>
    internal static GatewayTransferControl DecodeControl(GatewayWorkerFrameType type, ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body, type.ToString());
        Guid transferId = reader.Guid();
        ulong characterGuid = reader.UInt64();
        long generation = reader.Int64();
        switch (type)
        {
            case GatewayWorkerFrameType.TransferStage:
            {
                string source = reader.Text();
                string target = reader.Text();
                uint mapId = reader.UInt32();
                uint instanceId = reader.UInt32();
                var position = new TransferPosition(reader.Single(), reader.Single(), reader.Single(), reader.Single());
                reader.End();
                return new GatewayTransferStage(transferId, characterGuid, generation, source, target, mapId, instanceId, position);
            }

            case GatewayWorkerFrameType.TransferCancel:
            {
                string restored = reader.Text();
                string reason = reader.Text();
                reader.End();
                return new GatewayTransferCancel(transferId, characterGuid, generation, restored, reason);
            }

            case GatewayWorkerFrameType.TransferCommit:
            {
                string target = reader.Text();
                string scope = reader.Text();
                long epoch = reader.Int64();
                Guid incarnation = reader.Guid();
                reader.End();
                return new GatewayTransferCommit(transferId, characterGuid, generation, target, scope, epoch, incarnation);
            }

            default:
                throw new InvalidDataException($"frame {type} is not a transfer control frame");
        }
    }

    /// <summary>A TransferAck frame: type, transfer id (16), phase (byte), accepted (byte), reason (text, cut to the text limit).</summary>
    internal static byte[] EncodeAck(GatewayTransferAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        byte[] reason = Utf8Truncated(ack.Reason);
        var writer = new Writer(1 + 16 + 1 + 1 + 4 + reason.Length, GatewayWorkerFrameType.TransferAck);
        writer.Guid(ack.TransferId);
        writer.Byte((byte)ack.Phase);
        writer.Byte(ack.Accepted ? (byte)1 : (byte)0);
        writer.Text(reason);
        return writer.Buffer;
    }

    internal static GatewayTransferAck DecodeAck(ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body, "TransferAck");
        Guid transferId = reader.Guid();
        var phase = (GatewayTransferPhase)reader.Byte();
        if (phase is not (GatewayTransferPhase.Stage or GatewayTransferPhase.Cancel or GatewayTransferPhase.Commit))
        {
            throw new InvalidDataException($"TransferAck with unknown phase {(byte)phase}");
        }

        bool accepted = reader.Byte() != 0;
        string reason = reader.Text();
        reader.End();
        return new GatewayTransferAck(transferId, phase, accepted, reason);
    }

    private static byte[] Utf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > GatewaySessionWire.MaxTextBytes)
        {
            throw new ArgumentException($"text of {bytes.Length} B is over {GatewaySessionWire.MaxTextBytes} B in a transfer frame", nameof(text));
        }

        return bytes;
    }

    /// <summary>Reasons are free text for logs: cut at the byte limit (a cut inside a multi-byte character decodes to a replacement character).</summary>
    private static byte[] Utf8Truncated(string? text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return bytes.Length > GatewaySessionWire.MaxTextBytes ? bytes[..GatewaySessionWire.MaxTextBytes] : bytes;
    }

    private ref struct Writer
    {
        private int _offset;

        public Writer(int length, GatewayWorkerFrameType type)
        {
            Buffer = new byte[length];
            Buffer[0] = (byte)type;
            _offset = 1;
        }

        public byte[] Buffer { get; }

        public void Header(GatewayTransferControl control)
        {
            Guid(control.TransferId);
            UInt64(control.CharacterGuid);
            Int64(control.SessionGeneration);
        }

        public void Byte(byte value) => Buffer[_offset++] = value;

        public void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(_offset, 4), value);
            _offset += 4;
        }

        public void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(Buffer.AsSpan(_offset, 8), value);
            _offset += 8;
        }

        public void Int64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(Buffer.AsSpan(_offset, 8), value);
            _offset += 8;
        }

        public void Single(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(Buffer.AsSpan(_offset, 4), value);
            _offset += 4;
        }

        public void Guid(Guid value)
        {
            value.TryWriteBytes(Buffer.AsSpan(_offset, 16), bigEndian: false, out _);
            _offset += 16;
        }

        public void Text(byte[] utf8)
        {
            BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(_offset, 4), utf8.Length);
            _offset += 4;
            utf8.CopyTo(Buffer, _offset);
            _offset += utf8.Length;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> body, string frameName)
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

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public Guid Guid() => new(Take(16), bigEndian: false);

        public string Text()
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            if (length is < 0 or > GatewaySessionWire.MaxTextBytes)
            {
                throw new InvalidDataException($"{_frameName} text length {length} out of range");
            }

            return Encoding.UTF8.GetString(Take(length));
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
