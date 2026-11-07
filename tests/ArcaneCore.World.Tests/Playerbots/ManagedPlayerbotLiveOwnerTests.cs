using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
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

        /// <summary>Characters whose probe throws from the Nth call on (1 = every call).</summary>
        public ConcurrentDictionary<int, int> ThrowFrom { get; } = new();

        /// <summary>Characters whose first N probes answer "nobody holds it" before the holder in <see cref="Holders"/> shows up (a claim taken after the check).</summary>
        public ConcurrentDictionary<int, int> FreeProbes { get; } = new();

        private readonly ConcurrentDictionary<int, int> _calls = new();

        public int CallsFor(int characterId) => _calls.GetValueOrDefault(characterId);

        public Task<string?> FindAsync(int characterId, CancellationToken cancellationToken)
        {
            int call = _calls.AddOrUpdate(characterId, 1, (_, n) => n + 1);
            if (ThrowFrom.TryGetValue(characterId, out int from) && call >= from) throw new InvalidOperationException("probe-unavailable");
            if (FreeProbes.TryGetValue(characterId, out int free) && call <= free) return Task.FromResult<string?>(null);
            return Task.FromResult(Holders.TryGetValue(characterId, out string? scope) ? scope : null);
        }
    }

    private static MapOwnership Owning(params uint[] maps) => new([.. maps.Select(m => new MapOwnership.Range(m, m))], allowInstanceMaps: false);

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
        host.World.Ownership = Owning(0, 1);
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
        Assert.Contains("started 0, created 1", status.Reason); // the remote bot was not started, a bot of its own was created instead
        Assert.True(world.Owners.RowsWritten > rows, "the bot of its own was written to the registry");
        await world.WaitForSettledAsync(1);
    }

    [Fact]
    public async Task APopulationPass_RetriesABotLeftFaultedByARefusedStart()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        world.Host.World.Ownership = Owning(0, 1);
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

    [Fact]
    public async Task CombinedMode_DoesNotRetryABotLeftFaulted_AndDropsTheBotItsRowFaultsOutOfBand()
    {
        // Combined (nothing is withheld): no other worker can have written the fault, so both rules stay exactly as they were.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        Guid faulted = (await world.Bots.CreateAsync("Faultone", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(faulted)!;
        world.Owners.FailNextUpdate = true;
        Assert.Equal("start-failed", (await world.Bots.StartAsync(faulted.ToString())).Code);
        world.Members.Seed(new PlayerbotPopulationMember(faulted, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("started 0, created 1", status.Reason);
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(faulted)!.State);
    }

    [Fact]
    public async Task CombinedMode_StillDropsABotWhoseRowCarriesAStartFailedFault()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Combinedone", 1, 1)).BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new IdleController())).Success);
        await CheckpointAsync(host, feature);

        owners.ChangeOutOfBand(id, b => b with { State = ManagedPlayerbotState.Faulted, DesiredEnabled = false, ErrorCode = "start-failed" });
        await host.World.InvokeAsync(() => { feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.False(feature.IsActive(id));
        Assert.Equal(ManagedPlayerbotState.Faulted, owners.Peek(id)!.State);
    }

    [Fact]
    public async Task ASplitWorker_KeepsABotWhoseRowDiffersOnlyByRevision()
    {
        // A refused start of another worker wrote Starting and put the row back: same content, revision two higher.
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        host.World.Ownership = Owning(0, 1);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Revisionone", 1, 1)).BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new IdleController())).Success);
        await CheckpointAsync(host, feature);

        owners.BumpRevision(id);
        owners.BumpRevision(id);
        await host.World.InvokeAsync(() => { feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.True(feature.IsActive(id));
        Assert.Equal(11u, owners.Peek(id)!.TargetEntry); // the checkpoint went through once the revision was adopted
    }

    [Fact]
    public async Task CombinedMode_StillDropsABotWhoseRowDiffersOnlyByRevision()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Revisiontwo", 1, 1)).BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new IdleController())).Success);
        await CheckpointAsync(host, feature);

        owners.BumpRevision(id);
        await host.World.InvokeAsync(() => { feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task AProbeThatFails_SkipsTheBot_WithoutAFaultOrAStartingRow()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var live = new FakeLiveOwners();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Probeone", 1, 1)).BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        live.ThrowFrom[before.CharacterId] = 1;

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.False(result.Success);
        Assert.NotEqual("start-failed", result.Code);
        Assert.Equal(before, owners.Peek(id));
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task ALoginRefusedWhileAnotherWorkerTakesTheClaim_PutsTheRowBack_AndRecordsNoFault()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var characters = new InMemoryCharacterStore();
        var live = new FakeLiveOwners();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), characters, owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        host.World.Ownership = Owning(1); // the login map (0) is not owned here: the login is refused
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Racedone", 1, 1)).BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        live.Holders[before.CharacterId] = "worker:Kalimdor";
        live.FreeProbes[before.CharacterId] = 1; // the check finds no holder; the holder claims before the login does

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.Equal("login-raced", result.Code);
        ManagedPlayerbot after = owners.Peek(id)!;
        Assert.Equal(before with { Revision = after.Revision, UpdatedUnix = after.UpdatedUnix }, after); // content as before, no Faulted, no ErrorCode
        Assert.Equal(ManagedPlayerbotState.Stopped, after.State);
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task ALoginRefused_WhenTheSecondProbeFails_PutsTheRowBack_AndRecordsNoFault()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var live = new FakeLiveOwners();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        host.World.Ownership = Owning(1);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Probetwo", 1, 1)).BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        live.ThrowFrom[before.CharacterId] = 2; // the check passes, the probe after the refused login fails

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.Equal("login-raced", result.Code);
        ManagedPlayerbot after = owners.Peek(id)!;
        Assert.Equal(ManagedPlayerbotState.Stopped, after.State);
        Assert.Null(after.ErrorCode);
    }

    [Fact]
    public async Task AStart_WhoseFaultRowCannotBeWrittenEither_ReportsStartFailed_InsteadOfThrowing()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Faultwrite", 1, 1)).BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        owners.FailUpdatesOf(id); // the Starting write throws, and so does the fault row the failure handler then writes
        try
        {
            PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

            Assert.False(result.Success);
            Assert.Equal("start-failed", result.Code);
            Assert.Equal(before, owners.Peek(id));
            Assert.False(feature.IsActive(id));
        }
        finally { owners.FailUpdatesOf(null); }
    }

    [Fact]
    public async Task AStart_WhoseFaultRowWriteFailsAfterTheStartingRowWasStored_ReportsStartFailed_AndTheRowStaysStarting()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(new FakeLiveOwners()));
        host.World.Ownership = Owning(1); // the login map (0) is not owned here: the login is refused, no worker holds the character, so the start faults
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Faultrowfail", 1, 1)).BotId!.Value;
        owners.BeforeUpdate = (bot, _) => bot.State == ManagedPlayerbotState.Faulted
            ? Task.FromException(new InvalidOperationException("fixture-fault-row-failure"))
            : Task.CompletedTask;

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.False(result.Success);
        Assert.Equal("start-failed", result.Code);
        Assert.Equal(ManagedPlayerbotState.Starting, owners.Peek(id)!.State); // the Starting row stays: the fault row was never written
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task ALoginRaced_WhoseRowPutBackFailsWithoutAConflict_StillReportsLoginRaced_InsteadOfThrowing()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var live = new FakeLiveOwners();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        host.World.Ownership = Owning(1);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Putbackfail", 1, 1)).BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        live.Holders[before.CharacterId] = "worker:Kalimdor";
        live.FreeProbes[before.CharacterId] = 1; // the check finds no holder; the holder claims before the login does
        owners.BeforeUpdate = (bot, _) => bot.State == ManagedPlayerbotState.Stopped
            ? Task.FromException(new InvalidOperationException("fixture-put-back-failure")) // only the put-back writes the Stopped row
            : Task.CompletedTask;

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.Equal("login-raced", result.Code);
        Assert.Equal(ManagedPlayerbotState.Starting, owners.Peek(id)!.State); // the put-back never landed
        Assert.Null(owners.Peek(id)!.ErrorCode); // and no fault was written on top
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task AnOperatorStart_WhoseFaultRowConflicts_ReturnsTheRevisionConflictCode()
    {
        // An operator start never counts a fault, so this pins the returned code only; the fault accounting is the restore test below.
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(new FakeLiveOwners()));
        host.World.Ownership = Owning(1);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Faultconf", 1, 1)).BotId!.Value;
        owners.BeforeUpdate = (bot, _) =>
        {
            if (bot.State == ManagedPlayerbotState.Faulted) owners.RefuseUpdatesOf(id); // another writer took the row between the Starting write and the fault row
            return Task.CompletedTask;
        };

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.False(result.Success);
        Assert.Equal("managed-revision-conflict", result.Code); // one code for both conflict paths, not start-failed
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task ARestoreStart_WhoseFaultRowConflicts_ReturnsTheRevisionConflictCode_CountsNoFault_AndReportsNoLifecycleAnomaly()
    {
        // A restore (and a population retry, StartForRetryAsync) counts a fault for a failed start. When the fault row loses to another writer the row is theirs: no count, no quarantine
        // entry (so no retry is scheduled for a row this worker does not own) and no quarantined/disabled/operation-fault anomaly.
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(new FakeLiveOwners()), configure: o => o.MaxFaults = 1); // one fault would already disable the bot (an Error anomaly)
        host.World.Ownership = Owning(1); // the login map (0) is not owned here: the login is refused and no worker holds the character, so the start faults
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Faultconfres", 1, 1)).BotId!.Value;
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subsystem == "bot-lifecycle" && n.Detail.Contains(id.ToString("D"), StringComparison.Ordinal));
        owners.BeforeUpdate = (bot, _) =>
        {
            if (bot.State == ManagedPlayerbotState.Faulted) owners.RefuseUpdatesOf(id); // another writer took the row between the Starting write and the fault row
            return Task.CompletedTask;
        };

        PlayerbotOperationResult result = await feature.StartForRetryAsync(id, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("managed-revision-conflict", result.Code);
        Assert.Equal(0, feature.QuarantineCount);
        Assert.Empty(capture.Notices); // no quarantined, disabled or operation-fault report for a row another writer owns
        Assert.False(feature.IsActive(id));
    }

    [Fact]
    public async Task ARestoreStart_WhoseFaultRowIsWritten_CountsTheFault_AndReportsItAfterTheWrite()
    {
        // Positive control for the test above: the same start with a fault row that lands is counted and reported.
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            extraServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(new FakeLiveOwners()), configure: o => o.FaultBackoffSeconds = 3600);
        host.World.Ownership = Owning(1);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Faultwrres", 1, 1)).BotId!.Value;
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subsystem == "bot-lifecycle" && n.Detail.Contains(id.ToString("D"), StringComparison.Ordinal));

        PlayerbotOperationResult result = await feature.StartForRetryAsync(id, CancellationToken.None);

        Assert.Equal("start-failed", result.Code);
        Assert.Equal(1, feature.QuarantineCount);
        Assert.Equal(1, capture.Count(PlayerbotAnomalies.Key("lifecycle", "quarantined")));
        Assert.Equal(ManagedPlayerbotState.Faulted, owners.Peek(id)!.State);
        Assert.StartsWith("quarantined (fault 1/", owners.Peek(id)!.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetryOfAStartFailedBot_IsStampedWhenMade_BacksOffByDoubling_AndStopsAfterFourAttempts()
    {
        var live = new FakeLiveOwners();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 100;
            o.Population.TargetOnline = 100; // far from the target, so every pass is an add pass whatever it creates
            o.Population.MaxCreatePerMinute = 1;
        }, manualClock: true, configureServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live)); // a manual clock: the waits are exact
        world.Host.World.Ownership = Owning(0, 1);
        world.Feature.FaultRetryBaseMs = 1000;
        world.Feature.ForeignBotMemoryMs = 0; // the foreign memory must not be what holds the retries back
        Guid poisoned = (await world.Bots.CreateAsync("Throttleone", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(poisoned)!;
        world.Owners.FailNextUpdate = true;
        Assert.Equal("start-failed", (await world.Bots.StartAsync(poisoned.ToString())).Code);
        world.Members.Seed(new PlayerbotPopulationMember(poisoned, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        world.Owners.FailUpdatesOf(poisoned); // every retry's Starting write fails: a real attempt that fails, so the bot stays Faulted and is retried again
        world.Ticks.Set(200, 10);
        int baseline = live.CallsFor(record.CharacterId);

        async Task<int> PassAfterAsync(long advanceMs)
        {
            await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += advanceMs; return true; });
            int before = live.CallsFor(record.CharacterId);
            world.Ticks.Set(200, 10);
            await world.PassAsync();
            return live.CallsFor(record.CharacterId) - before;
        }

        Assert.Equal(1, await PassAfterAsync(0));        // attempt 1, stamped when made
        Assert.Equal(0, await PassAfterAsync(0));        // the same instant: waiting
        Assert.Equal(0, await PassAfterAsync(999));      // 999 ms of the 1000
        Assert.Equal(1, await PassAfterAsync(1));        // attempt 2 at +1000
        Assert.Equal(0, await PassAfterAsync(1999));     // wait is now 2000
        Assert.Equal(1, await PassAfterAsync(1));        // attempt 3
        Assert.Equal(0, await PassAfterAsync(3999));     // wait is now 4000
        Assert.Equal(1, await PassAfterAsync(1));        // attempt 4, the last
        Assert.Equal(0, await PassAfterAsync(1_000_000)); // no fifth attempt for the life of the process
        Assert.Equal(0, await PassAfterAsync(1_000_000));
        Assert.Equal(baseline + 4, live.CallsFor(record.CharacterId));
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(poisoned)!.State);
        Assert.True(string.IsNullOrEmpty(world.Feature.Status.LastError) || !world.Feature.Status.LastError!.Contains(poisoned.ToString()), "a failed retry is not one of the pass's failures");
    }

    [Fact]
    public async Task ARetryRefusedForALiveOwner_OrAnUnknownOne_DoesNotUseUpAnAttempt()
    {
        var live = new FakeLiveOwners();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 100;
            o.Population.TargetOnline = 100;
            o.Population.MaxCreatePerMinute = 1;
        }, manualClock: true, configureServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        world.Host.World.Ownership = Owning(0, 1);
        world.Feature.FaultRetryBaseMs = 1000;
        world.Feature.ForeignBotMemoryMs = 0;
        Guid poisoned = (await world.Bots.CreateAsync("Attemptone", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(poisoned)!;
        world.Owners.FailNextUpdate = true;
        Assert.Equal("start-failed", (await world.Bots.StartAsync(poisoned.ToString())).Code);
        world.Members.Seed(new PlayerbotPopulationMember(poisoned, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        world.Ticks.Set(200, 10);

        // Eight passes in which another worker holds it (and two in which the check fails): more than the four attempts a counted refusal would allow.
        live.Holders[record.CharacterId] = "worker:Kalimdor";
        for (int i = 0; i < 8; i++) await PassAfterAsync(world, 1_000_000);
        live.Holders.TryRemove(record.CharacterId, out _);
        live.ThrowFrom[record.CharacterId] = 1;
        for (int i = 0; i < 2; i++) await PassAfterAsync(world, 1_000_000);
        live.ThrowFrom.TryRemove(record.CharacterId, out _);
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(poisoned)!.State);

        await PassAfterAsync(world, 1_000_000); // the owner is gone: the retry is still available and starts it
        await world.Host.WaitForWorldAsync(() => world.Owners.Peek(poisoned)!.State == ManagedPlayerbotState.Running, "the retried bot running");
    }

    private static async Task<(PopulationTestHost World, FakeLiveOwners Live, Guid BotId, ManagedPlayerbot Record)> RestartedFaultedBotAsync(long baseMs)
    {
        // A bot a refused start left Faulted/start-failed, then a process restart: the startup snapshot lists it Stopped, so the population sees it as startable, not as a Faulted retry.
        var live = new FakeLiveOwners();
        PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 100;
            o.Population.TargetOnline = 100;
            o.Population.MaxCreatePerMinute = 1;
            o.RestoreOnStartup = false;
        }, manualClock: true, configureServices: s => s.AddSingleton<IPlayerbotLiveOwnerProbe>(live));
        world.Host.World.Ownership = Owning(0, 1);
        world.Feature.FaultRetryBaseMs = baseMs;
        world.Feature.ForeignBotMemoryMs = 0;
        Guid poisoned = (await world.Bots.CreateAsync("Restartbot", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(poisoned)!;
        world.Owners.FailNextUpdate = true;
        Assert.Equal("start-failed", (await world.Bots.StartAsync(poisoned.ToString())).Code);
        world.Members.Seed(new PlayerbotPopulationMember(poisoned, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        await world.Bots.StartupAsync(default);
        Assert.Equal(ManagedPlayerbotState.Stopped, world.Bots.Snapshot().Single(b => b.BotId == poisoned).State);
        return (world, live, poisoned, record);
    }

    [Fact]
    public async Task AFaultedRowAfterARestart_GetsAtMostFourAttempts_EvenWhenItsFaultRowCannotBeWritten()
    {
        (PopulationTestHost world, FakeLiveOwners live, Guid poisoned, ManagedPlayerbot record) = await RestartedFaultedBotAsync(1000);
        await using PopulationTestHost owned = world;
        world.Owners.FailUpdatesOf(poisoned); // the fault write throws, so the snapshot stays Stopped and the attempt is only known to the population
        int baseline = live.CallsFor(record.CharacterId);

        for (int i = 0; i < 12; i++) await PassAfterAsync(world, 1_000_000);

        Assert.Equal(baseline + 4, live.CallsFor(record.CharacterId));
    }

    [Fact]
    public async Task AFaultedRowAfterARestart_WaitsForItsBackoff()
    {
        (PopulationTestHost world, FakeLiveOwners live, Guid poisoned, ManagedPlayerbot record) = await RestartedFaultedBotAsync(100_000_000);
        await using PopulationTestHost owned = world;
        world.Owners.FailUpdatesOf(poisoned);
        int baseline = live.CallsFor(record.CharacterId);

        for (int i = 0; i < 6; i++) await PassAfterAsync(world, 1000);

        Assert.Equal(baseline + 1, live.CallsFor(record.CharacterId));
    }

    // The feature contains a failed fault-row write (the start reports start-failed), so this proves the pass goes on through a poisoned row, not the population's retry catch:
    // that catch (a start that still throws, an unexpected fault) is covered by the tests that use BeforeRetryStartForTests below.
    [Fact]
    public async Task ARetryWhoseFaultRowCannotBeWritten_ReportsStartFailed_SoNewBotsAreStillCreated()
    {
        (PopulationTestHost world, _, Guid poisoned, _) = await RestartedFaultedBotAsync(1000);
        await using PopulationTestHost owned = world;
        world.Owners.FailUpdatesOf(poisoned);
        int before = world.Bots.Snapshot().Count;

        PopulationStatus status = await PassAndStatusAsync(world, 1_000_000);

        Assert.False(status.Reason.Contains("the pass failed"), "the pass must not fault: " + status.Reason);
        Assert.True(world.Bots.Snapshot().Count > before, "a new bot was created in the same pass; " + status.Reason);
    }

    [Fact]
    public async Task ARetryThatThrowsAnUnexpectedFault_IsContained_CountsAsAnAttempt_AndTheRestOfThePassRuns()
    {
        (PopulationTestHost world, _, Guid poisoned, _) = await RestartedFaultedBotAsync(1000);
        await using PopulationTestHost owned = world;
        int retries = 0;
        world.Feature.BeforeRetryStartForTests = _ =>
        {
            Interlocked.Increment(ref retries);
            throw new InvalidOperationException("injected-retry-fault");
        };
        int before = world.Bots.Snapshot().Count;

        PopulationStatus status = await PassAndStatusAsync(world, 1_000_000);

        Assert.Equal(1, Volatile.Read(ref retries));
        Assert.False(status.Reason.Contains("the pass failed"), "the pass must not fault: " + status.Reason);
        Assert.True(world.Bots.Snapshot().Count > before, "a new bot was created in the same pass; " + status.Reason);
        await PassAfterAsync(world, 0);
        Assert.Equal(1, Volatile.Read(ref retries)); // the contained fault used up an attempt: its wait has not passed
        await PassAfterAsync(world, 1000);
        Assert.Equal(2, Volatile.Read(ref retries)); // and it is retried once the wait did
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(poisoned)!.State);
    }

    [Fact]
    public async Task ARetryCancelledByATimeoutOfItsOwn_FaultsThePass_AndCountsAsAnAttempt()
    {
        // A cancellation that is not the caller's (the pass token is live) is not a refusal that did nothing: it ends the pass as a pass fault (RunAsync's generic catch) and the attempt stands.
        (PopulationTestHost world, _, Guid poisoned, _) = await RestartedFaultedBotAsync(1000);
        await using PopulationTestHost owned = world;
        int retries = 0;
        world.Feature.BeforeRetryStartForTests = _ =>
        {
            Interlocked.Increment(ref retries);
            throw new OperationCanceledException("injected-timeout");
        };

        PopulationStatus status = await PassAndStatusAsync(world, 1_000_000);

        Assert.Equal(1, Volatile.Read(ref retries));
        Assert.Contains("the pass failed", status.Reason, StringComparison.Ordinal);
        Assert.Contains("OperationCanceledException", status.LastError ?? string.Empty, StringComparison.Ordinal);
        await PassAfterAsync(world, 0);
        Assert.Equal(1, Volatile.Read(ref retries)); // stamped
        await PassAfterAsync(world, 1000);
        Assert.Equal(2, Volatile.Read(ref retries));
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(poisoned)!.State);
    }

    [Fact]
    public async Task ARetryRefusedByASplitBrainLatch_EndsThePassQuietly_AndUsesNoAttempt()
    {
        // The latch is set just before the retry's start, as a checkpoint latching mid-pass would: the start refuses, the pass ends with no fault of its own, and nothing was done to the bot.
        (PopulationTestHost world, _, Guid poisoned, _) = await RestartedFaultedBotAsync(1000);
        await using PopulationTestHost owned = world;
        ManagedPlayerbot before = world.Owners.Peek(poisoned)!;
        int retries = 0;
        world.Feature.BeforeRetryStartForTests = async _ =>
        {
            Interlocked.Increment(ref retries);
            await world.Bots.ForceSplitBrainForTests();
        };

        PopulationStatus status = await PassAndStatusAsync(world, 1_000_000);

        Assert.Equal(1, Volatile.Read(ref retries));
        Assert.DoesNotContain("the pass failed", status.Reason, StringComparison.Ordinal);
        Assert.Null(status.LastError);
        Assert.Equal(before, world.Owners.Peek(poisoned));
        world.Feature.BeforeRetryStartForTests = _ =>
        {
            Interlocked.Increment(ref retries);
            return Task.CompletedTask;
        };
        Assert.True((await world.Bots.ClearSplitBrainAsync()).WasSet);
        await PassAfterAsync(world, 0);
        Assert.Equal(2, Volatile.Read(ref retries)); // the refused retry used no attempt: the same instant is not a wait
    }

    private static async Task<(PopulationTestHost World, FakeLiveOwners Live, ManagedPlayerbot Before, Func<int> Retries)> RacedRetryAsync(long baseMs, long foreignMs)
    {
        // The retry's pick passes the ownership check (map 0 is owned); while its Starting row is written another worker takes the claim and this worker stops owning the login map, so the login is refused
        // and the second probe finds the holder: the raced-login path of the start, reached through a whole pass.
        // The trigger is filtered to the retried bot: the pass's pre-loop create also writes a Starting row (for a new bot) and must not spend the race.
        (PopulationTestHost world, FakeLiveOwners live, Guid poisoned, ManagedPlayerbot record) = await RestartedFaultedBotAsync(baseMs);
        world.Feature.ForeignBotMemoryMs = foreignMs;
        ManagedPlayerbot before = world.Owners.Peek(poisoned)!;
        int retries = 0;
        world.Feature.BeforeRetryStartForTests = _ =>
        {
            Interlocked.Increment(ref retries);
            return Task.CompletedTask;
        };
        bool raced = false;
        world.Owners.BeforeUpdate = (bot, _) =>
        {
            if (!raced && bot.BotId == before.BotId && bot.State == ManagedPlayerbotState.Starting)
            {
                raced = true;
                world.Host.World.Ownership = Owning(1);
                live.Holders[record.CharacterId] = "worker:Kalimdor";
            }

            return Task.CompletedTask;
        };
        return (world, live, before, () => Volatile.Read(ref retries));
    }

    [Fact]
    public async Task ARetryWhoseLoginRaced_PutsTheRowBack_AndIsStampedAsAnAttempt()
    {
        (PopulationTestHost world, FakeLiveOwners live, ManagedPlayerbot before, Func<int> retries) = await RacedRetryAsync(baseMs: 1000, foreignMs: 500);
        await using PopulationTestHost owned = world;
        int baseline = live.CallsFor(before.CharacterId);

        PopulationStatus status = await PassAndStatusAsync(world, 1_000_000);

        Assert.Equal(1, retries());
        Assert.Null(status.LastError);
        ManagedPlayerbot after = world.Owners.Peek(before.BotId)!;
        Assert.Equal(before with { Revision = after.Revision, UpdatedUnix = after.UpdatedUnix }, after); // put back as it was: still Faulted/start-failed, no fault written on top
        Assert.Equal(baseline + 2, live.CallsFor(before.CharacterId)); // the check, then the probe after the refused login
        world.Host.World.Ownership = Owning(0, 1);
        await PassAfterAsync(world, 500); // the foreign memory (500 ms) is over, the attempt's wait (1000 ms) is not
        Assert.Equal(1, retries());
        await PassAfterAsync(world, 500);
        Assert.Equal(2, retries()); // the wait is over: the next attempt
    }

    [Fact]
    public async Task ARetryWhoseLoginRaced_IsRememberedAsAnotherWorkersBot()
    {
        (PopulationTestHost world, FakeLiveOwners live, ManagedPlayerbot before, Func<int> retries) = await RacedRetryAsync(baseMs: 1000, foreignMs: 1500);
        await using PopulationTestHost owned = world;
        int baseline = live.CallsFor(before.CharacterId);

        await PassAndStatusAsync(world, 1_000_000);
        Assert.Equal(1, retries());
        Assert.Equal(baseline + 2, live.CallsFor(before.CharacterId)); // the check, then the probe after the refused login: the raced path, not a first-probe LiveOnOtherWorker
        world.Host.World.Ownership = Owning(0, 1);
        await PassAfterAsync(world, 1000); // the attempt's wait is over, the foreign memory (1500 ms) is not: the bot is skipped without a read
        Assert.Equal(1, retries());
        await PassAfterAsync(world, 500);
        Assert.Equal(2, retries());
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(before.BotId)!.State);
    }

    [Fact]
    public async Task ARetryRefusedWithoutAWrite_IsBoundedToo()
    {
        // The owner account is refused by the start (a session key is set): it writes nothing and used to be retried every pass for good.
        (PopulationTestHost world, _, Guid poisoned, ManagedPlayerbot record) = await RestartedFaultedBotAsync(1000);
        await using PopulationTestHost owned = world;
        var accounts = (InMemoryAccountStore)world.Host.WorldServices.GetRequiredService<IAccountStore>();
        Account owner = (await accounts.FindByUsernameAsync(record.AccountName))!;
        owner.SessionKey = new byte[40];

        for (int i = 0; i < 6; i++) await PassAfterAsync(world, 1_000_000);
        owner.SessionKey = null; // the owner would be accepted now
        for (int i = 0; i < 2; i++) await PassAfterAsync(world, 1_000_000);

        Assert.NotEqual(ManagedPlayerbotState.Running, world.Owners.Peek(poisoned)!.State); // the four attempts were spent on the refusals
    }

    [Theory]
    [InlineData("playerbot-capacity", true)]
    [InlineData("live-on-other-worker", true)]
    [InlineData("live-owner-unknown", true)]
    [InlineData("fleet-reset-running", true)]
    [InlineData("playerbots-stopping", true)]
    [InlineData("playerbots-disabled", true)]
    [InlineData("playerbots-detaching", true)]
    [InlineData("login-raced", false)]
    [InlineData("owner-refused", false)]
    [InlineData("start-failed", false)]
    [InlineData("retry-faulted", false)]
    public void OnlyARefusalThatDidNothingToTheBot_UsesNoAttempt(string code, bool noAttempt)
        => Assert.Equal(noAttempt, PlayerbotPopulationFeature.MadeNoStartAttempt(code));

    private static async Task<PopulationStatus> PassAndStatusAsync(PopulationTestHost world, long advanceMs)
    {
        await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += advanceMs; return true; });
        world.Ticks.Set(200, 10);
        return await world.PassAsync();
    }

    private static async Task PassAfterAsync(PopulationTestHost world, long advanceMs)
    {
        await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += advanceMs; return true; });
        world.Ticks.Set(200, 10);
        await world.PassAsync();
    }

    [Fact]
    public async Task AForeignBot_IsRememberedForTheMemoryWindow_ThenReadAgain()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 4;
            o.Population.MaxCreatePerMinute = 1; // one new bot per pass: the second pass has a free slot only for the parked bot
        });
        world.Host.World.Ownership = Owning(0);
        Guid parked = (await world.Bots.CreateAsync("Memoryone", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(parked)!;
        CharacterRecord character = Assert.IsType<CharacterRecord>(await world.Host.Characters.GetByIdAsync(record.CharacterId));
        character.MapId = 1; // saved on a map this worker does not own
        world.Members.Seed(new PlayerbotPopulationMember(parked, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        world.Ticks.Set(200, 10);

        await world.PassAsync();
        character.MapId = 0; // it is this worker's now, but the pass remembers the bot as another worker's
        world.Ticks.Set(200, 10);
        PopulationStatus second = await world.PassAsync();
        Assert.Contains("started 0", second.Reason);
        Assert.NotEqual(ManagedPlayerbotState.Running, world.Bots.Snapshot().Single(s => s.BotId == parked).State);

        await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += world.Feature.ForeignBotMemoryMs + 1; return true; });
        world.Ticks.Set(200, 10);
        PopulationStatus third = await world.PassAsync();
        Assert.Contains("started 1", third.Reason);
        await world.Host.WaitForWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == parked).State == ManagedPlayerbotState.Running, "the parked bot running");
    }

    [Fact]
    public async Task WhenTheForeignReadCapEndsTheScan_NoNewBotIsCreated_UntilTheOwnedParkedBotIsReached()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 4);
        world.Host.World.Ownership = Owning(0);
        world.Feature.MaxForeignChecksPerPass = 2;
        var members = new List<PlayerbotPopulationMember>();
        for (int i = 0; i < 3; i++)
        {
            Guid id = (await world.Bots.CreateAsync("Capbot" + (char)('a' + i), 1, 1)).BotId!.Value;
            ManagedPlayerbot record = world.Owners.Peek(id)!;
            CharacterRecord character = Assert.IsType<CharacterRecord>(await world.Host.Characters.GetByIdAsync(record.CharacterId));
            character.MapId = i < 2 ? 1u : 0u; // two foreign bots, then the owned one the cap hides
            members.Add(new PlayerbotPopulationMember(id, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 100 + i));
        }

        foreach (PlayerbotPopulationMember member in members) world.Members.Seed(member);
        world.Ticks.Set(200, 10);
        int rows = world.Owners.RowsWritten;

        await world.PassAsync();
        Assert.Equal(0, world.RunningCount);
        Assert.Equal(rows, world.Owners.RowsWritten); // nothing started and no new bot made while the owned parked bot is unread
        Assert.Equal(3, world.Bots.Snapshot().Count);

        world.Ticks.Set(200, 10);
        PopulationStatus next = await world.PassAsync(); // the two foreign bots are remembered: the scan reaches the owned one
        Assert.Contains("started 1", next.Reason);
        await world.Host.WaitForWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == members[2].BotId).State == ManagedPlayerbotState.Running, "the owned bot running");
    }

    [Fact]
    public async Task CombinedMode_AFaultedBotAfterARestart_IsStartedAsAnOperatorStart_NotRetried()
    {
        // After a restart the startup snapshot lists every registered bot as Stopped, so a Faulted row looks startable to the population. In combined mode it goes through the operator start
        // (which reports "start-failed" and does not quarantine), never through the split-only retry (a restore-kind start, which would quarantine it: "quarantined (fault 1/3)").
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            o.RestoreOnStartup = false;
        });
        Guid faulted = (await world.Bots.CreateAsync("Restartone", 1, 1)).BotId!.Value;
        ManagedPlayerbot record = world.Owners.Peek(faulted)!;
        world.Owners.FailNextUpdate = true;
        Assert.Equal("start-failed", (await world.Bots.StartAsync(faulted.ToString())).Code);
        Assert.Equal(ManagedPlayerbotState.Faulted, world.Owners.Peek(faulted)!.State);
        await world.Bots.StartupAsync(default); // the restart: every registered bot is listed Stopped
        Assert.Equal(ManagedPlayerbotState.Stopped, world.Bots.Snapshot().Single(b => b.BotId == faulted).State);
        world.Members.Seed(new PlayerbotPopulationMember(faulted, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        world.Owners.FailNextUpdate = true; // the population's start of it fails once more

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        string error = status.LastError ?? string.Empty;
        Assert.True(error.Contains($"start of {faulted} refused: start-failed"), "the operator start reports its failure as start-failed; got: " + error);
        Assert.False(error.Contains("quarantined"), "a combined start is not a quarantine retry; got: " + error);
    }

    [Fact]
    public async Task ASplitWorker_DropsABotWhoseRowDiffersInContent_EvenWhenTheRevisionAlsoMoved()
    {
        // Another writer changed more than the revision (here the bot's target): the row is not the trace of a refused start, so the bot must leave the active set, not adopt the row.
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            manualClock: true, configure: o => o.FaultBackoffSeconds = 3600);
        host.World.Ownership = Owning(0, 1);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Contentone", 1, 1)).BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new IdleController())).Success);
        await CheckpointAsync(host, feature);

        owners.ChangeOutOfBand(id, b => b with { TargetEntry = 777 });
        await host.World.InvokeAsync(() => { feature.FindBrain(id)!.HoldGoalForTests(PlayerbotGoalKind.Grind, 11, 0); return true; });
        await CheckpointAsync(host, feature);
        await CheckpointAsync(host, feature);

        Assert.False(feature.IsActive(id));
        Assert.Equal(777u, owners.Peek(id)!.TargetEntry); // the other writer's row is left as it wrote it
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
