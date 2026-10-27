using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>Explicitly enabled, per-session PKT 3.1 trace. Payloads are plaintext and may contain private chat.</summary>
public sealed class PacketCaptureOptions
{
    public const string SectionName = "Diagnostics:PacketCapture";

    /// <summary>Permit an Administrator to start a session capture. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Directory for new traces; generated filenames do not include player input.</summary>
    public string Directory { get; set; } = "packet-captures";

    /// <summary>
    /// Maximum bytes in one trace, including its header; a full trace stops automatically. A trace also stops when the disk writer falls
    /// 8192 records behind. Records are written by a background writer and the file is finished asynchronously after a stop; the server drains open traces at shutdown.
    /// </summary>
    public long MaxBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>
/// PKT 3.1 writer, using the fields read by WPP BinaryPacketReader.ReadHeader/Read. <see cref="Write"/> only copies the record into a bounded
/// queue (it is called from the session's send and receive paths under the session send lock); one background writer per file appends the
/// records in the order they were queued. A trace whose queue is full (the disk cannot keep up) stops like one that reached its size limit.
/// <see cref="Stop"/> ends the trace without waiting (the writer drains the queue and closes the file in the background); <see cref="Dispose"/>
/// waits until everything queued is on disk and is for process shutdown and tests only. A disk error in the writer is recorded in the
/// <see cref="FaultLedger"/> (and logged when a logger was given), ends the trace and sets <see cref="Faulted"/>. A trace stopped because its
/// queue was full is counted as <c>net.packet-capture-full</c>. Every open trace is tracked: <see cref="DrainAll"/> stops them all and waits (with
/// a time limit) until their files are complete; the world host calls it at stop and it also runs at process exit, so a clean exit loses no
/// queued records. A drain that times out is counted as <c>net.packet-capture-drain</c>.
/// </summary>
public sealed class PacketCapture : IDisposable
{
    private const int HeaderLength = 66;
    private const int QueueCapacity = 8192;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<PacketCapture, byte> Live = new();
    private static readonly TimeSpan DefaultDrainBudget = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = true, // every Write holds _gate
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Task _writer;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _maxBytes;
    private readonly ILogger? _logger;
    private volatile bool _faulted;
    private long _position = HeaderLength;
    private bool _closed;

    static PacketCapture() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DrainAll(DefaultDrainBudget);

    public string Path { get; }

    public PacketCapture(string directory, long maxBytes, ILogger? logger = null)
        : this(OpenFile(directory, maxBytes, out string path), path, maxBytes, logger)
    {
    }

    /// <summary>Capture onto an already open stream (the public constructor's file; tests pass a failing stream).</summary>
    internal PacketCapture(Stream stream, string path, long maxBytes, ILogger? logger = null)
    {
        Path = path;
        _stream = stream;
        _maxBytes = maxBytes;
        _logger = logger;
        using var header = new MemoryStream(HeaderLength);
        using (var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("PKT"));
            writer.Write((ushort)0x301);
            writer.Write((byte)'A'); // ArcaneCore; WPP treats this as a generic sniffer.
            writer.Write((uint)5875);
            writer.Write(Encoding.ASCII.GetBytes("enUS"));
            writer.Write(new byte[40]); // no session key is recorded
            writer.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.Write(unchecked((uint)Environment.TickCount));
            writer.Write(0); // optional header data
        }

        _stream.Write(header.GetBuffer(), 0, (int)header.Length);
        _stream.Flush();
        Live[this] = 0;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Stop every open trace and wait until each file is complete, for at most <paramref name="budget"/> (host stop, process exit, tests). Returns true when all
    /// finished in time. A trace still draining when the budget ends is counted as <c>net.packet-capture-drain</c>; its remaining records may be lost.
    /// </summary>
    public static bool DrainAll(TimeSpan budget, ILogger? logger = null)
    {
        PacketCapture[] captures = [.. Live.Keys];
        foreach (PacketCapture capture in captures)
        {
            capture.Stop();
        }

        if (captures.Length == 0 || Task.WaitAll([.. captures.Select(c => c._finished.Task)], budget))
        {
            return true;
        }

        var ex = new TimeoutException($"packet capture drain did not finish within {budget.TotalSeconds:0.###} s; queued records may be lost");
        FaultLedger.Shared.Record("net.packet-capture-drain", ex);
        try
        {
            if (logger is not null)
            {
                logger.LogFault("net-packet-capture-drain", ex, "packet capture drain timed out; queued records may be lost");
            }
            else
            {
                Console.Error.WriteLine("packet capture drain timed out; queued records may be lost: " + ex.Message);
            }
        }
        catch (Exception reportEx) when (reportEx is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("net.packet-capture-drain", reportEx);
        }

        return false;
    }

    private static FileStream OpenFile(string directory, long maxBytes, out string path)
    {
        if (maxBytes < HeaderLength + 24 || maxBytes > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Directory.CreateDirectory(directory);
        path = System.IO.Path.Combine(directory, $"arcane-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.pkt");
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, FileOptions.Asynchronous);
    }

    public bool IsOpen { get { lock (_gate) return !_closed; } }

    /// <summary>True once the background writer hit a disk error: the trace is dead and every later record is dropped.</summary>
    public bool Faulted => _faulted;

    /// <summary>Accept no more records and return at once; the background writer drains the queue and closes the file.</summary>
    public void Stop()
    {
        lock (_gate) Complete();
    }

    public void Write(bool fromClient, ushort opcode, ReadOnlySpan<byte> payload)
    {
        bool queueFull = false;
        lock (_gate)
        {
            if (_closed) return;
            long recordLength = 24L + payload.Length;
            if (_position + recordLength > _maxBytes)
            {
                Complete(); // the size limit is reached: the writer finishes the file in the background
            }
            else
            {
                byte[] record = new byte[recordLength];
                Span<byte> span = record;
                Encoding.ASCII.GetBytes(fromClient ? "CMSG" : "SMSG", span);
                // bytes 4..7: one world connection per file (0)
                BinaryPrimitives.WriteUInt32LittleEndian(span[8..], unchecked((uint)Environment.TickCount));
                // bytes 12..15: no optional record data (0)
                BinaryPrimitives.WriteInt32LittleEndian(span[16..], checked(payload.Length + 4));
                BinaryPrimitives.WriteInt32LittleEndian(span[20..], opcode);
                payload.CopyTo(span[24..]);
                if (_queue.Writer.TryWrite(record))
                {
                    _position += recordLength;
                }
                else
                {
                    Complete(); // the writer is far behind: stop the trace rather than block the session
                    queueFull = true;
                }
            }
        }

        if (queueFull)
        {
            ReportQueueFull(); // outside the gate: the logger is caller code and must not run under the trace's lock
        }
    }

    /// <summary>Once per trace (the trace is closed under <see cref="_gate"/> just before this runs, after the lock is released): the stop is counted and logged, not silent.</summary>
    private void ReportQueueFull()
    {
        var ex = new InvalidOperationException($"packet capture {Path} stopped: the writer is {QueueCapacity} records behind");
        FaultLedger.Shared.Record("net.packet-capture-full", ex);
        try
        {
            if (_logger is not null)
            {
                _logger.LogFault("net-packet-capture-full", ex, "packet capture {Path} stopped: the writer is {Capacity} records behind", Path, QueueCapacity);
            }
            else
            {
                Console.Error.WriteLine($"packet capture {Path} stopped: the writer is {QueueCapacity} records behind");
            }
        }
        catch (Exception logEx) when (logEx is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("net.packet-capture-full", logEx);
        }
    }

    /// <summary>Under <see cref="_gate"/>: accept no more records; the writer closes the file once the queue is empty.</summary>
    private void Complete()
    {
        _closed = true;
        _queue.Writer.TryComplete();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            ChannelReader<byte[]> reader = _queue.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out byte[]? record))
                {
                    await _stream.WriteAsync(record).ConfigureAwait(false);
                }

                await _stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The capture stops here; the connection must not.
            _faulted = true;
            FaultLedger.Shared.Record("net.packet-capture", ex);
            _logger?.LogFault("net-packet-capture", ex, "packet capture {Path} stopped by a disk error", Path);
            lock (_gate) Complete();
        }
        finally
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Closing the file failed: the trace may be incomplete, but the writer task must not fault (Dispose waits on it).
                _faulted = true;
                FaultLedger.Shared.Record("net.packet-capture", ex);
                _logger?.LogFault("net-packet-capture", ex, "packet capture {Path} could not be closed", Path);
            }
            finally
            {
                Live.TryRemove(this, out _);
                _finished.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) Complete();
        _writer.Wait();
    }
}
