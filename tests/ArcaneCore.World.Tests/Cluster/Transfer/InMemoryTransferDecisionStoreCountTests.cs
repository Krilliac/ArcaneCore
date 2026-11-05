using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary><see cref="ITransferDecisionStore.CountUnresolvedAsync"/>: the aggregate behind the <c>.cluster diag</c> pending-transfer figure.</summary>
public sealed class InMemoryTransferDecisionStoreCountTests
{
    private static readonly WorkerNodeId A = new("world-a");
    private static readonly WorkerNodeId B = new("world-b");

    private static Task<TransferDecisionRecord> Reserve(InMemoryTransferDecisionStore store, Guid id, ulong guid)
        => store.CreateReservationAsync(id, guid, 11, 5, A, B, 0, 0, 1, 0, new CharacterOwnershipToken($"char:{guid}", 1, Guid.NewGuid()));

    [Fact]
    public async Task CountUnresolvedAsync_IsZeroForAnEmptyStore()
        => Assert.Equal(0, await new InMemoryTransferDecisionStore().CountUnresolvedAsync());

    [Fact]
    public async Task CountUnresolvedAsync_CountsPreCommitAndCommittedAcrossCharacters_AndDropsAbortedAndCompleted()
    {
        var store = new InMemoryTransferDecisionStore();
        Guid preCommit = Guid.NewGuid(), committed = Guid.NewGuid(), completed = Guid.NewGuid(), aborted = Guid.NewGuid();
        await Reserve(store, preCommit, 21);
        await Reserve(store, committed, 22);
        await Reserve(store, completed, 23);
        await Reserve(store, aborted, 24);
        Assert.Equal(4, await store.CountUnresolvedAsync());

        await store.AbortPreCommitAsync(aborted, "test");
        Assert.Equal(3, await store.CountUnresolvedAsync());

        foreach (Guid id in new[] { committed, completed })
        {
            await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.SuspendAndCapture, PlayerTransferValidatorTests.CreateSampleValidBundle(transferId: id));
            await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.InstallPassive);
            await store.AdvancePreCommitStageAsync(id, PlayerTransferStage.StageWorldPort);
            await store.CommitDecisionAsync(id, Guid.NewGuid());
        }

        Assert.Equal(3, await store.CountUnresolvedAsync()); // committed is still unresolved until completed

        await store.MarkCompletedAsync(completed);
        Assert.Equal(2, await store.CountUnresolvedAsync());
    }

    [Fact]
    public async Task CountUnresolvedAsync_HonoursACancelledToken()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InMemoryTransferDecisionStore().CountUnresolvedAsync(cts.Token));
    }

    [Fact]
    public async Task ARefusedCall_ComesBackAsAFaultedOrCancelledTask_NeverASynchronousThrow()
    {
        var store = new InMemoryTransferDecisionStore();

        Task<TransferDecisionRecord> bad = Reserve(store, Guid.NewGuid(), 0); // returns, does not throw
        Assert.True(bad.IsFaulted);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bad);

        Task<TransferDecisionRecord> missing = store.MarkCompletedAsync(Guid.NewGuid());
        Assert.True(missing.IsFaulted);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => missing);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Task seed = store.SeedCharacterOwnershipAsync(31, new CharacterOwnershipToken("char:31", 1, Guid.NewGuid()), cancelled.Token);
        Assert.True(seed.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seed);
        Assert.Null(await store.GetActiveOwnershipAsync(31)); // nothing was seeded
    }
}
