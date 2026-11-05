using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Peer;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// Presence and social routing of the Social lease holder as seen from another worker: every call is one request over the <see cref="PeerLinkClient"/> to the holder's
/// <see cref="PresenceHolderService"/> (<see cref="PresenceWireCodec"/>). Implements the async <see cref="IPresenceAuthority"/> for the publisher and the synchronous
/// <see cref="IRealmPresenceDirectory"/> / <see cref="IClusterSocialRouter"/> for readers such as <c>.cluster</c> commands; the synchronous members block the calling thread for
/// up to the link's request timeout and must not be called on the world thread. A lost link, a timeout or a refusal surfaces as <see cref="IOException"/>,
/// <see cref="TimeoutException"/> or <see cref="InvalidOperationException"/>. A reply over the link's payload limit (a very large Snapshot or Broadcast) is a refusal.
/// Thread-safe: no state of its own.
/// </summary>
public sealed class PeerPresenceClient(PeerLinkClient link) : IPresenceAuthority, IRealmPresenceDirectory, IClusterSocialRouter, IGroupAuthorityTransport, IGuildHolderTransport, IBattlegroundHubTransport
{
    /// <summary>Most bounded batches one <see cref="DrainWorkerDeliveries"/> call fetches.</summary>
    public const int MaxDrainBatches = 64;

    private async Task<BinaryReader> SendAsync(PresenceOp op, Action<BinaryWriter>? write, CancellationToken cancellationToken = default)
    {
        byte[] reply = await link.EchoAsync(PresenceWireCodec.EncodeRequest(op, write), cancellationToken: cancellationToken).ConfigureAwait(false);
        return PresenceWireCodec.OpenReply(reply);
    }

    private T Send<T>(PresenceOp op, Action<BinaryWriter>? write, Func<BinaryReader, T> read)
    {
#pragma warning disable ARC0006 // the documented synchronous entry point: its callers are the cluster sender and pump tasks and the .cluster commands (never the world thread), Task.Run keeps the await off any captured context, and the link request timeout bounds the wait
        using BinaryReader r = Task.Run(() => SendAsync(op, write)).GetAwaiter().GetResult();
#pragma warning restore ARC0006
        return read(r);
    }

    /// <summary>True while the peer link to the holder is admitted and open (an in-memory read, never blocks).</summary>
    public bool IsAuthorityReachable => link.IsConnected;

    // ---- IGroupAuthorityTransport --------------------------------------------------------------------------------------------------------------

    public void ExecuteGroupCommand(string workerId, GroupCommand command)
        => Send(
            PresenceOp.GroupCommand,
            w =>
            {
                w.Write(workerId);
                GroupWireCodec.WriteCommand(w, command);
            },
            static _ => true);

    public IReadOnlyList<GroupEvent> DrainGroupEvents(string workerId)
        => string.IsNullOrWhiteSpace(workerId) ? [] : Send(PresenceOp.GroupDrain, w => w.Write(workerId), GroupWireCodec.ReadEvents);

    // ---- IGuildHolderTransport ------------------------------------------------------------------------------------------------------------------

    public void ForwardGuildRequest(string workerId, long requestId, GuildRequest request)
        => Send(
            PresenceOp.GuildForward,
            w =>
            {
                w.Write(workerId);
                w.Write(requestId);
                GuildWireCodec.WriteRequest(w, request);
            },
            static _ => true);

    public IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId)
        => string.IsNullOrWhiteSpace(workerId) ? [] : Send(PresenceOp.GuildDrain, w => w.Write(workerId), GuildWireCodec.ReadEvents);

    // ---- IBattlegroundHubTransport --------------------------------------------------------------------------------------------------------------

    public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundCommand> commands)
        => Send(
            PresenceOp.BattlegroundSubmit,
            w =>
            {
                w.Write(workerId);
                BattlegroundWireCodec.WriteCommands(w, [.. commands.Select(c => new BattlegroundQueuedCommand(workerId, c))]);
            },
            static r => r.ReadBoolean());

    public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId)
        => Send(
            PresenceOp.BattlegroundTake,
            w => w.Write(nodeId),
            static r =>
            {
                bool isHolder = r.ReadBoolean();
                return new BattlegroundTakeResult(isHolder, BattlegroundWireCodec.ReadCommands(r));
            });

    public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices)
        => Send(
            PresenceOp.BattlegroundPost,
            w =>
            {
                w.Write(nodeId);
                BattlegroundWireCodec.WriteNotices(w, notices);
            },
            static r => r.ReadBoolean());

    public IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundNotice> DrainBattlegroundNotices(string workerId)
        => string.IsNullOrWhiteSpace(workerId)
            ? []
            : Send(
                PresenceOp.BattlegroundDrain,
                w => w.Write(workerId),
                static r => (IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundNotice>)[.. BattlegroundWireCodec.ReadNotices(r).Select(n => n.Notice)]);

    // ---- IPresenceAuthority --------------------------------------------------------------------------------------------------------------------

    public async Task<PresenceUpsertReply> RegisterAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
    {
        using BinaryReader r = await SendAsync(PresenceOp.Register, w => PresenceWireCodec.WriteEntry(w, entry), cancellationToken).ConfigureAwait(false);
        return PresenceWireCodec.ReadUpsertReply(r);
    }

    public async Task<PresenceUpsertReply> UpsertAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
    {
        using BinaryReader r = await SendAsync(PresenceOp.Upsert, w => PresenceWireCodec.WriteEntry(w, entry), cancellationToken).ConfigureAwait(false);
        return PresenceWireCodec.ReadUpsertReply(r);
    }

    public async Task<PresenceMutationResult> RemoveAsync(ulong characterGuid, long presenceVersion, long sessionGeneration, string workerId, CancellationToken cancellationToken = default)
    {
        using BinaryReader r = await SendAsync(PresenceOp.Remove, w => WriteRemove(w, characterGuid, presenceVersion, sessionGeneration, workerId), cancellationToken).ConfigureAwait(false);
        return (PresenceMutationResult)r.ReadByte();
    }

    public async Task<IReadOnlyList<PresenceKick>> DrainKicksAsync(string workerId, CancellationToken cancellationToken = default)
    {
        using BinaryReader r = await SendAsync(PresenceOp.DrainKicks, w => w.Write(workerId), cancellationToken).ConfigureAwait(false);
        return PresenceWireCodec.ReadKicks(r);
    }

    private static void WriteRemove(BinaryWriter w, ulong guid, long version, long generation, string worker)
    {
        w.Write(guid);
        w.Write(version);
        w.Write(generation);
        w.Write(worker);
    }

    // ---- IRealmPresenceDirectory ---------------------------------------------------------------------------------------------------------------

    public int Count => Send(PresenceOp.Count, null, r => r.ReadInt32());

    public PresenceMutationResult TryUpsert(RealmPresenceEntry entry, out RealmPresenceEntry? current)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PresenceUpsertReply reply = Send(PresenceOp.Upsert, w => PresenceWireCodec.WriteEntry(w, entry), PresenceWireCodec.ReadUpsertReply);
        current = reply.Current;
        return reply.Result;
    }

    public PresenceMutationResult TryRemove(ulong characterGuid, long expectedPresenceVersion, long expectedSessionGeneration, string? expectedWorkerId = null)
        => Send(
            PresenceOp.Remove,
            w => WriteRemove(w, characterGuid, expectedPresenceVersion, expectedSessionGeneration, expectedWorkerId ?? string.Empty),
            r => (PresenceMutationResult)r.ReadByte());

    public RealmPresenceEntry? FindByGuid(ulong characterGuid)
        => Send(PresenceOp.FindByGuid, w => w.Write(characterGuid), PresenceWireCodec.ReadNullableEntry);

    public RealmPresenceEntry? FindByName(string characterName)
        => string.IsNullOrWhiteSpace(characterName) ? null : Send(PresenceOp.FindByName, w => w.Write(characterName), PresenceWireCodec.ReadNullableEntry);

    public IReadOnlyList<RealmPresenceEntry> FindByAccount(int accountId)
        => Send(PresenceOp.FindByAccount, w => w.Write(accountId), PresenceWireCodec.ReadEntries);

    public IReadOnlyList<RealmPresenceEntry> FindByGuild(int guildId)
        => Send(PresenceOp.FindByGuild, w => w.Write(guildId), PresenceWireCodec.ReadEntries);

    public IReadOnlyList<RealmPresenceEntry> FindByGroup(int groupId)
        => Send(PresenceOp.FindByGroup, w => w.Write(groupId), PresenceWireCodec.ReadEntries);

    public IReadOnlyList<RealmPresenceEntry> QueryWho(RealmWhoFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Send(PresenceOp.QueryWho, w => PresenceWireCodec.WriteWho(w, filter), PresenceWireCodec.ReadEntries);
    }

    public IReadOnlyList<RealmPresenceEntry> Snapshot() => Send(PresenceOp.Snapshot, null, PresenceWireCodec.ReadEntries);

    // ---- IClusterSocialRouter ------------------------------------------------------------------------------------------------------------------

    public SocialRouteResult Route(ClusterSocialMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty)
        {
            throw new ArgumentException("MessageId must be non-empty.", nameof(message));
        }

        // The reply carries the status and the number of deliveries only; the deliveries themselves stay queued at the holder for their workers.
        return Send(PresenceOp.Route, w => PresenceWireCodec.WriteMessage(w, message), r =>
        {
            var status = (SocialRouteStatus)r.ReadByte();
            r.ReadInt32();
            return new SocialRouteResult(status, []);
        });
    }

    public bool JoinChannel(string channelName, ulong characterGuid, long sessionGeneration)
        => !string.IsNullOrWhiteSpace(channelName)
            && Send(PresenceOp.JoinChannel, w => WriteChannel(w, channelName, characterGuid, sessionGeneration), r => r.ReadBoolean());

    public bool LeaveChannel(string channelName, ulong characterGuid, long sessionGeneration)
        => !string.IsNullOrWhiteSpace(channelName)
            && Send(PresenceOp.LeaveChannel, w => WriteChannel(w, channelName, characterGuid, sessionGeneration), r => r.ReadBoolean());

    private static void WriteChannel(BinaryWriter w, string channel, ulong guid, long generation)
    {
        w.Write(channel);
        w.Write(guid);
        w.Write(generation);
    }

    public IReadOnlyList<RoutedSocialDelivery> DrainGatewayDeliveries(string gatewayId)
        => string.IsNullOrWhiteSpace(gatewayId) ? [] : Send(PresenceOp.DrainGateway, w => w.Write(gatewayId), PresenceWireCodec.ReadDeliveries);

    /// <summary>Every delivery queued for the worker when the call starts, fetched as successive bounded batches (at most <see cref="MaxDrainBatches"/> per call; the rest stays queued).</summary>
    public IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveries(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        var all = new List<RoutedSocialDelivery>();
        for (int i = 0; i < MaxDrainBatches; i++)
        {
            IReadOnlyList<RoutedSocialDelivery> batch = DrainWorkerDeliveriesBatch(workerId);
            if (batch.Count == 0)
            {
                break;
            }

            all.AddRange(batch);
        }

        return all;
    }

    /// <summary>One bounded reply of the holder's worker queue (see <see cref="InMemoryClusterSocialRouter.MaxDrainBatchBytes"/>); the remainder stays queued there.</summary>
    public IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveriesBatch(string workerId)
        => string.IsNullOrWhiteSpace(workerId) ? [] : Send(PresenceOp.DrainWorker, w => w.Write(workerId), PresenceWireCodec.ReadDeliveries);

    public bool ModerateChannel(string channelName, ChannelModerationAction action, ulong actorGuid, ulong targetGuid)
        => !string.IsNullOrWhiteSpace(channelName)
            && Send(
                PresenceOp.ChannelModerate,
                w =>
                {
                    w.Write(channelName);
                    w.Write((byte)action);
                    w.Write(actorGuid);
                    w.Write(targetGuid);
                },
                r => r.ReadBoolean());

    public ChannelModerationState? GetChannelModeration(string channelName)
        => string.IsNullOrWhiteSpace(channelName) ? null : Send(PresenceOp.ChannelState, w => w.Write(channelName), PresenceWireCodec.ReadChannelState);

    public PresenceMutationResult LogoutWithOrderedFlush(
        ulong characterGuid,
        long expectedPresenceVersion,
        long expectedSessionGeneration,
        string expectedWorkerId,
        out IReadOnlyList<RoutedSocialDelivery> flushedGatewayDeliveries)
    {
        (PresenceMutationResult result, IReadOnlyList<RoutedSocialDelivery> flushed) = Send(
            PresenceOp.Logout,
            w => WriteRemove(w, characterGuid, expectedPresenceVersion, expectedSessionGeneration, expectedWorkerId),
            r =>
            {
                var status = (PresenceMutationResult)r.ReadByte();
                return (status, PresenceWireCodec.ReadDeliveries(r));
            });
        flushedGatewayDeliveries = flushed;
        return result;
    }
}

/// <summary>
/// The one presence object a node registers for <see cref="IRealmPresenceDirectory"/>, <see cref="IClusterSocialRouter"/> and <see cref="IPresenceAuthority"/>: each call goes to the
/// local <see cref="PresenceHolderService"/> while <c>holdsSocial</c> says this node holds the Social lease (always true in Combined mode: the in-process fast path), and to the
/// <see cref="PeerPresenceClient"/> of the holder otherwise. With no peer client and no lease a call throws <see cref="InvalidOperationException"/> (a misuse: ask <see cref="IsAuthorityReachable"/> first, as the publisher and the routers do).
/// Thread-safe if <c>holdsSocial</c> and <c>remote</c> are.
/// </summary>
public sealed class LeaseRoutedPresence(PresenceHolderService local, Func<bool> holdsSocial, Func<PeerPresenceClient?> remote)
    : IPresenceAuthority, IRealmPresenceDirectory, IClusterSocialRouter, IGroupAuthorityTransport, IGuildHolderTransport, IBattlegroundHubTransport
{
    private IPresenceAuthority Authority => holdsSocial() ? local : Remote;

    private IRealmPresenceDirectory Directory => holdsSocial() ? local.Directory : Remote;

    private IClusterSocialRouter Router => holdsSocial() ? local.Router : Remote;

    /// <summary>
    /// True when a call can be made now: this node holds the Social lease (the in-process fast path), or it has a peer client whose link to the holder is open. False is the normal
    /// state before the link connects and while the holder is away: callers check it and wait, so nothing is thrown for it. Never blocks; safe on any thread.
    /// </summary>
    public bool IsAuthorityReachable => holdsSocial() || remote() is { IsAuthorityReachable: true };

    // Reaching this without checking IsAuthorityReachable first is a misuse (or a link that vanished between the check and the call); the callers handle the throw as a transport failure.
    private PeerPresenceClient Remote => remote() ?? throw new InvalidOperationException("this node does not hold the Social lease and has no peer link to the node that does");

    public Task<PresenceUpsertReply> RegisterAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default) => Authority.RegisterAsync(entry, cancellationToken);

    public Task<PresenceUpsertReply> UpsertAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default) => Authority.UpsertAsync(entry, cancellationToken);

    public Task<PresenceMutationResult> RemoveAsync(ulong characterGuid, long presenceVersion, long sessionGeneration, string workerId, CancellationToken cancellationToken = default)
        => Authority.RemoveAsync(characterGuid, presenceVersion, sessionGeneration, workerId, cancellationToken);

    public Task<IReadOnlyList<PresenceKick>> DrainKicksAsync(string workerId, CancellationToken cancellationToken = default) => Authority.DrainKicksAsync(workerId, cancellationToken);

    public void ExecuteGroupCommand(string workerId, GroupCommand command)
    {
        if (holdsSocial())
        {
            local.ExecuteGroupCommand(workerId, command);
        }
        else
        {
            Remote.ExecuteGroupCommand(workerId, command);
        }
    }

    public IReadOnlyList<GroupEvent> DrainGroupEvents(string workerId) => holdsSocial() ? local.DrainGroupEvents(workerId) : Remote.DrainGroupEvents(workerId);

    public void ForwardGuildRequest(string workerId, long requestId, GuildRequest request)
    {
        if (holdsSocial())
        {
            local.ForwardGuildRequest(workerId, requestId, request);
        }
        else
        {
            Remote.ForwardGuildRequest(workerId, requestId, request);
        }
    }

    public IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId) => holdsSocial() ? local.DrainGuildEvents(workerId) : Remote.DrainGuildEvents(workerId);

    public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundCommand> commands)
        => holdsSocial() ? local.SubmitBattlegroundCommands(workerId, commands) : Remote.SubmitBattlegroundCommands(workerId, commands);

    public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId)
        => holdsSocial() ? local.TakeBattlegroundCommands(nodeId) : Remote.TakeBattlegroundCommands(nodeId);

    public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices)
        => holdsSocial() ? local.PostBattlegroundNotices(nodeId, notices) : Remote.PostBattlegroundNotices(nodeId, notices);

    public IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundNotice> DrainBattlegroundNotices(string workerId)
        => holdsSocial() ? local.DrainBattlegroundNotices(workerId) : Remote.DrainBattlegroundNotices(workerId);

    public int Count => Directory.Count;

    /// <summary>
    /// The directory size when this node holds the Social lease (an in-memory read, never blocks); false on a node that does not, where <see cref="Count"/> would be a blocking
    /// round trip to the holder. Safe on the world thread.
    /// </summary>
    public bool TryGetLocalCount(out int count)
    {
        if (holdsSocial())
        {
            count = local.Directory.Count;
            return true;
        }

        count = 0;
        return false;
    }

    public PresenceMutationResult TryUpsert(RealmPresenceEntry entry, out RealmPresenceEntry? current) => Directory.TryUpsert(entry, out current);

    public PresenceMutationResult TryRemove(ulong characterGuid, long expectedPresenceVersion, long expectedSessionGeneration, string? expectedWorkerId = null)
        => Directory.TryRemove(characterGuid, expectedPresenceVersion, expectedSessionGeneration, expectedWorkerId);

    public RealmPresenceEntry? FindByGuid(ulong characterGuid) => Directory.FindByGuid(characterGuid);

    public RealmPresenceEntry? FindByName(string characterName) => Directory.FindByName(characterName);

    public IReadOnlyList<RealmPresenceEntry> FindByAccount(int accountId) => Directory.FindByAccount(accountId);

    public IReadOnlyList<RealmPresenceEntry> FindByGuild(int guildId) => Directory.FindByGuild(guildId);

    public IReadOnlyList<RealmPresenceEntry> FindByGroup(int groupId) => Directory.FindByGroup(groupId);

    public IReadOnlyList<RealmPresenceEntry> QueryWho(RealmWhoFilter filter) => Directory.QueryWho(filter);

    public IReadOnlyList<RealmPresenceEntry> Snapshot() => Directory.Snapshot();

    public SocialRouteResult Route(ClusterSocialMessage message) => Router.Route(message);

    public bool JoinChannel(string channelName, ulong characterGuid, long sessionGeneration) => Router.JoinChannel(channelName, characterGuid, sessionGeneration);

    public bool LeaveChannel(string channelName, ulong characterGuid, long sessionGeneration) => Router.LeaveChannel(channelName, characterGuid, sessionGeneration);

    public IReadOnlyList<RoutedSocialDelivery> DrainGatewayDeliveries(string gatewayId) => Router.DrainGatewayDeliveries(gatewayId);

    public IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveries(string workerId) => Router.DrainWorkerDeliveries(workerId);

    public IReadOnlyList<RoutedSocialDelivery> DrainWorkerDeliveriesBatch(string workerId) => Router.DrainWorkerDeliveriesBatch(workerId);

    public bool ModerateChannel(string channelName, ChannelModerationAction action, ulong actorGuid, ulong targetGuid) => Router.ModerateChannel(channelName, action, actorGuid, targetGuid);

    public ChannelModerationState? GetChannelModeration(string channelName) => Router.GetChannelModeration(channelName);

    public PresenceMutationResult LogoutWithOrderedFlush(
        ulong characterGuid,
        long expectedPresenceVersion,
        long expectedSessionGeneration,
        string expectedWorkerId,
        out IReadOnlyList<RoutedSocialDelivery> flushedGatewayDeliveries)
        => Router.LogoutWithOrderedFlush(characterGuid, expectedPresenceVersion, expectedSessionGeneration, expectedWorkerId, out flushedGatewayDeliveries);
}
