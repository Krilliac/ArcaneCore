using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.World.Net;

/// <summary>Explicitly enabled, per-session PKT 3.1 trace. Payloads are plaintext and may contain private chat.</summary>
public sealed class PacketCaptureOptions
{
    public const string SectionName = "Diagnostics:PacketCapture";

    /// <summary>Permit an Administrator to start a session capture. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Directory for new traces; generated filenames do not include player input.</summary>
    public string Directory { get; set; } = "packet-captures";

    /// <summary>Maximum bytes in one trace, including its header; a full trace stops automatically.</summary>
    public long MaxBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>PKT 3.1 writer, using the fields read by WPP BinaryPacketReader.ReadHeader/Read.</summary>
public sealed class PacketCapture : IDisposable
{
    private const int HeaderLength = 66;
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly long _maxBytes;
    private bool _closed;

    public string Path { get; }

    public PacketCapture(string directory, long maxBytes)
    {
        if (maxBytes < HeaderLength + 24 || maxBytes > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"arcane-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.pkt");
        _stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: false);
        _maxBytes = maxBytes;
        _writer.Write(Encoding.ASCII.GetBytes("PKT"));
        _writer.Write((ushort)0x301);
        _writer.Write((byte)'A'); // ArcaneCore; WPP treats this as a generic sniffer.
        _writer.Write((uint)5875);
        _writer.Write(Encoding.ASCII.GetBytes("enUS"));
        _writer.Write(new byte[40]); // no session key is recorded
        _writer.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _writer.Write(unchecked((uint)Environment.TickCount));
        _writer.Write(0); // optional header data
    }

    public bool IsOpen { get { lock (_gate) return !_closed; } }

    public void Write(bool fromClient, ushort opcode, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_closed) return;
            long recordLength = 24L + payload.Length;
            if (_stream.Position + recordLength > _maxBytes)
            {
                Close();
                return;
            }

            _writer.Write(Encoding.ASCII.GetBytes(fromClient ? "CMSG" : "SMSG"));
            _writer.Write(0); // one world connection per file
            _writer.Write(unchecked((uint)Environment.TickCount));
            _writer.Write(0); // no optional record data
            _writer.Write(checked(payload.Length + 4));
            _writer.Write((int)opcode);
            _writer.Write(payload);
            _writer.Flush();
        }
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _writer.Dispose();
    }

    public void Dispose() { lock (_gate) Close(); }
}
