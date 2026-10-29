using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>
/// Issue #1618: <c>LogFault</c> already records into the fault ledger, so a catch block that also calls
/// <c>FaultLedger.Shared.Record</c> with the same site counted every fault twice. Each test throws a probe exception type of its own,
/// so the ledger count it reads belongs to that test alone, and asserts that one fault raises the count by exactly one.
/// </summary>
public sealed class FeatureFaultLedgerCountTests
{
    private sealed class SettlementProbeException(string message) : Exception(message);
    private sealed class ReconcileProbeException(string message) : Exception(message);
    private sealed class ReadProbeException(string message) : Exception(message);

    private static long Count<T>(string site) => FaultLedger.Shared.Snapshot()
        .Where(s => s.Site == site && s.ExceptionType == typeof(T).FullName).Sum(s => s.Count);

    [Fact]
    public async Task EconomySettlements_ACommitAndAReconcileFailure_AreEachCountedOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IEconomyStore>(_ => new ProbeEconomyStore(failCommit: true, failExpiredMails: false));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        var settlements = new EconomySettlements(scopes, NullLogger.Instance);
        settlements.Attach(world, saves, null);

        var done = new TaskCompletionSource<EconomyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Post(() => settlements.TryStart([], [], (outcome, _) => done.TrySetResult(outcome), Guid.NewGuid()));
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            world.RunTick(1);
            await Task.Delay(10);
        }

        Assert.True(done.Task.IsCompleted, "the settlement did not complete");
        Assert.Equal(1, Count<SettlementProbeException>("economy.settlement"));
        Assert.Equal(1, Count<ReconcileProbeException>("economy.settlement-reconcile"));
    }

    [Fact]
    public async Task EconomySettlements_AThrowingFinishedCallback_IsCountedOnce_UnderSettlementCallback()
    {
        var probe = new ArcaneCore.World.Tests.Playerbots.FaultCountProbe("economy.settlement-callback");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IEconomyStore>(_ => new ProbeEconomyStore(failCommit: true, failExpiredMails: false));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        var settlements = new EconomySettlements(scopes, NullLogger.Instance);
        settlements.Attach(world, saves, null);

        // The settlement fails (the store rejects the commit) and finalizes; the finished callback is the throwing part.
        int called = 0;
        world.Post(() => settlements.TryStart([], [], (_, _) =>
        {
            Interlocked.Exchange(ref called, 1);
            throw probe.Fault();
        }, Guid.NewGuid()));
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref called) == 0 && DateTime.UtcNow < deadline)
        {
            world.RunTick(1); // finalize runs inside a tick on this thread, so its catch has finished when the tick returns
            await Task.Delay(10);
        }

        Assert.True(Volatile.Read(ref called) == 1, "the finished callback never ran");
        probe.AssertCountedOnce("the settlement finished-callback catch");
    }

    [Fact]
    public async Task EconomyFeature_AFailedStoreRead_IsCountedOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IEconomyStore>(_ => new ProbeEconomyStore(failCommit: false, failExpiredMails: true));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        await using var feature = new EconomyFeature(provider, scopes, NullLogger<EconomyFeature>.Instance, null);
        feature.Options.ExpirySweepSeconds = 0;
        feature.Attach(world);
        Assert.True(feature.Enabled);

        feature.RunExpirySweep(); // the expired-mail read throws off the world thread
        await WorldTestHost.WaitForAsync(() => Count<ReadProbeException>("economy.read") >= 1, "the failed read to be recorded");
        await Task.Delay(200);
        Assert.Equal(1, Count<ReadProbeException>("economy.read"));
    }

    private sealed class ProbeEconomyStore(bool failCommit, bool failExpiredMails) : IEconomyStore
    {
        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
            => failCommit ? Task.FromException<EconomyCommitResult>(new SettlementProbeException("settlement probe")) : throw new NotSupportedException();

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new ReconcileProbeException("reconcile probe"));

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MailRecord>>([]);

        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
            => failExpiredMails ? Task.FromException<IReadOnlyList<MailRecord>>(new ReadProbeException("read probe")) : Task.FromResult<IReadOnlyList<MailRecord>>([]);

        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MailRecord>>([]);

        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuctionRecord>>([]);

        public Task<IReadOnlyDictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>> GetEscrowItemsAsync(
            IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>>(
                new Dictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>());

        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(new AuctionSnapshot([], new Dictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>()));

        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new EconomyIdSeed(0, 0, 0));
    }
}
