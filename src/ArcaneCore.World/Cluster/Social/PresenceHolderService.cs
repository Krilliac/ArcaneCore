using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Cluster.Social;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>A request to the worker that owns <see cref="WorkerId"/>: kick the session of <see cref="AccountId"/> that registered <see cref="CharacterGuid"/> at <see cref="SessionGeneration"/>, because a newer login of the account registered on another worker.</summary>
public sealed record PresenceKick(string WorkerId, int AccountId, ulong CharacterGuid, long SessionGeneration);

/// <summary>Outcome of a presence write at the authority: the result and the entry that is now active for the character (the applied one on success, the blocking one on a stale rejection).</summary>
public sealed record PresenceUpsertReply(PresenceMutationResult Result, RealmPresenceEntry? Current);

/// <summary>
/// The write side of realm presence as a worker sees it, whether the Social lease holder is this process (<see cref="PresenceHolderService"/>, an in-process call) or
/// another one (<see cref="PeerPresenceClient"/>, over the peer link). All members are thread-safe; none may be awaited on the world thread.
/// </summary>
public interface IPresenceAuthority
{
    /// <summary>
    /// A login: the authority assigns a realm-wide <see cref="RealmPresenceEntry.SessionGeneration"/> and <see cref="RealmPresenceEntry.PresenceVersion"/> at least the ones
    /// offered (the reply's <see cref="PresenceUpsertReply.Current"/> carries the effective values), and every entry of the same account owned by another worker is
    /// removed and queued as a <see cref="PresenceKick"/> for that worker.
    /// </summary>
    Task<PresenceUpsertReply> RegisterAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default);

    /// <summary>An update (map, zone, level, guild, group, status) with the entry's own generation and version; applied through <see cref="IRealmPresenceDirectory.TryUpsert"/> unchanged.</summary>
    Task<PresenceUpsertReply> UpsertAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default);

    /// <summary>A logout: <see cref="IRealmPresenceDirectory.TryRemove"/> with the owning worker as a guard, so a late logout never deletes a newer worker's entry.</summary>
    Task<PresenceMutationResult> RemoveAsync(ulong characterGuid, long presenceVersion, long sessionGeneration, string workerId, CancellationToken cancellationToken = default);

    /// <summary>The kicks queued for <paramref name="workerId"/> since the last drain (oldest first); each is delivered once.</summary>
    Task<IReadOnlyList<PresenceKick>> DrainKicksAsync(string workerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The authoritative side of realm presence, run by the node that holds the Social lease (M15.2): wraps the node's <see cref="IRealmPresenceDirectory"/> and
/// <see cref="IClusterSocialRouter"/>, implements <see cref="IPresenceAuthority"/> for the in-process workers and answers the peer link's presence requests
/// (<see cref="Handle"/>, installed as <see cref="Peer.PeerLinkHost.PayloadHandler"/>).
/// <para>
/// Duplicate-account replacement: sessions are claimed per node, so two workers can both hold generation 1 of one account. <see cref="RegisterAsync"/> therefore stamps every
/// login with a generation above every one it has assigned for that account and a version above every version it has seen (<c>_versionFloor</c>), then removes the account's
/// entries on other workers and queues one <see cref="PresenceKick"/> each. The worker applies the kick only if its own record of the registration still matches, so a newer
/// local login is never kicked by a stale request.
/// </para>
/// <para>Thread-safety: one lock guards the generation, version and kick state; the directory and router have their own locks and are always taken after it (never the reverse).
/// Memory: one long per account ever registered, one version floor, at most <see cref="MaxQueuedKicksPerWorker"/> kicks per worker (oldest dropped).</para>
/// </summary>
/// <param name="directory">The authoritative presence directory.</param>
/// <param name="router">The authoritative social router.</param>
/// <param name="holdsSocial">Whether this node holds the Social lease now; null means it always does (Combined mode, tests). Read on every group request from a peer: any thread, must not block.</param>
public sealed class PresenceHolderService(IRealmPresenceDirectory directory, IClusterSocialRouter router, Func<bool>? holdsSocial = null) : IPresenceAuthority, IGroupAuthorityTransport, IGuildHolderTransport, IBattlegroundHubTransport
{
    /// <summary>Most undelivered kicks kept per worker; a worker that never drains loses its oldest.</summary>
    public const int MaxQueuedKicksPerWorker = 1024;

    /// <summary>The holder itself is always reachable (in process).</summary>
    public bool IsAuthorityReachable => true;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, long> _generationByAccount = [];
    private readonly Dictionary<string, List<PresenceKick>> _kicks = new(StringComparer.Ordinal);
    private long _versionFloor;

    /// <summary>The authoritative directory this service mutates.</summary>
    public IRealmPresenceDirectory Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>The authoritative group table (S3a): every worker's group commands run here, on whichever node holds the Social lease.</summary>
    public GroupAuthority Groups { get; } = new(directory ?? throw new ArgumentNullException(nameof(directory)));

    /// <summary>
    /// Whether group requests from peers are served here. A node can run the peer listener without the Social lease (it holds Economy, or the lease went), and a command acked here would sit in a table that may never be loaded,
    /// so <see cref="PresenceWireCodec"/> answers such a request "not the holder" and the sender keeps the command. In production this is <see cref="RealmAuthorityLeaseKeeper.Holds"/>, so it turns false as soon as the keeper knows of a loss and when the lease's local expiry has passed; the store grants the lease to another node
    /// only after that expiry, so a deposed node still takes commands (until its next renewal tick, 10 s by default) only when its clock runs behind the store's.
    /// </summary>
    public bool HoldsSocial => holdsSocial is null || holdsSocial();

    /// <inheritdoc cref="IGroupAuthorityTransport.ExecuteGroupCommand"/>
    public void ExecuteGroupCommand(string workerId, GroupCommand command, long commandId)
    {
        try
        {
            Groups.Execute(workerId, command);
        }
        catch (InvalidOperationException ex) when (ex is not PresenceRefusedException)
        {
            // As over the peer link, where the wire turns it into a refusal: the table's own code failed on this command, which says nothing about the link, so the sender drops it instead of retrying it as a transport failure (for a Login or Logout, forever).
            throw new PresenceRefusedException(ex.Message);
        }
    }

    public IReadOnlyList<GroupEvent> DrainGroupEvents(string workerId) => Groups.Drain(workerId);

    /// <summary>The authoritative guild and charter side (S3b2): outboxes of what the holder worker's managers publish, and the entry for forwarded requests.</summary>
    public GuildHolderService Guilds { get; } = new(directory ?? throw new ArgumentNullException(nameof(directory)));

    public void ForwardGuildRequest(string workerId, long requestId, GuildRequest request) => Guilds.Submit(workerId, requestId, request);

    public IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId) => Guilds.Drain(workerId);

    /// <summary>The battleground queue mailbox: workers' queue commands wait here for the battleground holder (the node that owns the battleground maps) and its notices wait for the workers.</summary>
    public BattlegroundHub Battlegrounds { get; } = new();

    public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundCommand> commands) => Battlegrounds.Submit(workerId, commands);

    public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId) => Battlegrounds.Take(nodeId);

    public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices) => Battlegrounds.Post(nodeId, notices);

    public IReadOnlyList<ArcaneCore.World.Battlegrounds.BattlegroundNotice> DrainBattlegroundNotices(string workerId) => Battlegrounds.Drain(workerId);

    /// <summary>The authoritative router this service answers for.</summary>
    public IClusterSocialRouter Router { get; } = router ?? throw new ArgumentNullException(nameof(router));

    public Task<PresenceUpsertReply> RegisterAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
        => Task.FromResult(Register(entry));

    public Task<PresenceUpsertReply> UpsertAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
        => Task.FromResult(Upsert(entry));

    public Task<PresenceMutationResult> RemoveAsync(ulong characterGuid, long presenceVersion, long sessionGeneration, string workerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Remove(characterGuid, presenceVersion, sessionGeneration, workerId));

    public Task<IReadOnlyList<PresenceKick>> DrainKicksAsync(string workerId, CancellationToken cancellationToken = default)
        => Task.FromResult(DrainKicks(workerId));

    public PresenceUpsertReply Register(RealmPresenceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _generationByAccount.TryGetValue(entry.AccountId, out long lastGeneration);
            long generation = Math.Max(entry.SessionGeneration, lastGeneration + 1);
            long version = Math.Max(entry.PresenceVersion, _versionFloor + 1);
            RealmPresenceEntry effective = entry with { SessionGeneration = generation, PresenceVersion = version };

            IReadOnlyList<RealmPresenceEntry> sameAccount = directory.FindByAccount(entry.AccountId);
            PresenceMutationResult result = directory.TryUpsert(effective, out RealmPresenceEntry? current);
            if (result != PresenceMutationResult.Applied)
            {
                _versionFloor = Math.Max(_versionFloor, current?.PresenceVersion ?? 0);
                return new PresenceUpsertReply(result, current);
            }

            _generationByAccount[entry.AccountId] = generation;
            _versionFloor = version;
            foreach (RealmPresenceEntry other in sameAccount)
            {
                if (string.Equals(other.WorkerId, effective.WorkerId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (other.CharacterGuid != effective.CharacterGuid)
                {
                    directory.TryRemove(other.CharacterGuid, other.PresenceVersion, other.SessionGeneration, other.WorkerId);
                }

                QueueKick(new PresenceKick(other.WorkerId, other.AccountId, other.CharacterGuid, other.SessionGeneration));
            }

            return new PresenceUpsertReply(PresenceMutationResult.Applied, current);
        }
    }

    public PresenceUpsertReply Upsert(RealmPresenceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            PresenceMutationResult result = directory.TryUpsert(entry, out RealmPresenceEntry? current);
            if (result == PresenceMutationResult.Applied)
            {
                _versionFloor = Math.Max(_versionFloor, entry.PresenceVersion);
                _generationByAccount.TryGetValue(entry.AccountId, out long last);
                _generationByAccount[entry.AccountId] = Math.Max(last, entry.SessionGeneration);
            }

            return new PresenceUpsertReply(result, current);
        }
    }

    public PresenceMutationResult Remove(ulong characterGuid, long presenceVersion, long sessionGeneration, string? workerId)
    {
        lock (_gate)
        {
            PresenceMutationResult result = directory.TryRemove(characterGuid, presenceVersion, sessionGeneration, workerId);
            if (result == PresenceMutationResult.Applied)
            {
                _versionFloor = Math.Max(_versionFloor, presenceVersion);
            }

            return result;
        }
    }

    public IReadOnlyList<PresenceKick> DrainKicks(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        lock (_gate)
        {
            if (!_kicks.TryGetValue(workerId, out List<PresenceKick>? queue) || queue.Count == 0)
            {
                return [];
            }

            PresenceKick[] drained = [.. queue];
            queue.Clear();
            return drained;
        }
    }

    /// <summary>Under the gate.</summary>
    private void QueueKick(PresenceKick kick)
    {
        if (!_kicks.TryGetValue(kick.WorkerId, out List<PresenceKick>? queue))
        {
            queue = [];
            _kicks[kick.WorkerId] = queue;
        }

        queue.Add(kick);
        if (queue.Count > MaxQueuedKicksPerWorker)
        {
            queue.RemoveRange(0, queue.Count - MaxQueuedKicksPerWorker);
        }
    }

    /// <summary>For <see cref="Peer.PeerLinkHost.RememberAnswer"/>: only a group command that was taken is worth remembering, see <see cref="PresenceWireCodec.IsRetriableAndTaken"/>.</summary>
    public static bool RememberAnswer(ReadOnlyMemory<byte> request, byte[] answer) => PresenceWireCodec.IsRetriableAndTaken(request, answer);

    /// <summary>
    /// Answers one peer-link request (see <see cref="PresenceWireCodec"/>) with the encoded reply. Never throws for a malformed or oversized request: the reply is then an
    /// error frame, so one bad frame does not close the link. Suitable as <see cref="Peer.PeerLinkHost.PayloadHandler"/>.
    /// </summary>
    public byte[] Handle(ReadOnlyMemory<byte> request)
    {
        try
        {
            byte[] reply = PresenceWireCodec.Dispatch(this, request);
            return reply.Length <= Peer.PeerLinkWire.MaxPayloadBytes
                ? reply
                : PresenceWireCodec.EncodeError($"reply of {reply.Length} B exceeds {Peer.PeerLinkWire.MaxPayloadBytes} B");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PresenceWireCodec.EncodeError(ex.Message);
        }
    }
}
