using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// One gateway-worker TCP link: the length-prefixed frames of the loopback host, a single writer task
/// that batches queued frames into one write, and a byte bound on what is queued. Frames are sent in the order
/// <see cref="TryEnqueue"/> accepted them, which is the order the session protocol relies on (open reply before the first
/// server packet, sequence numbers ascending). Thread-safe enqueue; one reader (the owner's receive loop).
/// <para>
/// Integrity: a link starts unauthenticated (the handshake frames). Once both ends have proved the link secret the owner secures it
/// (<see cref="EnqueueSendKey"/> on the writer side, <see cref="ActivateReceiveKey"/> on the reader side, keys from <see cref="LinkAuth.DeriveKeys"/>)
/// and from then on every frame carries a <see cref="LinkMac.TagBytes"/>-byte tag over a per-direction frame counter and the frame.
/// <see cref="ReadFrameAsync"/> throws <see cref="InvalidDataException"/> for a frame whose tag does not verify, which the owners' receive loops
/// treat like any protocol break: the link closes. The tag is not encryption; frames are still readable by anyone on the path.
/// </para>
/// </summary>
internal sealed class GatewayLinkChannel : IAsyncDisposable
{
    /// <summary>
    /// Most bytes one link may have queued for its writer. A loopback peer drains far faster than a world produces, so a link
    /// over this bound has a peer that stopped reading; the link is closed rather than grown without limit (each client's own
    /// outbound bound is enforced by the gateway, World:MaxOutboundBytes per client).
    /// </summary>
    public const long MaxQueuedBytes = 64L * 1024 * 1024;

    /// <summary>Queue accounting per frame: the length prefix plus the largest tag, so enqueue and dequeue always agree whatever the secured state is.</summary>
    private const int WireOverhead = 4 + LinkMac.TagBytes;

    /// <summary>One queue entry: a frame to send, or (Frame null) the point from which the writer MACs with <see cref="NewSendKey"/>.</summary>
    private readonly record struct Outbound(byte[]? Frame, byte[]? NewSendKey);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<Outbound> _outbound = Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _writer;
    private long _queuedBytes;
    private int _disposed;
    private int _closing;
    private byte[]? _receiveKey;
    private ulong _receiveCounter;

    public GatewayLinkChannel(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Cancelled when the link closes (either side, a fault, or an over-bound queue).</summary>
    public CancellationToken Closed => _closed.Token;

    public bool IsOpen => !_closed.IsCancellationRequested;

    /// <summary>Queue one frame (type byte first). False when the link is closed or would exceed <see cref="MaxQueuedBytes"/> (it is then closed).</summary>
    public bool TryEnqueue(byte[] frame)
    {
        if (frame.Length > GatewayLinkFraming.MaxFrameBytes)
        {
            throw new InvalidOperationException($"frame of {frame.Length} B exceeds MaxFrameBytes ({GatewayLinkFraming.MaxFrameBytes} B)");
        }

        if (_closed.IsCancellationRequested)
        {
            return false;
        }

        if (Interlocked.Add(ref _queuedBytes, frame.Length + WireOverhead) > MaxQueuedBytes)
        {
            Close();
            return false;
        }

        if (!_outbound.Writer.TryWrite(new Outbound(frame, null)))
        {
            Interlocked.Add(ref _queuedBytes, -(frame.Length + WireOverhead));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Queue the switch to a MAC-protected send direction: every frame queued after this call is sent with a tag under <paramref name="key"/>
    /// (counter from 0); frames queued before it go out as they were. Call once, right after the last handshake frame, before any other frame is queued.
    /// False when the link is closed.
    /// </summary>
    public bool EnqueueSendKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return !_closed.IsCancellationRequested && _outbound.Writer.TryWrite(new Outbound(null, key));
    }

    /// <summary>
    /// From the next <see cref="ReadFrameAsync"/> on, require and verify a tag under <paramref name="key"/> on every frame (counter from 0).
    /// Call from the reader (the receive loop), right after reading the last unprotected frame; the reader is the only thread that touches this state.
    /// </summary>
    public void ActivateReceiveKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _receiveCounter = 0;
        _receiveKey = key;
    }

    /// <summary>The next frame (tag verified and removed once secured), or null at end of stream. Throws on a malformed length prefix or a frame whose tag does not verify.</summary>
    public async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        byte[]? key = _receiveKey;
        byte[]? wire = await GatewayLinkFraming.ReadFrameAsync(
            _stream, cancellationToken, GatewayLinkFraming.MaxFrameBytes + (key is null ? 0 : LinkMac.TagBytes)).ConfigureAwait(false);
        if (wire is null || key is null)
        {
            return wire;
        }

        if (wire.Length < LinkMac.TagBytes)
        {
            throw new InvalidDataException($"secured frame of {wire.Length} B is shorter than its {LinkMac.TagBytes} B tag");
        }

        int length = wire.Length - LinkMac.TagBytes;
        if (!LinkMac.Verify(key, _receiveCounter, wire.AsSpan(0, length), wire.AsSpan(length)))
        {
            throw new InvalidDataException("link frame failed its integrity check (altered, replayed, reordered or sent under another key)");
        }

        _receiveCounter++;
        return wire.AsSpan(0, length).ToArray();
    }

    /// <summary>Stop accepting frames, let the writer send what is queued (at most <paramref name="bound"/>), then close.</summary>
    public async Task FlushAndCloseAsync(TimeSpan bound)
    {
        _outbound.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // a peer that stopped reading: close below
        }

        Close();
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _outbound.Writer.TryComplete();
        try
        {
            _tcp.Client?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // already gone
        }

        _tcp.Close();
    }

    /// <summary>Writes the length prefix, the frame and (when <paramref name="key"/> is set) its tag for frame number <paramref name="counter"/>; returns the bytes written.</summary>
    private static int WriteWire(Span<byte> destination, byte[] frame, byte[]? key, ulong counter)
    {
        int tag = key is null ? 0 : LinkMac.TagBytes;
        BinaryPrimitives.WriteInt32LittleEndian(destination, frame.Length + tag);
        frame.CopyTo(destination[4..]);
        if (key is not null)
        {
            LinkMac.Compute(key, counter, frame, destination.Slice(4 + frame.Length, tag));
        }

        return 4 + frame.Length + tag;
    }

    private async Task WriteLoopAsync()
    {
        byte[] buffer = new byte[64 * 1024];
        byte[]? sendKey = null;
        ulong sendCounter = 0;
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(_closed.Token).ConfigureAwait(false))
            {
                int used = 0;
                while (_outbound.Reader.TryRead(out Outbound item))
                {
                    if (item.Frame is not { } frame)
                    {
                        sendKey = item.NewSendKey;
                        sendCounter = 0;
                        continue;
                    }

                    Interlocked.Add(ref _queuedBytes, -(frame.Length + WireOverhead));
                    int needed = frame.Length + 4 + (sendKey is null ? 0 : LinkMac.TagBytes);
                    if (used + needed > buffer.Length && used > 0)
                    {
                        await _stream.WriteAsync(buffer.AsMemory(0, used), _closed.Token).ConfigureAwait(false);
                        used = 0;
                    }

                    if (needed > buffer.Length)
                    {
                        // Rare (a frame over the 64 KiB batch buffer): one exact-size copy so the prefix, frame and tag go out in one write.
                        byte[] large = new byte[needed];
                        WriteWire(large, frame, sendKey, sendCounter);
                        sendCounter++;
                        await _stream.WriteAsync(large, _closed.Token).ConfigureAwait(false);
                        continue;
                    }

                    used += WriteWire(buffer.AsSpan(used, needed), frame, sendKey, sendCounter);
                    sendCounter++;
                }

                if (used > 0)
                {
                    await _stream.WriteAsync(buffer.AsMemory(0, used), _closed.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
            }
        }
        finally
        {
            Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transport", ex);
        }

        _tcp.Dispose();
        _closed.Dispose();
    }
}
