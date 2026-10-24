using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The durable transfer-decision store (characters schema 62, Tier 3b groundwork): every stage of a player transfer and of a whole-World
/// handoff is committed before the call returns, so a new process (a new provider on the same database) finds it where the old one left it;
/// the handoff's epoch takeover moves the characters ownership and commits every transfer in one transaction, and fences the old world out.
/// </summary>
public sealed class TransferDecisionStoreTests : IAsyncLifetime
{
    private static readonly WorkerNodeId A = new("world-a");
    private static readonly WorkerNodeId B = new("world-b");
    private readonly TestDatabases _databases = new();
    private readonly List<ServiceProvider> _providers = [];

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
            await provider.DisposeAsync();
        await _databases.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheModule_IsSchema62_AndRegistersOneStoreForBothContracts(DatabaseProvider provider)
    {
        Assert.Equal(62, ClusterTransferDataModule.Version);
        (ServiceProvider services, _) = await OpenAsync(await _databases.CreateAsync(provider));
        EfTransferDecisionStore store = services.GetRequiredService<EfTransferDecisionStore>();
        Assert.Same(store, services.GetRequiredService<ITransferDecisionStore>());
        Assert.Same(store, services.GetRequiredService<IWorldHandoffStore>());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheDecisionTable_IndexesState_SoTheAggregateCountDoesNotScan(DatabaseProvider provider)
    {
        (ServiceProvider services, _) = await OpenAsync(await _databases.CreateAsync(provider));
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        var entity = db.Model.FindEntityType(typeof(ClusterTransferDecisionRow));
        Assert.NotNull(entity);
        Assert.Contains(entity!.GetIndexes(), i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(ClusterTransferDecisionRow.State));
        Assert.Contains(ClusterTransferStateIndexDataModule.Version, DataModules.For(DatabaseComponent.Characters).Select(m => m.SchemaVersion));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CountUnresolvedAsync_CountsPreCommitAndCommittedAcrossAllCharacters_AndNothingElse(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        (_, EfTransferDecisionStore store) = await OpenAsync(connection);
        Assert.Equal(0, await store.CountUnresolvedAsync());

        Guid preCommit = Guid.NewGuid(), committed = Guid.NewGuid(), completed = Guid.NewGuid(), aborted = Guid.NewGuid();
        var tokens = new Dictionary<Guid, (ulong Guid, CharacterOwnershipToken Token)>();
        foreach ((Guid id, ulong guid) in new[] { (preCommit, 21UL), (committed, 22UL), (completed, 23UL), (aborted, 24UL) })
        {
            var token = new CharacterOwnershipToken($"char:{guid}", 1, Guid.NewGuid());
            tokens[id] = (guid, token);
            await store.CreateReservationAsync(id, guid, 11, 5, A, B, 0, 0, 1, 0, token);
        }

        Assert.Equal(4, await store.CountUnresolvedAsync());

        await store.AbortPreCommitAsync(aborted, "test");
        Assert.Equal(3, await store.CountUnresolvedAsync());

        foreach (Guid id in new[] { committed, completed })
        {
            await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.SuspendAndCapture, Bundle(id, tokens[id].Guid, tokens[id].Token));
            await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.InstallPassive);
            await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.StageWorldPort);
            await store.CommitDecisionAsync(id, Guid.NewGuid());
        }

        Assert.Equal(3, await store.CountUnresolvedAsync()); // a committed transfer is still unresolved

        await store.MarkCompletedAsync(completed);
        Assert.Equal(2, await store.CountUnresolvedAsync());

        // The count is read from storage, so a new process agrees with the old one.
        (_, EfTransferDecisionStore restarted) = await OpenAsync(connection);
        Assert.Equal(2, await restarted.CountUnresolvedAsync());
        Assert.Single(await restarted.GetUnresolvedForCharacterAsync(21));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EveryTransferStage_SurvivesARestart_AndTheBundleRoundTrips(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        (_, EfTransferDecisionStore store) = await OpenAsync(connection);
        var source = new CharacterOwnershipToken("char:7", 3, Guid.NewGuid());
        Guid transferId = Guid.NewGuid();
        Guid target = Guid.NewGuid();
        PlayerTransferBundle bundle = Bundle(transferId, 7, source);

        await store.CreateReservationAsync(transferId, 7, 11, 5, A, B, 0, 0, 1, 0, source);
        await store.AdvancePreCommitStageAsync(transferId, PlayerTransferStage.SuspendAndCapture, bundle);

        // A new process sees exactly what was committed.
        (_, EfTransferDecisionStore restarted) = await OpenAsync(connection);
        TransferDecisionRecord afterCapture = (await restarted.GetDecisionAsync(transferId))!;
        Assert.Equal(PlayerTransferStage.SuspendAndCapture, afterCapture.CurrentStage);
        Assert.Equal(PlayerTransferDecisionState.PreCommit, afterCapture.DecisionState);
        Assert.Equal(source, afterCapture.SourceOwnershipToken);
        Assert.Equal(source, restarted.GetActiveOwnership(7));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(bundle), System.Text.Json.JsonSerializer.Serialize(afterCapture.CapturedBundle));

        await restarted.AdvancePreCommitStageAsync(transferId, PlayerTransferStage.InstallPassive);
        await restarted.AdvancePreCommitStageAsync(transferId, PlayerTransferStage.StageWorldPort);
        TransferDecisionRecord committed = await restarted.CommitDecisionAsync(transferId, target);
        Assert.Equal(new CharacterOwnershipToken("char:7", 4, target), committed.TargetOwnershipToken);
        Assert.Equal(PlayerTransferDecisionState.Committed, committed.DecisionState);
        Assert.Equal(committed.TargetOwnershipToken, (await restarted.CommitDecisionAsync(transferId, target)).TargetOwnershipToken); // idempotent

        (_, EfTransferDecisionStore again) = await OpenAsync(connection);
        Assert.Equal(committed.TargetOwnershipToken, again.GetActiveOwnership(7));
        Assert.Single(await again.GetUnresolvedForCharacterAsync(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => again.AbortPreCommitAsync(transferId, "too-late"));
        TransferDecisionRecord completed = await again.MarkCompletedAsync(transferId);
        Assert.Equal(PlayerTransferDecisionState.Completed, completed.DecisionState);
        Assert.Equal(PlayerTransferStage.ActivateAndRetire, completed.CurrentStage);
        Assert.Empty(await again.GetUnresolvedForCharacterAsync(7));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OneTransferInFlightPerCharacter_StagesInOrder_AndAStaleTokenIsRefused(DatabaseProvider provider)
    {
        (_, EfTransferDecisionStore store) = await OpenAsync(await _databases.CreateAsync(provider));
        var source = new CharacterOwnershipToken("char:9", 1, Guid.NewGuid());
        Guid first = Guid.NewGuid();
        await store.CreateReservationAsync(first, 9, 1, 1, A, B, 0, 0, 1, 0, source);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateReservationAsync(Guid.NewGuid(), 9, 1, 1, A, B, 0, 0, 1, 0, source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvancePreCommitStageAsync(first, PlayerTransferStage.InstallPassive));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitDecisionAsync(first, Guid.NewGuid()));

        await store.AbortPreCommitAsync(first, "operator");
        Assert.Equal(source, store.GetActiveOwnership(9));

        // The claim was given back: a new transfer may start, but only with the token the store holds.
        var stale = new CharacterOwnershipToken("char:9", 0, source.Incarnation);
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => store.CreateReservationAsync(Guid.NewGuid(), 9, 1, 1, A, B, 0, 0, 1, 0, stale));
        await store.CreateReservationAsync(Guid.NewGuid(), 9, 1, 1, A, B, 0, 0, 1, 0, source);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HandoffEpochTakeover_MovesTheWorldOwnership_CommitsEveryTransfer_AndFencesTheOldWorld(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(connection);
        int characterId = await CreateCharacterAsync(services, "Handoff");
        CharacterOwnershipToken worldA = await AcquireAsync(services, "node-a");
        services.GetRequiredService<CharacterOwnershipContext>().Current = worldA;

        Guid handoffId = Guid.NewGuid();
        await store.BeginAsync(handoffId, A, B, worldA);
        foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
            await store.AdvancePreCommitAsync(handoffId, stage);

        ITransferDecisionStore scoped = store.ForHandoff(handoffId);
        Guid[] transfers = [Guid.NewGuid(), Guid.NewGuid()];
        for (int i = 0; i < transfers.Length; i++)
        {
            ulong guid = (ulong)(100 + i);
            await scoped.CreateReservationAsync(transfers[i], guid, 1, i + 1, A, B, 0, 0, 0, 0, worldA);
            await scoped.AdvancePreCommitStageAsync(transfers[i], PlayerTransferStage.SuspendAndCapture, Bundle(transfers[i], guid, worldA));
            await scoped.AdvancePreCommitStageAsync(transfers[i], PlayerTransferStage.InstallPassive);
            await scoped.AdvancePreCommitStageAsync(transfers[i], PlayerTransferStage.StageWorldPort);
        }

        // A handoff transfer never commits on its own.
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.CommitDecisionAsync(transfers[0], Guid.NewGuid()));
        Assert.Equal(transfers.Order(), await store.GetTransferIdsAsync(handoffId));

        Guid incarnationB = Guid.NewGuid();
        WorldHandoffRecord committed = (await store.CommitEpochTakeoverAsync(handoffId, "node-b", incarnationB))!;
        var worldB = new CharacterOwnershipToken("world", worldA.Epoch + 1, incarnationB);
        Assert.Equal(WorldHandoffStage.EpochTakeover, committed.Stage);
        Assert.Equal(PlayerTransferDecisionState.Committed, committed.DecisionState);
        Assert.Equal(worldB, committed.TargetOwnershipToken);
        Assert.Equal("node-b", committed.TargetOwnerNode);
        Assert.Equal(committed, await store.CommitEpochTakeoverAsync(handoffId, "node-b", incarnationB)); // idempotent

        // The ownership row now names B at epoch N + 1, and every transfer committed to B's token.
        CharacterOwnershipRow row = await ReadOwnershipAsync(services);
        Assert.True(row.Active);
        Assert.Equal(worldB.Epoch, row.Epoch);
        Assert.Equal(incarnationB.ToString("D"), row.OwnerIncarnation);
        Assert.Equal("node-b", row.OwnerNode);
        foreach (Guid transfer in transfers)
        {
            TransferDecisionRecord record = (await store.GetDecisionAsync(transfer))!;
            Assert.Equal(PlayerTransferDecisionState.Committed, record.DecisionState);
            Assert.Equal(worldB, record.TargetOwnershipToken);
        }

        // A, still holding epoch N, is fenced out of every characters write; B's token writes.
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            Exception? refused = await Record.ExceptionAsync(() => new EfSocialStore(db).SetSocialAsync(characterId, 900, SocialFlags.Friend));
            Assert.True(Contains<CharacterOwnershipRejectedException>(refused), "the old world's write was not fenced: " + refused);
        }

        services.GetRequiredService<CharacterOwnershipContext>().Current = worldB;
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await new EfSocialStore(db).SetSocialAsync(characterId, 901, SocialFlags.Friend);
        }

        // After the point of no return: no abort, even from a new process; roll forward only.
        (_, EfTransferDecisionStore restarted) = await OpenAsync(connection);
        WorldHandoffRecord unresolved = Assert.Single(await restarted.GetUnresolvedAsync());
        Assert.Equal(WorldHandoffStage.EpochTakeover, unresolved.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AbortAsync(handoffId, "too-late"));
        await restarted.MarkReroutedAsync(handoffId);
        foreach (Guid transfer in transfers)
            await restarted.MarkCompletedAsync(transfer);
        WorldHandoffRecord retired = await restarted.MarkRetiredAsync(handoffId);
        Assert.Equal(WorldHandoffStage.Retired, retired.Stage);
        Assert.Equal(PlayerTransferDecisionState.Completed, retired.DecisionState);
        Assert.Empty(await restarted.GetUnresolvedAsync());
        await restarted.BeginAsync(Guid.NewGuid(), B, A, worldB); // the next handoff may begin
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HandoffTakeover_ChangesNothing_WhenATransferIsNotReady_OrTheSourceLostTheOwnership(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(connection);
        CharacterOwnershipToken worldA = await AcquireAsync(services, "node-a");
        Guid handoffId = Guid.NewGuid();
        await store.BeginAsync(handoffId, A, B, worldA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitEpochTakeoverAsync(handoffId, "node-b", Guid.NewGuid())); // not flushed
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvancePreCommitAsync(handoffId, WorldHandoffStage.Frozen)); // skips a stage
        foreach (WorldHandoffStage stage in new[] { WorldHandoffStage.Warm, WorldHandoffStage.AdmissionsStopped, WorldHandoffStage.Frozen, WorldHandoffStage.Flushed })
            await store.AdvancePreCommitAsync(handoffId, stage);

        Guid stuck = Guid.NewGuid();
        await store.ForHandoff(handoffId).CreateReservationAsync(stuck, 300, 1, 1, A, B, 0, 0, 0, 0, worldA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitEpochTakeoverAsync(handoffId, "node-b", Guid.NewGuid()));
        Assert.Equal(worldA.Epoch, (await ReadOwnershipAsync(services)).Epoch);
        Assert.Equal(PlayerTransferDecisionState.PreCommit, (await store.GetDecisionAsync(stuck))!.DecisionState);

        // Someone else takes the ownership from A: the takeover finds no source token and changes nothing.
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryTakeoverAsync(worldA, "intruder", Guid.NewGuid());
        await store.AdvancePreCommitStageAsync(stuck, PlayerTransferStage.SuspendAndCapture, Bundle(stuck, 300, worldA));
        await store.AdvancePreCommitStageAsync(stuck, PlayerTransferStage.InstallPassive);
        await store.AdvancePreCommitStageAsync(stuck, PlayerTransferStage.StageWorldPort);
        Assert.Null(await store.CommitEpochTakeoverAsync(handoffId, "node-b", Guid.NewGuid()));
        Assert.Equal(WorldHandoffStage.Flushed, (await store.GetAsync(handoffId))!.Stage);
        Assert.Equal("intruder", (await ReadOwnershipAsync(services)).OwnerNode);

        // Abort before the point of no return: the handoff and its transfers are aborted and the character's claim is given back.
        WorldHandoffRecord aborted = await store.AbortAsync(handoffId, "source-lost-ownership");
        Assert.Equal(WorldHandoffStage.Aborted, aborted.Stage);
        Assert.Equal("source-lost-ownership", aborted.AbortReason);
        Assert.Equal(PlayerTransferDecisionState.Aborted, (await store.GetDecisionAsync(stuck))!.DecisionState);
        Assert.Empty(await store.GetUnresolvedAsync());
        await store.CreateReservationAsync(Guid.NewGuid(), 300, 1, 1, A, B, 0, 0, 0, 0, worldA);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OnlyOneHandoffIsOpen_AndAClosedHandoffTakesNoTransfers(DatabaseProvider provider)
    {
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(await _databases.CreateAsync(provider));
        CharacterOwnershipToken worldA = await AcquireAsync(services, "node-a");
        Guid first = Guid.NewGuid();
        await store.BeginAsync(first, A, B, worldA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginAsync(Guid.NewGuid(), A, B, worldA));
        await Assert.ThrowsAsync<ArgumentException>(() => store.BeginAsync(Guid.NewGuid(), A, A, worldA));
        await Assert.ThrowsAsync<ArgumentException>(() => store.BeginAsync(Guid.NewGuid(), A, B, new CharacterOwnershipToken("char:1", 1, Guid.NewGuid())));

        await store.AbortAsync(first, "operator");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ForHandoff(first).CreateReservationAsync(Guid.NewGuid(), 5, 1, 1, A, B, 0, 0, 0, 0, worldA));
        await store.BeginAsync(Guid.NewGuid(), A, B, worldA);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletingACharacter_IsRefusedWhileItsTransferIsUnresolved_AndOtherwiseRemovesItsTransferRows(DatabaseProvider provider)
    {
        (ServiceProvider services, EfTransferDecisionStore store) = await OpenAsync(await _databases.CreateAsync(provider));
        Assert.Contains(CharacterDataCleanups.All, c => c is ClusterTransferDataModule);
        services.GetRequiredService<CharacterOwnershipContext>().Current = await AcquireAsync(services, "node-a");
        int characterId = await CreateCharacterAsync(services, "Mover");
        int otherId = await CreateCharacterAsync(services, "Stayer");
        ulong guid = (ulong)characterId;
        var source = new CharacterOwnershipToken($"char:{characterId}", 1, Guid.NewGuid());
        var otherSource = new CharacterOwnershipToken($"char:{otherId}", 1, Guid.NewGuid());
        Guid transfer = Guid.NewGuid();
        await store.CreateReservationAsync(transfer, guid, 1, 1, A, B, 0, 0, 1, 0, source);
        await store.CreateReservationAsync(Guid.NewGuid(), (ulong)otherId, 1, 1, A, B, 0, 0, 1, 0, otherSource);

        // In flight: the deletion is refused and rolls back; the character and its transfer rows stay.
        Assert.False(await DeleteCharacterAsync(services, characterId));
        Assert.Equal((1, 1, true), await CountTransferRowsAsync(services, characterId));
        Assert.Single(await store.GetUnresolvedForCharacterAsync(guid));

        // Resolved: the deletion removes the character's ownership token and decisions, and only its own.
        await store.AbortPreCommitAsync(transfer, "operator");
        Assert.True(await DeleteCharacterAsync(services, characterId));
        Assert.Equal((0, 0, false), await CountTransferRowsAsync(services, characterId));
        Assert.Null(await store.GetDecisionAsync(transfer));
        Assert.Equal((1, 1, true), await CountTransferRowsAsync(services, otherId));
    }

    private static async Task<bool> DeleteCharacterAsync(ServiceProvider services, int characterId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await new EfCharacterStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).DeleteAsync(characterId, accountId: 1);
    }

    private static async Task<(int Owners, int Decisions, bool Character)> CountTransferRowsAsync(ServiceProvider services, int characterId)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        long guid = characterId;
        return (await db.Set<ClusterTransferOwnerRow>().CountAsync(r => r.CharacterGuid == guid),
            await db.Set<ClusterTransferDecisionRow>().CountAsync(r => r.CharacterGuid == guid),
            await db.Characters.AnyAsync(c => c.Id == characterId));
    }

    private async Task<(ServiceProvider Services, EfTransferDecisionStore Store)> OpenAsync(DatabaseConnectionOptions connection)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
            ["Cluster:Fencing:Enabled"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return (provider, provider.GetRequiredService<EfTransferDecisionStore>());
    }

    private static async Task<CharacterOwnershipToken> AcquireAsync(ServiceProvider services, string node)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryAcquireAsync("world", node, Guid.NewGuid()))!;
    }

    private static async Task<CharacterOwnershipRow> ReadOwnershipAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CharacterDbContext>().Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == "world");
    }

    private static async Task<int> CreateCharacterAsync(ServiceProvider services, string name)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
    }

    private static bool Contains<T>(Exception? error) where T : Exception
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is T)
                return true;
        }

        return false;
    }

    private static PlayerTransferBundle Bundle(Guid transferId, ulong guid, CharacterOwnershipToken source) => new()
    {
        TransferId = transferId,
        CharacterGuid = guid,
        CharacterId = (int)guid,
        AccountId = 1,
        SessionGeneration = 1,
        SourceWorkerId = A,
        TargetWorkerId = B,
        SourceOwnershipToken = source,
        CapturedAtUtcMs = 1_700_000_000_000,
        SourceMapId = 0,
        SourcePosition = new TransferPosition(-8949.95f, -132.49f, 83.53f, 0.5f),
        TargetMapId = 0,
        TargetPosition = new TransferPosition(-8949.95f, -132.49f, 83.53f, 0.5f),
        Health = 55,
        MaxHealth = 60,
        Powers = [10, 0, 100, 0, 0],
        Level = 3,
        Money = 1234,
        Items = [new TransferItemSnapshot(500, 25, 0, 15, 1, 20, 20, 0, 0, 0, 0, [0, 0, 0, 0, 0], [new TransferItemEnchantment(0, 37, 0, 0)])],
        Quests = [new TransferQuestSlotSnapshot(0, 783, 1, false, false, 0, [1, 0, 0, 0])],
        KnownSpells = [78, 81, 2457],
        HomeBind = new HomeBind(0, 12, -8949.95f, -132.49f, 83.53f),
        Corpse = new CorpseSnapshot(0, 1f, 2f, 3f, 0f, 1_700_000_000, 1),
        TaxiNodesMask = [2u],
    };
}
