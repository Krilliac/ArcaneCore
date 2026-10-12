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
    public async Task PublishesTheClaimAsTheAmbientTokenBeforeFeaturesAttach_AndClearsItOnRelease()
    {
        await using TestKit kit = TestKit.Create(enabled: true);
        Assert.Null(kit.Context.Current);

        await kit.Host.StartAsync(CancellationToken.None);
        Assert.NotNull(kit.Context.Current);
        Assert.Equal(kit.Feature.TokenAtAttach, kit.Context.Current);
        Assert.Equal("world", kit.Context.Current!.Scope);

        await kit.Host.StopAsync(CancellationToken.None);
        Assert.Null(kit.Context.Current);
    }

    [Fact]
    public async Task DisabledFencingLeavesTheAmbientTokenEmpty()
    {
        await using TestKit kit = TestKit.Create(enabled: false);
        await kit.Host.StartAsync(CancellationToken.None);

        Assert.Null(kit.Context.Current);
        Assert.Null(kit.Feature.TokenAtAttach);
    }

    [Fact]
    public async Task FailedDrainKeepsTheAmbientTokenSoLaterWritesStayFenced()
    {
        await using TestKit kit = TestKit.Create(enabled: true);
        kit.Store.SaveOverride = _ => Task.FromException(new CharacterOwnershipRejectedException("world", 1));
        await kit.Host.StartAsync(CancellationToken.None);
        kit.SaveQueue.Enqueue(new CharacterState(1, 0, 12, 0, 0, 83.5f, 0, 1, 0));

        await Assert.ThrowsAsync<AggregateException>(() => kit.Host.StopAsync(CancellationToken.None));
        Assert.NotNull(kit.Context.Current);
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
    public async Task UnfencedWorld_StopsThroughTheTerminalPath_WhenAFencedWorldClaimsTheDatabase()
    {
        await using TestKit kit = TestKit.Create(enabled: false, claimWatchInterval: TimeSpan.FromMilliseconds(20));
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.SaveQueue.FatalFailure += error => fatal.TrySetResult(error);
        await kit.Host.StartAsync(CancellationToken.None);

        // Unclaimed: the watch reads the row (once per interval) and the world keeps running.
        await Task.Delay(150);
        Assert.False(fatal.Task.IsCompleted);
        Assert.True(kit.Ownership.IsOwnedCalls >= 2, $"polled {kit.Ownership.IsOwnedCalls} times");

        kit.Ownership.Owned = true; // a fenced world claims the characters database
        Exception error = await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("claimed by a fenced world", error.Message);
        Assert.Throws<InvalidOperationException>(() => kit.SaveQueue.Enqueue(new CharacterState(1, 0, 12, 0, 0, 83.5f, 0, 1, 0)));
    }

    [Fact]
    public async Task UnfencedWorld_ReadsTheOwnershipRowOncePerInterval_NotPerSave_AndAFencedWorldNeverPolls()
    {
        await using TestKit slow = TestKit.Create(enabled: false, claimWatchInterval: TimeSpan.FromHours(1));
        await slow.Host.StartAsync(CancellationToken.None);
        int atStart = slow.Ownership.IsOwnedCalls; // the startup refusal check
        for (int i = 1; i <= 20; i++)
        {
            slow.SaveQueue.Enqueue(new CharacterState(i, 0, 12, 0, 0, 83.5f, 0, 1, 0));
            await slow.SaveQueue.FlushCharacterAsync(i);
        }

        Assert.Equal(atStart, slow.Ownership.IsOwnedCalls);

        await using TestKit fenced = TestKit.Create(enabled: true, claimWatchInterval: TimeSpan.FromMilliseconds(10));
        await fenced.Host.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, fenced.Ownership.IsOwnedCalls);
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
            InMemoryCharacterStore store, CharacterOwnershipContext context)
        {
            _provider = provider;
            _world = world;
            SaveQueue = saveQueue;
            Host = host;
            Ownership = ownership;
            Feature = feature;
            Store = store;
            Context = context;
        }

        public WorldHost Host { get; }
        public CharacterSaveQueue SaveQueue { get; }
        public RecordingOwnership Ownership { get; }
        public RecordingFeature Feature { get; }
        public InMemoryCharacterStore Store { get; }
        public CharacterOwnershipContext Context { get; }

        public static TestKit Create(bool enabled, TimeSpan? claimWatchInterval = null)
        {
            var store = new InMemoryCharacterStore();
            var ownership = new RecordingOwnership();
            var context = new CharacterOwnershipContext();
            var feature = new RecordingFeature(context);
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
                NullLogger<WorldHost>.Instance, options, ownershipContext: context) { ClaimWatchInterval = claimWatchInterval };
            return new TestKit(provider, world, saveQueue, host, ownership, feature, store, context);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(CancellationToken.None); }
            catch (Exception) { }
            _world.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed class RecordingFeature(CharacterOwnershipContext context) : IWorldFeature
    {
        public bool AttachedWithToken { get; private set; }
        public CharacterOwnershipToken? TokenAtAttach { get; private set; }

        public void Attach(WorldRuntime world)
        {
            AttachedWithToken = world.CharacterOwnership is not null;
            TokenAtAttach = context.Current;
        }
    }

    private sealed class RecordingOwnership : ICharacterOwnershipStore
    {
        public bool Owned { get; set; }
        public int AcquireCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        private int _isOwnedCalls;
        public int IsOwnedCalls => Volatile.Read(ref _isOwnedCalls);

        public Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _isOwnedCalls);
            return Task.FromResult(Owned);
        }

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
