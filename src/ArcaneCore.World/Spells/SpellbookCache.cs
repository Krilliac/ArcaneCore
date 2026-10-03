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
/// once at startup (like the character name cache, WorldHost) because the login handler
/// offers no per-login async load seam yet; changes are queued and written in order on a
/// background task, so the world thread never waits for the database
/// (docs/integration/spells.md § Spellbook persistence). Thread-safe.
/// </summary>
public sealed class SpellbookCache : ISpellbook, IAsyncDisposable
{
    private readonly Dictionary<int, HashSet<uint>> _spells = [];
    private readonly Lock _lock = new();
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger _logger;
    private readonly Channel<Func<ICharacterSpellStore, Task>> _writes =
        Channel.CreateUnbounded<Func<ICharacterSpellStore, Task>>(new UnboundedChannelOptions { SingleReader = true });

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

        Enqueue(store => store.AddAsync(id, [spellId]));
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

        Enqueue(store => store.RemoveAsync(id, spellId));
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
            Enqueue(store => store.AddAsync(id, spells));
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

        Enqueue(store => store.DeleteCharacterAsync(characterId));
    }

    /// <summary>Wait until every queued write has been attempted (tests, shutdown).</summary>
    public async Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(_ =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        }))
        {
            return;
        }

        if (_writer is null)
        {
            return;
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

    private void Enqueue(Func<ICharacterSpellStore, Task> write)
    {
        if (_scopes is null)
        {
            return;
        }

        _writes.Writer.TryWrite(write);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (Func<ICharacterSpellStore, Task> write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
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

                    await write(NullCharacterSpellStore.Instance).ConfigureAwait(false);
                    continue;
                }

                await write(store).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fail loudly but keep the queue alive; the cache stays authoritative until restart.
                _logger.LogError(ex, "Saving a spellbook change failed");
            }
        }
    }

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
