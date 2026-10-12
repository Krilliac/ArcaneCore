using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
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

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SetBasedWrites_UnderTheFence_RunVerified_WhenOwned_AndLeaveRowsUnchanged_WhenTheOwnerIsStale(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true);
        int id = await CreateCharacterAsync(connection, "SetBased");
        CharacterOwnershipToken first = await AcquireAsync(services);
        var context = services.GetRequiredService<CharacterOwnershipContext>();
        context.Current = first;

        // Startup cleanup (EfInstanceStore.LoadAsync) used to throw under the fence; the owner now runs it.
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            var instances = new EfInstanceStore(db);
            await instances.LoadAsync();
            await instances.SaveInstanceAsync(new InstanceRecord(5, 33, 1000));
            await instances.SaveBindAsync(new CharacterInstanceBindRecord(id, 5, true));
            await instances.SaveInstanceDataAsync(5, "owned");
            await new EfCharacterSpellStore(db).AddAsync(id, [133, 168]);
            await new EfCharacterSpellStore(db).RemoveAsync(id, 168);
        }

        async Task<(string? Data, int Binds, IReadOnlyList<uint> Spells)> SnapshotAsync()
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
            InstanceStoreSnapshot snapshot = await new EfInstanceStore(db).LoadAsync();
            return (snapshot.Instances.Single().Data, snapshot.Binds.Count, await new EfCharacterSpellStore(db).GetAsync(id));
        }

        var before = await SnapshotAsync();
        Assert.Equal("owned", before.Data);
        Assert.Equal(1, before.Binds);
        Assert.Equal([133u], before.Spells);

        // Another node takes over; every set-based write of this process is now refused and changes nothing.
        await TakeoverAsync(services, first);
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await AssertRejectedAsync(() => new EfInstanceStore(db).SaveInstanceDataAsync(5, "stale"));
            await AssertRejectedAsync(() => new EfInstanceStore(db).DeleteBindAsync(id, 5));
            await AssertRejectedAsync(() => new EfCharacterSpellStore(db).RemoveAsync(id, 133));
            await AssertRejectedAsync(() => new EfInstanceStore(db).LoadAsync());
        }

        var after = await SnapshotAsync();
        Assert.Equal(before.Data, after.Data);
        Assert.Equal(before.Binds, after.Binds);
        Assert.Equal(before.Spells, after.Spells);
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
                await new EfInstanceStore(db).SaveBindAsync(new CharacterInstanceBindRecord(id, 8, true));
                await new EfInstanceStore(db).SaveInstanceDataAsync(8, "data");
                await new EfInstanceStore(db).DeleteBindAsync(id, 8);
                await new EfCharacterSpellStore(db).AddAsync(id, [133]);
                await new EfCharacterSpellStore(db).RemoveAsync(id, 133);
                await new EfCharacterStore(db).CompleteAsync(Guid.NewGuid());
            }
            finally
            {
                if (scope is { } s) await s.DisposeAsync();
            }

            return [.. log.Texts];
        }
        finally
        {
            if (direct is not null) await direct.DisposeAsync();
            if (services is not null) await services.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ARefusedTransaction_IsRolledBack_SoTheSameContextAndOtherConnectionsCanWriteAfterwards(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true);
        int id = await CreateCharacterAsync(connection, "Refused");
        CharacterOwnershipToken first = await AcquireAsync(services);
        CharacterOwnershipToken second = await TakeoverAsync(services, first);
        var context = services.GetRequiredService<CharacterOwnershipContext>();
        context.Current = first; // stale

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        // An open connection stays open across the refusal (EF only closes the ones it opened), so closing the connection cannot
        // hide a transaction the fence failed to roll back.
        await db.Database.OpenConnectionAsync();
        await AssertRejectedAsync(() => new EfSocialStore(db).SetSocialAsync(id, 1, SocialFlags.Friend));
        Assert.Null(db.Database.CurrentTransaction);

        // A refused transaction that stayed open would still hold the connection's transaction (and SQLite's write lock): another
        // connection could not write, and this context could not begin the next transaction.
        await using (CharacterDbContext other = TestContexts.Create<CharacterDbContext>(connection))
            Assert.Equal(1, await other.Characters.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Level, (byte)5)));

        context.Current = second;
        await new EfSocialStore(db).SetSocialAsync(id, 2, SocialFlags.Friend);
        Assert.Contains(await ReadSocialAsync(connection, id), e => e.OtherId == 2 && e.Flags == SocialFlags.Friend);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WithTheFenceOn_TheStoresDoNotVerifyASecondTime_AndWithItOff_TheyIssueNoOwnershipStatementAtAll(DatabaseProvider provider)
    {
        var onLog = new CommandLog();
        var offLog = new CommandLog();
        int on = await OwnershipStatementsAsync(provider, fence: true, onLog);
        int off = await OwnershipStatementsAsync(provider, fence: false, offLog);

        // On: the transaction interceptor verifies with raw commands that EF's command log never sees, so the stores add none of their own.
        Assert.Equal(0, on);
        Assert.Equal(0, off); // fence off: no ownership statement (the unfenced world watches the row once per autosave interval instead)

        // Zero statements also holds if the interceptor verified nothing, so prove it did: a snapshot carrying a stale explicit token is
        // refused although the process's ambient token is valid.
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true);
        int id = await CreateCharacterAsync(connection, "Stale");
        CharacterOwnershipToken first = await AcquireAsync(services);
        CharacterOwnershipToken second = await TakeoverAsync(services, first);
        services.GetRequiredService<CharacterOwnershipContext>().Current = second;
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        uint before = await ReadMapAsync(connection, id);
        await AssertRejectedAsync(() => new EfCharacterStore(db).SaveStateAsync(State(id, 9, first)));
        Assert.Equal(before, await ReadMapAsync(connection, id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WithTheFenceOff_AnUntokenedSave_IssuesOnlyTheStatementsTrain16Did(DatabaseProvider provider)
    {
        // Train 16 (000f778c): SaveStateAsync read the rows with no transaction, then SaveChanges opened its own transaction for the writes;
        // SaveQuestsAsync and SaveTaxiMaskAsync did the same. No explicit BEGIN before the reads, no character_ownership statement.
        var log = new CommandLog();
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: false, log);
        int id = await CreateCharacterAsync(connection, "Plain");
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.False(CharacterOwnershipGuard.IsFenceActive(db));

        log.Clear();
        await new EfCharacterStore(db).SaveStateAsync(State(id, 4, null));
        IReadOnlyList<string> state = log.Texts;
        log.Clear();
        await new EfCharacterQuestStore(db).SaveQuestsAsync(id, [], ownership: null);
        IReadOnlyList<string> quests = log.Texts;
        log.Clear();
        await new EfCharacterQuestStore(db).SaveQuestsAsync(id, [Quest(id, 4001, 1)], ownership: null);
        IReadOnlyList<string> questInsert = log.Texts;
        log.Clear();
        await new EfCharacterQuestStore(db).SaveQuestsAsync(id, [Quest(id, 4001, 2)], ownership: null);
        IReadOnlyList<string> questUpdate = log.Texts;
        log.Clear();
        await new EfCharacterQuestStore(db).SaveTaxiMaskAsync(id, [1u], ownership: null);
        IReadOnlyList<string> taxi = log.Texts;

        foreach (IReadOnlyList<string> statements in new[] { state, quests, questInsert, questUpdate, taxi })
        {
            Assert.DoesNotContain(statements, t => t.Contains(CharacterOwnershipDataModule.Table, StringComparison.Ordinal));
            // The only transaction is SaveChanges' own, begun after the last read: nothing precedes the reads.
            const string Begin = "<begin transaction>";
            int begins = statements.Count(t => t == Begin);
            Assert.True(begins <= 1, string.Join("\n", statements));
            if (begins == 1)
                Assert.True(statements.Select((t, i) => (t, i)).Where(p => p.t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).All(p => p.i < statements.ToList().IndexOf(Begin)),
                    "a read ran inside the transaction (it would hold the write lock through the reads): " + string.Join("\n", statements));
        }

        // The same statement kinds, in order (the Train 16 list): SaveState reads then writes; quests with no upserts only read;
        // the taxi mask reads then writes.
        static string Kinds(IReadOnlyList<string> s) => string.Join(",", s.Select(t => t == "<begin transaction>" ? "BEGIN" : t.TrimStart().Split(' ')[0].ToUpperInvariant()));
        Assert.Equal(ExpectedStateKinds, Kinds(state));
        Assert.Equal(ExpectedQuestKinds, Kinds(quests));
        Assert.Equal(ExpectedQuestInsertKinds, Kinds(questInsert));
        Assert.Equal(ExpectedQuestUpdateKinds, Kinds(questUpdate));
        Assert.Equal(ExpectedTaxiKinds, Kinds(taxi));
    }

    private const string ExpectedStateKinds = "SELECT,UPDATE";
    private const string ExpectedQuestKinds = "SELECT,SELECT";
    private const string ExpectedQuestInsertKinds = "SELECT,SELECT,INSERT";
    private const string ExpectedQuestUpdateKinds = "SELECT,SELECT,UPDATE";

    private static CharacterQuestStatus Quest(int characterId, uint quest, byte status)
        => new(characterId, quest, status, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private const string ExpectedTaxiKinds = "SELECT,SELECT,INSERT";

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WithTheFenceOn_AnUntokenedSnapshotIsRefused_EvenUnderAValidAmbientToken(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence: true);
        int id = await CreateCharacterAsync(connection, "Untokened");
        CharacterOwnershipToken token = await AcquireAsync(services);
        services.GetRequiredService<CharacterOwnershipContext>().Current = token;
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();

        // The interceptor would pass this transaction on the ambient token; only the store's own check of the snapshot's (null) token refuses it.
        uint before = await ReadMapAsync(connection, id);
        await AssertRejectedAsync(() => new EfCharacterStore(db).SaveStateAsync(State(id, 9, null)));
        Assert.Equal(before, await ReadMapAsync(connection, id));

        // The same write with the claim it was taken under is accepted, so the refusal above is about the missing token.
        await new EfCharacterStore(db).SaveStateAsync(State(id, 9, token));
        Assert.Equal(9u, await ReadMapAsync(connection, id));
    }

    private static async Task<uint> ReadMapAsync(DatabaseConnectionOptions connection, int id)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return (uint)await db.Characters.AsNoTracking().Where(c => c.Id == id).Select(c => c.MapId).SingleAsync();
    }

    private async Task<int> OwnershipStatementsAsync(DatabaseProvider provider, bool fence, CommandLog log)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, fence, log);
        int id = await CreateCharacterAsync(connection, "Once");
        CharacterOwnershipToken? token = null;
        if (fence)
        {
            token = await AcquireAsync(services);
            services.GetRequiredService<CharacterOwnershipContext>().Current = token;
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(fence, CharacterOwnershipGuard.IsFenceActive(db));
        log.Clear();
        await new EfCharacterStore(db).SaveStateAsync(State(id, 4, token));
        await new EfCharacterQuestStore(db).SaveQuestsAsync(id, [], ownership: token);
        await new EfCharacterQuestStore(db).SaveTaxiMaskAsync(id, [1u], ownership: token);
        return log.Texts.Count(t => t.Contains(CharacterOwnershipDataModule.Table, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<SocialEntry>> ReadSocialAsync(DatabaseConnectionOptions connection, int id)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfSocialStore(db).GetSocialAsync(id);
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken? ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);
}
