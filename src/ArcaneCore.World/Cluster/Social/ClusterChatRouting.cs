using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>What a <see cref="ClusterChatFrame"/> asks the receiving worker to do. Values are wire values: append only.</summary>
public enum ClusterChatFrameKind : byte
{
    /// <summary>A whisper for the recipient: the receiving worker applies the whisper rules and answers with a <see cref="WhisperReply"/>.</summary>
    Whisper = 1,

    /// <summary>The answer to a whisper (the inform line, the away/busy auto-reply, or a refusal): send the packets to the whisperer.</summary>
    WhisperReply = 2,

    /// <summary>Party, raid, raid leader or raid warning chat of a group.</summary>
    Group = 3,

    /// <summary>Guild chat.</summary>
    Guild = 4,

    /// <summary>Officer chat.</summary>
    Officer = 5,

    /// <summary>A custom or built-in chat channel line.</summary>
    Channel = 6,
}

/// <summary>One server packet carried inside a <see cref="ClusterChatFrame"/> (already built by the originating worker, sent to the recipient unchanged).</summary>
public sealed record ClusterChatPacket(WorldOpcode Opcode, byte[] Payload);

/// <summary>
/// The payload of a chat delivery between workers (carried as the base64 of <see cref="ToBytes"/> in <see cref="ClusterSocialMessage.PayloadText"/>, so it travels the existing
/// <c>Route</c> request unchanged). Layout, little-endian, strings 7-bit length-prefixed UTF-8: u8 version (1), u8 kind, string origin worker, u64 sender guid, u8 sender team,
/// u8 sender security, bool ignore-exempt, u8 sub-group (255: none), nullable string channel, nullable string mute notice, string text, u8 chat tag, u64 whisper-noted guid,
/// i32 packet count (at most <see cref="MaxPackets"/>), then per packet u16 opcode, i32 length (at most <see cref="MaxPacketBytes"/>), bytes. Append-only: a new field raises the
/// version. Immutable; thread-safe.
/// </summary>
public sealed record ClusterChatFrame
{
    public const byte Version = 1;
    public const int MaxPackets = 16;
    public const int MaxPacketBytes = ushort.MaxValue;

    /// <summary>The <see cref="SubGroup"/> value of a frame that is not limited to one sub-group.</summary>
    public const byte NoSubGroup = 255;

    public ClusterChatFrameKind Kind { get; init; }

    /// <summary>The worker that built the frame; set by <see cref="ClusterChatRouting"/> when it sends. A worker never applies a frame it originated (it served its local recipients itself).</summary>
    public string OriginWorkerId { get; init; } = string.Empty;

    /// <summary>The speaker (whisper request, group, guild and channel lines) or, in a reply, the character who answered.</summary>
    public ulong SenderGuid { get; init; }

    /// <summary>The speaker's <c>Team</c> as a byte (0 Alliance, 1 Horde): faction rules of whispers and channels.</summary>
    public byte SenderTeam { get; init; }

    /// <summary>The speaker's <c>AccountSecurity</c> as a byte: staff rules of whispers.</summary>
    public byte SenderSecurity { get; init; }

    /// <summary>True for a channel line of a moderator, which ignore lists do not filter (vmangos Channel::Say passes no exception for a moderator).</summary>
    public bool IgnoreExempt { get; init; }

    /// <summary>Party chat only: the speaker's sub-group; recipients in another sub-group do not get the line.</summary>
    public byte SubGroup { get; init; } = NoSubGroup;

    /// <summary>The channel name of a <see cref="ClusterChatFrameKind.Channel"/> line.</summary>
    public string? Channel { get; init; }

    /// <summary>Whisper request: the notice the speaker gets while muted (it may still whisper staff), or null.</summary>
    public string? MuteNotice { get; init; }

    /// <summary>Whisper request: the message text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Whisper request: the speaker's <c>ChatTag</c> as a byte.</summary>
    public byte ChatTag { get; init; }

    /// <summary>Whisper reply: the character that was whispered (the whisperer's worker records it as allowed to whisper back), or 0.</summary>
    public ulong NoteWhisperGuid { get; init; }

    /// <summary>The packets to send (group, guild, channel lines and whisper replies); empty in a whisper request.</summary>
    public IReadOnlyList<ClusterChatPacket> Packets { get; init; } = [];

    public byte[] ToBytes()
    {
        if (Packets.Count > MaxPackets)
        {
            throw new InvalidOperationException($"a chat frame carries at most {MaxPackets} packets");
        }

        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            w.Write((byte)Kind);
            w.Write(OriginWorkerId);
            w.Write(SenderGuid);
            w.Write(SenderTeam);
            w.Write(SenderSecurity);
            w.Write(IgnoreExempt);
            w.Write(SubGroup);
            WriteNullableString(w, Channel);
            WriteNullableString(w, MuteNotice);
            w.Write(Text);
            w.Write(ChatTag);
            w.Write(NoteWhisperGuid);
            w.Write(Packets.Count);
            foreach (ClusterChatPacket packet in Packets)
            {
                if (packet.Payload.Length > MaxPacketBytes)
                {
                    throw new InvalidOperationException($"a chat frame packet is at most {MaxPacketBytes} bytes");
                }

                w.Write((ushort)packet.Opcode);
                w.Write(packet.Payload.Length);
                w.Write(packet.Payload);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Decodes a frame; throws <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/> for bytes that are not one.</summary>
    public static ClusterChatFrame FromBytes(ReadOnlySpan<byte> bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes.ToArray(), writable: false), Encoding.UTF8);
        byte version = r.ReadByte();
        if (version != Version)
        {
            throw new InvalidDataException($"chat frame version {version} (expected {Version})");
        }

        byte kind = r.ReadByte();
        if (kind is < (byte)ClusterChatFrameKind.Whisper or > (byte)ClusterChatFrameKind.Channel)
        {
            throw new InvalidDataException($"chat frame kind {kind}");
        }

        string origin = r.ReadString();
        ulong senderGuid = r.ReadUInt64();
        byte team = r.ReadByte();
        byte security = r.ReadByte();
        bool exempt = r.ReadBoolean();
        byte subGroup = r.ReadByte();
        string? channel = ReadNullableString(r);
        string? mute = ReadNullableString(r);
        string text = r.ReadString();
        byte tag = r.ReadByte();
        ulong note = r.ReadUInt64();
        int count = r.ReadInt32();
        if (count < 0 || count > MaxPackets)
        {
            throw new InvalidDataException($"chat frame with {count} packets (at most {MaxPackets})");
        }

        var packets = new List<ClusterChatPacket>(count);
        for (int i = 0; i < count; i++)
        {
            ushort opcode = r.ReadUInt16();
            int length = r.ReadInt32();
            if (length < 0 || length > MaxPacketBytes)
            {
                throw new InvalidDataException($"chat frame packet of {length} bytes (at most {MaxPacketBytes})");
            }

            byte[] payload = r.ReadBytes(length);
            if (payload.Length != length)
            {
                throw new EndOfStreamException();
            }

            packets.Add(new ClusterChatPacket((WorldOpcode)opcode, payload));
        }

        return new ClusterChatFrame
        {
            Kind = (ClusterChatFrameKind)kind,
            OriginWorkerId = origin,
            SenderGuid = senderGuid,
            SenderTeam = team,
            SenderSecurity = security,
            IgnoreExempt = exempt,
            SubGroup = subGroup,
            Channel = channel,
            MuteNotice = mute,
            Text = text,
            ChatTag = tag,
            NoteWhisperGuid = note,
            Packets = packets,
        };
    }

    private static void WriteNullableString(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
        {
            w.Write(value);
        }
    }

    private static string? ReadNullableString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
}

/// <summary>The character that speaks (or answers): what the social router needs to authorize a message. Values come from the world thread's player.</summary>
public readonly record struct ClusterChatSender(ulong Guid, string Name, int AccountId);

/// <summary>A chat frame addressed to one local character, after the stale-session check: apply it on the world thread (<see cref="ClusterChatRouting.IsCurrent"/> again first).</summary>
public sealed record ClusterChatDelivery(ulong RecipientGuid, long RecipientSessionGeneration, long DeliverySequence, ClusterChatFrame Frame);

/// <summary>
/// Chat between workers (M15.2 S2c). The world thread hands a line to one of the <c>Send</c> members after serving its own local recipients; the line is routed through the realm
/// social router (<see cref="IClusterSocialRouter.Route"/>, held by the Social lease holder, over the peer link when that is another node) as a <see cref="ClusterSocialMessage"/>
/// whose payload is a <see cref="ClusterChatFrame"/>, stamped by the router with each recipient's session generation and a realm-wide delivery sequence. Each worker's pump
/// (<see cref="PumpOnce"/>, every interval after <see cref="Start"/>) drains the deliveries queued for it, drops a frame it originated (already served locally) and a delivery whose
/// generation is not the one this worker registered for the character any more (a replaced or logged-out session), and hands the rest to <see cref="Deliver"/>.
/// <para>
/// Same-worker delivery never passes here: callers serve local recipients themselves and the origin check keeps a worker from applying its own frames, so a single-worker realm
/// behaves as before. Channel membership (join, leave) and moderation (owner, moderators, bans, mutes) are kept at the router (the presence holder) by <see cref="JoinChannel"/> and
/// <see cref="ModerateChannel"/>: the router refuses a join of a banned character and a line of a banned or muted speaker, and the pump drops a delivery whose speaker has been
/// barred since (<see cref="IsSpeakerBarred"/>).
/// </para>
/// <para>
/// Threading: every <c>Send</c>, <c>JoinChannel</c>, <c>LeaveChannel</c> and <see cref="QueryWho"/> member only enqueues (never blocks, safe on the world thread); one background
/// task runs the queued operations in order, so a join reaches the router before the first line spoken in the channel, and a failed operation (link down, refusal) is dropped
/// with a debug line: chat is best effort. The queue holds at most <see cref="MaxQueuedOperations"/> operations (the newest are dropped when it is full). The pump runs on the
/// thread pool and blocks while the router is remote, so it must not be driven from the world thread. Ownership: owns the queue task and the pump task; <see cref="DisposeAsync"/> stops both.
/// </para>
/// </summary>
public sealed class ClusterChatRouting : IAsyncDisposable
{
    /// <summary>Most operations waiting for the sender task; beyond it a new one is dropped.</summary>
    public const int MaxQueuedOperations = 4096;

    /// <summary>Most bounded batches one <see cref="PumpOnce"/> pass takes from the holder; what is left stays queued for the next pass.</summary>
    public const int MaxPumpBatches = 64;

    /// <summary>Most channels whose moderation snapshot is cached for <see cref="IsSpeakerBarred"/>.</summary>
    private const int MaxCachedChannels = 1024;

    private readonly IClusterSocialRouter _router;
    private readonly IRealmPresenceDirectory _directory;
    private readonly string _workerId;
    private readonly string _gatewayId;
    private readonly Func<ulong, long> _registeredGeneration;
    private readonly ILogger _logger;
    private readonly Channel<Action> _queue = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(MaxQueuedOperations) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private Task _pump = Task.CompletedTask;
    private Action<ClusterChatDelivery>? _deliver;
    private long _sequence;
    private readonly ConcurrentDictionary<string, ChannelModerationState> _moderation = new(StringComparer.OrdinalIgnoreCase);
    private long _staleDropped;
    private long _queueDropped;
    private long _barredDropped;
    private int _started;
    private int _disposed;

    /// <param name="router">The realm social router (the lease-routed one in production).</param>
    /// <param name="directory">The realm presence directory (name lookup, who).</param>
    /// <param name="workerId">This worker's id, as published in presence.</param>
    /// <param name="gatewayId">The gateway id published in presence.</param>
    /// <param name="registeredGeneration">The session generation this worker's presence registration of a character holds, or 0 when it has none (the publisher's registration).</param>
    public ClusterChatRouting(
        IClusterSocialRouter router,
        IRealmPresenceDirectory directory,
        string workerId,
        string gatewayId,
        Func<ulong, long> registeredGeneration,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        ArgumentNullException.ThrowIfNull(registeredGeneration);
        _router = router;
        _directory = directory;
        _workerId = workerId;
        _gatewayId = gatewayId;
        _registeredGeneration = registeredGeneration;
        _logger = logger ?? NullLogger.Instance;
        _sender = Task.Run(RunSenderAsync, CancellationToken.None);
    }

    /// <summary>This worker's id.</summary>
    public string WorkerId => _workerId;

    /// <summary>Where accepted deliveries go (the chat feature posts them to the world thread). While null the pump leaves the queued deliveries at the router.</summary>
    public Action<ClusterChatDelivery>? Deliver
    {
        get => Volatile.Read(ref _deliver);
        set => Volatile.Write(ref _deliver, value);
    }

    /// <summary>Deliveries dropped because the recipient's session was replaced or gone.</summary>
    public long StaleDropped => Interlocked.Read(ref _staleDropped);

    /// <summary>Operations dropped because the queue was full.</summary>
    public long QueueDropped => Interlocked.Read(ref _queueDropped);

    /// <summary>Channel lines dropped at the pump because their speaker was banned or muted at the holder.</summary>
    public long BarredDropped => Interlocked.Read(ref _barredDropped);

    /// <summary>
    /// True when the holder's moderation state, as the pump last read it for <paramref name="channelName"/>, bans or mutes <paramref name="speakerGuid"/>. Never blocks (a cache the pump
    /// refreshes for every channel delivery it handles), so the world thread may call it when applying a delivery.
    /// </summary>
    public bool IsSpeakerBarred(string channelName, ulong speakerGuid)
        => !string.IsNullOrEmpty(channelName) && _moderation.TryGetValue(channelName, out ChannelModerationState? state) && state.IsBarred(speakerGuid);

    /// <summary>True when this worker's registration of <paramref name="characterGuid"/> is at <paramref name="generation"/>. Thread-safe.</summary>
    public bool IsCurrent(ulong characterGuid, long generation) => generation > 0 && _registeredGeneration(characterGuid) == generation;

    // ---- outbound ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whisper to a character that is not on this worker. Looks the name up in realm presence; <paramref name="notFound"/> runs (on a pool thread) when no other worker has it
    /// online. The receiving worker applies the whisper rules and answers through <see cref="SendReply"/>.
    /// </summary>
    public void SendWhisper(ClusterChatSender sender, string targetName, ClusterChatFrame request, Action notFound)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(notFound);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        long generation = _registeredGeneration(sender.Guid);
        Enqueue(() =>
        {
            RealmPresenceEntry? target = _directory.FindByName(targetName);
            if (target is null || string.Equals(target.WorkerId, _workerId, StringComparison.Ordinal))
            {
                notFound();
                return;
            }

            RouteFrame(SocialRouteChannel.Whisper, sender, generation, request, targetGuid: target.CharacterGuid);
        });
    }

    /// <summary>The answer of a whispered character to the whisperer (<paramref name="whispererGuid"/>), routed as a whisper from the answering character.</summary>
    public void SendReply(ClusterChatSender answering, ulong whispererGuid, ClusterChatFrame reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        long generation = _registeredGeneration(answering.Guid);
        Enqueue(() => RouteFrame(SocialRouteChannel.Whisper, answering, generation, reply, targetGuid: whispererGuid));
    }

    /// <summary>A line to the members of <paramref name="groupId"/> on other workers.</summary>
    public void SendToGroup(ClusterChatSender sender, int groupId, ClusterChatFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long generation = _registeredGeneration(sender.Guid);
        Enqueue(() => RouteFrame(SocialRouteChannel.GroupChat, sender, generation, frame, groupId: groupId));
    }

    /// <summary>A guild or officer line to the members of <paramref name="guildId"/> on other workers (the frame's kind says which).</summary>
    public void SendToGuild(ClusterChatSender sender, int guildId, ClusterChatFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long generation = _registeredGeneration(sender.Guid);
        Enqueue(() => RouteFrame(SocialRouteChannel.GuildChat, sender, generation, frame, guildId: guildId));
    }

    /// <summary>A channel line to the members of <paramref name="channelName"/> on other workers; the sender must have joined it through <see cref="JoinChannel"/>.</summary>
    public void SendToChannel(ClusterChatSender sender, string channelName, ClusterChatFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        long generation = _registeredGeneration(sender.Guid);
        Enqueue(() => RouteFrame(SocialRouteChannel.CustomChannel, sender, generation, frame, channelName: channelName));
    }

    /// <summary>The character joined <paramref name="channelName"/> on this worker: subscribe it at the router so other workers' lines reach it.</summary>
    public void JoinChannel(ClusterChatSender member, string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        long generation = _registeredGeneration(member.Guid);
        if (generation <= 0)
        {
            return;
        }

        Enqueue(() => _router.JoinChannel(channelName, member.Guid, generation));
    }

    /// <summary>The character left <paramref name="channelName"/> on this worker.</summary>
    public void LeaveChannel(ClusterChatSender member, string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        long generation = _registeredGeneration(member.Guid);
        if (generation <= 0)
        {
            return;
        }

        Enqueue(() => _router.LeaveChannel(channelName, member.Guid, generation));
    }

    /// <summary>
    /// A moderation change decided on this worker (owner claimed or handed on, moderator, ban, mute) is sent to the holder, which applies it only if <paramref name="actorGuid"/> may
    /// moderate the channel there (<see cref="IClusterSocialRouter.ModerateChannel"/>). Queued behind the join and leave operations before it.
    /// </summary>
    public void ModerateChannel(string channelName, ChannelModerationAction action, ulong actorGuid, ulong targetGuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        Enqueue(() =>
        {
            if (!_router.ModerateChannel(channelName, action, actorGuid, targetGuid))
            {
                _logger.LogDebug("Channel moderation {Action} of {Target} in {Channel} refused by the holder", action, targetGuid, channelName);
            }
        });
    }

    /// <summary>
    /// The realm-wide who list: the entries of <paramref name="filter"/> on every worker and the realm's online total, handed to <paramref name="done"/> on a pool thread. When the
    /// directory cannot be reached <paramref name="done"/> gets an empty list and 0 (the caller then answers with what it has).
    /// </summary>
    public void QueryWho(RealmWhoFilter filter, Action<IReadOnlyList<RealmPresenceEntry>, int> done)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(done);
        Enqueue(() =>
        {
            IReadOnlyList<RealmPresenceEntry> entries = [];
            int online = 0;
            try
            {
                entries = _directory.QueryWho(filter);
                online = _directory.Count;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                _logger.LogDebug("Realm who query failed ({Message}); answering from this worker only", ex.Message);
            }

            done(entries, online);
        });
    }

    /// <summary>Completes when every operation queued before the call has run (tests and orderly shutdown).</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() => done.TrySetResult()))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    private void Enqueue(Action operation)
    {
        if (!_queue.Writer.TryWrite(operation))
        {
            Interlocked.Increment(ref _queueDropped);
        }
    }

    private async Task RunSenderAsync()
    {
        ChannelReader<Action> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out Action? operation))
                {
                    try
                    {
                        operation();
                    }
                    catch (Exception ex) when (IsTransportFailure(ex))
                    {
                        _logger.LogDebug("Cross-worker chat operation dropped ({Message})", ex.Message);
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                    {
                        _logger.LogFault("cluster.chat-send", ex, "A cross-worker chat operation failed; it is dropped");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RouteFrame(
        SocialRouteChannel channel,
        ClusterChatSender sender,
        long generation,
        ClusterChatFrame frame,
        ulong? targetGuid = null,
        int? groupId = null,
        int? guildId = null,
        string? channelName = null)
    {
        if (generation <= 0)
        {
            return; // the character is not registered in realm presence (login not sent yet, or superseded): the router would refuse it
        }

        string payload = Convert.ToBase64String((frame with { OriginWorkerId = _workerId }).ToBytes());
        var message = new ClusterSocialMessage(
            Guid.NewGuid(),
            channel,
            sender.Guid,
            sender.Name,
            sender.AccountId,
            generation,
            _workerId,
            _gatewayId,
            Interlocked.Increment(ref _sequence),
            payload,
            TargetCharacterGuid: targetGuid,
            TargetGroupId: groupId,
            TargetGuildId: guildId,
            TargetChannelName: channelName,
            CreatedAtUtc: DateTimeOffset.UtcNow);
        SocialRouteResult result = _router.Route(message);
        if (result.Status is SocialRouteStatus.RejectedUnauthorizedSender or SocialRouteStatus.RejectedUnknownTarget)
        {
            _logger.LogDebug("Cross-worker chat {Channel} from {Guid} refused: {Status}", channel, sender.Guid, result.Status);
        }
    }

    private static bool IsTransportFailure(Exception ex)
        => ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException;

    // ---- inbound -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Drains the deliveries queued for this worker and hands the live ones to <see cref="Deliver"/>; returns how many it handed over. A frame that does not decode, one this worker
    /// originated, and one stamped with a session generation other than the registered one are dropped. Blocking when the router is remote: never call on the world thread.
    /// </summary>
    public int PumpOnce()
    {
        // Chat is delivered through the worker queue. The gateway copy of each delivery has no consumer in a cluster realm (every worker shares one gateway id); the router bounds that
        // queue itself (InMemoryClusterSocialRouter.MaxGatewayQueueDeliveries), so nothing is drained or retained here.
        Action<ClusterChatDelivery>? deliver = Deliver;
        if (deliver is null)
        {
            return 0;
        }

        // The holder answers one bounded batch per request (its reply must fit the peer link's payload limit); the remainder stays queued there, so take batches until it is empty.
        int handed = 0;
        Dictionary<string, ChannelModerationState?>? channelStates = null;
        for (int batchNumber = 0; batchNumber < MaxPumpBatches; batchNumber++)
        {
            IReadOnlyList<RoutedSocialDelivery> batch = _router.DrainWorkerDeliveriesBatch(_workerId);
            if (batch.Count == 0)
            {
                break;
            }

            handed += HandOver(batch, deliver, ref channelStates);
        }

        return handed;
    }

    private int HandOver(IReadOnlyList<RoutedSocialDelivery> deliveries, Action<ClusterChatDelivery> deliver, ref Dictionary<string, ChannelModerationState?>? channelStates)
    {
        int handed = 0;
        foreach (RoutedSocialDelivery delivery in deliveries.OrderBy(static d => d.DeliverySequence))
        {
            ClusterChatFrame frame;
            try
            {
                frame = ClusterChatFrame.FromBytes(Convert.FromBase64String(delivery.PayloadText));
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException)
            {
                _logger.LogDebug("Dropped a delivery that is not a chat frame ({Message})", ex.Message);
                continue;
            }

            if (string.Equals(frame.OriginWorkerId, _workerId, StringComparison.Ordinal))
            {
                continue; // served by the worker that spoke
            }

            if (!IsCurrent(delivery.RecipientGuid, delivery.RecipientSessionGeneration))
            {
                Interlocked.Increment(ref _staleDropped);
                continue;
            }

            if (frame.Kind == ClusterChatFrameKind.Channel && frame.Channel is { Length: > 0 } channelName
                && RefreshChannelState(channelName, ref channelStates) is { } state && state.IsBarred(frame.SenderGuid))
            {
                Interlocked.Increment(ref _barredDropped); // banned or muted at the holder since the line was routed
                continue;
            }

            deliver(new ClusterChatDelivery(delivery.RecipientGuid, delivery.RecipientSessionGeneration, delivery.DeliverySequence, frame));
            handed++;
        }

        return handed;
    }

    /// <summary>
    /// The holder's moderation state of <paramref name="channelName"/>, read once per pump pass and kept for <see cref="IsSpeakerBarred"/>. A holder that cannot be reached leaves the
    /// previous snapshot in place (the holder already refused the line of a barred speaker when it routed it).
    /// </summary>
    private ChannelModerationState? RefreshChannelState(string channelName, ref Dictionary<string, ChannelModerationState?>? channelStates)
    {
        channelStates ??= new Dictionary<string, ChannelModerationState?>(StringComparer.OrdinalIgnoreCase);
        if (channelStates.TryGetValue(channelName, out ChannelModerationState? known))
        {
            return known;
        }

        ChannelModerationState? state;
        try
        {
            state = _router.GetChannelModeration(channelName);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            _logger.LogDebug("Channel moderation state of {Channel} not read ({Message})", channelName, ex.Message);
            _moderation.TryGetValue(channelName, out state);
            channelStates[channelName] = state;
            return state;
        }

        channelStates[channelName] = state;
        if (state is null)
        {
            _moderation.TryRemove(channelName, out _);
        }
        else
        {
            if (_moderation.Count >= MaxCachedChannels)
            {
                _moderation.Clear();
            }

            _moderation[channelName] = state;
        }

        return state;
    }

    /// <summary>Starts the pump: <see cref="PumpOnce"/> every <paramref name="interval"/>. Idempotent.</summary>
    public void Start(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _pump = Task.Run(() => RunPumpAsync(interval, _stop.Token), CancellationToken.None);
    }

    private async Task RunPumpAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    PumpOnce();
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    _logger.LogDebug("Cross-worker chat pump deferred ({Message})", ex.Message);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.LogFault("cluster.chat-pump", ex, "A cross-worker chat pump pass failed; the loop continues");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_sender, _pump).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.chat-routing", ex);
        }

        _stop.Dispose();
    }
}
