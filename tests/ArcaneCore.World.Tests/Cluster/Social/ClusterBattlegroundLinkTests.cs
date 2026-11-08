using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The node-side battleground link over a real <see cref="PresenceHolderService"/> hub (in process): a worker's commands reach the node that can run matches, that node's notices
/// reach the worker, only one eligible node is the holder, and a holder cut off from the hub steps down. The sender tasks are asynchronous, so tests wait for effects with a
/// bounded poll; the pump is driven by hand. Unproven until the integration gate (no build in the lane).
/// </summary>
public sealed class ClusterBattlegroundLinkTests
{
    private static readonly ObjectGuid Someone = ObjectGuid.Player(9);

    private sealed class Sink
    {
        public ConcurrentQueue<(string Worker, BattlegroundCommand Command)> Commands { get; } = new();

        public ConcurrentQueue<BattlegroundNotice> Notices { get; } = new();
    }

    /// <summary>The hub transport with a switch that makes every call fail like a dropped link.</summary>
    private sealed class FlakyHub(IBattlegroundHubTransport inner) : IBattlegroundHubTransport
    {
        public volatile bool Down;

        public bool IsAuthorityReachable => true;

        private void Check()
        {
            if (Down)
            {
                throw new IOException("link down");
            }
        }

        public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<BattlegroundCommand> commands)
        {
            Check();
            return inner.SubmitBattlegroundCommands(workerId, commands);
        }

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId)
        {
            Check();
            return inner.TakeBattlegroundCommands(nodeId);
        }

        public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices)
        {
            Check();
            return inner.PostBattlegroundNotices(nodeId, notices);
        }

        public IReadOnlyList<BattlegroundNotice> DrainBattlegroundNotices(string workerId)
        {
            Check();
            return inner.DrainBattlegroundNotices(workerId);
        }
    }

    /// <summary>The transport straight over a hub whose clock the test controls.</summary>
    private sealed class DirectHub(BattlegroundHub hub) : IBattlegroundHubTransport
    {
        public bool IsAuthorityReachable => true;

        public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<BattlegroundCommand> commands) => hub.Submit(workerId, commands);

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId) => hub.Take(nodeId);

        public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices) => hub.Post(nodeId, notices);

        public IReadOnlyList<BattlegroundNotice> DrainBattlegroundNotices(string workerId) => hub.Drain(workerId);
    }

    private sealed class Unreachable : IBattlegroundHubTransport
    {
        public bool IsAuthorityReachable => false;

        public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<BattlegroundCommand> commands) => throw new InvalidOperationException("unreachable");

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId) => throw new InvalidOperationException("unreachable");

        public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices) => throw new InvalidOperationException("unreachable");

        public IReadOnlyList<BattlegroundNotice> DrainBattlegroundNotices(string workerId) => throw new InvalidOperationException("unreachable");
    }

    private static PresenceHolderService NewHub()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        return new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
    }

    private static ClusterBattlegroundLink NewLink(IBattlegroundHubTransport hub, string node, bool canHold, Sink sink, TimeProvider? clock = null, TimeSpan? loss = null)
    {
        var link = new ClusterBattlegroundLink(hub, node, time: clock, holderLoss: loss);
        link.Bind((worker, command) => sink.Commands.Enqueue((worker, command)), sink.Notices.Enqueue, canHold);
        return link;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new Xunit.Sdk.XunitException("the condition did not hold within 10 s");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task WorkerCommand_ReachesTheHolder_AndTheHoldersNotice_ReachesTheWorker()
    {
        PresenceHolderService hub = NewHub();
        var holderSink = new Sink();
        var workerSink = new Sink();
        await using ClusterBattlegroundLink holder = NewLink(hub, "instances", canHold: true, holderSink);
        await using ClusterBattlegroundLink worker = NewLink(hub, "ek", canHold: false, workerSink);

        worker.Submit("ek", new BattlegroundLogoutCommand(Someone));
        await Eventually(() => hub.Battlegrounds.QueuedCommands == 1);

        holder.PumpOnce();
        Assert.True(holder.IsHolder);
        (string Worker, BattlegroundCommand Command) got = Assert.Single(holderSink.Commands);
        Assert.Equal("ek", got.Worker);
        Assert.Equal(new BattlegroundLogoutCommand(Someone), got.Command);

        holder.Deliver("ek", new BattlegroundPrepareToPortNotice(Someone));
        await Eventually(() =>
        {
            worker.PumpOnce();
            return !workerSink.Notices.IsEmpty;
        });
        Assert.Equal(new BattlegroundPrepareToPortNotice(Someone), Assert.Single(workerSink.Notices));
    }

    [Fact]
    public async Task OnlyOneEligibleNode_IsTheHolder()
    {
        PresenceHolderService hub = NewHub();
        await using ClusterBattlegroundLink first = NewLink(hub, "instances-1", canHold: true, new Sink());
        await using ClusterBattlegroundLink second = NewLink(hub, "instances-2", canHold: true, new Sink());

        first.PumpOnce();
        second.PumpOnce();

        Assert.True(first.IsHolder);
        Assert.False(second.IsHolder);
        Assert.Equal("instances-1", hub.Battlegrounds.Holder);
    }

    [Fact]
    public async Task WorkerThatCannotHold_NeverClaimsTheRole()
    {
        PresenceHolderService hub = NewHub();
        await using ClusterBattlegroundLink worker = NewLink(hub, "ek", canHold: false, new Sink());

        worker.PumpOnce();

        Assert.False(worker.IsHolder);
        Assert.Null(hub.Battlegrounds.Holder);
    }

    [Fact]
    public async Task NoticeFromANodeThatIsNotTheHolder_IsDropped()
    {
        PresenceHolderService hub = NewHub();
        await using ClusterBattlegroundLink worker = NewLink(hub, "ek", canHold: false, new Sink());

        worker.Deliver("other", new BattlegroundPrepareToPortNotice(Someone));

        Assert.Equal(1, worker.NoticesDropped);
        Assert.Empty(hub.Battlegrounds.Drain("other"));
    }

    [Fact]
    public async Task HolderCutOffFromTheHub_StepsDownAfterTheTimeToLive()
    {
        PresenceHolderService hub = NewHub();
        var clock = new BattlegroundHubTests.ManualClock();
        var flaky = new FlakyHub(hub);
        await using ClusterBattlegroundLink holder = NewLink(flaky, "instances", canHold: true, new Sink(), clock, TimeSpan.FromSeconds(5));
        holder.PumpOnce();
        Assert.True(holder.IsHolder);

        flaky.Down = true;
        clock.Advance(TimeSpan.FromSeconds(3));
        holder.PumpOnce();
        Assert.True(holder.IsHolder);

        clock.Advance(TimeSpan.FromSeconds(3));
        holder.PumpOnce();
        Assert.False(holder.IsHolder);
        holder.Deliver("ek", new BattlegroundPrepareToPortNotice(Someone));
        Assert.Equal(1, holder.NoticesDropped);
    }

    [Fact]
    public async Task HolderReplacedAtTheHub_StopsActingAsHolder()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5));
        await using ClusterBattlegroundLink old = NewLink(new DirectHub(hub), "old", canHold: true, new Sink());
        old.PumpOnce();
        Assert.True(old.IsHolder);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(hub.Take("new").IsHolder);
        old.PumpOnce();

        Assert.False(old.IsHolder);
    }

    [Fact]
    public async Task SendsWithNoLink_AreDroppedAndCounted_NotThrown()
    {
        await using ClusterBattlegroundLink worker = NewLink(new Unreachable(), "ek", canHold: false, new Sink());

        worker.Submit("ek", new BattlegroundLogoutCommand(Someone));

        await Eventually(() => worker.CommandsDropped == 1);
        Assert.True(worker.SendFailed >= 1);
        Assert.Equal(0, worker.PumpOnce());
    }

    [Fact]
    public async Task CommandsSubmittedWhileTheHubFails_AreDroppedAndCounted()
    {
        PresenceHolderService hub = NewHub();
        var flaky = new FlakyHub(hub) { Down = true };
        await using ClusterBattlegroundLink worker = NewLink(flaky, "ek", canHold: false, new Sink());

        worker.Submit("ek", new BattlegroundLogoutCommand(Someone));

        await Eventually(() => worker.CommandsDropped == 1);
        Assert.Equal(0, hub.Battlegrounds.QueuedCommands);
    }

    [Fact]
    public async Task Start_WithoutBind_IsRefused()
    {
        await using var link = new ClusterBattlegroundLink(NewHub(), "ek");

        Assert.Throws<InvalidOperationException>(() => link.Start(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task StartedPump_MovesTrafficWithoutManualPumping()
    {
        PresenceHolderService hub = NewHub();
        var holderSink = new Sink();
        await using ClusterBattlegroundLink holder = NewLink(hub, "instances", canHold: true, holderSink);
        await using ClusterBattlegroundLink worker = NewLink(hub, "ek", canHold: false, new Sink());
        holder.Start(TimeSpan.FromMilliseconds(10));

        worker.Submit("ek", new BattlegroundStatusCommand(Someone, 60));

        await Eventually(() => !holderSink.Commands.IsEmpty);
        Assert.Equal(new BattlegroundStatusCommand(Someone, 60), holderSink.Commands.Single().Command);
    }
}
