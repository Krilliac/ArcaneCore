using System.Collections.Concurrent;
using System.Threading.Channels;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// The guild half of the Social holder as a worker reaches it: the holder's <see cref="GuildHolderService"/> in process (<see cref="PresenceHolderService"/>) or over the peer
/// link (<see cref="PeerPresenceClient"/>). Blocking members: never call on the world thread.
/// </summary>
public interface IGuildHolderTransport
{
    /// <summary>Hand <paramref name="request"/> of a character served by <paramref name="workerId"/> to the holder, which runs it on its world thread. <paramref name="requestId"/> 0 asks for no answer.</summary>
    void ForwardGuildRequest(string workerId, long requestId, GuildRequest request);

    /// <summary>One bounded batch of the events queued for <paramref name="workerId"/>, oldest first; empty when there is none.</summary>
    IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId);
}

/// <summary>
/// The authoritative side of guild and charter operations on the node that holds the Social lease (M15.2 S3b2). It owns one outbox per worker: what the holder's
/// <see cref="GuildManager"/> and <see cref="PetitionManager"/> publish (replicas), the packets they send to characters online on other workers, and the answers to forwarded
/// requests. Workers pull their outbox in bounded batches (<see cref="Drain"/>), so a worker that is down or slow costs the holder memory only up to the caps below.
/// <para>
/// Replica events coalesce (a newer state of the same guild or petition replaces the queued one), a queue holds at most <see cref="MaxQueuedEventsPerWorker"/> events (the oldest
/// are dropped and counted), and a worker that has not touched the service for <see cref="IdleWorkerTimeout"/> loses its outbox and gets a full snapshot when it returns. A replica
/// larger than <see cref="MaxBatchBytes"/> cannot be carried by one peer-link frame and is dropped and counted (<see cref="Oversized"/>).
/// </para>
/// Thread-safety: the outboxes are guarded by one lock; <see cref="Submit"/>, <see cref="Drain"/> and the lookups are safe from any thread; <see cref="Deliver"/> and the
/// replication calls are made from the holder's world thread. Ownership: holds references to the holder worker's managers once <see cref="Bind"/> is called.
/// </summary>
public sealed class GuildHolderService(IRealmPresenceDirectory directory, ILogger? logger = null)
{
    /// <summary>Most events queued for one worker.</summary>
    public const int MaxQueuedEventsPerWorker = 4096;

    /// <summary>Most events in one drain reply.</summary>
    public const int MaxBatchEvents = 512;

    /// <summary>Most encoded bytes of events in one drain reply (the peer-link payload limit with room for the reply header).</summary>
    public const int MaxBatchBytes = 56 * 1024;

    /// <summary>How long a worker may stay silent before its outbox is dropped.</summary>
    public static readonly TimeSpan IdleWorkerTimeout = TimeSpan.FromMinutes(5);

    private sealed record Binding(string SelfWorkerId, GuildManager Guilds, PetitionManager Petitions, Action<Action> Post);

    private sealed class Outbox
    {
        public readonly List<GuildLinkEvent> Events = [];
        public long LastTouchMs;
    }

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Outbox> _outboxes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _snapshotPending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _snapshotted = new(StringComparer.Ordinal);
    private Binding? _binding;
    private long _dropped;
    private long _oversized;

    /// <summary>Events dropped because a worker's queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Events dropped because one alone exceeded <see cref="MaxBatchBytes"/>.</summary>
    public long Oversized => Interlocked.Read(ref _oversized);

    /// <summary>
    /// Connect the holder worker's managers and its world thread (<paramref name="post"/> runs an action there). <paramref name="selfWorkerId"/> is this node's worker id: characters the
    /// directory places on it are served locally and never queued. Call once the social feature is attached; workers that arrived earlier get their snapshot now.
    /// </summary>
    public void Bind(string selfWorkerId, GuildManager guilds, PetitionManager petitions, Action<Action> post)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfWorkerId);
        ArgumentNullException.ThrowIfNull(guilds);
        ArgumentNullException.ThrowIfNull(petitions);
        ArgumentNullException.ThrowIfNull(post);
        Volatile.Write(ref _binding, new Binding(selfWorkerId, guilds, petitions, post));
        TrySnapshot();
    }

    /// <summary>Events queued for <paramref name="workerId"/> (tests and diagnostics).</summary>
    public int QueuedFor(string workerId)
    {
        lock (_gate)
        {
            return _outboxes.TryGetValue(workerId, out Outbox? box) ? box.Events.Count : 0;
        }
    }

    // ---- worker to holder ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Queue <paramref name="request"/> of <paramref name="workerId"/> for the holder's world thread, which runs <see cref="GuildManager.Serve"/> and, when
    /// <paramref name="requestId"/> is not 0, queues the answer for the worker. Throws <see cref="InvalidOperationException"/> while the holder's managers are not bound.
    /// </summary>
    public void Submit(string workerId, long requestId, GuildRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(request);
        Binding binding = Volatile.Read(ref _binding) ?? throw new InvalidOperationException("the guild holder is not ready");
        Touch(workerId);
        binding.Post(() => Run(binding, workerId, requestId, request));
    }

    private void Run(Binding binding, string workerId, long requestId, GuildRequest request)
    {
        bool result = false;
        try
        {
            result = binding.Guilds.Serve(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.guild-serve", ex, "A forwarded guild request {Kind} failed on the holder", request.Kind);
        }

        if (requestId != 0)
        {
            Enqueue(workerId, GuildLinkEvent.OfCompleted(requestId, result), coalesceKey: null);
        }
    }

    /// <summary>The events queued for <paramref name="workerId"/>: up to <see cref="MaxBatchEvents"/> and <see cref="MaxBatchBytes"/> bytes, removed from the queue. Registers the worker.</summary>
    public IReadOnlyList<GuildLinkEvent> Drain(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        Touch(workerId);
        lock (_gate)
        {
            if (!_outboxes.TryGetValue(workerId, out Outbox? box) || box.Events.Count == 0)
            {
                return [];
            }

            var batch = new List<GuildLinkEvent>();
            int bytes = 0;
            int taken = 0;
            while (taken < box.Events.Count && batch.Count < MaxBatchEvents)
            {
                GuildLinkEvent e = box.Events[taken];
                int size = GuildWireCodec.EncodedSize(e);
                if (size > MaxBatchBytes)
                {
                    Interlocked.Increment(ref _oversized);
                    _logger.LogWarning("Guild event {Kind} of {Size} B is over the peer-link limit and was dropped", e.Kind, size);
                    taken++;
                    continue;
                }

                if (bytes + size > MaxBatchBytes)
                {
                    break;
                }

                bytes += size;
                batch.Add(e);
                taken++;
            }

            box.Events.RemoveRange(0, taken);
            return batch;
        }
    }

    // ---- holder to worker (world thread of the holder) -----------------------------------------------------------------------------------------

    /// <summary>A packet for a character that is online on another worker: queued for the worker the realm directory places it on; dropped when it is online nowhere or here.</summary>
    public void Deliver(ObjectGuid character, WorldOpcode opcode, byte[] payload)
    {
        if (!character.IsPlayer || directory.FindByGuid(character.Low) is not { } entry)
        {
            return;
        }

        if (Volatile.Read(ref _binding) is { } binding && string.Equals(entry.WorkerId, binding.SelfWorkerId, StringComparison.Ordinal))
        {
            return;
        }

        Enqueue(entry.WorkerId, GuildLinkEvent.OfDeliver(character.Value, opcode, payload), coalesceKey: null);
    }

    /// <summary>Publish the stored state of a guild (null: disbanded) to every known worker.</summary>
    public void ReplicateGuild(int guildId, GuildData? data)
        => EnqueueAll(GuildLinkEvent.OfGuild(guildId, data), (byte)GuildLinkEventKind.Guild, guildId);

    /// <summary>Publish the state of a petition (null: gone) to every known worker.</summary>
    public void ReplicatePetition(int petitionId, PetitionData? data)
        => EnqueueAll(GuildLinkEvent.OfPetition(petitionId, data), (byte)GuildLinkEventKind.Petition, petitionId);

    // ---- the realm directory as the guild rules' view of other workers -------------------------------------------------------------------------

    /// <summary>A character the realm directory places on another worker, by guid; null when it is online nowhere or on the holder's own worker. In-memory, never blocks.</summary>
    public GuildRemoteCharacter? FindCharacter(ObjectGuid character)
        => character.IsPlayer ? Remote(directory.FindByGuid(character.Low)) : null;

    /// <summary>As <see cref="FindCharacter(ObjectGuid)"/>, by name.</summary>
    public GuildRemoteCharacter? FindCharacter(string name) => Remote(directory.FindByName(name));

    private GuildRemoteCharacter? Remote(RealmPresenceEntry? entry)
    {
        if (entry is null || (Volatile.Read(ref _binding) is { } binding && string.Equals(entry.WorkerId, binding.SelfWorkerId, StringComparison.Ordinal)))
        {
            return null;
        }

        return new GuildRemoteCharacter(
            ObjectGuid.Player((uint)entry.CharacterGuid), entry.CharacterName, TeamRules.TeamOf((Race)entry.Race), entry.Level, entry.Class, entry.ZoneId, entry.IsAfk, entry.IsDnd);
    }

    // ---- outboxes ------------------------------------------------------------------------------------------------------------------------------

    private static long NowMs() => Environment.TickCount64;

    /// <summary>Register or refresh a worker; a worker seen for the first time (or again after its outbox idled out) gets a full snapshot.</summary>
    private void Touch(string workerId)
    {
        bool fresh;
        lock (_gate)
        {
            long now = NowMs();
            if (!_outboxes.TryGetValue(workerId, out Outbox? box))
            {
                box = new Outbox();
                _outboxes[workerId] = box;
            }

            box.LastTouchMs = now;
            fresh = !_snapshotPending.Contains(workerId) && !_snapshotted.Contains(workerId);
            if (fresh)
            {
                _snapshotPending.Add(workerId);
            }
        }

        if (fresh)
        {
            TrySnapshot();
        }
    }

    private void TrySnapshot()
    {
        if (Volatile.Read(ref _binding) is not { } binding)
        {
            return;
        }

        string[] workers;
        lock (_gate)
        {
            if (_snapshotPending.Count == 0)
            {
                return;
            }

            workers = [.. _snapshotPending];
            _snapshotPending.Clear();
            foreach (string w in workers)
            {
                _snapshotted.Add(w);
            }
        }

        binding.Post(() =>
        {
            if (!binding.Guilds.IsAuthority)
            {
                return;
            }

            foreach (Guild guild in binding.Guilds.All.ToArray())
            {
                foreach (string worker in workers)
                {
                    Enqueue(worker, GuildLinkEvent.OfGuild(guild.Id, guild.ToData()), (byte)GuildLinkEventKind.Guild, guild.Id);
                }
            }

            foreach (Petition petition in binding.Petitions.All.ToArray())
            {
                foreach (string worker in workers)
                {
                    Enqueue(worker, GuildLinkEvent.OfPetition(petition.Id, petition.ToData()), (byte)GuildLinkEventKind.Petition, petition.Id);
                }
            }
        });
    }

    private void Enqueue(string workerId, GuildLinkEvent e, (byte Kind, int Id)? coalesceKey)
    {
        lock (_gate)
        {
            if (!_outboxes.TryGetValue(workerId, out Outbox? box))
            {
                box = new Outbox { LastTouchMs = NowMs() };
                _outboxes[workerId] = box;
            }

            AddTo(box, e, coalesceKey);
        }
    }

    private void Enqueue(string workerId, GuildLinkEvent e, byte kind, int id) => Enqueue(workerId, e, (kind, id));

    private void EnqueueAll(GuildLinkEvent e, byte kind, int id)
    {
        lock (_gate)
        {
            long now = NowMs();
            long idle = (long)IdleWorkerTimeout.TotalMilliseconds;
            List<string>? expired = null;
            foreach ((string worker, Outbox box) in _outboxes)
            {
                if (now - box.LastTouchMs > idle)
                {
                    (expired ??= []).Add(worker);
                    continue;
                }

                AddTo(box, e, (kind, id));
            }

            if (expired is not null)
            {
                foreach (string worker in expired)
                {
                    _outboxes.Remove(worker);
                    _snapshotted.Remove(worker);
                    _logger.LogInformation("Guild outbox of idle worker {Worker} dropped; it gets a snapshot when it returns", worker);
                }
            }
        }
    }

    /// <summary>Under the gate.</summary>
    private void AddTo(Outbox box, GuildLinkEvent e, (byte Kind, int Id)? coalesceKey)
    {
        if (coalesceKey is { } key)
        {
            for (int i = 0; i < box.Events.Count; i++)
            {
                GuildLinkEvent queued = box.Events[i];
                if ((byte)queued.Kind == key.Kind && queued.Id == key.Id)
                {
                    box.Events.RemoveAt(i);
                    break;
                }
            }
        }

        box.Events.Add(e);
        if (box.Events.Count > MaxQueuedEventsPerWorker)
        {
            int excess = box.Events.Count - MaxQueuedEventsPerWorker;
            box.Events.RemoveRange(0, excess);
            Interlocked.Add(ref _dropped, excess);
        }
    }
}

/// <summary>
/// The production <see cref="IGuildHolderLink"/> of a cluster node (M15.2 S3b2). On a worker that is not the Social holder, <see cref="Forward"/> queues the request for one
/// sender task, which hands requests in order to the holder (<see cref="IGuildHolderTransport"/>: over the peer link), and a pump (<see cref="PumpOnce"/>, every interval after
/// <see cref="Start"/>) drains the events the holder queued for this worker and posts them to the world thread: packets to the character's session, guild and petition replicas to
/// the managers, answers to the completion callbacks. On the holder, <see cref="Deliver"/> and the replication calls queue into the local <see cref="GuildHolderService"/> and
/// <see cref="FindOnline(ObjectGuid)"/> reads the realm presence directory; on a worker that is not the holder they do nothing (those rules only run on the holder).
/// <para>
/// <see cref="TargetIgnores"/> is false: ignore lists live on the target's own worker and the directory does not carry them, so a guild invitation to an ignoring character is
/// not refused on its behalf. A request whose send fails (link down, refusal) is dropped with a log line and its completion callback runs with false; while the last
/// contact with the holder failed, <see cref="Forward"/> returns false (nothing sent) so the caller can answer the player.
/// </para>
/// Ownership: owns the sender and pump tasks; <see cref="DisposeAsync"/> stops both. Thread-safety: all members are safe from any thread except where noted (Deliver, the
/// replication calls and FindOnline are made on the holder's world thread, which is the contract of <see cref="IGuildHolderLink"/>).
/// </summary>
public sealed class PeerGuildHolderLink : IGuildHolderLink, IAsyncDisposable
{
    /// <summary>Most requests waiting for the sender task.</summary>
    public const int MaxQueuedRequests = 4096;

    /// <summary>Most batches one <see cref="PumpOnce"/> pass takes from the holder.</summary>
    public const int MaxPumpBatches = 64;

    /// <summary>How long a forwarded request waits for its answer before its callback runs with false.</summary>
    public static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);

    private sealed record Pending(long RequestId, GuildRequest Request);

    private sealed record Waiting(Action<bool> Completed, long StartedMs);

    private readonly IGuildHolderTransport _transport;
    private readonly GuildHolderService _local;
    private readonly string _workerId;
    private readonly Func<bool> _holdsSocial;
    private readonly ILogger _logger;
    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(MaxQueuedRequests) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<long, Waiting> _waiting = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private readonly List<TaskCompletionSource> _flushWaiters = [];
    private static readonly Pending FlushMarker = new(-1, new GuildRequest(GuildRequestKind.Info, ObjectGuid.Empty));
    private Task _pump = Task.CompletedTask;
    private WorldRuntime? _world;
    private GuildManager? _guilds;
    private PetitionManager? _petitions;
    private long _nextRequestId;
    private long _queueDropped;
    private long _sendFailed;
    private int _unreachable;
    private int _started;
    private int _disposed;

    public PeerGuildHolderLink(IGuildHolderTransport transport, GuildHolderService local, string workerId, Func<bool> holdsSocial, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(holdsSocial);
        _transport = transport;
        _local = local;
        _workerId = workerId;
        _holdsSocial = holdsSocial;
        _logger = logger ?? NullLogger.Instance;
        _sender = Task.Run(RunSenderAsync, CancellationToken.None);
    }

    /// <summary>This worker's id.</summary>
    public string WorkerId => _workerId;

    /// <summary>Requests dropped because the queue was full.</summary>
    public long QueueDropped => Interlocked.Read(ref _queueDropped);

    /// <summary>Requests dropped because the holder could not be reached or refused them.</summary>
    public long SendFailed => Interlocked.Read(ref _sendFailed);

    /// <summary>
    /// Connect this worker's world and managers (the social petition feature calls this when it attaches): events from the holder are posted to <paramref name="world"/>, and the
    /// holder-side service serves forwarded requests on it when this worker holds the lease.
    /// </summary>
    public void Bind(WorldRuntime world, GuildManager guilds, PetitionManager petitions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(guilds);
        ArgumentNullException.ThrowIfNull(petitions);
        _world = world;
        _guilds = guilds;
        _petitions = petitions;
        _local.Bind(_workerId, guilds, petitions, world.Post);
    }

    public bool IsHolder => _holdsSocial();

    public bool Forward(GuildRequest request, Action<bool>? completed)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0 || IsHolder || Volatile.Read(ref _unreachable) != 0)
        {
            return false;
        }

        long id = 0;
        if (completed is not null)
        {
            id = Interlocked.Increment(ref _nextRequestId);
            _waiting[id] = new Waiting(completed, Environment.TickCount64);
        }

        if (!_queue.Writer.TryWrite(new Pending(id, request)))
        {
            Interlocked.Increment(ref _queueDropped);
            _waiting.TryRemove(id, out _);
            return false;
        }

        return true;
    }

    public void Deliver(ObjectGuid character, WorldOpcode opcode, byte[] payload)
    {
        if (IsHolder)
        {
            _local.Deliver(character, opcode, payload);
        }
    }

    public void ReplicateGuild(int guildId, GuildData? data)
    {
        if (IsHolder)
        {
            _local.ReplicateGuild(guildId, data);
        }
    }

    public void ReplicatePetition(int petitionId, PetitionData? data)
    {
        if (IsHolder)
        {
            _local.ReplicatePetition(petitionId, data);
        }
    }

    public GuildRemoteCharacter? FindOnline(ObjectGuid character) => IsHolder ? _local.FindCharacter(character) : null;

    public GuildRemoteCharacter? FindOnline(string name) => IsHolder && !string.IsNullOrWhiteSpace(name) ? _local.FindCharacter(name) : null;

    public bool TargetIgnores(ObjectGuid target, ObjectGuid ignorer) => false;

    /// <summary>Completes when every request queued before the call has been sent (tests and orderly shutdown).</summary>
    public async Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_flushWaiters)
        {
            _flushWaiters.Add(done);
        }

        if (!_queue.Writer.TryWrite(FlushMarker))
        {
            done.TrySetResult();
        }

        await done.Task.ConfigureAwait(false);
    }

    private async Task RunSenderAsync()
    {
        ChannelReader<Pending> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out Pending? pending))
                {
                    if (ReferenceEquals(pending, FlushMarker))
                    {
                        lock (_flushWaiters)
                        {
                            foreach (TaskCompletionSource waiter in _flushWaiters)
                            {
                                waiter.TrySetResult();
                            }

                            _flushWaiters.Clear();
                        }

                        continue;
                    }

                    try
                    {
                        _transport.ForwardGuildRequest(_workerId, pending.RequestId, pending.Request);
                        Volatile.Write(ref _unreachable, 0);
                    }
                    catch (Exception ex) when (IsTransportFailure(ex))
                    {
                        Volatile.Write(ref _unreachable, 1);
                        Interlocked.Increment(ref _sendFailed);
                        _logger.LogWarning("Guild request {Kind} of {Actor} dropped ({Message})", pending.Request.Kind, pending.Request.Actor.Value, ex.Message);
                        Complete(pending.RequestId, false);
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                    {
                        Interlocked.Increment(ref _sendFailed);
                        _logger.LogFault("cluster.guild-send", ex, "A guild request failed at the holder; it is dropped");
                        Complete(pending.RequestId, false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Drains the events the holder queued for this worker and posts them to the world thread; returns how many. Blocking when the holder is remote: never call on the world thread.</summary>
    public int PumpOnce()
    {
        SweepWaiting();
        if (IsHolder || _world is null)
        {
            return 0;
        }

        int handed = 0;
        for (int batch = 0; batch < MaxPumpBatches; batch++)
        {
            IReadOnlyList<GuildLinkEvent> events = _transport.DrainGuildEvents(_workerId);
            Volatile.Write(ref _unreachable, 0);
            if (events.Count == 0)
            {
                break;
            }

            foreach (GuildLinkEvent e in events)
            {
                Apply(e);
                handed++;
            }
        }

        return handed;
    }

    private void Apply(GuildLinkEvent e)
    {
        WorldRuntime? world = _world;
        GuildManager? guilds = _guilds;
        PetitionManager? petitions = _petitions;
        if (world is null || guilds is null || petitions is null)
        {
            return;
        }

        switch (e.Kind)
        {
            case GuildLinkEventKind.Deliver:
            {
                var guid = new ObjectGuid(e.Character);
                world.Post(() => world.FindOnlinePlayer(guid)?.Session.Send(e.Opcode, e.Payload));
                break;
            }

            case GuildLinkEventKind.Guild:
                world.Post(() => guilds.ApplyReplica(e.Id, e.Guild));
                break;
            case GuildLinkEventKind.Petition:
                world.Post(() => petitions.ApplyReplica(e.Id, e.Petition));
                break;
            case GuildLinkEventKind.Completed:
                Complete(e.RequestId, e.Result);
                break;
        }
    }

    /// <summary>Runs the callback of <paramref name="requestId"/> (if any) on the world thread.</summary>
    private void Complete(long requestId, bool result)
    {
        if (requestId == 0 || !_waiting.TryRemove(requestId, out Waiting? waiting))
        {
            return;
        }

        _world?.Post(() => waiting.Completed(result));
    }

    private void SweepWaiting()
    {
        if (_waiting.IsEmpty)
        {
            return;
        }

        long now = Environment.TickCount64;
        long limit = (long)CompletionTimeout.TotalMilliseconds;
        foreach ((long id, Waiting waiting) in _waiting)
        {
            if (now - waiting.StartedMs > limit)
            {
                Complete(id, false);
            }
        }
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
                    Volatile.Write(ref _unreachable, 1);
                    _logger.LogDebug("Guild event pump deferred ({Message})", ex.Message);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.LogFault("cluster.guild-pump", ex, "A guild event pump pass failed; the loop continues");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsTransportFailure(Exception ex)
        => ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException;

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
            FaultLedger.Shared.Record("cluster.guild-link", ex);
        }

        _stop.Dispose();
    }
}
