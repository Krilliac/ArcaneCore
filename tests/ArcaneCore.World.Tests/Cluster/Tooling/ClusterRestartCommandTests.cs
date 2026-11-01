using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>The Administrator-only <c>.cluster restart $worker</c>: drain by handoff (sessions kept), then ask the supervisor to restart; fakes for both seams.</summary>
[Collection("ClusterEndpointsEnv")]
public sealed class ClusterRestartCommandTests
{
    private static readonly WorkerNodeId Worker = new("worker:EK");

    private sealed class FakeDriver(Func<WorkerNodeId, WorldHandoffRecord> run, List<string> log) : IWorkerHandoffDriver
    {
        /// <summary>Sessions still on the worker. A completed handoff reroutes them to the standby; an aborted or failed one leaves them here.</summary>
        public int Sessions { get; private set; } = 3;

        /// <summary>Sessions that were moved to the standby by a completed handoff.</summary>
        public int StandbySessions { get; private set; }

        public Task<WorldHandoffRecord> HandoffAsync(WorkerNodeId worker, CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add("handoff:" + worker.Value);
            }

            WorldHandoffRecord record = run(worker);
            if (record.Stage == WorldHandoffStage.Retired)
            {
                StandbySessions += Sessions;
                Sessions = 0;
            }

            return Task.FromResult(record);
        }
    }

    private sealed class FakeRestarter(List<string> log, FakeDriver? driver = null, bool accept = true) : IWorkerRestartRequester
    {
        /// <summary>Sessions still on the worker at the moment each restart was requested.</summary>
        public List<int> SessionsAtRestart { get; } = [];

        public Task<bool> RequestRestartAsync(WorkerNodeId worker, string requestedBy, CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add("restart:" + worker.Value + ":" + requestedBy);
                SessionsAtRestart.Add(driver?.Sessions ?? -1);
            }

            return Task.FromResult(accept);
        }
    }

    private static WorldHandoffRecord Record(WorldHandoffStage stage, string? abortReason = null)
    {
        var token = new CharacterOwnershipToken("worker:EK", 1, Guid.NewGuid());
        return new WorldHandoffRecord(
            Guid.NewGuid(),
            Worker,
            new WorkerNodeId("worker:EK-standby"),
            stage,
            stage == WorldHandoffStage.Retired ? PlayerTransferDecisionState.Completed : PlayerTransferDecisionState.Aborted,
            token,
            null,
            null,
            abortReason,
            0,
            0);
    }

    private static ClusterMapPlacementTable Placement()
    {
        var table = new ClusterMapPlacementTable();
        table.RegisterWorker(Worker, isReady: true, maxPlayerCapacity: 100, activePlayerCount: 3, maxInstanceCapacity: 1, activeInstanceCount: 0);
        return table;
    }

    [Fact]
    public async Task NonAdmin_IsRefused_AndNothingRuns()
    {
        var log = new List<string>();
        var orchestration = new ClusterRestartOrchestration(new FakeDriver(_ => Record(WorldHandoffStage.Retired), log), new FakeRestarter(log), Placement());

        foreach (AccountSecurity level in new[] { AccountSecurity.Player, AccountSecurity.Moderator, AccountSecurity.GameMaster })
        {
            ClusterRestartResult result = await orchestration.RunAsync(level, "Staffer", Worker.Value);
            Assert.Equal(ClusterRestartOutcome.Refused, result.Outcome);
        }

        Assert.Empty(log);
    }

    [Fact]
    public async Task Admin_HandsOffThenRestarts_InThatOrder_AndSessionsSurvive()
    {
        var log = new List<string>();
        var driver = new FakeDriver(_ => Record(WorldHandoffStage.Retired), log);
        var restarter = new FakeRestarter(log, driver);
        var orchestration = new ClusterRestartOrchestration(driver, restarter, Placement());

        ClusterRestartResult result = await orchestration.RunAsync(AccountSecurity.Administrator, "Admiral", Worker.Value);

        Assert.Equal(ClusterRestartOutcome.Restarted, result.Outcome);
        Assert.Equal(new[] { "handoff:worker:EK", "restart:worker:EK:Admiral" }, log);
        // Every session was rerouted to the standby before the restart was requested, so the restart drops none.
        Assert.Equal(new[] { 0 }, restarter.SessionsAtRestart);
        Assert.Equal(0, driver.Sessions);
        Assert.Equal(3, driver.StandbySessions);
        Assert.Equal(WorldHandoffStage.Retired, result.Handoff!.Stage);
    }

    [Fact]
    public async Task AnAbortedHandoff_NeverRestarts_SoTheWorkerKeepsItsSessions()
    {
        var log = new List<string>();
        var driver = new FakeDriver(_ => throw new WorldHandoffAbortedException(Record(WorldHandoffStage.Aborted, "frozen refused"), null), log);
        var restarter = new FakeRestarter(log, driver);
        var orchestration = new ClusterRestartOrchestration(driver, restarter, Placement());

        ClusterRestartResult result = await orchestration.RunAsync(AccountSecurity.Administrator, "Admiral", Worker.Value);

        Assert.Equal(ClusterRestartOutcome.HandoffAborted, result.Outcome);
        Assert.Equal(new[] { "handoff:worker:EK" }, log);
        Assert.Empty(restarter.SessionsAtRestart);
        Assert.Equal(3, driver.Sessions);
        Assert.Equal(0, driver.StandbySessions);
    }

    [Fact]
    public async Task AHandoffThatThrows_NeverRestarts()
    {
        var log = new List<string>();
        var driver = new FakeDriver(_ => throw new InvalidOperationException("boom"), log);
        var orchestration = new ClusterRestartOrchestration(driver, new FakeRestarter(log), Placement());

        ClusterRestartResult result = await orchestration.RunAsync(AccountSecurity.Administrator, "Admiral", Worker.Value);

        Assert.Equal(ClusterRestartOutcome.HandoffFailed, result.Outcome);
        Assert.Equal(new[] { "handoff:worker:EK" }, log);
    }

    [Fact]
    public async Task AnUnknownWorker_IsRefusedBeforeAnyHandoff()
    {
        var log = new List<string>();
        var orchestration = new ClusterRestartOrchestration(new FakeDriver(_ => Record(WorldHandoffStage.Retired), log), new FakeRestarter(log), Placement());

        ClusterRestartResult result = await orchestration.RunAsync(AccountSecurity.Administrator, "Admiral", "worker:nope");

        Assert.Equal(ClusterRestartOutcome.UnknownWorker, result.Outcome);
        Assert.Empty(log);
    }

    [Fact]
    public async Task ASupervisorThatRefusesTheRestart_IsReported()
    {
        var log = new List<string>();
        var orchestration = new ClusterRestartOrchestration(new FakeDriver(_ => Record(WorldHandoffStage.Retired), log), new FakeRestarter(log, accept: false), Placement());

        ClusterRestartResult result = await orchestration.RunAsync(AccountSecurity.Administrator, "Admiral", Worker.Value);

        Assert.Equal(ClusterRestartOutcome.RestartNotAccepted, result.Outcome);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public async Task TheCommand_RefusesAGameMaster_AndRunsHandoffThenRestartForAnAdministrator()
    {
        var log = new List<string>();
        var driver = new FakeDriver(_ => Record(WorldHandoffStage.Retired), log);
        var restarter = new FakeRestarter(log, driver);
        var orchestration = new ClusterRestartOrchestration(driver, restarter, Placement());
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(orchestration));
        await using WorldTestClient gm = await host.EnterWorldAsync("GMR", "Gmrestart", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADR", "Admrestart", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();

        Assert.Equal("This command is not available to you.", (await LinesAsync(gm, ".cluster restart worker:EK"))[0]);
        Assert.Equal("This command is not available to you.", (await LinesAsync(gm, ".arcane cluster restart worker:EK"))[0]);
        Assert.Empty(log);

        string[] reply = await LinesAsync(admin, ".cluster restart worker:EK");
        Assert.Contains("handed off (sessions kept)", reply[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "handoff:worker:EK", "restart:worker:EK:Admrestart" }, log);
        Assert.Equal(new[] { 0 }, restarter.SessionsAtRestart);
    }

    [Fact]
    public async Task TheCommand_WithoutAConfiguredOrchestration_SaysItIsUnavailable()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADU", "Admunavail", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string[] reply = await LinesAsync(admin, ".cluster restart worker:EK");

        Assert.Contains("unavailable", reply[0], StringComparison.Ordinal);
    }
}
