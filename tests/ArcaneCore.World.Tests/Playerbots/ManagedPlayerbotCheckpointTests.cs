using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>The 5 s checkpoint writes a running bot only when its persisted goal fields changed (one batched write for all of them).</summary>
public sealed class ManagedPlayerbotCheckpointTests
{
    private static async Task CheckpointAsync(WorldTestHost host, ManagedPlayerbotFeature feature)
    {
        Task before = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        await before.WaitAsync(TimeSpan.FromSeconds(30));
        for (int tick = 0; tick < 110; tick++) await host.World.AdvanceClockAsync(50); // a checkpoint starts at 5000 ms
        Task started = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        Assert.NotSame(before, started);
        await started.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<Guid[]> StartBotsAsync(ManagedPlayerbotFeature feature, int count)
    {
        var ids = new Guid[count];
        for (int i = 0; i < count; i++)
        {
            PlayerbotOperationResult created = await feature.CreateAsync("Ckpt" + (char)('a' + i), 1, 1);
            Assert.True(created.Success, created.Code);
            ids[i] = created.BotId!.Value;
            Assert.True((await feature.StartScriptedAsync(ids[i].ToString(), new IdleController())).Success);
        }

        return ids;
    }

    private sealed class IdleController : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }
        public void Detached(Guid botId) { }
    }

    [Fact]
    public async Task UnchangedGoals_AreNotWritten_ByAnyCheckpoint()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 3);

        await CheckpointAsync(host, feature); // settles whatever the start left different from the brain
        long[] revisions = ids.Select(id => owners.Peek(id)!.Revision).ToArray();
        int rows = owners.RowsWritten;
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.Equal(rows, owners.RowsWritten);
        Assert.Equal(revisions, ids.Select(id => owners.Peek(id)!.Revision).ToArray());
    }

    [Fact]
    public async Task AChangedGoal_IsTheOnlyRowTheNextCheckpointWrites()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 3);
        await CheckpointAsync(host, feature);
        long[] revisions = ids.Select(id => owners.Peek(id)!.Revision).ToArray();
        int rows = owners.RowsWritten;

        await host.World.InvokeAsync(() => { feature.FindBrain(ids[1])!.HoldGoalForTests(PlayerbotGoalKind.Quest, 1234, 77); return true; });
        await CheckpointAsync(host, feature);

        Assert.Equal(rows + 1, owners.RowsWritten);
        Assert.Equal(revisions[0], owners.Peek(ids[0])!.Revision);
        Assert.Equal(revisions[1] + 1, owners.Peek(ids[1])!.Revision);
        Assert.Equal(revisions[2], owners.Peek(ids[2])!.Revision);
        ManagedPlayerbot stored = owners.Peek(ids[1])!;
        Assert.Equal((PlayerbotGoalKind.Quest, 1234u, 77u), (stored.Goal, stored.TargetEntry, stored.QuestId));
    }

    [Fact]
    public async Task ARevisionConflict_FaultsOnlyThatBot_ItLeavesCleanly_AndTheOthersKeepCheckpointing()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new RecordingSetAsideStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            logger: log, manualClock: true, setAsideStore: setAsides, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 3);
        await CheckpointAsync(host, feature);

        // Another writer moves the first bot's row on; the bot's next write is stale. The second bot changes goal and set-asides too.
        owners.BumpRevision(ids[0]);
        await host.World.InvokeAsync(() =>
        {
            feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            feature.FindBrain(ids[1])!.HoldGoalForTests(PlayerbotGoalKind.Quest, 22, 5);
            feature.FindBrain(ids[1])!.Suspensions.SuspendQuest(1361, host.World.NowMs);
            return true;
        });
        await CheckpointAsync(host, feature);
        ManagedPlayerbot second = owners.Peek(ids[1])!;
        Assert.Equal((PlayerbotGoalKind.Quest, 22u), (second.Goal, second.TargetEntry)); // the conflict did not stop the batch
        Assert.NotEmpty(setAsides.RowsOf(ids[1]));

        // The conflicted bot is faulted, stops through the normal path and leaves the active set; the next checkpoints are not blocked.
        for (int i = 0; i < 4 && owners.Peek(ids[0])!.State != ManagedPlayerbotState.Faulted; i++) await CheckpointAsync(host, feature);
        Assert.Equal(ManagedPlayerbotState.Faulted, owners.Peek(ids[0])!.State);
        Assert.Null(await host.World.InvokeAsync(() => feature.FindBrain(ids[0])));

        await host.World.InvokeAsync(() =>
        {
            feature.FindBrain(ids[2])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 33, 0);
            feature.FindBrain(ids[2])!.Suspensions.SuspendQuest(6605, host.World.NowMs);
            return true;
        });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);
        ManagedPlayerbot third = owners.Peek(ids[2])!;
        Assert.Equal((PlayerbotGoalKind.Grind, 33u), (third.Goal, third.TargetEntry));
        Assert.NotEmpty(setAsides.RowsOf(ids[2]));

        // One fault recorded, no stop failure repeating every round.
        Assert.Equal(1, log.Entries.Count(e => e.Message.Contains($"Playerbot {ids[0]} quarantined for")));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("checkpoint stop failed"));
    }

    private sealed class RecordingSetAsideStore : IManagedPlayerbotSetAsideStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PlayerbotSetAside[]> _rows = new();

        public PlayerbotSetAside[] RowsOf(Guid botId) => _rows.GetValueOrDefault(botId) ?? [];

        public Task<IReadOnlyList<PlayerbotSetAside>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotSetAside>>([.. _rows.Values.SelectMany(rows => rows)]);

        public Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotSetAside> rows, CancellationToken cancellationToken = default)
        {
            _rows[botId] = [.. rows];
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AGoalChange_SurvivesARestart_AndSkippedBotsLoseNothing()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        Guid[] ids;
        await using (WorldTestHost first = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, manualClock: true))
        {
            ManagedPlayerbotFeature feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            ids = await StartBotsAsync(feature, 2);
            await CheckpointAsync(first, feature);
            await first.World.InvokeAsync(() => { feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 4321, 0); return true; });
            await CheckpointAsync(first, feature);
            await feature.ShutdownBeforeWorldStopAsync();
        }

        IReadOnlyList<ManagedPlayerbot> reloaded = await owners.LoadAllAsync();
        ManagedPlayerbot changed = reloaded.Single(b => b.BotId == ids[0]);
        Assert.Equal((PlayerbotGoalKind.Grind, 4321u), (changed.Goal, changed.TargetEntry));
        ManagedPlayerbot untouched = reloaded.Single(b => b.BotId == ids[1]);
        Assert.Equal(PlayerbotGoalKind.Explore, untouched.Goal);
        Assert.Equal(2, reloaded.Count);
    }
}
