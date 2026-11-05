using System.Data;
using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests.Cluster;

/// <summary>
/// The row locks of the lease on a real PostgreSQL (reported skipped without a server). A fenced writer holds <c>FOR KEY SHARE</c> on the lease row until its own commit; a renewal changes only
/// <c>expires_at_unix_ms</c> (<c>FOR NO KEY UPDATE</c>, no conflict), so it never waits for the writer's commit; every change of authority takes <c>FOR UPDATE</c> first (conflicts), so it waits for in-flight fenced
/// commits and a fenced write is wholly before or wholly after it. Orderings are asserted with bounded real waits (a blocked backend is looked up in pg_stat_activity, not guessed with a sleep).
/// </summary>
public sealed class LeaseRowLockTests : IAsyncLifetime
{
    private const string Application = "arcane-lease-test";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Scope = RealmAuthorityScopes.Social;
    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private sealed record Env(DatabaseConnectionOptions Connection, EfRealmAuthorityLeaseStore Store, Func<CharacterDbContext> Factory);

    private async Task<Env> CreateAsync(int poolSize = 2)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.PostgreSql);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);

        Func<CharacterDbContext> factory = EfRealmAuthorityLeaseStore.DedicatedContextFactory(connection, Application, poolSize, TimeSpan.FromSeconds(5))!;
        var store = new EfRealmAuthorityLeaseStore(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeSpan.FromSeconds(5), factory);
        return new Env(connection, store, factory);
    }

    private static RealmLeaseFence FenceOf(AuthorityLeaseResult granted, string node, Guid incarnation)
        => new(Scope, node, incarnation, granted.Lease!.FencingToken);

    /// <summary>A fenced writer's transaction that has passed the fence (holds its row lock) and stays open.</summary>
    private sealed class OpenFencedWriter(CharacterDbContext db, IDbContextTransaction transaction) : IAsyncDisposable
    {
        public async Task CommitAsync() => await transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private static async Task<OpenFencedWriter> HoldFenceAsync(DatabaseConnectionOptions connection, RealmLeaseFence fence)
    {
        CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await LeaseFencedWrite.EnsureHeldAsync(db, fence, CancellationToken.None);
        }
        catch
        {
            await transaction.DisposeAsync();
            await db.DisposeAsync();
            throw;
        }

        return new OpenFencedWriter(db, transaction);
    }

    private static async Task<long> ScalarAsync(DatabaseConnectionOptions connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection.ConnectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(DatabaseConnectionOptions connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection.ConnectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand(sql, conn);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Waits (bounded) until a lease backend is blocked on a row lock of a statement containing <paramref name="statementFragment"/>.</summary>
    private static async Task<bool> WaitForBlockedBackendAsync(DatabaseConnectionOptions connection, string statementFragment)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < Bound)
        {
            long blocked = await ScalarAsync(connection,
                $"SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{Application}' AND wait_event_type = 'Lock' AND query LIKE '%{statementFragment}%'");
            if (blocked > 0)
                return true;
            await Task.Delay(50);
        }

        return false;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan limit) => await Task.WhenAny(task, Task.Delay(limit)) == task;

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task ARenewal_CompletesPromptly_WhileAFencedTransactionHoldsItsLock()
    {
        Env env = await CreateAsync();
        Guid a = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);
        await using OpenFencedWriter writer = await HoldFenceAsync(env.Connection, FenceOf(granted, "node-a", a));

        Task<AuthorityLeaseResult> renewal = Task.Run(() => env.Store.TryRenew(Scope, "node-a", a, granted.Lease!.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(10)));

        Assert.True(await CompletesWithinAsync(renewal, TimeSpan.FromSeconds(3)), "a renewal must not wait for a fenced writer's commit (the command timeout is 5 s)");
        Assert.Equal(AuthorityLeaseOutcome.Renewed, (await renewal).Outcome);
    }

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task ATakeover_WaitsForTheInFlightFencedCommit_ThenTheTokenChanges_AndTheStaleWriterIsRefused()
    {
        Env env = await CreateAsync();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);
        RealmLeaseFence stale = FenceOf(granted, "node-a", a);
        await using OpenFencedWriter writer = await HoldFenceAsync(env.Connection, stale);

        Task<AuthorityLeaseResult> takeover = Task.Run(() => env.Store.TryAcquire(Scope, "node-b", b, Ttl, T0 + Ttl + TimeSpan.FromSeconds(1)));

        Assert.True(await WaitForBlockedBackendAsync(env.Connection, "FOR UPDATE"), "the takeover must be blocked on the fenced writer's row lock");
        Assert.False(takeover.IsCompleted);
        Assert.Equal(granted.Lease!.FencingToken, await ScalarAsync(env.Connection, $"SELECT fencing_token FROM realm_authority_lease WHERE scope = '{Scope}'"));

        await writer.CommitAsync(); // the in-flight fenced write commits first
        Assert.True(await CompletesWithinAsync(takeover, Bound), "the takeover must proceed once the fenced transaction ended");
        AuthorityLeaseResult taken = await takeover;
        Assert.Equal(AuthorityLeaseOutcome.Granted, taken.Outcome);
        Assert.True(taken.Lease!.FencingToken > granted.Lease.FencingToken);

        // The stale token is now refused: at the fence itself, and in a real store write, which writes nothing.
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(env.Connection))
        await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(() => LeaseFencedWrite.EnsureHeldAsync(db, stale, CancellationToken.None));
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(env.Connection))
            await Assert.ThrowsAsync<RealmLeaseFenceLostException>(() => new EfSocialStore(db, new FixedSource(stale)).SetSocialAsync(1, 2, SocialFlags.Friend));
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(env.Connection);
        Assert.Empty(await check.Set<CharacterSocialRow>().AsNoTracking().ToListAsync());
    }

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task AFencedWriter_StartingAfterTheTakeoverCommitted_IsRefused()
    {
        Env env = await CreateAsync();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);
        AuthorityLeaseResult taken = env.Store.TryAcquire(Scope, "node-b", b, Ttl, T0 + Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(AuthorityLeaseOutcome.Granted, taken.Outcome);

        await Assert.ThrowsAsync<RealmLeaseFenceLostException>(async () => await HoldFenceAsync(env.Connection, FenceOf(granted, "node-a", a)));
        await using OpenFencedWriter current = await HoldFenceAsync(env.Connection, FenceOf(taken, "node-b", b)); // the new holder passes
    }

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task ARelease_WaitsForTheInFlightFencedCommit_Too()
    {
        Env env = await CreateAsync();
        Guid a = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);
        await using OpenFencedWriter writer = await HoldFenceAsync(env.Connection, FenceOf(granted, "node-a", a));

        Task<AuthorityLeaseResult> release = Task.Run(() => env.Store.TryRelease(Scope, "node-a", a, granted.Lease!.FencingToken));

        Assert.True(await WaitForBlockedBackendAsync(env.Connection, "FOR UPDATE"), "the release must be blocked on the fenced writer's row lock");
        Assert.False(release.IsCompleted);
        await writer.CommitAsync();
        Assert.True(await CompletesWithinAsync(release, Bound));
        Assert.Equal(AuthorityLeaseOutcome.Released, (await release).Outcome);
    }

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task TheLeaseTable_HasOnlyItsScopePrimaryKey_SoARenewalNeverTakesAKeyUpdateLock()
    {
        Env env = await CreateAsync();

        Assert.Equal(1, await ScalarAsync(env.Connection, "SELECT count(*) FROM pg_indexes WHERE tablename = 'realm_authority_lease'"));
        Assert.Equal(1, await ScalarAsync(env.Connection, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'realm_authority_lease'::regclass AND contype = 'p'"));
        Assert.Equal(0, await ScalarAsync(env.Connection, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'realm_authority_lease'::regclass AND contype IN ('u', 'x')"));
        Assert.Equal(0, await ScalarAsync(env.Connection, "SELECT count(*) FROM pg_constraint WHERE confrelid = 'realm_authority_lease'::regclass"));
        Assert.Equal("scope", await TextAsync(env.Connection,
            "SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey) WHERE i.indrelid = 'realm_authority_lease'::regclass AND i.indisprimary"));
    }

    /// <summary>
    /// The economy commit runs Serializable and takes the fence as its first statements. A renewal that commits after the transaction's snapshot but before its fence lock updates the row the lock wants. With the old lock
    /// mode (FOR SHARE) that raised 40001 (observed on PostgreSQL: the lock cannot follow a concurrent update under a transaction snapshot); FOR KEY SHARE does not conflict with the renewal's non-key update and succeeds,
    /// so the new mode produces no new 40001 and removes that one.
    /// </summary>
    [LeaseRenewalPoolTests.PostgresFact]
    public async Task ARenewalAfterASerializableSnapshot_DoesNotFailTheFenceLock()
    {
        Env env = await CreateAsync();
        Guid a = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);

        await using var conn = new NpgsqlConnection(env.Connection.ConnectionString);
        await conn.OpenAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var snapshot = new NpgsqlCommand("SELECT count(*) FROM realm_authority_lease", conn, tx))
            await snapshot.ExecuteScalarAsync();
        env.Store.TryRenew(Scope, "node-a", a, granted.Lease!.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(10)); // commits after the snapshot

        await using var fence = new NpgsqlCommand(
            $"SELECT 1 FROM realm_authority_lease WHERE scope = '{Scope}' AND fencing_token = {granted.Lease.FencingToken} FOR KEY SHARE", conn, tx);
        Assert.NotNull(await fence.ExecuteScalarAsync()); // would raise 40001 with FOR SHARE
        await tx.CommitAsync();
    }

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task SetLocalSynchronousCommit_DoesNotLeakToTheNextUseOfThePooledConnection()
    {
        Env env = await CreateAsync(poolSize: 1); // one physical connection: the next use is the same backend
        Guid a = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);
        string baseline = await TextAsync(env.Connection, "SHOW synchronous_commit");
        Assert.NotEqual("off", baseline); // a server that is configured off would make this check meaningless

        long pidBefore;
        await using (CharacterDbContext first = env.Factory())
            pidBefore = Convert.ToInt64(await first.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync());
        Assert.Equal(AuthorityLeaseOutcome.Renewed, env.Store.TryRenew(Scope, "node-a", a, granted.Lease!.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(10)).Outcome);

        await using CharacterDbContext next = env.Factory();
        long pidAfter = Convert.ToInt64(await next.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync());
        string setting = await next.Database.SqlQueryRaw<string>("SELECT current_setting('synchronous_commit') AS \"Value\"").SingleAsync();

        Assert.Equal(pidBefore, pidAfter); // the renewal and this read really shared a backend
        Assert.Equal(baseline, setting);
    }

    [LeaseRenewalPoolTests.PostgresFact]
    public async Task TheDedicatedContext_OpensItsOwnConnections_AndRenews_WithinThePoolSize()
    {
        Env env = await CreateAsync();
        Guid a = Guid.NewGuid();
        AuthorityLeaseResult granted = env.Store.TryAcquire(Scope, "node-a", a, Ttl, T0);

        Task<AuthorityLeaseResult>[] renewals = Enumerable.Range(1, 8)
            .Select(i => Task.Run(() => env.Store.TryRenew(Scope, "node-a", a, granted.Lease!.FencingToken, Ttl, T0 + TimeSpan.FromSeconds(i))))
            .ToArray();
        AuthorityLeaseResult[] results = await Task.WhenAll(renewals).WaitAsync(Bound);

        Assert.All(results, r => Assert.Equal(AuthorityLeaseOutcome.Renewed, r.Outcome));
        long backends = await ScalarAsync(env.Connection, $"SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = '{Application}'");
        Assert.InRange(backends, 1, 2); // its own pool, capped at the configured size
        Assert.True(await ScalarAsync(env.Connection, $"SELECT expires_at_unix_ms FROM realm_authority_lease WHERE scope = '{Scope}'") >= (T0 + TimeSpan.FromSeconds(1) + Ttl).ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// A commit that stalls (here a deferred constraint trigger that sleeps 4 s when COMMIT runs, standing in for a WAL flush that stalls) is bounded by the dedicated pool's 1 s command timeout: it is the connection
    /// string's Command Timeout that reaches the internal COMMIT command, EF's per-command timeout does not.
    /// </summary>
    [LeaseRenewalPoolTests.PostgresFact]
    public async Task TheDedicatedPoolsCommandTimeout_BoundsACommit()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.PostgreSql);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            await setup.Database.ExecuteSqlRawAsync("CREATE TABLE commit_stall (id integer)");
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE FUNCTION commit_stall_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(4); RETURN NULL; END $$");
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE CONSTRAINT TRIGGER commit_stall_t AFTER INSERT ON commit_stall DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION commit_stall_fn()");
        }

        Func<CharacterDbContext> factory = EfRealmAuthorityLeaseStore.DedicatedContextFactory(connection, Application, 1, TimeSpan.FromSeconds(1))!;
        await using CharacterDbContext db = factory();
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO commit_stall VALUES (1)");

        Stopwatch watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => tx.CommitAsync());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3.5), $"the commit took {watch.Elapsed.TotalSeconds:F1} s; the 1 s command timeout must bound it");
    }

    private sealed class FixedSource(RealmLeaseFence fence) : IRealmLeaseFenceSource
    {
        public RealmLeaseFence? FenceFor(string scope) => scope == fence.Scope ? fence : null;
    }
}
