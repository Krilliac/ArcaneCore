using System.Buffers.Binary;
using System.IO.Compression;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Updates;

/// <summary>
/// The update blocks and out-of-range GUIDs queued for one client during a tick, turned into
/// SMSG_UPDATE_OBJECT / SMSG_COMPRESSED_UPDATE_OBJECT packets on flush.
/// <para>
/// Packet layout (vmangos WorldPackets::ObjectUpdate::UpdateObject::AppendBodyTo, build
/// &gt; 1.8.4): u32 blockCount (the out-of-range list counts as one block), u8 hasTransport,
/// [u8 UPDATETYPE_OUT_OF_RANGE_OBJECTS, u32 count, packed GUIDs], then the blocks. Payloads
/// larger than the compression threshold are sent as SMSG_COMPRESSED_UPDATE_OBJECT:
/// u32 uncompressedSize + zlib stream (vmangos Compression.Update.Size default 128).
/// </para>
/// Thread affinity: world thread only.
/// </summary>
public sealed class UpdateData
{
    /// <summary>
    /// Largest uncompressed body per packet. Keeps the uncompressed form inside the 16-bit
    /// SMSG size field so a packet is valid whether or not it ends up compressed.
    /// </summary>
    public const int MaxBodySize = 60000;

    private readonly PacketWriter _blocks = new(1024);
    private readonly List<int> _blockEnds = [];
    private readonly List<ObjectGuid> _outOfRange = [];
    private int _openBlockStart = -1;

    public bool IsEmpty => _blockEnds.Count == 0 && _outOfRange.Count == 0;

    public int BlockCount => _blockEnds.Count;

    /// <summary>Start a block; write it to the returned writer, then call <see cref="EndBlock"/>.</summary>
    public PacketWriter BeginBlock()
    {
        if (_openBlockStart >= 0)
        {
            throw new InvalidOperationException("a block is already open");
        }

        _openBlockStart = _blocks.Length;
        return _blocks;
    }

    public void EndBlock()
    {
        if (_openBlockStart < 0)
        {
            throw new InvalidOperationException("no open block");
        }

        if (_blocks.Length > _openBlockStart)
        {
            _blockEnds.Add(_blocks.Length);
        }

        _openBlockStart = -1;
    }

    /// <summary>Drop a block that turned out to be empty (nothing was written since <see cref="BeginBlock"/>).</summary>
    public void CancelBlock()
    {
        if (_openBlockStart < 0)
        {
            throw new InvalidOperationException("no open block");
        }

        if (_blocks.Length != _openBlockStart)
        {
            throw new InvalidOperationException("cannot cancel a block that has content");
        }

        _openBlockStart = -1;
    }

    public void AddOutOfRange(ObjectGuid guid) => _outOfRange.Add(guid);

    /// <summary>Build the queued data into packets, hand them to <paramref name="send"/>, and clear.</summary>
    public void Flush(Action<WorldOpcode, byte[]> send, int compressionThreshold)
    {
        if (_openBlockStart >= 0)
        {
            throw new InvalidOperationException("flush with an open block");
        }

        if (IsEmpty)
        {
            return;
        }

        ReadOnlySpan<byte> all = _blocks.AsSpan();
        int blockIndex = 0;
        int blockStart = 0;
        bool outOfRangePending = _outOfRange.Count > 0;

        while (outOfRangePending || blockIndex < _blockEnds.Count)
        {
            var body = new PacketWriter(Math.Min(MaxBodySize, all.Length - blockStart + 64));
            body.WriteUInt32(0); // block count, patched below
            body.WriteByte(0);   // has transport
            uint count = 0;

            if (outOfRangePending)
            {
                body.WriteByte((byte)ObjectUpdateType.OutOfRangeObjects);
                body.WriteUInt32((uint)_outOfRange.Count);
                foreach (ObjectGuid guid in _outOfRange)
                {
                    body.WritePackedGuid(guid.Value);
                }

                count++;
                outOfRangePending = false;
            }

            while (blockIndex < _blockEnds.Count)
            {
                int length = _blockEnds[blockIndex] - blockStart;
                if (count > 0 && body.Length + length > MaxBodySize)
                {
                    break;
                }

                body.WriteBytes(all.Slice(blockStart, length));
                blockStart = _blockEnds[blockIndex];
                blockIndex++;
                count++;
            }

            body.PatchUInt32(0, count);
            Send(body, send, compressionThreshold);
        }

        Clear();
    }

    public void Clear()
    {
        _blocks.Reset();
        _blockEnds.Clear();
        _outOfRange.Clear();
        _openBlockStart = -1;
    }

    /// <summary>Send one finished update body, compressed above <paramref name="compressionThreshold"/> (also used for the ship packets).</summary>
    internal static void Send(PacketWriter body, Action<WorldOpcode, byte[]> send, int compressionThreshold)
    {
        if (compressionThreshold <= 0 || body.Length <= compressionThreshold)
        {
            send(WorldOpcode.SmsgUpdateObject, body.ToArray());
            return;
        }

        using var compressed = new MemoryStream(body.Length / 2 + 16);
        Span<byte> sizePrefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizePrefix, (uint)body.Length);
        compressed.Write(sizePrefix);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(body.AsSpan());
        }

        send(WorldOpcode.SmsgCompressedUpdateObject, compressed.ToArray());
    }
}
