using ArcaneCore.Kernel.Cluster.Social;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>Social message categories routed across workers and gateways by <see cref="IClusterSocialRouter"/> (M15.2).</summary>
public enum SocialRouteChannel
{
    Whisper = 0,
    GroupChat = 1,
    GroupControl = 2,
    GuildChat = 3,
    GuildEvent = 4,
    CustomChannel = 5,
    Broadcast = 6,
}

/// <summary>Outcome of a realm-wide social routing request.</summary>
public enum SocialRouteStatus
{
    Delivered = 0,
    RejectedUnauthorizedSender = 1,
    RejectedUnknownTarget = 2,
    DuplicateSuppressed = 3,
    NoRecipients = 4,
}

/// <summary>
/// Cross-worker social message envelope submitted to <see cref="IClusterSocialRouter"/>.
/// Carries the sender's identity, <see cref="SenderSessionGeneration"/>, originating worker/gateway,
/// and per-sender sequence number so the authority can verify sender authorization and deduplicate retries.
/// </summary>
public sealed record ClusterSocialMessage(
    Guid MessageId,
    SocialRouteChannel Channel,
    ulong SenderGuid,
    string SenderName,
    int SenderAccountId,
    long SenderSessionGeneration,
    string OriginWorkerId,
    string OriginGatewayId,
    long SenderSequence,
    string PayloadText,
    ulong? TargetCharacterGuid = null,
    string? TargetCharacterName = null,
    int? TargetGroupId = null,
    int? TargetGuildId = null,
    string? TargetChannelName = null,
    DateTimeOffset CreatedAtUtc = default);

/// <summary>
/// One concrete recipient delivery produced by <see cref="IClusterSocialRouter"/>.
/// Stamped with the recipient's current <see cref="RecipientSessionGeneration"/>, <see cref="TargetWorkerId"/>,
/// <see cref="TargetGatewayId"/>, and a strictly monotonic realm-wide <see cref="DeliverySequence"/> so
/// gateways/workers drop deliveries aimed at a superseded session and preserve causal ordering.
/// </summary>
public sealed record RoutedSocialDelivery(
    Guid MessageId,
    SocialRouteChannel Channel,
    ulong RecipientGuid,
    string RecipientName,
    int RecipientAccountId,
    long RecipientSessionGeneration,
    string TargetWorkerId,
    string TargetGatewayId,
    ulong SenderGuid,
    string SenderName,
    long DeliverySequence,
    string PayloadText);

/// <summary>Result of routing a <see cref="ClusterSocialMessage"/> through <see cref="IClusterSocialRouter"/>.</summary>
public sealed record SocialRouteResult(SocialRouteStatus Status, IReadOnlyList<RoutedSocialDelivery> Deliveries);

/// <summary>
/// Realm-wide cross-worker social router (M15.2, <c>docs/CLUSTERING_DESIGN.md</c> §180, §212, §218-219, §565).
/// Routes whispers, group chat/control, guild chat/events, custom channel messages, and broadcasts across workers
/// and gateways using <see cref="IRealmPresenceDirectory"/>, enforcing sender authorization, retry deduplication,
/// and logout ordering.
/// </summary>
public interface IClusterSocialRouter
{
    /// <summary>Route a social message across workers and gateways.</summary>
    SocialRouteResult Route(ClusterSocialMessage message);

    /// <summary>Subscribe an online character (at its active <paramref name="sessionGeneration"/>) to a custom chat channel.</summary>
    bool JoinChannel(string channelName, ulong characterGuid, long sessionGeneration);

    /// <summary>Remove a character's custom chat channel subscription.</summary>
    bool LeaveChannel(string channelName, ulong characterGuid, long sessionGeneration);

    /// <summary>Drain all pending deliveries queued for <paramref name="gatewayId"/> in monotonic <see cref="RoutedSocialDelivery.DeliverySequence"/> order.</summary>
    IReadOnlyList<RoutedSocialDelivery> DrainGatewayDeliveries(string gatewayId);

    /// <summary>Drain all pending deliveries queued for <paramref name="workerId"/> in monotonic <see cref="RoutedSocialDelivery.DeliverySequence"/> order.</summary>
    IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveries(string workerId);

    /// <summary>
    /// Atomically flush all queued deliveries originating from or addressed to <paramref name="characterGuid"/>'s
    /// gateway before conditionally removing the character's presence entry and custom channel memberships.
    /// Ensures messages sent before logout are delivered in causal order before the player disappears from presence.
    /// </summary>
    PresenceMutationResult LogoutWithOrderedFlush(
        ulong characterGuid,
        long expectedPresenceVersion,
        long expectedSessionGeneration,
        string expectedWorkerId,
        out IReadOnlyList<RoutedSocialDelivery> flushedGatewayDeliveries);
}

/// <summary>
/// Thread-safe in-memory <see cref="IClusterSocialRouter"/> backed by <see cref="IRealmPresenceDirectory"/>.
/// </summary>
public sealed class InMemoryClusterSocialRouter : IClusterSocialRouter
{
    private readonly IRealmPresenceDirectory _directory;
    private readonly int _dedupCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, IReadOnlyList<RoutedSocialDelivery>> _seenByMessageId = new();
    private readonly Dictionary<(ulong SenderGuid, long SessionGeneration, long Sequence), IReadOnlyList<RoutedSocialDelivery>> _seenBySenderSeq = new();
    private readonly Queue<(Guid MessageId, (ulong SenderGuid, long SessionGeneration, long Sequence) SenderKey)> _dedupOrder = new();
    private readonly Dictionary<string, Dictionary<ulong, long>> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RoutedSocialDelivery>> _gatewayQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RoutedSocialDelivery>> _workerQueues = new(StringComparer.Ordinal);
    private long _nextDeliverySequence;

    public InMemoryClusterSocialRouter(IRealmPresenceDirectory directory, int dedupCapacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (dedupCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dedupCapacity), "Deduplication capacity must be positive.");
        }

        _directory = directory;
        _dedupCapacity = dedupCapacity;
    }

    public SocialRouteResult Route(ClusterSocialMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty)
        {
            throw new ArgumentException("MessageId must be non-empty.", nameof(message));
        }

        lock (_gate)
        {
            var senderKey = (message.SenderGuid, message.SenderSessionGeneration, message.SenderSequence);
            if (_seenByMessageId.TryGetValue(message.MessageId, out IReadOnlyList<RoutedSocialDelivery>? priorById))
            {
                return new SocialRouteResult(SocialRouteStatus.DuplicateSuppressed, priorById);
            }

            if (message.SenderSequence > 0
                && _seenBySenderSeq.TryGetValue(senderKey, out IReadOnlyList<RoutedSocialDelivery>? priorBySeq))
            {
                return new SocialRouteResult(SocialRouteStatus.DuplicateSuppressed, priorBySeq);
            }

            if (!IsAuthorizedSender(message, out RealmPresenceEntry? senderPresence))
            {
                return new SocialRouteResult(SocialRouteStatus.RejectedUnauthorizedSender, []);
            }

            if (!TryResolveRecipients(message, senderPresence, out List<RealmPresenceEntry>? recipients))
            {
                return new SocialRouteResult(SocialRouteStatus.RejectedUnknownTarget, []);
            }

            var deliveries = new List<RoutedSocialDelivery>(recipients.Count);
            string senderName = senderPresence?.CharacterName ?? message.SenderName;
            foreach (RealmPresenceEntry recipient in recipients)
            {
                long seq = ++_nextDeliverySequence;
                var delivery = new RoutedSocialDelivery(
                    message.MessageId,
                    message.Channel,
                    recipient.CharacterGuid,
                    recipient.CharacterName,
                    recipient.AccountId,
                    recipient.SessionGeneration,
                    recipient.WorkerId,
                    recipient.GatewayId,
                    message.SenderGuid,
                    senderName,
                    seq,
                    message.PayloadText);
                deliveries.Add(delivery);
                Enqueue(_gatewayQueues, recipient.GatewayId, delivery);
                Enqueue(_workerQueues, recipient.WorkerId, delivery);
            }

            RecordDedup(message.MessageId, senderKey, deliveries);
            SocialRouteStatus status = deliveries.Count > 0 ? SocialRouteStatus.Delivered : SocialRouteStatus.NoRecipients;
            return new SocialRouteResult(status, deliveries);
        }
    }

    public bool JoinChannel(string channelName, ulong characterGuid, long sessionGeneration)
    {
        if (string.IsNullOrWhiteSpace(channelName) || characterGuid == 0 || sessionGeneration <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            RealmPresenceEntry? active = _directory.FindByGuid(characterGuid);
            if (active is null || active.SessionGeneration != sessionGeneration)
            {
                return false;
            }

            if (!_channels.TryGetValue(channelName, out Dictionary<ulong, long>? members))
            {
                members = new Dictionary<ulong, long>();
                _channels[channelName] = members;
            }

            members[characterGuid] = sessionGeneration;
            return true;
        }
    }

    public bool LeaveChannel(string channelName, ulong characterGuid, long sessionGeneration)
    {
        if (string.IsNullOrWhiteSpace(channelName) || characterGuid == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_channels.TryGetValue(channelName, out Dictionary<ulong, long>? members)
                || !members.TryGetValue(characterGuid, out long subscribedGeneration)
                || subscribedGeneration != sessionGeneration)
            {
                return false;
            }

            members.Remove(characterGuid);
            if (members.Count == 0)
            {
                _channels.Remove(channelName);
            }

            return true;
        }
    }

    public IReadOnlyList<RoutedSocialDelivery> DrainGatewayDeliveries(string gatewayId)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
        {
            return [];
        }

        lock (_gate)
        {
            return DrainQueue(_gatewayQueues, gatewayId);
        }
    }

    public IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveries(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        lock (_gate)
        {
            return DrainQueue(_workerQueues, workerId);
        }
    }

    public PresenceMutationResult LogoutWithOrderedFlush(
        ulong characterGuid,
        long expectedPresenceVersion,
        long expectedSessionGeneration,
        string expectedWorkerId,
        out IReadOnlyList<RoutedSocialDelivery> flushedGatewayDeliveries)
    {
        lock (_gate)
        {
            RealmPresenceEntry? current = _directory.FindByGuid(characterGuid);
            string? gatewayId = current?.GatewayId;

            PresenceMutationResult removal = _directory.TryRemove(
                characterGuid,
                expectedPresenceVersion,
                expectedSessionGeneration,
                expectedWorkerId);

            if (removal != PresenceMutationResult.Applied)
            {
                flushedGatewayDeliveries = [];
                return removal;
            }

            RemoveChannelSubscriptionsForSession(characterGuid, expectedSessionGeneration);
            flushedGatewayDeliveries = gatewayId is not null
                ? DrainQueue(_gatewayQueues, gatewayId)
                : [];
            return PresenceMutationResult.Applied;
        }
    }

    private bool IsAuthorizedSender(ClusterSocialMessage message, out RealmPresenceEntry? senderPresence)
    {
        if (message.Channel == SocialRouteChannel.Broadcast && message.SenderGuid == 0)
        {
            senderPresence = null;
            return !string.IsNullOrWhiteSpace(message.OriginWorkerId);
        }

        senderPresence = _directory.FindByGuid(message.SenderGuid);
        if (senderPresence is null)
        {
            return false;
        }

        if (senderPresence.AccountId != message.SenderAccountId
            || senderPresence.SessionGeneration != message.SenderSessionGeneration
            || !string.Equals(senderPresence.WorkerId, message.OriginWorkerId, StringComparison.Ordinal)
            || !string.Equals(senderPresence.GatewayId, message.OriginGatewayId, StringComparison.Ordinal))
        {
            return false;
        }

        if (message.Channel is SocialRouteChannel.GroupChat or SocialRouteChannel.GroupControl)
        {
            if (message.TargetGroupId is not { } groupId || groupId <= 0 || senderPresence.GroupId != groupId)
            {
                return false;
            }
        }
        else if (message.Channel is SocialRouteChannel.GuildChat or SocialRouteChannel.GuildEvent)
        {
            if (message.TargetGuildId is not { } guildId || guildId <= 0 || senderPresence.GuildId != guildId)
            {
                return false;
            }
        }
        else if (message.Channel == SocialRouteChannel.CustomChannel)
        {
            if (string.IsNullOrWhiteSpace(message.TargetChannelName)
                || !_channels.TryGetValue(message.TargetChannelName, out Dictionary<ulong, long>? members)
                || !members.TryGetValue(senderPresence.CharacterGuid, out long subscribedGeneration)
                || subscribedGeneration != senderPresence.SessionGeneration)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryResolveRecipients(
        ClusterSocialMessage message,
        RealmPresenceEntry? senderPresence,
        out List<RealmPresenceEntry> recipients)
    {
        recipients = [];
        switch (message.Channel)
        {
            case SocialRouteChannel.Whisper:
            {
                RealmPresenceEntry? target = message.TargetCharacterGuid is { } guid and > 0
                    ? _directory.FindByGuid(guid)
                    : !string.IsNullOrWhiteSpace(message.TargetCharacterName)
                        ? _directory.FindByName(message.TargetCharacterName)
                        : null;
                if (target is null)
                {
                    return false;
                }

                recipients.Add(target);
                return true;
            }

            case SocialRouteChannel.GroupChat:
            case SocialRouteChannel.GroupControl:
            {
                if (message.TargetGroupId is not { } groupId || groupId <= 0)
                {
                    return false;
                }

                recipients.AddRange(_directory.FindByGroup(groupId));
                return true;
            }

            case SocialRouteChannel.GuildChat:
            case SocialRouteChannel.GuildEvent:
            {
                if (message.TargetGuildId is not { } guildId || guildId <= 0)
                {
                    return false;
                }

                recipients.AddRange(_directory.FindByGuild(guildId));
                return true;
            }

            case SocialRouteChannel.CustomChannel:
            {
                if (string.IsNullOrWhiteSpace(message.TargetChannelName)
                    || !_channels.TryGetValue(message.TargetChannelName, out Dictionary<ulong, long>? members))
                {
                    return false;
                }

                foreach ((ulong guid, long generation) in members.OrderBy(static kv => kv.Key))
                {
                    if (_directory.FindByGuid(guid) is { } active && active.SessionGeneration == generation)
                    {
                        recipients.Add(active);
                    }
                }

                return true;
            }

            case SocialRouteChannel.Broadcast:
            {
                recipients.AddRange(_directory.Snapshot());
                return true;
            }

            default:
                return false;
        }
    }

    private void RecordDedup(
        Guid messageId,
        (ulong SenderGuid, long SessionGeneration, long Sequence) senderKey,
        IReadOnlyList<RoutedSocialDelivery> deliveries)
    {
        while (_dedupOrder.Count >= _dedupCapacity)
        {
            (Guid oldId, (ulong SenderGuid, long SessionGeneration, long Sequence) oldSenderKey) = _dedupOrder.Dequeue();
            _seenByMessageId.Remove(oldId);
            if (oldSenderKey.Sequence > 0)
            {
                _seenBySenderSeq.Remove(oldSenderKey);
            }
        }

        _seenByMessageId[messageId] = deliveries;
        if (senderKey.Sequence > 0)
        {
            _seenBySenderSeq[senderKey] = deliveries;
        }

        _dedupOrder.Enqueue((messageId, senderKey));
    }

    private void RemoveChannelSubscriptionsForSession(ulong characterGuid, long sessionGeneration)
    {
        List<string>? emptyChannels = null;
        foreach ((string channelName, Dictionary<ulong, long> members) in _channels)
        {
            if (members.TryGetValue(characterGuid, out long subscribedGeneration)
                && subscribedGeneration <= sessionGeneration)
            {
                members.Remove(characterGuid);
                if (members.Count == 0)
                {
                    (emptyChannels ??= []).Add(channelName);
                }
            }
        }

        if (emptyChannels is not null)
        {
            foreach (string channelName in emptyChannels)
            {
                _channels.Remove(channelName);
            }
        }
    }

    private static void Enqueue(Dictionary<string, List<RoutedSocialDelivery>> queues, string key, RoutedSocialDelivery delivery)
    {
        if (!queues.TryGetValue(key, out List<RoutedSocialDelivery>? queue))
        {
            queue = [];
            queues[key] = queue;
        }

        queue.Add(delivery);
    }

    private static IReadOnlyList<RoutedSocialDelivery> DrainQueue(Dictionary<string, List<RoutedSocialDelivery>> queues, string key)
    {
        if (!queues.TryGetValue(key, out List<RoutedSocialDelivery>? queue) || queue.Count == 0)
        {
            return [];
        }

        RoutedSocialDelivery[] snapshot = [.. queue];
        queue.Clear();
        return snapshot;
    }
}
