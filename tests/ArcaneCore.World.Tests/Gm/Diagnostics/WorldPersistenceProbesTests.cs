using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Pets;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Persistence;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.World.Gm.Diagnostics;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Tests.Honor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Gm.Diagnostics;

/// <summary>The built-in probe wiring: each queue kind reports its pending count, retained writes and failures through the registry.</summary>
public sealed class WorldPersistenceProbesTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>The queues the live test host resolves, in registration order (creature respawns, groups and account addresses belong to features the test host does not load): a new persistence queue must be added here, and to WorldPersistenceProbes, on purpose.</summary>
    private static readonly string[] LiveQueueNames =
    [
        "character saves", "social", "reputation", "instances", "honor", "quests and taxi mask", "spell state", "rest",
        "explored zones", "taxi routes", "spellbook", "talents", "skills", "pets", "gm audit", "account data",
    ];

    [Fact]
    public async Task ACharacterKeyedQueue_ReportsItsRetainedCharacters_AsCharIds_UntilTheWriteLands()
    {
        var store = new MemoryHonorStore { LockedWrites = 3 }; // the three inline attempts fail, the write is retained
        await using ServiceProvider storeProvider = new ServiceCollection().AddSingleton<IHonorStore>(store).BuildServiceProvider();
        var queue = new HonorWriteQueue(storeProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        await using ServiceProvider services = new ServiceCollection().AddSingleton(queue).BuildServiceProvider();
        queue.Start();
        queue.AppendCp(7, new HonorCpRecord(4, 1, 10f, 100, (byte)HonorKind.Honorable));
        await queue.FlushAsync().WaitAsync(Budget);

        PersistenceQueueSnapshot retained = Assert.Single(WorldPersistenceProbes.CreateRegistry(services).Snapshot());

        Assert.Equal("honor: 0 pending, RETAINED after failed writes: char:7", retained.Format());
        Assert.False(retained.IsHealthy);

        Assert.True(queue.PostRetainedSweepForTest());
        await queue.FlushAsync().WaitAsync(Budget);

        PersistenceQueueSnapshot recovered = Assert.Single(WorldPersistenceProbes.CreateRegistry(services).Snapshot());
        Assert.Equal("honor: 0 pending", recovered.Format());
        Assert.True(recovered.IsHealthy);
        await queue.StopAsync();
    }

    [Fact]
    public async Task AQueueThatFailedTerminally_IsReportedAsHalted_WithTheCause()
    {
        await using ServiceProvider storeProvider = new ServiceCollection().AddSingleton<IInstanceStore>(new RejectingInstanceStore()).BuildServiceProvider();
        var queue = new InstanceWriteQueue(storeProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        var halted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.FatalFailure += ex => halted.TrySetResult(ex);
        await using ServiceProvider services = new ServiceCollection().AddSingleton(queue).BuildServiceProvider();
        queue.Start();
        queue.PlayerBound(5, 100, permanent: true);
        await halted.Task.WaitAsync(Budget);

        PersistenceQueueSnapshot snapshot = Assert.Single(WorldPersistenceProbes.CreateRegistry(services).Snapshot());

        Assert.Equal("instances", snapshot.Name);
        Assert.StartsWith("instances: ", snapshot.Format(), StringComparison.Ordinal);
        Assert.Contains(", HALTED: CharacterOwnershipRejectedException: ", snapshot.Format(), StringComparison.Ordinal);
        Assert.NotNull(snapshot.RetainedFailure);
        Assert.Null(snapshot.FormatFailure()); // the halt text already names the cause
        Assert.False(snapshot.IsHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync()); // a halted queue reports its undrained writes on stop
    }

    [Fact]
    public async Task TheLiveWorld_ResolvesExactlyTheListedQueues_InOrder()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        string[] names = [.. WorldPersistenceProbes.CreateRegistry(host.WorldServices).Snapshot().Select(s => s.Name)];

        Assert.Equal(LiveQueueNames, names);
    }

    [Fact]
    public async Task EveryQueueTheBarrierFlushes_HasAProbe()
    {
        // The barrier and the probes each look their queues up through the service provider, so recording what each asks for
        // derives the barrier's queue set from the barrier itself: a flush step added without a probe fails here.
        var barrierLookups = new RecordingServices();
        CharacterPersistenceFlushResult flushed = await new CharacterPersistenceBarrier(barrierLookups).TryFlushCharacterAsync(1);
        Assert.True(flushed.IsDurable);

        var probeLookups = new RecordingServices();
        Assert.Empty(WorldPersistenceProbes.CreateRegistry(probeLookups).Snapshot());

        // Not queues: the sets of economy-style settlement barriers and of externally registered participants.
        HashSet<Type> notQueues = [typeof(IEnumerable<ICharacterSettlementBarrier>), typeof(IEnumerable<ICharacterPersistenceParticipant>)];
        Type[] queueLookups = [.. barrierLookups.Requested.Where(t => !notQueues.Contains(t))];

        Assert.True(queueLookups.Length >= 13, "the barrier looked up " + queueLookups.Length + " types; it should name the world's queues");
        Assert.Contains(typeof(SummonService), queueLookups);
        Assert.Empty(queueLookups.Where(t => !probeLookups.Requested.Contains(t)).Select(t => t.Name));
    }

    [Fact]
    public async Task TheLiveQueuesThatCannotReportAField_PrintNaForIt_InsteadOfLookingHealthy()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        Dictionary<string, PersistenceQueueSnapshot> byName = WorldPersistenceProbes.CreateRegistry(host.WorldServices).Snapshot().ToDictionary(s => s.Name);

        Assert.All(new[] { "character saves", "quests and taxi mask", "spell state", "social" }, n =>
        {
            Assert.NotNull(byName[n].RetainedKeys);
            Assert.True(byName[n].TerminalKnown, n);
        });
        Assert.Contains(", halted n/a", byName["reputation"].Format(), StringComparison.Ordinal);
        Assert.Contains(", retained n/a", byName["rest"].Format(), StringComparison.Ordinal);
        Assert.Contains(", retained n/a", byName["skills"].Format(), StringComparison.Ordinal);
        Assert.False(byName["reputation"].IsHealthy);
        Assert.False(byName["rest"].IsHealthy);
    }

    [Fact]
    public async Task ArcaneQueues_ShowsAKeyedQueueFailureLine_UnderItsQueue_ThenClearsOnceItLands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("STAFF", "Staffer", AccountSecurity.Administrator);
        await admin.CollectAsync();
        StoreOf(host).FailNext(3);
        AuditOf(host).Mute(2, 600, "Staffer", AccountSecurity.Administrator, "r");
        await AuditOf(host).Writes.FlushAsync();

        string[] lines = await LinesAsync(admin, ".arcane queues", LiveQueueNames.Length + 1);

        Assert.Equal(LiveQueueNames, lines.Where(l => !l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]));
        int queueLine = Array.IndexOf(lines, "gm audit: 0 pending, RETAINED after failed writes: mute:2, halted n/a");
        Assert.True(queueLine >= 0, string.Join(" | ", lines));
        Assert.StartsWith("  gm audit retained failure: InvalidOperationException: injected GM audit store failure", lines[queueLine + 1], StringComparison.Ordinal);

        await AuditOf(host).Writes.RetryRetainedAsync();
        string[] healthy = await LinesAsync(admin, ".arcane queues", LiveQueueNames.Length);
        Assert.DoesNotContain(healthy, l => l.Contains("retained failure", StringComparison.Ordinal) || l.Contains("RETAINED", StringComparison.Ordinal));
        Assert.Contains("gm audit: 0 pending, halted n/a", healthy);
    }

    /// <summary>A provider that has nothing registered and remembers every type asked for (collections come back empty, as the DI container returns them).</summary>
    private sealed class RecordingServices : IServiceProvider
    {
        public HashSet<Type> Requested { get; } = [];

        public object? GetService(Type serviceType)
        {
            Requested.Add(serviceType);
            return serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? Array.CreateInstance(serviceType.GetGenericArguments()[0], 0)
                : null;
        }
    }

    private sealed class RejectingInstanceStore : IInstanceStore
    {
        private static Task Reject() => throw new CharacterOwnershipRejectedException("world", 1);

        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(InstanceStoreSnapshot.Empty);

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default) => Reject();

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Reject();

        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => Reject();

        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Reject();

        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Reject();

        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => Reject();

        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Reject();

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Reject();

        public Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default) => Reject();

        public Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default) => Reject();
    }
}
