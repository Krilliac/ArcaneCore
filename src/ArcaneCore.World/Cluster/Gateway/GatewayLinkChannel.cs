using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// One gateway-worker TCP link: the length-prefixed frames of <see cref="LoopbackGatewayWorkerHost"/>, a single writer task
/// that batches queued frames into one write, and a byte bound on what is queued. Frames are sent in the order
/// <see cref="TryEnqueue"/> accepted them, which is the order the session protocol relies on (open reply before the first
/// server packet, sequence numbers ascending). Thread-safe enqueue; one reader (the owner's receive loop).
/// </summary>
internal sealed class GatewayLinkChannel : IAsyncDisposable
{
    /// <summary>
    /// Most bytes one link may have queued for its writer. A loopback peer drains far faster than a world produces, so a link
    /// over this bound has a peer that stopped reading; the link is closed rather than grown without limit (each client's own
    /// outbound bound is enforced by the gateway, World:MaxOutboundBytes per client).
    /// </summary>
    public const long MaxQueuedBytes = 64L * 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _writer;
    private long _queuedBytes;
    private int _disposed;
    private int _closing;

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
        if (frame.Length > LoopbackGatewayWorkerHost.MaxFrameBytes)
        {
            throw new InvalidOperationException($"frame of {frame.Length} B exceeds MaxFrameBytes ({LoopbackGatewayWorkerHost.MaxFrameBytes} B)");
        }

        if (_closed.IsCancellationRequested)
        {
            return false;
        }

        if (Interlocked.Add(ref _queuedBytes, frame.Length + 4) > MaxQueuedBytes)
        {
            Close();
            return false;
        }

        if (!_outbound.Writer.TryWrite(frame))
        {
            Interlocked.Add(ref _queuedBytes, -(frame.Length + 4));
            return false;
        }

        return true;
    }

    /// <summary>The next frame, or null at end of stream. Throws on a malformed length prefix.</summary>
    public ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken) =>
        LoopbackGatewayWorkerHost.ReadFrameAsync(_stream, cancellationToken);

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

    private async Task WriteLoopAsync()
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(_closed.Token).ConfigureAwait(false))
            {
                int used = 0;
                while (_outbound.Reader.TryRead(out byte[]? frame))
                {
                    Interlocked.Add(ref _queuedBytes, -(frame.Length + 4));
                    int needed = frame.Length + 4;
                    if (used + needed > buffer.Length && used > 0)
                    {
                        await _stream.WriteAsync(buffer.AsMemory(0, used), _closed.Token).ConfigureAwait(false);
                        used = 0;
                    }

                    if (needed > buffer.Length)
                    {
                        byte[] header = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
                        await _stream.WriteAsync(header, _closed.Token).ConfigureAwait(false);
                        await _stream.WriteAsync(frame, _closed.Token).ConfigureAwait(false);
                        continue;
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used, 4), frame.Length);
                    frame.CopyTo(buffer, used + 4);
                    used += needed;
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
                FaultLedger.Shared.Record("cluster-transport", ex);
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
            FaultLedger.Shared.Record("cluster-transport", ex);
        }

        _tcp.Dispose();
        _closed.Dispose();
    }
}
