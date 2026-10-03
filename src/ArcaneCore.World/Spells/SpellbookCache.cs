using System.Threading.Channels;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// The spellbook of every character, cached in memory and written through to
/// <c>character_spell</c> (vmangos Player::m_spells + Player::_SaveSpells). All rows are read
/// once at startup (like the character name cache, WorldHost), then refreshed by the character
/// loading hook. Changes are queued and written in order on a background task, so the world
/// thread never waits for the database
/// (docs/integration/spells.md § Spellbook persistence). Thread-safe.
/// </summary>
public sealed class SpellbookCache : ISpellbook, IAsyncDisposable
{
    private readonly Dictionary<int, HashSet<uint>> _spells = [];
    private readonly HashSet<int> _failedCharacters = [];
    private readonly Lock _lock = new();
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger _logger;
    private readonly Channel<PendingWrite> _writes =
        Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions { SingleReader = true });

    private Task? _writer;
    private bool _warnedNoStore;

    /// <param name="scopes">Where the scoped <see cref="ICharacterSpellStore"/> comes from; null keeps the spellbook in memory only.</param>
    public SpellbookCache(IServiceScopeFactory? scopes, ILogger logger)
    {
        _scopes = scopes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Characters with a cached spellbook.</summary>
    public int CharacterCount
    {
        get
        {
            lock (_lock)
            {
                return _spells.Count;
            }
        }
    }

    /// <summary>Writes queued and not yet finished.</summary>
    public int PendingWrites => _writes.Reader.Count;

    /// <summary>Fill the cache (startup, before the world thread runs).</summary>
    public void Load(IEnumerable<CharacterSpellRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_lock)
        {
            _spells.Clear();
            foreach (CharacterSpellRow row in rows)
            {
                Book(row.CharacterId).Add(row.Spell);
            }
        }
    }

    /// <summary>Replace one offline character's book after its storage load succeeds.</summary>
    public void LoadCharacter(int characterId, IReadOnlyList<uint> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        lock (_lock)
        {
            _spells[characterId] = [.. spells];
        }
    }

    /// <summary>An initialized book may be empty after its owner forgets every spell.</summary>
    public bool ContainsCharacter(int characterId)
    {
        lock (_lock)
        {
            return _spells.ContainsKey(characterId);
        }
    }

    /// <summary>Start the background writer.</summary>
    public void Start() => _writer ??= Task.Run(WriteLoopAsync);

    /// <summary>The character id a player's spellbook is keyed by (the player GUID's low part is the characters.Id).</summary>
    public static int CharacterId(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return unchecked((int)player.Guid.Low);
    }

    public bool HasSpell(Player player, uint spellId)
    {
        lock (_lock)
        {
            return _spells.TryGetValue(CharacterId(player), out HashSet<uint>? book) && book.Contains(spellId);
        }
    }

    public bool LearnSpell(Player player, uint spellId)
    {
        int id = CharacterId(player);
        lock (_lock)
        {
            if (!Book(id).Add(spellId))
            {
                return false;
            }
        }

        Enqueue(id, store => store.AddAsync(id, [spellId]));
        return true;
    }

    /// <summary>Forget a spell (vmangos Player::removeSpell); false when it was not known.</summary>
    public bool ForgetSpell(Player player, uint spellId)
    {
        int id = CharacterId(player);
        lock (_lock)
        {
            if (!_spells.TryGetValue(id, out HashSet<uint>? book) || !book.Remove(spellId))
            {
                return false;
            }
        }

        Enqueue(id, store => store.RemoveAsync(id, spellId));
        return true;
    }

    /// <summary>A player's known spells, ascending.</summary>
    public IReadOnlyList<uint> GetSpells(Player player)
    {
        lock (_lock)
        {
            return _spells.TryGetValue(CharacterId(player), out HashSet<uint>? book) ? [.. book.Order()] : [];
        }
    }

    /// <summary>
    /// Give a character that has no spellbook yet its race/class defaults and persist them
    /// (cmangos-classic Player::Create → learnDefaultSpells, done here on the first login
    /// because character creation is the characters area's handler). Returns true when granted.
    /// </summary>
    public bool EnsureDefaults(Player player, IReadOnlyList<uint> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        int id = CharacterId(player);
        lock (_lock)
        {
            if (_spells.ContainsKey(id))
            {
                return false;
            }

            Book(id).UnionWith(defaults);
        }

        if (defaults.Count > 0)
        {
            uint[] spells = [.. defaults];
            Enqueue(id, store => store.AddAsync(id, spells));
        }

        return true;
    }

    /// <summary>Drop a deleted character's spellbook (cache and table).</summary>
    public void DeleteCharacter(int characterId)
    {
        lock (_lock)
        {
            _spells.Remove(characterId);
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
    /// Wait for earlier writes, then retry a failed character's final cached book before login
    /// reloads storage. A failed retry propagates; the cache remains authoritative for the next attempt.
    /// </summary>
    public async Task FlushCharacterAsync(int characterId)
    {
        if (_scopes is null || _writer is null)
        {
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new PendingWrite(characterId, store => RetryFailedCharacterAsync(store, characterId), done)))
        {
            throw new InvalidOperationException("The spellbook writer has stopped");
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

    private HashSet<uint> Book(int characterId)
    {
        if (!_spells.TryGetValue(characterId, out HashSet<uint>? book))
        {
            book = [];
            _spells[characterId] = book;
        }

        return book;
    }

    private void Enqueue(int characterId, Func<ICharacterSpellStore, Task> write)
    {
        if (_scopes is null)
        {
            return;
        }

        _writes.Writer.TryWrite(new PendingWrite(characterId, write));
    }

    private async Task RetryFailedCharacterAsync(ICharacterSpellStore store, int characterId)
    {
        HashSet<uint>? desired;
        lock (_lock)
        {
            if (!_failedCharacters.Contains(characterId))
            {
                return;
            }

            desired = _spells.TryGetValue(characterId, out HashSet<uint>? book) ? [.. book] : null;
        }

        if (ReferenceEquals(store, NullCharacterSpellStore.Instance))
        {
            throw new InvalidOperationException("Cannot retry a spellbook without a character spell store");
        }

        if (desired is null)
        {
            await store.DeleteCharacterAsync(characterId).ConfigureAwait(false);
        }
        else
        {
            IReadOnlyList<uint> persisted = await store.GetAsync(characterId).ConfigureAwait(false);
            uint[] missing = [.. desired.Except(persisted)];
            if (missing.Length > 0)
            {
                await store.AddAsync(characterId, missing).ConfigureAwait(false);
            }

            foreach (uint spell in persisted.Except(desired))
            {
                await store.RemoveAsync(characterId, spell).ConfigureAwait(false);
            }
        }

        lock (_lock)
        {
            bool unchanged = desired is null
                ? !_spells.ContainsKey(characterId)
                : _spells.TryGetValue(characterId, out HashSet<uint>? current) && current.SetEquals(desired);
            if (!unchanged)
            {
                throw new InvalidOperationException("Spellbook changed while its failed writes were being recovered");
            }

            _failedCharacters.Remove(characterId);
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (PendingWrite pending in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
                ICharacterSpellStore? store = scope.ServiceProvider.GetService<ICharacterSpellStore>();
                if (store is null)
                {
                    if (!_warnedNoStore)
                    {
                        _warnedNoStore = true;
                        _logger.LogWarning("No ICharacterSpellStore is registered; spellbook changes are not persisted");
                    }

                    await pending.Write(NullCharacterSpellStore.Instance).ConfigureAwait(false);
                    pending.Done?.TrySetResult();
                    continue;
                }

                await pending.Write(store).ConfigureAwait(false);
                pending.Done?.TrySetResult();
            }
            catch (Exception ex)
            {
                // Fail loudly but keep the queue alive; the cache stays authoritative until restart.
                if (pending.CharacterId is { } characterId)
                {
                    lock (_lock)
                    {
                        _failedCharacters.Add(characterId);
                    }
                }

                pending.Done?.TrySetException(ex);
                _logger.LogError(ex, "Saving a spellbook change failed");
            }
        }
    }

    private readonly record struct PendingWrite(int? CharacterId, Func<ICharacterSpellStore, Task> Write, TaskCompletionSource? Done = null);

    private sealed class NullCharacterSpellStore : ICharacterSpellStore
    {
        public static readonly NullCharacterSpellStore Instance = new();

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([]);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
