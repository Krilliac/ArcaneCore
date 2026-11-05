using System.Data.Common;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// Step C5 of the per-worker character writer lock (_wf/writer-lock-DESIGN.md section 1b): per-character claims. Each worker shard holds a claim row
/// (<c>cluster_transfer_owner</c>) per character it has logged in; the characters write fence proves, inside every save transaction, that the writing worker
/// still holds it. These cases run on SQLite (the real <c>worker:&lt;shard&gt;</c> ownership rows, the real claim store and the real fence); the same scenarios on
/// MariaDB and PostgreSQL are in <c>CharacterClaimProviderTests</c> (ArcaneCore.Data.Tests), which run where a server is configured.
/// </summary>
public sealed class CharacterClaimTests
{
    private static readonly WorkerNodeId NodeA = new("node-a");
    private static readonly WorkerNodeId NodeB = new("node-b");

    [Fact]
    public async Task ALiveClaim_IsRefused_AndTheHolderKeepsIt()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);

        CharacterClaimResult first = await rig.Store.TryClaimCharacterAsync(10, a);
        Assert.Equal(CharacterClaimOutcome.Claimed, first.Outcome);
        Assert.Equal(CharacterClaimOutcome.AlreadyHeld, (await rig.Store.TryClaimCharacterAsync(10, a)).Outcome);

        CharacterClaimResult refused = await rig.Store.TryClaimCharacterAsync(10, b);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, refused.Outcome);
        Assert.False(refused.IsClaimed);
        Assert.Equal(a.Scope, refused.PreviousScope);
        Assert.Equal(a, rig.Store.GetActiveOwnership(10));

        await rig.WriteAsync(10, a); // the holder still writes
        await AssertRefusedAsync<CharacterClaimRejectedException>(() => rig.WriteAsync(10, b)); // the refused worker cannot
    }

    [Fact]
    public async Task ADeadIncarnationsClaim_IsTakenOver()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync(11, a)).Outcome);

        // Shard a is reclaimed by a new incarnation (a restart after a kill): the old incarnation is no longer the active one of its scope.
        CharacterOwnershipToken restarted = (await rig.ReclaimAsync(a))!;
        Assert.Equal(a.Scope, restarted.Scope);
        Assert.NotEqual(a.Incarnation, restarted.Incarnation);

        CharacterClaimResult taken = await rig.Store.TryClaimCharacterAsync(11, b);
        Assert.Equal(CharacterClaimOutcome.TookOverDeadOwner, taken.Outcome);
        Assert.Equal(a.Scope, taken.PreviousScope);
        Assert.Equal(b, rig.Store.GetActiveOwnership(11));
        await rig.WriteAsync(11, b);
        await AssertRefusedAsync<CharacterOwnershipRejectedException>(() => rig.WriteAsync(11, a)); // the zombie's scope token is stale (its shard was reclaimed), which the write fence refuses before the claim is even looked at
    }

    [Fact]
    public async Task ARestartedShard_RefreshesItsOwnClaim_AndTheOldIncarnationIsRefused()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await rig.Store.TryClaimCharacterAsync(12, a);

        CharacterOwnershipToken restarted = (await rig.ReclaimAsync(a))!;
        CharacterClaimResult again = await rig.Store.TryClaimCharacterAsync(12, restarted);
        Assert.Equal(CharacterClaimOutcome.Claimed, again.Outcome); // same shard, new incarnation: no liveness question
        Assert.Null(again.PreviousScope);
        await rig.WriteAsync(12, restarted);
        await AssertRefusedAsync<CharacterOwnershipRejectedException>(() => rig.WriteAsync(12, a)); // the old incarnation's scope token is stale, refused before the claim check
    }

    [Fact]
    public async Task AClaim_WhileATransferIsUnresolved_IsRefused()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync(13, a);
        await rig.Store.CreateReservationAsync(Guid.NewGuid(), 13, 1, 1, NodeA, NodeB, 1, 0, 30, 0, a);

        // Even a reclaimed owner's claim stays put while the transfer is unresolved: coordinator recovery resolves it first.
        CharacterOwnershipToken restarted = (await rig.ReclaimAsync(a))!;
        Assert.Equal(CharacterClaimOutcome.RefusedTransferInFlight, (await rig.Store.TryClaimCharacterAsync(13, b)).Outcome);
        Assert.Equal(CharacterClaimOutcome.RefusedTransferInFlight, (await rig.Store.TryClaimCharacterAsync(13, restarted)).Outcome);
        Assert.Equal(a, rig.Store.GetActiveOwnership(13));
    }

    [Fact]
    public async Task ADeadShardThatNeverComesBack_IsReapedAtLogin_OnlyWhenAllThreeConditionsHold()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync(14, a);
        var policy = new CharacterClaimReapPolicy(TimeSpan.FromSeconds(150), (_, _) => Task.FromResult(true));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. A fresh heartbeat: a network blip must not trigger a reap.
        Assert.True(await rig.HeartbeatAsync(a, now));
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, (await rig.Store.TryClaimCharacterAsync(14, b, policy)).Outcome);

        // 2. A stale heartbeat but ReapDeadOwners off (no policy): leaves it to an operator.
        Assert.True(await rig.HeartbeatAsync(a, now - (long)TimeSpan.FromMinutes(10).TotalMilliseconds));
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, (await rig.Store.TryClaimCharacterAsync(14, b)).Outcome);

        // 3. A stale heartbeat but the shard's advisory lock is still held by someone: it is alive.
        var locked = new CharacterClaimReapPolicy(TimeSpan.FromSeconds(150), (_, _) => Task.FromResult(false));
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, (await rig.Store.TryClaimCharacterAsync(14, b, locked)).Outcome);
        Assert.True((await rig.OwnershipRowAsync(a.Scope)).Active);

        // 4. All three: reaped, claimed. The reaped shard's token is dead, so its own saves fail the worker-token check too.
        CharacterClaimResult reaped = await rig.Store.TryClaimCharacterAsync(14, b, policy);
        Assert.Equal(CharacterClaimOutcome.TookOverDeadOwner, reaped.Outcome);
        Assert.False((await rig.OwnershipRowAsync(a.Scope)).Active);
        Assert.Equal(b, rig.Store.GetActiveOwnership(14));
        await rig.WriteAsync(14, b);
        await AssertRefusedAsync<CharacterOwnershipRejectedException>(() => rig.WriteAsync(14, a));
    }

    [Fact]
    public async Task ARetiredSourcesRetainedSnapshot_IsDurablyRefusedAfterTheTransferCommits()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        const int character = 15;
        await rig.Store.TryClaimCharacterAsync(character, a);
        await rig.WriteAsync(character, a);

        Guid transfer = Guid.NewGuid();
        await rig.Store.CreateReservationAsync(transfer, character, 1, 1, NodeA, NodeB, 1, 0, 30, 0, a);
        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, character, NodeA, NodeB, a);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, bundle);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);
        await rig.WriteAsync(character, a); // the settlement save runs while the transfer is reserved: still allowed

        TransferDecisionRecord committed = await rig.Store.CommitDecisionAsync(transfer, b.Incarnation, CancellationToken.None, b.Scope);
        Assert.Equal(b, committed.TargetOwnershipToken); // into another worker scope the committed token is that worker's own live scope token

        // The source still holds a snapshot for the character (a retained retry): refused by the database, not by anything in its own memory.
        await AssertRefusedAsync<CharacterClaimRejectedException>(() => rig.WriteAsync(character, a));
        await rig.WriteAsync(character, b); // the target writes under its own worker token
        TransferDecisionRecord done = await rig.Store.MarkCompletedAsync(transfer);
        Assert.Equal(b.Scope, done.TargetOwnershipToken!.Scope);
        Assert.Equal(committed.TargetOwnershipToken, rig.Store.GetActiveOwnership(character));
    }

    [Fact]
    public async Task ACommitIntoAWorkerScope_WithNoLiveRowUnderTheTargetIncarnation_IsRefused_AndTheTransferStaysAbortable()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        const int character = 16;
        await rig.Store.TryClaimCharacterAsync(character, a);

        Guid transfer = Guid.NewGuid();
        await rig.Store.CreateReservationAsync(transfer, character, 1, 1, NodeA, NodeB, 1, 0, 30, 0, a);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, character, NodeA, NodeB, a));
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);

        // Worker b's scope is live, but not under this incarnation (b restarted, or the coordinator names a stale one): b could never write the character.
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => rig.Store.CommitDecisionAsync(transfer, Guid.NewGuid(), CancellationToken.None, b.Scope));
        Assert.Equal(PlayerTransferDecisionState.PreCommit, (await rig.Store.GetDecisionAsync(transfer))!.DecisionState);
        await rig.WriteAsync(character, a); // the source still holds the claim

        await rig.Store.AbortPreCommitAsync(transfer, "target not live");
        await rig.WriteAsync(character, a);
    }

    [Fact]
    public async Task ARetainedSnapshotInAWriteQueue_IsRefusedAndDropped_NotRetriedForever_AndTheOtherWorkerWritesItsOwn()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Moved");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);

        // The character leaves a for b: a's claim row is moved to b (what a transfer commit does).
        Guid transfer = Guid.NewGuid();
        await rig.Store.CreateReservationAsync(transfer, (ulong)character, 1, 1, NodeA, NodeB, 1, 0, 30, 0, a);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, (ulong)character, NodeA, NodeB, a));
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
        await rig.Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);
        await rig.Store.CommitDecisionAsync(transfer, b.Incarnation, CancellationToken.None, b.Scope);

        // Worker a still queues the character's rested state (a retained write): its store call is refused and the value is dropped.
        rig.Holder.Current = a;
        var queueA = new RestWriteQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        queueA.Start();
        queueA.Save(character, new CharacterRestState(50f, 1000, true));
        await queueA.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(queueA.RetainedCharacters);
        Assert.Null(queueA.TerminalFailure); // one character lost to another world is not a reason to halt the queue
        Assert.Null(await rig.RestOfAsync(character));
        await queueA.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // Worker b's queue writes the same character under its own claim.
        rig.Holder.Current = b;
        var queueB = new RestWriteQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        queueB.Start();
        queueB.Save(character, new CharacterRestState(75f, 2000, false));
        await queueB.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(75f, (await rig.RestOfAsync(character))!.Value.RestBonus);
        await queueB.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ReservationInWorkerScope_RefusesAMissingClaim_InsteadOfSeedingOne()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);

        await Assert.ThrowsAsync<CharacterClaimRejectedException>(
            () => rig.Store.CreateReservationAsync(Guid.NewGuid(), 16, 1, 1, NodeA, NodeB, 1, 0, 30, 0, a));
        Assert.Null(rig.Store.GetActiveOwnership(16));

        // After a release the claim is gone again (logout): a late save is refused and a new login can claim it elsewhere.
        await rig.Store.TryClaimCharacterAsync(16, a);
        Assert.True(await rig.Store.ReleaseCharacterClaimAsync(16, a));
        Assert.False(await rig.Store.ReleaseCharacterClaimAsync(16, a));
        await AssertRefusedAsync<CharacterClaimRejectedException>(() => rig.WriteAsync(16, a));
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync(16, b)).Outcome);
    }

    [Fact]
    public async Task DeletingACharacter_IsRefusedWhileALiveWorkerClaimsIt_AndAllowedOnceTheOwnerIsGone()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync(17, a);

        await Assert.ThrowsAsync<CharacterDeletionRefusedException>(() => rig.DeleteClaimDataAsync(17, b));
        Assert.Equal(a, rig.Store.GetActiveOwnership(17));

        await rig.ReleaseShardAsync(a); // the owner is gone (its row is inactive), so its claim no longer blocks
        await rig.DeleteClaimDataAsync(17, b);
        Assert.Null(rig.Store.GetActiveOwnership(17));
    }

    [Fact]
    public async Task RealmScope_IssuesNoClaimSql()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Realm");
        CharacterOwnershipToken world = await rig.AcquireWorldAsync();
        rig.Log.Clear();

        Assert.Equal(CharacterClaimOutcome.NotWorkerScope, (await rig.Store.TryClaimCharacterAsync((ulong)character, world)).Outcome);
        Assert.False(await rig.Store.ReleaseCharacterClaimAsync((ulong)character, world));
        await rig.WriteAsync(character, world); // a claim scope in force, a realm token: the fence must not look at claims
        await rig.WriteAsync(character, world, CharacterClaimRequirement.HeldByAnyLiveWorker);

        rig.Holder.Current = world;
        var queue = new RestWriteQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        queue.Start();
        queue.Save(character, new CharacterRestState(10f, 3000, true));
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(10f, (await rig.RestOfAsync(character))!.Value.RestBonus); // the save itself went through
        Assert.DoesNotContain(rig.Log.Texts, t => t.Contains("cluster_transfer_owner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WorkerScope_EconomyStyleCheck_RequiresALiveHolder_NotThisWorker()
    {
        using var directory = new TempDirectory("arcanecore-claims-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync(18, a);
        await rig.Store.TryClaimCharacterAsync(19, b);

        // The holder (b) commits for a character a has claimed and for its own: both are held by live workers.
        await rig.WriteAsync(18, b, CharacterClaimRequirement.HeldByAnyLiveWorker, 19);
        // A character nobody claims is refused.
        await AssertRefusedAsync<CharacterClaimRejectedException>(() => rig.WriteAsync(20, b, CharacterClaimRequirement.HeldByAnyLiveWorker));
        // A claim whose owner is gone is refused.
        await rig.ReleaseShardAsync(a);
        await AssertRefusedAsync<CharacterClaimRejectedException>(() => rig.WriteAsync(18, b, CharacterClaimRequirement.HeldByAnyLiveWorker, 19));
        await rig.WriteAsync(19, b, CharacterClaimRequirement.HeldByAnyLiveWorker);
    }

    /// <summary>The write was refused with <typeparamref name="T"/>, possibly wrapped by the persistence layer.</summary>
    private static async Task AssertRefusedAsync<T>(Func<Task> write) where T : Exception
    {
        Exception? error = await Record.ExceptionAsync(write);
        var chain = new List<Type>();
        for (Exception? e = error; e is not null; e = e.InnerException)
            chain.Add(e.GetType());
        Assert.Contains(typeof(T), chain);
    }

    /// <summary>Records every command's SQL, to prove what a configuration does and does not send to the database.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Texts { get { lock (_gate) return [.. _texts]; } }

        public void Clear() { lock (_gate) _texts.Clear(); }

        private void Record(DbCommand command) { lock (_gate) _texts.Add(command.CommandText); }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return new(result); }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Record(command); return new(result); }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Record(command); return new(result); }
    }

    /// <summary>One SQLite characters database with the write fence on, a process-wide ownership holder the cases switch between workers, and the claim store.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Rig(ServiceProvider services, CommandLog log)
        {
            _services = services;
            Log = log;
            Holder = services.GetRequiredService<CharacterOwnershipContext>();
            Store = services.GetRequiredService<EfTransferDecisionStore>();
        }

        public CommandLog Log { get; }
        public CharacterOwnershipContext Holder { get; }
        public EfTransferDecisionStore Store { get; }
        public IServiceProvider Services => _services;

        public static async Task<Rig> CreateAsync(TempDirectory directory)
        {
            string file = Path.Combine(directory.Path, "characters.db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={file}",
                [DataServiceCollectionExtensions.FencingEnabledKey] = "true",
            }).Build();
            var log = new CommandLog();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCharacterDatabase(configuration);
            services.ConfigureDbContext<CharacterDbContext>(builder => builder.AddInterceptors(log));
            ServiceProvider provider = services.BuildServiceProvider();
            await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            return new Rig(provider, log);
        }

        public async Task<int> CreateCharacterAsync(string name)
        {
            // Before any ownership row is active, so the tokenless write is allowed.
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
        }

        public async Task<CharacterOwnershipToken> AcquireWorkerAsync(string shard, uint map)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
                .TryAcquireAsync(CharacterOwnershipScopesFor(shard), "node-" + shard, Guid.NewGuid(), [map]))!;
        }

        public async Task<CharacterOwnershipToken> AcquireWorldAsync()
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryAcquireAsync("world", "node-world", Guid.NewGuid()))!;
        }

        public async Task<CharacterOwnershipToken?> ReclaimAsync(CharacterOwnershipToken observed)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryReclaimAsync(observed, "node-restarted", Guid.NewGuid(), [1u]);
        }

        public async Task<bool> HeartbeatAsync(CharacterOwnershipToken token, long nowUnixMs)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().HeartbeatAsync(token, nowUnixMs);
        }

        public async Task ReleaseShardAsync(CharacterOwnershipToken token)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().ReleaseAsync(token);
        }

        public async Task<CharacterOwnershipRow> OwnershipRowAsync(string scopeName)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            return await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scopeName);
        }

        public async Task<CharacterRestState?> RestOfAsync(int characterId)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterRestStore>().LoadAsync(characterId);
        }

        /// <summary>Begin and commit a characters transaction as the worker holding <paramref name="as"/>, under the claim scope the write queues set; the fence verifies at begin.</summary>
        public async Task WriteAsync(int characterId, CharacterOwnershipToken @as, CharacterClaimRequirement requirement = CharacterClaimRequirement.HeldByThisWorker, params int[] more)
        {
            Holder.Current = @as;
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            using (requirement == CharacterClaimRequirement.HeldByThisWorker && more.Length == 0
                ? CharacterWriteClaim.Use(characterId)
                : CharacterWriteClaim.Use([characterId, .. more], requirement))
            {
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
                await transaction.CommitAsync();
            }
        }

        /// <summary>Runs the transfer module's character cleanup (the deletion hook) as the worker holding <paramref name="as"/>.</summary>
        public async Task DeleteClaimDataAsync(int characterId, CharacterOwnershipToken @as)
        {
            Holder.Current = @as;
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
            await new ClusterTransferDataModule().DeleteCharacterDataAsync(db, characterId, CancellationToken.None);
            await transaction.CommitAsync();
        }

        private static string CharacterOwnershipScopesFor(string shard) => CharacterOwnershipScopes.ForShard(shard);

        public async ValueTask DisposeAsync() => await _services.DisposeAsync();
    }
}
