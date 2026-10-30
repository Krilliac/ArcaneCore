using System.Collections.Concurrent;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// Spells learned for one character while the writer is busy reach the store as one call (one transaction) instead of one
/// transaction per spell, without ever being reordered against a forget, a delete or a flush (docs/areas/persistence.md).
/// </summary>
public sealed class SpellbookCacheBatchingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LearnsQueuedForOneCharacterReachTheStoreAsOneCall()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(9, []);
        await fixture.ParkWriterAsync();

        uint[] expected = [.. Enumerable.Range(1000, 18).Select(i => (uint)i)];
        foreach (uint spell in expected)
        {
            Assert.True(fixture.Cache.LearnSpell(NewPlayer(7), spell));
        }

        Assert.False(fixture.Cache.LearnSpell(NewPlayer(7), expected[0])); // an already-known spell queues nothing
        Assert.Equal(1, fixture.Cache.PendingWrites); // 18 learns, one queued entry
        fixture.ReleaseWriter();
        await fixture.Cache.FlushAsync().WaitAsync(Wait);

        (int Character, uint[] Spells)[] calls = [.. fixture.Store.AddCalls.Where(c => c.Character == 7)];
        Assert.Single(calls); // 18 transactions before the change
        Assert.Equal(expected, calls[0].Spells); // all of them, in learn order
        Assert.Equal(expected, await fixture.Store.GetAsync(7));
        Assert.Equal(0, fixture.Cache.PendingWrites);
    }

    [Fact]
    public async Task LearnsOfDifferentCharactersAreNeverMerged_AndKeepTheirOrder()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(8, []);
        fixture.Cache.LoadCharacter(9, []);
        await fixture.ParkWriterAsync();

        fixture.Cache.LearnSpell(NewPlayer(7), 1);
        fixture.Cache.LearnSpell(NewPlayer(8), 2);
        fixture.Cache.LearnSpell(NewPlayer(7), 3); // 8 sits between: a new entry, not folded into the first
        fixture.ReleaseWriter();
        await fixture.Cache.FlushAsync().WaitAsync(Wait);

        Assert.Equal(["add 9 99", "add 7 1", "add 8 2", "add 7 3"], fixture.Store.Log.ToArray());
    }

    [Fact]
    public async Task AForgetBetweenLearnsIsNeverReordered()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(9, []);
        await fixture.ParkWriterAsync();

        Assert.True(fixture.Cache.LearnSpell(NewPlayer(7), 10));
        Assert.True(fixture.Cache.LearnSpell(NewPlayer(7), 11));
        Assert.True(fixture.Cache.ForgetSpell(NewPlayer(7), 10));
        Assert.True(fixture.Cache.LearnSpell(NewPlayer(7), 10)); // learned again after the forget
        Assert.True(fixture.Cache.LearnSpell(NewPlayer(7), 12));
        fixture.ReleaseWriter();
        await fixture.Cache.FlushAsync().WaitAsync(Wait);

        // Add[10,11] -> Remove 10 -> Add[10,12]: folding the second learn of 10 into the first batch would persist it before the forget
        // removed it, and the book on disk would lack a spell the cache says is known.
        Assert.Equal(
            ["add 9 99", "add 7 10,11", "remove 7 10", "add 7 10,12"],
            fixture.Store.Log.ToArray());
        Assert.Equal([10u, 11u, 12u], await fixture.Store.GetAsync(7));
        Assert.Equal([10u, 11u, 12u], fixture.Cache.GetSpells(NewPlayer(7)).Order().ToArray());
    }

    [Fact]
    public async Task ADeleteBetweenLearnsIsNeverReordered()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(9, []);
        await fixture.ParkWriterAsync();

        fixture.Cache.LearnSpell(NewPlayer(7), 20);
        fixture.Cache.DeleteCharacter(7);
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LearnSpell(NewPlayer(7), 21);
        fixture.ReleaseWriter();
        await fixture.Cache.FlushAsync().WaitAsync(Wait);

        Assert.Equal(["add 9 99", "add 7 20", "delete 7", "add 7 21"], fixture.Store.Log.ToArray());
        Assert.Equal([21u], await fixture.Store.GetAsync(7));
    }

    [Fact]
    public async Task FlushCharacterWaitsForTheBatchedLearns()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(9, []);
        await fixture.ParkWriterAsync();

        for (uint spell = 30; spell < 35; spell++)
        {
            fixture.Cache.LearnSpell(NewPlayer(7), spell);
        }

        Task flush = fixture.Cache.FlushCharacterAsync(7);
        await Task.Delay(100);
        Assert.False(flush.IsCompleted); // the writer is still parked in front of it
        Assert.Empty(await fixture.Store.GetAsync(7));

        fixture.Cache.LearnSpell(NewPlayer(7), 35); // after the flush entry: its own entry, written after the flush point
        fixture.ReleaseWriter();
        await flush.WaitAsync(Wait);
        Assert.Equal([30u, 31u, 32u, 33u, 34u], fixture.Store.AddCalls.Where(c => c.Character == 7).First().Spells);

        await fixture.Cache.FlushAsync().WaitAsync(Wait);
        Assert.Equal(2, fixture.Store.AddCalls.Count(c => c.Character == 7));
        Assert.Equal([30u, 31u, 32u, 33u, 34u, 35u], await fixture.Store.GetAsync(7));
    }

    [Fact]
    public async Task FailedBatchMarksTheCharacterForRetry()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(9, []);
        await fixture.ParkWriterAsync();
        fixture.Store.FailAddsFor = 7;

        foreach (uint spell in new uint[] { 40, 41, 42 })
        {
            fixture.Cache.LearnSpell(NewPlayer(7), spell);
        }

        fixture.ReleaseWriter();
        await fixture.Cache.FlushAsync().WaitAsync(Wait); // the failure is logged, the queue stays alive
        Assert.Single(fixture.Store.AddCalls, c => c.Character == 7);
        Assert.Empty(await fixture.Store.GetAsync(7)); // nothing persisted: the batch is one transaction
        Assert.Equal([40u, 41u, 42u], fixture.Cache.GetSpells(NewPlayer(7)).Order().ToArray()); // the cache stays authoritative

        fixture.Store.FailAddsFor = null;
        await fixture.Cache.FlushCharacterAsync(7).WaitAsync(Wait); // the retry writes the whole cached book
        Assert.Equal([40u, 41u, 42u], await fixture.Store.GetAsync(7));
        await fixture.Cache.FlushCharacterAsync(7).WaitAsync(Wait); // a recovered character is not retried again
        Assert.Equal(2, fixture.Store.AddCalls.Count(c => c.Character == 7));
    }

    [Fact]
    public async Task DisposeRetriesAFailedCharacterAndASecondDisposeIsANoOp()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Store.FailAddsFor = 7;
        fixture.Cache.LearnSpell(NewPlayer(7), 60);
        await fixture.Cache.FlushAsync().WaitAsync(Wait); // the write fails and the character is marked
        Assert.Empty(await fixture.Store.GetAsync(7));

        fixture.Store.FailAddsFor = null;
        await fixture.Cache.DisposeAsync();
        Assert.Equal([60u], await fixture.Store.GetAsync(7));

        int adds = fixture.Store.AddCalls.Count();
        await fixture.Cache.DisposeAsync();
        Assert.Equal(adds, fixture.Store.AddCalls.Count());
    }

    [Fact]
    public async Task DisposeThrowsAnAggregateNamingACharacterThatCannotBeWritten()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Store.FailAddsFor = 7;
        fixture.Cache.LearnSpell(NewPlayer(7), 61);
        await fixture.Cache.FlushAsync().WaitAsync(Wait);

        AggregateException ex = await Assert.ThrowsAsync<AggregateException>(async () => await fixture.Cache.DisposeAsync());
        Assert.Contains("character 7", Assert.Single(ex.InnerExceptions).Message);
        await fixture.Cache.DisposeAsync(); // a second dispose does not throw again
    }

    [Fact]
    public async Task AWriterThatIsNotBusyStillPersistsEveryLearn()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        for (uint spell = 50; spell < 55; spell++)
        {
            fixture.Cache.LearnSpell(NewPlayer(7), spell);
            await fixture.Cache.FlushAsync().WaitAsync(Wait); // each flush drains: nothing to batch with
        }

        Assert.Equal(5, fixture.Store.AddCalls.Count(c => c.Character == 7));
        Assert.Equal([50u, 51u, 52u, 53u, 54u], await fixture.Store.GetAsync(7));
    }

    [Fact]
    public async Task ConcurrentLearnsFromManyThreadsAreAllPersistedExactlyOnce()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, []);
        fixture.Cache.LoadCharacter(8, []);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (uint i = 0; i < 100; i++)
            {
                fixture.Cache.LearnSpell(NewPlayer(7 + t % 2), (uint)(t * 100) + i + 1);
            }
        })));
        await fixture.Cache.FlushAsync().WaitAsync(Wait);

        for (int character = 7; character <= 8; character++)
        {
            uint[] persisted = [.. await fixture.Store.GetAsync(character)];
            Assert.Equal(400, persisted.Length);
            Assert.Equal(400, fixture.Store.AddCalls.Where(c => c.Character == character).Sum(c => c.Spells.Length)); // none written twice or dropped
        }
    }

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullPlayerSession());

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(ServiceProvider services, RecordingSpellStore store)
        {
            _services = services;
            Store = store;
            Cache = new SpellbookCache(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
            Cache.Start();
        }

        public RecordingSpellStore Store { get; }

        public SpellbookCache Cache { get; }

        public static Fixture Create()
        {
            var store = new RecordingSpellStore();
            ServiceProvider services = new ServiceCollection().AddScoped<ICharacterSpellStore>(_ => store).BuildServiceProvider();
            return new Fixture(services, store);
        }

        /// <summary>
        /// Occupy the writer: it takes a learn of character 9 (spell 99) and parks inside the store, so everything learned afterwards
        /// is still queued, untaken, when the test releases it.
        /// </summary>
        public async Task ParkWriterAsync()
        {
            Store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Cache.LearnSpell(NewPlayer(9), 99);
            await Store.Parked.Task.WaitAsync(Wait);
        }

        public void ReleaseWriter() => Store.Gate!.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Store.Gate?.TrySetResult();
            await Cache.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingSpellStore : ICharacterSpellStore
    {
        private readonly ConcurrentDictionary<(int, uint), byte> _rows = new();
        private readonly ConcurrentQueue<(int Character, uint[] Spells)> _adds = new();
        private readonly ConcurrentQueue<string> _log = new();

        /// <summary>When set, the first AddAsync parks until the gate is released.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? FailAddsFor { get; set; }

        public IEnumerable<(int Character, uint[] Spells)> AddCalls => _adds;

        public IEnumerable<string> Log => _log;

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([.. _rows.Keys.Select(k => new CharacterSpellRow { CharacterId = k.Item1, Spell = k.Item2 })]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([.. _rows.Keys.Where(k => k.Item1 == characterId).Select(k => k.Item2).Order()]);

        public async Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
        {
            uint[] snapshot = [.. spells];
            if (Gate is { } gate && !Parked.Task.IsCompleted)
            {
                Parked.TrySetResult();
                await gate.Task.ConfigureAwait(false);
            }

            _adds.Enqueue((characterId, snapshot));
            _log.Enqueue($"add {characterId} {string.Join(',', snapshot)}");
            if (FailAddsFor == characterId)
            {
                throw new IOException("controlled spell add failure");
            }

            foreach (uint spell in snapshot)
            {
                _rows.TryAdd((characterId, spell), 0);
            }
        }

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default)
        {
            _log.Enqueue($"remove {characterId} {spell}");
            _rows.TryRemove((characterId, spell), out _);
            return Task.CompletedTask;
        }

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            _log.Enqueue($"delete {characterId}");
            foreach ((int, uint) key in _rows.Keys.Where(k => k.Item1 == characterId))
            {
                _rows.TryRemove(key, out _);
            }

            return Task.CompletedTask;
        }
    }
}
