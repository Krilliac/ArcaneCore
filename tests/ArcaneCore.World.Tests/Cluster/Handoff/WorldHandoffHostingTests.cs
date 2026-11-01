using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Ops.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Handoff;

/// <summary>
/// The production hosting of the whole-World handoff: <see cref="WorldHandoffHosting.AddWorldHandoff"/> (worker mode only), the coordinator provider that resolves a real
/// <see cref="WorldHandoffCoordinator"/> once a standby target and router are registered, the start-up recovery, the not-cancellable roll-forward and the "unavailable"
/// answer when no coordinator is configured. In-process fakes and an in-memory journal; no server is started.
/// </summary>
public sealed class WorldHandoffHostingTests
{
    private const string WorkerName = "worker:EK";
    private const int RealmId = 4;

    private static IConfiguration Configuration(bool workerEnabled = true, bool standby = false, bool processes = true)
    {
        var values = new Dictionary<string, string?>();
        if (workerEnabled)
        {
            values["Cluster:Worker:Enabled"] = "true";
        }

        if (standby)
        {
            values["Cluster:Worker:Standby"] = "true";
        }

        if (processes)
        {
            values["Cluster:Supervisor:Processes:worker-EK:Name"] = WorkerName;
            values["Cluster:Supervisor:Processes:worker-EK:RealmId"] = RealmId.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class Journal : IWorldHandoffStore
    {
        private readonly Dictionary<Guid, WorldHandoffRecord> _records = [];

        public void Seed(WorldHandoffRecord record)
        {
            lock (_records)
            {
                _records[record.HandoffId] = record;
            }
        }

        public WorldHandoffRecord Current(Guid id)
        {
            lock (_records)
            {
                return _records[id];
            }
        }

        private WorldHandoffRecord Put(WorldHandoffRecord record)
        {
            Seed(record);
            return record;
        }

        public Task<WorldHandoffRecord> BeginAsync(Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(new WorldHandoffRecord(handoffId, sourceWorkerId, targetWorkerId, WorldHandoffStage.Begun, PlayerTransferDecisionState.PreCommit, sourceOwnershipToken, null, null, null, 0, 0)));

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = nextStage }));

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
        {
            WorldHandoffRecord before = Current(handoffId);
            var taken = new CharacterOwnershipToken(before.SourceOwnershipToken.Scope, before.SourceOwnershipToken.Epoch + 1, targetIncarnation);
            return Task.FromResult<WorldHandoffRecord?>(Put(before with
            {
                Stage = WorldHandoffStage.EpochTakeover,
                DecisionState = PlayerTransferDecisionState.Committed,
                TargetOwnershipToken = taken,
                TargetOwnerNode = targetOwnerNode,
            }));
        }

        // The post-commit steps honour the token, so a roll-forward that is handed a cancelled token fails here.
        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Rerouted }));
        }

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Retired, DecisionState = PlayerTransferDecisionState.Completed }));
        }

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Aborted, DecisionState = PlayerTransferDecisionState.Aborted, AbortReason = reason }));

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult(_records.GetValueOrDefault(handoffId));
            }
        }

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                return Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([.. _records.Values.Where(r => !r.IsResolved)]);
            }
        }

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw new NotSupportedException();
    }

    private sealed class Events
    {
        private readonly List<string> _list = [];

        public void Add(string what)
        {
            lock (_list)
            {
                _list.Add(what);
            }
        }

        public string[] Snapshot()
        {
            lock (_list)
            {
                return [.. _list];
            }
        }
    }

    private sealed class Source(Events events, string name = WorkerName) : IWorldHandoffSource
    {
        public WorkerNodeId WorkerId => new(name);

        public CharacterOwnershipToken Ownership { get; } = new("worker:EK", 1, Guid.NewGuid());

        public Task StopAdmissionsAsync(CancellationToken cancellationToken) => Log("stop-admissions");

        public Task FreezeAsync(CancellationToken cancellationToken) => Log("freeze");

        public Task FlushAsync(CancellationToken cancellationToken) => Log("flush");

        public Task ResumeAsync(CancellationToken cancellationToken) => Log("resume");

        public Task RetireAsync(CancellationToken cancellationToken) => Log("retire");

        private Task Log(string what)
        {
            events.Add("source:" + what);
            return Task.CompletedTask;
        }
    }

    private sealed class Target(Events events, Action? onPromote = null) : IWorldHandoffTarget
    {
        public WorkerNodeId WorkerId => new("worker:EK-standby");

        public string OwnerNode => "standby";

        public Guid Incarnation { get; } = Guid.NewGuid();

        public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        {
            events.Add("target:promote");
            onPromote?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Router(Events events) : IWorldHandoffRouter
    {
        public Task RerouteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("router:reroute");
            return Task.CompletedTask;
        }
    }

    private sealed class SupervisorWriter(Events events) : StringWriter
    {
        public override void WriteLine(string? value) => events.Add("supervisor:" + value);
    }

    private static ServiceCollection Services(IConfiguration configuration, Events events, Journal journal, bool withStandby, Action? onPromote = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ClusterSupervisorOptions>(configuration.GetSection(ClusterSupervisorOptions.SectionName));
        services.AddSingleton<IWorldHandoffStore>(journal);
        services.AddSingleton<IWorldHandoffSource>(new Source(events));
        if (withStandby)
        {
            services.AddSingleton<IWorldHandoffTarget>(new Target(events, onPromote));
            services.AddSingleton<IWorldHandoffRouter>(new Router(events));
        }

        services.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(new SupervisorWriter(events)), "tok", ownRealmId: 1));
        services.AddWorldHandoff(configuration);
        services.AddClusterRestart(configuration);
        return services;
    }

    private static WorldHandoffRecord Interrupted(Journal journal, WorldHandoffStage stage, PlayerTransferDecisionState state, string worker = WorkerName)
    {
        var source = new CharacterOwnershipToken("worker:EK", 1, Guid.NewGuid());
        CharacterOwnershipToken? target = state == PlayerTransferDecisionState.Committed ? new CharacterOwnershipToken("worker:EK", 2, Guid.NewGuid()) : null;
        var record = new WorldHandoffRecord(Guid.NewGuid(), new WorkerNodeId(worker), new WorkerNodeId("worker:EK-standby"), stage, state, source, target, target is null ? null : "standby", null, 0, 0);
        journal.Seed(record);
        return record;
    }

    [Fact]
    public async Task WorkerWithAStandby_ResolvesARealCoordinator_AndTheRestartDrivesHandoffThenTheSupervisorRequest()
    {
        var events = new Events();
        var journal = new Journal();
        await using ServiceProvider provider = Services(Configuration(), events, journal, withStandby: true).BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<WorldHandoffCoordinatorProvider>().Coordinator);

        ClusterRestartResult result = await provider.GetRequiredService<ClusterRestartOrchestration>()
            .RunAsync(AccountSecurity.Administrator, "Admiral", WorkerName);

        Assert.Equal(ClusterRestartOutcome.RestartQueued, result.Outcome);
        Assert.Equal(WorldHandoffStage.Retired, result.Handoff!.Stage);
        // The supervisor channel only queues the request line; its own writer thread writes it, so it can land just after RunAsync returns.
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!events.Snapshot().Any(e => e.StartsWith("supervisor:", StringComparison.Ordinal)))
        {
            await Task.Delay(10, wait.Token);
        }

        string[] seen = events.Snapshot();
        int retire = Array.IndexOf(seen, "source:retire");
        int request = Array.FindIndex(seen, e => e.StartsWith("supervisor:", StringComparison.Ordinal));
        Assert.True(retire >= 0 && request > retire, string.Join(" | ", seen));
        Assert.Contains("target:promote", seen);
        Assert.Contains("router:reroute", seen);
    }

    [Fact]
    public async Task WorkerWithoutAStandby_HasNoCoordinator_AndTheRestartIsUnavailable()
    {
        var events = new Events();
        await using ServiceProvider provider = Services(Configuration(), events, new Journal(), withStandby: false).BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<WorldHandoffCoordinatorProvider>().Coordinator);

        ClusterRestartResult result = await provider.GetRequiredService<ClusterRestartOrchestration>()
            .RunAsync(AccountSecurity.Administrator, "Admiral", WorkerName);

        Assert.Equal(ClusterRestartOutcome.HandoffUnavailable, result.Outcome);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events.Snapshot(), e => e.StartsWith("source:", StringComparison.Ordinal) || e.StartsWith("supervisor:", StringComparison.Ordinal));
    }

    [Fact]
    public void NotWorkerMode_OrAStandbyProcess_RegistersNothing()
    {
        foreach (IConfiguration configuration in new[] { Configuration(workerEnabled: false), Configuration(standby: true) })
        {
            var services = new ServiceCollection();
            services.AddWorldHandoff(configuration);
            Assert.Empty(services);
        }
    }

    [Fact]
    public async Task Start_RollsAnInterruptedCommittedHandoffForward()
    {
        var events = new Events();
        var journal = new Journal();
        WorldHandoffRecord interrupted = Interrupted(journal, WorldHandoffStage.EpochTakeover, PlayerTransferDecisionState.Committed);
        await using ServiceProvider provider = Services(Configuration(), events, journal, withStandby: true).BuildServiceProvider();
        var recovery = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorldHandoffRecoveryService>().Single();

        await recovery.StartAsync(CancellationToken.None);
        await recovery.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorldHandoffStage.Retired, journal.Current(interrupted.HandoffId).Stage);
        string[] seen = events.Snapshot();
        Assert.Contains("target:promote", seen);
        Assert.Contains("router:reroute", seen);
        Assert.Contains("source:retire", seen);
        Assert.DoesNotContain("source:resume", seen);
        await recovery.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_AbortsAnInterruptedPreCommitHandoff_AndLeavesAnotherShardsAlone()
    {
        var events = new Events();
        var journal = new Journal();
        WorldHandoffRecord mine = Interrupted(journal, WorldHandoffStage.Frozen, PlayerTransferDecisionState.PreCommit);
        WorldHandoffRecord other = Interrupted(journal, WorldHandoffStage.Frozen, PlayerTransferDecisionState.PreCommit, worker: "worker:Kalimdor");
        await using ServiceProvider provider = Services(Configuration(), events, journal, withStandby: true).BuildServiceProvider();
        var recovery = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorldHandoffRecoveryService>().Single();

        await recovery.StartAsync(CancellationToken.None);
        await recovery.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorldHandoffStage.Aborted, journal.Current(mine.HandoffId).Stage);
        Assert.Equal(WorldHandoffStage.Frozen, journal.Current(other.HandoffId).Stage);
        Assert.Contains("source:resume", events.Snapshot());
    }

    [Fact]
    public async Task Start_WithoutACoordinator_DoesNothing()
    {
        var events = new Events();
        var journal = new Journal();
        WorldHandoffRecord interrupted = Interrupted(journal, WorldHandoffStage.EpochTakeover, PlayerTransferDecisionState.Committed);
        await using ServiceProvider provider = Services(Configuration(), events, journal, withStandby: false).BuildServiceProvider();
        var recovery = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorldHandoffRecoveryService>().Single();

        await recovery.StartAsync(CancellationToken.None);
        await recovery.RecoveryCompleted;

        Assert.Equal(WorldHandoffStage.EpochTakeover, journal.Current(interrupted.HandoffId).Stage);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task ACancellationAfterTheCommit_DoesNotLeaveAHalfHandoff()
    {
        var events = new Events();
        var journal = new Journal();
        using var cts = new CancellationTokenSource();
        // The token is cancelled while the standby is being promoted: after the epoch takeover, so the roll-forward must still finish.
        await using ServiceProvider provider = Services(Configuration(), events, journal, withStandby: true, onPromote: cts.Cancel).BuildServiceProvider();

        ClusterRestartResult result = await provider.GetRequiredService<ClusterRestartOrchestration>()
            .RunAsync(AccountSecurity.Administrator, "Admiral", WorkerName, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.NotNull(result.Handoff);
        Assert.Equal(WorldHandoffStage.Retired, result.Handoff!.Stage);
        Assert.Equal(WorldHandoffStage.Retired, journal.Current(result.Handoff.HandoffId).Stage);
        Assert.Contains("router:reroute", events.Snapshot());
        Assert.Equal(ClusterRestartOutcome.RestartQueued, result.Outcome);
        Assert.DoesNotContain("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopWaitsForAHandoffThatIsStillRunning_AndNeverCancelsIt()
    {
        var inFlight = new WorldHandoffInFlight();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = inFlight.Track(gate.Task);
        var recovery = new WorldHandoffRecoveryService(new WorldHandoffCoordinatorProvider(new ServiceCollection().BuildServiceProvider()), inFlight);

        Task stopping = recovery.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(1, inFlight.Count);

        gate.SetResult(1);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, inFlight.Count);
    }

    [Fact]
    public void WorkerNames_AreComparedOrdinallyEverywhere()
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["worker-EK"] = new SupervisedProcessConfig { Name = WorkerName, RealmId = RealmId };

        Assert.Equal(RealmId, SupervisorWorkerRestartRequester.RealmIdOf(options, new WorkerNodeId(WorkerName)));
        Assert.Equal(0, SupervisorWorkerRestartRequester.RealmIdOf(options, new WorkerNodeId("WORKER:ek")));
        Assert.False(WorkerNodeNames.Equal(new WorkerNodeId(WorkerName), new WorkerNodeId("WORKER:ek")));
        Assert.True(WorkerNodeNames.Equal(new WorkerNodeId(WorkerName), new WorkerNodeId(WorkerName)));
    }
}
