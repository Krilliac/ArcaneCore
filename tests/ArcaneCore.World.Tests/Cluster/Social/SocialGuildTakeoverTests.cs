using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Guilds and petitions are the Social lease holder's, and a save stores a whole guild or petition. A worker that takes the lease over at run time rereads both before any of its saves reach storage, so its replica never
/// reverts what the dead holder stored but never published. The stores here refuse as the real ones do (<see cref="LeaseFencedWrite.RequireFence"/>), so a write that reaches them after the takeover would pass.
/// </summary>
public sealed class SocialGuildTakeoverTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static GuildData Guild(int id, string name, string motd, params int[] members)
        => new(id, name, members[0], motd, string.Empty, 100, 0, 0, 0, 0, 0, [],
            [.. members.Select((member, index) => new GuildMemberData(member, (byte)(index == 0 ? 0 : 4), string.Empty, string.Empty, 10, 12, 0))]);

    private static PetitionData Charter(int id, int owner, string name, params int[] signers)
        => new(id, owner, 5000 + id, name, [.. signers.Select(signer => new PetitionSignatureData(signer, 100 + signer))]);

    [Fact]
    public async Task Survivor_installs_what_the_dead_holder_stored_and_its_next_save_keeps_it()
    {
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Stays", "old", 1, 2), Guild(2, "Disbanded", "old", 3)], [Charter(5, 4, "Signed Later"), Charter(6, 5, "Gone")]);

        // After this node attached the holder stored all of this, and died before publishing it.
        await rig.Rows.SaveGuildAsync(Guild(1, "Stays", "from the holder", 1, 2, 7));
        await rig.Rows.DeleteGuildAsync(2);
        await rig.Rows.SaveGuildAsync(Guild(3, "Founded", "new", 8));
        await rig.Charters.SavePetitionAsync(Charter(5, 4, "Signed Later", 6));
        await rig.Charters.DeletePetitionAsync(6);

        rig.TakeOver();
        await rig.WaitUntilCurrentAsync();

        await rig.OnWorld(() =>
        {
            Assert.True(rig.Guilds.IsAuthority);
            Assert.Equal("from the holder", rig.Guilds.GetByName("Stays")!.Motd);
            Assert.Equal(1, rig.Guilds.GetGuildOf(7)!.Id);
            Assert.Null(rig.Guilds.Get(2));
            Assert.Null(rig.Guilds.GetGuildOf(3));
            Assert.Equal(3, rig.Guilds.GetGuildOf(8)!.Id);
            Assert.Equal(6u, Assert.Single(rig.Petitions.Get(5)!.Signatures).PlayerId);
            Assert.Null(rig.Petitions.Get(6));

            // The whole-guild save that would have replaced the stored guild with the old replica.
            Assert.Equal(GuildAdminResult.Ok, rig.Guilds.AdminInvite(9, "Stays"));
            return true;
        });

        await rig.Social.Writes!.FlushAsync();
        GuildData stored = (await rig.Rows.GetGuildsAsync()).Single(g => g.Id == 1);
        Assert.Equal("from the holder", stored.Motd);
        Assert.Equal([1, 2, 7, 9], stored.Members.Select(m => m.CharacterId).Order());
        Assert.Equal(1, rig.Store.Writes);
    }

    [Fact]
    public async Task With_the_lease_held_nothing_is_stored_or_served_until_the_read_is_done()
    {
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Stays", "old", 1, 2)], [Charter(5, 4, "Charter")]);
        await rig.Rows.SaveGuildAsync(Guild(1, "Stays", "from the holder", 1, 2));

        rig.Store.HoldLoads();
        rig.TakeOver();
        Assert.False(await rig.OnWorld(rig.Feature.EnsureGuildsCurrent));
        await rig.Store.LoadEntered.WaitAsync(Budget);

        // The lease is held and the read is still running: the store's fence would let every write through.
        Assert.NotNull(LeaseFencedWrite.RequireFence(rig.Keeper, RealmAuthorityScopes.Social));
        Assert.False(rig.Link.IsHolder);
        Assert.False(rig.Link.CanForward);
        Assert.False(rig.Link.Forward(new GuildRequest(GuildRequestKind.Info, ObjectGuid.Player(1)), null));
        Assert.Equal(0, rig.Link.PumpOnce());
        Assert.Equal(0, rig.Transport.Calls);
        await rig.OnWorld(() =>
        {
            Assert.False(rig.Guilds.IsAuthority);
            Assert.Equal(GuildAdminResult.NotHolder, rig.Guilds.AdminInvite(9, "Stays"));
            Assert.False(rig.Guilds.Serve(new GuildRequest(GuildRequestKind.AdminUninvite, ObjectGuid.Player(2))));
            return true;
        });

        await rig.Social.Writes!.FlushAsync();
        Assert.Equal(0, rig.Store.Writes);
        Assert.Empty(rig.Social.Writes!.RetainedSharedWrites);

        rig.Store.ReleaseLoads();
        await rig.WaitUntilCurrentAsync();

        Assert.True(rig.Link.IsHolder);
        Assert.Equal(GuildAdminResult.Ok, await rig.OnWorld(() => rig.Guilds.AdminInvite(9, "Stays")));
        await rig.Social.Writes!.FlushAsync();
        GuildData stored = Assert.Single(await rig.Rows.GetGuildsAsync());
        Assert.Equal("from the holder", stored.Motd);
        Assert.Equal([1, 2, 9], stored.Members.Select(m => m.CharacterId).Order());
    }

    [Fact]
    public async Task Failed_read_keeps_the_saves_held_and_is_tried_again()
    {
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Stays", "old", 1, 2)], []);
        await rig.Rows.SaveGuildAsync(Guild(1, "Stays", "from the holder", 1, 2));

        // The first read fails; the retry is held, so the node stays in this state however fast the retry comes.
        rig.Store.FailNextLoad();
        rig.Store.HoldLoads();
        rig.TakeOver();
        Assert.False(await rig.OnWorld(rig.Feature.EnsureGuildsCurrent));
        await rig.Store.LoadFailed.WaitAsync(Budget);
        await rig.WaitForLoadRetryAsync();
        Assert.False(await rig.OnWorld(rig.Feature.EnsureGuildsCurrent));
        Assert.False(rig.Link.IsHolder);

        rig.Store.ReleaseLoads();
        await rig.WaitUntilCurrentAsync();
        Assert.Equal("from the holder", await rig.OnWorld(() => rig.Guilds.GetByName("Stays")!.Motd));
    }

    [Fact]
    public async Task Load_repairs_are_written_by_the_holder_only_and_after_the_reread()
    {
        // Character 99 does not exist, so guild 1 has no members left; the owner of charter 5 is guilded, so the charter is stale; charter 6 has a signer who does not exist.
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Empty", "old", 99), Guild(2, "Stays", "old", 4)], [Charter(5, 4, "Stale"), Charter(6, 5, "Trimmed", 6, 99)]);

        // A replica's repair would be refused now and kept, and would land when the lease arrives, whatever the holder stored meanwhile.
        await rig.Social.Writes!.FlushAsync();
        Assert.Equal(0, rig.Store.Writes);
        Assert.Empty(rig.Social.Writes!.RetainedSharedWrites);

        rig.TakeOver();
        await rig.WaitUntilCurrentAsync();
        await rig.Social.Writes!.FlushAsync();

        Assert.Equal(3, rig.Store.Writes);
        Assert.Equal(2, Assert.Single(await rig.Rows.GetGuildsAsync()).Id);
        PetitionData trimmed = Assert.Single(rig.Charters.Snapshot());
        Assert.Equal(6, trimmed.Id);
        Assert.Equal(6, Assert.Single(trimmed.Signatures).PlayerId);
    }

    [Fact]
    public async Task The_reread_state_is_published_to_workers_that_arrived_during_the_read()
    {
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Stays", "old", 1, 2), Guild(2, "Disbanded", "old", 3)], [Charter(5, 4, "Charter"), Charter(6, 5, "Gone")]);
        await rig.Rows.SaveGuildAsync(Guild(1, "Stays", "from the holder", 1, 2));
        await rig.Rows.DeleteGuildAsync(2);
        await rig.Charters.DeletePetitionAsync(6);

        rig.Store.HoldLoads();
        rig.TakeOver();
        Assert.False(await rig.OnWorld(rig.Feature.EnsureGuildsCurrent));
        await rig.Store.LoadEntered.WaitAsync(Budget);

        // Another worker's first contact with the new holder: its snapshot is skipped, this node not being the authority yet.
        Assert.Empty(rig.Local.Drain("worker-c"));
        await rig.OnWorld(() => true);

        rig.Store.ReleaseLoads();
        await rig.WaitUntilCurrentAsync();

        IReadOnlyList<GuildLinkEvent> events = rig.Local.Drain("worker-c");
        Assert.Equal("from the holder", events.Single(e => e.Kind == GuildLinkEventKind.Guild && e.Id == 1).Guild!.Motd);
        Assert.Null(events.Single(e => e.Kind == GuildLinkEventKind.Guild && e.Id == 2).Guild);
        Assert.NotNull(events.Single(e => e.Kind == GuildLinkEventKind.Petition && e.Id == 5).Petition);
        Assert.Null(events.Single(e => e.Kind == GuildLinkEventKind.Petition && e.Id == 6).Petition);
    }

    [Fact]
    public async Task Online_players_get_the_guild_fields_of_what_was_read()
    {
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Stays", "old", 1, 2)], []);
        Player left = await rig.JoinAsync(2);
        Player joined = await rig.JoinAsync(7);
        await rig.OnWorld(() =>
        {
            left.SetUInt32(UpdateFields.PlayerGuildid, 1);
            return true;
        });

        await rig.Rows.SaveGuildAsync(Guild(1, "Stays", "old", 1, 7));
        rig.TakeOver();
        await rig.WaitUntilCurrentAsync();

        await rig.OnWorld(() =>
        {
            Assert.Equal(0u, left.GetUInt32(UpdateFields.PlayerGuildid));
            Assert.Equal(1u, joined.GetUInt32(UpdateFields.PlayerGuildid));
            Assert.Equal(4u, joined.GetUInt32(UpdateFields.PlayerGuildrank));
            return true;
        });
    }

    [Fact]
    public async Task A_worker_that_never_gains_the_lease_stays_a_replica_and_reads_nothing()
    {
        await using Rig rig = await Rig.CreateAsync([Guild(1, "Stays", "old", 1)], []);

        Assert.False(await rig.OnWorld(rig.Feature.EnsureGuildsCurrent));
        Assert.True(rig.Link.CanForward);
        Assert.Equal(1, rig.Store.Loads); // the attach read only
    }

    [Fact]
    public async Task Holder_at_attach_stores_straight_through_and_never_rereads()
    {
        await using Rig rig = await Rig.CreateAsync(true, [Guild(1, "Stays", "old", 1)], []);

        Assert.True(await rig.OnWorld(rig.Feature.EnsureGuildsCurrent));
        Assert.True(rig.Link.IsHolder);
        Assert.Equal(GuildAdminResult.Ok, await rig.OnWorld(() => rig.Guilds.AdminInvite(9, "Stays")));
        await rig.Social.Writes!.FlushAsync();

        Assert.Equal(1, rig.Store.Writes);
        Assert.Equal(1, rig.Store.Loads);
    }

    /// <summary>The guild store as a worker sees it: refused without the Social fence, counted when a write gets through, and a read that can be held or failed.</summary>
    private sealed class FencedSocialStore(InMemorySocialStore rows, IRealmLeaseFenceSource fences) : ISocialStore
    {
        private readonly TaskCompletionSource _loadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _loadFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _release;
        private int _failLoads;
        private int _writes;
        private int _loads;

        public int Writes => Volatile.Read(ref _writes);

        public int Loads => Volatile.Read(ref _loads);

        public Task LoadEntered => _loadEntered.Task;

        public Task LoadFailed => _loadFailed.Task;

        public void HoldLoads() => Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseLoads() => Volatile.Read(ref _release)!.TrySetResult();

        public void FailNextLoad() => Volatile.Write(ref _failLoads, 1);

        public void CountWrite()
        {
            LeaseFencedWrite.RequireFence(fences, RealmAuthorityScopes.Social);
            Interlocked.Increment(ref _writes);
        }

        public async Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            if (Interlocked.Exchange(ref _failLoads, 0) == 1)
            {
                _loadFailed.TrySetResult();
                throw new IOException("guild read unavailable");
            }

            if (Volatile.Read(ref _release) is { } release)
            {
                _loadEntered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await rows.GetGuildsAsync(cancellationToken);
        }

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
        {
            CountWrite();
            return rows.SaveGuildAsync(guild, cancellationToken);
        }

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default)
        {
            CountWrite();
            return rows.DeleteGuildAsync(guildId, cancellationToken);
        }

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => rows.GetSocialAsync(characterId, cancellationToken);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => rows.SetSocialAsync(characterId, otherId, flags, cancellationToken);
    }

    /// <summary>The petition store as a worker sees it, counted with the guild writes.</summary>
    private sealed class FencedPetitionStore(InMemoryPetitionStore rows, FencedSocialStore guilds) : IPetitionStore
    {
        public Task<IReadOnlyList<PetitionData>> GetPetitionsAsync(CancellationToken cancellationToken = default) => rows.GetPetitionsAsync(cancellationToken);

        public Task SavePetitionAsync(PetitionData petition, CancellationToken cancellationToken = default)
        {
            guilds.CountWrite();
            return rows.SavePetitionAsync(petition, cancellationToken);
        }

        public Task DeletePetitionAsync(int petitionId, CancellationToken cancellationToken = default)
        {
            guilds.CountWrite();
            return rows.DeletePetitionAsync(petitionId, cancellationToken);
        }

        public Task CompletePetitionAsync(GuildData guild, int petitionId, CancellationToken cancellationToken = default)
        {
            guilds.CountWrite();
            return rows.CompletePetitionAsync(guild, petitionId, cancellationToken);
        }

        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => rows.PurgeCharacterAsync(characterId, cancellationToken);
    }

    /// <summary>A remote holder that this node never reaches once it holds the lease: every call is counted.</summary>
    private sealed class CountingTransport : IGuildHolderTransport
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool IsAuthorityReachable => true;

        public void ForwardGuildRequest(string workerId, long requestId, GuildRequest request) => Interlocked.Increment(ref _calls);

        public IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId)
        {
            Interlocked.Increment(ref _calls);
            return [];
        }
    }

    private sealed class SilentSession(int accountId) : IPlayerSession
    {
        public int AccountId => accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    /// <summary>A map worker (worker-a) with the real social features and link, whose Social scope is held by worker-b, which never renews it, unless <c>holdsSocial</c> says this node took it first.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ManualTimeProvider _time;
        private readonly ServiceProvider _services;

        private Rig(ManualTimeProvider time, RealmAuthorityLeaseKeeper keeper, InMemorySocialStore rows, InMemoryPetitionStore charters, FencedSocialStore store, GuildHolderService local, CountingTransport transport, PeerGuildHolderLink link, ServiceProvider services)
        {
            _time = time;
            _services = services;
            Keeper = keeper;
            Rows = rows;
            Charters = charters;
            Store = store;
            Local = local;
            Transport = transport;
            Link = link;
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            Social = services.GetRequiredService<SocialFeature>();
            Feature = new SocialPetitionFeature(services, services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Social.Attach(World);
            Feature.Attach(World);
            World.Start();
        }

        public RealmAuthorityLeaseKeeper Keeper { get; }

        public InMemorySocialStore Rows { get; }

        public InMemoryPetitionStore Charters { get; }

        public FencedSocialStore Store { get; }

        public GuildHolderService Local { get; }

        public CountingTransport Transport { get; }

        public PeerGuildHolderLink Link { get; }

        public WorldRuntime World { get; }

        public SocialFeature Social { get; }

        public SocialPetitionFeature Feature { get; }

        public GuildManager Guilds => Social.Context.Guilds;

        public PetitionManager Petitions => Social.Context.Petitions;

        public static Task<Rig> CreateAsync(GuildData[] guilds, PetitionData[] petitions) => CreateAsync(false, guilds, petitions);

        public static async Task<Rig> CreateAsync(bool holdsSocial, GuildData[] guilds, PetitionData[] petitions)
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
            var leases = new InMemoryRealmAuthorityLeaseStore();
            if (!holdsSocial)
            {
                leases.TryAcquire(RealmAuthorityScopes.Social, "worker-b", Guid.NewGuid(), TimeSpan.FromSeconds(30), time.GetUtcNow());
            }

            var keeper = new RealmAuthorityLeaseKeeper(leases,
                Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
                Microsoft.Extensions.Options.Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = "worker-a" }),
                NullLogger<RealmAuthorityLeaseKeeper>.Instance, null, time, _ => { });
            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(holdsSocial, keeper.Holds(RealmAuthorityScopes.Social));

            var rows = new InMemorySocialStore();
            var charters = new InMemoryPetitionStore(rows);
            foreach (GuildData guild in guilds)
            {
                await rows.SaveGuildAsync(guild);
            }

            foreach (PetitionData petition in petitions)
            {
                await charters.SavePetitionAsync(petition);
            }

            var directory = new CharacterDirectory();
            for (int id = 1; id <= 9; id++)
            {
                directory.Add(new CharacterIdentity(id, 100 + id, "Char" + (char)('a' + id), (byte)Race.Human, 0, (byte)Class.Warrior, 10, 12));
            }

            var store = new FencedSocialStore(rows, keeper);
            var local = new GuildHolderService(new InMemoryRealmPresenceDirectory());
            var transport = new CountingTransport();
            var link = new PeerGuildHolderLink(transport, local, "worker-a", () => keeper.TokenFor(RealmAuthorityScopes.Social) is not null);
            ServiceProvider services = new ServiceCollection()
                .AddSingleton<ISocialStore>(store)
                .AddSingleton<IPetitionStore>(new FencedPetitionStore(charters, store))
                .AddSingleton<IGuildHolderLink>(link)
                .AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance))
                .AddSingleton(provider => new SocialFeature(directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, services: provider))
                .BuildServiceProvider();
            var rig = new Rig(time, keeper, rows, charters, store, local, transport, link, services);
            await rig.Social.GuildsLoaded.WaitAsync(Budget);
            await rig.Feature.PetitionsLoaded.WaitAsync(Budget);
            return rig;
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

        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async Task<Player> JoinAsync(int id)
        {
            var record = new CharacterRecord
            {
                Id = id, AccountId = 100 + id, Name = "Char" + (char)('a' + id),
                Race = (byte)Race.Human, Class = (byte)Class.Warrior,
                Gender = (byte)Gender.Male, Level = 10, ZoneId = 12,
            };
            var player = new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new SilentSession(100 + id));
            await OnWorld(() =>
            {
                World.AddPlayer(player);
                return true;
            });
            return player;
        }

        public async Task WaitUntilCurrentAsync()
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(Feature.EnsureGuildsCurrent).WaitAsync(timeout.Token))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        /// <summary>Keeps asking until the failed read has been tried again (the retry then waits at the store's hold).</summary>
        public async Task WaitForLoadRetryAsync()
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!Store.LoadEntered.IsCompleted)
            {
                await OnWorld(Feature.EnsureGuildsCurrent).WaitAsync(timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            World.Dispose();
            await Feature.StopAsync();
            await Social.StopAsync();
            await _services.GetRequiredService<CharacterSaveQueue>().StopAsync();
            await Link.DisposeAsync();
            await _services.DisposeAsync();
            await Keeper.DisposeAsync();
        }
    }
}
