using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Tests.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Two world workers share one playerbot registry. A worker that is asked to start a bot another worker runs (its character claim is held there) must change
/// nothing: no registry row written, no fault, no anomaly. Writing the row (Starting, then Faulted) made the holder's checkpoint see a changed revision and drop its
/// healthy bot, which collapsed the population one bot at a time.
/// </summary>
public sealed class ManagedPlayerbotLiveOwnerTests
{
    /// <summary>The claim holders of the other workers, by character id.</summary>
    private sealed class FakeLiveOwners : IPlayerbotLiveOwnerProbe
    {
        public ConcurrentDictionary<int, string> Holders { get; } = new();

        public Task<string?> FindAsync(int characterId, CancellationToken cancellationToken)
            => Task.FromResult(Holders.TryGetValue(characterId, out string? scope) ? scope : null);
    }

    private sealed class IdleController : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }
        public void Detached(Guid botId) { }
    }

    [Fact]
    public async Task AStart_OfABotAnotherWorkerRuns_ChangesNoRow_AndRecordsNoFault()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var live = new FakeLiveOwners();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotOperationResult made = await feature.CreateAsync("Remoteone", 1, 1);
        Guid id = made.BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        live.Holders[before.CharacterId] = "worker:Kalimdor";
        int rows = owners.RowsWritten;

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.False(result.Success);
        Assert.Equal("live-on-other-worker", result.Code);
        Assert.Equal(before, owners.Peek(id)); // not Starting, not Faulted: the row is as the holder left it
        Assert.Equal(rows, owners.RowsWritten);
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task AStart_OfABotNoOneElseHolds_StillStarts()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(new FakeLiveOwners()));
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Localone", 1, 1)).BotId!.Value;

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.True(result.Success, result.Code);
        Assert.True(feature.IsActive(id));
    }

    [Fact]
    public async Task AStartFailedFault_WrittenByAnotherWorker_DoesNotDropTheBotThisWorkerRuns()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Ownedone", 1, 1)).BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new IdleController())).Success);
        await CheckpointAsync(host, feature);

        // The other worker's refused start wrote its fault over the row of the bot that is alive here.
        owners.ChangeOutOfBand(id, b => b with { State = ManagedPlayerbotState.Faulted, DesiredEnabled = false, ErrorCode = "start-failed" });
        await host.World.InvokeAsync(() => { feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.True(feature.IsActive(id));
        ManagedPlayerbot row = owners.Peek(id)!;
        Assert.Equal(ManagedPlayerbotState.Running, row.State);
        Assert.Null(row.ErrorCode);
        Assert.True(row.DesiredEnabled);
    }

    [Fact]
    public async Task APopulationPass_LeavesABotAnotherWorkerRuns_Alone()
    {
        var live = new FakeLiveOwners();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        Guid remote = (await world.Bots.CreateAsync("Remotetwo", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(remote)!;
        live.Holders[record.CharacterId] = "worker:Kalimdor";
        world.Members.Seed(new PlayerbotPopulationMember(remote, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        int rows = world.Owners.RowsWritten;

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Null(status.LastError);
        Assert.Equal(record, world.Owners.Peek(remote));
        Assert.NotEqual(ManagedPlayerbotState.Running, world.Bots.Snapshot().Single(s => s.BotId == remote).State);
        Assert.True(world.Owners.RowsWritten >= rows); // a bot of its own was created instead
        await world.WaitForSettledAsync(1);
    }

    [Fact]
    public async Task APopulationPass_RetriesABotLeftFaultedByARefusedStart()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        Guid poisoned = (await world.Bots.CreateAsync("Poisonone", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(poisoned)!;
        // A start that fails (its first row write throws) leaves the row as a refused start of another worker did: Faulted, start-failed, not wanted running.
        world.Owners.FailNextUpdate = true;
        Assert.Equal("start-failed", (await world.Bots.StartAsync(poisoned.ToString())).Code);
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(poisoned)!.State);
        Assert.Equal("start-failed", world.Owners.Peek(poisoned)!.ErrorCode);
        world.Members.Seed(new PlayerbotPopulationMember(poisoned, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("started 1, created 0", status.Reason);
        await world.WaitForSettledAsync(1);
        Assert.Equal(ManagedPlayerbotState.Running, world.Owners.Peek(poisoned)!.State);
    }

    private static async Task CheckpointAsync(WorldTestHost host, ManagedPlayerbotFeature feature)
    {
        Task before = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        await before.WaitAsync(TimeSpan.FromSeconds(30));
        for (int tick = 0; tick < 110; tick++) await host.World.AdvanceClockAsync(50);
        Task started = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        await started.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
