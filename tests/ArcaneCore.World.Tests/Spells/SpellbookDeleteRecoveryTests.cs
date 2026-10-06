using System.Collections.Concurrent;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class SpellbookDeleteRecoveryTests
{
    [Fact]
    public async Task SuccessfulDelete_ClearsFailedAddMarker_AndIsNotRetried()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Store.FailAdds = true;
        fixture.Cache.LoadCharacter(7, []);
        Assert.True(fixture.Cache.LearnSpell(NewPlayer(7), 123));
        await fixture.Cache.FlushAsync();

        fixture.Store.FailAdds = false;
        fixture.Cache.DeleteCharacter(7);
        await fixture.Cache.FlushAsync();
        await fixture.Cache.FlushCharacterAsync(7);

        Assert.Equal(1, fixture.Store.DeleteCount);
    }

    [Fact]
    public async Task FailedDelete_RemainsRecoverable_WhenStoreReturns()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, [123]);
        fixture.Store.FailDeletes = true;
        fixture.Cache.DeleteCharacter(7);
        await fixture.Cache.FlushAsync();

        fixture.Store.FailDeletes = false;
        await fixture.Cache.FlushCharacterAsync(7);

        Assert.Equal(2, fixture.Store.DeleteCount);
    }

    [Fact]
    public async Task ReplacementBookWhileDeleteIsQueued_RetainsRecoveryMarker()
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Cache.LoadCharacter(7, [123]);
        fixture.Store.FailDeletes = true;
        fixture.Cache.DeleteCharacter(7);
        await fixture.Cache.FlushAsync();

        fixture.Store.FailDeletes = false;
        fixture.Store.HoldDeletes = true;
        fixture.Cache.DeleteCharacter(7);
        await fixture.Store.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Cache.LoadCharacter(7, [456]);
        fixture.Store.ReleaseDelete.TrySetResult();
        await fixture.Cache.FlushAsync();
        await fixture.Cache.FlushCharacterAsync(7);

        Assert.Equal([456u], await fixture.Store.GetAsync(7));
    }

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullSession());

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(ServiceProvider services, CountingStore store)
        {
            _services = services;
            Store = store;
            Cache = new SpellbookCache(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
            Cache.Start();
        }

        public CountingStore Store { get; }
        public SpellbookCache Cache { get; }

        public static Fixture Create()
        {
            var store = new CountingStore();
            ServiceProvider services = new ServiceCollection()
                .AddScoped<ICharacterSpellStore>(_ => store)
                .BuildServiceProvider();
            return new Fixture(services, store);
        }

        public async ValueTask DisposeAsync()
        {
            Store.ReleaseDelete.TrySetResult();
            await Cache.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class CountingStore : ICharacterSpellStore
    {
        private readonly ConcurrentDictionary<(int, uint), byte> _rows = new();
        public bool FailAdds { get; set; }
        public bool FailDeletes { get; set; }
        public bool HoldDeletes { get; set; }
        public int DeleteCount { get; private set; }
        public TaskCompletionSource DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([.. _rows.Keys.Select(k => new CharacterSpellRow { CharacterId = k.Item1, Spell = k.Item2 })]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([.. _rows.Keys.Where(k => k.Item1 == characterId).Select(k => k.Item2).Order()]);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
        {
            if (FailAdds)
            {
                throw new IOException("controlled spell add failure");
            }

            foreach (uint spell in spells)
            {
                _rows.TryAdd((characterId, spell), 0);
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default)
        {
            _rows.TryRemove((characterId, spell), out _);
            return Task.CompletedTask;
        }

        public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (HoldDeletes)
            {
                DeleteEntered.TrySetResult();
                await ReleaseDelete.Task.ConfigureAwait(false);
            }

            if (FailDeletes)
            {
                throw new IOException("controlled spell delete failure");
            }

            foreach ((int, uint) key in _rows.Keys.Where(k => k.Item1 == characterId))
            {
                _rows.TryRemove(key, out _);
            }
        }
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }
}
