using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Framed TCP loopback host on a Worker node that validates peer <see cref="ClusterNodeManifest"/> admission
/// during handshake and bridges gateway session traffic onto <see cref="LocalGatewayWorkerTransport"/>.
/// </summary>
public sealed class LoopbackGatewayWorkerHost : IAsyncDisposable
{
    public const int MaxFrameBytes = WorldSession.MaxServerPayload + 256;
    private const int OutboundQueueCapacity = 1024;

    private readonly ClusterNodeManifest _workerManifest;
    private readonly LocalGatewayWorkerTransport _localTransport;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ClientConnection> _connections = new();
    private Task? _acceptTask;
    private int _nextConnectionId;

    private sealed class ClientConnection : IAsyncDisposable
    {
        private static readonly TimeSpan SendDrainTimeout = TimeSpan.FromSeconds(2);

        public required LoopbackGatewayWorkerHost Owner { get; init; }
        public required int Id { get; init; }
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public required Channel<byte[]> SendChannel { get; init; }
        public Task? ReceiveTask { get; set; }
        public Task? SendTask { get; set; }
        public volatile bool Admitted;

        public async ValueTask DisposeAsync()
        {
            Owner.DisposeStartedForTest?.Invoke();
            SendChannel.Writer.TryComplete();

            // Drain queued frames (notably the HelloAck rejection) before closing: closing first would discard
            // the unsent ack or turn the close into an RST that the peer sees before it reads the ack.
            if (SendTask is not null)
            {
                try
                {
                    await SendTask.WaitAsync(SendDrainTimeout).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not (TimeoutException or OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                    {
                        FaultLedger.Shared.Record("cluster.transport", ex);
                    }
                }
            }

            try
            {
                // Graceful FIN after the last queued byte, so the peer reads everything before seeing EOF.
                // This avoids a reset only when no unread incoming data is pending; closing a socket that still
                // holds unread received bytes makes the OS send an RST regardless.
                Tcp.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (ObjectDisposedException or SocketException or IOException or InvalidOperationException))
                {
                    FaultLedger.Shared.Record("cluster.transport", ex);
                }
            }

            try
            {
                Tcp.Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (ObjectDisposedException or SocketException or IOException))
                {
                    FaultLedger.Shared.Record("cluster.transport", ex);
                }
            }

            if (SendTask is not null)
            {
                try
                {
                    await SendTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                    {
                        FaultLedger.Shared.Record("cluster.transport", ex);
                    }
                }
            }

            if (ReceiveTask is not null)
            {
                try
                {
                    await ReceiveTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
                    {
                        FaultLedger.Shared.Record("cluster.transport", ex);
                    }
                }
            }

            Tcp.Dispose();
        }
    }

    public LoopbackGatewayWorkerHost(
        ClusterNodeManifest workerManifest,
        LocalGatewayWorkerTransport localTransport,
        int port = 0)
    {
        ArgumentNullException.ThrowIfNull(workerManifest);
        ArgumentNullException.ThrowIfNull(localTransport);

        _workerManifest = workerManifest;
        _localTransport = localTransport;
        _listener = new TcpListener(IPAddress.Loopback, port);

        _localTransport.OutboundPacketForConnectionReceived += OnWorkerOutboundPacket;
        _localTransport.SessionDetachedForConnection += OnWorkerSessionDetached;
    }

    /// <summary>Test seam (instance state): awaited by the send loop before each frame is written.</summary>
    internal Func<Task>? BeforeSendFrameForTest { get; set; }

    /// <summary>Test seam (instance state): invoked when a connection's disposal starts.</summary>
    internal Action? DisposeStartedForTest { get; set; }

    public IPEndPoint BoundEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
                break;
            }

            client.NoDelay = true;
            int id = Interlocked.Increment(ref _nextConnectionId);
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var conn = new ClientConnection
            {
                Owner = this,
                Id = id,
                Tcp = client,
                Stream = client.GetStream(),
                SendChannel = channel,
            };

            _connections[id] = conn;
            conn.SendTask = Task.Run(() => SendLoopAsync(conn, ct));
            conn.ReceiveTask = Task.Run(() => ReceiveClientLoopAsync(conn, ct));
        }
    }

    private async Task SendLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        try
        {
            await foreach (byte[] frame in conn.SendChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (BeforeSendFrameForTest is { } gate)
                {
                    await gate().ConfigureAwait(false);
                }

                await WriteFrameAsync(conn.Stream, frame, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException or IOException))
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
            }
        }
    }

    private async Task ReceiveClientLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = await ReadFrameAsync(conn.Stream, ct).ConfigureAwait(false);
                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                var type = (GatewayWorkerFrameType)frame[0];
                ReadOnlyMemory<byte> body = frame.AsMemory(1);

                if (!conn.Admitted && type != GatewayWorkerFrameType.HelloRegister)
                {
                    break;
                }

                switch (type)
                {
                    case GatewayWorkerFrameType.HelloRegister:
                    {
                        ClusterNodeManifest peer = DeserializeManifest(body.Span);
                        bool admitted = _workerManifest.TryAdmit(peer, out string? reason);
                        conn.Admitted = admitted;
                        byte[] ack = BuildHelloAckFrame(admitted, reason ?? string.Empty);
                        await conn.SendChannel.Writer.WriteAsync(ack, ct).ConfigureAwait(false);
                        if (!admitted)
                        {
                            conn.SendChannel.Writer.TryComplete();
                            return;
                        }

                        break;
                    }

                    case GatewayWorkerFrameType.AttachSessionRequest:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 36)
                        {
                            throw new InvalidDataException($"truncated AttachSessionRequest frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        int accountId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
                        ulong characterGuid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(12, 8));
                        long requestedGeneration = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
                        long expectedOwnerEpoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(28, 8));

                        ClusterSessionAttachResult res = await _localTransport.AttachSessionCoreAsync(
                            conn.Id,
                            accountId,
                            characterGuid,
                            requestedGeneration,
                            expectedOwnerEpoch,
                            ct).ConfigureAwait(false);

                        byte[] reply = BuildAttachReplyFrame(reqId, res);
                        await conn.SendChannel.Writer.WriteAsync(reply, ct).ConfigureAwait(false);
                        break;
                    }

                    case GatewayWorkerFrameType.DetachSessionRequest:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 25)
                        {
                            throw new InvalidDataException($"truncated DetachSessionRequest frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        ulong characterGuid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
                        long sessionGeneration = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(16, 8));
                        var reason = (ClusterSessionDetachReason)span[24];

                        bool detached = await _localTransport.DetachSessionCoreAsync(
                            conn.Id,
                            characterGuid,
                            sessionGeneration,
                            reason,
                            ct).ConfigureAwait(false);

                        byte[] reply = new byte[1 + 8 + 1];
                        reply[0] = (byte)GatewayWorkerFrameType.DetachSessionReply;
                        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(1, 8), reqId);
                        reply[9] = detached ? (byte)1 : (byte)0;
                        await conn.SendChannel.Writer.WriteAsync(reply, ct).ConfigureAwait(false);
                        break;
                    }

                    case GatewayWorkerFrameType.ClientSessionPacket:
                    {
                        ReadOnlySpan<byte> span = body.Span;
                        if (span.Length < 52)
                        {
                            throw new InvalidDataException($"truncated ClientSessionPacket frame ({span.Length} B)");
                        }

                        long reqId = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
                        ClusterSessionPacket packet = DeserializeSessionPacket(body[8..]);
                        bool accepted = await _localTransport.SendToWorkerAsync(packet, ct).ConfigureAwait(false);

                        byte[] ack = new byte[1 + 8 + 1];
                        ack[0] = (byte)GatewayWorkerFrameType.ClientSessionPacketAck;
                        BinaryPrimitives.WriteInt64LittleEndian(ack.AsSpan(1, 8), reqId);
                        ack[9] = accepted ? (byte)1 : (byte)0;
                        await conn.SendChannel.Writer.WriteAsync(ack, ct).ConfigureAwait(false);
                        break;
                    }
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
            if (_connections.TryRemove(conn.Id, out _))
            {
                await _localTransport.DetachAllForConnectionAsync(conn.Id, CancellationToken.None).ConfigureAwait(false);
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnWorkerOutboundPacket(int connectionId, ClusterSessionPacket packet)
    {
        if (connectionId <= 0 || !_connections.TryGetValue(connectionId, out ClientConnection? conn) || !conn.Admitted)
        {
            return;
        }

        byte[] frame = SerializeServerPacketFrame(packet);
        conn.SendChannel.Writer.TryWrite(frame);
    }

    private void OnWorkerSessionDetached(
        int connectionId,
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason)
    {
        if (connectionId <= 0 || !_connections.TryGetValue(connectionId, out ClientConnection? conn) || !conn.Admitted)
        {
            return;
        }

        byte[] frame = new byte[1 + 8 + 8 + 1];
        frame[0] = (byte)GatewayWorkerFrameType.ServerSessionDetached;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1, 8), characterGuid);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(9, 8), sessionGeneration);
        frame[17] = (byte)reason;
        conn.SendChannel.Writer.TryWrite(frame);
    }

    public async ValueTask DisposeAsync()
    {
        _localTransport.OutboundPacketForConnectionReceived -= OnWorkerOutboundPacket;
        _localTransport.SessionDetachedForConnection -= OnWorkerSessionDetached;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (ObjectDisposedException or SocketException))
            {
                FaultLedger.Shared.Record("cluster.transport", ex);
            }
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (OperationCanceledException or ObjectDisposedException or SocketException))
                {
                    FaultLedger.Shared.Record("cluster.transport", ex);
                }
            }
        }

        foreach (KeyValuePair<int, ClientConnection> kvp in _connections)
        {
            if (_connections.TryRemove(kvp.Key, out ClientConnection? conn))
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }

        _cts.Dispose();
    }

    internal static async ValueTask WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
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

    internal static async ValueTask<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
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

    internal static byte[] BuildHelloRegisterFrame(ClusterNodeManifest manifest)
    {
        byte[] nodeIdBytes = Encoding.UTF8.GetBytes(manifest.NodeId);
        byte[] contentBytes = Encoding.UTF8.GetBytes(manifest.ContentRevision);
        int total = 1 + 4 + 4 + nodeIdBytes.Length + 16 + 4 + 4 + 4 + 4 + 4 + contentBytes.Length;
        byte[] buf = new byte[total];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.HelloRegister;
        int offset = 1;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.RealmId);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), nodeIdBytes.Length);
        offset += 4;
        nodeIdBytes.CopyTo(span.Slice(offset, nodeIdBytes.Length));
        offset += nodeIdBytes.Length;
        manifest.NodeIncarnation.TryWriteBytes(span.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), (int)manifest.Role);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.ContractVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.CharactersSchemaVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), manifest.WorldSchemaVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), contentBytes.Length);
        offset += 4;
        contentBytes.CopyTo(span.Slice(offset, contentBytes.Length));
        return buf;
    }

    internal static ClusterNodeManifest DeserializeManifest(ReadOnlySpan<byte> span)
    {
        int offset = 0;
        int realmId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int nodeIdLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        string nodeId = Encoding.UTF8.GetString(span.Slice(offset, nodeIdLen));
        offset += nodeIdLen;
        var incarnation = new Guid(span.Slice(offset, 16));
        offset += 16;
        var role = (ClusterRole)BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int contractVer = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int charSchema = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int worldSchema = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int contentLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        string contentRev = Encoding.UTF8.GetString(span.Slice(offset, contentLen));
        return new ClusterNodeManifest(
            realmId,
            nodeId,
            incarnation,
            role,
            contractVer,
            charSchema,
            worldSchema,
            contentRev);
    }

    internal static byte[] BuildHelloAckFrame(bool accepted, string reason)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        byte[] buf = new byte[1 + 1 + 4 + reasonBytes.Length];
        buf[0] = (byte)GatewayWorkerFrameType.HelloAck;
        buf[1] = accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(2, 4), reasonBytes.Length);
        reasonBytes.CopyTo(buf.AsSpan(6));
        return buf;
    }

    internal static byte[] BuildAttachReplyFrame(long reqId, ClusterSessionAttachResult result)
    {
        byte[] reasonBytes = Encoding.UTF8.GetBytes(result.RejectionReason ?? string.Empty);
        byte[] buf = new byte[1 + 8 + 1 + 8 + 8 + 4 + reasonBytes.Length];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.AttachSessionReply;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
        span[9] = result.Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(10, 8), result.AssignedGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(18, 8), result.WorkerOwnerEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(26, 4), reasonBytes.Length);
        reasonBytes.CopyTo(span[30..]);
        return buf;
    }

    internal static byte[] SerializeClientPacketFrame(long reqId, ClusterSessionPacket packet)
    {
        ReadOnlySpan<byte> payload = packet.Payload.Span;
        byte[] buf = new byte[1 + 8 + 44 + payload.Length];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.ClientSessionPacket;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), reqId);
        WriteSessionPacketBody(span[9..], packet);
        return buf;
    }

    internal static byte[] SerializeServerPacketFrame(ClusterSessionPacket packet)
    {
        ReadOnlySpan<byte> payload = packet.Payload.Span;
        byte[] buf = new byte[1 + 44 + payload.Length];
        Span<byte> span = buf;
        span[0] = (byte)GatewayWorkerFrameType.ServerSessionPacket;
        WriteSessionPacketBody(span[1..], packet);
        return buf;
    }

    internal static void WriteSessionPacketBody(Span<byte> span, ClusterSessionPacket packet)
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

    /// <summary>Bytes of the envelope header that precedes the payload (<see cref="WriteSessionPacketBody"/>).</summary>
    internal const int SessionPacketHeaderBytes = 44;

    internal static ClusterSessionPacket DeserializeSessionPacket(ReadOnlyMemory<byte> memory)
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
