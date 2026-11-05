using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ids;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Gm;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Items;
using ArcaneCore.World.Pets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Step C4 of the per-worker writer lock (docs in the writer-lock design, section 3): the ids that were process counters (pet numbers, GM tickets,
/// petitions) come from durable blocks on the shared characters database, the item GUID allocator keeps doing so, and an allocator that was never
/// primed refuses to count in memory when the node runs as a Worker. Two "hosts" here are two allocator sets with their own database contexts on one
/// database, which is exactly what the database sees from two processes.
/// SQLite always runs; MariaDB and PostgreSQL run where <c>ARCANECORE_TEST_MARIADB</c> / <c>ARCANECORE_TEST_POSTGRES</c> name a server (a server
/// connection string without a database).
/// </summary>
public sealed class CrossWorkerIdAllocationTests : IAsyncLifetime
{
    private const string MariaDbVariable = "ARCANECORE_TEST_MARIADB";
    private const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";
    private const int PerHostPerScope = 10_000;

    private readonly List<DatabaseConnectionOptions> _created = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-c4-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Providers()
    {
        yield return [DatabaseProvider.Sqlite];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MariaDbVariable)))
            yield return [DatabaseProvider.MariaDb];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
            yield return [DatabaseProvider.PostgreSql];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (DatabaseConnectionOptions options in _created.Where(o => o.Provider != DatabaseProvider.Sqlite))
        {
            try
            {
                await DropAsync(options);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or TimeoutException)
            {
                failures.Add(ex); // the other databases are still dropped; the failures are reported below
            }
        }

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory is reclaimed by the OS temp cleanup.
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} scratch database(s) could not be dropped and are left on the server", failures);
    }

    /// <summary>
    /// Drops a scratch database. EnsureDeleted refuses while a pooled session is still attached (PostgreSQL 55006), and a swallowed refusal left
    /// the database on the server for good, so this closes this database's own pool, drops with FORCE (PostgreSQL) and retries.
    /// </summary>
    private static async Task DropAsync(DatabaseConnectionOptions options)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (options.Provider == DatabaseProvider.PostgreSql)
                {
                    using (var pooled = new NpgsqlConnection(options.ConnectionString))
                        NpgsqlConnection.ClearPool(pooled);
                    var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false };
                    string name = builder.Database!;
                    builder.Database = "postgres";
                    await using var connection = new NpgsqlConnection(builder.ConnectionString);
                    await connection.OpenAsync();
                    await using NpgsqlCommand drop = connection.CreateCommand();
                    drop.CommandTimeout = 120;
                    drop.CommandText = "DROP DATABASE IF EXISTS \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" WITH (FORCE)";
                    await drop.ExecuteNonQueryAsync();
                }
                else
                {
                    using (var pooled = new MySqlConnection(options.ConnectionString))
                        MySqlConnection.ClearPool(pooled);
                    var builder = new MySqlConnectionStringBuilder(options.ConnectionString) { Pooling = false };
                    string name = builder.Database;
                    builder.Database = string.Empty;
                    await using var connection = new MySqlConnection(builder.ConnectionString);
                    await connection.OpenAsync();
                    await using MySqlCommand drop = connection.CreateCommand();
                    drop.CommandTimeout = 120;
                    drop.CommandText = "DROP DATABASE IF EXISTS `" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
                    await drop.ExecuteNonQueryAsync();
                }

                return;
            }
            catch (Exception ex) when (attempt < 4 && (ex is System.Data.Common.DbException || ex is TimeoutException))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoHosts_OnOneDatabase_AllocateItemPetTicketAndPetitionIds_ConcurrentlyWithoutDuplicates(DatabaseProvider provider)
    {
        DatabaseConnectionOptions database = await CreateDatabaseAsync(provider);
        using var worldA = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        using var worldB = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        await using HostIds a = await HostIds.CreateAsync(database, worldA, hostIndex: 0);
        await using HostIds b = await HostIds.CreateAsync(database, worldB, hostIndex: 1);

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var items = new ConcurrentBag<uint>();
        var pets = new ConcurrentBag<uint>();
        var tickets = new ConcurrentBag<uint>();
        var petitions = new ConcurrentBag<uint>();
        var workers = new List<Task>();
        foreach (HostIds host in new[] { a, b })
        {
            HostIds h = host;
            workers.Add(RunAsync(go.Task, () => h.AllocateItemsAsync(PerHostPerScope, items)));
            workers.Add(RunAsync(go.Task, () => h.AllocatePetsAsync(PerHostPerScope, pets)));
            workers.Add(RunAsync(go.Task, () => h.AllocateTicketsAsync(PerHostPerScope, tickets)));
            workers.Add(RunAsync(go.Task, () => h.AllocatePetitionsAsync(PerHostPerScope, petitions)));
        }

        go.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(5));

        AssertUnique(items, "item");
        AssertUnique(pets, "pet");
        AssertUnique(tickets, "gm_ticket");
        AssertUnique(petitions, "petition");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NewScopes_SeedAboveStoredRows_AndRestartsNeverReuseIds(DatabaseProvider provider)
    {
        DatabaseConnectionOptions database = await CreateDatabaseAsync(provider);
        await using (CharacterDbContext db = NewContext(database))
        {
            db.Set<PersistentPetRow>().Add(new PersistentPetRow { CharacterId = 1, PetNumber = 40, Entry = 1 });
            db.Set<PetStableRow>().Add(new PetStableRow { CharacterId = 2, Slot = 0, PetNumber = 70, Entry = 1 });
            db.Set<GmTicketRow>().Add(new GmTicketRow { Id = 25, CharacterId = 1 });
            db.Set<PetitionRow>().Add(new PetitionRow { Id = 9, OwnerId = 1, CharterItemId = 1, Name = "Charter" });
            await db.SaveChangesAsync();
        }

        await using (CharacterDbContext db = NewContext(database))
        {
            var store = new EfCharacterIdRangeStore(db);
            IdRangeBlock pet = await store.ReserveBlockAsync(IdRangeScopes.Pet, 8);
            IdRangeBlock ticket = await store.ReserveBlockAsync(IdRangeScopes.GmTicket, 8);
            IdRangeBlock petition = await store.ReserveBlockAsync(IdRangeScopes.Petition, 8);
            Assert.Equal(71u, pet.Start);
            Assert.Equal(26u, ticket.Start);
            Assert.Equal(10u, petition.Start);

            // A second reservation (a restarted or second worker) starts after the first block, never inside it.
            Assert.Equal(pet.EndInclusive + 1u, (await store.ReserveBlockAsync(IdRangeScopes.Pet, 8)).Start);
        }
    }

    [Fact]
    public void NewScopes_AreExtendedNotReleased_AndTheSignedColumnsCapAtInt32()
    {
        Assert.DoesNotContain(IdRangeScopes.Pet, IdRangeScopes.All);
        Assert.DoesNotContain(IdRangeScopes.GmTicket, IdRangeScopes.All);
        Assert.DoesNotContain(IdRangeScopes.Petition, IdRangeScopes.All);
        Assert.Equal(new[] { "pet", "gm_ticket", "petition" }, IdRangeScopes.Extended.ToArray());
        Assert.Equal((uint)int.MaxValue, IdRangeScopes.GetMaxId(IdRangeScopes.GmTicket));
        Assert.Equal((uint)int.MaxValue, IdRangeScopes.GetMaxId(IdRangeScopes.Petition));
        Assert.Equal(uint.MaxValue, IdRangeScopes.GetMaxId(IdRangeScopes.Pet));
    }

    [Fact]
    public void UnprimedAllocators_InWorkerMode_Throw_ForItemPetTicketAndPetition()
    {
        var items = new ItemGuidAllocator { RequireDurableIds = true };
        Assert.Throws<IdRangeExhaustedException>(() => items.Next());

        var summons = new SummonService { RequireDurableIds = true };
        Assert.Throws<IdRangeExhaustedException>(() => summons.NextPetNumber());
        Assert.Throws<IdRangeExhaustedException>(() => summons.ReservePetNumberAbove(5));

        using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var social = new SocialContext(world, new EmptyLookup(), new NoopSocialPersistence(), new SocialOptions());
        social.Petitions.RequireDurableIds = true;
        Assert.Throws<IdRangeExhaustedException>(() => social.Petitions.AllocatePetitionId());

        var audit = new GmAuditFeature(NewScopes(), NullLoggerFactory.Instance) { RequireDurableIds = true };
        Assert.Throws<IdRangeExhaustedException>(() => audit.CreateTicket(1, "help", 0, 0, 0f, 0f, 0f));
        Assert.Null(audit.OpenTicketOf(1));
    }

    [Fact]
    public void RealmMode_FallbackIsUnchanged_ProcessCountersStillCount()
    {
        var items = new ItemGuidAllocator(10);
        Assert.Equal(11u, items.Next());
        Assert.Equal(12u, items.Next());

        var summons = new SummonService();
        Assert.Equal(1u, summons.NextPetNumber());
        Assert.Equal(2u, summons.NextPetNumber());
        Assert.Equal(6u, summons.ReservePetNumberAbove(5));

        using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var social = new SocialContext(world, new EmptyLookup(), new NoopSocialPersistence(), new SocialOptions());
        Assert.Equal(1, social.Petitions.AllocatePetitionId());
        Assert.Equal(2, social.Petitions.AllocatePetitionId());

        var audit = new GmAuditFeature(NewScopes(), NullLoggerFactory.Instance);
        Assert.Equal(1, audit.CreateTicket(1, "a", 0, 0, 0f, 0f, 0f)!.Id);
        Assert.Equal(2, audit.CreateTicket(2, "b", 0, 0, 0f, 0f, 0f)!.Id);
    }

    [Fact]
    public async Task PrimeDurableIdAllocatorsAsync_PrimesPetAndTicketScopes_AndCarriesTheWorkerFlag()
    {
        var store = new InMemoryStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIdRangeReservationStore>(store);
        services.AddSingleton<ItemsFeature>();
        services.AddSingleton<PetsFeature>();
        services.AddSingleton<GmAuditFeature>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();

        await WorldHost.PrimeDurableIdAllocatorsAsync(provider, scopes, blockSize: 10, lowWatermark: 2, requireDurableIds: true);

        ItemsFeature items = provider.GetRequiredService<ItemsFeature>();
        Assert.True(items.GuidAllocator.RequireDurableIds);
        Assert.Equal(1u, items.GuidAllocator.Next());

        SummonService pets = provider.GetRequiredService<PetsFeature>().Service;
        Assert.True(pets.RequireDurableIds);
        Assert.Equal(1u, pets.NextPetNumber());

        GmAuditFeature audit = provider.GetRequiredService<GmAuditFeature>();
        Assert.True(audit.RequireDurableIds);
        Assert.Equal(1, audit.CreateTicket(7, "x", 0, 0, 0f, 0f, 0f)!.Id);

        // Without the flag (Realm mode) the same scopes are still primed, but nothing is required of an unprimed allocator.
        var realmServices = new ServiceCollection();
        realmServices.AddLogging();
        realmServices.AddSingleton<IIdRangeReservationStore>(new InMemoryStore());
        realmServices.AddSingleton<PetsFeature>();
        await using ServiceProvider realm = realmServices.BuildServiceProvider();
        await WorldHost.PrimeDurableIdAllocatorsAsync(realm, realm.GetRequiredService<IServiceScopeFactory>(), blockSize: 10, lowWatermark: 2);
        Assert.False(realm.GetRequiredService<PetsFeature>().Service.RequireDurableIds);
    }

    private static async Task RunAsync(Task start, Func<Task> work)
    {
        await start.ConfigureAwait(false);
        await Task.Run(work).ConfigureAwait(false);
    }

    private static void AssertUnique(ConcurrentBag<uint> ids, string scope)
    {
        Assert.Equal(PerHostPerScope * 2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(0u, ids);
        Assert.True(ids.Count > 0, scope);
    }

    private static IServiceScopeFactory NewScopes()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private async Task<DatabaseConnectionOptions> CreateDatabaseAsync(DatabaseProvider provider)
    {
        string name = "arcane_t_" + Guid.NewGuid().ToString("N")[..16];
        string connectionString = provider switch
        {
            DatabaseProvider.Sqlite => $"Data Source={SqlitePath(name)};Pooling=False",
            DatabaseProvider.MariaDb or DatabaseProvider.MySql =>
                Environment.GetEnvironmentVariable(MariaDbVariable)!.TrimEnd(';') + $";Database={name}",
            DatabaseProvider.PostgreSql =>
                Environment.GetEnvironmentVariable(PostgresVariable)!.TrimEnd(';') + $";Database={name}",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        var options = new DatabaseConnectionOptions { Provider = provider, ConnectionString = connectionString };
        _created.Add(options);
        await using CharacterDbContext db = NewContext(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return options;
    }

    private string SqlitePath(string name)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, name + ".db");
    }

    private static CharacterDbContext NewContext(DatabaseConnectionOptions database)
    {
        var builder = new DbContextOptionsBuilder<CharacterDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, database);
        return new CharacterDbContext(builder.Options);
    }

    /// <summary>One host's allocators: its own reservation callback (a fresh context per reservation, like the world host) and its own consumers.</summary>
    private sealed class HostIds : IAsyncDisposable
    {
        private readonly int _hostIndex;
        private readonly ItemGuidAllocator _items = new() { RequireDurableIds = true };
        private readonly DurableIdBlockAllocator _itemBlocks;
        private readonly SummonService _pets = new() { RequireDurableIds = true };
        private readonly DurableIdBlockAllocator _petBlocks;
        private readonly GmAuditFeature _audit;
        private readonly DurableIdBlockAllocator _ticketBlocks;
        private readonly SocialContext _social;
        private readonly DurableIdBlockAllocator _petitionBlocks;

        private HostIds(DatabaseConnectionOptions database, WorldRuntime world, int hostIndex)
        {
            _hostIndex = hostIndex;
            Func<string, uint, CancellationToken, Task<IdRangeBlock>> reserve = async (scope, count, ct) =>
            {
                await using CharacterDbContext db = NewContext(database);
                return await new EfCharacterIdRangeStore(db).ReserveBlockAsync(scope, count, ct).ConfigureAwait(false);
            };

            _itemBlocks = NewBlocks(IdRangeScopes.Item, reserve);
            _items.UseBlockAllocator(_itemBlocks);
            _petBlocks = NewBlocks(IdRangeScopes.Pet, reserve);
            _pets.IdBlockAllocator = _petBlocks;
            _audit = new GmAuditFeature(NewScopes(), NullLoggerFactory.Instance) { RequireDurableIds = true };
            _ticketBlocks = NewBlocks(IdRangeScopes.GmTicket, reserve);
            _audit.TicketIdAllocator = _ticketBlocks;
            _social = new SocialContext(world, new EmptyLookup(), new NoopSocialPersistence(), new SocialOptions());
            _social.Petitions.RequireDurableIds = true;
            _petitionBlocks = NewBlocks(IdRangeScopes.Petition, reserve);
            _social.Petitions.IdBlockAllocator = _petitionBlocks;
        }

        public static async Task<HostIds> CreateAsync(DatabaseConnectionOptions database, WorldRuntime world, int hostIndex)
        {
            var host = new HostIds(database, world, hostIndex);
            // Prime before anything allocates (the world host does this before the features attach).
            await host._itemBlocks.EnsurePrimedAsync();
            await host._petBlocks.EnsurePrimedAsync();
            await host._ticketBlocks.EnsurePrimedAsync();
            await host._petitionBlocks.EnsurePrimedAsync();
            return host;
        }

        public Task AllocateItemsAsync(int count, ConcurrentBag<uint> into)
            => TakeAsync(_itemBlocks, count, into, () => _items.Next());

        public Task AllocatePetsAsync(int count, ConcurrentBag<uint> into)
            => TakeAsync(_petBlocks, count, into, () => _pets.NextPetNumber());

        public Task AllocateTicketsAsync(int count, ConcurrentBag<uint> into)
        {
            int next = 0;
            return TakeAsync(_ticketBlocks, count, into, () =>
            {
                // One open ticket per character: a distinct character per ticket.
                int character = (_hostIndex * 1_000_000) + 1 + next;
                GmTicketRecord ticket = _audit.CreateTicket(character, "t", 0, 0, 0f, 0f, 0f)
                    ?? throw new InvalidOperationException("ticket refused");
                next++;
                return (uint)ticket.Id;
            });
        }

        public Task AllocatePetitionsAsync(int count, ConcurrentBag<uint> into)
            => TakeAsync(_petitionBlocks, count, into, () => (uint)_social.Petitions.AllocatePetitionId());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static DurableIdBlockAllocator NewBlocks(string scope, Func<string, uint, CancellationToken, Task<IdRangeBlock>> reserve)
            => new(scope, blockSize: 256, lowWatermark: 64, reserveBlockAsync: reserve, retryBackoff: TimeSpan.Zero);

        /// <summary>Allocate <paramref name="count"/> ids; when a tight loop outruns the background prefetch, wait for it and go on (the allocator fails closed, it never invents an id).</summary>
        private static async Task TakeAsync(DurableIdBlockAllocator blocks, int count, ConcurrentBag<uint> into, Func<uint> next)
        {
            int taken = 0;
            while (taken < count)
            {
                try
                {
                    into.Add(next());
                    taken++;
                }
                catch (IdRangeExhaustedException)
                {
                    await blocks.WaitForPendingPrefetchAsync().ConfigureAwait(false);
                    await Task.Yield();
                }
            }
        }
    }

    private sealed class EmptyLookup : ICharacterLookup
    {
        public CharacterInfo? Find(uint characterId) => null;

        public CharacterInfo? FindByName(string name) => null;
    }

    private sealed class NoopSocialPersistence : ISocialPersistence
    {
        public void SetSocial(int characterId, int otherId, ArcaneCore.Kernel.Social.SocialFlags flags) { }

        public void SaveGuild(ArcaneCore.Kernel.Social.GuildData guild) { }

        public void DeleteGuild(int guildId) { }
    }

    private sealed class InMemoryStore : IIdRangeReservationStore
    {
        private readonly Dictionary<string, uint> _nextByScope = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public Task<IdRangeBlock> ReserveBlockAsync(string scope, uint count, CancellationToken ct = default)
        {
            lock (_gate)
            {
                uint start = _nextByScope.GetValueOrDefault(scope, IdRangeScopes.GetMinFirstId(scope));
                uint end = start + count - 1u;
                _nextByScope[scope] = end + 1u;
                return Task.FromResult(new IdRangeBlock(scope, start, end));
            }
        }
    }
}
