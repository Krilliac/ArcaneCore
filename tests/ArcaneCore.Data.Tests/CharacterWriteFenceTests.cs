using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The one guard for every characters-database write: with <c>Cluster:Fencing:Enabled</c> the characters context verifies the process's
/// ownership token when a write transaction starts and refuses a write that has no transaction, so a store that was never taught about
/// ownership is still fenced. Each test builds the context the way the hosts do (through <c>AddCharacterDatabase</c>).
/// </summary>
public sealed class CharacterWriteFenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>Records every command's SQL so two configurations can be compared statement for statement.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Texts { get { lock (_gate) return [.. _texts]; } }
        public void Clear() { lock (_gate) _texts.Clear(); }
        private void Record(DbCommand command) { lock (_gate) _texts.Add(command.CommandText); }
        public void Mark(string text) { lock (_gate) _texts.Add(text); }

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

    /// <summary>Adds a marker to the command log for every transaction EF begins, so transaction counts are compared as well.</summary>
    private sealed class TransactionLog(CommandLog log) : DbTransactionInterceptor
    {
        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        { log.Mark("<begin transaction>"); return result; }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        { log.Mark("<begin transaction>"); return new(result); }
    }

    private static async Task<ServiceProvider> BuildAsync(DatabaseConnectionOptions connection, bool fence, CommandLog? log = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        };
        if (fence)
            settings["Cluster:Fencing:Enabled"] = "true";
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(configuration);
        if (log is not null)
            services.ConfigureDbContext<CharacterDbContext>(builder => builder.AddInterceptors(log, new TransactionLog(log)));
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return provider;
    }

    private static async Task<CharacterOwnershipToken> AcquireAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
            .TryAcquireAsync("world", "first", Guid.NewGuid()))!;
    }

    private static async Task<CharacterOwnershipToken> TakeoverAsync(ServiceProvider services, CharacterOwnershipToken observed)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
            .TryTakeoverAsync(observed, "second", Guid.NewGuid()))!;
    }

    private static async Task<int> CreateCharacterAsync(DatabaseConnectionOptions connection, string name)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
    }

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

    private static ItemInstanceData Item(uint guid, uint entry) => new() { Guid = guid, Entry = entry, Count = 1 };

    private sealed record Rows(
        IReadOnlyList<SocialEntry> Social, InstanceStoreSnapshot Instances, CharacterReputationData Reputation, IReadOnlyList<InventoryItemData> Inventory);

    private static async Task<Rows> ReadAsync(DatabaseConnectionOptions connection, int characterId)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return new Rows(
            await new EfSocialStore(db).GetSocialAsync(characterId),
            await new EfInstanceStore(db).LoadAsync(),
            await new EfCharacterReputationStore(db).LoadAsync(characterId),
            await new EfItemStore(db).GetInventoryAsync(characterId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OldOwnerWrites_ThroughStoresThatKnowNothingOfOwnership_LeaveRowsUnchanged(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true);
        int id = await CreateCharacterAsync(connection, "OldOwner");
        CharacterOwnershipToken first = await AcquireAsync(services);
        var context = services.GetRequiredService<CharacterOwnershipContext>();
        context.Current = first;

        // The current owner writes through every one of the stores.
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await new EfSocialStore(db).SetSocialAsync(id, 900, SocialFlags.Friend);
            await new EfInstanceStore(db).SaveInstanceAsync(new InstanceRecord(5, 33, 1000));
            await new EfCharacterReputationStore(db).SaveFactionsAsync(id, [new CharacterReputationRow(id, 72, 500, 1)]);
            await new EfItemStore(db).SaveInventoryAsync(id, new InventorySnapshot([new(0, 19, Item(100, 4496))]));
        }

        Rows before = await ReadAsync(connection, id);
        Assert.Single(before.Social);
        Assert.Single(before.Instances.Instances);
        Assert.Single(before.Reputation.Factions);
        Assert.Single(before.Inventory);

        // Another node takes over; this process still holds the old token and must write nothing.
        await TakeoverAsync(services, first);
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await AssertRejectedAsync(() => new EfSocialStore(db).SetSocialAsync(id, 901, SocialFlags.Ignored));
        }

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await AssertRejectedAsync(() => new EfInstanceStore(db).SaveInstanceAsync(new InstanceRecord(6, 34, 2000)));
        }

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await AssertRejectedAsync(() => new EfCharacterReputationStore(db).SaveFactionsAsync(id, [new CharacterReputationRow(id, 72, 9000, 2)]));
        }

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await AssertRejectedAsync(() => new EfItemStore(db).SaveInventoryAsync(id, new InventorySnapshot([new(0, 19, Item(101, 25))])));
        }

        Rows after = await ReadAsync(connection, id);
        Assert.Equivalent(before.Social, after.Social);
        Assert.Equivalent(before.Instances.Instances, after.Instances.Instances);
        Assert.Equivalent(before.Reputation.Factions, after.Reputation.Factions);
        Assert.Equal(before.Inventory.Select(i => (i.ContainerGuid, i.Slot, i.Item.Guid, i.Item.Entry)),
            after.Inventory.Select(i => (i.ContainerGuid, i.Slot, i.Item.Guid, i.Item.Entry)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CurrentOwnerWrites_Succeed_AndAnExplicitTokenOutranksTheAmbientOne(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true);
        int id = await CreateCharacterAsync(connection, "Explicit");
        CharacterOwnershipToken first = await AcquireAsync(services);
        CharacterOwnershipToken second = await TakeoverAsync(services, first);
        var context = services.GetRequiredService<CharacterOwnershipContext>();

        // The ambient token is stale, the snapshot's own is current: the snapshot's wins and the save lands.
        context.Current = first;
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await new EfCharacterStore(db).SaveStateAsync(State(id, 11, second));
        }

        await using (CharacterDbContext plain = TestContexts.Create<CharacterDbContext>(connection))
            Assert.Equal(11u, (await new EfCharacterStore(plain).GetByIdAsync(id))!.MapId);

        // The other way round: a current ambient token does not rescue a snapshot whose own token is stale.
        context.Current = second;
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await AssertRejectedAsync(() => new EfCharacterStore(db).SaveStateAsync(State(id, 22, first)));
        }

        await using (CharacterDbContext plain = TestContexts.Create<CharacterDbContext>(connection))
            Assert.Equal(11u, (await new EfCharacterStore(plain).GetByIdAsync(id))!.MapId);

        // And with the current ambient token an unaware store writes.
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await new EfSocialStore(db).SetSocialAsync(id, 7, SocialFlags.Friend);
        }

        Assert.Single(await ReadSocialAsync(connection, id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AnUntransactedExecuteUpdate_UnderTheFence_Throws_ButTheExemptStoresStillWork(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true); // the schema bootstrap ran fenced and succeeded
        int id = await CreateCharacterAsync(connection, "Untransacted");

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await Assert.ThrowsAsync<CharacterWriteFenceException>(() => db.Characters.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Level, (byte)9)));
            await Assert.ThrowsAsync<CharacterWriteFenceException>(() => db.Characters.Where(c => c.Id == id).ExecuteDeleteAsync());

            // Inside a transaction the same statement is allowed (the transaction was verified when it began).
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Equal(1, await db.Characters.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Level, (byte)9)));
            await transaction.CommitAsync();
        }

        // The ownership store (claim, takeover, release) and the lifetime fence's conditional delete are exempt.
        CharacterOwnershipToken first = await AcquireAsync(services);
        CharacterOwnershipToken second = await TakeoverAsync(services, first);
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().ReleaseAsync(second);
            Assert.False(await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().IsOwnedAsync("world"));
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await new EfCharacterReputationStore(db).DeleteDeletedCharacterAsync(id + 1000);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FenceOff_RunsTheSameSqlAsAContextWithoutAnyFence(DatabaseProvider provider)
    {
        var offLog = new CommandLog();
        var plainLog = new CommandLog();
        var onLog = new CommandLog();
        List<string> off = await WorkloadAsync(provider, fence: false, offLog);
        List<string> plain = await WorkloadAsync(provider, fence: null, plainLog);
        List<string> on = await WorkloadAsync(provider, fence: true, onLog);

        Assert.NotEmpty(off);
        Assert.Equal(plain, off);
        // One-row saves autocommit with the fence off; with it on every save is a verified transaction.
        Assert.True(on.Count(t => t == "<begin transaction>") > off.Count(t => t == "<begin transaction>"),
            "the fence wraps single-statement saves in a verified transaction; it is not free when on");
    }

    /// <summary>The same writes in every configuration; fence null builds the context with no registration at all.</summary>
    private async Task<List<string>> WorkloadAsync(DatabaseProvider provider, bool? fence, CommandLog log)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        CharacterOwnershipToken? token = null;
        ServiceProvider? services = null;
        CharacterDbContext? direct = null;
        try
        {
            if (fence is bool enabled)
            {
                services = await BuildAsync(connection, enabled, log);
                if (enabled)
                {
                    token = await AcquireAsync(services);
                    services.GetRequiredService<CharacterOwnershipContext>().Current = token;
                }
            }
            else
            {
                await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
                    await ArcaneCore.Data.Schema.SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
                var builder = new DbContextOptionsBuilder<CharacterDbContext>();
                DataServiceCollectionExtensions.ConfigureProvider(builder, connection);
                builder.AddInterceptors(log, new TransactionLog(log));
                direct = new CharacterDbContext(builder.Options);
            }

            int id = await CreateCharacterAsync(connection, "Counted");
            log.Clear();
            AsyncServiceScope? scope = services?.CreateAsyncScope();
            try
            {
                CharacterDbContext db = direct ?? scope!.Value.ServiceProvider.GetRequiredService<CharacterDbContext>();
                await new EfSocialStore(db).SetSocialAsync(id, 5, SocialFlags.Friend);
                await new EfInstanceStore(db).SaveInstanceAsync(new InstanceRecord(8, 33, 10));
                await new EfCharacterReputationStore(db).SaveFactionsAsync(id, [new CharacterReputationRow(id, 72, 500, 1)]);
                await new EfItemStore(db).SaveInventoryAsync(id, new InventorySnapshot([new(0, 19, Item(300, 25))]));
                await new EfCharacterStore(db).SaveStateAsync(State(id, 3, token));
            }
            finally
            {
                if (scope is { } s) await s.DisposeAsync();
            }

            return [.. log.Texts.Select(NormalizeSql)];
        }
        finally
        {
            if (direct is not null) await direct.DisposeAsync();
            if (services is not null) await services.DisposeAsync();
        }
    }

    // Parameter names and values do not appear in the text, but generated ids differ between runs only in parameters; the text is stable.
    private static string NormalizeSql(string sql) => sql;

    private static async Task<IReadOnlyList<SocialEntry>> ReadSocialAsync(DatabaseConnectionOptions connection, int id)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfSocialStore(db).GetSocialAsync(id);
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken? ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);
}
