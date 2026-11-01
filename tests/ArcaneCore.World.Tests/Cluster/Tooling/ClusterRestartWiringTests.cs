using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Ops.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// The production wiring of <c>.cluster restart</c>: <see cref="ClusterRestartHosting.AddClusterRestart"/> resolves the orchestration, which drives the real
/// <see cref="WorldHandoffCoordinator"/> (in-process, in-memory journal) and then writes the supervisor's restart request on the protocol channel (a fake transport).
/// </summary>
public sealed class ClusterRestartWiringTests
{
    private const string WorkerName = "worker:EK";
    private const int RealmId = 4;

    private static IConfiguration Configured(bool withProcesses) => new ConfigurationBuilder()
        .AddInMemoryCollection(withProcesses
            ? new Dictionary<string, string?>
            {
                ["Cluster:Supervisor:Processes:worker-EK:Name"] = WorkerName,
                ["Cluster:Supervisor:Processes:worker-EK:RealmId"] = RealmId.ToString(),
            }
            : new Dictionary<string, string?>())
        .Build();

    /// <summary>Records each line written, in order, into the shared event log and signals it.</summary>
    private sealed class LoggingWriter(List<string> events) : StringWriter
    {
        public SemaphoreSlim Written { get; } = new(0);

        public override void WriteLine(string? value)
        {
            lock (events)
            {
                events.Add("supervisor:" + value);
            }

            Written.Release();
        }
    }

    private sealed class InMemoryStore : IWorldHandoffStore
    {
        private readonly Dictionary<Guid, WorldHandoffRecord> _records = [];

        private WorldHandoffRecord Put(WorldHandoffRecord record)
        {
            lock (_records)
            {
                _records[record.HandoffId] = record;
            }

            return record;
        }

        private WorldHandoffRecord Current(Guid id)
        {
            lock (_records)
            {
                return _records[id];
            }
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

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Rerouted }));

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult(Put(Current(handoffId) with { Stage = WorldHandoffStage.Retired, DecisionState = PlayerTransferDecisionState.Completed }));

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
            => Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([]);

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public ITransferDecisionStore ForHandoff(Guid handoffId) => throw new NotSupportedException();
    }

    private sealed class Source(List<string> events, bool failFreeze = false) : IWorldHandoffSource
    {
        public WorkerNodeId WorkerId => new(WorkerName);

        public CharacterOwnershipToken Ownership { get; } = new("worker:EK", 1, Guid.NewGuid());

        private void Log(string what)
        {
            lock (events)
            {
                events.Add("source:" + what);
            }
        }

        public Task StopAdmissionsAsync(CancellationToken cancellationToken)
        {
            Log("stop-admissions");
            return Task.CompletedTask;
        }

        public Task FreezeAsync(CancellationToken cancellationToken)
        {
            Log("freeze");
            return failFreeze ? Task.FromException(new InvalidOperationException("not quiescent")) : Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            Log("flush");
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            Log("resume");
            return Task.CompletedTask;
        }

        public Task RetireAsync(CancellationToken cancellationToken)
        {
            Log("retire");
            return Task.CompletedTask;
        }
    }

    private sealed class Target : IWorldHandoffTarget
    {
        public WorkerNodeId WorkerId => new("worker:EK-standby");

        public string OwnerNode => "standby";

        public Guid Incarnation { get; } = Guid.NewGuid();

        public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Router : IWorldHandoffRouter
    {
        public Task RerouteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ServiceProvider Build(List<string> events, LoggingWriter stdout, bool withProcesses = true, bool failFreeze = false)
    {
        IConfiguration configuration = Configured(withProcesses);
        var source = new Source(events, failFreeze);
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ClusterSupervisorOptions>(configuration.GetSection(ClusterSupervisorOptions.SectionName));
        services.AddSingleton<IWorldHandoffSource>(source);
        services.AddSingleton(new WorldHandoffCoordinator(new InMemoryStore(), source, new Target(), new Router()));
        services.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(stdout), "tok", ownRealmId: 1));
        services.AddClusterRestart(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ConfiguredClusterHost_ResolvesTheOrchestration_AndRestartsAfterTheRealHandoffRetired()
    {
        var events = new List<string>();
        var stdout = new LoggingWriter(events);
        await using ServiceProvider provider = Build(events, stdout);

        ClusterRestartOrchestration? orchestration = provider.GetService<ClusterRestartOrchestration>();
        Assert.NotNull(orchestration);

        ClusterRestartResult result = await orchestration.RunAsync(AccountSecurity.Administrator, "Admiral", WorkerName);

        Assert.Equal(ClusterRestartOutcome.Restarted, result.Outcome);
        Assert.Equal(WorldHandoffStage.Retired, result.Handoff!.Stage);
        Assert.True(await stdout.Written.WaitAsync(TimeSpan.FromSeconds(10)), "the supervisor request was never written");

        string[] snapshot;
        lock (events)
        {
            snapshot = [.. events];
        }

        int retire = Array.IndexOf(snapshot, "source:retire");
        int request = Array.FindIndex(snapshot, e => e.StartsWith("supervisor:", StringComparison.Ordinal));
        Assert.True(retire >= 0 && request > retire, "the restart was requested before the handoff retired the worker: " + string.Join(" | ", snapshot));
        Assert.DoesNotContain("source:resume", snapshot);

        Assert.True(SupervisorProtocol.TryParseLifecycleRequest(snapshot[request]["supervisor:".Length..], out SupervisorLifecycleRequest line));
        Assert.Equal("tok", line.Token);
        Assert.Equal(RealmId, line.RealmId);
        Assert.Equal(SupervisorLifecycleAction.Restart, line.Action);
        Assert.Equal("Admiral", line.By);
    }

    [Fact]
    public async Task AHandoffThatAborts_WritesNoRestartRequest()
    {
        var events = new List<string>();
        var stdout = new LoggingWriter(events);
        await using ServiceProvider provider = Build(events, stdout, failFreeze: true);

        ClusterRestartResult result = await provider.GetRequiredService<ClusterRestartOrchestration>()
            .RunAsync(AccountSecurity.Administrator, "Admiral", WorkerName);

        Assert.Equal(ClusterRestartOutcome.HandoffAborted, result.Outcome);
        Assert.False(await stdout.Written.WaitAsync(TimeSpan.FromMilliseconds(300)), "an aborted handoff must not request a restart");
    }

    [Fact]
    public async Task AnotherWorkersName_IsRefusedByTheDriver_AndNothingIsRestarted()
    {
        var events = new List<string>();
        var stdout = new LoggingWriter(events);
        await using ServiceProvider provider = Build(events, stdout);

        ClusterRestartResult result = await provider.GetRequiredService<ClusterRestartOrchestration>()
            .RunAsync(AccountSecurity.Administrator, "Admiral", "worker:Kalimdor");

        Assert.Equal(ClusterRestartOutcome.HandoffFailed, result.Outcome);
        Assert.DoesNotContain("source:freeze", events);
        Assert.False(await stdout.Written.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task WorkerNameWithWhitespace_IsRejected()
    {
        var events = new List<string>();
        await using ServiceProvider provider = Build(events, new LoggingWriter(events));

        ClusterRestartResult result = await provider.GetRequiredService<ClusterRestartOrchestration>()
            .RunAsync(AccountSecurity.Administrator, "Admiral", "worker:EK\tx");

        Assert.Equal(ClusterRestartOutcome.UnknownWorker, result.Outcome);
        Assert.Empty(events);
    }

    [Fact]
    public async Task WithoutSupervisorProcesses_NothingIsRegistered()
    {
        var events = new List<string>();
        await using ServiceProvider provider = Build(events, new LoggingWriter(events), withProcesses: false);

        Assert.Null(provider.GetService<ClusterRestartOrchestration>());
    }

    [Fact]
    public void RealmIdOf_MatchesTheChildByNameOrKey_AndIsZeroForAnUnknownWorker()
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["worker-EK"] = new SupervisedProcessConfig { Name = WorkerName, RealmId = RealmId };

        Assert.Equal(RealmId, SupervisorWorkerRestartRequester.RealmIdOf(options, new WorkerNodeId(WorkerName)));
        Assert.Equal(RealmId, SupervisorWorkerRestartRequester.RealmIdOf(options, new WorkerNodeId("worker-EK")));
        Assert.Equal(0, SupervisorWorkerRestartRequester.RealmIdOf(options, new WorkerNodeId("worker:nope")));
    }
}
