using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Framed TCP loopback client on a Gateway node that connects to a <see cref="LoopbackGatewayWorkerHost"/>.
/// Starts its background receive loop BEFORE sending registration and awaiting the handshake reply
/// (<c>docs/CLUSTERING_DESIGN.md</c> §2.3, §4.4) so it can never deadlock on startup.
/// </summary>
public sealed class LoopbackGatewayWorkerClient : IGatewayWorkerTransport
{
    private readonly ClusterNodeManifest _gatewayManifest;
    private readonly IPEndPoint _workerEndPoint;
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _receiveLoopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<(bool Accepted, string Reason)> _helloAckTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ClusterSessionAttachResult>> _pendingAttaches = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingDetaches = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _pendingPacketAcks = new();

    private NetworkStream? _stream;
    private Task? _receiveTask;
    private long _nextRequestId;
    private volatile bool _isReceiveLoopRunning;
    private volatile bool _receiveLoopStartedBeforeRegisterSent;

    public LoopbackGatewayWorkerClient(ClusterNodeManifest gatewayManifest, IPEndPoint workerEndPoint)
    {
        ArgumentNullException.ThrowIfNull(gatewayManifest);
        ArgumentNullException.ThrowIfNull(workerEndPoint);
        _gatewayManifest = gatewayManifest;
        _workerEndPoint = workerEndPoint;
    }

    public event Action<ClusterSessionPacket>? OutboundPacketReceived;

    public event Action<ulong, long, ClusterSessionDetachReason>? SessionDetached;

    /// <summary>True while the client's background receive loop is actively running.</summary>
    public bool IsReceiveLoopRunning => _isReceiveLoopRunning;

    /// <summary>
    /// Diagnostic proof for <c>docs/CLUSTERING_DESIGN.md</c> §4.4: verifies that the client receive loop was
    /// started and actively awaiting frames BEFORE the registration frame was sent and awaited.
    /// </summary>
    public bool ReceiveLoopStartedBeforeRegisterSent => _receiveLoopStartedBeforeRegisterSent;

    public async Task ConnectAndRegisterAsync(CancellationToken cancellationToken = default)
    {
        await _tcp.ConnectAsync(_workerEndPoint.Address, _workerEndPoint.Port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();

        // CRITICAL INVARIANT (CLUSTERING_DESIGN.md:27,232):
        // Start the receive loop BEFORE sending registration and awaiting the registration reply.
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        await _receiveLoopStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _receiveLoopStartedBeforeRegisterSent = _isReceiveLoopRunning;

        byte[] helloFrame = LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(_gatewayManifest);
        await SendFrameAsync(helloFrame, cancellationToken).ConfigureAwait(false);

        (bool accepted, string reason) = await _helloAckTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new InvalidOperationException($"worker rejected gateway registration: {reason}");
        }
    }

    public async ValueTask<ClusterSessionAttachResult> AttachSessionAsync(
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default)
    {
        long reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<ClusterSessionAttachResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAttaches[reqId] = tcs;

        try
        {
            byte[] frame = new byte[1 + 36];
            Span<byte> span = frame;
            span[0] = (byte)GatewayWorkerFrameType.AttachSessionRequest;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), accountId);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(13, 8), characterGuid);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(21, 8), requestedGeneration);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(29, 8), expectedOwnerEpoch);

            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingAttaches.TryRemove(reqId, out _);
        }
    }

    public async ValueTask<bool> DetachSessionAsync(
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default)
    {
        long reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDetaches[reqId] = tcs;

        try
        {
            byte[] frame = new byte[1 + 8 + 8 + 8 + 1];
            Span<byte> span = frame;
            span[0] = (byte)GatewayWorkerFrameType.DetachSessionRequest;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(9, 8), characterGuid);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(17, 8), sessionGeneration);
            span[25] = (byte)reason;

            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingDetaches.TryRemove(reqId, out _);
        }
    }

    public async ValueTask<bool> SendToWorkerAsync(
        ClusterSessionPacket packet,
        CancellationToken cancellationToken = default)
    {
        if (packet.Payload.Length > WorldSession.MaxClientPacketSize)
        {
            throw new InvalidOperationException(
                $"client packet payload ({packet.Payload.Length} B) exceeds MaxClientPacketSize ({WorldSession.MaxClientPacketSize} B)");
        }

        long reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPacketAcks[reqId] = tcs;

        try
        {
            byte[] frame = LoopbackGatewayWorkerHost.SerializeClientPacketFrame(reqId, packet);
            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingPacketAcks.TryRemove(reqId, out _);
        }
    }

    private async ValueTask SendFrameAsync(byte[] frame, CancellationToken ct)
    {
        NetworkStream? stream = _stream ?? throw new InvalidOperationException("LoopbackGatewayWorkerClient is not connected");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoopbackGatewayWorkerHost.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _isReceiveLoopRunning = true;
        _receiveLoopStarted.TrySetResult();

        try
        {
            if (_stream is not { } stream)
            {
                Invariant.Check(false, "LoopbackGatewayWorkerClient.ReceiveLoopAsync started without an active NetworkStream.");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = await LoopbackGatewayWorkerHost.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (GatewayWorkerFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);

                switch (type)
                {
                    case GatewayWorkerFrameType.HelloAck:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 5)
                        {
                            throw new InvalidDataException($"truncated HelloAck frame ({span.Length} B)");
                        }

                        bool accepted = span[0] != 0;
                        int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(1, 4));
                        if (len < 0 || span.Length < 5 + len)
                        {
                            throw new InvalidDataException($"invalid HelloAck reason length ({len})");
                        }

                        string reason = len > 0 ? Encoding.UTF8.GetString(span.Slice(5, len)) : string.Empty;
                        _helloAckTcs.TrySetResult((accepted, reason));
                        break;
                    }

                    case GatewayWorkerFrameType.AttachSessionReply:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 29)
                        {
                            throw new InvalidDataException($"truncated AttachSessionReply frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        bool accepted = span[8] != 0;
                        long assignedGen = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(9, 8));
                        long workerEpoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(17, 8));
                        int reasonLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(25, 4));
                        if (reasonLen < 0 || span.Length < 29 + reasonLen)
                        {
                            throw new InvalidDataException($"invalid AttachSessionReply reason length ({reasonLen})");
                        }

                        string? reason = reasonLen > 0 ? Encoding.UTF8.GetString(span.Slice(29, reasonLen)) : null;

                        if (_pendingAttaches.TryGetValue(reqId, out TaskCompletionSource<ClusterSessionAttachResult>? tcs))
                        {
                            tcs.TrySetResult(new ClusterSessionAttachResult(accepted, assignedGen, workerEpoch, reason));
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.DetachSessionReply:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 9)
                        {
                            throw new InvalidDataException($"truncated DetachSessionReply frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        bool detached = span[8] != 0;
                        if (_pendingDetaches.TryGetValue(reqId, out TaskCompletionSource<bool>? tcs))
                        {
                            tcs.TrySetResult(detached);
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.ClientSessionPacketAck:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 9)
                        {
                            throw new InvalidDataException($"truncated ClientSessionPacketAck frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        bool accepted = span[8] != 0;
                        if (_pendingPacketAcks.TryGetValue(reqId, out TaskCompletionSource<bool>? tcs))
                        {
                            tcs.TrySetResult(accepted);
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.ServerSessionPacket:
                    {
                        ClusterSessionPacket packet = LoopbackGatewayWorkerHost.DeserializeSessionPacket(body);
                        OutboundPacketReceived?.Invoke(packet);
                        break;
                    }

                    case GatewayWorkerFrameType.ServerSessionDetached:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 17)
                        {
                            throw new InvalidDataException($"truncated ServerSessionDetached frame ({span.Length} B)");
                        }

                        ulong characterGuid = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
                        long sessionGeneration = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(8, 8));
                        var reason = (ClusterSessionDetachReason)span[16];
                        SessionDetached?.Invoke(characterGuid, sessionGeneration, reason);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }

            _helloAckTcs.TrySetException(ex);
        }
        finally
        {
            _isReceiveLoopRunning = false;
            var closedEx = new IOException("gateway–worker loopback connection closed");
            _helloAckTcs.TrySetException(closedEx);
            foreach (TaskCompletionSource<ClusterSessionAttachResult> tcs in _pendingAttaches.Values)
            {
                tcs.TrySetException(closedEx);
            }

            foreach (TaskCompletionSource<bool> tcs in _pendingDetaches.Values)
            {
                tcs.TrySetException(closedEx);
            }

            foreach (TaskCompletionSource<bool> tcs in _pendingPacketAcks.Values)
            {
                tcs.TrySetException(closedEx);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _tcp.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster-transport", ex);
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                {
                    FaultLedger.Shared.Record("cluster-transport", ex);
                }
            }
        }

        _tcp.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
