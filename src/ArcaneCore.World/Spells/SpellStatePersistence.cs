using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly Dictionary<int, Task> _chains = [];
    private readonly Dictionary<int, SpellStateSnapshot> _unsaved = [];

    public SpellStatePersistence(IServiceScopeFactory? scopes, ILogger logger)
    {
        _scopes = scopes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

    /// <summary>Whether a character has a snapshot that has not reached storage yet.</summary>
    public bool HasUnsaved(int characterId)
    {
        lock (_lock)
        {
            return _unsaved.ContainsKey(characterId);
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

    private async Task WriteAsync(int characterId, SpellStateSnapshot snapshot)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ICharacterSpellStateStore>() is not { } store)
            {
                // No store: the state lives in memory for this process only.
                return;
            }

            await store.SaveAsync(characterId, ToRows(snapshot)).ConfigureAwait(false);
            lock (_lock)
            {
                if (_unsaved.TryGetValue(characterId, out SpellStateSnapshot? current) && ReferenceEquals(current, snapshot))
                {
                    _unsaved.Remove(characterId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the cooldowns and auras of character {Character} failed; kept in memory", characterId);
        }
    }

    private async Task DeleteAsync(int characterId)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ICharacterSpellStateStore>() is { } store)
            {
                await store.DeleteCharacterAsync(characterId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting the cooldowns and auras of character {Character} failed", characterId);
        }
    }
}
