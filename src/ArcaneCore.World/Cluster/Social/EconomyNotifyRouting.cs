using System.Text;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Peer;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// One economy notification (a client packet) addressed to the worker that currently hosts <see cref="RecipientGuid"/>.
/// <see cref="Payload"/> is the finished packet body (built with <c>EconomyPackets</c>); <see cref="Sequence"/> is strictly increasing per router so the
/// receiving worker applies notifications in commit order. <see cref="RecipientSessionGeneration"/> is the presence session generation seen at routing time:
/// a worker drops the notification if the character has since logged in again under another generation.
/// </summary>
public sealed record EconomyNotification(
    WorldOpcode Opcode,
    byte[] Payload,
    ulong RecipientGuid,
    long RecipientSessionGeneration,
    string TargetWorkerId,
    long Sequence);

/// <summary>Outcome of <see cref="EconomyNotifyRouter.Notify"/>.</summary>
public enum EconomyNotifyStatus
{
    /// <summary>Queued for the recipient's current worker (which may be this one).</summary>
    Queued = 0,

    /// <summary>The recipient is not online: nothing to notify (the mail or auction state is read on login).</summary>
    RecipientOffline = 1,

    /// <summary>
    /// The letter is not deliverable yet (its deliver time is in the future): nothing was queued. The holder notifies later through
    /// <c>EconomyCommitNotifier.MailDelivered</c> when the letter becomes due, like the local path's NotifyDeliveredMail.
    /// </summary>
    Deferred = 2,
}

/// <summary>
/// Economy lease holder side of cross-worker notifications (M15 S2d, <c>docs/CLUSTERING_DESIGN.md</c> section 8 mail and auctions). After the holder commits a mail or an
/// auction result it calls <see cref="Notify"/>; the router finds the recipient's current worker by presence lookup and queues the packet for that worker. Workers
/// fetch their queue with a drain request over the peer link (<see cref="Handle"/> on the holder, <see cref="EconomyNotifyClient"/> on the worker): the peer link
/// is request/answer towards the lease holder, so delivery is pull-based exactly like <c>IClusterSocialRouter.DrainWorkerDeliveries</c>.
/// Delivery is best effort (a notification is only a client refresh hint): each worker queue holds at most <see cref="MaxQueuedPerWorker"/> entries and drops the oldest.
/// A drain request carries no idempotency id and is not retried: the batch is removed before the reply is sent, so a reply lost to a timeout loses that batch
/// (acceptable for a refresh hint; the mail and auction state is re-read on the next list or login).
/// Thread-safe: queues are guarded by one lock; the directory is the caller's thread-safe presence directory.
/// </summary>
public sealed class EconomyNotifyRouter(IRealmPresenceDirectory directory)
{
    public const int MaxQueuedPerWorker = 1024;

    /// <summary>Most notifications one drain returns.</summary>
    public const int MaxDrainBatch = 256;

    /// <summary>Longest worker id (chars) the router queues for or drains; bounds the encoded size of one item.</summary>
    public const int MaxWorkerIdLength = 64;

    /// <summary>
    /// Most encoded bytes one drain reply may hold: the peer link echo limit minus a 1 KB margin. With <see cref="EconomyNotifyWire.MaxPayloadLength"/> and
    /// <see cref="MaxWorkerIdLength"/> one item is at most 1248 B, so at least one item always fits.
    /// </summary>
    public const int MaxDrainReplyBytes = PeerLinkWire.MaxPayloadBytes - 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<EconomyNotification>> _queues = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>Looks up the recipient by presence and queues the packet for the worker it is on. Returns <see cref="EconomyNotifyStatus.RecipientOffline"/> when there is no presence entry.</summary>
    public EconomyNotifyStatus Notify(ulong recipientGuid, WorldOpcode opcode, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > EconomyNotifyWire.MaxPayloadLength)
        {
            throw new ArgumentException($"notification payload of {payload.Length} B exceeds {EconomyNotifyWire.MaxPayloadLength} B", nameof(payload));
        }

        if (directory.FindByGuid(recipientGuid) is not { } entry || string.IsNullOrEmpty(entry.WorkerId) || entry.WorkerId.Length > MaxWorkerIdLength)
        {
            return EconomyNotifyStatus.RecipientOffline;
        }

        lock (_gate)
        {
            if (!_queues.TryGetValue(entry.WorkerId, out List<EconomyNotification>? queue))
            {
                queue = [];
                _queues[entry.WorkerId] = queue;
            }

            queue.Add(new EconomyNotification(opcode, payload, recipientGuid, entry.SessionGeneration, entry.WorkerId, ++_sequence));
            if (queue.Count > MaxQueuedPerWorker)
            {
                queue.RemoveRange(0, queue.Count - MaxQueuedPerWorker);
            }
        }

        return EconomyNotifyStatus.Queued;
    }

    /// <summary>
    /// Removes and returns queued notifications of <paramref name="workerId"/>, oldest first: at most <see cref="MaxDrainBatch"/> and at most
    /// <see cref="MaxDrainReplyBytes"/> of encoded reply, so the reply always fits one peer link frame. The rest stays queued for the next drain.
    /// </summary>
    public IReadOnlyList<EconomyNotification> DrainWorker(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > MaxWorkerIdLength)
        {
            return [];
        }

        lock (_gate)
        {
            if (!_queues.TryGetValue(workerId, out List<EconomyNotification>? queue) || queue.Count == 0)
            {
                return [];
            }

            int take = 0;
            int bytes = EconomyNotifyWire.ReplyHeaderBytes;
            while (take < queue.Count && take < MaxDrainBatch)
            {
                bytes += EconomyNotifyWire.EncodedItemBytes(queue[take]);
                if (take > 0 && bytes > MaxDrainReplyBytes)
                {
                    break;
                }

                take++;
            }

            List<EconomyNotification> batch = queue.GetRange(0, take);
            queue.RemoveRange(0, take);
            return batch;
        }
    }

    /// <summary>Number of notifications waiting for <paramref name="workerId"/>.</summary>
    public int PendingCount(string workerId)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        lock (_gate)
        {
            return _queues.TryGetValue(workerId, out List<EconomyNotification>? queue) ? queue.Count : 0;
        }
    }

    /// <summary>
    /// Answers one drain request (see <see cref="EconomyNotifyWire"/>); never throws for a malformed request (the reply is then an error frame).
    /// Suitable as a <see cref="PeerLinkHost.PayloadHandler"/> body; a node that also serves presence routes by <c>EconomyNotifyWire.IsEconomyNotifyRequest</c>.
    /// </summary>
    public byte[] Handle(ReadOnlyMemory<byte> request)
    {
        try
        {
            return EconomyNotifyWire.Dispatch(this, request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return EconomyNotifyWire.EncodeError(ex.Message);
        }
    }
}

/// <summary>
/// Worker side of <see cref="EconomyNotifyRouter"/>: sends one drain request to the Economy holder per call. Thread-safe: no state of its own.
/// A lost link, timeout or refusal surfaces as <see cref="IOException"/>, <see cref="TimeoutException"/> or <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class EconomyNotifyClient(Func<byte[], CancellationToken, Task<byte[]>> send)
{
    /// <summary>A client that sends over <paramref name="link"/>. The shared peer locator reaches only the Social holder (<see cref="PeerLinkClient"/>), so this reaches the Economy holder only on a link the caller aimed at it; it has no production caller.</summary>
    public static EconomyNotifyClient OverPeerLink(PeerLinkClient link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new EconomyNotifyClient((request, ct) => link.EchoAsync(request, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<EconomyNotification>> DrainAsync(string workerId, CancellationToken cancellationToken = default)
    {
        byte[] reply = await send(EconomyNotifyWire.EncodeDrainRequest(workerId), cancellationToken).ConfigureAwait(false);
        return EconomyNotifyWire.DecodeDrainReply(reply);
    }
}

/// <summary>
/// Worker side delivery: applies drained notifications to the sessions this worker hosts. A notification is delivered only while the character is still here under the
/// generation it was routed for; <c>currentSessionGeneration</c> returns the character's local session generation or null if not online on this worker.
/// Thread affinity: call <see cref="Apply"/> on the thread that may touch the sessions (the world thread); no state of its own.
/// </summary>
public sealed class EconomyNotifyReceiver(Func<ulong, long?> currentSessionGeneration, Action<EconomyNotification> deliver)
{
    /// <summary>Delivers the still-valid notifications in the order given (the router drains in sequence order) and returns how many were delivered.</summary>
    public int Apply(IReadOnlyList<EconomyNotification> notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        int delivered = 0;
        foreach (EconomyNotification n in notifications)
        {
            if (currentSessionGeneration(n.RecipientGuid) is { } generation && generation == n.RecipientSessionGeneration)
            {
                deliver(n);
                delivered++;
            }
        }

        return delivered;
    }
}

/// <summary>
/// Encoding of the economy notification protocol over the peer link payload. Request: op byte (<see cref="OpDrainWorker"/>) and the worker id string. Reply: status
/// byte (0 ok, 1 error) then, for ok, an int32 count (at most <see cref="MaxListCount"/>) and per item: u16 opcode, int32 payload length and bytes, u64 recipient guid,
/// i64 session generation, worker id string, i64 sequence; for an error, a message string. Op values start above the presence ops so one dispatcher can serve both.
/// Little-endian <see cref="BinaryWriter"/> encoding; decoders throw <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/> on a frame that does not fit.
/// Pure functions except <see cref="Dispatch"/>; thread-safe.
/// </summary>
internal static class EconomyNotifyWire
{
    public const byte OpDrainWorker = 64;
    public const int MaxListCount = 4096;
    public const int MaxPayloadLength = 1024;

    /// <summary>Bytes before the first item of an ok reply: status byte and int32 count.</summary>
    public const int ReplyHeaderBytes = 5;

    private const byte StatusOk = 0;
    private const byte StatusError = 1;

    /// <summary>Exact encoded size of one reply item (BinaryWriter string: 7-bit length prefix then UTF-8).</summary>
    public static int EncodedItemBytes(EconomyNotification n)
    {
        int idBytes = Encoding.UTF8.GetByteCount(n.TargetWorkerId);
        int prefix = idBytes < 0x80 ? 1 : idBytes < 0x4000 ? 2 : 3;
        return 2 + 4 + n.Payload.Length + 8 + 8 + prefix + idBytes + 8;
    }

    public static bool IsEconomyNotifyRequest(ReadOnlySpan<byte> request) => request.Length > 0 && request[0] == OpDrainWorker;

    public static byte[] EncodeDrainRequest(string workerId)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(OpDrainWorker);
            w.Write(workerId);
        }

        return stream.ToArray();
    }

    public static byte[] EncodeError(string message)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(StatusError);
            w.Write(message.Length <= 200 ? message : message[..200]);
        }

        return stream.ToArray();
    }

    public static byte[] EncodeDrainReply(IReadOnlyList<EconomyNotification> items)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(StatusOk);
            w.Write(items.Count);
            foreach (EconomyNotification n in items)
            {
                w.Write((ushort)n.Opcode);
                w.Write(n.Payload.Length);
                w.Write(n.Payload);
                w.Write(n.RecipientGuid);
                w.Write(n.RecipientSessionGeneration);
                w.Write(n.TargetWorkerId);
                w.Write(n.Sequence);
            }
        }

        return stream.ToArray();
    }

    public static IReadOnlyList<EconomyNotification> DecodeDrainReply(byte[] reply)
    {
        using var r = new BinaryReader(new MemoryStream(reply, writable: false), Encoding.UTF8);
        if (r.ReadByte() != StatusOk)
        {
            throw new InvalidOperationException("the economy authority refused the request: " + r.ReadString());
        }

        int count = r.ReadInt32();
        if (count < 0 || count > MaxListCount)
        {
            throw new InvalidDataException($"list of {count} items (at most {MaxListCount})");
        }

        var items = new List<EconomyNotification>(count);
        for (int i = 0; i < count; i++)
        {
            var opcode = (WorldOpcode)r.ReadUInt16();
            int length = r.ReadInt32();
            if (length < 0 || length > MaxPayloadLength)
            {
                throw new InvalidDataException($"notification payload of {length} B (at most {MaxPayloadLength})");
            }

            byte[] payload = r.ReadBytes(length);
            if (payload.Length != length)
            {
                throw new EndOfStreamException();
            }

            items.Add(new EconomyNotification(opcode, payload, r.ReadUInt64(), r.ReadInt64(), r.ReadString(), r.ReadInt64()));
        }

        return items;
    }

    public static byte[] Dispatch(EconomyNotifyRouter router, ReadOnlyMemory<byte> request)
    {
        using var r = new BinaryReader(new MemoryStream(request.ToArray(), writable: false), Encoding.UTF8);
        byte op = r.ReadByte();
        if (op != OpDrainWorker)
        {
            throw new InvalidDataException($"unknown economy notify op {op}");
        }

        return EncodeDrainReply(router.DrainWorker(r.ReadString()));
    }
}
