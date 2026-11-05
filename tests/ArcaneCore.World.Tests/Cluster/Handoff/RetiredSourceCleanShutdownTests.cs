using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Social;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Handoff;

/// <summary>
/// A retired handoff source drops every pending write (the save queue, and every keyed write queue through <see cref="CharacterRetirementRegistry"/>) and stops cleanly. Its flush writes what it can of offline
/// characters too, through the real barrier, and aborts the handoff when it cannot. Between the flush and the retirement the source is sealed: a write the fence refuses there is dropped, not terminal.
/// The same holds for writes of no one character (instance rows, guilds, purges), which no character barrier reaches. A flush that outlives its freeze seals nothing.
/// </summary>
public sealed class RetiredSourceCleanShutdownTests
{
    /// <summary>The writes outside the per-character protections: an instance row and a guild, keyed by no character, and a purge, keyed by one but not listed among the retained characters.</summary>
    public enum SharedWrite
    {
        InstanceRow,
        Guild,
        CharacterPurge,
    }

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: money);

    [Fact]
    public async Task RetiredCharacters_AreDropped_AndTheQueueStopsWithoutError_WhileAnOwnedCharacterIsStillWritten()
    {
        var store = new FencedStore(moved: 1);
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var logger = new CaptureLogger();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;

        // Both characters have a snapshot waiting before the queue runs.
        queue.Enqueue(State(1, 11));
        queue.Enqueue(State(2, 22));

        int dropped = queue.RetireCharacters([1]);
        Assert.Equal(1, dropped);

        // A later snapshot of the retired character is skipped without any error.
        queue.Enqueue(State(1, 33));

        queue.Start();
        await queue.StopAsync(); // must not throw: no write of the retired character is attempted, so the fence never refuses

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.DoesNotContain(store.Attempts, s => s.Id == 1);
        CharacterState kept = Assert.Single(store.Saved);
        Assert.Equal(2, kept.Id);
        Assert.Equal(22u, kept.Money);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("another world", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ASaveInFlightWhenTheCharacterIsRetired_IsDroppedNotTerminal_WhenTheFenceRefusesIt()
    {
        var store = new FencedStore(moved: 1);
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var logger = new CaptureLogger();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;
        store.OnSave = state => queue.RetireCharacters([state.Id]); // the handoff retires the character while its write is inside the store call

        queue.Enqueue(State(1, 11));
        queue.Start();
        await queue.StopAsync(); // must not throw: the refusal of a retired character is expected

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.Contains(store.Attempts, s => s.Id == 1);
        Assert.Empty(queue.RetainedCharacterIds());
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    /// <summary>An offline character whose last save is retained (the database was locked) is not frozen by the handoff, yet its retry would be refused by the fence once another world owns the shard: the retirement drops it, so a clean stop follows.</summary>
    [Fact]
    public async Task ARetainedSnapshotOfANonFrozenCharacter_IsDroppedByTheSourceRetirement_AndTheQueueStopsCleanly()
    {
        var store = new FencedStore(moved: 5) { BusyOnce = 5 }; // the first write of 5 finds the database locked (retained); every later write is refused by the fence
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var logger = new CaptureLogger();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger) { BusyBackoff = [TimeSpan.FromHours(1)] }; // no retry of its own before the retirement
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;
        using WorldRuntime world = new(new WorldRuntimeOptions(), NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance);
        using var source = new LocalWorldHandoffSource(new WorkerNodeId("w1"), world, queue, new WorkerAdmission(), new WorldWriterGuard());

        queue.Enqueue(State(5, 55));
        queue.Start();
        await WorldTestHost.WaitForAsync(() => queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained");
        Assert.Equal(new[] { 5 }, queue.RetainedCharacterIds()); // not frozen: the source froze nobody

        await source.RetireAsync(CancellationToken.None);
        Assert.Empty(queue.RetainedCharacterIds());

        await queue.StopAsync(); // must not throw: the retained snapshot was dropped, so no refused retry reaches the fence

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.Single(store.Attempts); // only the first, busy attempt
        Assert.Empty(store.Saved);
    }

    /// <summary>A settlement failure quarantines a character without clearing its retained snapshot; retiring it afterwards must still drop that snapshot, or the stop reports it as undrained.</summary>
    [Fact]
    public async Task ARetainedSnapshotOfAnAlreadyQuarantinedCharacter_IsDroppedByTheRetirement_AndTheQueueStopsCleanly()
    {
        var store = new FencedStore(moved: 5) { BusyOnce = 5 };
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger()) { BusyBackoff = [TimeSpan.FromHours(1)] }; // no retry of its own before the retirement

        queue.Enqueue(State(5, 55));
        queue.Start();
        await WorldTestHost.WaitForAsync(() => queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained");

        queue.QuarantineCharacter(5);
        Assert.Equal(new[] { 5 }, queue.RetainedCharacterIds()); // quarantine keeps the retained snapshot
        Assert.Equal(1, queue.RetireCharacters([5]));
        Assert.Empty(queue.RetainedCharacterIds());

        await queue.StopAsync(); // must not throw: nothing retained is left to report as undrained

        Assert.Null(queue.TerminalFailure);
        Assert.Empty(store.Saved);
    }

    /// <summary>The write is in flight when the handoff retires the character and fails as a locked database: the failure path re-retains it, and it must not survive to the stop.</summary>
    [Fact]
    public async Task ABusyFailureOfAWriteInFlightAtRetirement_DoesNotLeaveARetainedSnapshot_AndTheQueueStopsCleanly()
    {
        var store = new FencedStore(moved: 5) { BusyOnce = 5 };
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger());
        store.OnSave = state => queue.RetireCharacters([state.Id]); // retired while inside the store call, which then reports the database locked

        queue.Enqueue(State(5, 55));
        queue.Start();
        await WorldTestHost.WaitForAsync(() => !store.Attempts.IsEmpty, "the write of character 5 to be attempted");

        await queue.StopAsync(); // must not throw: the busy failure's retained snapshot of a retired character is discarded

        Assert.Null(queue.TerminalFailure);
        Assert.Empty(queue.RetainedCharacterIds());
        Assert.Empty(store.Saved);
    }

    /// <summary>
    /// The real window. The fence moves inside the commit, as the takeover does, and the commit does not return until a retained retry has been refused there: the retirement has not run and
    /// nobody named the character, so only the flush's seal keeps the queue from going terminal. Without it the refusal stops the world.
    /// </summary>
    [Fact]
    public async Task ARetainedRetry_RefusedBetweenTheCommitAndTheRetirement_IsDropped_AndTheQueueStopsCleanly()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.WhileCommitted = () => rig.WaitForRefusalsAsync(1);
        bool retiredBeforeTheStep = true;

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.Queue.Enqueue(State(5, 55)); // a late write: the flush is over, so it is not flushed
                await WorldTestHost.WaitForAsync(() => rig.Queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained after the flush");
            }
            else if (stage == WorldHandoffStage.EpochTakeover)
            {
                retiredBeforeTheStep = rig.Queue.IsQuarantined(5);
            }
        }

        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.False(retiredBeforeTheStep); // the refusal really came before the retirement step
        Assert.Equal(1, rig.Refusals);
        await rig.AssertCleanStopAsync();
    }

    /// <summary>
    /// The window that lasts: the coordinator dies right after the commit, so the step does not run until a recovery. A retained retry and a brand-new write of a character nobody ever named are
    /// both refused in the gap; the recovery then finishes the handoff.
    /// </summary>
    [Fact]
    public async Task WritesRefusedWhileACommittedHandoffWaitsForItsRecovery_AreDropped_AndTheRecoveryFinishesCleanly()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.WhileCommitted = () => rig.WaitForRefusalsAsync(1);
        Guid id = Guid.NewGuid();

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.Queue.Enqueue(State(5, 55));
                await WorldTestHost.WaitForAsync(() => rig.Queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained after the flush");
            }
            else if (stage == WorldHandoffStage.EpochTakeover)
            {
                rig.Queue.Enqueue(State(6, 66));
                await rig.WaitForRefusalsAsync(2);
                throw new InvalidOperationException("the coordinator died after the commit");
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(id, AfterStage));
        Assert.False(rig.Queue.IsQuarantined(6)); // the retirement step never ran
        Assert.Equal(2, rig.Refusals);

        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RecoverAsync(id);

        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        await rig.AssertCleanStopAsync();
    }

    /// <summary>
    /// A keyed queue in the same window: a late write is retained, and the sweep the 30 s timer would post reaches the moved fence before the retirement. Dropped and counted; the queue stops
    /// cleanly. Without the seal this is the keyed queue's terminal failure, and the registry's per-character check cannot help: nobody named the character.
    /// </summary>
    [Fact]
    public async Task AKeyedRetry_RefusedBetweenTheCommitAndTheRetirement_IsDropped_AndTheQueueStopsCleanly()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.RestWrites.Start();
        rig.WhileCommitted = async () =>
        {
            Assert.True(rig.RestWrites.PostRetainedSweepForTest());
            await rig.WaitForRefusalsAsync(1);
        };

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.RestWrites.Save(5, Rest(1f));
                await WorldTestHost.WaitForAsync(() => rig.RestWrites.RetainedCharacters.Contains(5), "the busy rest write of character 5 to be retained after the flush");
            }
        }

        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(1, rig.Refusals);
        Assert.Equal(1, rig.Retirement.DroppedCount("RestWriteQueue"));
        Assert.Null(rig.RestWrites.TerminalFailure);
        Assert.Empty(rig.RestWrites.RetainedCharacters);
        await rig.RestWrites.StopAsync(); // must not throw
        await rig.AssertCleanStopAsync();
    }

    /// <summary>From the retirement on nothing is attempted any more, of any character: a retained retry is skipped, and so is a write of a character nobody named, in the save queue and the keyed queues.</summary>
    [Fact]
    public async Task AfterTheRetirement_NoWriteOfAnyCharacter_IsAttempted_EvenOfOneNobodyNamed()
    {
        await using var rig = new HandoffRig(TimeSpan.FromHours(1)); // the retained retry cannot fire in the commit window: this test is about the skip after it
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.RestWrites.Start();
        rig.HonorWrites.Start();

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.Queue.Enqueue(State(5, 55));
                await WorldTestHost.WaitForAsync(() => rig.Queue.RetainedCharacters.Contains(5), "the busy snapshot of character 5 to be retained after the flush");
            }
        }

        var target = new CallbackTarget(() =>
        {
            rig.Queue.Enqueue(State(6, 66));
            rig.RestWrites.Save(7, Rest(1f));
            rig.HonorWrites.SaveState(8, CharacterHonorState.Empty);
        });
        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, target, new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(target.Promoted);
        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        await rig.RestWrites.StopAsync();
        await rig.HonorWrites.StopAsync();
        Assert.Equal(0, rig.Refusals);
        Assert.Single(rig.Store.Attempts, s => s.Id == 5); // only the first, busy attempt
        Assert.DoesNotContain(rig.Store.Attempts, s => s.Id == 6);
        Assert.Empty(rig.RestStore.Attempts);
        Assert.Empty(rig.HonorStore.Attempts);
        Assert.Equal(1, rig.Retirement.DroppedCount("RestWriteQueue"));
        Assert.Equal(1, rig.Retirement.DroppedCount("HonorWriteQueue"));
        await rig.AssertCleanStopAsync();
    }

    /// <summary>The seal is not a mask: a handoff that aborts after its flush resumes A, and a fence refusal then is a lost ownership again.</summary>
    [Fact]
    public async Task AResumedSource_TreatsAFenceRefusalAsALostOwnershipAgain()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();

        static Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
            => stage == WorldHandoffStage.Flushed ? throw new InvalidOperationException("the target went away") : Task.CompletedTask;

        await Assert.ThrowsAsync<WorldHandoffAbortedException>(() => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage));

        rig.MoveTheFence();
        rig.Queue.Enqueue(State(5, 55));
        await WorldTestHost.WaitForAsync(() => rig.Queue.TerminalFailure is not null, "the refused write of character 5 to stop the queue");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync());
        Assert.Equal(1, rig.Refusals);
    }

    /// <summary>
    /// The flush outlives its freeze: the gateway's timeout does not cancel it, and the resume does not wait for it. It is held behind the rest queue's write, as a locked database would hold it,
    /// until the world has moved on. It must fail and leave the seal alone, or a resumed world would drop every refusal for the rest of its life.
    /// </summary>
    private static async Task HoldTheFlushWhileAsync(HandoffRig rig, LocalWorldHandoffSource source, Func<Task> whileHeld)
    {
        using var entered = new ManualResetEventSlim(false);
        using var hold = new ManualResetEventSlim(false);
        rig.RestWrites.Start();
        rig.RestAnswer = _ =>
        {
            entered.Set();
            hold.Wait();
            return null;
        };

        try
        {
            rig.RestWrites.Save(1, Rest(1f));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the rest write to reach the store");
            await source.FreezeAsync(CancellationToken.None);
            Task flush = source.FlushAsync(CancellationToken.None);
            Assert.False(flush.IsCompleted); // waiting behind the held write, in the queue's ordered barrier
            await whileHeld();
            hold.Set();
            InvalidOperationException late = await Assert.ThrowsAsync<InvalidOperationException>(() => flush);
            Assert.Contains("nothing was sealed", late.Message, StringComparison.Ordinal);
        }
        finally
        {
            hold.Set();
        }
    }

    /// <summary>A refusal after a late flush is a lost ownership: in the save queue and in a queue that asks the registry, also when a new freeze has begun meanwhile (that freeze has not flushed).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFlushThatFinishesAfterTheResume_SealsNothing_AndAFenceRefusalIsStillTerminal(bool freezeAgain)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();

        await HoldTheFlushWhileAsync(rig, source, async () =>
        {
            await source.ResumeAsync(CancellationToken.None);
            if (freezeAgain)
            {
                await world.InvokeAsync(() => true, CancellationToken.None); // the first park has ended
                await source.FreezeAsync(CancellationToken.None);
            }
        });

        rig.RestAnswer = _ => new CharacterOwnershipRejectedException("world", 7);
        rig.RestWrites.Save(9, Rest(2f));
        await WorldTestHost.WaitForAsync(() => rig.RestWrites.TerminalFailure is not null, "the refused rest write to stop its queue");
        rig.MoveTheFence();
        rig.Queue.Enqueue(State(5, 55));
        await WorldTestHost.WaitForAsync(() => rig.Queue.TerminalFailure is not null, "the refused write of character 5 to stop the queue");

        Assert.Equal(0, rig.Retirement.DroppedCount("RestWriteQueue"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RestWrites.StopAsync());
    }

    /// <summary>The ownership loss the worker heartbeat reports is the other thing the seal silences: after a late flush it stops the queue, and the watcher is told it is done.</summary>
    [Fact]
    public async Task AFlushThatFinishesAfterTheResume_LeavesFailTerminalStoppingTheQueue()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        var lost = new InvalidOperationException("the worker scope no longer belongs to this world");

        await HoldTheFlushWhileAsync(rig, source, () => source.ResumeAsync(CancellationToken.None));

        Assert.True(rig.Queue.FailTerminal(lost));
        Assert.Same(lost, rig.Queue.TerminalFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync());
        await rig.RestWrites.StopAsync();
    }

    /// <summary>A flush that finds the world retired has nothing left to promise either.</summary>
    [Fact]
    public async Task AFlushThatFinishesAfterTheRetirement_Fails()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();

        await HoldTheFlushWhileAsync(rig, source, () => source.RetireWritesAsync(CancellationToken.None));

        await rig.AssertCleanStopAsync();
        await rig.RestWrites.StopAsync();
    }

    /// <summary>Only the flush can write it in time: no character barrier names it, and the queue's own ordered barrier never retries (the 30 s sweep does not run in this rig).</summary>
    [Theory]
    [InlineData(SharedWrite.InstanceRow)]
    [InlineData(SharedWrite.Guild)]
    [InlineData(SharedWrite.CharacterPurge)]
    public async Task AWriteOfNoOneCharacter_RetainedBeforeTheHandoff_IsWrittenByTheFlush_BeforeTheCommit(SharedWrite kind)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.StartSharedQueues();
        rig.SharedAnswer = _ => new InvalidOperationException("the disk said no");
        rig.WriteShared(kind, 9);
        await WorldTestHost.WaitForAsync(() => rig.RetainedShared(kind).Count > 0, "the write to be retained");
        rig.SharedAnswer = _ => null;
        bool durableAtFlushed = false;

        Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                durableAtFlushed = rig.SharedStore.Saved.Contains(9);
            }

            return Task.CompletedTask;
        }

        await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(durableAtFlushed);
        Assert.Empty(rig.RetainedShared(kind));
        Assert.Equal(0, rig.SharedDrops(kind));
        await rig.StopSharedAsync(kind);
    }

    /// <summary>One the flush cannot write fails the handoff before the commit: A resumes, still owns it and writes it once the store recovers.</summary>
    [Theory]
    [InlineData(SharedWrite.InstanceRow)]
    [InlineData(SharedWrite.Guild)]
    [InlineData(SharedWrite.CharacterPurge)]
    public async Task AWriteOfNoOneCharacterTheFlushCannotWrite_AbortsTheHandoff_AndStaysAWriteOfTheSource(SharedWrite kind)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.StartSharedQueues();
        rig.SharedAnswer = _ => new InvalidOperationException("the disk said no");
        rig.WriteShared(kind, 9);
        await WorldTestHost.WaitForAsync(() => rig.RetainedShared(kind).Count > 0, "the write to be retained");

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(
            () => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid()));

        Assert.Equal(PlayerTransferDecisionState.Aborted, aborted.Record.DecisionState);
        Assert.Null(aborted.Record.TargetOwnershipToken);
        Assert.NotEmpty(rig.RetainedShared(kind));
        rig.SharedAnswer = _ => null;
        await rig.StopSharedAsync(kind);
        Assert.Contains(9, rig.SharedStore.Saved);
    }

    /// <summary>
    /// The window of the review: a write of no one character retained after the flush, and the sweep the 30 s timer would post reaches the moved fence before the retirement. Dropped and counted.
    /// Without the seal covering these keys this is the queue's terminal failure and StopApplication.
    /// </summary>
    [Theory]
    [InlineData(SharedWrite.InstanceRow)]
    [InlineData(SharedWrite.Guild)]
    [InlineData(SharedWrite.CharacterPurge)]
    public async Task AWriteOfNoOneCharacter_RefusedBetweenTheCommitAndTheRetirement_IsDropped_AndTheQueueStopsCleanly(SharedWrite kind)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.StartSharedQueues();
        rig.WhileCommitted = async () =>
        {
            Assert.True(rig.PostSharedSweep(kind));
            await rig.WaitForRefusalsAsync(1);
        };

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.WriteShared(kind, 5);
                await WorldTestHost.WaitForAsync(() => rig.SharedWriteSettled(kind), "the busy write to be retained after the flush, and its queue to be idle");
            }
        }

        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        Assert.Equal(1, rig.Refusals);
        Assert.Equal(1, rig.SharedDrops(kind));
        Assert.Null(rig.SharedTerminal(kind));
        Assert.Empty(rig.RetainedShared(kind));
        await rig.StopSharedAsync(kind); // must not throw
        await rig.AssertCleanStopAsync();
    }

    /// <summary>A stop that comes while the source is sealed (the fence moved, nobody retired it yet: the coordinator died after the commit) reaches the fence in its final drain. Not terminal, nothing left.</summary>
    [Theory]
    [InlineData(SharedWrite.InstanceRow)]
    [InlineData(SharedWrite.Guild)]
    [InlineData(SharedWrite.CharacterPurge)]
    public async Task AWriteOfNoOneCharacter_RefusedAtAStopWhileSealed_IsDropped_AndTheQueueStopsCleanly(SharedWrite kind)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.StartSharedQueues();
        await source.FreezeAsync(CancellationToken.None);
        await source.FlushAsync(CancellationToken.None);
        rig.WriteShared(kind, 5);
        await WorldTestHost.WaitForAsync(() => rig.SharedWriteSettled(kind), "the busy write to be retained after the flush, and its queue to be idle");
        rig.MoveTheFence();

        await rig.StopSharedAsync(kind); // must not throw

        Assert.Equal(1, rig.Refusals);
        Assert.Equal(1, rig.SharedDrops(kind));
        Assert.Null(rig.SharedTerminal(kind));
        Assert.Empty(rig.RetainedShared(kind));
    }

    /// <summary>After the retirement neither the retained write nor a new one is attempted, at the stop drain included: they could only be refused.</summary>
    [Theory]
    [InlineData(SharedWrite.InstanceRow)]
    [InlineData(SharedWrite.Guild)]
    [InlineData(SharedWrite.CharacterPurge)]
    public async Task AfterTheRetirement_NoWriteOfNoOneCharacter_IsAttempted_AtTheStopIncluded(SharedWrite kind)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.StartSharedQueues();
        int attemptsBefore = 0;

        async Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                rig.WriteShared(kind, 5);
                await WorldTestHost.WaitForAsync(() => rig.SharedWriteSettled(kind), "the busy write to be retained after the flush, and its queue to be idle");
                attemptsBefore = rig.SharedStore.Attempts.Count;
            }
        }

        var target = new CallbackTarget(() => rig.WriteShared(kind, 6));
        WorldHandoffRecord done = await new WorldHandoffCoordinator(rig.Journal, source, target, new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.Equal(WorldHandoffStage.Retired, done.Stage);
        await rig.StopSharedAsync(kind); // must not throw
        Assert.Equal(0, rig.Refusals);
        Assert.Equal(attemptsBefore, rig.SharedStore.Attempts.Count);
        Assert.Equal(2, rig.SharedDrops(kind)); // the retained one and the new one
        Assert.Null(rig.SharedTerminal(kind));
        Assert.Empty(rig.RetainedShared(kind));
    }

    /// <summary>The seal is no mask for these keys either: after a resume a refusal is a lost ownership.</summary>
    [Theory]
    [InlineData(SharedWrite.InstanceRow)]
    [InlineData(SharedWrite.Guild)]
    [InlineData(SharedWrite.CharacterPurge)]
    public async Task AResumedSource_TreatsARefusalOfAWriteOfNoOneCharacterAsALostOwnershipAgain(SharedWrite kind)
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.StartSharedQueues();

        static Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
            => stage == WorldHandoffStage.Flushed ? throw new InvalidOperationException("the target went away") : Task.CompletedTask;

        await Assert.ThrowsAsync<WorldHandoffAbortedException>(() => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage));

        rig.MoveTheFence();
        rig.WriteShared(kind, 5);
        await WorldTestHost.WaitForAsync(() => rig.SharedTerminal(kind) is not null, "the refused write to stop its queue");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StopSharedAsync(kind));
        Assert.Equal(0, rig.SharedDrops(kind));
    }

    /// <summary>The worker heartbeat reports the moved ownership row through <see cref="CharacterSaveQueue.FailTerminal"/>: while a handoff decides, and after it retired the world, that is no reason to stop.</summary>
    [Fact]
    public async Task AnOwnershipLossReported_WhileSealedOrRetired_IsNotTerminal_AndIsAfterTheSealLifts()
    {
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(new FencedStore(moved: 1)).BuildServiceProvider();
        var sealedQueue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger());
        var retiredQueue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger());
        var lost = new InvalidOperationException("the worker scope no longer belongs to this world");

        sealedQueue.SealWrites();
        Assert.False(sealedQueue.FailTerminal(lost)); // undecided: the watcher keeps watching, a resume can make this terminal
        Assert.Null(sealedQueue.TerminalFailure);
        sealedQueue.UnsealWrites();
        Assert.True(sealedQueue.FailTerminal(lost));
        Assert.Same(lost, sealedQueue.TerminalFailure);

        retiredQueue.RetireEveryCharacter();
        Assert.True(retiredQueue.FailTerminal(lost)); // decided: nothing left to watch
        Assert.Null(retiredQueue.TerminalFailure);
    }

    /// <summary>An offline character with nothing in the save queue and a retained rest write: only the keyed queue knows it. The real barrier writes it before the commit, so B reads it.</summary>
    [Fact]
    public async Task AnOfflineCharactersRetainedRestWrite_IsWrittenThroughTheBarrier_BeforeTheCommit()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.RestWrites.Start();
        rig.RestAnswer = _ => new InvalidOperationException("the disk said no");
        rig.RestWrites.Save(9, Rest(4f));
        await WorldTestHost.WaitForAsync(() => rig.RestWrites.RetainedCharacters.Contains(9), "the rest write of character 9 to be retained");
        rig.RestAnswer = _ => null;
        bool durableAtFlushed = false;

        Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                durableAtFlushed = rig.RestStore.Saved.Any(s => s.Id == 9);
            }

            return Task.CompletedTask;
        }

        await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(durableAtFlushed);
        Assert.Empty(rig.RestWrites.RetainedCharacters);
        Assert.Equal(0, rig.Retirement.DroppedCount("RestWriteQueue"));
        await rig.RestWrites.StopAsync();
    }

    /// <summary>The same for honor, whose queue is not the shared keyed engine.</summary>
    [Fact]
    public async Task AnOfflineCharactersRetainedHonorWrite_IsWrittenThroughTheBarrier_BeforeTheCommit()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.HonorWrites.Start();
        rig.HonorAnswer = _ => new InvalidOperationException("the disk said no");
        rig.HonorWrites.SaveState(9, CharacterHonorState.Empty);
        await WorldTestHost.WaitForAsync(() => rig.HonorWrites.RetainedCharacters.Contains(9), "the honor write of character 9 to be retained");
        rig.HonorAnswer = _ => null;
        bool durableAtFlushed = false;

        Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                durableAtFlushed = rig.HonorStore.Saved.Contains(9);
            }

            return Task.CompletedTask;
        }

        await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(durableAtFlushed);
        Assert.Empty(rig.HonorWrites.RetainedCharacters);
        await rig.HonorWrites.StopAsync();
    }

    /// <summary>A keyed write the flush cannot write fails the handoff before the commit: A resumes, still owns the character and writes it once the store recovers.</summary>
    [Fact]
    public async Task AnOfflineCharactersKeyedWriteTheFlushCannotWrite_AbortsTheHandoff_AndStaysAWriteOfTheSource()
    {
        await using var rig = new HandoffRig();
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Start();
        rig.RestWrites.Start();
        rig.RestAnswer = _ => new InvalidOperationException("the disk said no");
        rig.RestWrites.Save(9, Rest(4f));
        await WorldTestHost.WaitForAsync(() => rig.RestWrites.RetainedCharacters.Contains(9), "the rest write of character 9 to be retained");

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(
            () => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid()));

        Assert.Equal(PlayerTransferDecisionState.Aborted, aborted.Record.DecisionState);
        Assert.Null(aborted.Record.TargetOwnershipToken);
        Assert.Equal(9, Assert.Single(rig.RestWrites.RetainedCharacters));
        rig.RestAnswer = _ => null;
        await rig.RestWrites.StopAsync();
        Assert.Contains(rig.RestStore.Saved, s => s.Id == 9);
    }

    /// <summary>A failure that is not a locked database is retained and never retried by the idle consumer: only the flush can write it, and it does, while A still owns the character.</summary>
    [Fact]
    public async Task TheFlush_WritesARetainedSnapshotOfAnOfflineCharacter_BeforeTheCommit()
    {
        await using var rig = new HandoffRig();
        rig.Write = _ => new InvalidOperationException("the disk said no");
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Enqueue(State(5, 55));
        rig.Queue.Start();
        await WorldTestHost.WaitForAsync(() => rig.Store.Attempts.Count >= 3 && rig.Queue.Pending == 0, "the inline attempts of character 5 to end");
        rig.Write = _ => null;
        bool durableAtFlushed = false;

        Task AfterStage(WorldHandoffStage stage, CancellationToken cancellationToken)
        {
            if (stage == WorldHandoffStage.Flushed)
            {
                durableAtFlushed = rig.Store.Saved.Any(s => s.Id == 5);
            }

            return Task.CompletedTask;
        }

        await new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid(), AfterStage);

        Assert.True(durableAtFlushed);
        Assert.Empty(rig.Queue.RetainedCharacterIds());
        await rig.Queue.StopAsync();
        Assert.Null(rig.Queue.TerminalFailure);
    }

    /// <summary>A save the flush cannot write fails the handoff before the commit: A resumes, still owns the character, keeps the snapshot and writes it once the store recovers.</summary>
    [Fact]
    public async Task ARetainedSnapshotTheFlushCannotWrite_AbortsTheHandoff_AndStaysAWriteOfTheSource()
    {
        await using var rig = new HandoffRig();
        rig.Write = _ => new InvalidOperationException("the disk said no");
        using WorldRuntime world = rig.StartWorld();
        using var source = rig.Source(world);
        rig.Queue.Enqueue(State(5, 55));
        rig.Queue.Start();
        await WorldTestHost.WaitForAsync(() => rig.Store.Attempts.Count >= 3 && rig.Queue.Pending == 0, "the inline attempts of character 5 to end");

        WorldHandoffAbortedException aborted = await Assert.ThrowsAsync<WorldHandoffAbortedException>(
            () => new WorldHandoffCoordinator(rig.Journal, source, new PlainTarget(), new NoRouter()).RunAsync(Guid.NewGuid()));

        Assert.Equal(PlayerTransferDecisionState.Aborted, aborted.Record.DecisionState);
        Assert.Null(aborted.Record.TargetOwnershipToken);
        Assert.Equal(new[] { 5 }, rig.Queue.RetainedCharacterIds());
        Assert.False(rig.Queue.IsQuarantined(5)); // the retirement never ran
        rig.Write = _ => null;
        await rig.Queue.StopAsync();
        Assert.Contains(rig.Store.Saved, s => s.Id == 5 && s.Money == 55);
    }

    [Fact]
    public async Task WithoutRetirement_TheFenceRefusalOfAMovedCharacter_IsTerminalForTheQueue()
    {
        var store = new FencedStore(moved: 1);
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);

        queue.Enqueue(State(1, 11));
        queue.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());
        Assert.Contains(store.Attempts, s => s.Id == 1);
    }

    [Fact]
    public async Task RetireCharacters_IsIdempotent_AndLogsNothingForAnEmptyOrRepeatedSet()
    {
        var logger = new CaptureLogger();
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(new FencedStore(moved: 1)).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), logger);

        Assert.Equal(0, queue.RetireCharacters([]));
        queue.Enqueue(State(1, 11));
        Assert.Equal(1, queue.RetireCharacters([1]));
        Assert.Equal(0, queue.RetireCharacters([1]));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
    }

    private static CharacterRestState Rest(float bonus) => new(bonus, 1000, false);

    private static (RestWriteQueue Queue, CharacterRetirementRegistry Registry, FencedRestStore Store, CaptureLogger Logger, ServiceProvider Services) RestRig(int moved, bool withRegistry = true)
    {
        var store = new FencedRestStore(moved);
        ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterRestStore>(store).BuildServiceProvider();
        var registry = new CharacterRetirementRegistry();
        store.Registry = registry;
        var logger = new CaptureLogger();
        var queue = new RestWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), logger)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
            Retirement = withRegistry ? registry : null,
        };
        return (queue, registry, store, logger, services);
    }

    [Fact]
    public async Task KeyedQueue_DropsTheWritesOfARetiredCharacter_StopsCleanly_AndStillWritesAnOwnedCharacter()
    {
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, CaptureLogger logger, ServiceProvider services) = RestRig(moved: 1);
        await using ServiceProvider owned = services;
        Exception? fatal = null;
        queue.FatalFailure += ex => fatal = ex;

        // Both characters have a write waiting before the queue runs; then character 1 moves to the new owner; a later write of it is dropped on arrival.
        queue.Save(1, Rest(1f));
        queue.Save(2, Rest(2f));
        registry.Retire([1]);
        queue.Save(1, Rest(3f));

        queue.Start();
        await queue.StopAsync(); // must not throw: nothing is refused by the fence and nothing is left undrained, so no announcement either

        Assert.Null(fatal);
        Assert.Null(queue.TerminalFailure);
        Assert.Equal(0, queue.Pending);
        Assert.Empty(queue.RetainedCharacters);
        Assert.DoesNotContain(store.Attempts, a => a.Id == 1);
        (int id, CharacterRestState kept) = Assert.Single(store.Saved);
        Assert.Equal(2, id);
        Assert.Equal(2f, kept.RestBonus);
        Assert.Equal(2, registry.DroppedCount("RestWriteQueue"));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("RestWriteQueue dropped 2 write(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task KeyedQueue_CountsAFenceRefusalOfACharacterRetiredMidWrite_WithoutAnErrorOrTerminalFailure()
    {
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, CaptureLogger logger, ServiceProvider services) = RestRig(moved: 1);
        await using ServiceProvider owned = services;
        store.RetireWhenWriting = true; // the character moves while its write is in the store call: the fence refuses it

        queue.Save(1, Rest(1f));
        queue.Start();
        await queue.StopAsync();

        Assert.Null(queue.TerminalFailure);
        Assert.Empty(queue.RetainedCharacters);
        Assert.Contains(store.Attempts, a => a.Id == 1);
        Assert.Equal(1, registry.DroppedCount("RestWriteQueue"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task KeyedQueue_AFenceRefusalOfACharacterThatIsStillOwned_IsStillTerminal()
    {
        // The registry exists but names nobody: a refusal of character 1 is a real loss of ownership, handled exactly as before.
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, _, ServiceProvider services) = RestRig(moved: 1);
        await using ServiceProvider owned = services;

        queue.Save(1, Rest(1f));
        queue.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());

        Assert.NotNull(queue.TerminalFailure);
        Assert.Contains(store.Attempts, a => a.Id == 1);
        Assert.Equal(0, registry.DroppedCount("RestWriteQueue"));
    }

    [Fact]
    public async Task KeyedQueue_WithoutARegistry_BehavesAsBefore()
    {
        (RestWriteQueue queue, CharacterRetirementRegistry registry, FencedRestStore store, _, ServiceProvider services) = RestRig(moved: 1, withRegistry: false);
        await using ServiceProvider owned = services;
        registry.Retire([1]); // not wired into the queue

        queue.Save(1, Rest(1f));
        queue.Save(2, Rest(2f));
        queue.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync());

        Assert.Contains(store.Attempts, a => a.Id == 1);
        Assert.Equal(0, registry.DroppedCount("RestWriteQueue"));
    }

    [Fact]
    public void Registry_CountsPerQueue_AndOnlyForRetiredCharacters()
    {
        var registry = new CharacterRetirementRegistry();
        Assert.False(registry.TryDrop("A", 1));
        Assert.Equal(2, registry.Retire([1, 2, 2]));
        Assert.Equal(0, registry.Retire([1]));
        Assert.True(registry.IsRetired(1));
        Assert.False(registry.IsRetired(3));
        Assert.True(registry.TryDrop("A", 1));
        Assert.True(registry.TryDrop("A", 2));
        Assert.True(registry.TryDrop("B", 1));
        Assert.False(registry.TryDrop("A", 3));
        Assert.Equal(2, registry.DroppedCount("A"));
        Assert.Equal(1, registry.DroppedCount("B"));
        Assert.Equal(0, registry.DroppedCount("C"));
        Assert.False(CharacterRetirementRegistry.Drops(null, "A", 1));
        Assert.False(CharacterRetirementRegistry.Retired(null, 1));
    }

    [Fact]
    public void Registry_Sealed_DropsOnlyARefusal_AndRetireAll_CoversEveryCharacter()
    {
        var registry = new CharacterRetirementRegistry();
        registry.Seal();
        Assert.False(registry.TryDrop("A", 1)); // a write is still attempted: the takeover may not have landed
        Assert.False(registry.IsRetired(1));
        Assert.True(registry.TryDropRefused("A", 1));
        registry.Unseal();
        Assert.False(registry.TryDropRefused("A", 1));
        Assert.Equal(1, registry.DroppedCount("A"));

        registry.RetireAll();
        Assert.True(registry.IsRetired(99));
        Assert.True(registry.TryDrop("A", 99));
        Assert.True(registry.TryDropRefused("A", 99));
        Assert.False(CharacterRetirementRegistry.DropsRefused(null, "A", 99));
    }

    [Fact]
    public void Registry_AWriteOfNoOneCharacter_FollowsOnlyTheWholeProcess()
    {
        var registry = new CharacterRetirementRegistry();
        registry.Retire([1, 2]);
        Assert.False(registry.TryDropShared("A")); // a transfer retires a character; the instance rows and guilds stay here
        Assert.False(registry.TryDropSharedRefused("A"));

        registry.Seal();
        Assert.False(registry.TryDropShared("A")); // still attempted: the takeover may not have landed
        Assert.True(registry.TryDropSharedRefused("A"));
        registry.Unseal();
        Assert.False(registry.TryDropSharedRefused("A"));
        Assert.Equal(1, registry.DroppedCount("A"));

        registry.RetireAll();
        Assert.True(registry.TryDropShared("A"));
        Assert.True(registry.TryDropSharedRefused("A"));
        Assert.Equal(3, registry.DroppedCount("A"));
        Assert.False(CharacterRetirementRegistry.DropsShared(null, "A"));
        Assert.False(CharacterRetirementRegistry.DropsSharedRefused(null, "A"));
    }

    /// <summary>Refuses every write of the moved character the way the characters fence refuses a stale owner.</summary>
    private sealed class FencedRestStore(int moved) : ICharacterRestStore
    {
        public ConcurrentQueue<(int Id, CharacterRestState State)> Attempts { get; } = new();
        public ConcurrentQueue<(int Id, CharacterRestState State)> Saved { get; } = new();
        public CharacterRetirementRegistry? Registry { get; set; }
        public bool RetireWhenWriting { get; set; }

        /// <summary>The failure of this write, scripted by the test; null lets the rules below decide.</summary>
        public Func<int, Exception?>? Answer { get; init; }

        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<CharacterRestState?>(null);

        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue((characterId, state));
            if (Answer?.Invoke(characterId) is { } scripted)
                return Task.FromException(scripted);
            if (characterId == moved)
            {
                if (RetireWhenWriting)
                    Registry?.Retire([characterId]);
                return Task.FromException(new CharacterOwnershipRejectedException("world", 7));
            }

            Saved.Enqueue((characterId, state));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// The production wiring around stores whose answers the test scripts: a real source, a real barrier and real queues over one registry. By default a write of character 5 or above finds the
    /// database locked until the fence moves, and is refused from then on. The fence moves inside the journal's commit, as the epoch takeover moves it.
    /// </summary>
    private sealed class HandoffRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private int _fenceMoved;
        private int _refusals;
        private Exception? _fatal;

        public HandoffRig(TimeSpan? busyBackoff = null)
        {
            Store = new FencedStore(moved: 0) { Answer = state => Write(state) };
            RestStore = new FencedRestStore(moved: 0) { Answer = id => RestAnswer(id) };
            HonorStore = new ScriptedHonorStore { Answer = id => HonorAnswer(id) };
            SharedStore = new ScriptedSharedStore { Answer = id => SharedAnswer(id) };
            Write = state => state.Id >= 5 ? LockedOrRefused() : null;
            RestAnswer = id => id >= 5 ? LockedOrRefused() : null;
            HonorAnswer = id => id >= 5 ? LockedOrRefused() : null;
            SharedAnswer = id => id >= 5 ? LockedOrRefused() : null;
            Journal = new HandoffJournal(OnCommitAsync);
            _services = new ServiceCollection()
                .AddSingleton<ICharacterStore>(Store)
                .AddSingleton<ICharacterRestStore>(RestStore)
                .AddSingleton<IHonorStore>(HonorStore)
                .AddSingleton<IInstanceStore>(SharedStore)
                .AddSingleton<ISocialStore>(SharedStore)
                .AddSingleton(Retirement)
                .AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), new CaptureLogger()) { BusyBackoff = [busyBackoff ?? TimeSpan.FromMilliseconds(10)] })
                .AddSingleton(sp => new RestWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
                {
                    RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                    RetryDelay = TimeSpan.FromMilliseconds(1),
                    Retirement = Retirement,
                })
                .AddSingleton(sp => new HonorWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
                {
                    RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                    Retirement = Retirement,
                })
                .AddSingleton(sp => new InstanceWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
                {
                    RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                    RetryDelay = TimeSpan.FromMilliseconds(1),
                    Retirement = Retirement,
                })
                .AddSingleton(sp => new SocialWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, new SocialWriteQueueOptions { RetryDelayMs = 1 })
                {
                    RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                    Retirement = Retirement,
                })
                .AddSingleton<CharacterPersistenceBarrier>()
                .BuildServiceProvider();
            Queue = _services.GetRequiredService<CharacterSaveQueue>();
            RestWrites = _services.GetRequiredService<RestWriteQueue>();
            HonorWrites = _services.GetRequiredService<HonorWriteQueue>();
            InstanceWrites = _services.GetRequiredService<InstanceWriteQueue>();
            SocialWrites = _services.GetRequiredService<SocialWriteQueue>();
            Queue.FatalFailure += ex => _fatal = ex;
        }

        public FencedStore Store { get; }

        public FencedRestStore RestStore { get; }

        public ScriptedHonorStore HonorStore { get; }

        public ScriptedSharedStore SharedStore { get; }

        public Func<CharacterState, Exception?> Write { get; set; }

        public Func<int, Exception?> RestAnswer { get; set; }

        public Func<int, Exception?> HonorAnswer { get; set; }

        public Func<int, Exception?> SharedAnswer { get; set; }

        public CharacterRetirementRegistry Retirement { get; } = new();

        public CharacterSaveQueue Queue { get; }

        public RestWriteQueue RestWrites { get; }

        public HonorWriteQueue HonorWrites { get; }

        public InstanceWriteQueue InstanceWrites { get; }

        public SocialWriteQueue SocialWrites { get; }

        public HandoffJournal Journal { get; }

        /// <summary>Runs inside the journal's commit, with the fence already moved: the test holds the window between the commit and the retirement step open here.</summary>
        public Func<Task>? WhileCommitted { get; set; }

        public int Refusals => Volatile.Read(ref _refusals);

        public WorldRuntime UnstartedWorld() => new(new WorldRuntimeOptions(), NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = new CharacterOwnershipToken("world", 3, Guid.NewGuid()),
        };

        public WorldRuntime StartWorld()
        {
            WorldRuntime world = UnstartedWorld();
            world.Start();
            return world;
        }

        /// <summary>The source as WorldHandoffHosting builds it: with the real barrier and the shared registry.</summary>
        public LocalWorldHandoffSource Source(WorldRuntime world) => new(
            new WorkerNodeId("w1"), world, Queue, new WorkerAdmission(), new WorldWriterGuard(), _services.GetRequiredService<CharacterPersistenceBarrier>(), Retirement);

        public void MoveTheFence() => Volatile.Write(ref _fenceMoved, 1);

        /// <summary>Ends when the fence has refused that many writes, or when a queue went terminal (the test's assertions then fail).</summary>
        public Task WaitForRefusalsAsync(int refusals) => WorldTestHost.WaitForAsync(
            () => Refusals >= refusals || Queue.TerminalFailure is not null || RestWrites.TerminalFailure is not null
                || InstanceWrites.TerminalFailure is not null || SocialWrites.TerminalFailure is not null,
            $"the fence to refuse {refusals} write(s)");

        public async Task AssertCleanStopAsync()
        {
            Assert.Null(_fatal);
            Assert.Null(Queue.TerminalFailure);
            Assert.Empty(Queue.RetainedCharacterIds());
            await Queue.StopAsync(); // must not throw
            Assert.Null(Queue.TerminalFailure);
            Assert.DoesNotContain(Store.Saved, s => s.Id >= 5);
        }

        public void StartSharedQueues()
        {
            InstanceWrites.Start();
            SocialWrites.Start();
        }

        public void WriteShared(SharedWrite kind, int id)
        {
            switch (kind)
            {
                case SharedWrite.InstanceRow:
                    InstanceWrites.EnqueueInstanceSaveForTest((uint)id, 409, 1_700_000_000);
                    break;
                case SharedWrite.Guild:
                    SocialWrites.SaveGuild(new GuildData(id, "Guild", 1, "", "", 0, 0, 0, 0, 0, 0, [], []));
                    break;
                default:
                    SocialWrites.PurgeCharacter(id);
                    break;
            }
        }

        public IReadOnlyList<string> RetainedShared(SharedWrite kind) => kind == SharedWrite.InstanceRow ? InstanceWrites.RetainedSharedWrites : SocialWrites.RetainedSharedWrites;

        /// <summary>Retained, and the queue idle: a purge is two writes, and the second retries the first once more.</summary>
        public bool SharedWriteSettled(SharedWrite kind) => RetainedShared(kind).Count > 0 && (kind == SharedWrite.InstanceRow ? InstanceWrites.Pending : SocialWrites.Pending) == 0;

        public bool PostSharedSweep(SharedWrite kind) => kind == SharedWrite.InstanceRow ? InstanceWrites.PostRetainedSweepForTest() : SocialWrites.PostRetainedSweepForTest();

        public Exception? SharedTerminal(SharedWrite kind) => kind == SharedWrite.InstanceRow ? InstanceWrites.TerminalFailure : SocialWrites.TerminalFailure;

        public long SharedDrops(SharedWrite kind) => Retirement.DroppedCount(kind == SharedWrite.InstanceRow ? "InstanceWriteQueue" : "SocialWriteQueue");

        public Task StopSharedAsync(SharedWrite kind) => kind == SharedWrite.InstanceRow ? InstanceWrites.StopAsync() : SocialWrites.StopAsync();

        public async ValueTask DisposeAsync()
        {
            // A test that cares has already stopped these and asserted how; this only ends the consumers of the rest.
            await Task.WhenAll(InstanceWrites.StopAsync(), SocialWrites.StopAsync()).ContinueWith(static _ => { }, TaskScheduler.Default);
            await _services.DisposeAsync();
        }

        private async Task OnCommitAsync()
        {
            MoveTheFence();
            if (WhileCommitted is { } window)
            {
                await window().ConfigureAwait(false);
            }
        }

        private Exception LockedOrRefused()
        {
            if (Volatile.Read(ref _fenceMoved) == 0)
            {
                return new SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            Interlocked.Increment(ref _refusals);
            return new CharacterOwnershipRejectedException("world", 7);
        }
    }

    /// <summary>Runs its action as the promotion starts: by then the retirement step has run.</summary>
    private sealed class CallbackTarget(Action whilePromoting) : PlainTarget
    {
        private int _promoted;

        public bool Promoted => Volatile.Read(ref _promoted) == 1;

        public override Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        {
            whilePromoting();
            Volatile.Write(ref _promoted, 1);
            return Task.CompletedTask;
        }
    }

    private class PlainTarget : IWorldHandoffTarget
    {
        public WorkerNodeId WorkerId { get; } = new("w2");

        public string OwnerNode => "w2";

        public Guid Incarnation { get; } = Guid.NewGuid();

        public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoRouter : IWorldHandoffRouter
    {
        public Task RerouteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HandoffJournal(Func<Task> onCommit) : IWorldHandoffStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, WorldHandoffRecord> _records = [];

        private WorldHandoffRecord Current(Guid id)
        {
            lock (_gate)
            {
                return _records[id];
            }
        }

        private WorldHandoffRecord Put(WorldHandoffRecord record)
        {
            lock (_gate)
            {
                _records[record.HandoffId] = record;
            }

            return record;
        }

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(new WorldHandoffRecord(handoffId, sourceWorkerId, targetWorkerId, WorldHandoffStage.Begun, PlayerTransferDecisionState.PreCommit, sourceOwnershipToken, null, null, null, 0, 0)));

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = nextStage }));

        public async Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
        {
            WorldHandoffRecord before = Current(handoffId);
            var taken = new CharacterOwnershipToken(before.SourceOwnershipToken.Scope, before.SourceOwnershipToken.Epoch + 1, targetIncarnation);
            WorldHandoffRecord committed = Put(before with
            {
                Stage = WorldHandoffStage.EpochTakeover,
                DecisionState = PlayerTransferDecisionState.Committed,
                TargetOwnershipToken = taken,
                TargetOwnerNode = targetOwnerNode,
            });
            await onCommit().ConfigureAwait(false);
            return committed;
        }

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Rerouted }));

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Retired, DecisionState = PlayerTransferDecisionState.Completed }));

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Aborted, DecisionState = PlayerTransferDecisionState.Aborted, AbortReason = reason }));

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_records.GetValueOrDefault(handoffId));
            }
        }

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([.. _records.Values.Where(r => !r.IsResolved)]);
            }
        }

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw new NotSupportedException();
    }

    /// <summary>Honor rows whose answers the test scripts; only the state write is used.</summary>
    private sealed class ScriptedHonorStore : IHonorStore
    {
        public ConcurrentQueue<int> Attempts { get; } = new();
        public ConcurrentQueue<int> Saved { get; } = new();
        public Func<int, Exception?>? Answer { get; init; }

        public Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(characterId);
            if (Answer?.Invoke(characterId) is { } scripted)
                return Task.FromException(scripted);
            Saved.Enqueue(characterId);
            return Task.CompletedTask;
        }

        public Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(CharacterHonorData.Empty);
        public Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<HonorMaintenanceState?>(null);
        public Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HonorWeeklyScore>>([]);
        public Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>The instance and social stores with a scripted answer per id: an instance, a guild or a purged character.</summary>
    private sealed class ScriptedSharedStore : IInstanceStore, ISocialStore
    {
        public ConcurrentQueue<int> Attempts { get; } = new();
        public ConcurrentQueue<int> Saved { get; } = new();
        public required Func<int, Exception?> Answer { get; init; }

        private Task WriteAsync(int id)
        {
            Attempts.Enqueue(id);
            if (Answer(id) is { } scripted)
                return Task.FromException(scripted);
            Saved.Enqueue(id);
            return Task.CompletedTask;
        }

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default) => WriteAsync((int)instance.Id);
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => WriteAsync(guild.Id);
        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => WriteAsync(characterId);

        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(InstanceStoreSnapshot.Empty);
        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SocialEntry>>([]);
        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Refuses every write of the moved character the way the characters fence refuses a stale owner.</summary>
    private sealed class FencedStore(int moved) : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        public ConcurrentQueue<CharacterState> Attempts { get; } = new();
        public ConcurrentQueue<CharacterState> Saved { get; } = new();

        /// <summary>Runs inside the store call, before the fence answers: the point where a handoff can retire a character whose write is already in flight.</summary>
        public Action<CharacterState>? OnSave { get; set; }

        /// <summary>The character whose first write fails as a locked database (retained by the queue) before the fence logic applies; null for none.</summary>
        public int? BusyOnce { get; set; }

        /// <summary>The failure of this write, scripted by the test; null lets the rules below decide.</summary>
        public Func<CharacterState, Exception?>? Answer { get; init; }

        private int _busyDone;

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue(state);
            OnSave?.Invoke(state);
            if (Answer?.Invoke(state) is { } scripted)
                return Task.FromException(scripted);
            if (BusyOnce == state.Id && Interlocked.Exchange(ref _busyDone, 1) == 0)
                return Task.FromException(new SqliteException("SQLite Error 5: 'database is locked'.", 5));
            if (state.Id == moved)
                return Task.FromException(new CharacterOwnershipRejectedException("world", 7));
            Saved.Enqueue(state);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => _inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => _inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => _inner.GetAllIdentitiesAsync(cancellationToken);
    }

    private sealed class CaptureLogger : ILogger<CharacterSaveQueue>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
