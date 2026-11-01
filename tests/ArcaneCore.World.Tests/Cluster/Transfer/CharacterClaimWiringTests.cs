using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// Step C5 of the per-worker character writer lock (_wf/writer-lock-DESIGN.md section 1b), the product wiring on top of <c>CharacterClaimTests</c>: the login claim and the
/// logout release run through the real login path of a world in Worker scope (<see cref="CharacterClaimService"/>, <see cref="CharacterClaimFeature"/>), the login-time reap is
/// built from <c>Cluster:Fencing:ReapDeadOwners</c>/<c>ReapAfterSeconds</c> and backed by the real shard advisory-lock probe, and the main character save queue and the honor writer
/// are claim-checked like the keyed queues. Claims live in a real SQLite characters database (real claim store, real write fence); the world itself runs on the in-memory harness.
/// </summary>
public sealed class CharacterClaimWiringTests
{
    private static readonly WorkerNodeId NodeA = new("node-a");
    private static readonly WorkerNodeId NodeB = new("node-b");

    [Fact]
    public async Task ARealLogin_ClaimsTheCharacter_AndTheLogoutReleasesItOnceTheSavesAreDurable()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await using WorldTestHost host = rig.StartWorld(a);
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE1", "Claimant");

        Assert.Null(rig.Store.GetActiveOwnership((ulong)id));
        await client.LoginAsync((ulong)id);
        Assert.True(host.World.IsOnline(ObjectGuid.Player((uint)id)));
        Assert.Equal(a, rig.Store.GetActiveOwnership((ulong)id)); // claimed before the character was attached

        await client.DisposeAsync(); // logout
        await WorldTestHost.WaitForAsync(() => rig.Store.GetActiveOwnership((ulong)id) is null, "the claim is released after the logout save");
        Assert.False(host.World.IsOnline(ObjectGuid.Player((uint)id)));
    }

    [Fact]
    public async Task ARealLogin_IsRefused_WhileAnotherLiveWorkerHoldsTheCharacter()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await using WorldTestHost host = rig.StartWorld(a);
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE2", "Taken");
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync((ulong)id, b)).Outcome);

        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        byte[] failed = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        Assert.Equal((byte)CharResult.CharLoginDuplicateCharacter, failed[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);
        Assert.Equal(b, rig.Store.GetActiveOwnership((ulong)id)); // the holder keeps it
    }

    [Fact]
    public async Task ALoginTimeReap_UsesTheConfiguredOptions_AndTheRealShardLockProbe()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        long stale = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

        // Shard a is silent for ten minutes but its advisory lock is still held: it is alive, so b's login is refused and nothing is reaped.
        await using (WorldTestHost held = rig.StartWorld(b))
        {
            (WorldTestClient client, int id) = await ConnectWithCharacterAsync(held, "CLAIMWIRE3", "Held");
            await rig.Store.TryClaimCharacterAsync((ulong)id, a);
            Assert.True(await rig.HeartbeatAsync(a, stale));
            using (CharacterDatabaseWriterLock.TryAcquireShard(rig.ConnectionOptions, "a")!)
            {
                var login = new PacketWriter(8);
                login.WriteUInt64((ulong)id);
                await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
                byte[] refused = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
                Assert.Equal((byte)CharResult.CharLoginDuplicateCharacter, refused[0]);
            }

            Assert.True((await rig.OwnershipRowAsync(a.Scope)).Active);
            Assert.Equal(a, rig.Store.GetActiveOwnership((ulong)id));
        }

        // The lock is free now (the process is gone) and the heartbeat is stale: the login reaps shard a and takes the character over.
        await using (WorldTestHost reaping = rig.StartWorld(b))
        {
            (WorldTestClient client, int id) = await ConnectWithCharacterAsync(reaping, "CLAIMWIRE4", "Reaped");
            await rig.Store.TryClaimCharacterAsync((ulong)id, a);
            Assert.True(await rig.HeartbeatAsync(a, stale));
            await client.LoginAsync((ulong)id);
            Assert.Equal(b, rig.Store.GetActiveOwnership((ulong)id));
            Assert.False((await rig.OwnershipRowAsync(a.Scope)).Active);
        }

        // ReapDeadOwners off: the same silent, lock-free shard is left to an operator.
        await using (WorldTestHost off = rig.StartWorld(b, reapDeadOwners: false))
        {
            CharacterOwnershipToken a2 = await rig.AcquireWorkerAsync("a2", 2);
            (WorldTestClient client, int id) = await ConnectWithCharacterAsync(off, "CLAIMWIRE5", "Left");
            await rig.Store.TryClaimCharacterAsync((ulong)id, a2);
            Assert.True(await rig.HeartbeatAsync(a2, stale));
            var login = new PacketWriter(8);
            login.WriteUInt64((ulong)id);
            await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            byte[] refused = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
            Assert.Equal((byte)CharResult.CharLoginDuplicateCharacter, refused[0]);
            Assert.True((await rig.OwnershipRowAsync(a2.Scope)).Active);
        }
    }

    [Fact]
    public async Task TheClaimService_IsInertWithoutAWorkerToken_AndAReleaseNeverDeletesAnotherHoldersClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        CharacterOwnershipToken world = await rig.AcquireWorldAsync();
        using var claims = new CharacterClaimService(rig.Services);

        Assert.Equal(CharacterClaimOutcome.NotWorkerScope, (await claims.ClaimAsync(30, null)).Outcome);
        CharacterClaimResult realm = await claims.ClaimAsync(30, world);
        Assert.Equal(CharacterClaimOutcome.NotWorkerScope, realm.Outcome);
        Assert.True(CharacterClaimService.Admits(realm));
        Assert.Null(rig.Store.GetActiveOwnership(30));

        CharacterClaimResult claimed = await claims.ClaimAsync(31, a);
        Assert.Equal(CharacterClaimOutcome.Claimed, claimed.Outcome);
        Assert.True(CharacterClaimService.Admits(claimed));
        CharacterClaimResult refused = await claims.ClaimAsync(31, b);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, refused.Outcome);
        Assert.False(CharacterClaimService.Admits(refused));

        claims.ScheduleRelease(31, b); // not the holder: the delete is exact on the token
        await claims.DrainAsync();
        Assert.Equal(a, rig.Store.GetActiveOwnership(31));

        claims.ScheduleRelease(31, a, () => true); // the character is online again: it keeps the claim
        await claims.DrainAsync();
        Assert.Equal(a, rig.Store.GetActiveOwnership(31));

        claims.ScheduleRelease(31, a, () => false);
        await claims.DrainAsync();
        Assert.Null(rig.Store.GetActiveOwnership(31));
    }

    [Fact]
    public async Task AReleaseWaitingForItsFlush_IsSupersededByANewLoginsClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var gate = new TaskCompletionSource();
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        services.AddSingleton(new CharacterPersistenceBarrier(new SlowParticipantProvider(rig.Services, gate.Task)));
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);
        await claims.ClaimAsync(32, a);

        claims.ScheduleRelease(32, a); // waits in the persistence barrier until the gate opens
        CharacterClaimResult relogin = await claims.ClaimAsync(32, a); // the next login of the same character on this shard
        Assert.Equal(CharacterClaimOutcome.AlreadyHeld, relogin.Outcome);
        gate.SetResult();
        await claims.DrainAsync();
        Assert.Equal(a, rig.Store.GetActiveOwnership(32)); // the stale release did not strip the new session's claim
    }

    [Fact]
    public async Task TwoConcurrentFirstClaims_OfTheSameCharacter_ExactlyOneWins_AndTheLoserIsRefusedNotThrown()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);

        CharacterClaimResult[] results = await Task.WhenAll(
            Task.Run(() => rig.Store.TryClaimCharacterAsync(33, a)),
            Task.Run(() => rig.Store.TryClaimCharacterAsync(33, b)));

        Assert.Single(results, r => r.IsClaimed);
        Assert.Single(results, r => r.Outcome == CharacterClaimOutcome.RefusedLiveOwner);
        CharacterOwnershipToken holder = rig.Store.GetActiveOwnership(33)!;
        Assert.Equal(results.Single(r => r.IsClaimed).Claim, holder);
    }

    [Fact]
    public async Task TheMainSaveQueuesSnapshot_OfARetiredSource_IsDurablyRefusedAfterTheCommit_AndTheTargetsIsWritten()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Retired");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);

        // While the claim is a's, a's snapshot lands.
        var queue = new CharacterSaveQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        queue.Start();
        rig.Holder.Current = a;
        queue.Enqueue(State(character, 5, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(5u, (await rig.CharacterAsync(character))!.MapId);

        // The character moves to b with a committed transfer. a still has a snapshot for it: refused by the database, dropped (not retained forever).
        await rig.MoveClaimAsync(character, a, b);
        rig.Holder.Current = a;
        queue.Enqueue(State(character, 7, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(queue.RetainedCharacters);
        Assert.Null(queue.TerminalFailure); // one character lost to another world never halts the queue
        Assert.Equal(5u, (await rig.CharacterAsync(character))!.MapId);

        // b writes the same character through the same queue type under its own claim.
        rig.Holder.Current = b;
        queue.Enqueue(State(character, 30, b));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(30u, (await rig.CharacterAsync(character))!.MapId);
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AnUnclaimedCharactersSnapshot_IsRefused_UntilTheLoginClaimsIt()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Unclaimed");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var queue = new CharacterSaveQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        queue.Start();
        rig.Holder.Current = a;

        queue.Enqueue(State(character, 9, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(9u, (await rig.CharacterAsync(character))!.MapId); // no claim, no write

        using var claims = new CharacterClaimService(rig.Services);
        Assert.True(CharacterClaimService.Admits(await claims.ClaimAsync(character, a)));
        queue.Enqueue(State(character, 9, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(9u, (await rig.CharacterAsync(character))!.MapId);
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheHonorQueue_RefusesAndDropsARetiredSourcesWrite_AndTheTargetWritesItsOwn()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Honored");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        await rig.MoveClaimAsync(character, a, b);

        var queue = new HonorWriteQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        queue.Start();
        rig.Holder.Current = a;
        queue.SaveState(character, HonorState(42));
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(queue.RetainedCharacters);
        Assert.Null(queue.TerminalFailure);
        Assert.Equal(0u, (await rig.HonorOfAsync(character)).State.Standing);

        rig.Holder.Current = b;
        queue.SaveState(character, HonorState(77));
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(77u, (await rig.HonorOfAsync(character)).State.Standing);
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);

    private static CharacterHonorState HonorState(uint standing) => new(0f, 0, standing, 0, 0f, 0, 0, 0, false);

    private static async Task<(WorldTestClient Client, int Id)> ConnectWithCharacterAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        return (client, record.Id);
    }

    /// <summary>Gives <see cref="CharacterPersistenceBarrier"/> one participant that waits for a gate, so a release can be held inside its flush.</summary>
    private sealed class SlowParticipantProvider(IServiceProvider inner, Task gate) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IEnumerable<ICharacterPersistenceParticipant>)
                ? new ICharacterPersistenceParticipant[] { new GatedParticipant(gate) }
                : inner.GetService(serviceType);
    }

    private sealed class GatedParticipant(Task gate) : ICharacterPersistenceParticipant
    {
        public string Name => "gated";

        public async Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default) => await gate.WaitAsync(cancellationToken);
    }

    /// <summary>One SQLite characters database with the write fence on and a process-wide ownership holder the cases switch between workers, plus a factory for Worker-scope worlds on top of it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _file;

        private Rig(ServiceProvider services, string file)
        {
            _services = services;
            _file = file;
            Holder = services.GetRequiredService<CharacterOwnershipContext>();
            Store = services.GetRequiredService<EfTransferDecisionStore>();
        }

        public CharacterOwnershipContext Holder { get; }
        public EfTransferDecisionStore Store { get; }
        public IServiceProvider Services => _services;

        public DatabaseConnectionOptions ConnectionOptions => new() { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={_file}" };

        public static async Task<Rig> CreateAsync(TempDirectory directory)
        {
            string file = Path.Combine(directory.Path, "characters.db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={file}",
                [DataServiceCollectionExtensions.FencingEnabledKey] = "true",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCharacterDatabase(configuration);
            ServiceProvider provider = services.BuildServiceProvider();
            await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            return new Rig(provider, file);
        }

        /// <summary>
        /// A Worker-scope world on the in-memory harness: its ownership token is <paramref name="token"/>, and its claim service, persistence barrier and shard-lock guard run
        /// against this rig's database with <c>ReapDeadOwners</c> as given and the default <c>ReapAfterSeconds</c>.
        /// </summary>
        public WorldTestHost StartWorld(CharacterOwnershipToken token, bool reapDeadOwners = true)
        {
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(Store);
                services.AddSingleton(Options.Create(new CharacterFencingOptions { Scope = CharacterFencingScope.Worker, ReapDeadOwners = reapDeadOwners }));
                services.AddSingleton(new WorldWriterGuard(Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={_file}" })));
                services.TryAddSingleton<CharacterPersistenceBarrier>();
                services.TryAddSingleton<CharacterClaimService>();
            });
            host.World.CharacterOwnership = token;
            return host;
        }

        public async Task<int> CreateCharacterAsync(string name)
        {
            // Before any ownership row is active, so the tokenless write is allowed.
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
        }

        public async Task<CharacterRecord?> CharacterAsync(int characterId)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(characterId);
        }

        public async Task<CharacterHonorData> HonorOfAsync(int characterId)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IHonorStore>().LoadAsync(characterId);
        }

        public async Task<CharacterOwnershipToken> AcquireWorkerAsync(string shard, uint map)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
                .TryAcquireAsync(CharacterOwnershipScopes.ForShard(shard), "node-" + shard, Guid.NewGuid(), [map]))!;
        }

        public async Task<CharacterOwnershipToken> AcquireWorldAsync()
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryAcquireAsync("world", "node-world", Guid.NewGuid()))!;
        }

        public async Task<bool> HeartbeatAsync(CharacterOwnershipToken token, long nowUnixMs)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().HeartbeatAsync(token, nowUnixMs);
        }

        public async Task<CharacterOwnershipRow> OwnershipRowAsync(string scopeName)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            return await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scopeName);
        }

        /// <summary>Moves the claim of <paramref name="character"/> from <paramref name="from"/> to <paramref name="to"/> with a full committed transfer (what a cross-worker handoff does).</summary>
        public async Task MoveClaimAsync(int character, CharacterOwnershipToken from, CharacterOwnershipToken to)
        {
            Guid transfer = Guid.NewGuid();
            await Store.CreateReservationAsync(transfer, (ulong)character, 1, 1, NodeA, NodeB, 1, 0, 30, 0, from);
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, (ulong)character, NodeA, NodeB, from));
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);
            await Store.CommitDecisionAsync(transfer, to.Incarnation, CancellationToken.None, to.Scope);
            await Store.MarkCompletedAsync(transfer);
        }

        public async ValueTask DisposeAsync() => await _services.DisposeAsync();
    }
}
