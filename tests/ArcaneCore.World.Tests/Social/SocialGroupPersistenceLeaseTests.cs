using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Cluster.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// The Social lease gain of <see cref="SocialGroupPersistenceFeature"/>: the groups table is read on the thread pool and applied on a later world tick, <c>_holding</c> stays false (nothing is
/// written) until then, a lease lost or a stop during the read drops the stale result, and a failed read is retried on the next sweep. Each test starts a world host that does not hold the
/// Social lease (another node does), gives the lease over and drives the sweep by hand (<see cref="SocialGroupPersistenceFeature.SyncNow"/>; the host's clock is frozen, so the world tick sweeps once only).
/// </summary>
public sealed class SocialGroupPersistenceLeaseTests
{
    private const string Node = "worker-a";
    private const string OtherNode = "other-node";

    private static GroupRecord Party(uint id, int leader, int member)
        => new(id, leader, 3, leader, 2, false, new ulong[8], [new GroupMemberRecord(leader, 0, false), new GroupMemberRecord(member, 0, false)]);

    /// <summary>An <see cref="IGroupStore"/> whose reads can be held back or made to fail, and that counts reads and writes. A read takes its snapshot when it is called, before it waits.</summary>
    private sealed class ControlledGroupStore : IGroupStore
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<uint, GroupRecord> _groups = [];
        private TaskCompletionSource? _gate;
        private int _failures;
        private int _loads;
        private int _loadsReturned;
        private int _saves;
        private int _deletes;

        public int Loads => Volatile.Read(ref _loads);

        public int LoadsReturned => Volatile.Read(ref _loadsReturned);

        public int Saves => Volatile.Read(ref _saves);

        public int Deletes => Volatile.Read(ref _deletes);

        /// <summary>Replace the stored rows (what the previous holder wrote).</summary>
        public void Seed(params GroupRecord[] groups)
        {
            lock (_lock)
            {
                _groups.Clear();
                foreach (GroupRecord group in groups)
                {
                    _groups[group.Id] = group;
                }
            }
        }

        /// <summary>From now on a read waits until <see cref="Release"/>.</summary>
        public void Hold()
        {
            lock (_lock)
            {
                _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            TaskCompletionSource? gate;
            lock (_lock)
            {
                gate = _gate;
                _gate = null;
            }

            gate?.SetResult();
        }

        public void FailNext(int reads)
        {
            lock (_lock)
            {
                _failures = reads;
            }
        }

        public async Task<IReadOnlyList<GroupRecord>> LoadGroupsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<GroupRecord> snapshot;
            TaskCompletionSource? gate;
            bool fail = false;
            lock (_lock)
            {
                _loads++;
                snapshot = [.. _groups.Values.OrderBy(g => g.Id)];
                gate = _gate;
                if (_failures > 0)
                {
                    _failures--;
                    fail = true;
                }
            }

            if (fail)
            {
                throw new IOException("the groups table cannot be read");
            }

            if (gate is not null)
            {
                await gate.Task.ConfigureAwait(false);
            }

            Interlocked.Increment(ref _loadsReturned);
            return snapshot;
        }

        public Task SaveGroupAsync(GroupRecord group, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _saves++;
                _groups[group.Id] = group;
            }

            return Task.CompletedTask;
        }

        public Task DeleteGroupAsync(uint groupId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _deletes++;
                _groups.Remove(groupId);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A clock that never moves: the once-a-second sweep of the world tick runs on the first tick only.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InMemoryRealmAuthorityLeaseStore _leases;
        private readonly Guid _incarnation;
        private readonly Guid _otherIncarnation;
        private readonly long _otherToken;

        private Fixture(
            InMemoryRealmAuthorityLeaseStore leases, Guid incarnation, Guid otherIncarnation, long otherToken, RealmAuthorityLeaseKeeper keeper, ControlledGroupStore store, PresenceHolderService holder)
        {
            _leases = leases;
            _incarnation = incarnation;
            _otherIncarnation = otherIncarnation;
            _otherToken = otherToken;
            Keeper = keeper;
            Store = store;
            Holder = holder;
        }

        public RealmAuthorityLeaseKeeper Keeper { get; }

        public ControlledGroupStore Store { get; }

        public PresenceHolderService Holder { get; }

        public WorldTestHost Host { get; private set; } = null!;

        public SocialGroupPersistenceFeature Persistence { get; private set; } = null!;

        /// <summary>Starts a host over an empty groups table whose Social lease is held by another node.</summary>
        public static async Task<Fixture> StartAsync()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
            var leases = new InMemoryRealmAuthorityLeaseStore();
            var incarnation = Guid.NewGuid();
            var otherIncarnation = Guid.NewGuid();
            AuthorityLeaseResult other = leases.TryAcquire(RealmAuthorityScopes.Social, OtherNode, otherIncarnation, TimeSpan.FromMinutes(10), time.GetUtcNow());
            Assert.Equal(AuthorityLeaseOutcome.Granted, other.Outcome);

            var keeper = new RealmAuthorityLeaseKeeper(
                leases,
                Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
                Microsoft.Extensions.Options.Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = Node }),
                NullLogger<RealmAuthorityLeaseKeeper>.Instance,
                lifetime: null,
                time: time,
                setExitCode: _ => { }); // not the process-global ExitCodes.Current
            await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);
            Assert.False(keeper.Holds(RealmAuthorityScopes.Social));

            var directory = new InMemoryRealmPresenceDirectory();
            var holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
            var store = new ControlledGroupStore();
            var fixture = new Fixture(leases, incarnation, otherIncarnation, other.Lease!.FencingToken, keeper, store, holder);
            fixture.Host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IGroupStore>(store);
                services.AddSingleton<TimeProvider>(new FrozenClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
                services.AddSingleton(keeper);
                services.AddSingleton(holder);
                services.AddSingleton(_ => new ClusterGroupRouting(holder, Node));
            });
            for (int id = 101; id <= 106; id++)
            {
                fixture.Host.Directory.Add(new CharacterIdentity(id, 1000 + id, "Member" + id, 1, 0, 1));
            }

            fixture.Persistence = fixture.Host.WorldServices.GetRequiredService<SocialGroupPersistenceFeature>();
            Assert.NotNull(fixture.Persistence.Writes); // the store is wired: the feature attached
            await fixture.Host.OnWorldAsync(fixture.Persistence.SyncNow); // a sweep has run: this node sees that it does not hold the lease
            return fixture;
        }

        /// <summary>The other node gives the Social lease up and this node takes it (the keeper's own start path, with the same incarnation).</summary>
        public async Task GainAsync()
        {
            AuthorityLeaseResult released = _leases.TryRelease(RealmAuthorityScopes.Social, OtherNode, _otherIncarnation, _otherToken);
            Assert.Equal(AuthorityLeaseOutcome.Released, released.Outcome);
            await Regain();
        }

        /// <summary>This node takes the lease (again, after <see cref="Keeper"/> gave it up).</summary>
        public async Task Regain()
        {
            await Keeper.AcquireAtStartAsync(_incarnation, CancellationToken.None);
            Assert.NotNull(Keeper.TokenFor(RealmAuthorityScopes.Social));
        }

        /// <summary>The ids of the groups the Social authority of this node holds, read on the world thread.</summary>
        public Task<uint[]> AuthorityGroupsAsync() => Host.OnWorldAsync(() => Holder.Groups.Groups.Select(g => g.Id).ToArray());

        public async Task WaitForAuthorityGroupsAsync(params uint[] ids)
            => await Host.WaitForWorldAsync(() => Holder.Groups.Groups.Select(g => g.Id).SequenceEqual(ids), "the authority to hold groups " + string.Join(",", ids));

        /// <summary>A world-thread sweep, as the once-a-second tick does it.</summary>
        public Task SweepAsync() => Host.OnWorldAsync(Persistence.SyncNow);

        /// <summary>Everything the world thread has been handed has run (the apply a finished read posts).</summary>
        public async Task SettleWorldAsync()
        {
            await Host.OnWorldAsync(() => { });
            await Host.OnWorldAsync(() => { });
        }

        public async ValueTask DisposeAsync()
        {
            Store.Release();
            await Host.DisposeAsync();
            await Keeper.DisposeAsync();
        }
    }

    [Fact]
    public async Task ALeaseGain_RestoresTheStoredGroupsOnALaterTick_AndWritesNothingUntilThen()
    {
        await using Fixture f = await Fixture.StartAsync();
        f.Store.Seed(Party(12, 103, 104)); // written by the previous holder
        f.Store.Hold();
        int loadsBefore = f.Store.Loads;
        await f.GainAsync();

        await f.SweepAsync();
        await WorldTestHost.WaitForAsync(() => f.Store.Loads == loadsBefore + 1, "the groups read to start");
        await f.SweepAsync(); // a second sweep while the read is out starts no second read and writes nothing
        await f.Persistence.Writes!.FlushAsync();

        Assert.Equal(loadsBefore + 1, f.Store.Loads);
        Assert.Empty(await f.AuthorityGroupsAsync()); // the world thread has not applied anything: the read is still out
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));

        f.Store.Release();
        await f.WaitForAuthorityGroupsAsync(12);
        Assert.Equal([12u], f.Persistence.Restored!.Restored);
        Assert.Empty(f.Persistence.Restored.Dropped);

        await f.SweepAsync(); // holding now: the restored group equals what was read, so it is not written again
        await f.Persistence.Writes.FlushAsync();
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));
        Assert.Equal(loadsBefore + 1, f.Store.Loads);
    }

    [Fact]
    public async Task ALeaseLostDuringTheRead_DropsTheStaleResult_AndTheNextGainReadsAgain()
    {
        await using Fixture f = await Fixture.StartAsync();
        f.Store.Seed(Party(12, 103, 104));
        f.Store.Hold();
        int loadsBefore = f.Store.Loads;
        await f.GainAsync();
        await f.SweepAsync();
        await WorldTestHost.WaitForAsync(() => f.Store.Loads == loadsBefore + 1, "the groups read to start");

        // The lease goes away while the read is out, and the table changes meanwhile (another node held it and wrote).
        f.Store.Seed(Party(13, 105, 106));
        await f.Keeper.ReleaseAllAsync();
        Assert.Null(f.Keeper.TokenFor(RealmAuthorityScopes.Social));
        await f.SweepAsync(); // the sweep sees the loss: the read in flight belongs to the lease that is gone

        f.Store.Release();
        await WorldTestHost.WaitForAsync(() => f.Store.LoadsReturned == loadsBefore + 1, "the stale read to return");
        await f.SettleWorldAsync();
        await f.Persistence.Writes!.FlushAsync();

        Assert.Empty(await f.AuthorityGroupsAsync()); // the stale result (group 12) was not applied
        Assert.Empty(f.Persistence.Restored!.Restored);
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));

        // The lease comes back: a new read starts (the lost one did not leave the reload stuck) and its result, not the stale one, is applied.
        await f.Regain();
        await f.SweepAsync();
        await f.WaitForAuthorityGroupsAsync(13);
        Assert.Equal([13u], f.Persistence.Restored.Restored);
        Assert.Equal(loadsBefore + 2, f.Store.Loads);
        await f.SweepAsync();
        await f.Persistence.Writes.FlushAsync();
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));
    }

    [Fact]
    public async Task AFailedRead_IsRetriedOnTheNextSweep_AndNothingIsWrittenMeanwhile()
    {
        await using Fixture f = await Fixture.StartAsync();
        f.Store.Seed(Party(12, 103, 104));
        f.Store.FailNext(1);
        int loadsBefore = f.Store.Loads;
        await f.GainAsync();

        // The first sweep starts the read that fails; sweeps go on until the failure has been handled and the next sweep reads again.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (f.Store.Loads < loadsBefore + 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "no second read started after the first one failed");
            await f.SweepAsync();
            await Task.Delay(10);
        }

        await f.WaitForAuthorityGroupsAsync(12);
        Assert.Equal([12u], f.Persistence.Restored!.Restored);
        Assert.Equal(loadsBefore + 2, f.Store.Loads); // exactly one failed read and one that was applied
        await f.SweepAsync();
        await f.Persistence.Writes!.FlushAsync();
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));
    }

    [Fact]
    public async Task AStopDuringTheRead_AppliesNothingAndStartsNoSecondRead()
    {
        await using Fixture f = await Fixture.StartAsync();
        f.Store.Seed(Party(12, 103, 104));
        f.Store.Hold();
        int loadsBefore = f.Store.Loads;
        await f.GainAsync();
        await f.SweepAsync();
        await WorldTestHost.WaitForAsync(() => f.Store.Loads == loadsBefore + 1, "the groups read to start");

        await f.Persistence.StopAsync(); // the final sync of the stop runs with the read still out
        f.Store.Release();
        await WorldTestHost.WaitForAsync(() => f.Store.LoadsReturned == loadsBefore + 1, "the read to return");
        await f.SettleWorldAsync();

        Assert.Empty(await f.AuthorityGroupsAsync()); // the late result was not applied
        Assert.Empty(f.Persistence.Restored!.Restored);
        Assert.Equal(loadsBefore + 1, f.Store.Loads);
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));
    }

    [Fact]
    public async Task AGainAfterTheStop_StartsNoRead()
    {
        await using Fixture f = await Fixture.StartAsync();
        f.Store.Seed(Party(12, 103, 104));
        await f.Persistence.StopAsync();
        int loadsBefore = f.Store.Loads;

        await f.GainAsync();
        await f.SweepAsync();
        await f.SettleWorldAsync();

        Assert.Equal(loadsBefore, f.Store.Loads);
        Assert.Empty(await f.AuthorityGroupsAsync());
        Assert.Equal((0, 0), (f.Store.Saves, f.Store.Deletes));
    }
}
