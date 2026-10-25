using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Saves and loads the cooldowns and auras of characters across logout (vmangos
/// Player::_SaveAuras / _SaveSpellCooldowns / _LoadAuras / _LoadSpellCooldowns, re-implemented).
/// Saves run off the world thread, one chain per character so a quick relog waits for its own
/// logout save; a failed save keeps the snapshot in memory, so the next login of that character
/// in this process still restores it and a later logout retries the write.
/// </summary>
public sealed class SpellStatePersistence
{
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<int, Task> _chains = [];
    private readonly Dictionary<int, SpellStateSnapshot> _unsaved = [];
    private readonly Dictionary<int, Exception> _lastFailure = [];
    private Exception? _terminalFailure;

    public SpellStatePersistence(IServiceScopeFactory? scopes, ILogger logger)
    {
        _scopes = scopes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Terminal failure that halted persistence (such as <see cref="CharacterOwnershipRejectedException"/>), if any.</summary>
    public Exception? TerminalFailure
    {
        get
        {
            lock (_lock)
            {
                return _terminalFailure;
            }
        }
    }

    /// <summary>Raised once when persistence enters terminal failure (for example on <see cref="CharacterOwnershipRejectedException"/>).</summary>
    public event Action<Exception>? FatalFailure;

    /// <summary>Queue a save of <paramref name="snapshot"/> for one character (world thread; never blocks).</summary>
    public void Save(int characterId, SpellStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_scopes is null)
        {
            return;
        }

        lock (_lock)
        {
            _unsaved[characterId] = snapshot;
            Task previous = _chains.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _chains[characterId] = previous.ContinueWith(_ => WriteAsync(characterId, snapshot),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// The saved state of one character for login (session task): waits for that character's
    /// queued saves, then prefers an unsaved in-memory snapshot over storage.
    /// </summary>
    public async Task<SpellStateSnapshot> LoadAsync(int characterId, ICharacterSpellStateStore? store)
    {
        await FlushCharacterAsync(characterId).ConfigureAwait(false);
        lock (_lock)
        {
            if (_terminalFailure is { } terminal)
            {
                throw new InvalidOperationException($"spell state writes for character {characterId} are not durable", terminal);
            }

            if (_unsaved.TryGetValue(characterId, out SpellStateSnapshot? pending))
            {
                return pending;
            }
        }

        return store is null ? SpellStateSnapshot.Empty : FromRows(await store.LoadAsync(characterId).ConfigureAwait(false));
    }

    /// <summary>Wait for the queued saves of one character (failures were logged and kept in memory).</summary>
    public async Task FlushCharacterAsync(int characterId)
    {
        Task? chain;
        lock (_lock)
        {
            chain = _chains.GetValueOrDefault(characterId);
        }

        if (chain is not null)
        {
            await chain.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wait for the queued saves of one character, retry an unsaved snapshot once if needed (unless terminal),
    /// and throw <see cref="InvalidOperationException"/> if the character's spell state is still not durable.
    /// </summary>
    public async Task FlushCharacterDurableAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await FlushCharacterAsync(characterId).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_scopes is null)
        {
            return;
        }

        Task? retryChain = null;
        lock (_lock)
        {
            if (_terminalFailure is null && _unsaved.TryGetValue(characterId, out SpellStateSnapshot? pending))
            {
                Task previous = _chains.GetValueOrDefault(characterId) ?? Task.CompletedTask;
                retryChain = _chains[characterId] = previous.ContinueWith(_ => WriteAsync(characterId, pending),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        if (retryChain is not null)
        {
            await retryChain.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Exception? failure = null;
        lock (_lock)
        {
            if (_unsaved.ContainsKey(characterId))
            {
                failure = _lastFailure.GetValueOrDefault(characterId) ?? _terminalFailure ?? new InvalidOperationException("spell state snapshot remained unsaved");
            }
            else if (_terminalFailure is not null)
            {
                failure = _terminalFailure;
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"spell state writes for character {characterId} are not durable", failure);
        }
    }

    /// <summary>Wait for every queued save (shutdown, tests).</summary>
    public async Task FlushAsync()
    {
        Task[] chains;
        lock (_lock)
        {
            chains = [.. _chains.Values];
        }

        await Task.WhenAll(chains).ConfigureAwait(false);
    }

    /// <summary>Characters whose snapshot has not reached storage yet.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _unsaved.Count;
            }
        }
    }

    /// <summary>Ids of characters whose unsaved snapshot has a recorded write failure, ascending.</summary>
    public IReadOnlyList<int> RetainedCharacters
    {
        get
        {
            lock (_lock)
            {
                return [.. _lastFailure.Keys.Order()];
            }
        }
    }

    /// <summary>Whether a character has a snapshot that has not reached storage yet.</summary>
    public bool HasUnsaved(int characterId)
    {
        lock (_lock)
        {
            return _unsaved.ContainsKey(characterId);
        }
    }

    /// <summary>The last write failure for a character whose snapshot is still unsaved, if any.</summary>
    public Exception? LastFailure(int characterId)
    {
        lock (_lock)
        {
            return _lastFailure.GetValueOrDefault(characterId) ?? _terminalFailure;
        }
    }

    /// <summary>Drop a deleted character's saved state (memory and tables).</summary>
    public void DeleteCharacter(int characterId)
    {
        if (_scopes is null)
        {
            return;
        }

        lock (_lock)
        {
            _unsaved.Remove(characterId);
            _lastFailure.Remove(characterId);
            Task previous = _chains.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _chains[characterId] = previous.ContinueWith(_ => DeleteAsync(characterId),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Rows for <see cref="ICharacterSpellStateStore.SaveAsync"/>.</summary>
    public static CharacterSpellState ToRows(SpellStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var owners = snapshot.Cooldowns.Where(c => c.SpellId != 0 && (c.ItemId != 0 || c.Category != 0))
            .GroupBy(c => (c.SpellId, c.ItemId, c.Category))
            .Select(group => new CharacterSpellCooldownOwnerRow
            {
                SpellId = group.Key.SpellId, ItemId = group.Key.ItemId, Category = group.Key.Category,
                SpellEndsAtUnixMs = group.Where(c => c.Kind == SpellCooldownKind.Spell).Select(c => c.EndsAtUnixMs).DefaultIfEmpty().Max(),
                CategoryEndsAtUnixMs = group.Where(c => c.Kind == SpellCooldownKind.Category).Select(c => c.EndsAtUnixMs).DefaultIfEmpty().Max(),
            }).ToArray();
        return new CharacterSpellState(
            [.. snapshot.Cooldowns.Select(c => new CharacterSpellCooldownRow { Kind = (byte)c.Kind, Id = c.Id, EndsAtUnixMs = c.EndsAtUnixMs })],
            [.. snapshot.Auras.Select(a => new CharacterAuraRow
            {
                Spell = a.SpellId,
                CasterGuid = a.CasterGuid.Value,
                CasterLevel = a.CasterLevel,
                StackCount = a.StackAmount,
                Charges = a.Charges,
                MaxDurationMs = a.MaxDurationMs,
                RemainingMs = a.RemainingMs,
                EffectMask = a.EffectMask,
                Amount0 = a.Amounts.ElementAtOrDefault(0),
                Amount1 = a.Amounts.ElementAtOrDefault(1),
                Amount2 = a.Amounts.ElementAtOrDefault(2),
                PeriodicTimer0 = a.PeriodicTimers.ElementAtOrDefault(0),
                PeriodicTimer1 = a.PeriodicTimers.ElementAtOrDefault(1),
                PeriodicTimer2 = a.PeriodicTimers.ElementAtOrDefault(2),
                SavedAtUnixMs = a.SavedAtUnixMs,
            })],
            owners);
    }

    /// <summary>The snapshot stored rows describe; unknown cooldown kinds are dropped.</summary>
    public static SpellStateSnapshot FromRows(CharacterSpellState rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cooldowns = rows.Cooldowns.Where(c => Enum.IsDefined((SpellCooldownKind)c.Kind)
                && !(c.Kind == (byte)SpellCooldownKind.Category && (rows.CooldownOwners ?? []).Any(owner => owner.Category == c.Id)))
            .Select(c => new PersistedCooldown((SpellCooldownKind)c.Kind, c.Id, c.EndsAtUnixMs)).ToList();
        foreach (CharacterSpellCooldownOwnerRow owner in rows.CooldownOwners ?? [])
        {
            cooldowns.Add(new PersistedCooldown(SpellCooldownKind.Spell, owner.SpellId, owner.SpellEndsAtUnixMs,
                owner.ItemId, owner.Category, owner.SpellId));
            if (owner.Category != 0 && owner.CategoryEndsAtUnixMs > 0)
                cooldowns.Add(new PersistedCooldown(SpellCooldownKind.Category, owner.Category, owner.CategoryEndsAtUnixMs,
                    owner.ItemId, owner.Category, owner.SpellId));
        }
        return new SpellStateSnapshot(
            cooldowns,
            [.. rows.Auras.OrderBy(a => a.Seq).Select(a => new PersistedAura
            {
                SpellId = a.Spell,
                CasterGuid = new ObjectGuid(a.CasterGuid),
                CasterLevel = a.CasterLevel,
                StackAmount = a.StackCount,
                Charges = a.Charges,
                MaxDurationMs = a.MaxDurationMs,
                RemainingMs = a.RemainingMs,
                EffectMask = a.EffectMask,
                Amounts = [a.Amount0, a.Amount1, a.Amount2],
                PeriodicTimers = [a.PeriodicTimer0, a.PeriodicTimer1, a.PeriodicTimer2],
                SavedAtUnixMs = a.SavedAtUnixMs,
            })]);
    }

    private void FailTerminal(CharacterOwnershipRejectedException ex, IServiceProvider? scopedServices)
    {
        bool first = false;
        lock (_lock)
        {
            if (_terminalFailure is null)
            {
                _terminalFailure = ex;
                first = true;
            }
        }

        if (!first)
        {
            return;
        }

        _logger.LogCritical(ex, "spell state persistence entered terminal failure after character ownership rejection");
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
            FaultLedger.Shared.Record("writequeue.spellstate-stop-host", stopEx);
        }

        FatalFailure?.Invoke(ex);
    }

    private async Task WriteAsync(int characterId, SpellStateSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_terminalFailure is { } terminal)
            {
                _lastFailure[characterId] = terminal;
                return;
            }
        }

        if (!Invariant.Check(_scopes is not null, "SpellStatePersistence WriteAsync requires a scope factory") || _scopes is not { } scopes)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            const int maxAttempts = 4;
            CharacterSpellState? rows = null;
            for (int attempt = 1; ; attempt++)
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                try
                {
                    if (scope.ServiceProvider.GetService<ICharacterSpellStateStore>() is not { } store)
                    {
                        // No store: the state lives in memory for this process only.
                        lock (_lock)
                        {
                            if (_unsaved.TryGetValue(characterId, out SpellStateSnapshot? current) && ReferenceEquals(current, snapshot))
                            {
                                _unsaved.Remove(characterId);
                                _lastFailure.Remove(characterId);
                            }
                        }

                        return;
                    }

                    rows ??= ToRows(snapshot);
                    await store.SaveAsync(characterId, rows).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_unsaved.TryGetValue(characterId, out SpellStateSnapshot? current) && ReferenceEquals(current, snapshot))
                        {
                            _unsaved.Remove(characterId);
                            _lastFailure.Remove(characterId);
                        }
                    }

                    return;
                }
                catch (CharacterOwnershipRejectedException ex)
                {
                    FaultLedger.Shared.Record("writequeue.spellstate-ownership", ex);
                    lock (_lock)
                    {
                        _lastFailure[characterId] = ex;
                    }

                    FailTerminal(ex, scope.ServiceProvider);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && ArcaneCore.Data.Resilience.DatabaseTransience.IsBusy(ex))
                {
                    _logger.LogWarning(ex, "Saving the cooldowns and auras of character {Character} hit a transient database lock (attempt {Attempt} of {Max}); retrying", characterId, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1)))).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("persist.spellstate", ex);
            AnomalyLedger.Shared.Report("persist", "save-failed", $"character {characterId}", $"queue=SpellStatePersistence operation=Save error={ex.GetType().Name}", AnomalySeverity.Warning);
            lock (_lock)
            {
                _lastFailure[characterId] = ex;
            }

            _logger.LogFault("writequeue.spellstate", ex, "Saving the cooldowns and auras of character {Character} failed; kept in memory", characterId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task DeleteAsync(int characterId)
    {
        lock (_lock)
        {
            if (_terminalFailure is not null)
            {
                return;
            }
        }

        if (!Invariant.Check(_scopes is not null, "SpellStatePersistence DeleteAsync requires a scope factory") || _scopes is not { } scopes)
        {
            return;
        }

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            try
            {
                if (scope.ServiceProvider.GetService<ICharacterSpellStateStore>() is { } store)
                {
                    await store.DeleteCharacterAsync(characterId).ConfigureAwait(false);
                }
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                FaultLedger.Shared.Record("writequeue.spellstate-ownership", ex);
                FailTerminal(ex, scope.ServiceProvider);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("persist.spellstate", ex);
            AnomalyLedger.Shared.Report("persist", "save-failed", $"character {characterId}", $"queue=SpellStatePersistence operation=Delete error={ex.GetType().Name}", AnomalySeverity.Warning);
            _logger.LogFault("writequeue.spellstate-delete", ex, "Deleting the cooldowns and auras of character {Character} failed", characterId);
        }
    }
}
