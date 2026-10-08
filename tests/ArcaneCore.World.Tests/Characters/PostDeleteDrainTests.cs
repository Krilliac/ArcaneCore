using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// The spell, reputation, instance and social delete hooks end their post-delete step by waiting for
/// the removal they queued (bounded), so a pending deletion completes only after the removal was
/// attempted. The store call is held on a gate: the hook's task must stay open until the gate is
/// released, and a gate that never opens must surface as a <see cref="TimeoutException"/>. Deleting
/// the drain await in a hook makes the matching test fail.
/// </summary>
public sealed class PostDeleteDrainTests
{
    private const int CharacterId = 4242;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortDrain = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StillOpen = TimeSpan.FromMilliseconds(300);

    private static readonly CharacterRecord Character = new() { Id = CharacterId, AccountId = 1, Name = "Victim" };

    // The four hooks never read the session.
    private static WorldSession NoSession => null!;

    // ---- spell: spellbook removal ----

    [Fact]
    public async Task SpellHook_WaitsForTheQueuedSpellbookRemoval()
    {
        var gate = new Gate();
        await using ServiceProvider provider = SpellProvider(spellbookGate: gate, stateGate: null);
        await using SpellFeature feature = SpellFeatureOf(provider);
        var hook = new SpellCharacterDeleteHook(feature);

        await AssertWaitsForGateAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    [Fact]
    public async Task SpellHook_SurfacesATimeoutWhenTheSpellbookRemovalNeverFinishes()
    {
        var gate = new Gate();
        await using ServiceProvider provider = SpellProvider(spellbookGate: gate, stateGate: null);
        await using SpellFeature feature = SpellFeatureOf(provider);
        var hook = new SpellCharacterDeleteHook(feature) { DrainTimeout = ShortDrain };

        await AssertTimesOutAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    // ---- spell: saved cooldowns and auras ----

    [Fact]
    public async Task SpellHook_WaitsForTheQueuedStateRemoval()
    {
        var gate = new Gate();
        await using ServiceProvider provider = SpellProvider(spellbookGate: null, stateGate: gate);
        await using SpellFeature feature = SpellFeatureOf(provider);
        var hook = new SpellCharacterDeleteHook(feature);

        await AssertWaitsForGateAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    [Fact]
    public async Task SpellHook_SurfacesATimeoutWhenTheStateRemovalNeverFinishes()
    {
        var gate = new Gate();
        await using ServiceProvider provider = SpellProvider(spellbookGate: null, stateGate: gate);
        await using SpellFeature feature = SpellFeatureOf(provider);
        var hook = new SpellCharacterDeleteHook(feature) { DrainTimeout = ShortDrain };

        await AssertTimesOutAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    // ---- reputation ----

    [Fact]
    public async Task ReputationHook_WaitsForTheQueuedRemoval()
    {
        var gate = new Gate();
        await using ServiceProvider provider = ReputationProvider(gate);
        using WorldRuntime world = CreateWorld();
        await using ReputationFeature feature = new(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        feature.Attach(world);
        var hook = new ReputationCharacterDeleteHook(feature);

        await AssertWaitsForGateAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    [Fact]
    public async Task ReputationHook_SurfacesATimeoutWhenTheRemovalNeverFinishes()
    {
        var gate = new Gate();
        await using ServiceProvider provider = ReputationProvider(gate);
        using WorldRuntime world = CreateWorld();
        await using ReputationFeature feature = new(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        feature.Attach(world);
        var hook = new ReputationCharacterDeleteHook(feature) { DrainTimeout = ShortDrain };

        await AssertTimesOutAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    // ---- instances ----

    [Fact]
    public async Task InstanceHook_WaitsForTheQueuedRemoval()
    {
        var gate = new Gate();
        await using ServiceProvider provider = InstanceProvider(gate);
        using WorldRuntime world = CreateWorld();
        await using InstanceFeature feature = new(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        feature.Attach(world);
        world.Start();

        await AssertWaitsForGateAsync(gate, feature.OnCharacterDeletedAsync(NoSession, Character));
    }

    [Fact]
    public async Task InstanceHook_SurfacesATimeoutWhenTheRemovalNeverFinishes()
    {
        var gate = new Gate();
        await using ServiceProvider provider = InstanceProvider(gate);
        using WorldRuntime world = CreateWorld();
        await using InstanceFeature feature = new(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance)
        {
            DrainTimeout = ShortDrain,
        };
        feature.Attach(world);
        world.Start();

        await AssertTimesOutAsync(gate, feature.OnCharacterDeletedAsync(NoSession, Character));
    }

    // ---- social ----

    [Fact]
    public async Task SocialHook_WaitsForTheQueuedPurge()
    {
        var gate = new Gate();
        await using ServiceProvider provider = SocialProvider(gate);
        using WorldRuntime world = CreateWorld();
        var directory = new CharacterDirectory();
        await using SocialFeature feature = new(directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        feature.Attach(world);
        world.Start();
        await feature.GuildsLoaded.WaitAsync(Deadline);
        var hook = new SocialCharacterDeleteHook(feature, directory);
        hook.Attach(world);

        await AssertWaitsForGateAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    [Fact]
    public async Task SocialHook_SurfacesATimeoutWhenThePurgeNeverFinishes()
    {
        var gate = new Gate();
        await using ServiceProvider provider = SocialProvider(gate);
        using WorldRuntime world = CreateWorld();
        var directory = new CharacterDirectory();
        await using SocialFeature feature = new(directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        feature.Attach(world);
        world.Start();
        await feature.GuildsLoaded.WaitAsync(Deadline);
        var hook = new SocialCharacterDeleteHook(feature, directory) { DrainTimeout = ShortDrain };
        hook.Attach(world);

        await AssertTimesOutAsync(gate, hook.OnCharacterDeletedAsync(NoSession, Character));
    }

    // ---- assertions ----

    /// <summary>The hook's task stays open while the store call is held, and completes once it is released.</summary>
    private static async Task AssertWaitsForGateAsync(Gate gate, Task hook)
    {
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Task first = await Task.WhenAny(hook, Task.Delay(StillOpen));
            Assert.NotSame(hook, first);
            Assert.False(hook.IsCompleted);

            gate.Open.TrySetResult();
            await hook.WaitAsync(Deadline);
        }
        finally
        {
            gate.Open.TrySetResult();
        }
    }

    private static async Task AssertTimesOutAsync(Gate gate, Task hook)
    {
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<TimeoutException>(() => hook.WaitAsync(Deadline));
        }
        finally
        {
            gate.Open.TrySetResult();
        }
    }

    // ---- fixtures ----

    private static WorldRuntime CreateWorld() => new(
        new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
        new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);

    private static SpellFeature SpellFeatureOf(ServiceProvider provider)
    {
        var feature = new SpellFeature(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance);
        feature.Spellbook.Start();
        return feature;
    }

    private static ServiceProvider SpellProvider(Gate? spellbookGate, Gate? stateGate)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICharacterSpellStore>(_ => new GatedSpellStore(spellbookGate));
        services.AddScoped<ICharacterSpellStateStore>(_ => new GatedSpellStateStore(stateGate));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider ReputationProvider(Gate gate)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICharacterReputationStore>(_ => new GatedReputationStore(gate));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider InstanceProvider(Gate gate)
    {
        var services = new ServiceCollection();
        services.AddScoped<IInstanceStore>(_ => new GatedInstanceStore(gate));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider SocialProvider(Gate gate)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISocialStore>(_ => new GatedSocialStore(gate));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Holds the store call that removes the deleted character until <see cref="Open"/> is set.</summary>
    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PassAsync()
        {
            Entered.TrySetResult();
            await Open.Task.ConfigureAwait(false);
        }
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state) { }
    }

    private sealed class GatedSpellStore(Gate? gate) : ICharacterSpellStore
    {
        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([]);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => gate?.PassAsync() ?? Task.CompletedTask;
    }

    private sealed class GatedSpellStateStore(Gate? gate) : ICharacterSpellStateStore
    {
        public Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterSpellState([], []));

        public Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => gate?.PassAsync() ?? Task.CompletedTask;
    }

    private sealed class GatedReputationStore(Gate gate) : ICharacterReputationStore
    {
        public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(CharacterReputationData.Empty);

        public Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => gate.PassAsync();
    }

    private sealed class GatedInstanceStore(Gate gate) : IInstanceStore
    {
        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(InstanceStoreSnapshot.Empty);

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => gate.PassAsync();
    }

    private sealed class GatedSocialStore(Gate gate) : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => gate.PassAsync();
    }
}
