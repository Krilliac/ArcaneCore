using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

/// <summary>
/// Limits of <see cref="SocialWriteQueue"/> (configuration section <c>World:Social:WriteQueue</c>). The
/// defaults are far above anything a legitimate player reaches: a row is only pending while its
/// write has not reached the database, which normally takes milliseconds.
/// </summary>
public sealed class SocialWriteQueueOptions
{
    /// <summary>Most friend/ignore rows of one character that may be waiting for storage (queued or retained). 0 or less: unlimited.</summary>
    public int MaxPendingPerCharacter { get; set; } = 256;

    /// <summary>Most friend/ignore rows of all characters waiting for storage. 0 or less: unlimited.</summary>
    public int MaxPendingTotal { get; set; } = 25_000;

    /// <summary>Backoff unit between the three attempts of one write, in milliseconds (attempt n waits n times this).</summary>
    public int RetryDelayMs { get; set; } = 200;
}

/// <summary>
/// Persists social writes made on the world thread, off the world thread and in order (one
/// consumer, like <see cref="Persistence.CharacterSaveQueue"/>), so a guild's snapshots never
/// overtake each other, and keeps them until they are durable (the contract of
/// <see cref="Reputation.ReputationWriteQueue"/>).
/// <para>
/// Every write sets one absolute value: a friend/ignore row's flags, a guild's full snapshot (or
/// its deletion), a character purge. So pending writes are <b>coalesced per key</b>: a newer value
/// for a key that has a write waiting replaces that write's value, and the last value written is the
/// same as applying every write in order. A purge is a barrier: a write queued after it is never merged
/// into one queued before it. A normal player can therefore not grow the queue by toggling the same
/// friend or ignore entry, and the number of rows waiting is bounded per character and in total
/// (<see cref="SocialWriteQueueOptions"/>): <see cref="TrySetSocial"/> refuses a new row beyond the
/// limit and the caller disconnects the player.
/// </para>
/// <para>
/// A write that fails all <c>MaxAttempts</c> attempts is <b>retained</b>, never dropped. The next write
/// of that character (one attempt), <see cref="FlushCharacterAsync"/> (the login barrier, which
/// throws while the character is still unrecovered), <see cref="RequestRetry"/> (logout) and
/// <see cref="StopAsync"/> (a final retry that throws, naming what is left) retry it.
/// <see cref="FlushAsync"/> stays a pure ordered barrier: it never retries and never throws, so one
/// failing row cannot stall a character deletion. Retention is in process only: a crash, or a
/// store that is still down at exit, loses what is retained (loudly).
/// </para>
/// </summary>
public sealed class SocialWriteQueue : ISocialPersistence, IPetitionPersistence
{
    private const int MaxAttempts = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly SocialWriteQueueOptions _options;
    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();

    // Under _gate. The newest queued write per key (its value may still be replaced until it starts).
    private readonly Dictionary<Key, Work> _queued = [];
    private readonly Dictionary<Key, Retained> _retained = [];
    private readonly Dictionary<int, HashSet<Key>> _retainedByCharacter = [];
    private readonly Dictionary<int, int> _rowsPerCharacter = [];
    private readonly HashSet<int> _backlogged = []; // characters whose refusal at the limit was already reported (this episode)
    private readonly List<string> _droppedWrites = []; // writes refused because the channel was closed, reported once the gate is released
    private int _rowsTotal;
    private long _epoch;
    private Exception? _terminalFailure;

    private Task? _consumer;
    private Task? _stop;
    private RetainedRetryTimer? _retry;
    private volatile bool _stopped;
    private readonly StopDrainBudget _drain;
    private int _pending;

    public SocialWriteQueue(IServiceScopeFactory scopes, ILogger logger, SocialWriteQueueOptions? options = null, StopDrainBudget? drain = null)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options ?? new SocialWriteQueueOptions();
        _drain = drain ?? new StopDrainBudget();
    }

    private enum KeyKind
    {
        Row,
        Guild,
        Purge,
        Petition,
        PetitionPurge,
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

    /// <summary>Writes queued or in progress (one per key waiting, not one per request).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Friend/ignore rows waiting for storage, queued or retained.</summary>
    public int PendingRows
    {
        get
        {
            lock (_gate)
            {
                return _rowsTotal;
            }
        }
    }

    /// <summary>Ids of characters with a friend/ignore write that failed all attempts and is retained, ascending.</summary>
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

    /// <summary>How often retained writes are retried by a background sweep while any exist (one attempt per key, stopping at the first locked one; tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tests: queue one retained-write sweep now, exactly as a timer tick would; false when one is already pending or the queue is not started.</summary>
    internal bool PostRetainedSweepForTest() => _retry?.TryPost() ?? false;

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _retry = new RetainedRetryTimer(RetainedRetryInterval, HasAnyRetained, () => _channel.Writer.TryWrite(new Work(WorkKind.Sweep, default, default, 0, 0, null)));
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

    public void SetSocial(int characterId, int otherId, SocialFlags flags) => TrySetSocial(characterId, otherId, flags);

    /// <summary>
    /// Queue (or merge into the waiting write of the same row) the flags of one friend/ignore row.
    /// False when the write cannot be accepted: the queue is stopped, or the row is new and the
    /// character or the realm already has the configured number of rows waiting. Nothing is queued then.
    /// </summary>
    public bool TrySetSocial(int characterId, int otherId, SocialFlags flags)
    {
        var key = new Key(KeyKind.Row, characterId, otherId);
        bool accepted = true;
        bool reportStopped = false;
        bool reportBacklog = false;
        int rowsTotal = 0;
        lock (_gate)
        {
            if (_stopped)
            {
                _logger.LogError("social write queue stopped; friend write for character {Character} lost", characterId);
                reportStopped = true;
                accepted = false;
            }
            else
            {
                bool tracked = _queued.ContainsKey(key) || _retained.ContainsKey(key);
                if (!tracked && OverLimit(characterId))
                {
                    _logger.LogWarning("character {Character} has too many social writes waiting ({Rows} in total); refusing", characterId, _rowsTotal);
                    reportBacklog = _backlogged.Add(characterId); // one report per character until it is back under the limit
                    rowsTotal = _rowsTotal;
                    accepted = false;
                }
                else
                {
                    if (!tracked)
                    {
                        _backlogged.Remove(characterId);
                        _rowsPerCharacter[characterId] = _rowsPerCharacter.GetValueOrDefault(characterId) + 1;
                        _rowsTotal++;
                    }

                    if (!EnqueueWrite(key, new Payload(flags, null)))
                    {
                        ReleaseKey(key);
                        accepted = false;
                    }
                }
            }
        }

        if (reportStopped)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                $"character {characterId}",
                $"queue=SocialWriteQueue character={characterId} reason=queue-stopped",
                AnomalySeverity.Error);
        }

        if (reportBacklog)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "queue-backlog",
                "queue SocialWriteQueue",
                $"queue=SocialWriteQueue character={characterId} rowsTotal={rowsTotal}",
                AnomalySeverity.Warning);
        }

        ReportDroppedWrites();
        return accepted;
    }

    public void SaveGuild(GuildData guild)
    {
        ArgumentNullException.ThrowIfNull(guild);
        lock (_gate)
        {
            EnqueueGuildWrite(guild.Id, guild, null);
        }

        ReportDroppedWrites();
    }

    public void DeleteGuild(int guildId)
    {
        lock (_gate)
        {
            EnqueueGuildWrite(guildId, null, null);
        }

        ReportDroppedWrites();
    }

    public void SavePetition(PetitionData petition)
    {
        ArgumentNullException.ThrowIfNull(petition);
        lock (_gate)
        {
            EnqueueWrite(new Key(KeyKind.Petition, petition.Id, 0), new Payload(SocialFlags.None, null, petition));
        }

        ReportDroppedWrites();
    }

    public void DeletePetition(int petitionId)
    {
        lock (_gate)
        {
            EnqueueWrite(new Key(KeyKind.Petition, petitionId, 0), new Payload(SocialFlags.None, null));
        }

        ReportDroppedWrites();
    }

    /// <summary>
    /// The founded guild and the petition's deletion commit together. The completion is queued under the
    /// guild's own key (with the petition id in the payload), never under a key of its own: a later guild
    /// change or disband then merges into it instead of running before a stale snapshot, and a retained
    /// completion is superseded (its petition id carried over) by a newer guild write instead of being
    /// replayed over it.
    /// </summary>
    public void CompletePetition(GuildData guild, int petitionId)
    {
        ArgumentNullException.ThrowIfNull(guild);
        lock (_gate)
        {
            EnqueueGuildWrite(guild.Id, guild, petitionId);
        }

        ReportDroppedWrites();
    }

    /// <summary>
    /// Queue a deleted character's purge after every earlier write. Friend/ignore writes that
    /// failed earlier and are retained for the character (or about it) are discarded first: the purge
    /// removes those rows, so a retry must never bring one back.
    /// </summary>
    public void PurgeCharacter(int characterId)
    {
        lock (_gate)
        {
            _epoch++;
            foreach (Key key in _retained.Keys.Where(k => k.Kind == KeyKind.Row && (k.A == characterId || k.B == characterId)).ToArray())
            {
                RemoveRetained(key);
                ReleaseKey(key);
            }

            // A retained petition write of the deleted character must not bring the petition back (guild writes
            // are keyed by guild, not character, and stay: the next guild write corrects the member list).
            foreach (Key key in _retained.Where(r => r.Key.Kind == KeyKind.Petition && r.Value.Value.Petition?.OwnerId == characterId).Select(r => r.Key).ToArray())
            {
                RemoveRetained(key);
            }

            EnqueueWrite(new Key(KeyKind.Purge, characterId, 0), default);
            EnqueueWrite(new Key(KeyKind.PetitionPurge, characterId, 0), default); // the character's petition rows, after the social rows
        }

        ReportDroppedWrites();
    }

    /// <summary>True while a friend/ignore write of the character failed all attempts and is retained.</summary>
    public bool HasRetainedFailure(int characterId)
    {
        lock (_gate)
        {
            return _retainedByCharacter.ContainsKey(characterId);
        }
    }

    /// <summary>
    /// The stored rows of a character with its retained (not yet durable) rows applied, so a list
    /// loaded while storage is failing still shows the player's latest changes.
    /// </summary>
    public IReadOnlyList<SocialEntry> WithRetained(int characterId, IReadOnlyList<SocialEntry> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        lock (_gate)
        {
            if (!_retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys))
            {
                return stored;
            }

            Dictionary<int, SocialFlags> rows = stored.ToDictionary(e => e.OtherId, e => e.Flags);
            foreach (Key key in keys)
            {
                SocialFlags flags = _retained[key].Value.Flags;
                if (flags == SocialFlags.None)
                {
                    rows.Remove(key.B);
                }
                else
                {
                    rows[key.B] = flags;
                }
            }

            return [.. rows.OrderBy(r => r.Key).Select(r => new SocialEntry(r.Key, r.Value))];
        }
    }

    /// <summary>
    /// Completes once every write queued before the call has been attempted. A pure ordered
    /// barrier: it never retries retained failures and never throws (see <see cref="FlushCharacterAsync"/>).
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
    /// The login barrier. After every earlier write, retries this character's retained rows once
    /// more and faults with <see cref="InvalidOperationException"/> while they are still not durable.
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

    /// <summary>Queue a fire-and-forget retry of the character's retained rows (logout).</summary>
    public void RequestRetry(int characterId)
    {
        if (_consumer is not null && !_stopped)
        {
            _channel.Writer.TryWrite(new Work(WorkKind.Retry, default, default, 0, characterId, null));
        }
    }

    /// <summary>
    /// Stop accepting writes, drain the queue and retry everything retained once. Throws
    /// <see cref="InvalidOperationException"/> naming the characters and guilds whose writes are
    /// still not durable. Idempotent: later calls return the same outcome.
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
                .. failures.Select(f => f.Key.Kind switch
                {
                    KeyKind.Row => $"character {f.Key.A}",
                    KeyKind.Guild => $"guild {f.Key.A}",
                    KeyKind.Petition => $"petition {f.Key.A}",
                    KeyKind.PetitionPurge => $"petition purge of character {f.Key.A}",
                    _ => $"purge of character {f.Key.A}",
                }).Distinct(),
            ];
            first = failures.Length > 0 ? failures[0].Value.Failure : terminal;
        }

        if (left.Length > 0)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                "queue SocialWriteQueue",
                $"queue=SocialWriteQueue undrainedTargets={left.Length} firstTarget={left[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.social-stop-drain", refusal);
                _logger.LogFault(
                    "writequeue-social-stop-drain",
                    refusal,
                    "social write queue shutdown drain stalled; {Refused} of {Undrained} retained targets were not attempted: {Summary}",
                    _drain.Refused,
                    left.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"social writes did not drain for {string.Join(", ", left)}", first);
        }

        if (terminal is not null)
        {
            throw new InvalidOperationException("social write queue stopped after terminal ownership rejection", terminal);
        }
    }

    // --- gate-held helpers -------------------------------------------------------------------

    private bool OverLimit(int characterId)
        => (_options.MaxPendingPerCharacter > 0 && _rowsPerCharacter.GetValueOrDefault(characterId) >= _options.MaxPendingPerCharacter)
            || (_options.MaxPendingTotal > 0 && _rowsTotal >= _options.MaxPendingTotal);

    /// <summary>
    /// Queue a guild snapshot (null: deletion) with an optional petition to consume. Petitions already attached to
    /// the write this one merges into (or to the retained failure it will supersede) are carried over, so the
    /// completion of a petition is never lost when a newer guild write replaces its value.
    /// </summary>
    private void EnqueueGuildWrite(int guildId, GuildData? guild, int? petitionId)
    {
        var key = new Key(KeyKind.Guild, guildId, 0);
        int[]? carried = _queued.TryGetValue(key, out Work? waiting) && !waiting.Started && waiting.Epoch == _epoch
            ? waiting.Value.Petitions
            : _retained.TryGetValue(key, out Retained? retained) ? retained.Value.Petitions : null;
        int[]? petitions = petitionId is { } id ? [.. (carried ?? []).Append(id).Distinct()] : carried;
        EnqueueWrite(key, new Payload(SocialFlags.None, guild, Petitions: petitions));
    }

    /// <summary>Merge into the key's waiting write, or queue a new one. False (nothing queued) when the channel is closed.</summary>
    private bool EnqueueWrite(Key key, Payload value)
    {
        if (_queued.TryGetValue(key, out Work? waiting) && !waiting.Started && waiting.Epoch == _epoch)
        {
            waiting.Value = value;
            return true;
        }

        var work = new Work(WorkKind.Write, key, value, _epoch, key.A, null);
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref _pending);
            _logger.LogError("social write queue closed; write for {Kind} {Id} lost", key.Kind, key.A);
            _droppedWrites.Add($"kind={key.Kind} id={key.A} reason=queue-closed");
            return false;
        }

        _queued[key] = work;
        return true;
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
        if (key.Kind == KeyKind.Row)
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
        if (_retained.Remove(key) && key.Kind == KeyKind.Row && _retainedByCharacter.TryGetValue(key.A, out HashSet<Key>? keys))
        {
            keys.Remove(key);
            if (keys.Count == 0)
            {
                _retainedByCharacter.Remove(key.A);
            }
        }
    }

    /// <summary>Reports the writes <see cref="EnqueueWrite"/> refused because the channel was closed; called after the gate is released.</summary>
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
                "queue SocialWriteQueue",
                $"queue=SocialWriteQueue {what}",
                AnomalySeverity.Error);
        }
    }

    /// <summary>A row with nothing queued and nothing retained no longer counts against the limits.</summary>
    private void ReleaseKey(Key key)
    {
        if (key.Kind != KeyKind.Row || _queued.ContainsKey(key) || _retained.ContainsKey(key))
        {
            return;
        }

        int rows = _rowsPerCharacter.GetValueOrDefault(key.A) - 1;
        if (rows <= 0)
        {
            _rowsPerCharacter.Remove(key.A);
        }
        else
        {
            _rowsPerCharacter[key.A] = rows;
        }

        _rowsTotal--;
        if (_backlogged.Count > 0 && !OverLimit(key.A))
        {
            _backlogged.Remove(key.A); // back under the limit: the next refusal is a new episode
        }
    }

    private Exception? RetainedFailureOf(int characterId)
    {
        lock (_gate)
        {
            return _retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys) && keys.Count > 0
                ? _retained[keys.First()].Failure
                : _terminalFailure;
        }
    }

    private Task RetainedFailureTask(int characterId)
        => RetainedFailureOf(characterId) is { } failure ? Task.FromException(NotDurable(characterId, failure)) : Task.CompletedTask;

    private static InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"social writes for character {characterId} are not durable", failure);

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
            "queue SocialWriteQueue",
            $"queue=SocialWriteQueue reason=terminal-ownership-failure error={ex.GetType().Name}",
            AnomalySeverity.Warning);
        _logger.LogCritical(ex, "social write queue entered terminal failure after character ownership rejection");
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
            FaultLedger.Shared.Record("writequeue.social-stop-host", stopEx);
        }

        FatalFailure?.Invoke(ex);
    }

    // --- consumer ----------------------------------------------------------------------------

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
                // Store failures are handled where they happen; this is a queue bug and must not kill the consumer.
                FaultLedger.Shared.Record("persist.social-write-queue", ex);
                _logger.LogFault("writequeue.social-consume", ex, "social write queue item {Kind} failed unexpectedly", work.Kind);
                work.Done?.TrySetException(ex);
            }
        }
    }

    private async Task ExecuteWriteAsync(Work work)
    {
        Payload value;
        lock (_gate)
        {
            work.Started = true; // no later request merges into it; it stays in _queued (tracked) until it completes
            value = work.Value;
        }

        var made = new WriteAttemptCount();
        Exception? failure = await TryWriteAsync(work.Key, value, MaxAttempts, made).ConfigureAwait(false);
        bool newEpisode = false;
        lock (_gate)
        {
            if (_queued.TryGetValue(work.Key, out Work? newest) && ReferenceEquals(newest, work))
            {
                _queued.Remove(work.Key);
            }

            if (failure is null)
            {
                // This write is newer than anything retained for the key, so it supersedes it.
                RemoveRetained(work.Key);
            }
            else
            {
                if (work.Key.Kind == KeyKind.Guild && value.Petitions is { } pending
                    && _queued.TryGetValue(work.Key, out Work? later) && !ReferenceEquals(later, work))
                {
                    // A newer guild write is already queued and will supersede this retained one: keep the petitions.
                    later.Value = later.Value with { Petitions = [.. (later.Value.Petitions ?? []).Concat(pending).Distinct()] };
                }

                newEpisode = !_retained.ContainsKey(work.Key); // a retained write failing again is the same episode
                SetRetained(work.Key, value, failure);
                if (failure is not StopDrainExhaustedException)
                {
                    _logger.LogFault("writequeue.social", failure, "social write for {Kind} {Id} failed; retained for retry", work.Key.Kind, work.Key.A);
                }
            }

            ReleaseKey(work.Key);
        }

        if (failure is not null && newEpisode && failure is not CharacterOwnershipRejectedException and not StopDrainExhaustedException)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-retry-exhausted",
                "queue SocialWriteQueue",
                $"queue=SocialWriteQueue kind={work.Key.Kind} id={work.Key.A} attempts={made.Made} error={failure.GetType().Name}",
                AnomalySeverity.Error);
        }

        if (failure is null)
        {
            // Storage works: give this character's (or the guilds') older failures one more try, without backoff.
            if (work.Key.Kind == KeyKind.Row)
            {
                await RetryCharacterAsync(work.Key.A, 1).ConfigureAwait(false);
            }
            else
            {
                await RetryOpaqueAsync(work.Key, 1).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Retry the retained friend/ignore rows of one character (consumer thread).</summary>
    private async Task RetryCharacterAsync(int characterId, int attempts)
    {
        KeyValuePair<Key, Retained>[] rows;
        lock (_gate)
        {
            if (!_retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys))
            {
                return;
            }

            rows = [.. keys.Select(k => KeyValuePair.Create(k, _retained[k]))];
        }

        foreach ((Key key, Retained retained) in rows)
        {
            await RetryOneAsync(key, retained, attempts).ConfigureAwait(false);
        }
    }

    /// <summary>Retry retained guild writes and purges, except <paramref name="except"/> (consumer thread).</summary>
    private async Task RetryOpaqueAsync(Key? except, int attempts)
    {
        KeyValuePair<Key, Retained>[] rows;
        lock (_gate)
        {
            rows = [.. _retained.Where(r => r.Key.Kind != KeyKind.Row && r.Key != except).OrderBy(r => r.Key.A)];
        }

        foreach ((Key key, Retained retained) in rows)
        {
            await RetryOneAsync(key, retained, attempts).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// On the consumer: one attempt for each retained write (friend/ignore rows by character, then guilds, petitions and purges), so a lock that outlasts the
    /// inline retries does not leave a write held in memory with nobody to retry it. Stops at the first locked database; any other failure does not stop it.
    /// </summary>
    private async Task SweepRetainedAsync()
    {
        KeyValuePair<Key, Retained>[] rows;
        lock (_gate)
        {
            rows =
            [
                .. _retained.OrderBy(r => r.Key.Kind == KeyKind.Row ? 0 : 1).ThenBy(r => r.Key.A).ThenBy(r => r.Key.B).ThenBy(r => r.Key.Kind),
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
            if (DatabaseTransience.IsBusy(failure))
            {
                locked = true;
                break;
            }
        }

        if (failed > 0 && Invariant.Check(first is not null, "social retry recorded failures without a first exception") && first is { } firstFailure)
        {
            _logger.LogWarning("social retry: {Failed} of {Total} retained writes still failing{Locked}; first: {Reason}", failed, rows.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", firstFailure.Message);
        }
    }

    private async Task<Exception?> RetryOneAsync(Key key, Retained retained, int attempts)
    {
        Payload value;
        lock (_gate)
        {
            if (_terminalFailure is not null || !_retained.TryGetValue(key, out Retained? current) || !ReferenceEquals(current, retained))
            {
                return _terminalFailure; // superseded, discarded, or terminal failure meanwhile
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
                ReleaseKey(key);
            }
            else
            {
                retained.Failure = failure;
            }
        }

        return failure;
    }

    /// <summary>Apply one key's value to the store. Null on success; otherwise the last failure.</summary>
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

            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            try
            {
                using StopDrainBudget.Attempt progress = _drain.BeginAttempt(); // the attempt is the store call: its verdict is in before the catch's backoff
                if (key.Kind is KeyKind.Petition or KeyKind.PetitionPurge)
                {
                    if (scope.ServiceProvider.GetService<IPetitionStore>() is not { } petitions)
                    {
                        progress.Succeeded();
                        return null; // hosts without persistence keep nothing
                    }

                    switch (key.Kind)
                    {
                        case KeyKind.Petition when value.Petition is { } petition:
                            await petitions.SavePetitionAsync(petition).ConfigureAwait(false);
                            break;
                        case KeyKind.Petition:
                            await petitions.DeletePetitionAsync(key.A).ConfigureAwait(false);
                            break;
                        default:
                            await petitions.PurgeCharacterAsync(key.A).ConfigureAwait(false);
                            break;
                    }

                    progress.Succeeded();
                    return null;
                }

                if (scope.ServiceProvider.GetService<ISocialStore>() is not { } store)
                {
                    progress.Succeeded();
                    return null; // hosts without persistence keep nothing
                }

                // Every call sets an absolute state, so a retry after a commit whose acknowledgement was lost is safe.
                switch (key.Kind)
                {
                    case KeyKind.Row:
                        await store.SetSocialAsync(key.A, key.B, value.Flags).ConfigureAwait(false);
                        break;
                    case KeyKind.Guild:
                        await WriteGuildAsync(scope.ServiceProvider, store, key.A, value).ConfigureAwait(false);
                        break;
                    default:
                        await store.PurgeCharacterAsync(key.A).ConfigureAwait(false);
                        break;
                }

                progress.Succeeded();
                return null;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                FaultLedger.Shared.Record("writequeue.social-ownership", ex);
                FailTerminal(ex, scope.ServiceProvider);
                return ex;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.social-attempt", ex);
                _logger.LogWarning(ex, "social write for {Kind} {Id} failed (attempt {Attempt}); retrying", key.Kind, key.A, attempt);
                if (_stopped && _drain.Exhausted)
                {
                    return ex; // the lane has stalled: no retry, the caller retains the real failure
                }

                await Task.Delay(Math.Max(0, _options.RetryDelayMs) * attempt).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.social-failure", ex);
                return ex;
            }
        }
    }

    /// <summary>
    /// A guild snapshot (or its deletion) and the petitions it consumes. A founded guild and its first petition
    /// commit in one transaction; any further petition (a newer write merged in) is deleted after. A deletion
    /// still deletes the petitions, never recreating the guild. All steps set absolute state, so a retry is safe.
    /// </summary>
    private static async Task WriteGuildAsync(IServiceProvider services, ISocialStore store, int guildId, Payload value)
    {
        int[] petitionIds = value.Petitions ?? [];
        IPetitionStore? petitions = petitionIds.Length > 0 ? services.GetService<IPetitionStore>() : null;
        if (value.Guild is { } guild)
        {
            if (petitions is null)
            {
                await store.SaveGuildAsync(guild).ConfigureAwait(false);
                return;
            }

            await petitions.CompletePetitionAsync(guild, petitionIds[0]).ConfigureAwait(false);
            petitionIds = petitionIds[1..];
        }
        else
        {
            await store.DeleteGuildAsync(guildId).ConfigureAwait(false);
        }

        foreach (int petitionId in petitionIds)
        {
            if (petitions is not null)
            {
                await petitions.DeletePetitionAsync(petitionId).ConfigureAwait(false);
            }
        }
    }

    private readonly record struct Key(KeyKind Kind, int A, int B);

    /// <summary>
    /// The absolute value a key is set to: row flags, a guild snapshot (null: delete the guild) with the petitions
    /// consumed by its founding, or a petition snapshot (null: delete the petition).
    /// </summary>
    private readonly record struct Payload(SocialFlags Flags, GuildData? Guild, PetitionData? Petition = null, int[]? Petitions = null);

    private sealed class Work(WorkKind kind, Key key, Payload value, long epoch, int characterId, TaskCompletionSource? done)
    {
        public WorkKind Kind { get; } = kind;

        public Key Key { get; } = key;

        public long Epoch { get; } = epoch;

        public int CharacterId { get; } = characterId;

        public TaskCompletionSource? Done { get; } = done;

        public Payload Value { get; set; } = value;

        public bool Started { get; set; }
    }

    private sealed class Retained(Payload value, Exception failure)
    {
        public Payload Value { get; set; } = value;

        public Exception Failure { get; set; } = failure;
    }
}
