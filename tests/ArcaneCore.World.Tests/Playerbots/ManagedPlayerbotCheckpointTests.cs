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
