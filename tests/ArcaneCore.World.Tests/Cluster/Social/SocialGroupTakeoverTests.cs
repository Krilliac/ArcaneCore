using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Characters;
using ArcaneCore.World.Tests.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Groups belong to the Social lease holder's <see cref="GroupAuthority"/>. A worker that takes the lease over at run time rebuilds the table from storage, off the world thread, and <see cref="GroupAuthority.Restore"/> drops a
/// group or member it cannot find. The character directory is per process, so the read refreshes it first, or the rows of every character created elsewhere since this worker started would be deleted.
/// </summary>
public sealed class SocialGroupTakeoverTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static GroupRecord Group(uint id, params int[] members)
        => new(id, members[0], 0, members[0], ArcaneCore.Game.Groups.Group.DefaultLootThreshold, false, new ulong[8], [.. members.Select(member => new GroupMemberRecord(member, 0, false))]);

    private static GroupCommand ConvertToRaid(int actor)
        => new(GroupCommandKind.ConvertToRaid, ObjectGuid.Player((uint)actor).Value, "Char" + actor, 0, false);

    private static GroupCommand Loot(LootMethod method)
        => new(GroupCommandKind.SetLootMethod, ObjectGuid.Player(1).Value, "Char1", 0, false, Value: (uint)method, Value2: ArcaneCore.Game.Groups.Group.DefaultLootThreshold);

    [Fact]
    public async Task Groups_of_characters_this_worker_never_heard_of_survive_the_takeover()
    {
        await using Rig rig = await Rig.CreateAsync();

        // Created on the dead holder or a peer after this worker started: stored, but not in its directory.
        Assert.Null(rig.Directory.Find(20));
        rig.StoreCharacters(20, 21, 22);
        rig.Rows.Seed(Group(1, 20, 21)); // unknown leader
        rig.Rows.Seed(Group(2, 1, 22)); // unknown member; the group would fall under two members and go

        rig.TakeOver();
        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.Empty(rig.Persistence.Restored!.Dropped);
        Assert.Equal([20, 21], rig.Rows.Group(1)!.Members.Select(m => m.CharacterId));
        Assert.Equal([1, 22], rig.Rows.Group(2)!.Members.Select(m => m.CharacterId));
        Assert.Equal(2, rig.Holder.Groups.Snapshots().Count);
        Assert.Equal(1u, rig.Holder.Groups.GetGroup(ObjectGuid.Player(21))!.Id);
        Assert.Equal(2u, rig.Holder.Groups.GetGroup(ObjectGuid.Player(22))!.Id);
    }

    [Fact]
    public async Task A_group_of_a_character_that_is_really_gone_is_still_dropped()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 99, 1)); // 99 has no row anywhere
        rig.Rows.Seed(Group(2, 3, 2)); // 3 was deleted on another worker: this worker's directory still has it
        rig.Characters.Delete(3);

        rig.TakeOver();
        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.Equal([1u, 2u], rig.Persistence.Restored!.Dropped.Order());
        Assert.Empty(rig.Rows.All());
        Assert.Null(rig.Directory.Find(3));
    }

    [Fact]
    public async Task The_directory_is_read_after_the_rows()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));

        // The rows are read while the character and its group are stored: whatever the rows name, the directory read that follows has.
        rig.Rows.HoldLoads();
        rig.TakeOver();
        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            return true;
        });
        await rig.Rows.LoadEntered.WaitAsync(Budget);
        rig.StoreCharacters(30, 31);
        rig.Rows.Seed(Group(2, 30, 31));
        rig.Rows.ReleaseLoads();

        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.Equal([30, 31], rig.Rows.Group(2)!.Members.Select(m => m.CharacterId));
    }

    [Fact]
    public async Task During_the_read_nothing_is_written_and_commands_wait_for_the_new_table()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));
        rig.Characters.HoldReads();
        rig.TakeOver();

        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            return true;
        });
        await rig.Characters.ReadHeld.WaitAsync(Budget);

        // On the table as it stands this does nothing (this worker has no groups), and the reload would have thrown the result away anyway.
        rig.Holder.Groups.Execute("worker-c", ConvertToRaid(1));
        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            Assert.False(rig.Persistence.AuthorityLoaded);
            return true;
        });
        await rig.Persistence.Writes!.FlushAsync();
        Assert.Equal(0, rig.Rows.Writes);
        Assert.False(rig.Rows.Group(1)!.IsRaid);

        rig.Characters.ReleaseReads();
        await rig.WaitUntilLoadedAsync();

        Assert.True(Assert.Single(rig.Holder.Groups.Snapshots()).IsRaid);
        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            return true;
        });
        await rig.Persistence.Writes!.FlushAsync();
        Assert.True(rig.Rows.Group(1)!.IsRaid);
    }

    [Fact]
    public async Task The_outage_backlog_of_this_worker_runs_on_the_reloaded_table()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));
        rig.Routing.Forward(ConvertToRaid(1)); // worker-b holds: the command waits in the routing queue
        Assert.Empty(rig.Holder.Groups.Snapshots());

        rig.BlockWorld(); // no tick runs, so no sync starts the read before the backlog has reached the table
        rig.TakeOver();
        await rig.Routing.FlushAsync().WaitAsync(Budget);
        rig.ReleaseWorld();
        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.True(Assert.Single(rig.Holder.Groups.Snapshots()).IsRaid);
        Assert.True(rig.Rows.Group(1)!.IsRaid);
    }

    [Fact]
    public async Task A_command_that_reaches_the_table_before_the_first_sync_is_kept()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));

        rig.TakeOver();
        rig.Holder.Groups.Execute("worker-c", ConvertToRaid(1)); // a peer's command, before any sync has started the read
        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.True(Assert.Single(rig.Holder.Groups.Snapshots()).IsRaid);
        Assert.True(rig.Rows.Group(1)!.IsRaid);
    }

    [Fact]
    public async Task A_stop_during_the_read_loses_what_was_held_installs_nothing_and_applies_nothing_afterwards()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));
        rig.Characters.HoldReads();
        rig.TakeOver();
        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            return true;
        });
        await rig.Characters.ReadHeld.WaitAsync(Budget);

        var invite = new GroupCommand(GroupCommandKind.Invite, ObjectGuid.Player(1).Value, "Char1", 0, false, TargetName: "Nobody");
        rig.Holder.Groups.Execute("worker-c", invite); // held: a table that is about to be replaced
        Assert.Equal(0, rig.Holder.Groups.PendingFor("worker-c"));

        await rig.StopPersistenceAsync();

        Assert.False(rig.Persistence.AuthorityLoaded);
        Assert.Equal(0, rig.Holder.Groups.PendingFor("worker-c")); // the held invite did not run
        rig.Holder.Groups.Execute("worker-c", invite); // the peer link may still be up: this table was never loaded, so nothing runs on it
        Assert.Equal(0, rig.Holder.Groups.PendingFor("worker-c"));
        Assert.Equal(1, rig.Holder.Groups.CommandsRefusedAfterClose);
        Assert.Equal(0, rig.Holder.Groups.HeldCommandCount);
        Assert.False(rig.Holder.Groups.TryExecute("worker-c", invite));
        Assert.Empty(rig.Holder.Groups.Snapshots());
        Assert.Equal(0, rig.Rows.Writes);
    }

    [Fact]
    public async Task A_backlog_over_one_batch_is_replayed_over_several_ticks_in_order()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));
        rig.Characters.HoldReads();
        rig.TakeOver();
        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            return true;
        });
        await rig.Characters.ReadHeld.WaitAsync(Budget);
        for (int i = 0; i < SocialGroupPersistenceFeature.ReplayBatch; i++)
        {
            rig.Holder.Groups.Execute("worker-c", Loot(LootMethod.FreeForAll));
        }

        rig.Holder.Groups.Execute("worker-c", Loot(LootMethod.RoundRobin)); // the last of the backlog, past the first batch
        rig.Characters.ReleaseReads();
        await rig.WaitUntilLoadedAsync();

        using var timeout = new CancellationTokenSource(Budget);
        while (rig.Holder.Groups.HeldCommandCount > 0)
        {
            await Task.Delay(5, timeout.Token); // the world's own ticks run the next batch
        }

        Assert.Equal((byte)LootMethod.RoundRobin, Assert.Single(rig.Holder.Groups.Snapshots()).LootMethod);
    }

    [Fact]
    public async Task A_stop_with_a_replay_under_way_runs_the_rest_of_the_backlog_and_writes_it()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.Rows.Seed(Group(1, 1, 2));
        rig.Characters.HoldReads();
        rig.TakeOver();
        await rig.OnWorld(() =>
        {
            rig.Persistence.SyncNow();
            return true;
        });
        await rig.Characters.ReadHeld.WaitAsync(Budget);

        // Each command below leaves one event for worker-c (a failed invite its answer, the loot change the update of its actor), so the event count says how many ran. 4096 commands: four batches, under the event queue's cap.
        var invite = new GroupCommand(GroupCommandKind.Invite, ObjectGuid.Player(1).Value, "Char1", 0, false, TargetName: "Nobody");
        int invites = (4 * SocialGroupPersistenceFeature.ReplayBatch) - 1;
        for (int i = 0; i < invites; i++)
        {
            rig.Holder.Groups.Execute("worker-c", invite);
        }

        rig.Holder.Groups.Execute("worker-c", Loot(LootMethod.RoundRobin)); // the last of the backlog

        Task parked = rig.ParkWorldOnceLoaded();
        rig.Characters.ReleaseReads();
        await parked.WaitAsync(Budget);
        await rig.StopParkedWorldAsync();
        Assert.True(rig.Persistence.AuthorityLoaded);
        Assert.True(rig.Holder.Groups.HeldCommandCount > 0, "the world stopped with no backlog left, so this test proves nothing");

        await rig.Persistence.StopAsync();

        Assert.Equal(0, rig.Holder.Groups.HeldCommandCount);
        Assert.Equal(invites + 1, rig.Holder.Groups.PendingFor("worker-c"));
        Assert.Equal((byte)LootMethod.RoundRobin, Assert.Single(rig.Holder.Groups.Snapshots()).LootMethod);
        Assert.Equal((byte)LootMethod.RoundRobin, rig.Rows.Group(1)!.LootMethod); // the final write has it

        // A loaded table is closed after the final write: a late peer command is refused, not acked into a table no write will cover.
        int pendingAtStop = rig.Holder.Groups.PendingFor("worker-c");
        Assert.False(rig.Holder.Groups.TryExecute("worker-c", invite));
        rig.Holder.Groups.Execute("worker-c", invite);
        Assert.Equal(1, rig.Holder.Groups.CommandsRefusedAfterClose);
        Assert.Equal(pendingAtStop, rig.Holder.Groups.PendingFor("worker-c"));
        Assert.Equal(0, rig.Holder.Groups.HeldCommandCount);
    }

    [Fact]
    public async Task A_failed_directory_read_installs_nothing_and_is_tried_again()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.StoreCharacters(20, 21);
        rig.Rows.Seed(Group(1, 20, 21));

        // The first read fails; the retry is held, so the node stays in this state however fast the retry comes.
        rig.Characters.FailNextRead();
        rig.Characters.HoldReads();
        rig.TakeOver();
        await rig.Characters.ReadFailed.WaitAsync(Budget);
        await rig.Characters.ReadHeld.WaitAsync(Budget); // the world's own once a second sync retries

        Assert.False(await rig.OnWorld(() => rig.Persistence.AuthorityLoaded));
        Assert.Empty(rig.Holder.Groups.Snapshots());
        await rig.Persistence.Writes!.FlushAsync();
        Assert.NotNull(rig.Rows.Group(1));
        Assert.Equal(0, rig.Rows.Writes);

        rig.Characters.ReleaseReads();
        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.Equal([20, 21], rig.Rows.Group(1)!.Members.Select(m => m.CharacterId));
    }

    [Fact]
    public async Task A_command_held_across_a_failed_read_is_kept_and_runs_on_the_table_the_retry_installs()
    {
        await using Rig rig = await Rig.CreateAsync();
        rig.StoreCharacters(20, 21);
        rig.Rows.Seed(Group(1, 20, 21));
        rig.Routing.Forward(ConvertToRaid(20)); // worker-b holds: it waits in the routing queue, and the table is held from attach on a node without the lease

        rig.Characters.FailNextRead();
        rig.Characters.HoldReads();
        rig.BlockWorld(); // the first tick starts the read only after the backlog has reached the table
        rig.TakeOver();
        await rig.Routing.FlushAsync().WaitAsync(Budget);
        Assert.Equal(1, rig.Holder.Groups.HeldCommandCount);
        rig.ReleaseWorld();
        await rig.Characters.ReadFailed.WaitAsync(Budget);
        await rig.Characters.ReadHeld.WaitAsync(Budget); // the retry, held

        // The failed read installed nothing and discarded nothing: the command is still waiting, and nothing ran or was written.
        Assert.False(await rig.OnWorld(() => rig.Persistence.AuthorityLoaded));
        Assert.Equal(1, rig.Holder.Groups.HeldCommandCount);
        Assert.Empty(rig.Holder.Groups.Snapshots());
        await rig.Persistence.Writes!.FlushAsync();
        Assert.Equal(0, rig.Rows.Writes);

        rig.Characters.ReleaseReads();
        await rig.WaitUntilLoadedAsync();
        await rig.Persistence.Writes!.FlushAsync();

        Assert.Equal(0, rig.Holder.Groups.HeldCommandCount);
        Assert.True(Assert.Single(rig.Holder.Groups.Snapshots()).IsRaid);
        Assert.True(rig.Rows.Group(1)!.IsRaid);
    }

    /// <summary>The group table as a worker reads it; a read can be held before it takes its rows, and writes are counted.</summary>
    private sealed class GroupRows : IGroupStore
    {
        private readonly InMemoryGroupStore _rows = new();
        private readonly TaskCompletionSource _loadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _release;
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public Task LoadEntered => _loadEntered.Task;

        public void HoldLoads() => Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseLoads() => Volatile.Read(ref _release)!.TrySetResult();

        public void Seed(GroupRecord group) => _rows.Seed(group);

        public GroupRecord? Group(uint id) => _rows.Group(id);

        public IReadOnlyList<GroupRecord> All() => _rows.All();

        public async Task<IReadOnlyList<GroupRecord>> LoadGroupsAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _release) is { } release)
            {
                _loadEntered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await _rows.LoadGroupsAsync(cancellationToken);
        }

        public Task SaveGroupAsync(GroupRecord group, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return _rows.SaveGroupAsync(group, cancellationToken);
        }

        public Task DeleteGroupAsync(uint groupId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            return _rows.DeleteGroupAsync(groupId, cancellationToken);
        }
    }

    /// <summary>A map worker (worker-a) with the real social and group features, whose Social scope is held by worker-b, which never renews it. Its directory knows characters 1 to 9.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ManualTimeProvider _time;
        private readonly ServiceProvider _services;
        private readonly ManualResetEventSlim _worldGate = new(true);
        private readonly ManualResetEventSlim _parkGate = new(true);
        private bool _worldDisposed;

        private Rig(ManualTimeProvider time, RealmAuthorityLeaseKeeper keeper, GroupRows rows, IdentityRows characters, CharacterDirectory directory, PresenceHolderService holder, ServiceProvider services)
        {
            _time = time;
            _services = services;
            Keeper = keeper;
            Rows = rows;
            Characters = characters;
            Directory = directory;
            Holder = holder;
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            Social = services.GetRequiredService<SocialFeature>();
            Persistence = new SocialGroupPersistenceFeature(services, services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Social.Attach(World);
            Persistence.Attach(World);
            World.Start();
        }

        public RealmAuthorityLeaseKeeper Keeper { get; }

        public ClusterGroupRouting Routing => _services.GetRequiredService<ClusterGroupRouting>();

        public GroupRows Rows { get; }

        public IdentityRows Characters { get; }

        public CharacterDirectory Directory { get; }

        public PresenceHolderService Holder { get; }

        public WorldRuntime World { get; }

        public SocialFeature Social { get; }

        public SocialGroupPersistenceFeature Persistence { get; }

        public static async Task<Rig> CreateAsync()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
            var leases = new InMemoryRealmAuthorityLeaseStore();
            leases.TryAcquire(RealmAuthorityScopes.Social, "worker-b", Guid.NewGuid(), TimeSpan.FromSeconds(30), time.GetUtcNow());
            var keeper = new RealmAuthorityLeaseKeeper(leases,
                Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
                Microsoft.Extensions.Options.Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = "worker-a" }),
                NullLogger<RealmAuthorityLeaseKeeper>.Instance, null, time, _ => { });
            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.False(keeper.Holds(RealmAuthorityScopes.Social));

            var directory = new CharacterDirectory();
            var characters = new IdentityRows();
            for (int id = 1; id <= 9; id++)
            {
                directory.Add(IdentityOf(id));
                characters.Put(IdentityOf(id));
            }

            var rows = new GroupRows();
            var presence = new InMemoryRealmPresenceDirectory();
            var holder = new PresenceHolderService(presence, new InMemoryClusterSocialRouter(presence));
            ServiceProvider services = new ServiceCollection()
                .AddSingleton<ISocialStore>(new InMemorySocialStore())
                .AddSingleton<IGroupStore>(rows)
                .AddSingleton<ICharacterStore>(characters)
                .AddSingleton(keeper)
                .AddSingleton(holder)
                .AddSingleton(new ClusterGroupRouting(new LeaseRoutedPresence(holder, () => keeper.Holds(RealmAuthorityScopes.Social), () => null), "worker-a"))
                .AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance))
                .AddSingleton(provider => new SocialFeature(directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, services: provider))
                .BuildServiceProvider();
            var rig = new Rig(time, keeper, rows, characters, directory, holder, services);
            await rig.Social.GuildsLoaded.WaitAsync(Budget);
            return rig;
        }

        private static CharacterIdentity IdentityOf(int id) => new(id, 100 + id, "Char" + id, (byte)Race.Human, 0, (byte)Class.Warrior, 10, 12);

        /// <summary>Another worker stored these characters: this worker's directory does not hear of it.</summary>
        public void StoreCharacters(params int[] ids)
        {
            foreach (int id in ids)
            {
                Characters.Put(IdentityOf(id));
            }
        }

        /// <summary>worker-b's lease lapses at 30 s, and the keeper's renewal tick takes it over.</summary>
        public void TakeOver()
        {
            for (int i = 0; i < 4; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(10));
            }

            Assert.True(Keeper.Holds(RealmAuthorityScopes.Social));
        }

        /// <summary>Parks the world thread inside a posted action, so no tick (and no sync) runs until <see cref="ReleaseWorld"/>.</summary>
        public void BlockWorld()
        {
            _worldGate.Reset();
            World.Post(() => _worldGate.Wait(Budget));
        }

        public void ReleaseWorld() => _worldGate.Set();

        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async Task WaitUntilLoadedAsync()
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(() =>
            {
                Persistence.SyncNow();
                return Persistence.AuthorityLoaded;
            }).WaitAsync(timeout.Token))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        /// <summary>Parks the world thread at the end of the first tick that sees the table loaded, with a replay still under way. The task completes when it is parked.</summary>
        public Task ParkWorldOnceLoaded()
        {
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _parkGate.Reset();
            void Park(uint diffMs)
            {
                if (Persistence.AuthorityLoaded)
                {
                    World.Updated -= Park;
                    parked.TrySetResult();
                    _parkGate.Wait(Budget);
                }
            }

            World.Updated += Park;
            return parked.Task;
        }

        /// <summary>Ends the parked world thread with no further tick: the stop is under way before it wakes, so the replay stays where it was.</summary>
        public async Task StopParkedWorldAsync()
        {
            Task stopping = Task.Run(World.Stop);
            await Task.Delay(100);
            _parkGate.Set();
            await stopping.WaitAsync(Budget);
        }

        /// <summary>The world stops, then the group feature stops, as the host does it.</summary>
        public async Task StopPersistenceAsync()
        {
            _worldGate.Set();
            DisposeWorld();
            await Persistence.StopAsync();
        }

        private void DisposeWorld()
        {
            if (!_worldDisposed)
            {
                _worldDisposed = true;
                World.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _worldGate.Set();
            _parkGate.Set();
            DisposeWorld();
            await Persistence.StopAsync();
            await Social.StopAsync();
            await _services.GetRequiredService<CharacterSaveQueue>().StopAsync();
            await _services.GetRequiredService<ClusterGroupRouting>().DisposeAsync();
            await _services.DisposeAsync();
            await Keeper.DisposeAsync();
        }
    }
}
