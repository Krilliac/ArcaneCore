using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Persists instance changes made on the world thread, off the world thread and in order (one
/// consumer, like <see cref="Social.SocialWriteQueue"/>), and keeps them until they are durable.
/// <para>
/// Every write sets an absolute state for its key (an instance record, instance script data, an
/// instance deletion, a raid reset time, a character bind, a character's last instance, a group
/// bind, or a character deletion purge). A write that fails all <c>MaxAttempts</c> inline attempts is
/// <b>retained</b>, never dropped: the next write, the periodic <see cref="RetainedRetryTimer"/>
/// sweep, <see cref="FlushCharacterAsync"/> (the login and handoff barrier, which faults while the
/// character's writes are still not durable), <see cref="RequestRetry"/> (logout) and
/// <see cref="StopAsync"/> (a final retry that throws <see cref="InvalidOperationException"/> naming
/// what is still not durable) all retry it. Instance script data waits, retained, behind a retained row save of its
/// instance: it only updates an existing row, so it is written once the row has landed.
/// </para>
/// <para>
/// A <see cref="CharacterOwnershipRejectedException"/> is terminal on the first attempt: it is never
/// retried as a transient failure, stops the queue from issuing further store writes, notifies the
/// host lifetime (<see cref="IHostApplicationLifetime.StopApplication"/>) and <see cref="FatalFailure"/>,
/// and faults <see cref="FlushCharacterAsync"/> and <see cref="StopAsync"/>.
/// </para>
/// </summary>
public sealed class InstanceWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null) : IInstancePersistence
{
    private const int MaxAttempts = 3;

    /// <summary>Backoff unit between inline attempts of one write (attempt n waits n times this; tests shorten it).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How often retained writes are retried by a background sweep while any exist (one attempt per key, stopping at the first locked one; tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Retained> _retained = [];
    private readonly List<string> _droppedWrites = []; // under _gate: writes refused after the queue stopped, reported once the gate is released
    private readonly Dictionary<int, HashSet<Key>> _retainedByCharacter = [];
    private readonly Dictionary<int, long> _deletedCharacterEpoch = [];
    private readonly Dictionary<int, long> _deletedInstanceEpoch = [];
    private long _epoch;

    private Task? _consumer;
    private Task? _stop;
    private RetainedRetryTimer? _retry;
    private volatile bool _stopped;
    private readonly StopDrainBudget _drain = drain ?? new StopDrainBudget();
    private Exception? _terminalFailure;
    private int _pending;
    private long _enqueued;
    private long _attempted;

    private enum KeyKind
    {
        PlayerBind,
        LastInstance,
        GroupBind,
        CharacterDelete,
        InstanceSave,
        InstanceData,
        InstanceDelete,
        ResetTime,
    }

    private enum WorkKind
    {
        Write,
        Barrier,
        FlushCharacter,
        Retry,
        DrainAll,
        Sweep,
    }

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>The number of writes queued so far: a watermark for <see cref="WaitForAsync"/>.</summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);

    /// <summary>Ids of characters with an instance write (bind, last instance, group bind or delete) that failed all attempts and is retained, ascending.</summary>
    public IReadOnlyList<int> RetainedCharacters
    {
        get
        {
            lock (_gate)
            {
                return [.. _retainedByCharacter.Keys.Order()];
            }
        }
    }

    /// <summary>Terminal failure that halted the queue (such as <see cref="CharacterOwnershipRejectedException"/>), if any.</summary>
    public Exception? TerminalFailure
    {
        get
        {
            lock (_gate)
            {
                return _terminalFailure;
            }
        }
    }

    /// <summary>Raised once when the queue enters terminal failure (for example on <see cref="CharacterOwnershipRejectedException"/>).</summary>
    public event Action<Exception>? FatalFailure;

    /// <summary>
    /// Raised on the calling (world) thread for every instance whose deletion is queued, before it is queued: a deleted save, and also the saves
    /// the manager drops while it loads (which do not reach <c>InstanceManager.InstanceDeleted</c>).
    /// </summary>
    public event Action<uint>? InstanceRemoved;

    /// <summary>Tests: queue one retained-write sweep now, exactly as a timer tick would; false when one is already pending or the queue is not started.</summary>
    internal bool PostRetainedSweepForTest() => _retry?.TryPost() ?? false;

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _retry = new RetainedRetryTimer(
            RetainedRetryInterval,
            HasAnyRetained,
            () => _channel.Writer.TryWrite(new Work(WorkKind.Sweep, default, default, 0, 0, null)));
        _retry.Start();
        _consumer = Task.Run(ConsumeAsync);
    }

    private bool HasAnyRetained()
    {
        lock (_gate)
        {
            return _terminalFailure is null && _retained.Count > 0;
        }
    }

    public void InstanceSaved(InstanceSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        var record = new InstanceRecord(save.InstanceId, save.MapId, save.ResetTime);
        lock (_gate)
        {
            EnqueueWriteCore(new Key(KeyKind.InstanceSave, (int)save.InstanceId, 0), new Payload(Instance: record));
        }

        ReportDroppedWrites();
    }

    public void InstanceDataSaved(InstanceSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Data is { } data)
        {
            lock (_gate)
            {
                EnqueueWriteCore(new Key(KeyKind.InstanceData, (int)save.InstanceId, 0), new Payload(Data: data));
            }

            ReportDroppedWrites();
        }
    }

    internal void EnqueueInstanceSaveForTest(uint instanceId, uint mapId, long resetTime, string? data = null)
    {
        var record = new InstanceRecord(instanceId, mapId, resetTime);
        lock (_gate)
        {
            EnqueueWriteCore(new Key(KeyKind.InstanceSave, (int)instanceId, 0), new Payload(Instance: record));
            if (data is not null)
            {
                EnqueueWriteCore(new Key(KeyKind.InstanceData, (int)instanceId, 0), new Payload(Data: data));
            }
        }

        ReportDroppedWrites();
    }

    public void InstanceDeleted(uint instanceId)
    {
        InstanceRemoved?.Invoke(instanceId);
        int id = (int)instanceId;
        lock (_gate)
        {
            _epoch++;
            _deletedInstanceEpoch[id] = _epoch;
            RemoveRetained(new Key(KeyKind.InstanceSave, id, 0));
            RemoveRetained(new Key(KeyKind.InstanceData, id, 0));
            EnqueueWriteCore(new Key(KeyKind.InstanceDelete, id, 0), default);
        }

        ReportDroppedWrites();
    }

    public void PlayerBound(uint characterId, uint instanceId, bool permanent)
    {
        lock (_gate)
        {
            EnqueueWriteCore(
                new Key(KeyKind.PlayerBind, (int)characterId, (int)instanceId),
                new Payload(Bind: new CharacterInstanceBindRecord((int)characterId, instanceId, permanent)));
        }

        ReportDroppedWrites();
    }

    public void PlayerUnbound(uint characterId, uint instanceId)
    {
        lock (_gate)
        {
            EnqueueWriteCore(new Key(KeyKind.PlayerBind, (int)characterId, (int)instanceId), default);
        }

        ReportDroppedWrites();
    }

    public void RaidResetTimeChanged(uint mapId, long resetTime)
    {
        lock (_gate)
        {
            EnqueueWriteCore(
                new Key(KeyKind.ResetTime, (int)mapId, 0),
                new Payload(Reset: new InstanceResetRecord(mapId, resetTime)));
        }

        ReportDroppedWrites();
    }

    public void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId)
    {
        lock (_gate)
        {
            EnqueueWriteCore(
                new Key(KeyKind.LastInstance, (int)characterId, 0),
                new Payload(Last: new CharacterLastInstanceRecord((int)characterId, mapId, instanceId)));
        }

        ReportDroppedWrites();
    }

    /// <summary>A stored group bind (vmangos <c>group_instance</c>), under the leader's character id.</summary>
    public void GroupBound(uint leaderCharacterId, uint instanceId, bool permanent)
    {
        lock (_gate)
        {
            EnqueueWriteCore(
                new Key(KeyKind.GroupBind, (int)leaderCharacterId, (int)instanceId),
                new Payload(GroupBind: new GroupInstanceBindRecord((int)leaderCharacterId, instanceId, permanent)));
        }

        ReportDroppedWrites();
    }

    public void GroupUnbound(uint leaderCharacterId, uint instanceId)
    {
        lock (_gate)
        {
            EnqueueWriteCore(new Key(KeyKind.GroupBind, (int)leaderCharacterId, (int)instanceId), default);
        }

        ReportDroppedWrites();
    }

    /// <summary>
    /// Delete a character's binds and last instance (queued by the character delete hook). Any earlier
    /// failed binds or last-instance writes retained for the character are discarded first so a retry
    /// cannot bring a row back.
    /// </summary>
    public void CharacterDeleted(int characterId)
    {
        lock (_gate)
        {
            _epoch++;
            _deletedCharacterEpoch[characterId] = _epoch;
            PurgeRetainedCharacter(characterId, includeDelete: false);
            EnqueueWriteCore(new Key(KeyKind.CharacterDelete, characterId, 0), default);
        }

        ReportDroppedWrites();
    }

    /// <summary>Drop anything retained for a deleted character or a reused id.</summary>
    public void Forget(int characterId)
    {
        lock (_gate)
        {
            _epoch++;
            _deletedCharacterEpoch[characterId] = _epoch;
            PurgeRetainedCharacter(characterId, includeDelete: true);
        }
    }

    /// <summary>True while a write for the character failed all attempts and is retained.</summary>
    public bool HasRetainedFailure(int characterId)
    {
        lock (_gate)
        {
            return _retainedByCharacter.ContainsKey(characterId);
        }
    }

    /// <summary>
    /// Wait until the first <paramref name="watermark"/> writes (<see cref="Enqueued"/> read earlier)
    /// have been attempted, whatever is queued after them. Unlike <see cref="FlushAsync"/> a steady
    /// stream of later writes cannot starve it, and it honours cancellation.
    /// </summary>
    public async Task WaitForAsync(long watermark, CancellationToken cancellationToken)
    {
        while (Interlocked.Read(ref _attempted) < watermark)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes once every write queued before the call has been attempted. A pure ordered barrier:
    /// it never retries retained failures and never throws (see <see cref="FlushCharacterAsync"/>).
    /// </summary>
    public Task FlushAsync()
    {
        if (_consumer is null)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new Work(WorkKind.Barrier, default, default, 0, 0, done)) ? done.Task : Task.CompletedTask;
    }

    /// <summary>
    /// The login and persistence barrier: after every earlier write, retries this character's retained
    /// writes once more and faults with <see cref="InvalidOperationException"/> while they are still
    /// not durable.
    /// </summary>
    public Task FlushCharacterAsync(int characterId)
    {
        if (_consumer is null || _stopped)
        {
            return RetainedFailureTask(characterId);
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new Work(WorkKind.FlushCharacter, default, default, 0, characterId, done))
            ? done.Task
            : RetainedFailureTask(characterId);
    }

    /// <summary>Queue a fire-and-forget retry of the character's retained writes (logout).</summary>
    public void RequestRetry(int characterId)
    {
        if (_consumer is not null && !_stopped)
        {
            _channel.Writer.TryWrite(new Work(WorkKind.Retry, default, default, 0, characterId, null));
        }
    }

    /// <summary>
    /// Stop accepting writes, drain the queue and retry everything retained once. Throws
    /// <see cref="InvalidOperationException"/> naming the characters, instances or maps whose writes
    /// are still not durable. Idempotent: later calls return the same outcome.
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            return _stop ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        lock (_gate)
        {
            _stopped = true;
        }

        _drain.Begin();
        if (_retry is not null)
        {
            await _retry.StopAsync().ConfigureAwait(false);
        }

        _channel.Writer.TryWrite(new Work(WorkKind.DrainAll, default, default, 0, 0, null));
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        string[] left;
        Exception? first;
        Exception? terminal;
        lock (_gate)
        {
            terminal = _terminalFailure;
            KeyValuePair<Key, Retained>[] failures = [.. _retained.OrderBy(r => r.Key.Kind).ThenBy(r => r.Key.A).ThenBy(r => r.Key.B)];
            left =
            [
                .. failures.Select(f => DescribeKey(f.Key)).Distinct(),
            ];
            first = failures.Length > 0 ? failures[0].Value.Failure : terminal;
        }

        if (left.Length > 0)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                "queue InstanceWriteQueue",
                $"queue=InstanceWriteQueue undrainedTargets={left.Length} firstTarget={left[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.instance-stop-drain", refusal);
                logger.LogFault(
                    "writequeue-instance-stop-drain",
                    refusal,
                    "instance write queue shutdown drain stalled; {Refused} of {Undrained} retained targets were not attempted: {Summary}",
                    _drain.Refused,
                    left.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"instance writes did not drain for {string.Join(", ", left)}", first);
        }

        if (terminal is not null)
        {
            throw new InvalidOperationException("instance write queue stopped after terminal ownership rejection", terminal);
        }
    }

    private static string DescribeKey(Key key) => key.Kind switch
    {
        KeyKind.PlayerBind or KeyKind.LastInstance or KeyKind.GroupBind => $"character {key.A}",
        KeyKind.CharacterDelete => $"delete of character {key.A}",
        KeyKind.InstanceSave or KeyKind.InstanceData or KeyKind.InstanceDelete => $"instance {key.A}",
        _ => $"reset time for map {key.A}",
    };

    private static bool IsCharacterKey(KeyKind kind)
        => kind is KeyKind.PlayerBind or KeyKind.LastInstance or KeyKind.GroupBind or KeyKind.CharacterDelete;

    private void EnqueueWriteCore(Key key, Payload value)
    {
        if (_stopped)
        {
            Exception failure = _terminalFailure ?? new InvalidOperationException("the instance write queue is stopped; the change was not persisted");
            SetRetained(key, value, failure);
            logger.LogError("instance write for {Target} arrived after the queue stopped; retained as undrained", DescribeKey(key));
            _droppedWrites.Add($"target={DescribeKey(key)} reason=queue-stopped");
            return;
        }

        var work = new Work(WorkKind.Write, key, value, _epoch, IsCharacterKey(key.Kind) ? key.A : 0, null);
        Interlocked.Increment(ref _pending);
        Interlocked.Increment(ref _enqueued);
        if (!_channel.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Decrement(ref _enqueued);
            Exception failure = _terminalFailure ?? new InvalidOperationException("the instance write queue is closed; the change was not persisted");
            SetRetained(key, value, failure);
            logger.LogError("instance write queue closed; write for {Target} retained as undrained", DescribeKey(key));
            _droppedWrites.Add($"target={DescribeKey(key)} reason=queue-closed");
        }
    }

    /// <summary>Reports the writes <see cref="EnqueueWriteCore"/> refused; called after the gate is released.</summary>
    private void ReportDroppedWrites()
    {
        string[] dropped;
        lock (_gate)
        {
            if (_droppedWrites.Count == 0)
            {
                return;
            }

            dropped = [.. _droppedWrites];
            _droppedWrites.Clear();
        }

        foreach (string what in dropped)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                "queue InstanceWriteQueue",
                $"queue=InstanceWriteQueue {what}",
                AnomalySeverity.Error);
        }
    }

    private void PurgeRetainedCharacter(int characterId, bool includeDelete)
    {
        if (!_retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys))
        {
            return;
        }

        foreach (Key key in keys.Where(k => includeDelete || k.Kind != KeyKind.CharacterDelete).ToArray())
        {
            RemoveRetained(key);
        }
    }

    private void SetRetained(Key key, Payload value, Exception failure)
    {
        if (_retained.TryGetValue(key, out Retained? existing))
        {
            existing.Value = value;
            existing.Failure = failure;
            return;
        }

        _retained[key] = new Retained(value, failure);
        if (IsCharacterKey(key.Kind))
        {
            if (!_retainedByCharacter.TryGetValue(key.A, out HashSet<Key>? keys))
            {
                _retainedByCharacter[key.A] = keys = [];
            }

            keys.Add(key);
        }
    }

    private void RemoveRetained(Key key)
    {
        if (_retained.Remove(key) && IsCharacterKey(key.Kind) && _retainedByCharacter.TryGetValue(key.A, out HashSet<Key>? keys))
        {
            keys.Remove(key);
            if (keys.Count == 0)
            {
                _retainedByCharacter.Remove(key.A);
            }
        }
    }

    private Exception? RetainedFailureOf(int characterId)
    {
        lock (_gate)
        {
            if (_retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys) && keys.Count > 0)
            {
                return _retained[keys.OrderBy(k => k.Kind).ThenBy(k => k.B).First()].Failure;
            }

            return _terminalFailure;
        }
    }

    private Task RetainedFailureTask(int characterId)
        => RetainedFailureOf(characterId) is { } failure ? Task.FromException(NotDurable(characterId, failure)) : Task.CompletedTask;

    private static InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"instance writes for character {characterId} are not durable", failure);

    private void FailTerminal(CharacterOwnershipRejectedException ex, IServiceProvider? scopedServices)
    {
        bool first = false;
        lock (_gate)
        {
            if (_terminalFailure is null)
            {
                _terminalFailure = ex;
                _stopped = true;
                first = true;
            }
        }

        if (!first)
        {
            return;
        }

        AnomalyLedger.Shared.Report(
            "persist",
            "circuit-open",
            "queue InstanceWriteQueue",
            $"queue=InstanceWriteQueue reason=terminal-ownership-failure error={ex.GetType().Name}",
            AnomalySeverity.Warning);
        logger.LogCritical(ex, "instance write queue entered terminal failure after character ownership rejection");
        try
        {
            scopedServices?.GetService<IHostApplicationLifetime>()?.StopApplication();
        }
        catch (OperationCanceledException)
        {
            /* expected on shutdown */
        }
        catch (Exception stopEx) when (stopEx is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("writequeue.instance-stop-host", stopEx);
        }

        FatalFailure?.Invoke(ex);
    }

    private async Task ConsumeAsync()
    {
        await foreach (Work work in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (work.Kind)
                {
                    case WorkKind.Write:
                        try
                        {
                            await ExecuteWriteAsync(work).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Increment(ref _attempted);
                            Interlocked.Decrement(ref _pending);
                        }

                        break;
                    case WorkKind.Retry:
                        await RetryCharacterAsync(work.CharacterId, MaxAttempts).ConfigureAwait(false);
                        break;
                    case WorkKind.FlushCharacter:
                        await RetryCharacterAsync(work.CharacterId, MaxAttempts).ConfigureAwait(false);
                        if (RetainedFailureOf(work.CharacterId) is { } failure)
                        {
                            work.Done?.TrySetException(NotDurable(work.CharacterId, failure));
                        }
                        else
                        {
                            work.Done?.TrySetResult();
                        }

                        break;
                    case WorkKind.DrainAll:
                        int[] characters;
                        lock (_gate)
                        {
                            characters = [.. _retainedByCharacter.Keys.Order()];
                        }

                        foreach (int id in characters)
                        {
                            await RetryCharacterAsync(id, MaxAttempts).ConfigureAwait(false);
                        }

                        await RetryOpaqueAsync(null, MaxAttempts).ConfigureAwait(false);
                        break;
                    case WorkKind.Sweep:
                        try
                        {
                            if (!_stopped)
                            {
                                await SweepRetainedAsync().ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _retry?.SweepFinished();
                        }

                        break;
                    default:
                        work.Done?.TrySetResult();
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                /* expected on shutdown */
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.writequeue", ex);
                logger.LogFault("writequeue.instance-consume", ex, "instance write queue item {Kind} failed unexpectedly", work.Kind);
                work.Done?.TrySetException(ex);
            }
        }
    }

    private bool IsObsoleteUnderGate(Key key, long epoch)
    {
        if (key.Kind is KeyKind.PlayerBind or KeyKind.LastInstance or KeyKind.GroupBind
            && _deletedCharacterEpoch.TryGetValue(key.A, out long deletedCharEpoch)
            && epoch < deletedCharEpoch)
        {
            return true;
        }

        if (key.Kind is KeyKind.InstanceSave or KeyKind.InstanceData
            && _deletedInstanceEpoch.TryGetValue(key.A, out long deletedInstEpoch)
            && epoch < deletedInstEpoch)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Under the gate: the retained row save of the instance an instance script data write belongs to, or null. The data write
    /// only updates an existing row (<see cref="IInstanceStore.SaveInstanceDataAsync"/>; vmangos InstanceData::SaveToDB,
    /// Maps/InstanceData.cpp:38, <c>UPDATE instance SET data</c>), so run before the retained row lands it would update nothing,
    /// count as durable, and the row would later land without the data. It waits behind the row save instead, as vmangos'
    /// ordered character database queue keeps it behind DungeonPersistentState::SaveToDB's <c>INSERT</c> (MapPersistentStateMgr.cpp:265).
    /// </summary>
    private Retained? RowSaveAheadOfUnderGate(Key key)
        => key.Kind == KeyKind.InstanceData && _retained.TryGetValue(key with { Kind = KeyKind.InstanceSave }, out Retained? rowSave) ? rowSave : null;

    private async Task ExecuteWriteAsync(Work work)
    {
        lock (_gate)
        {
            if (RowSaveAheadOfUnderGate(work.Key) is { } rowSave)
            {
                if (!IsObsoleteUnderGate(work.Key, work.Epoch))
                {
                    SetRetained(work.Key, work.Value, rowSave.Failure);
                }

                return;
            }
        }

        var made = new WriteAttemptCount();
        Exception? failure = await TryWriteAsync(work.Key, work.Value, MaxAttempts, made).ConfigureAwait(false);
        bool reportExhausted = false;
        lock (_gate)
        {
            if (IsObsoleteUnderGate(work.Key, work.Epoch))
            {
                return;
            }

            if (failure is null)
            {
                RemoveRetained(work.Key);
            }
            else
            {
                bool alreadyRetained = _retained.ContainsKey(work.Key); // a retained write failing again is the same episode
                SetRetained(work.Key, work.Value, failure);
                if (failure is not CharacterOwnershipRejectedException and not StopDrainExhaustedException)
                {
                    reportExhausted = !alreadyRetained;
                    logger.LogFault("writequeue.instance", failure, "instance write for {Target} failed; retained for retry", DescribeKey(work.Key));
                }
            }
        }

        if (reportExhausted && failure is not null)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-retry-exhausted",
                "queue InstanceWriteQueue",
                $"queue=InstanceWriteQueue target={DescribeKey(work.Key)} attempts={made.Made} error={failure.GetType().Name}",
                AnomalySeverity.Error);
        }

        if (failure is null)
        {
            if (IsCharacterKey(work.Key.Kind))
            {
                await RetryCharacterAsync(work.Key.A, 1).ConfigureAwait(false);
            }
            else
            {
                await RetryOpaqueAsync(work.Key, 1).ConfigureAwait(false);
            }
        }
    }

    private async Task RetryCharacterAsync(int characterId, int attempts)
    {
        KeyValuePair<Key, Retained>[] rows;
        lock (_gate)
        {
            if (_terminalFailure is not null || !_retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys))
            {
                return;
            }

            rows = [.. keys.OrderBy(k => k.Kind).ThenBy(k => k.B).Select(k => KeyValuePair.Create(k, _retained[k]))];
        }

        foreach ((Key key, Retained retained) in rows)
        {
            await RetryOneAsync(key, retained, attempts).ConfigureAwait(false);
        }
    }

    private async Task RetryOpaqueAsync(Key? except, int attempts)
    {
        KeyValuePair<Key, Retained>[] rows;
        lock (_gate)
        {
            if (_terminalFailure is not null)
            {
                return;
            }

            rows = [.. _retained.Where(r => !IsCharacterKey(r.Key.Kind) && r.Key != except).OrderBy(r => r.Key.Kind).ThenBy(r => r.Key.A)];
        }

        foreach ((Key key, Retained retained) in rows)
        {
            await RetryOneAsync(key, retained, attempts).ConfigureAwait(false);
        }
    }

    private async Task SweepRetainedAsync()
    {
        KeyValuePair<Key, Retained>[] rows;
        lock (_gate)
        {
            if (_terminalFailure is not null)
            {
                return;
            }

            rows =
            [
                .. _retained.OrderBy(r => IsCharacterKey(r.Key.Kind) ? 0 : 1).ThenBy(r => r.Key.A).ThenBy(r => r.Key.B).ThenBy(r => r.Key.Kind),
            ];
        }

        int failed = 0;
        bool locked = false;
        Exception? first = null;
        for (int i = 0; i < rows.Length; i++)
        {
            Exception? failure = await RetryOneAsync(rows[i].Key, rows[i].Value, 1).ConfigureAwait(false);
            if (failure is null)
            {
                continue;
            }

            failed++;
            first ??= failure;
            if (failure is CharacterOwnershipRejectedException)
            {
                break;
            }

            if (DatabaseTransience.IsBusy(failure))
            {
                locked = true;
                break;
            }
        }

        if (failed > 0 && Invariant.Check(first is not null, "instance retry recorded failures without a first exception")
            && first is { } firstFailure && firstFailure is not CharacterOwnershipRejectedException)
        {
            logger.LogWarning(
                "instance retry: {Failed} of {Total} retained writes still failing{Locked}; first: {Reason}",
                failed,
                rows.Length,
                locked ? " (the database is locked; the rest wait for the next sweep)" : "",
                firstFailure.Message);
        }
    }

    private async Task<Exception?> RetryOneAsync(Key key, Retained retained, int attempts)
    {
        Payload value;
        lock (_gate)
        {
            if (_terminalFailure is not null || !_retained.TryGetValue(key, out Retained? current) || !ReferenceEquals(current, retained))
            {
                return _terminalFailure;
            }

            if (RowSaveAheadOfUnderGate(key) is { } rowSave)
            {
                retained.Failure = rowSave.Failure;
                return rowSave.Failure;
            }

            value = retained.Value;
        }

        Exception? failure = await TryWriteAsync(key, value, attempts).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_retained.TryGetValue(key, out Retained? current) || !ReferenceEquals(current, retained))
            {
                return null;
            }

            if (failure is null)
            {
                RemoveRetained(key);
            }
            else
            {
                retained.Failure = failure;
            }
        }

        return failure;
    }

    private async Task<Exception?> TryWriteAsync(Key key, Payload value, int attempts, WriteAttemptCount? made = null)
    {
        lock (_gate)
        {
            if (_terminalFailure is { } terminal)
            {
                return terminal;
            }
        }

        if (_stopped)
        {
            attempts = 1; // at stop: one attempt per write, busy or not, no inline retry
        }

        for (int attempt = 1; ; attempt++)
        {
            if (attempt == 1 && _stopped && _drain.Refuse() is { } refused)
            {
                return refused; // shutdown drain only: nothing is attempted, the write stays retained
            }

            if (made is not null)
            {
                made.Made = attempt;
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            try
            {
                using StopDrainBudget.Attempt progress = _drain.BeginAttempt(); // the attempt is the store call: its verdict is in before the catch's backoff
                if (scope.ServiceProvider.GetService<IInstanceStore>() is not { } store)
                {
                    progress.Succeeded();
                    return null;
                }

                switch (key.Kind)
                {
                    case KeyKind.InstanceSave when value.Instance is { } instance:
                        await store.SaveInstanceAsync(instance).ConfigureAwait(false);
                        break;
                    case KeyKind.InstanceData when value.Data is { } data:
                        await store.SaveInstanceDataAsync((uint)key.A, data).ConfigureAwait(false);
                        break;
                    case KeyKind.InstanceDelete:
                        await store.DeleteInstanceAsync((uint)key.A).ConfigureAwait(false);
                        break;
                    case KeyKind.ResetTime when value.Reset is { } reset:
                        await store.SaveResetTimeAsync(reset).ConfigureAwait(false);
                        break;
                    case KeyKind.PlayerBind when value.Bind is { } bind:
                        await store.SaveBindAsync(bind).ConfigureAwait(false);
                        break;
                    case KeyKind.PlayerBind:
                        await store.DeleteBindAsync(key.A, (uint)key.B).ConfigureAwait(false);
                        break;
                    case KeyKind.LastInstance when value.Last is { } last:
                        await store.SaveLastInstanceAsync(last).ConfigureAwait(false);
                        break;
                    case KeyKind.GroupBind when value.GroupBind is { } groupBind:
                        await store.SaveGroupBindAsync(groupBind).ConfigureAwait(false);
                        break;
                    case KeyKind.GroupBind:
                        await store.DeleteGroupBindAsync(key.A, (uint)key.B).ConfigureAwait(false);
                        break;
                    case KeyKind.CharacterDelete:
                        await store.DeleteCharacterAsync(key.A).ConfigureAwait(false);
                        break;
                }

                progress.Succeeded();
                return null;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                FaultLedger.Shared.Record("writequeue.instance-ownership", ex);
                FailTerminal(ex, scope.ServiceProvider);
                return ex;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.instance-attempt", ex);
                logger.LogWarning(ex, "instance write for {Target} failed (attempt {Attempt}); retrying", DescribeKey(key), attempt);
                if (_stopped && _drain.Exhausted)
                {
                    return ex; // the lane has stalled: no retry, the caller retains the real failure
                }

                int delayMs = Math.Max(0, (int)RetryDelay.TotalMilliseconds) * attempt;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.instance-failure", ex);
                return ex;
            }
        }
    }

    private readonly record struct Key(KeyKind Kind, int A, int B);

    private readonly record struct Payload(
        InstanceRecord? Instance = null,
        string? Data = null,
        InstanceResetRecord? Reset = null,
        CharacterInstanceBindRecord? Bind = null,
        CharacterLastInstanceRecord? Last = null,
        GroupInstanceBindRecord? GroupBind = null);

    private sealed record Work(WorkKind Kind, Key Key, Payload Value, long Epoch, int CharacterId, TaskCompletionSource? Done);

    private sealed class Retained(Payload value, Exception failure)
    {
        public Payload Value { get; set; } = value;

        public Exception Failure { get; set; } = failure;
    }
}

