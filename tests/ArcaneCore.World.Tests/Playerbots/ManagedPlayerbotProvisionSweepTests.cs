using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Two workers of a split cluster share one provisioning journal. A create writes its journal row, then its character, then its registry row, then completes the journal row; the reconcile
/// that cleans up a dead create must never take the row of a create still in flight on another worker (it would delete that bot's character or make its completion fail). In a split cluster
/// only the maintenance holder sweeps, only rows older than <see cref="ManagedPlayerbotFeature.ProvisionSweepGrace"/>, and a failed create repairs only its own row. Combined mode sweeps all rows as before.
/// </summary>
public sealed class ManagedPlayerbotProvisionSweepTests
{
    private sealed class Shared
    {
        public InMemoryAccountStore Accounts { get; } = new();
        public InMemoryCharacterStore Characters { get; } = new();
        public ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore Owners { get; } = new();
        public ManagedPlayerbotLifecycleTests.MemoryProvisionStore Provisions { get; }

        public Shared() => Provisions = new(Accounts);

        /// <summary>A world over the shared stores. <paramref name="ownedMap"/> null is combined mode; <paramref name="nonHolder"/> adds a lease keeper that holds nothing.</summary>
        public WorldTestHost Host(uint? ownedMap, bool nonHolder = false)
        {
            WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(Accounts, Characters, Owners, Provisions,
                extraServices: nonHolder ? PopulationOwnershipTests.AddNonHolderKeeper : null);
            if (ownedMap is { } map) host.World.Ownership = new MapOwnership([new MapOwnership.Range(map, map)], allowInstanceMaps: false);
            return host;
        }

        /// <summary>An account with a journal row of the given age, as another worker's create (or a dead one) leaves it.</summary>
        public async Task<(Guid BotId, int AccountId)> JournalRowAsync(string username, long ageSeconds)
        {
            Account account = await Accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
            Guid botId = Guid.NewGuid();
            Provisions.AddPendingOutOfBand(botId, account.Id, account.Username, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ageSeconds);
            return (botId, account.Id);
        }
    }

    private static ManagedPlayerbotFeature BotsOf(WorldTestHost host) => host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

    [Fact]
    public async Task TwoWorkers_CreatingAtOnce_BothSucceed_AndNeitherRollsBackTheOther()
    {
        var shared = new Shared();
        await using WorldTestHost hostB = shared.Host(0);
        await using WorldTestHost hostA = shared.Host(1);
        TaskCompletionSource reached = shared.Owners.HoldNextCreate();
        Task<PlayerbotOperationResult> creatingB = BotsOf(hostB).CreateAsync("Parkedbee", 1, 1);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30)); // B has its journal row and character, and waits before its registry row

        PlayerbotOperationResult a = await BotsOf(hostA).CreateAsync("Activeaye", 1, 1);
        shared.Owners.ReleaseHeldCreate();
        PlayerbotOperationResult b = await creatingB;

        Assert.True(a.Success, "worker A: " + a.Code);
        Assert.True(b.Success, "worker B: " + b.Code); // not create-failed: provision-completion-refused
        Assert.Equal(0, shared.Provisions.RollbackCount);
        Assert.Empty(await shared.Provisions.LoadPendingAsync());
        Assert.Equal(2, (await shared.Owners.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task TwoWorkers_CreatingAtOnce_WhenTheOtherHasItsRegistryRowButNotItsCompletion_BothStillSucceed()
    {
        // The other window: B stored its registry row and has not yet completed its journal row. A's sweep must not complete B's row for it (B's own completion would then be refused).
        var shared = new Shared();
        await using WorldTestHost hostB = shared.Host(0);
        await using WorldTestHost hostA = shared.Host(1);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shared.Owners.AfterCreate = async _ =>
        {
            shared.Owners.AfterCreate = null; // only the first registration parks
            reached.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(60));
        };
        Task<PlayerbotOperationResult> creatingB = BotsOf(hostB).CreateAsync("Parkedbee", 1, 1);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        PlayerbotOperationResult a = await BotsOf(hostA).CreateAsync("Activeaye", 1, 1);
        release.TrySetResult();
        PlayerbotOperationResult b = await creatingB;

        Assert.True(a.Success, "worker A: " + a.Code);
        Assert.True(b.Success, "worker B: " + b.Code);
        Assert.Equal(0, shared.Provisions.RollbackCount);
        Assert.Empty(await shared.Provisions.LoadPendingAsync());
    }

    [Fact]
    public async Task ASplitHolder_Creating_LeavesAYoungForeignJournalRowAlone()
    {
        var shared = new Shared();
        (Guid foreign, _) = await shared.JournalRowAsync("PBYOUNGFOREIGN", ageSeconds: 5);
        await using WorldTestHost host = shared.Host(0);

        PlayerbotOperationResult result = await BotsOf(host).CreateAsync("Youngcreate", 1, 1);

        Assert.True(result.Success, result.Code);
        Assert.Equal(0, shared.Provisions.RollbackCount);
        Assert.Equal(foreign, Assert.Single(await shared.Provisions.LoadPendingAsync()).BotId);
    }

    [Fact]
    public async Task ASplitHolder_Creating_SweepsAJournalRowOlderThanTheGrace()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBSTALEROW", ageSeconds: 601);
        await using WorldTestHost host = shared.Host(0);

        PlayerbotOperationResult result = await BotsOf(host).CreateAsync("Stalecreate", 1, 1);

        Assert.True(result.Success, result.Code);
        Assert.Equal(1, shared.Provisions.RollbackCount);
        Assert.Empty(await shared.Provisions.LoadPendingAsync());
    }

    [Fact]
    public async Task ASplitNonHolder_Creating_SweepsNothing()
    {
        var shared = new Shared();
        (Guid stale, _) = await shared.JournalRowAsync("PBSTALENONHOLDER", ageSeconds: 601);
        await using WorldTestHost host = shared.Host(0, nonHolder: true);

        PlayerbotOperationResult result = await BotsOf(host).CreateAsync("Nonholdercr", 1, 1);

        Assert.True(result.Success, result.Code);
        Assert.Equal(0, shared.Provisions.RollbackCount);
        Assert.Equal(stale, Assert.Single(await shared.Provisions.LoadPendingAsync()).BotId);
    }

    [Fact]
    public async Task ASplitHolder_AtStartup_SweepsTheStaleRows_AndKeepsTheYoungOnes()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBSTARTSTALE", ageSeconds: 601);
        (Guid young, _) = await shared.JournalRowAsync("PBSTARTYOUNG", ageSeconds: 30);
        await using WorldTestHost host = shared.Host(0);

        await BotsOf(host).StartupAsync(default);

        Assert.Equal(1, shared.Provisions.RollbackCount);
        Assert.Equal(young, Assert.Single(await shared.Provisions.LoadPendingAsync()).BotId);
    }

    [Fact]
    public async Task ASplitCreateThatFails_RollsBackItsOwnRow_AndKeepsAYoungForeignRow()
    {
        var shared = new Shared();
        (Guid foreign, int foreignAccount) = await shared.JournalRowAsync("PBFAILFOREIGN", ageSeconds: 5);
        shared.Owners.FailNextCreate = true;
        await using WorldTestHost host = shared.Host(0);

        PlayerbotOperationResult result = await BotsOf(host).CreateAsync("Failedcreate", 1, 1);

        Assert.False(result.Success);
        Assert.Equal(1, shared.Provisions.RollbackCount);
        Assert.Equal(foreign, Assert.Single(await shared.Provisions.LoadPendingAsync()).BotId);
        Assert.Empty(await shared.Owners.LoadAllAsync());
        int ownAccount = foreignAccount + 1; // the account the create made after the foreign one
        Assert.Empty(await shared.Characters.GetByAccountAsync(ownAccount));
    }

    [Fact]
    public async Task CombinedMode_Creating_StillSweepsEveryJournalRow()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBCOMBINEDYOUNG", ageSeconds: 5);
        await using WorldTestHost host = shared.Host(null);

        PlayerbotOperationResult result = await BotsOf(host).CreateAsync("Combinedcr", 1, 1);

        Assert.True(result.Success, result.Code);
        Assert.Equal(1, shared.Provisions.RollbackCount);
        Assert.Empty(await shared.Provisions.LoadPendingAsync());
    }

    [Fact]
    public void TheSweepFilter_CoversARowAtTheGraceAge_AndNotAYoungerOne()
    {
        const long now = 1_800_000_000;
        var sweep = ManagedPlayerbotFeature.ProvisionSweep.StaleAt(now);
        ManagedPlayerbotProvision At(long created) => new(Guid.NewGuid(), 1, "PBX", created);
        long grace = (long)ManagedPlayerbotFeature.ProvisionSweepGrace.TotalSeconds;

        Assert.Equal(600, grace);
        Assert.True(sweep.Covers(At(now - 600)));
        Assert.False(sweep.Covers(At(now - 599)));
        Assert.True(sweep.Covers(At(now - 601)));
        Guid one = Guid.NewGuid();
        Assert.True(ManagedPlayerbotFeature.ProvisionSweep.Only(one).Covers(new ManagedPlayerbotProvision(one, 1, "PBX", now)));
        Assert.False(ManagedPlayerbotFeature.ProvisionSweep.Only(one).Covers(At(now - 5000)));
        Assert.True(ManagedPlayerbotFeature.ProvisionSweep.All.Covers(At(now)));
    }

    [Fact]
    public async Task AHolderThatNeverCreates_SweepsAStaleJournalRowOnItsPass_AndKeepsAYoungOne()
    {
        // The instances worker owns no map bots stand on, so it never creates; as the holder its population pass still takes what a dead worker left.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 2);
        world.Host.World.Ownership = new MapOwnership([new MapOwnership.Range(33, 34), new MapOwnership.Range(409, 409)], allowInstanceMaps: true);
        var accounts = (InMemoryAccountStore)world.Host.WorldServices.GetRequiredService<IAccountStore>();
        var provisions = (ManagedPlayerbotLifecycleTests.MemoryProvisionStore)world.Host.WorldServices.GetRequiredService<IManagedPlayerbotProvisionStore>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Account stale = await accounts.CreateAsync(new Account { Username = "PBIDLESTALE", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        Account young = await accounts.CreateAsync(new Account { Username = "PBIDLEYOUNG", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        provisions.AddPendingOutOfBand(Guid.NewGuid(), stale.Id, stale.Username, now - 601);
        Guid youngBot = Guid.NewGuid();
        provisions.AddPendingOutOfBand(youngBot, young.Id, young.Username, now - 5);

        PopulationStatus status = await world.PassAsync();

        Assert.Equal("idle", status.State);
        Assert.Equal(1, provisions.RollbackCount);
        Assert.Equal(youngBot, Assert.Single(await provisions.LoadPendingAsync()).BotId);
    }

    private static async Task<(PopulationTestHost World, ManagedPlayerbotLifecycleTests.MemoryProvisionStore Provisions, InMemoryAccountStore Accounts)> IdleHolderAsync()
    {
        PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 2);
        world.Host.World.Ownership = new MapOwnership([new MapOwnership.Range(33, 34), new MapOwnership.Range(409, 409)], allowInstanceMaps: true);
        return (world,
            (ManagedPlayerbotLifecycleTests.MemoryProvisionStore)world.Host.WorldServices.GetRequiredService<IManagedPlayerbotProvisionStore>(),
            (InMemoryAccountStore)world.Host.WorldServices.GetRequiredService<IAccountStore>());
    }

    private static async Task<Guid> StaleRowAsync(ManagedPlayerbotLifecycleTests.MemoryProvisionStore provisions, InMemoryAccountStore accounts, string username, long ageSeconds)
    {
        Account account = await accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        Guid botId = Guid.NewGuid();
        provisions.AddPendingOutOfBand(botId, account.Id, account.Username, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ageSeconds);
        return botId;
    }

    [Fact]
    public async Task TheHolderSweep_RunsOncePerInterval_AndAgainOnceTheIntervalAllows()
    {
        (PopulationTestHost world, var provisions, var accounts) = await IdleHolderAsync();
        await using PopulationTestHost owned = world;
        int baseline = provisions.LoadPendingCalls;

        await world.PassAsync();
        Assert.Equal(baseline + 1, provisions.LoadPendingCalls); // the first pass sweeps
        await StaleRowAsync(provisions, accounts, "PBRATEONE", 601);
        await world.PassAsync();
        Assert.Equal(baseline + 1, provisions.LoadPendingCalls); // inside the interval: no load at all
        Assert.Equal(0, provisions.RollbackCount);

        world.Bots.ProvisionSweepIntervalMs = 0;
        await world.PassAsync();
        Assert.Equal(baseline + 2, provisions.LoadPendingCalls);
        Assert.Equal(1, provisions.RollbackCount);
    }

    [Fact]
    public async Task AHeldOperationLock_SkipsTheHolderSweep_WithoutBlockingThePass_AndTheNextPassSweeps()
    {
        (PopulationTestHost world, var provisions, var accounts) = await IdleHolderAsync();
        await using PopulationTestHost owned = world;
        await StaleRowAsync(provisions, accounts, "PBHELDLOCK", 601);
        int baseline = provisions.LoadPendingCalls;

        using (await world.Bots.HoldOperationForTestsAsync())
        {
            await world.PassAsync().WaitAsync(TimeSpan.FromSeconds(20)); // a sweep that waited for the lock would hang the pass here
            Assert.Equal(baseline, provisions.LoadPendingCalls);
            Assert.Equal(0, provisions.RollbackCount);
        }

        await world.PassAsync(); // the skipped sweep did not use up the interval
        Assert.Equal(1, provisions.RollbackCount);
    }

    [Fact]
    public async Task ARowThatCannotBeRolledBack_FailsNeitherThePass_NorTheRowsBehindIt()
    {
        (PopulationTestHost world, var provisions, var accounts) = await IdleHolderAsync();
        await using PopulationTestHost owned = world;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The oldest row's account is gone (the reconcile refuses it): it is first in the sweep, and the good stale row behind it must still be taken.
        provisions.AddPendingOutOfBand(Guid.NewGuid(), 99_999, "PBGHOSTACCOUNT", now - 5000);
        await StaleRowAsync(provisions, accounts, "PBBEHINDGHOST", 601);
        Guid orphan = Guid.NewGuid();
        world.Members.Seed(new PlayerbotPopulationMember(orphan, 424242, PopulationTestHost.Elwynn, 0, 100, 5000));

        await world.PassAsync();

        Assert.Equal(1, provisions.RollbackCount); // the row behind the ghost was rolled back
        Assert.DoesNotContain(world.Members.Snapshot, m => m.BotId == orphan); // and the pass went on to its member sweep
        Assert.Single(await provisions.LoadPendingAsync()); // the ghost row stays pending
    }

    [Fact]
    public async Task ASplitCreate_WithAnUnrollableStaleRowAhead_StillSucceeds()
    {
        var shared = new Shared();
        shared.Provisions.AddPendingOutOfBand(Guid.NewGuid(), 99_999, "PBGHOSTCREATE", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5000);
        await using WorldTestHost host = shared.Host(0);

        PlayerbotOperationResult result = await BotsOf(host).CreateAsync("Aheadcreate", 1, 1);

        Assert.True(result.Success, result.Code);
    }

    [Fact]
    public async Task ASplitHolder_Creating_SweepsOncePerInterval_NotOncePerCreate()
    {
        var shared = new Shared();
        shared.Provisions.AddPendingOutOfBand(Guid.NewGuid(), 99_999, "PBGHOSTRATE", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5000); // a stale row that can never be deleted
        await using WorldTestHost host = shared.Host(0);
        ManagedPlayerbotFeature bots = BotsOf(host);

        Assert.True((await bots.CreateAsync("Ratefirst", 1, 1)).Success);
        int afterFirst = shared.Provisions.LoadPendingCalls;
        Assert.True((await bots.CreateAsync("Ratesecond", 1, 1)).Success);
        Assert.Equal(afterFirst, shared.Provisions.LoadPendingCalls); // inside the interval: the second create did not sweep

        bots.ProvisionSweepIntervalMs = 0;
        Assert.True((await bots.CreateAsync("Ratethird", 1, 1)).Success);
        Assert.True(shared.Provisions.LoadPendingCalls > afterFirst); // the interval allows it again
        Assert.Single(await shared.Provisions.LoadPendingAsync()); // the ghost row is still pending
    }

    [Fact]
    public async Task TheStaleSweep_IsSplitOnly_AndHolderOnly()
    {
        var combined = new Shared();
        await combined.JournalRowAsync("PBSWEEPCOMB", ageSeconds: 5000);
        await using WorldTestHost combinedHost = combined.Host(null);
        var nonHolder = new Shared();
        await nonHolder.JournalRowAsync("PBSWEEPNONH", ageSeconds: 5000);
        await using WorldTestHost nonHolderHost = nonHolder.Host(0, nonHolder: true);

        await BotsOf(combinedHost).SweepStaleProvisionsAsync(default);
        await BotsOf(nonHolderHost).SweepStaleProvisionsAsync(default);

        Assert.Equal(0, combined.Provisions.LoadPendingCalls); // combined mode sweeps at startup and on create only
        Assert.Equal(0, nonHolder.Provisions.LoadPendingCalls); // a non-holder leaves the repair to the holder
        Assert.Equal(0, combined.Provisions.RollbackCount + nonHolder.Provisions.RollbackCount);
    }

    [Fact]
    public async Task TheStaleSweep_OfASplitHolder_DoesRun_SoTheNegativeControlsAboveCanFail()
    {
        // Positive control for TheStaleSweep_IsSplitOnly_AndHolderOnly: the same stale row, a split holder that does sweep.
        var holder = new Shared();
        await holder.JournalRowAsync("PBSWEEPHOLD", ageSeconds: 5000);
        await using WorldTestHost holderHost = holder.Host(0);

        await BotsOf(holderHost).SweepStaleProvisionsAsync(default);

        Assert.Equal(1, holder.Provisions.LoadPendingCalls);
        Assert.Equal(1, holder.Provisions.RollbackCount);
        Assert.Empty(await holder.Provisions.LoadPendingAsync());
    }

    [Fact]
    public async Task TheStaleSweep_UnderALatchedSplitBrain_IssuesNoWrites_AndRunsAgainOnceItIsCleared()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBSWEEPLATCH", ageSeconds: 5000);
        await using WorldTestHost host = shared.Host(0);
        ManagedPlayerbotFeature bots = BotsOf(host);
        await bots.ForceSplitBrainForTests();

        await bots.SweepStaleProvisionsAsync(default);

        Assert.Equal(0, shared.Provisions.LoadPendingCalls);
        Assert.Equal(0, shared.Provisions.RollbackCount);
        await bots.ClearSplitBrainAsync();
        await bots.SweepStaleProvisionsAsync(default); // the refused sweep did not use up the interval
        Assert.Equal(1, shared.Provisions.RollbackCount);
    }

    [Fact]
    public async Task ASweep_LatchedBetweenItsUnlockedCheckAndTheLock_IssuesNoWrites_AndDoesNotUseUpTheInterval()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBSWEEPRECHK", ageSeconds: 5000);
        await using WorldTestHost host = shared.Host(0);
        ManagedPlayerbotFeature bots = BotsOf(host);
        bots.SweepAfterUnlockedCheckForTests = () => bots.ForceSplitBrainForTests(); // the latch lands after the unlocked check passed: only the check under the lock can see it

        await bots.SweepStaleProvisionsAsync(default);

        Assert.Equal(0, shared.Provisions.LoadPendingCalls);
        Assert.Equal(0, shared.Provisions.RollbackCount);
        bots.SweepAfterUnlockedCheckForTests = null;
        await bots.ClearSplitBrainAsync();
        await bots.SweepStaleProvisionsAsync(default); // the refused sweep did not stamp the slot
        Assert.Equal(1, shared.Provisions.RollbackCount);
    }

    [Fact]
    public async Task ASweep_StampsTheSlotWithTheClockReadUnderTheLock_NotTheOneReadBeforeTheWait()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBSWEEPCLOCK", ageSeconds: 5000);
        await using WorldTestHost host = shared.Host(0);
        ManagedPlayerbotFeature bots = BotsOf(host);
        long clock = 1_000;
        bots.ProvisionSweepClock = () => Interlocked.Read(ref clock);
        bots.SweepAfterUnlockedCheckForTests = () =>
        {
            Interlocked.Add(ref clock, 5_000); // time passes while the sweep waits for the lock
            return Task.CompletedTask;
        };

        await bots.SweepStaleProvisionsAsync(default);

        Assert.Equal(1, shared.Provisions.RollbackCount); // it ran
        bots.SweepAfterUnlockedCheckForTests = null;
        long interval = bots.ProvisionSweepIntervalMs;
        Interlocked.Exchange(ref clock, 6_000 + interval - 1);
        Assert.False(bots.TakeProvisionSweepSlot()); // stamped at 6000, not 1000: the interval is not over yet
        Interlocked.Exchange(ref clock, 6_000 + interval);
        Assert.True(bots.TakeProvisionSweepSlot());
    }

    [Fact]
    public async Task ASweepCancelledDuringTheReconcile_Propagates_ReleasesTheLock_AndUsesUpTheInterval()
    {
        var shared = new Shared();
        await shared.JournalRowAsync("PBSWEEPCANCEL", ageSeconds: 5000);
        await using WorldTestHost host = shared.Host(0);
        ManagedPlayerbotFeature bots = BotsOf(host);
        using var cancel = new CancellationTokenSource();
        // The token is live when the sweep takes the lock and stamps the interval; it is cancelled inside the reconcile (the catch's cancellation filter is what lets it through).
        shared.Provisions.OnLoadPending = ct =>
        {
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bots.SweepStaleProvisionsAsync(cancel.Token));
        Assert.Equal(1, shared.Provisions.LoadPendingCalls);
        Assert.Equal(0, shared.Provisions.RollbackCount);
        shared.Provisions.OnLoadPending = null;

        // The stamp came before the reconcile, so the cancelled run used up the interval: the next sweep inside it does nothing (and the lock was released, or it would hang or skip for that reason).
        await bots.SweepStaleProvisionsAsync(default);
        Assert.Equal(1, shared.Provisions.LoadPendingCalls);
        Assert.Equal(0, shared.Provisions.RollbackCount);

        bots.ProvisionSweepIntervalMs = 0;
        await bots.SweepStaleProvisionsAsync(default);
        Assert.Equal(1, shared.Provisions.RollbackCount);
    }
}
