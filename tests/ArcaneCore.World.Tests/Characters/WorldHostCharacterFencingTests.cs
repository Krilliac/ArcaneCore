using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

public sealed class WorldHostCharacterFencingTests
{
    [Fact]
    public async Task ClaimsBeforeFeatureAttachmentAndReleasesAfterAHealthyDrain()
    {
        await using TestKit kit = TestKit.Create(enabled: true);
        await kit.Host.StartAsync(CancellationToken.None);

        Assert.Equal(1, kit.Ownership.AcquireCalls);
        Assert.True(kit.Feature.AttachedWithToken);
        await kit.Host.StopAsync(CancellationToken.None);
        Assert.Equal(1, kit.Ownership.ReleaseCalls);
    }

    [Fact]
    public async Task ExistingOwnerBlocksStartupBeforeFeaturesAttachEvenWhenFencingIsDisabled()
    {
        await using TestKit kit = TestKit.Create(enabled: false);
        kit.Ownership.Owned = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Host.StartAsync(CancellationToken.None));
        Assert.False(kit.Feature.AttachedWithToken);
        Assert.Equal(0, kit.Ownership.AcquireCalls);
    }

    [Fact]
    public async Task FailedTerminalDrainRetainsOwnership()
    {
        await using TestKit kit = TestKit.Create(enabled: true);
        kit.Store.SaveOverride = _ => Task.FromException(new CharacterOwnershipRejectedException("world", 1));
        await kit.Host.StartAsync(CancellationToken.None);
        kit.SaveQueue.Enqueue(new CharacterState(1, 0, 12, 0, 0, 83.5f, 0, 1, 0));

        await Assert.ThrowsAsync<AggregateException>(() => kit.Host.StopAsync(CancellationToken.None));
        Assert.Equal(0, kit.Ownership.ReleaseCalls);
    }

    private sealed class TestKit : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WorldRuntime _world;

        private TestKit(ServiceProvider provider, WorldRuntime world, CharacterSaveQueue saveQueue,
            WorldHost host, RecordingOwnership ownership, RecordingFeature feature,
            InMemoryCharacterStore store)
        {
            _provider = provider;
            _world = world;
            SaveQueue = saveQueue;
            Host = host;
            Ownership = ownership;
            Feature = feature;
            Store = store;
        }

        public WorldHost Host { get; }
        public CharacterSaveQueue SaveQueue { get; }
        public RecordingOwnership Ownership { get; }
        public RecordingFeature Feature { get; }
        public InMemoryCharacterStore Store { get; }

        public static TestKit Create(bool enabled)
        {
            var store = new InMemoryCharacterStore();
            var ownership = new RecordingOwnership();
            var feature = new RecordingFeature();
            var services = new ServiceCollection();
            services.AddSingleton<ICharacterStore>(store);
            services.AddSingleton<ICharacterOwnershipStore>(ownership);
            services.AddSingleton<IWorldFeature>(feature);
            ServiceProvider provider = services.BuildServiceProvider();
            IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue,
                NullLogger<WorldRuntime>.Instance);
            var options = Options.Create(new CharacterFencingOptions { Enabled = enabled, OwnerNode = "test" });
            var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes,
                NullLogger<WorldHost>.Instance, options);
            return new TestKit(provider, world, saveQueue, host, ownership, feature, store);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(CancellationToken.None); }
            catch (Exception) { }
            _world.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed class RecordingFeature : IWorldFeature
    {
        public bool AttachedWithToken { get; private set; }
        public void Attach(WorldRuntime world) => AttachedWithToken = world.CharacterOwnership is not null;
    }

    private sealed class RecordingOwnership : ICharacterOwnershipStore
    {
        public bool Owned { get; set; }
        public int AcquireCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default) => Task.FromResult(Owned);

        public Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            if (Owned) return Task.FromResult<CharacterOwnershipToken?>(null);
            Owned = true;
            return Task.FromResult<CharacterOwnershipToken?>(new(scope, 1, incarnation));
        }

        public Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode,
            Guid newIncarnation, CancellationToken cancellationToken = default)
            => Task.FromResult<CharacterOwnershipToken?>(new(observed.Scope, observed.Epoch + 1, newIncarnation));

        public Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            Owned = false;
            return Task.CompletedTask;
        }
    }
}
