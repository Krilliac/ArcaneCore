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

    [Fact]
    public async Task TheHolderSweep_IsRateLimited_AndAFailureOfItNeverStopsThePass()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 2);
        world.Host.World.Ownership = new MapOwnership([new MapOwnership.Range(33, 34), new MapOwnership.Range(409, 409)], allowInstanceMaps: true);
        var accounts = (InMemoryAccountStore)world.Host.WorldServices.GetRequiredService<IAccountStore>();
        var provisions = (ManagedPlayerbotLifecycleTests.MemoryProvisionStore)world.Host.WorldServices.GetRequiredService<IManagedPlayerbotProvisionStore>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // A stale row whose account is gone: the reconcile refuses it (throws). The pass must still run its own member sweep.
        provisions.AddPendingOutOfBand(Guid.NewGuid(), 99_999, "PBGHOSTACCOUNT", now - 601);
        Guid orphan = Guid.NewGuid();
        world.Members.Seed(new PlayerbotPopulationMember(orphan, 424242, PopulationTestHost.Elwynn, 0, 100, 5000));

        await world.PassAsync();

        Assert.DoesNotContain(world.Members.Snapshot, m => m.BotId == orphan); // the member sweep ran although the provision sweep failed
        Assert.Equal(0, provisions.RollbackCount);

        // Rate limit: a stale row added now is not looked at again within the interval, and is once the interval allows.
        Account stale = await accounts.CreateAsync(new Account { Username = "PBRATELIMITED", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        provisions.AddPendingOutOfBand(Guid.NewGuid(), stale.Id, stale.Username, now - 601);
        await world.PassAsync();
        Assert.Equal(0, provisions.RollbackCount);
        world.Bots.ProvisionSweepIntervalMs = 0;
        provisions.RemovePendingOutOfBand("PBGHOSTACCOUNT"); // the unrollable row would throw before the good one
        await world.PassAsync();
        Assert.Equal(1, provisions.RollbackCount);
    }
}
