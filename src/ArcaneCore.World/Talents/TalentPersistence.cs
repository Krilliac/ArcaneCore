using System.Threading.Channels;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Game.Talents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Talents;

/// <summary>
/// Writes the talent state (respec economy and disabled spells) behind the world thread: changes are queued and
/// written in order by one background task, so the world thread never waits for the database. The in-memory
/// "desired" state of every character that changed is authoritative: a failed write marks the character, and the next
/// flush for it (login, shutdown) reconciles the store against the desired state instead of dropping the change
/// (retain-on-failure, like <c>SpellbookCache</c>). Thread-safe.
/// <para>
/// The learned talent spells are ordinary spellbook rows owned by that cache's own queue; the two queues are independent,
/// so a respec is not atomic across them. See docs/areas/talents.md for the crash windows.
/// </para>
/// </summary>
public sealed class TalentPersistence : IAsyncDisposable
{
    private readonly Dictionary<int, Desired> _desired = [];
    private readonly HashSet<int> _failed = [];
    private readonly Lock _lock = new();
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger _logger;
    private readonly Channel<PendingWrite> _writes =
        Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions { SingleReader = true });

    private Task? _writer;
    private bool _warnedNoStore;

    /// <param name="scopes">Where the scoped <see cref="ICharacterTalentStore"/> comes from; null keeps the state in memory only.</param>
    public TalentPersistence(IServiceScopeFactory? scopes, ILogger logger)
    {
        _scopes = scopes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Writes queued and not yet finished.</summary>
    public int PendingWrites => _writes.Reader.Count;

    /// <summary>Characters whose last write failed and still await a reconciling flush.</summary>
    public IReadOnlyCollection<int> FailedCharacters
    {
        get
        {
            lock (_lock)
            {
                return [.. _failed];
            }
        }
    }

    /// <summary>Start the background writer.</summary>
    public void Start() => _writer ??= Task.Run(WriteLoopAsync);

    /// <summary>
    /// Login: wait for this character's earlier writes (a quick relog must not read before they land), then read its stored
    /// state and make it the desired state.
    /// </summary>
    public async Task<(CharacterTalentState? Respec, IReadOnlyList<uint> Disabled)> LoadCharacterAsync(int characterId, ICharacterTalentStore? store)
    {
        await FlushCharacterAsync(characterId).ConfigureAwait(false);
        if (store is null)
        {
            lock (_lock)
            {
                return _desired.TryGetValue(characterId, out Desired? kept) ? (kept.Respec, [.. kept.Disabled.Order()]) : (null, []);
            }
        }

        CharacterTalentState? respec = await store.GetAsync(characterId).ConfigureAwait(false);
        IReadOnlyList<uint> disabled = await store.GetDisabledAsync(characterId).ConfigureAwait(false);
        lock (_lock)
        {
            _desired[characterId] = new Desired { Respec = respec, Disabled = [.. disabled] };
        }

        return (respec, disabled);
    }

    /// <summary>The respec economy changed (queued).</summary>
    public void SaveRespec(int characterId, RespecState state)
    {
        var stored = new CharacterTalentState(state.Multiplier, state.TimeUnix);
        lock (_lock)
        {
            Of(characterId).Respec = stored;
        }

        Enqueue(characterId, store => store.SaveAsync(characterId, stored));
    }

    /// <summary>A spell was disabled or re-enabled (queued).</summary>
    public void SetDisabled(int characterId, uint spellId, bool disabled)
    {
        lock (_lock)
        {
            Desired desired = Of(characterId);
            if (disabled ? !desired.Disabled.Add(spellId) : !desired.Disabled.Remove(spellId))
            {
                return;
            }
        }

        uint[] spells = [spellId];
        Enqueue(characterId, disabled ? store => store.AddDisabledAsync(characterId, spells) : store => store.RemoveDisabledAsync(characterId, spells));
    }

    /// <summary>Drop a deleted character's state (cache and, conditionally, the rows).</summary>
    public void DeleteCharacter(int characterId)
    {
        lock (_lock)
        {
            _desired.Remove(characterId);
        }

        Enqueue(characterId, store => store.DeleteCharacterAsync(characterId));
    }

    /// <summary>Wait until every queued write has been attempted (tests, shutdown).</summary>
    public async Task FlushAsync()
    {
        if (_scopes is null || _writer is null)
        {
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new PendingWrite(null, static _ => Task.CompletedTask, done)))
        {
            return;
        }

        await done.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for earlier writes, then reconcile this character's store rows with its desired state if a write of its failed.
    /// A failed reconciliation propagates; the desired state stays authoritative for the next attempt.
    /// </summary>
    public async Task FlushCharacterAsync(int characterId)
    {
        if (_scopes is null || _writer is null)
        {
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new PendingWrite(characterId, store => RetryFailedAsync(store, characterId), done)))
        {
            throw new InvalidOperationException("The talent state writer has stopped");
        }

        await done.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        if (_writer is not null)
        {
            await _writer.ConfigureAwait(false);
        }
    }

    private Desired Of(int characterId)
    {
        if (!_desired.TryGetValue(characterId, out Desired? desired))
        {
            _desired[characterId] = desired = new Desired();
        }

        return desired;
    }

    private void Enqueue(int characterId, Func<ICharacterTalentStore, Task> write)
    {
        if (_scopes is null)
        {
            return;
        }

        _writes.Writer.TryWrite(new PendingWrite(characterId, write));
    }

    private async Task RetryFailedAsync(ICharacterTalentStore store, int characterId)
    {
        CharacterTalentState? respec;
        HashSet<uint>? disabled;
        lock (_lock)
        {
            if (!_failed.Contains(characterId))
            {
                return;
            }

            if (_desired.TryGetValue(characterId, out Desired? desired))
            {
                respec = desired.Respec;
                disabled = [.. desired.Disabled];
            }
            else
            {
                respec = null;
                disabled = null;
            }
        }

        if (disabled is null)
        {
            await store.DeleteCharacterAsync(characterId).ConfigureAwait(false);
        }
        else
        {
            if (respec is not null && await store.GetAsync(characterId).ConfigureAwait(false) != respec)
            {
                await store.SaveAsync(characterId, respec).ConfigureAwait(false);
            }

            IReadOnlyList<uint> persisted = await store.GetDisabledAsync(characterId).ConfigureAwait(false);
            await store.AddDisabledAsync(characterId, [.. disabled.Except(persisted)]).ConfigureAwait(false);
            await store.RemoveDisabledAsync(characterId, [.. persisted.Except(disabled)]).ConfigureAwait(false);
        }

        lock (_lock)
        {
            bool unchanged = disabled is null
                ? !_desired.ContainsKey(characterId)
                : _desired.TryGetValue(characterId, out Desired? current) && current.Respec == respec && current.Disabled.SetEquals(disabled);
            if (!unchanged)
            {
                throw new InvalidOperationException("Talent state changed while its failed writes were being recovered");
            }

            _failed.Remove(characterId);
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (PendingWrite pending in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
                ICharacterTalentStore? store = scope.ServiceProvider.GetService<ICharacterTalentStore>();
                if (store is null)
                {
                    if (!_warnedNoStore)
                    {
                        _warnedNoStore = true;
                        _logger.LogWarning("No ICharacterTalentStore is registered; talent state changes are not persisted");
                    }

                    pending.Done?.TrySetResult();
                    continue;
                }

                await pending.Write(store).ConfigureAwait(false);
                pending.Done?.TrySetResult();
            }
            catch (Exception ex)
            {
                // Fail loudly but keep the queue alive; the desired state stays authoritative until a reconciling flush.
                if (pending.CharacterId is { } characterId)
                {
                    lock (_lock)
                    {
                        _failed.Add(characterId);
                    }
                }

                pending.Done?.TrySetException(ex);
                _logger.LogError(ex, "Saving talent state failed");
            }
        }
    }

    private sealed class Desired
    {
        public CharacterTalentState? Respec { get; set; }

        public HashSet<uint> Disabled { get; init; } = [];
    }

    private readonly record struct PendingWrite(int? CharacterId, Func<ICharacterTalentStore, Task> Write, TaskCompletionSource? Done = null);
}
