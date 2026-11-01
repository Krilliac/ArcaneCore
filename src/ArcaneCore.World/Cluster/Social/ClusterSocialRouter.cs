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

/// <summary>What a channel moderation request changes at the holder (<see cref="IClusterSocialRouter.ModerateChannel"/>). Values are wire values: append only.</summary>
public enum ChannelModerationAction : byte
{
    /// <summary>The actor becomes owner and moderator of a channel that has no owner (the first joiner); refused when it is owned.</summary>
    ClaimOwner = 1,

    /// <summary>Hands ownership to the target (who also becomes a moderator); the actor must be owner, moderator or staff.</summary>
    SetOwner = 2,

    AddModerator = 3,

    RemoveModerator = 4,

    /// <summary>Bans the target: it is unsubscribed, cannot join again and its lines are refused.</summary>
    Ban = 5,

    Unban = 6,

    /// <summary>Mutes the target: its lines are refused while it stays subscribed.</summary>
    Mute = 7,

    Unmute = 8,
}

/// <summary>The moderation state the holder keeps for one custom channel: its owner (0 when none) and the sorted guids of its moderators, banned and muted characters.</summary>
public sealed record ChannelModerationState(ulong Owner, IReadOnlyList<ulong> Moderators, IReadOnlyList<ulong> Banned, IReadOnlyList<ulong> Muted)
{
    /// <summary>True when <paramref name="characterGuid"/> may not speak in the channel (banned or muted).</summary>
    public bool IsBarred(ulong characterGuid) => Banned.Contains(characterGuid) || Muted.Contains(characterGuid);
}

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
    /// Drain the oldest pending deliveries queued for <paramref name="workerId"/>, at most one peer-link reply's worth (<see cref="InMemoryClusterSocialRouter.MaxDrainBatchDeliveries"/>
    /// deliveries and <see cref="InMemoryClusterSocialRouter.MaxDrainBatchBytes"/> encoded bytes); the remainder stays queued. Call again until it returns an empty list.
    /// </summary>
    IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveriesBatch(string workerId);

    /// <summary>
    /// A moderation change of a custom channel, held at the router so every worker sees one owner, moderator, ban and mute state. <paramref name="actorGuid"/> must be the channel's
    /// owner or a moderator, or an online character of staff security (except for <see cref="ChannelModerationAction.ClaimOwner"/>, which needs an unowned channel and the actor to be
    /// the target and a member). Refused (false) for an unknown channel, a missing right or a full list (<see cref="InMemoryClusterSocialRouter.MaxModerationEntries"/>).
    /// A ban also unsubscribes the target.
    /// </summary>
    bool ModerateChannel(string channelName, ChannelModerationAction action, ulong actorGuid, ulong targetGuid);

    /// <summary>The moderation state of a channel that has subscribers, or null.</summary>
    ChannelModerationState? GetChannelModeration(string channelName);

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
    /// <summary>Most deliveries <see cref="DrainWorkerDeliveriesBatch"/> returns at once.</summary>
    public const int MaxDrainBatchDeliveries = 1024;

    /// <summary>Most encoded bytes (<see cref="PresenceWireCodec.DeliveryWireSize"/>) <see cref="DrainWorkerDeliveriesBatch"/> returns at once: well under the peer link's 64 KB payload limit.</summary>
    public const int MaxDrainBatchBytes = 48 * 1024;

    /// <summary>Most deliveries kept per gateway queue; the oldest are dropped beyond it (a gateway that never drains, such as the shared one of a cluster realm, must not grow without bound).</summary>
    public const int MaxGatewayQueueDeliveries = 256;

    /// <summary>Most encoded bytes kept per gateway queue (the oldest are dropped beyond it), so a drain or an ordered logout flush of a gateway queue always fits one peer-link reply.</summary>
    public const int MaxGatewayQueueBytes = 32 * 1024;

    /// <summary>Most moderators, banned and muted characters kept per channel and list.</summary>
    public const int MaxModerationEntries = 1024;

    /// <summary>The account security (<c>AccountSecurity.GameMaster</c>) from which a character may moderate any channel.</summary>
    private const byte StaffSecurity = 2;

    private sealed class ChannelModeration
    {
        public ulong Owner;
        public readonly HashSet<ulong> Moderators = [];
        public readonly HashSet<ulong> Banned = [];
        public readonly HashSet<ulong> Muted = [];
    }

    private readonly IRealmPresenceDirectory _directory;
    private readonly int _dedupCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, IReadOnlyList<RoutedSocialDelivery>> _seenByMessageId = new();
    private readonly Dictionary<(ulong SenderGuid, long SessionGeneration, long Sequence), IReadOnlyList<RoutedSocialDelivery>> _seenBySenderSeq = new();
    private readonly Queue<(Guid MessageId, (ulong SenderGuid, long SessionGeneration, long Sequence) SenderKey)> _dedupOrder = new();
    private readonly Dictionary<string, Dictionary<ulong, long>> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RoutedSocialDelivery>> _gatewayQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _gatewayQueueBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChannelModeration> _moderation = new(StringComparer.OrdinalIgnoreCase);
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
                EnqueueGateway(recipient.GatewayId, delivery);
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

            if (_moderation.TryGetValue(channelName, out ChannelModeration? moderation) && moderation.Banned.Contains(characterGuid))
            {
                return false; // banned at the holder: refused whichever worker the character joins from
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

            DropMember(channelName, members, characterGuid);
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
            return DrainGatewayQueue(gatewayId);
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

    public IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveriesBatch(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        lock (_gate)
        {
            if (!_workerQueues.TryGetValue(workerId, out List<RoutedSocialDelivery>? queue) || queue.Count == 0)
            {
                return [];
            }

            int take = 0;
            long bytes = 0;
            while (take < queue.Count && take < MaxDrainBatchDeliveries)
            {
                int size = PresenceWireCodec.DeliveryWireSize(queue[take]);
                if (size > MaxDrainBatchBytes)
                {
                    // One delivery that can never fit a reply would block the queue for good: it is dropped (its text exceeds anything chat produces).
                    queue.RemoveAt(take);
                    continue;
                }

                if (bytes + size > MaxDrainBatchBytes)
                {
                    break;
                }

                bytes += size;
                take++;
            }

            if (take == 0)
            {
                return [];
            }

            RoutedSocialDelivery[] batch = [.. queue.GetRange(0, take)];
            queue.RemoveRange(0, take);
            return batch;
        }
    }

    public bool ModerateChannel(string channelName, ChannelModerationAction action, ulong actorGuid, ulong targetGuid)
    {
        if (string.IsNullOrWhiteSpace(channelName) || actorGuid == 0 || targetGuid == 0 || !Enum.IsDefined(action))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_channels.TryGetValue(channelName, out Dictionary<ulong, long>? members))
            {
                return false;
            }

            if (!_moderation.TryGetValue(channelName, out ChannelModeration? state))
            {
                state = new ChannelModeration();
                _moderation[channelName] = state;
            }

            if (action == ChannelModerationAction.ClaimOwner)
            {
                if (state.Owner != 0 || actorGuid != targetGuid || !members.ContainsKey(actorGuid))
                {
                    return false;
                }

                state.Owner = actorGuid;
                state.Moderators.Add(actorGuid);
                return true;
            }

            bool actorMayModerate = actorGuid == state.Owner
                || state.Moderators.Contains(actorGuid)
                || (_directory.FindByGuid(actorGuid) is { } actor && actor.Security >= StaffSecurity);
            if (!actorMayModerate)
            {
                return false;
            }

            switch (action)
            {
                case ChannelModerationAction.SetOwner:
                    if (!members.ContainsKey(targetGuid) || (!state.Moderators.Contains(targetGuid) && (state.Moderators.Count >= MaxModerationEntries || !state.Moderators.Add(targetGuid))))
                    {
                        return false;
                    }

                    state.Owner = targetGuid;
                    return true;

                case ChannelModerationAction.AddModerator:
                    return state.Moderators.Contains(targetGuid) || (state.Moderators.Count < MaxModerationEntries && state.Moderators.Add(targetGuid));

                case ChannelModerationAction.RemoveModerator:
                    return state.Moderators.Remove(targetGuid);

                case ChannelModerationAction.Ban:
                    if (!state.Banned.Contains(targetGuid) && (state.Banned.Count >= MaxModerationEntries || !state.Banned.Add(targetGuid)))
                    {
                        return false;
                    }

                    if (members.ContainsKey(targetGuid))
                    {
                        DropMember(channelName, members, targetGuid);
                    }

                    return true;

                case ChannelModerationAction.Unban:
                    return state.Banned.Remove(targetGuid);

                case ChannelModerationAction.Mute:
                    return state.Muted.Contains(targetGuid) || (state.Muted.Count < MaxModerationEntries && state.Muted.Add(targetGuid));

                case ChannelModerationAction.Unmute:
                    return state.Muted.Remove(targetGuid);

                default:
                    return false;
            }
        }
    }

    public ChannelModerationState? GetChannelModeration(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_channels.ContainsKey(channelName))
            {
                return null;
            }

            if (!_moderation.TryGetValue(channelName, out ChannelModeration? state))
            {
                return new ChannelModerationState(0, [], [], []);
            }

            return new ChannelModerationState(state.Owner, Sorted(state.Moderators), Sorted(state.Banned), Sorted(state.Muted));
        }
    }

    private static ulong[] Sorted(HashSet<ulong> set)
    {
        ulong[] values = [.. set];
        Array.Sort(values);
        return values;
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
                ? DrainGatewayQueue(gatewayId)
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

            // Moderation is held here, so a muted or banned speaker is refused whichever worker it speaks from.
            if (_moderation.TryGetValue(message.TargetChannelName, out ChannelModeration? moderation)
                && (moderation.Banned.Contains(senderPresence.CharacterGuid) || moderation.Muted.Contains(senderPresence.CharacterGuid)))
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
                ForgetMemberModeration(channelName, characterGuid);
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
                _moderation.Remove(channelName);
            }
        }
    }

    /// <summary>Under the gate: removes a subscription (leave, ban) and ends the channel when it was the last one, as an empty channel is deleted at the workers.</summary>
    private void DropMember(string channelName, Dictionary<ulong, long> members, ulong characterGuid)
    {
        members.Remove(characterGuid);
        ForgetMemberModeration(channelName, characterGuid);
        if (members.Count == 0)
        {
            _channels.Remove(channelName);
            _moderation.Remove(channelName);
        }
    }

    /// <summary>Under the gate: member flags (owner, moderator, mute) end with the membership; a ban does not.</summary>
    private void ForgetMemberModeration(string channelName, ulong characterGuid)
    {
        if (!_moderation.TryGetValue(channelName, out ChannelModeration? state))
        {
            return;
        }

        state.Moderators.Remove(characterGuid);
        state.Muted.Remove(characterGuid);
        if (state.Owner == characterGuid)
        {
            state.Owner = 0;
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

    /// <summary>Under the gate: queues a delivery for a gateway, dropping the oldest while the queue is over <see cref="MaxGatewayQueueDeliveries"/> or <see cref="MaxGatewayQueueBytes"/>.</summary>
    private void EnqueueGateway(string gatewayId, RoutedSocialDelivery delivery)
    {
        Enqueue(_gatewayQueues, gatewayId, delivery);
        List<RoutedSocialDelivery> queue = _gatewayQueues[gatewayId];
        long bytes = _gatewayQueueBytes.GetValueOrDefault(gatewayId) + PresenceWireCodec.DeliveryWireSize(delivery);
        int drop = 0;
        while (drop < queue.Count && (queue.Count - drop > MaxGatewayQueueDeliveries || bytes > MaxGatewayQueueBytes))
        {
            bytes -= PresenceWireCodec.DeliveryWireSize(queue[drop]);
            drop++;
        }

        if (drop > 0)
        {
            queue.RemoveRange(0, drop);
        }

        _gatewayQueueBytes[gatewayId] = bytes;
    }

    /// <summary>Under the gate.</summary>
    private IReadOnlyList<RoutedSocialDelivery> DrainGatewayQueue(string gatewayId)
    {
        _gatewayQueueBytes.Remove(gatewayId);
        return DrainQueue(_gatewayQueues, gatewayId);
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
