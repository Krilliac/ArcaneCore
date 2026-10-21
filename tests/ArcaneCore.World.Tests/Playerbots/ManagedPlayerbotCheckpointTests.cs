using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ArcaneCore.World.Bots;

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
    public async Task ARevisionConflict_DropsOnlyThatBot_WithoutAFault_AndTheOthersKeepCheckpointing()
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

        // The conflicted bot belongs to the other writer: it leaves the active set at once, with no fault, and its row is left as the owner wrote it.
        Assert.False(feature.IsActive(ids[0]));
        Assert.NotEqual(ManagedPlayerbotState.Faulted, owners.Peek(ids[0])!.State);
        Assert.True(owners.Peek(ids[0])!.DesiredEnabled);

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

        // No fault recorded, one warning, no stop failure repeating every round.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("quarantined for"));
        Assert.Equal(1, log.Leveled.Count(e => e.Level == LogLevel.Warning && e.Message.Contains($"Playerbot {ids[0]}") && e.Message.Contains("managed-revision-conflict")));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("checkpoint stop failed"));
    }

    [Fact]
    public async Task AConflict_KeepsTheOtherWritersValues_AndWritesNothingOverThem()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 2);
        await CheckpointAsync(host, feature);

        owners.ChangeOutOfBand(ids[0], b => b with { DesiredEnabled = false }); // an operator disabled it by hand
        await host.World.InvokeAsync(() => { feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        long revision = owners.Peek(ids[0])!.Revision;
        for (int i = 0; i < 3; i++) await CheckpointAsync(host, feature);

        ManagedPlayerbot stored = owners.Peek(ids[0])!;
        Assert.Equal(revision, stored.Revision); // this process wrote nothing on top of the owner's row
        Assert.NotEqual(ManagedPlayerbotState.Faulted, stored.State);
        Assert.False(stored.DesiredEnabled);
        Assert.False(feature.IsActive(ids[0]));
    }

    /// <summary>
    /// The 2026-10 incident: a second world process on the same characters database was refused by the revision check at every checkpoint, each refusal
    /// counted as a fault, and three faults disabled the bot. A refusal means another writer owns the row, so the bot is never faulted by it.
    /// </summary>
    [Fact]
    public async Task ARestoreAndItsRetriesRefusedThreeTimes_KeepDesiredEnabled_AndNeverFaultTheBot()
    {
        // The incident path: the startup restore (a quarantine-style start) conflicts with the owner's rows while fault writes would still succeed.
        // The old code recorded a fault per refusal, wrote Faulted, and three faults disabled the bot.
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        Account account = await accounts.CreateAsync(new Account { Username = "PBTHREE", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord { AccountId = account.Id, Name = "Threetimes", Race = 1, Class = 1, Level = 1 });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), account.Id, character.Id, account.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);
        owners.RefuseUpdatesOf(bot.BotId, sparingFaultedWrites: true);

        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts),
            restoreOnStartup: true, logger: log, configure: o => { o.MaxFaults = 3; o.FaultBackoffSeconds = 1; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default); // refusal 1: the restore
        for (int attempt = 2; attempt <= 3; attempt++)
            Assert.Equal("managed-revision-conflict", (await feature.StartAsync(bot.BotId.ToString())).Code); // refusals 2 and 3

        ManagedPlayerbot stored = owners.Peek(bot.BotId)!;
        Assert.True(stored.DesiredEnabled);
        Assert.NotEqual(ManagedPlayerbotState.Faulted, stored.State);
        Assert.Equal(bot, stored);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("quarantined for") || e.Message.Contains("disabled after"));
        Assert.False(feature.IsActive(bot.BotId));
        Assert.False(feature.SplitBrain);
    }

    [Fact]
    public async Task ARowRefusedByTheCheckpoint_LeavesTheActiveSet_WithoutAFault()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            logger: log, manualClock: true, configure: o => { o.MaxFaults = 1; o.FaultBackoffSeconds = 1; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 1);
        await CheckpointAsync(host, feature);

        owners.RefuseUpdatesOf(ids[0], sparingFaultedWrites: true);
        owners.BumpRevision(ids[0]);
        await host.World.InvokeAsync(() => { feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.False(feature.IsActive(ids[0]));
        ManagedPlayerbot stored = owners.Peek(ids[0])!;
        Assert.True(stored.DesiredEnabled);
        Assert.NotEqual(ManagedPlayerbotState.Faulted, stored.State);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("quarantined for") || e.Message.Contains("disabled after"));
    }

    [Fact]
    public async Task OperatorEdits_OfDesiredEnabledOnly_DropTheBots_ButNeverMakeASplitBrain()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.MaxBots = 8);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        int count = ManagedPlayerbotFeature.SplitBrainConflictThreshold + 2;
        Guid[] ids = await StartBotsAsync(feature, count);
        await CheckpointAsync(host, feature);

        foreach (Guid id in ids) owners.ChangeOutOfBand(id, b => b with { DesiredEnabled = false }); // a bulk UPDATE ... SET desired_enabled = 0
        await host.World.InvokeAsync(() =>
        {
            foreach (Guid id in ids) feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            return true;
        });
        await CheckpointAsync(host, feature);

        Assert.False(feature.SplitBrain);
        foreach (Guid id in ids) Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task ASplitBrain_NamesTheOwnersRow_AndCanBeCleared()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            logger: log, manualClock: true, configure: o => o.MaxBots = 8);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, ManagedPlayerbotFeature.SplitBrainConflictThreshold + 1);
        await CheckpointAsync(host, feature);
        foreach (Guid id in ids) owners.BumpRevision(id);
        await host.World.InvokeAsync(() =>
        {
            foreach (Guid id in ids) feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            return true;
        });
        await CheckpointAsync(host, feature);

        Assert.True(feature.SplitBrain);
        // The Critical line names the last conflicting row with its real revision.
        Assert.Contains(log.Leveled, e => e.Level == LogLevel.Critical && e.Message.Contains("the one whose rows were taken over")
            && ids.Any(id => e.Message.Contains($"bot {id:D}: revision {owners.Peek(id)!.Revision},")) && e.Message.Contains("updated"));
        var cleared = await feature.ClearSplitBrainAsync();
        Assert.True(cleared.WasSet);
        Assert.Equal(ids.Length, cleared.Rows); // the stored rows are re-read and reported
        Assert.Equal(ids.Length, cleared.Enabled);
        Assert.Equal(0, cleared.Faulted);
        Assert.False(feature.SplitBrain);
        Assert.False((await feature.ClearSplitBrainAsync()).WasSet);

        // Writes resume after the clear: a bot starts again and its row is written.
        int rows = owners.RowsWritten;
        Assert.True((await feature.StartAsync(ids[0].ToString())).Success);
        Assert.True(owners.RowsWritten > rows);
        Assert.Equal(ManagedPlayerbotState.Running, owners.Peek(ids[0])!.State);
    }

    [Fact]
    public async Task AStartAndACreate_QueuedBehindTheCheckpointThatLatches_AreRefused_AndWriteNothing()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.MaxBots = 16);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, ManagedPlayerbotFeature.SplitBrainConflictThreshold + 1);
        PlayerbotOperationResult spare = await feature.CreateAsync("Spare", 1, 1); // registered, not started: the queued start's target
        Assert.True(spare.Success, spare.Code);
        await CheckpointAsync(host, feature);
        foreach (Guid id in ids) owners.BumpRevision(id);
        await host.World.InvokeAsync(() =>
        {
            foreach (Guid id in ids) feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            return true;
        });
        ManagedPlayerbot spareBefore = owners.Peek(spare.BotId!.Value)!;
        int registered = (await owners.LoadAllAsync()).Count;

        // The next checkpoint is held inside its first write, under the operation lock, before it has seen a conflict: the latch is not set yet.
        TaskCompletionSource<Guid> inWrite = owners.HoldNextUpdate();
        Task<Task> checkpointing = host.World.InvokeAsync(() => feature.LastCheckpoint);
        Task before = await checkpointing;
        for (int tick = 0; tick < 110; tick++) await host.World.AdvanceClockAsync(50);
        await inWrite.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task started = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        Assert.NotSame(before, started);
        Assert.False(feature.SplitBrain);

        // Both calls pass their pre-lock check and queue behind the checkpoint.
        Task<PlayerbotOperationResult> start = feature.StartAsync(spare.BotId.Value.ToString());
        Task<PlayerbotOperationResult> create = feature.CreateAsync("Queued", 1, 1);
        await Task.Delay(200);
        Assert.False(start.IsCompleted);
        Assert.False(create.IsCompleted);

        owners.ReleaseHeldUpdates();
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        PlayerbotOperationResult started2 = await start.WaitAsync(TimeSpan.FromSeconds(30));
        PlayerbotOperationResult created2 = await create.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(feature.SplitBrain);
        Assert.Equal("playerbot-split-brain", started2.Code);
        Assert.Equal("playerbot-split-brain", created2.Code);
        Assert.False(feature.IsActive(spare.BotId.Value));
        Assert.Equal(spareBefore, owners.Peek(spare.BotId.Value)); // no Starting/Running/Faulted write over the other world's row
        Assert.Equal(registered, (await owners.LoadAllAsync()).Count); // no row registered
    }

    [Fact]
    public async Task ADeleteAndAFleetReset_QueuedBehindTheCheckpointThatLatches_AreRefused_AndDeleteNothing()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.MaxBots = 16);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, ManagedPlayerbotFeature.SplitBrainConflictThreshold + 1);
        PlayerbotOperationResult spare = await feature.CreateAsync("Spare", 1, 1);
        Assert.True(spare.Success, spare.Code);
        await CheckpointAsync(host, feature);
        foreach (Guid id in ids) owners.BumpRevision(id);
        await host.World.InvokeAsync(() =>
        {
            foreach (Guid id in ids) feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            return true;
        });
        ManagedPlayerbot spareBefore = owners.Peek(spare.BotId!.Value)!;
        IReadOnlyList<ManagedPlayerbot> storedBefore = await owners.LoadAllAsync();

        TaskCompletionSource<Guid> inWrite = owners.HoldNextUpdate();
        Task before = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        for (int tick = 0; tick < 110; tick++) await host.World.AdvanceClockAsync(50);
        await inWrite.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task started = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        Assert.NotSame(before, started);
        Assert.False(feature.SplitBrain);

        Task<PlayerbotOperationResult> delete = feature.DeleteAsync(spare.BotId.Value.ToString());
        Task<FleetResetResult> reset = feature.ResetFleetAsync(new FleetResetPromise(storedBefore.Count, storedBefore.Count), _ => { });
        Task<PlayerbotOperationResult> stop = feature.StopAsync(spare.BotId.Value.ToString());
        await Task.Delay(200);
        Assert.False(delete.IsCompleted);
        Assert.False(reset.IsCompleted);
        Assert.False(stop.IsCompleted);

        owners.ReleaseHeldUpdates();
        await started.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(feature.SplitBrain);
        Assert.Equal("playerbot-split-brain", (await delete.WaitAsync(TimeSpan.FromSeconds(30))).Code);
        Assert.Equal("playerbot-split-brain", (await reset.WaitAsync(TimeSpan.FromSeconds(30))).Code);
        Assert.Equal("playerbot-split-brain", (await stop.WaitAsync(TimeSpan.FromSeconds(30))).Code);
        Assert.Equal(spareBefore, owners.Peek(spare.BotId.Value));
        Assert.Equal(storedBefore.Count, (await owners.LoadAllAsync()).Count); // not a row deleted
    }

    [Fact]
    public async Task AClearWhoseRereadFails_LeavesTheLatchSet_AndTheNextClearSucceeds()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.ForceSplitBrainForTests();

        owners.FailLoadAll = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => feature.ClearSplitBrainAsync());
        Assert.True(feature.SplitBrain);

        owners.FailLoadAll = false;
        Assert.True((await feature.ClearSplitBrainAsync()).WasSet);
        Assert.False(feature.SplitBrain);
    }

    [Fact]
    public async Task EnteringASplitBrain_ReleasesTheQuarantinedBots_SoNothingRetriesWhenItClears()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            configure: o => { o.FaultBackoffSeconds = 300; o.MaxFaults = 3; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Quarantined", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ManagedPlayerbotLifecycleTests.ThrowingController(1, "fixture-action-fault"))).Success);
        await WorldTestHost.WaitForAsync(() => owners.Peek(id)?.State == ManagedPlayerbotState.Faulted, "the quarantine");
        Assert.Equal(1, feature.QuarantineCount);

        await feature.ForceSplitBrainForTests();

        Assert.Equal(0, feature.QuarantineCount);
    }

    [Fact]
    public async Task ASplitBrain_RefusesTheOtherPlayerbotWrites_Delete_Homes_AndTheFleetReset()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotOperationResult spare = await feature.CreateAsync("Spare", 1, 1);
        Assert.True(spare.Success, spare.Code);
        Guid id = spare.BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        await feature.ForceSplitBrainForTests();

        Assert.Equal("playerbot-split-brain", (await feature.DeleteAsync(id.ToString())).Code);
        Assert.Equal("playerbot-split-brain", (await feature.StopAsync(id.ToString())).Code);
        Assert.Equal("playerbot-split-brain", (await feature.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { })).Code);
        InvalidOperationException home = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => feature.SetHomesAsync([new(id, 0, System.Numerics.Vector3.Zero)], 100f, null));
        Assert.Equal("playerbot-split-brain", home.Message);
        Assert.Equal(0, Assert.IsType<ManagedPlayerbotFeature.PlayerbotSplitBrainException>(home).Done);
        InvalidOperationException clear = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => feature.ClearHomesAsync([id]));
        Assert.Equal("playerbot-split-brain", clear.Message);
        Assert.Equal(before, owners.Peek(id)); // the row (and its character) survive

        await feature.ClearSplitBrainAsync();
        Assert.True((await feature.DeleteAsync(id.ToString())).Success); // writes resume
    }

    [Fact]
    public async Task EnteringASplitBrain_StopsEveryActiveBot_WithoutAManagedRowWrite()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.MaxBots = 8);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 3);
        await CheckpointAsync(host, feature);
        ManagedPlayerbot[] before = [.. ids.Select(id => owners.Peek(id)!)];
        int rows = owners.RowsWritten;

        await feature.ForceSplitBrainForTests();

        foreach (Guid id in ids) Assert.False(feature.IsActive(id)); // no longer driven
        Assert.Equal(rows, owners.RowsWritten);
        Assert.Equal(before, ids.Select(id => owners.Peek(id)!).ToArray());
        Assert.All(feature.Snapshot(), s => Assert.NotEqual(ManagedPlayerbotState.Running, s.State)); // published as stopped, not Running
    }

    [Fact]
    public async Task AStopWhoseFirstWriteConflicts_WritesNoSetAsideRows()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new RecordingSetAsideStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, setAsideStore: setAsides);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 1);
        await host.World.InvokeAsync(() => { feature.FindBrain(ids[0])!.Suspensions.SuspendQuest(1361, host.World.NowMs); return true; });
        owners.BumpRevision(ids[0]); // another writer owns the row (and so its set-aside rows)

        Assert.True((await feature.StopAsync(ids[0].ToString())).Success);

        Assert.Empty(setAsides.RowsOf(ids[0]));
    }

    [Fact]
    public async Task AStopThatSucceeds_StillSavesTheSetAsideRows_AfterTheStoppingWrite()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new RecordingSetAsideStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, setAsideStore: setAsides);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 1);
        await host.World.InvokeAsync(() => { feature.FindBrain(ids[0])!.Suspensions.SuspendQuest(1361, host.World.NowMs); return true; });

        Assert.True((await feature.StopAsync(ids[0].ToString())).Success);

        Assert.NotEmpty(setAsides.RowsOf(ids[0]));
    }

    /// <summary>Live 2026-10-18: every bot stop was a session/kick-no-reason (~300 per restart); the stop now kicks with a reason.</summary>
    [Fact]
    public async Task AStop_KicksTheBotSessionWithAReason()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 1);
        using ArcaneCore.Kernel.Diagnostics.AnomalyCapture kicks = ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Capture(
            n => n.Detail.Contains("StopCoreAsync", StringComparison.Ordinal));

        Assert.True((await feature.StopAsync(ids[0].ToString())).Success);

        Assert.False(kicks.Any("session/kick-no-reason"), kicks.Describe());
    }

    [Fact]
    public async Task CreateAndDelete_KickTemporaryBotSessionsWithAReason()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        using ArcaneCore.Kernel.Diagnostics.AnomalyCapture kicks = ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Capture(
            n => n.Detail.Contains("CreateAsync", StringComparison.Ordinal)
                || n.Detail.Contains("DeleteAsync", StringComparison.Ordinal));

        PlayerbotOperationResult created = await feature.CreateAsync("Kickreason", 1, 1);
        Assert.True(created.Success, created.Code);
        PlayerbotOperationResult deleted = await feature.DeleteAsync(created.BotId!.Value.ToString());
        Assert.True(deleted.Success, deleted.Code);

        Assert.False(kicks.Any("session/kick-no-reason"), kicks.Describe());
    }

    [Fact]
    public async Task TheSplitBrainGauge_IsOneWhileLatched_AndZeroAfterAClear()
    {
        using var store = new ArcaneCore.Kernel.Ops.Metrics.MetricsStore();
        store.Start();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true,
            extraServices: s => s.AddSingleton(new ArcaneCore.Kernel.Configuration.MetricsOptions { Enabled = true, MapSampleIntervalSeconds = 1 }));
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        double Gauge() => store.Collect().Single(m => m.Name == "arcanecore.playerbots.split_brain").Series.Single().Value;

        await WorldTestHost.WaitForAsync(() => store.Collect().Any(m => m.Name == "arcanecore.playerbots.split_brain"), "the gauge");
        Assert.Equal(0, Gauge());
        await feature.ForceSplitBrainForTests();
        Assert.Equal(1, Gauge());
        await feature.ClearSplitBrainAsync();
        Assert.Equal(0, Gauge());
    }

    [Fact]
    public async Task MoreThanAFewConflictsInOneCheckpoint_IsASplitBrain_ThatStopsEveryPlayerbotWrite()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            logger: log, manualClock: true, configure: o => o.MaxBots = 8);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, ManagedPlayerbotFeature.SplitBrainConflictThreshold + 2);
        await CheckpointAsync(host, feature);
        Guid[] foreign = [.. ids.Take(ManagedPlayerbotFeature.SplitBrainConflictThreshold + 1)];
        Guid last = ids[^1];

        foreach (Guid id in foreign) owners.BumpRevision(id);
        await host.World.InvokeAsync(() =>
        {
            foreach (Guid id in foreign) feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            return true;
        });
        await CheckpointAsync(host, feature);

        Assert.True(feature.SplitBrain);
        Assert.Contains(log.Leveled, e => e.Level == LogLevel.Critical && e.Message.Contains("Split brain"));
        foreach (Guid id in foreign) Assert.True(owners.Peek(id)!.DesiredEnabled);

        // Entering the split brain stopped every bot, the unconflicted one too, without a write, and nothing is driven any more.
        foreach (Guid id in ids) Assert.False(feature.IsActive(id));
        int rows = owners.RowsWritten;
        ManagedPlayerbot lastRow = owners.Peek(last)!;
        await CheckpointAsync(host, feature);

        // From now on this process writes nothing: not a checkpoint, a start, a create, a stop or the world's shutdown.
        Assert.Equal("playerbot-split-brain", (await feature.StartAsync(foreign[0].ToString())).Code);
        Assert.Equal("playerbot-split-brain", (await feature.CreateAsync("Latched", 1, 1)).Code);
        Assert.Equal("playerbot-split-brain", (await feature.StopAsync(last.ToString())).Code);
        await feature.ShutdownBeforeWorldStopAsync();
        Assert.Equal(rows, owners.RowsWritten);
        Assert.Equal(lastRow, owners.Peek(last));
    }

    [Fact]
    public async Task AStop_AfterAnotherWriterMovedTheRowOn_LeavesTheirRowExactlyAsTheyWroteIt()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 1);
        // The other writer moved the row on with real changes: still Running, a different goal and target, a note in the error code.
        owners.ChangeOutOfBand(ids[0], b => b with { State = ManagedPlayerbotState.Running, Goal = PlayerbotGoalKind.Quest, TargetEntry = 1234, QuestId = 77, ErrorCode = "theirs" });
        ManagedPlayerbot theirs = owners.Peek(ids[0])!;
        int rows = owners.RowsWritten;

        PlayerbotOperationResult stopped = await feature.StopAsync(ids[0].ToString());

        Assert.True(stopped.Success, stopped.Code);
        Assert.False(feature.IsActive(ids[0]));
        Assert.Equal(theirs, owners.Peek(ids[0])); // every field and the revision survive our stop
        Assert.Equal(rows, owners.RowsWritten);
    }

    [Fact]
    public async Task AStartRefusedAsStale_IsNotAFault_AndLeavesTheOwnersRowAlone()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var provisions = new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts);
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        Account owner = await accounts.CreateAsync(new Account { Username = "PBFOREIGN", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord { AccountId = owner.Id, Name = "ForeignBot", Race = 1, Class = 1, Level = 1 });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), owner.Id, character.Id, owner.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);
        owners.RefuseUpdatesOf(bot.BotId); // another world process owns this row

        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, provisions, restoreOnStartup: true, logger: log);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        Assert.False(feature.IsActive(bot.BotId));
        Assert.Equal(bot, owners.Peek(bot.BotId));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("quarantined for"));
        Assert.Equal("managed-revision-conflict", (await feature.StartAsync(bot.BotId.ToString())).Code);
    }

    [Fact]
    public async Task AConflict_OnADeletedRow_DropsTheBotOnce_AndTheOthersKeepCheckpointing()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            logger: log, manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 2);
        await CheckpointAsync(host, feature);

        owners.DropOutOfBand(ids[0]);
        await host.World.InvokeAsync(() =>
        {
            feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            feature.FindBrain(ids[1])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 12, 0);
            return true;
        });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.False(feature.IsActive(ids[0])); // gone from the active set
        Assert.Equal(12u, owners.Peek(ids[1])!.TargetEntry);
        Assert.DoesNotContain(feature.Snapshot(), s => s.BotId == ids[0]); // no ghost status line for a bot with no row
        Assert.Equal(1, log.Entries.Count(e => e.Message.Contains($"Playerbot {ids[0]} has no registry row")));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("checkpoint stop failed"));
    }

    [Fact]
    public async Task AConflict_WithAnOutOfBandFaultedRow_StillLeavesTheActiveSet()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = await StartBotsAsync(feature, 2);
        await CheckpointAsync(host, feature);

        owners.ChangeOutOfBand(ids[0], b => b with { State = ManagedPlayerbotState.Faulted, ErrorCode = "operator-fault" });
        await host.World.InvokeAsync(() =>
        {
            feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0);
            feature.FindBrain(ids[1])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 12, 0);
            return true;
        });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.False(feature.IsActive(ids[0]));
        Assert.Equal(ManagedPlayerbotState.Faulted, owners.Peek(ids[0])!.State);
        Assert.Equal("operator-fault", owners.Peek(ids[0])!.ErrorCode);
        Assert.Equal(12u, owners.Peek(ids[1])!.TargetEntry);
    }

    [Fact]
    public async Task AStopThatThrows_DoesNotKeepTheOtherBotsFromBeingCheckpointed()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            logger: log, manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotOperationResult a = await feature.CreateAsync("Stopthrow", 1, 1);
        Guid faulty = a.BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(faulty.ToString(), new ManagedPlayerbotLifecycleTests.ThrowingController(1))).Success);
        Guid[] ids = await StartBotsAsync(feature, 1);
        owners.FailUpdatesOf(faulty); // its stop's writes throw (not a conflict)
        await host.World.InvokeAsync(() => { feature.FindBrain(ids[0])!.HoldGoalForTests(PlayerbotGoalKind.Grind, 77, 0); return true; });

        try
        {
            for (int i = 0; i < 4 && owners.Peek(ids[0])!.TargetEntry != 77; i++) await CheckpointAsync(host, feature);

            Assert.Equal(77u, owners.Peek(ids[0])!.TargetEntry);
            Assert.Contains(log.Entries, e => e.Message.Contains($"Playerbot {faulty} checkpoint stop failed"));
        }
        finally { owners.FailUpdatesOf(null); }
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
