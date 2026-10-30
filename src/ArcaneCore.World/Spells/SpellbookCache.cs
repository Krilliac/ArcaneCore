using System.Collections.ObjectModel;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// The spellbook of every character, cached in memory and written through to
/// <c>character_spell</c> (vmangos Player::m_spells + Player::_SaveSpells). All rows are read
/// once at startup (like the character name cache, WorldHost), then refreshed by the character
/// loading hook. Changes are queued and written in order on a background task, so the world
/// thread never waits for the database
/// (docs/integration/spells.md § Spellbook persistence). Spells learned for one character while their
/// write has not been taken by the writer yet share one store call (one transaction), as long as nothing
/// else for the queue sits between them; a forget, a delete or a flush is never reordered
/// (docs/areas/persistence.md). Thread-safe.
/// </summary>
public sealed class SpellbookCache : ISpellbook, IAsyncDisposable
{
    private readonly Dictionary<int, HashSet<uint>> _spells = [];

    /// <summary>
    /// The ascending snapshot <see cref="GetSpells"/> hands out per character, built on the first read after the book changed: a playerbot
    /// think reads the book about twenty times (the sort and the copy were per read). Under <see cref="_lock"/>; every change of a book
    /// (load, learn, adopt, forget, defaults, delete) drops its entry. A snapshot is read-only and never written after it is published, so a
    /// caller may keep or iterate one while the book changes, as with the fresh copy each read used to return. At most one per cached book.
    /// </summary>
    private readonly Dictionary<int, ReadOnlyCollection<uint>> _sorted = [];
    private readonly HashSet<int> _failedCharacters = [];
    private readonly Lock _lock = new();
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger _logger;
    private readonly LinkedList<PendingWrite> _writes = new();
    private readonly Lock _writesLock = new();
    private readonly SemaphoreSlim _writesSignal = new(0);
    private bool _writesCompleted;
    private bool _disposed;
    private Exception? _terminalFailure;

    private Task? _writer;
    private bool _warnedNoStore;

    /// <param name="scopes">Where the scoped <see cref="ICharacterSpellStore"/> comes from; null keeps the spellbook in memory only.</param>
    public SpellbookCache(IServiceScopeFactory? scopes, ILogger logger)
    {
        _scopes = scopes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Terminal failure that halted the spellbook writer (such as <see cref="CharacterOwnershipRejectedException"/>), if any.</summary>
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

    /// <summary>Raised once when the spellbook writer enters terminal failure (for example on <see cref="CharacterOwnershipRejectedException"/>).</summary>
    public event Action<Exception>? FatalFailure;

    /// <summary>
    /// A spell was added to a player's book by <see cref="LearnSpell"/> (any thread that learns: the session task
    /// while a character loads, the world thread afterwards). Raised after the book changed and outside its lock,
    /// so a handler may learn or forget further spells (the skills area teaches the spells a skill grants).
    /// </summary>
    public event Action<Player, uint>? SpellLearned;

    /// <summary>A spell was removed from a player's book by <see cref="ForgetSpell"/> (same rules as <see cref="SpellLearned"/>).</summary>
    public event Action<Player, uint>? SpellForgotten;

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
    public int PendingWrites
    {
        get
        {
            lock (_writesLock)
            {
                return _writes.Count;
            }
        }
    }

    /// <summary>Fill the cache (startup, before the world thread runs).</summary>
    public void Load(IEnumerable<CharacterSpellRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_lock)
        {
            _spells.Clear();
            _sorted.Clear();
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
            _sorted.Remove(characterId);
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

            _sorted.Remove(id);
        }

        EnqueueAdd(id, [spellId]);
        SpellLearned?.Invoke(player, spellId);
        return true;
    }

    /// <summary>
    /// Add spells a quest reward transaction already persisted to a book that is cached; queues no write
    /// and never creates a book (a partial book would be mistaken for an initialized one; login reloads
    /// storage in any case). Returns false when the character has no cached book.
    /// </summary>
    public bool AdoptCommitted(int characterId, IReadOnlyCollection<uint> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        lock (_lock)
        {
            if (!_spells.TryGetValue(characterId, out HashSet<uint>? book))
            {
                return false;
            }

            book.UnionWith(spells);
            _sorted.Remove(characterId);
            return true;
        }
    }

    /// <summary>
    /// Forget a spell (vmangos Player::removeSpell); false when it was not known, or while a quest
    /// reward settlement holds the character (a queued removal could race the reward's own rows).
    /// </summary>
    public bool ForgetSpell(Player player, uint spellId)
    {
        if (!player.CanMutateQuestSettlementState)
        {
            return false;
        }

        int id = CharacterId(player);
        lock (_lock)
        {
            if (!_spells.TryGetValue(id, out HashSet<uint>? book) || !book.Remove(spellId))
            {
                return false;
            }

            _sorted.Remove(id);
        }

        Enqueue(id, store => store.RemoveAsync(id, spellId));
        SpellForgotten?.Invoke(player, spellId);
        return true;
    }

    /// <summary>
    /// A player's known spells, ascending: a read-only snapshot that later changes of the book do not alter (the same instance until the
    /// book changes).
    /// </summary>
    public IReadOnlyList<uint> GetSpells(Player player)
    {
        int id = CharacterId(player);
        lock (_lock)
        {
            if (!_spells.TryGetValue(id, out HashSet<uint>? book))
            {
                return [];
            }

            if (!_sorted.TryGetValue(id, out ReadOnlyCollection<uint>? sorted))
            {
                uint[] spells = [.. book];
                Array.Sort(spells);
                sorted = Array.AsReadOnly(spells);
                _sorted[id] = sorted;
            }

            return sorted;
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
            _sorted.Remove(id);
        }

        if (defaults.Count > 0)
        {
            EnqueueAdd(id, [.. defaults]);
        }

        return true;
    }

    /// <summary>Drop a deleted character's spellbook (cache and table).</summary>
    public void DeleteCharacter(int characterId)
    {
        lock (_lock)
        {
            _spells.Remove(characterId);
            _sorted.Remove(characterId);
        }

        Enqueue(characterId, store => DeleteCharacterAsync(store, characterId));
    }

    /// <summary>Wait until every queued write has been attempted (tests, shutdown).</summary>
    public async Task FlushAsync()
    {
        if (_scopes is null || _writer is null)
        {
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryAppend(new PendingWrite(null, static _ => Task.CompletedTask, done)))
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
        if (!TryAppend(new PendingWrite(characterId, store => RetryFailedCharacterAsync(store, characterId), done)))
        {
            throw new InvalidOperationException("The spellbook writer has stopped");
        }

        await done.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_writesLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writesCompleted = true;
        }

        _writesSignal.Release();
        if (_writer is not null)
        {
            await _writer.ConfigureAwait(false);
        }

        if (_terminalFailure is { } terminal)
        {
            throw new InvalidOperationException("SpellbookCache stopped with a terminal ownership failure", terminal);
        }

        await ReconcileFailedAtShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The writer has drained, so nothing else touches the store: retry every character whose write failed against its cached book in one
    /// scope. A character that still cannot be written is logged once and thrown in an <see cref="AggregateException"/>, not dropped.
    /// </summary>
    private async Task ReconcileFailedAtShutdownAsync()
    {
        int[] failed;
        lock (_lock)
        {
            failed = [.. _failedCharacters];
        }

        if (failed.Length == 0 || _scopes is null)
        {
            return;
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<ICharacterSpellStore>() is not { } store)
        {
            return;
        }

        var errors = new List<Exception>();
        foreach (int characterId in failed)
        {
            try
            {
                await RetryFailedCharacterAsync(store, characterId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(new InvalidOperationException($"spellbook of character {characterId} could not be written at shutdown", ex));
                _logger.LogFault("writequeue.spellbook-shutdown", ex, "Reconciling the spellbook of character {CharacterId} at shutdown failed", characterId);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("SpellbookCache stopped with unreconciled spellbooks", errors);
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

        TryAppend(new PendingWrite(characterId, write));
    }

    /// <summary>
    /// Queue spells for one character. They join the entry at the tail of the queue when that is an Add of the same character the writer has
    /// not taken yet (so the learns of one tick reach the store as one call whenever the writer is busy or behind); otherwise they start
    /// an entry. Never merged across any other entry, so every write keeps its position relative to forgets, deletes and flushes.
    /// </summary>
    private void EnqueueAdd(int characterId, uint[] spells)
    {
        if (_scopes is null)
        {
            return;
        }

        lock (_writesLock)
        {
            if (_writesCompleted)
            {
                return;
            }

            if (_writes.Last?.Value is { Spells: { } tail, Taken: false, CharacterId: { } owner } && owner == characterId)
            {
                tail.AddRange(spells);
                return;
            }

            List<uint> spellList = [.. spells];
            var entry = new PendingWrite(characterId, store => store.AddAsync(characterId, spellList)) { Spells = spellList };
            _writes.AddLast(entry);
        }

        _writesSignal.Release();
    }

    private bool TryAppend(PendingWrite entry)
    {
        lock (_writesLock)
        {
            if (_writesCompleted)
            {
                return false;
            }

            _writes.AddLast(entry);
        }

        _writesSignal.Release();
        return true;
    }

    /// <summary>The oldest queued write, marked taken so nothing is appended to it; null when the queue is empty.</summary>
    private PendingWrite? TakeNext(out bool completed)
    {
        lock (_writesLock)
        {
            completed = _writesCompleted;
            if (_writes.First is not { } first)
            {
                return null;
            }

            _writes.RemoveFirst();
            first.Value.Taken = true;
            return first.Value;
        }
    }

    private async Task RetryFailedCharacterAsync(ICharacterSpellStore store, int characterId)
    {
        HashSet<uint>? desired;
        lock (_lock)
        {
            if (_terminalFailure is { } terminal)
            {
                throw new InvalidOperationException($"spellbook writes for character {characterId} are not durable", terminal);
            }

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

    private async Task DeleteCharacterAsync(ICharacterSpellStore store, int characterId)
    {
        await store.DeleteCharacterAsync(characterId).ConfigureAwait(false);
        if (ReferenceEquals(store, NullCharacterSpellStore.Instance))
        {
            return;
        }

        lock (_lock)
        {
            if (!_spells.ContainsKey(characterId))
            {
                _failedCharacters.Remove(characterId);
            }
        }
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

        _logger.LogCritical(ex, "spellbook cache entered terminal failure after character ownership rejection");
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
            FaultLedger.Shared.Record("writequeue.spellbook-stop-host", stopEx);
        }

        FatalFailure?.Invoke(ex);
    }

    private async Task WriteLoopAsync()
    {
        while (true)
        {
            await _writesSignal.WaitAsync().ConfigureAwait(false);
            PendingWrite? next = TakeNext(out bool completed);
            if (next is null)
            {
                if (completed)
                {
                    break;
                }

                continue;
            }

            PendingWrite pending = next;
            if (!Invariant.Check(_scopes is not null, "SpellbookCache write loop requires a scope factory") || _scopes is not { } scopes)
            {
                pending.Done?.TrySetResult();
                continue;
            }

            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                try
                {
                    lock (_lock)
                    {
                        if (_terminalFailure is { } terminal && pending.CharacterId is not null)
                        {
                            throw new InvalidOperationException($"spellbook writes for character {pending.CharacterId.Value} are not durable", terminal);
                        }
                    }

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
                catch (CharacterOwnershipRejectedException ex)
                {
                    FaultLedger.Shared.Record("writequeue.spellbook-ownership", ex);
                    if (pending.CharacterId is { } characterId)
                    {
                        lock (_lock)
                        {
                            _failedCharacters.Add(characterId);
                        }
                    }

                    FailTerminal(ex, scope.ServiceProvider);
                    pending.Done?.TrySetException(ex);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
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
                FaultLedger.Shared.Record("spell.spellbook-save", ex);
                _logger.LogFault("writequeue.spellbook", ex, "Saving a spellbook change failed");
            }
        }
    }

    private sealed class PendingWrite(int? characterId, Func<ICharacterSpellStore, Task> write, TaskCompletionSource? done = null)
    {
        public int? CharacterId { get; } = characterId;

        public Func<ICharacterSpellStore, Task> Write { get; set; } = write;

        public TaskCompletionSource? Done { get; } = done;

        /// <summary>The spells of an Add entry, appended to under the writes lock until the writer takes the entry.</summary>
        public List<uint>? Spells { get; init; }

        /// <summary>Set under the writes lock when the writer dequeues the entry.</summary>
        public bool Taken { get; set; }
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
