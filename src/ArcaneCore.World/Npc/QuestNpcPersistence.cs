using System.Threading.Channels;
using ArcaneCore.Kernel.Quests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Ordered quest/taxi saves, outside the world thread. A failed write keeps the authoritative
/// character snapshot: the next login barrier retries it before storage may be loaded again.
/// Each operation owns a fresh scope, so no session DbContext crosses threads.
/// </summary>
public sealed class QuestNpcPersistence(IServiceScopeFactory scopes, ILogger logger) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, CharacterState> _characters = [];
    private readonly Channel<PendingWrite> _writes = Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private Task? _worker;
    private bool _stopped;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _worker ??= Task.Run(WriteLoopAsync);
        }
    }

    /// <summary>
    /// Capture after the character barrier and before starting an asynchronous storage load.
    /// Later cache loads or mutations invalidate this token even when their saves finish first.
    /// </summary>
    public long CaptureLoadRevision(int characterId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_characters.TryGetValue(characterId, out CharacterState? state))
            {
                return 0;
            }

            if (state.Pending != 0 || state.Failure is not null)
            {
                throw new InvalidOperationException($"quest saves for character {characterId} have not reached storage");
            }

            return state.Revision;
        }
    }

    /// <summary>
    /// Replace the offline cache only if it has not changed since the storage read began.
    /// Pending/failed saves and completed newer writes all refuse stale login snapshots.
    /// </summary>
    public void LoadCharacter(int characterId, CharacterQuestData data, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _characters.TryGetValue(characterId, out CharacterState? previous);
            long revision = previous?.Revision ?? 0;
            if (revision != expectedRevision)
            {
                throw new InvalidOperationException($"quest journal for character {characterId} changed while storage was loading");
            }

            if (previous is not null && (previous.Pending != 0 || previous.Failure is not null))
            {
                throw new InvalidOperationException($"quest saves for character {characterId} have not reached storage");
            }

            _characters[characterId] = new CharacterState(data, checked(revision + 1));
        }
    }

    /// <summary>Copy a world-thread delta before returning; callers may reuse their lists.</summary>
    public void SaveQuests(int characterId, IReadOnlyList<CharacterQuestStatus> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        CharacterQuestStatus[] copied = rows.Select(r => r with { CharacterId = characterId }).ToArray();
        lock (_gate)
        {
            CharacterState state = WritableCharacter(characterId);
            state.Revision = checked(state.Revision + 1);
            foreach (CharacterQuestStatus row in copied)
            {
                state.Quests[row.Quest] = row;
            }

            state.Pending++;
            Enqueue(new PendingWrite(characterId, copied, null, null, null));
        }
    }

    public void SaveTaxiMask(int characterId, IReadOnlyList<uint> mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        uint[] copied = mask.ToArray();
        lock (_gate)
        {
            CharacterState state = WritableCharacter(characterId);
            state.Revision = checked(state.Revision + 1);
            state.TaxiMask = copied;
            state.Pending++;
            Enqueue(new PendingWrite(characterId, null, copied, null, null));
        }
    }

    /// <summary>
    /// Wait for this character's earlier writes. If one failed, retry the complete snapshot
    /// captured at this barrier; an unsuccessful recovery refuses login and keeps that data.
    /// </summary>
    public Task FlushCharacterAsync(int characterId)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _worker ??= Task.Run(WriteLoopAsync);
            CharacterQuestData? snapshot = _characters.TryGetValue(characterId, out CharacterState? state)
                ? state.Snapshot()
                : null;
            Enqueue(new PendingWrite(characterId, null, null, snapshot, done));
        }

        return done.Task;
    }

    /// <summary>Finish queued operations and retry outstanding failures before releasing storage.</summary>
    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            if (!_stopped)
            {
                // Shutdown snapshots follow every queued delta, including writes not yet attempted.
                foreach ((int characterId, CharacterState state) in _characters)
                {
                    Enqueue(new PendingWrite(characterId, null, null, state.Snapshot(), null));
                }

                _stopped = true;
                _writes.Writer.TryComplete();
            }

            worker = _worker ??= Task.Run(WriteLoopAsync);
        }

        await worker.ConfigureAwait(false);
        lock (_gate)
        {
            int[] failed = _characters.Where(c => c.Value.Failure is not null).Select(c => c.Key).ToArray();
            if (failed.Length > 0)
            {
                throw new InvalidOperationException($"quest saves did not drain for characters {string.Join(", ", failed)}",
                    _characters[failed[0]].Failure);
            }
        }
    }

    private CharacterState WritableCharacter(int characterId)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        _worker ??= Task.Run(WriteLoopAsync);
        if (!_characters.TryGetValue(characterId, out CharacterState? state))
        {
            _characters[characterId] = state = new CharacterState(CharacterQuestData.Empty, 0);
        }

        return state;
    }

    private void Enqueue(PendingWrite pending)
    {
        if (!_writes.Writer.TryWrite(pending))
        {
            throw new InvalidOperationException("the quest save queue is stopped");
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (PendingWrite pending in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (pending.Rows is not null || pending.TaxiMask is not null)
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    ICharacterQuestStore store = GetStore(scope.ServiceProvider);
                    if (pending.Rows is { } rows)
                    {
                        await store.SaveQuestsAsync(pending.CharacterId, rows).ConfigureAwait(false);
                    }
                    else
                    {
                        await store.SaveTaxiMaskAsync(pending.CharacterId, pending.TaxiMask!).ConfigureAwait(false);
                    }
                }
                else
                {
                    await RecoverAsync(pending.CharacterId, pending.Snapshot).ConfigureAwait(false);
                }

                pending.Done?.TrySetResult();
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_characters.TryGetValue(pending.CharacterId, out CharacterState? state))
                    {
                        state.Failure = ex;
                    }
                }

                pending.Done?.TrySetException(ex);
                logger.LogError(ex, "quest/taxi save failed for character {Character}; authoritative snapshot retained", pending.CharacterId);
            }
            finally
            {
                if (pending.Rows is not null || pending.TaxiMask is not null)
                {
                    lock (_gate)
                    {
                        _characters[pending.CharacterId].Pending--;
                    }
                }
            }
        }
    }

    private async Task RecoverAsync(int characterId, CharacterQuestData? snapshot)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out CharacterState? state) || state.Failure is null)
            {
                return;
            }
        }

        if (snapshot is null)
        {
            throw new InvalidOperationException($"authoritative quest snapshot missing for character {characterId}");
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ICharacterQuestStore store = GetStore(scope.ServiceProvider);
        if (snapshot.Quests.Count > 0)
        {
            await store.SaveQuestsAsync(characterId, snapshot.Quests).ConfigureAwait(false);
        }

        await store.SaveTaxiMaskAsync(characterId, snapshot.TaxiMask).ConfigureAwait(false);
        lock (_gate)
        {
            _characters[characterId].Failure = null;
        }
    }

    private static ICharacterQuestStore GetStore(IServiceProvider services) => services.GetService<ICharacterQuestStore>()
        ?? throw new InvalidOperationException("no character quest store is registered for a queued save");

    private sealed class CharacterState(CharacterQuestData data, long revision)
    {
        public Dictionary<uint, CharacterQuestStatus> Quests { get; } = data.Quests.ToDictionary(q => q.Quest);
        public uint[] TaxiMask { get; set; } = data.TaxiMask.ToArray();
        public long Revision { get; set; } = revision;
        public int Pending { get; set; }
        public Exception? Failure { get; set; }
        public CharacterQuestData Snapshot() => new(Quests.Values.OrderBy(q => q.Quest).ToArray(), TaxiMask.ToArray());
    }

    private sealed record PendingWrite(int CharacterId, CharacterQuestStatus[]? Rows, uint[]? TaxiMask,
        CharacterQuestData? Snapshot, TaskCompletionSource? Done);
}
