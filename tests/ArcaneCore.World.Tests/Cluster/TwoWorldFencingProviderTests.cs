using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Characters.Ids;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Two in-process fenced <see cref="WorldHost"/> instances with distinct <c>Cluster:Fencing:OwnerNode</c> values on one server characters database
/// (these are test hosts, not cluster processes). Each host has its own container, connection pool and writer guard, so the database sees two
/// independent clients exactly as it would from two processes. The hosts take the database over one from the other the way a failover does: the first
/// holds the durable claim and the advisory writer lock; once its lock session is gone (the process died) the second claims the database with an
/// explicit takeover, and the first, still alive in this process, is the stale old owner.
/// <list type="number">
/// <item>Concurrent id reservations from both hosts never overlap, in the item, group, guild, instance, mail and auction scopes.</item>
/// <item>A save by the old owner after the takeover is refused and writes nothing.</item>
/// <item><c>realm_authority_lease</c> contends correctly across the two hosts' connections.</item>
/// <item>The advisory writer lock refuses an unfenced third host, and the durable claim refuses it once the lock is gone.</item>
/// <item>Worker scope (<c>Cluster:Fencing:Scope=Worker</c>): two shards start, the same shard is refused by the shard advisory lock, the realm-lock probe makes a worker
/// back out of an unfenced Combined world, and a restarted shard reclaims its dead incarnation so the old token is refused.</item>
/// </list>
/// They run on MariaDB and PostgreSQL where <c>ARCANECORE_TEST_MARIADB</c> / <c>ARCANECORE_TEST_POSTGRES</c> name a server (a server connection string
/// without a database); without either they are reported as Skipped with that reason, never silently passed.
/// </summary>
public sealed class TwoWorldFencingProviderTests
{
    private const string MariaDbVariable = "ARCANECORE_TEST_MARIADB";
    private const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";

    /// <summary>The server providers configured in this environment; empty when neither variable is set, which <see cref="TwoWorldTheoryAttribute"/> reports as Skipped.</summary>
    public static IEnumerable<object[]> ServerProviders()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MariaDbVariable)))
            yield return [DatabaseProvider.MariaDb];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
            yield return [DatabaseProvider.PostgreSql];
    }

    private sealed class TwoWorldTheoryAttribute : TheoryAttribute
    {
        public TwoWorldTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MariaDbVariable))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
                Skip = $"Set {PostgresVariable} and/or {MariaDbVariable} to a server connection string (no database) to run the two-world fencing proof on a real provider.";
        }
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ConcurrentIdReservations_FromTwoFencedHosts_NeverOverlap_InEveryScope(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        await using TakeoverPair pair = await cluster.StartTakeoverPairAsync();
        Assert.NotEqual(pair.A.Host.OwnerNode, pair.B.Host.OwnerNode);

        string[] scopes = [IdRangeScopes.Item, IdRangeScopes.Group, IdRangeScopes.Guild, IdRangeScopes.Instance, IdRangeScopes.Mail, IdRangeScopes.Auction];
        const uint BlockSize = 8;
        const int WorkersPerHostAndScope = 2;
        const int Rounds = 6;
        var blocks = new ConcurrentBag<(string Host, IdRangeBlock Block)>();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new List<Task>();
        foreach (HostKit host in new[] { pair.A, pair.B })
        {
            foreach (string scopeName in scopes)
            {
                for (int w = 0; w < WorkersPerHostAndScope; w++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        await go.Task;
                        for (int round = 0; round < Rounds; round++)
                        {
                            await using AsyncServiceScope scope = host.Scopes.CreateAsyncScope();
                            IIdRangeReservationStore store = scope.ServiceProvider.GetRequiredService<IIdRangeReservationStore>();
                            blocks.Add((host.Host.OwnerNode, await store.ReserveBlockAsync(scopeName, BlockSize)));
                        }
                    }));
                }
            }
        }

        go.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(3));

        foreach (string scopeName in scopes)
        {
            (string Host, IdRangeBlock Block)[] mine = [.. blocks.Where(b => b.Block.Scope == scopeName).OrderBy(b => b.Block.Start)];
            int expected = 2 * WorkersPerHostAndScope * Rounds;
            Assert.Equal(expected, mine.Length);
            Assert.All(mine, b => Assert.Equal(BlockSize, b.Block.Count));
            for (int i = 1; i < mine.Length; i++)
            {
                Assert.True(mine[i].Block.Start > mine[i - 1].Block.EndInclusive,
                    $"{scopeName}: block {mine[i].Block} ({mine[i].Host}) overlaps {mine[i - 1].Block} ({mine[i - 1].Host})");
            }

            // Contiguous as well: nothing is skipped or handed out twice, and the durable high-water mark is exactly one past the last block.
            Assert.Equal((ulong)expected * BlockSize, (ulong)mine[^1].Block.EndInclusive - mine[0].Block.Start + 1UL);
            Assert.True(mine[0].Block.Start >= IdRangeScopes.GetMinFirstId(scopeName));
            Assert.Contains(mine, b => b.Host == pair.A.Host.OwnerNode);
            Assert.Contains(mine, b => b.Host == pair.B.Host.OwnerNode);
            await using CharacterDbContext db = cluster.PlainContext();
            CharacterIdRangeRow row = await db.Set<CharacterIdRangeRow>().AsNoTracking().SingleAsync(r => r.Scope == scopeName);
            Assert.Equal((long)mine[^1].Block.EndInclusive + 1, row.NextId);
        }
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ASaveByTheOldOwner_AfterTakeover_IsRefused_AndTheNewOwnersSaveLands(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        int id = await cluster.CreateCharacterAsync("TwoWorld");
        await using TakeoverPair pair = await cluster.StartTakeoverPairAsync();
        CharacterOwnershipToken oldToken = pair.OldToken;
        CharacterOwnershipToken newToken = pair.B.World.CharacterOwnership!;
        Assert.True(newToken.Epoch > oldToken.Epoch);
        Assert.NotEqual(oldToken.Incarnation, newToken.Incarnation);
        Assert.Equal(oldToken, pair.A.World.CharacterOwnership); // the old host still believes it owns the database
        Assert.Equal(oldToken, pair.A.Ownership.Current);

        await SaveAsync(pair.B, State(id, 13, newToken));
        Assert.Equal(13u, await cluster.MapOfAsync(id));

        // The old owner saves with its own (stale) token, and with no token at all so the ambient stale one applies: both refused, nothing written.
        await AssertRejectedAsync(() => SaveAsync(pair.A, State(id, 21, oldToken)));
        await AssertRejectedAsync(() => SaveAsync(pair.A, State(id, 22, null)));
        Assert.Equal(13u, await cluster.MapOfAsync(id));

        // The current owner is unaffected by the stale writer.
        await SaveAsync(pair.B, State(id, 14, newToken));
        Assert.Equal(14u, await cluster.MapOfAsync(id));
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task RealmAuthorityLease_ContendsCorrectly_AcrossTwoHostsConnections(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        await using TakeoverPair pair = await cluster.StartTakeoverPairAsync();
        var storeA = new EfRealmAuthorityLeaseStore(pair.A.Scopes);
        var storeB = new EfRealmAuthorityLeaseStore(pair.B.Scopes);
        (string Node, Guid Incarnation, EfRealmAuthorityLeaseStore Store) a = (pair.A.Host.OwnerNode, pair.A.Host.Incarnation, storeA);
        (string Node, Guid Incarnation, EfRealmAuthorityLeaseStore Store) b = (pair.B.Host.OwnerNode, pair.B.Host.Incarnation, storeB);
        TimeSpan ttl = TimeSpan.FromMinutes(30);
        DateTimeOffset start = DateTimeOffset.UtcNow;

        foreach (string scope in RealmAuthorityLeaseDataModule.Scopes)
        {
            long expectedToken = 0;
            for (int round = 1; round <= 8; round++)
            {
                // Each round is an hour later than the last, so the previous round's 30 minute lease has lapsed and both hosts race for the same expired row.
                DateTimeOffset now = start + TimeSpan.FromHours(round);
                using var barrier = new Barrier(2);
                Task<AuthorityLeaseResult> raceA = Task.Run(() => { barrier.SignalAndWait(); return a.Store.TryAcquire(scope, a.Node, a.Incarnation, ttl, now); });
                Task<AuthorityLeaseResult> raceB = Task.Run(() => { barrier.SignalAndWait(); return b.Store.TryAcquire(scope, b.Node, b.Incarnation, ttl, now); });
                AuthorityLeaseResult resultA = await raceA.WaitAsync(TimeSpan.FromMinutes(2));
                AuthorityLeaseResult resultB = await raceB.WaitAsync(TimeSpan.FromMinutes(2));

                bool aWon = resultA.Outcome == AuthorityLeaseOutcome.Granted;
                Assert.True(aWon ^ (resultB.Outcome == AuthorityLeaseOutcome.Granted), $"{scope} round {round}: exactly one host is granted (A {resultA.Outcome}, B {resultB.Outcome})");
                var (winner, loser) = aWon ? (a, b) : (b, a);
                AuthorityLeaseResult won = aWon ? resultA : resultB;
                AuthorityLeaseResult lost = aWon ? resultB : resultA;
                expectedToken++;
                Assert.Equal(expectedToken, won.Lease!.FencingToken);
                Assert.Equal(AuthorityLeaseOutcome.RejectedHeldByOther, lost.Outcome);
                Assert.Equal(winner.Node, lost.Lease!.OwnerNodeId); // the loser is told who holds it
                Assert.Equal(expectedToken, lost.Lease.FencingToken);

                // Seen from the other connection: the loser cannot renew, validate or release with the winner's token; the winner can renew.
                Assert.Equal(AuthorityLeaseOutcome.RejectedHeldByOther,
                    loser.Store.TryRenew(scope, loser.Node, loser.Incarnation, expectedToken, ttl, now).Outcome);
                Assert.True(loser.Store.ValidateFencingToken(scope, winner.Node, winner.Incarnation, expectedToken, now));
                Assert.False(winner.Store.ValidateFencingToken(scope, loser.Node, loser.Incarnation, expectedToken, now));
                Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken, loser.Store.TryRelease(scope, loser.Node, loser.Incarnation, expectedToken).Outcome);
                AuthorityLeaseResult renewed = winner.Store.TryRenew(scope, winner.Node, winner.Incarnation, expectedToken, ttl, now);
                Assert.Equal(AuthorityLeaseOutcome.Renewed, renewed.Outcome);
                Assert.Equal(expectedToken, renewed.Lease!.FencingToken);
                Assert.Equal(winner.Node, loser.Store.GetCurrent(scope, now)!.OwnerNodeId);
            }

            // A release keeps the high-water mark: the next holder, on the other connection, gets a strictly higher token.
            RealmAuthorityLease held = storeA.GetCurrent(scope, start + TimeSpan.FromHours(8))!;
            var (holder, other) = held.OwnerNodeId == a.Node ? (a, b) : (b, a);
            Assert.Equal(AuthorityLeaseOutcome.Released, holder.Store.TryRelease(scope, holder.Node, holder.Incarnation, held.FencingToken).Outcome);
            Assert.Null(other.Store.GetCurrent(scope, start + TimeSpan.FromHours(8)));
            Assert.Equal(expectedToken, other.Store.GetHighWaterToken(scope));
            AuthorityLeaseResult next = other.Store.TryAcquire(scope, other.Node, other.Incarnation, ttl, start + TimeSpan.FromHours(8));
            Assert.Equal(AuthorityLeaseOutcome.Granted, next.Outcome);
            Assert.Equal(expectedToken + 1, next.Lease!.FencingToken);
        }
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task TheAdvisoryWriterLock_RefusesAnUnfencedThirdHost_AndTheDurableClaimRefusesItOnceTheLockIsGone(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        int id = await cluster.CreateCharacterAsync("ThirdHost");
        await using HostKit first = cluster.NewHost("node-a", fenced: true);
        await first.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken owned = first.World.CharacterOwnership!;

        await using HostKit third = cluster.NewHost("node-c", fenced: false);
        CharacterDatabaseAlreadyOwnedException refused = await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(
            () => third.Host.StartAsync(CancellationToken.None));
        Assert.StartsWith("the characters database is already owned by another world process", refused.Message);
        Assert.False(third.Feature.Attached); // no feature attached, so no restore ran
        Assert.Null(third.Guard.LockPath); // the refused start holds nothing
        Assert.Equal(owned, await CurrentClaimAsync(first)); // the durable claim is untouched
        await SaveAsync(first, State(id, 31, owned)); // and the first host still writes
        Assert.Equal(31u, await cluster.MapOfAsync(id));

        // The first host's lock session is gone (its process died) but its fenced claim stands: the unfenced third host is still refused, now by the claim.
        first.Guard.Dispose();
        await using HostKit retry = cluster.NewHost("node-c", fenced: false);
        InvalidOperationException claimed = await Assert.ThrowsAsync<InvalidOperationException>(() => retry.Host.StartAsync(CancellationToken.None));
        Assert.IsNotType<CharacterDatabaseAlreadyOwnedException>(claimed);
        Assert.Contains("already owned", claimed.Message);
        Assert.False(retry.Feature.Attached);
        Assert.Null(retry.Guard.LockPath);
        Assert.Equal(owned, await CurrentClaimAsync(first));
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task WorkerScope_TwoShardsStart_AndTheSameShardIsRefusedByTheShardLock(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        await using HostKit a = cluster.NewWorker("shard-a", "0,1");
        await using HostKit b = cluster.NewWorker("shard-b", "30,31");
        await a.Host.StartAsync(CancellationToken.None);
        await b.Host.StartAsync(CancellationToken.None);

        Assert.Equal("worker:shard-a", a.World.CharacterOwnership!.Scope);
        Assert.Equal("worker:shard-b", b.World.CharacterOwnership!.Scope);
        Assert.Equal(a.World.CharacterOwnership, await CurrentClaimAsync(a, "worker:shard-a"));
        Assert.Equal(b.World.CharacterOwnership, await CurrentClaimAsync(b, "worker:shard-b"));
        Assert.Null(await CurrentClaimAsync(a, "world")); // the realm row stays inactive

        await using HostKit duplicate = cluster.NewWorker("shard-a", "0,1");
        CharacterDatabaseAlreadyOwnedException refused = await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(
            () => duplicate.Host.StartAsync(CancellationToken.None));
        Assert.Contains("already owned by another world process", refused.Message);
        Assert.False(duplicate.Feature.Attached);
        Assert.Null(duplicate.Guard.LockPath); // the refused start holds nothing
        Assert.Equal(a.World.CharacterOwnership, await CurrentClaimAsync(a, "worker:shard-a")); // the live worker's claim is untouched
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task WorkerScope_BacksOut_WhenAnUnfencedCombinedWorldHoldsTheRealmLock(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        await using HostKit combined = cluster.NewHost("node-c", fenced: false);
        await combined.Host.StartAsync(CancellationToken.None); // takes the realm advisory lock and no durable claim

        await using HostKit worker = cluster.NewWorker("shard-a", "0,1");
        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker.Host.StartAsync(CancellationToken.None));

        Assert.Contains("realm writer lock", refused.Message);
        Assert.False(worker.Feature.Attached);
        Assert.Null(await CurrentClaimAsync(combined, "worker:shard-a")); // the claim written before the probe was released again
    }

    [TwoWorldTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task WorkerScope_ARestartedShardReclaimsItsDeadIncarnation_AndTheOldTokenIsRefused(DatabaseProvider provider)
    {
        await using ScratchCluster cluster = await ScratchCluster.CreateAsync(provider);
        int id = await cluster.CreateCharacterAsync("WorkerReclaim");
        await using HostKit old = cluster.NewWorker("shard-a", "0,1");
        await old.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken oldToken = old.World.CharacterOwnership!;
        await SaveAsync(old, State(id, 5, oldToken));
        Assert.Equal(5u, await cluster.MapOfAsync(id));

        // A hard kill: the shard lock session ends, the row stays active. The old host object remains alive here as the zombie.
        old.Guard.Dispose();

        await using HostKit restarted = cluster.NewWorker("shard-a", "0,1");
        await restarted.Host.StartAsync(CancellationToken.None);
        CharacterOwnershipToken newToken = restarted.World.CharacterOwnership!;
        Assert.Equal(oldToken.Scope, newToken.Scope);
        Assert.Equal(oldToken.Epoch + 1, newToken.Epoch);
        Assert.NotEqual(oldToken.Incarnation, newToken.Incarnation);
        Assert.Equal(newToken, await CurrentClaimAsync(restarted, newToken.Scope));

        await AssertRejectedAsync(() => SaveAsync(old, State(id, 6, oldToken)));
        Assert.Equal(5u, await cluster.MapOfAsync(id));
        await SaveAsync(restarted, State(id, 7, newToken));
        Assert.Equal(7u, await cluster.MapOfAsync(id));
    }

    private static async Task<CharacterOwnershipToken?> CurrentClaimAsync(HostKit host, string scopeName)
    {
        await using AsyncServiceScope scope = host.Scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().GetCurrentAsync(scopeName);
    }

    private static async Task<CharacterOwnershipToken?> CurrentClaimAsync(HostKit host)
    {
        await using AsyncServiceScope scope = host.Scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().GetCurrentAsync("world");
    }

    private static async Task SaveAsync(HostKit host, CharacterState state)
    {
        await using AsyncServiceScope scope = host.Scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterStore>().SaveStateAsync(state);
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken? ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);

    /// <summary>The ownership rejection, found at any depth of the thrown exception (EF may wrap what an interceptor throws).</summary>
    private static async Task AssertRejectedAsync(Func<Task> write)
    {
        Exception? thrown = await Record.ExceptionAsync(write);
        Assert.NotNull(thrown);
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException)
                return;
        }

        Assert.Fail("expected a CharacterOwnershipRejectedException but got " + thrown);
    }

    /// <summary>One scratch characters database on the configured server, dropped at the end, and the hosts that run against it.</summary>
    private sealed class ScratchCluster : IAsyncDisposable
    {
        private readonly DatabaseConnectionOptions _database;
        private readonly string _name;

        private ScratchCluster(DatabaseConnectionOptions database, string name)
        {
            _database = database;
            _name = name;
        }

        public static async Task<ScratchCluster> CreateAsync(DatabaseProvider provider)
        {
            string name = "arcane_t_" + Guid.NewGuid().ToString("N")[..16];
            string server = Environment.GetEnvironmentVariable(provider == DatabaseProvider.PostgreSql ? PostgresVariable : MariaDbVariable)!.TrimEnd(';');
            var cluster = new ScratchCluster(new DatabaseConnectionOptions { Provider = provider, ConnectionString = server + ";Database=" + name }, name);

            // The schema bootstrapper creates the database and every table; the hosts below find it ready.
            await using ServiceProvider bootstrap = BuildServices(cluster._database, fenced: false);
            await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            return cluster;
        }

        public CharacterDbContext PlainContext()
        {
            var builder = new DbContextOptionsBuilder<CharacterDbContext>();
            DataServiceCollectionExtensions.ConfigureProvider(builder, _database);
            return new CharacterDbContext(builder.Options);
        }

        public async Task<int> CreateCharacterAsync(string name)
        {
            await using CharacterDbContext db = PlainContext();
            return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
        }

        public async Task<uint> MapOfAsync(int id)
        {
            await using CharacterDbContext db = PlainContext();
            return (await new EfCharacterStore(db).GetByIdAsync(id))!.MapId;
        }

        public HostKit NewHost(string ownerNode, bool fenced, CharacterOwnershipToken? takeover = null)
            => HostKit.Create(BuildServices(_database, fenced), ownerNode, fenced, takeover);

        /// <summary>A Worker-scope host for one shard that owns the given maps (a test host, not a cluster process).</summary>
        public HostKit NewWorker(string shard, string ownedMaps)
            => HostKit.Create(BuildServices(_database, fenced: true), "node-" + shard, fenced: true, takeover: null, shard, ownedMaps);

        /// <summary>
        /// Host A starts and claims the database; its lock session then ends (the process is gone) and host B starts with an explicit takeover of A's claim.
        /// Both hosts are alive afterwards: B is the owner and A the stale old owner.
        /// </summary>
        public async Task<TakeoverPair> StartTakeoverPairAsync()
        {
            HostKit a = NewHost("node-a", fenced: true);
            HostKit? b = null;
            try
            {
                await a.Host.StartAsync(CancellationToken.None);
                CharacterOwnershipToken oldToken = a.World.CharacterOwnership ?? throw new InvalidOperationException("host A holds no claim after start");
                a.Guard.Dispose();
                b = NewHost("node-b", fenced: true, takeover: oldToken);
                await b.Host.StartAsync(CancellationToken.None);
                return new TakeoverPair(a, b, oldToken);
            }
            catch
            {
                if (b is not null) await b.DisposeAsync();
                await a.DisposeAsync();
                throw;
            }
        }

        private static ServiceProvider BuildServices(DatabaseConnectionOptions database, bool fenced)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Database:Provider"] = database.Provider.ToString(),
                ["Database:ConnectionString"] = database.ConnectionString,
            };
            if (fenced)
                settings[DataServiceCollectionExtensions.FencingEnabledKey] = "true";
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCharacterDatabase(configuration);
            services.AddSingleton<IWorldFeature, Feature>();
            return services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            // Only this database's own pool: other test classes run in parallel against the same server.
            switch (_database.Provider)
            {
                case DatabaseProvider.PostgreSql:
                    using (var connection = new NpgsqlConnection(_database.ConnectionString))
                        NpgsqlConnection.ClearPool(connection);
                    await DropPostgresAsync();
                    break;
                default:
                    using (var connection = new MySqlConnection(_database.ConnectionString))
                        MySqlConnection.ClearPool(connection);
                    await DropMariaDbAsync();
                    break;
            }
        }

        private async Task DropPostgresAsync()
        {
            var builder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres", Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await using NpgsqlCommand drop = connection.CreateCommand();
                    drop.CommandTimeout = 120;
                    drop.CommandText = "DROP DATABASE IF EXISTS \"" + _name + "\" WITH (FORCE)";
                    await drop.ExecuteNonQueryAsync();
                    return;
                }
                catch (PostgresException ex) when (ex.SqlState == "55006" && attempt < 5)
                {
                    await Task.Delay(200);
                }
            }
        }

        private async Task DropMariaDbAsync()
        {
            var builder = new MySqlConnectionStringBuilder(_database.ConnectionString) { Database = string.Empty, Pooling = false };
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using MySqlCommand drop = connection.CreateCommand();
            drop.CommandTimeout = 120;
            drop.CommandText = "DROP DATABASE IF EXISTS `" + _name + "`";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TakeoverPair(HostKit a, HostKit b, CharacterOwnershipToken oldToken) : IAsyncDisposable
    {
        public HostKit A { get; } = a;
        public HostKit B { get; } = b;

        /// <summary>The claim host A held before host B took it over.</summary>
        public CharacterOwnershipToken OldToken { get; } = oldToken;

        public async ValueTask DisposeAsync()
        {
            await B.DisposeAsync(); // the owner stops first: the stale host's release cannot touch a claim that is no longer its own
            await A.DisposeAsync();
        }
    }

    /// <summary>One world host with its own container (and so its own connection pool), writer guard and ownership context.</summary>
    private sealed class HostKit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private HostKit(ServiceProvider services, WorldRuntime world, WorldHost host, WorldWriterGuard guard, CharacterOwnershipContext ownership, Feature feature)
        {
            _services = services;
            World = world;
            Host = host;
            Guard = guard;
            Ownership = ownership;
            Feature = feature;
            Scopes = services.GetRequiredService<IServiceScopeFactory>();
        }

        public WorldRuntime World { get; }
        public WorldHost Host { get; }
        public WorldWriterGuard Guard { get; }
        public CharacterOwnershipContext Ownership { get; }
        public Feature Feature { get; }
        public IServiceScopeFactory Scopes { get; }

        public static HostKit Create(ServiceProvider services, string ownerNode, bool fenced, CharacterOwnershipToken? takeover, string? workerShard = null, string? ownedMaps = null)
        {
            IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            var ownership = services.GetRequiredService<CharacterOwnershipContext>();
            var fencing = new CharacterFencingOptions
            {
                Enabled = fenced,
                OwnerNode = ownerNode,
                TakeoverEpoch = takeover?.Epoch,
                TakeoverIncarnation = takeover?.Incarnation,
            };
            WorldHost host;
            WorldWriterGuard guard;
            if (workerShard is null)
            {
                guard = new WorldWriterGuard(services.GetRequiredService<IOptions<DatabaseOptions>>());
                host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, Options.Create(fencing),
                    ownershipContext: ownership, writerGuard: guard) { ListenerExpected = false };
            }
            else
            {
                fencing.Scope = CharacterFencingScope.Worker;
                fencing.Shard = workerShard;
                IOptions<CharacterFencingOptions> fencingOptions = Options.Create(fencing);
                IOptions<GatewayWorkerOptions> workerOptions = Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = ownerNode, OwnedMaps = ownedMaps ?? string.Empty });
                guard = new WorldWriterGuard(services.GetRequiredService<IOptions<DatabaseOptions>>(), fencingOptions: fencingOptions, workerOptions: workerOptions);
                host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, fencingOptions,
                    ownershipContext: ownership, writerGuard: guard, workerOptions: workerOptions,
                    nodeOptions: Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker })) { ListenerExpected = false };
            }

            return new HostKit(services, world, host, guard, ownership, (Feature)services.GetServices<IWorldFeature>().Single());
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(CancellationToken.None); }
            catch (Exception) { } // a stale or refused host may fail its orderly stop; the proof is in the assertions
            Guard.Dispose();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class Feature : IWorldFeature
    {
        public bool Attached { get; private set; }

        public void Attach(WorldRuntime world) => Attached = true;
    }
}
