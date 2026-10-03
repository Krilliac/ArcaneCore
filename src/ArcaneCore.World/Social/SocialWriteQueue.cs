using System.Threading.Channels;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class SocialWriteQueue : ISocialPersistence
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
    private int _rowsTotal;
    private long _epoch;

    private Task? _consumer;
    private Task? _stop;
    private volatile bool _stopped;
    private int _pending;

    public SocialWriteQueue(IServiceScopeFactory scopes, ILogger logger, SocialWriteQueueOptions? options = null)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options ?? new SocialWriteQueueOptions();
    }

    private enum KeyKind
    {
        Row,
        Guild,
        Purge,
    }

    private enum WorkKind
    {
        Write,
        Barrier,
        FlushCharacter,
        Retry,
        DrainAll,
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

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public void SetSocial(int characterId, int otherId, SocialFlags flags) => TrySetSocial(characterId, otherId, flags);

    /// <summary>
    /// Queue (or merge into the waiting write of the same row) the flags of one friend/ignore row.
    /// False when the write cannot be accepted: the queue is stopped, or the row is new and the
    /// character or the realm already has the configured number of rows waiting. Nothing is queued then.
    /// </summary>
    public bool TrySetSocial(int characterId, int otherId, SocialFlags flags)
    {
        var key = new Key(KeyKind.Row, characterId, otherId);
        lock (_gate)
        {
            if (_stopped)
            {
                _logger.LogError("social write queue stopped; friend write for character {Character} lost", characterId);
                return false;
            }

            bool tracked = _queued.ContainsKey(key) || _retained.ContainsKey(key);
            if (!tracked)
            {
                if (OverLimit(characterId))
                {
                    _logger.LogWarning("character {Character} has too many social writes waiting ({Rows} in total); refusing", characterId, _rowsTotal);
                    return false;
                }

                _rowsPerCharacter[characterId] = _rowsPerCharacter.GetValueOrDefault(characterId) + 1;
                _rowsTotal++;
            }

            if (!EnqueueWrite(key, new Payload(flags, null)))
            {
                ReleaseKey(key);
                return false;
            }

            return true;
        }
    }

    public void SaveGuild(GuildData guild)
    {
        ArgumentNullException.ThrowIfNull(guild);
        lock (_gate)
        {
            EnqueueWrite(new Key(KeyKind.Guild, guild.Id, 0), new Payload(SocialFlags.None, guild));
        }
    }

    public void DeleteGuild(int guildId)
    {
        lock (_gate)
        {
            EnqueueWrite(new Key(KeyKind.Guild, guildId, 0), new Payload(SocialFlags.None, null));
        }
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

            EnqueueWrite(new Key(KeyKind.Purge, characterId, 0), default);
        }
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
        if (_consumer is not null)
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

        _channel.Writer.TryWrite(new Work(WorkKind.DrainAll, default, default, 0, 0, null));
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        string[] left;
        Exception? first;
        lock (_gate)
        {
            KeyValuePair<Key, Retained>[] failures = [.. _retained.OrderBy(r => r.Key.Kind).ThenBy(r => r.Key.A).ThenBy(r => r.Key.B)];
            left =
            [
                .. failures.Select(f => f.Key.Kind switch
                {
                    KeyKind.Row => $"character {f.Key.A}",
                    KeyKind.Guild => $"guild {f.Key.A}",
                    _ => $"purge of character {f.Key.A}",
                }).Distinct(),
            ];
            first = failures.Length > 0 ? failures[0].Value.Failure : null;
        }

        if (left.Length > 0)
        {
            throw new InvalidOperationException($"social writes did not drain for {string.Join(", ", left)}", first);
        }
    }

    // --- gate-held helpers -------------------------------------------------------------------

    private bool OverLimit(int characterId)
        => (_options.MaxPendingPerCharacter > 0 && _rowsPerCharacter.GetValueOrDefault(characterId) >= _options.MaxPendingPerCharacter)
            || (_options.MaxPendingTotal > 0 && _rowsTotal >= _options.MaxPendingTotal);

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
    }

    private Exception? RetainedFailureOf(int characterId)
    {
        lock (_gate)
        {
            return _retainedByCharacter.TryGetValue(characterId, out HashSet<Key>? keys) && keys.Count > 0
                ? _retained[keys.First()].Failure
                : null;
        }
    }

    private Task RetainedFailureTask(int characterId)
        => RetainedFailureOf(characterId) is { } failure ? Task.FromException(NotDurable(characterId, failure)) : Task.CompletedTask;

    private static InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"social writes for character {characterId} are not durable", failure);

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
                    default:
                        work.Done?.TrySetResult();
                        break;
                }
            }
            catch (Exception ex)
            {
                // Store failures are handled where they happen; this is a queue bug and must not kill the consumer.
                _logger.LogError(ex, "social write queue item {Kind} failed unexpectedly", work.Kind);
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

        Exception? failure = await TryWriteAsync(work.Key, value, MaxAttempts).ConfigureAwait(false);
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
                SetRetained(work.Key, value, failure);
                _logger.LogError(failure, "social write for {Kind} {Id} failed; retained for retry", work.Key.Kind, work.Key.A);
            }

            ReleaseKey(work.Key);
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

    private async Task RetryOneAsync(Key key, Retained retained, int attempts)
    {
        Payload value;
        lock (_gate)
        {
            if (!_retained.TryGetValue(key, out Retained? current) || !ReferenceEquals(current, retained))
            {
                return; // superseded or discarded meanwhile
            }

            value = retained.Value;
        }

        Exception? failure = await TryWriteAsync(key, value, attempts).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_retained.TryGetValue(key, out Retained? current) || !ReferenceEquals(current, retained))
            {
                return;
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
    }

    /// <summary>Apply one key's value to the store. Null on success; otherwise the last failure.</summary>
    private async Task<Exception?> TryWriteAsync(Key key, Payload value, int attempts)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ISocialStore>() is not { } store)
                {
                    return null; // hosts without persistence keep nothing
                }

                // Every call sets an absolute state, so a retry after a commit whose acknowledgement was lost is safe.
                switch (key.Kind)
                {
                    case KeyKind.Row:
                        await store.SetSocialAsync(key.A, key.B, value.Flags).ConfigureAwait(false);
                        break;
                    case KeyKind.Guild when value.Guild is { } guild:
                        await store.SaveGuildAsync(guild).ConfigureAwait(false);
                        break;
                    case KeyKind.Guild:
                        await store.DeleteGuildAsync(key.A).ConfigureAwait(false);
                        break;
                    default:
                        await store.PurgeCharacterAsync(key.A).ConfigureAwait(false);
                        break;
                }

                return null;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                _logger.LogWarning(ex, "social write for {Kind} {Id} failed (attempt {Attempt}); retrying", key.Kind, key.A, attempt);
                await Task.Delay(Math.Max(0, _options.RetryDelayMs) * attempt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }

    private readonly record struct Key(KeyKind Kind, int A, int B);

    /// <summary>The absolute value a key is set to: row flags, or a guild snapshot (null: delete the guild).</summary>
    private readonly record struct Payload(SocialFlags Flags, GuildData? Guild);

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
