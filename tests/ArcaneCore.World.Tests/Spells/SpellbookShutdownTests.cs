using System.Collections.Concurrent;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>A shutdown that finds spellbooks whose write failed but no store to retry them against reports the failed set instead of returning silently.</summary>
public sealed class SpellbookShutdownTests
{
    [Fact]
    public async Task ShutdownWithoutAResolvableStore_LogsOneFaultNamingTheFailedCharacter()
    {
        var logger = new CaptureLogger();
        var store = new FailingStore();
        await using ServiceProvider inner = new ServiceCollection().AddScoped<ICharacterSpellStore>(_ => store).BuildServiceProvider();
        await using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        var factory = new SwitchableScopeFactory(inner.GetRequiredService<IServiceScopeFactory>(), empty.GetRequiredService<IServiceScopeFactory>());
        var cache = new SpellbookCache(factory, logger);
        cache.Start();

        cache.LoadCharacter(31, []);
        Assert.True(cache.LearnSpell(NewPlayer(31), 123));
        await cache.FlushAsync(); // the add fails, so character 31 is in the failed set

        factory.Hidden = true; // at shutdown the store can no longer be resolved
        await cache.DisposeAsync();

        (LogLevel Level, string Message)[] faults = [.. logger.Entries.Where(e => e.Message.Contains("no store available", StringComparison.Ordinal))];
        (LogLevel level, string message) = Assert.Single(faults);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("31", message, StringComparison.Ordinal);
    }

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullPlayerSession());

    private sealed class SwitchableScopeFactory(IServiceScopeFactory visible, IServiceScopeFactory hidden) : IServiceScopeFactory
    {
        public volatile bool Hidden;

        public IServiceScope CreateScope() => Hidden ? hidden.CreateScope() : visible.CreateScope();
    }

    private sealed class FailingStore : ICharacterSpellStore
    {
        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([]);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
            => throw new IOException("controlled spell add failure");

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
