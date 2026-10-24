using ArcaneCore.Game.Instances;
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

    private static readonly string[] AllQueueNames =
    [
        "character saves", "social", "reputation", "instances", "honor", "rest", "explored zones", "taxi routes",
        "spellbook", "talents", "skills", "creature respawns", "gm audit", "groups", "account addresses",
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
        Assert.NotNull(snapshot.LastFailure);
        Assert.Null(snapshot.FormatFailure()); // the halt text already names the cause
        Assert.False(snapshot.IsHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync()); // a halted queue reports its undrained writes on stop
    }

    [Fact]
    public async Task TheLiveWorld_ResolvesOnlyBuiltInQueueNames_EachOnce_AndMoreThanTheOriginalSeven()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        string[] names = [.. WorldPersistenceProbes.CreateRegistry(host.WorldServices).Snapshot().Select(s => s.Name)];

        Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Length);
        Assert.All(names, n => Assert.Contains(n, AllQueueNames));
        Assert.Contains("character saves", names);
        Assert.Contains("gm audit", names);
        Assert.True(names.Length > 7, $"only {names.Length} queues resolved: {string.Join(", ", names)}");
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
        string[] expected = ExpectedQueueLines(host);

        string[] lines = await LinesAsync(admin, ".arcane queues", expected.Length);

        Assert.Equal(expected, lines);
        int queueLine = Array.IndexOf(lines, "gm audit: 0 pending, RETAINED after failed writes: mute:2");
        Assert.True(queueLine >= 0, string.Join(" | ", lines));
        Assert.StartsWith("  gm audit last failure: InvalidOperationException: injected GM audit store failure", lines[queueLine + 1], StringComparison.Ordinal);

        await AuditOf(host).Writes.RetryRetainedAsync();
        string[] healthy = ExpectedQueueLines(host);
        Assert.DoesNotContain(healthy, l => l.Contains("last failure", StringComparison.Ordinal));
        Assert.Equal(healthy, await LinesAsync(admin, ".arcane queues", healthy.Length));
    }

    private static string[] ExpectedQueueLines(WorldTestHost host)
        => [.. WorldPersistenceProbes.CreateRegistry(host.WorldServices).Snapshot().SelectMany(s => s.FormatFailure() is { } failure ? new[] { s.Format(), failure } : new[] { s.Format() })];

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
