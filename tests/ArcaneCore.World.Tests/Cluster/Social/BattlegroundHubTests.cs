using ArcaneCore.Game;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>The battleground mailbox at the Social holder: election of the node that runs the matches, queue bounds, and the peer-link ops over <see cref="PresenceHolderService.Handle"/>. Unproven until the integration gate (no build in the lane).</summary>
public sealed class BattlegroundHubTests
{
    private static readonly ObjectGuid Someone = ObjectGuid.Player(9);

    /// <summary>A clock in milliseconds that only moves when told.</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _ms);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
    }

    private static BattlegroundCommand Cmd(uint level = 1) => new BattlegroundStatusCommand(Someone, level);

    [Fact]
    public void FirstTaker_BecomesHolder_AndOthersAreRefusedWhileItKeepsAsking()
    {
        var clock = new ManualClock();
        var hub = new BattlegroundHub(clock);

        Assert.True(hub.Take("instances").IsHolder);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(hub.Take("other").IsHolder);
        Assert.True(hub.Take("instances").IsHolder);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(hub.Take("other").IsHolder);
        Assert.Equal("instances", hub.Holder);
    }

    [Fact]
    public void SilentHolder_IsReplacedAfterTheTimeToLive()
    {
        var clock = new ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5));
        Assert.True(hub.Take("a").IsHolder);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.True(hub.Take("b").IsHolder);
        Assert.False(hub.Take("a").IsHolder);
        Assert.Equal("b", hub.Holder);
    }

    [Fact]
    public void Submit_ReportsWhetherAHolderIsLive()
    {
        var clock = new ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5));

        Assert.False(hub.Submit("w1", [Cmd()]));
        Assert.True(hub.Take("h").IsHolder);
        Assert.True(hub.Submit("w1", [Cmd()]));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False(hub.Submit("w1", [Cmd()]));
    }

    [Fact]
    public void CommandsQueuedBeforeTheHolderExists_AreHandedOverInOrder()
    {
        var hub = new BattlegroundHub(new ManualClock());
        hub.Submit("w1", [Cmd(1), Cmd(2)]);
        hub.Submit("w2", [Cmd(3)]);

        BattlegroundTakeResult taken = hub.Take("h");

        Assert.True(taken.IsHolder);
        Assert.Equal(new[] { "w1", "w1", "w2" }, taken.Commands.Select(c => c.WorkerId));
        Assert.Equal(new uint[] { 1, 2, 3 }, taken.Commands.Select(c => ((BattlegroundStatusCommand)c.Command).Level));
        Assert.Empty(hub.Take("h").Commands);
    }

    [Fact]
    public void RefusedTaker_GetsNoCommands_AndTheHolderStillDoes()
    {
        var hub = new BattlegroundHub(new ManualClock());
        hub.Take("h");
        hub.Submit("w1", [Cmd()]);

        Assert.Empty(hub.Take("other").Commands);
        Assert.Single(hub.Take("h").Commands);
    }

    [Fact]
    public void CommandQueue_DropsTheOldestWhenFull()
    {
        var hub = new BattlegroundHub(new ManualClock());
        for (uint i = 0; i < BattlegroundHub.MaxQueuedCommands + 3; i++)
        {
            hub.Submit("w", [Cmd(i)]);
        }

        Assert.Equal(BattlegroundHub.MaxQueuedCommands, hub.QueuedCommands);
        Assert.Equal(3L, hub.DroppedCommands);
        BattlegroundTakeResult first = hub.Take("h");
        Assert.Equal(3u, ((BattlegroundStatusCommand)first.Commands[0].Command).Level);
        Assert.Equal(BattlegroundHub.MaxBatchItems, first.Commands.Count);
    }

    [Fact]
    public void Post_IsAcceptedOnlyFromTheHolder_AndDrainIsPerWorkerAndBounded()
    {
        var hub = new BattlegroundHub(new ManualClock());
        hub.Take("h");
        BattlegroundQueuedNotice ForA(uint limit) => new("a", new BattlegroundQueueLimitNotice(Someone, limit));

        Assert.False(hub.Post("intruder", [ForA(1)]));
        Assert.True(hub.Post("h", [ForA(1), ForA(2), new BattlegroundQueuedNotice("b", new BattlegroundPrepareToPortNotice(Someone))]));

        Assert.Equal(new uint[] { 1, 2 }, hub.Drain("a").Select(n => ((BattlegroundQueueLimitNotice)n).Limit));
        Assert.Empty(hub.Drain("a"));
        Assert.IsType<BattlegroundPrepareToPortNotice>(Assert.Single(hub.Drain("b")));
        Assert.Empty(hub.Drain("nobody"));
    }

    [Fact]
    public void Outbox_DropsTheOldestWhenAWorkerNeverDrains()
    {
        var hub = new BattlegroundHub(new ManualClock());
        hub.Take("h");
        for (uint i = 0; i < BattlegroundHub.MaxQueuedNoticesPerWorker + 2; i++)
        {
            hub.Post("h", [new BattlegroundQueuedNotice("a", new BattlegroundQueueLimitNotice(Someone, i))]);
        }

        Assert.Equal(2L, hub.DroppedNotices);
        Assert.Equal(2u, ((BattlegroundQueueLimitNotice)hub.Drain("a")[0]).Limit);
    }

    // ---- over the peer link's payload ---------------------------------------------------------------------------------------------------------

    private static PresenceHolderService NewService()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        return new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
    }

    [Fact]
    public void Handle_RunsTheWholeLoopThroughTheWireOps()
    {
        PresenceHolderService service = NewService();

        byte[] submit = PresenceWireCodec.EncodeRequest(PresenceOp.BattlegroundSubmit, w =>
        {
            w.Write("worker-ek");
            BattlegroundWireCodec.WriteCommands(w, [new BattlegroundQueuedCommand("worker-ek", Cmd(7))]);
        });
        using (BinaryReader r = PresenceWireCodec.OpenReply(service.Handle(submit)))
        {
            Assert.False(r.ReadBoolean());   // no holder yet
        }

        using (BinaryReader r = PresenceWireCodec.OpenReply(service.Handle(PresenceWireCodec.EncodeRequest(PresenceOp.BattlegroundTake, w => w.Write("worker-instances")))))
        {
            Assert.True(r.ReadBoolean());
            IReadOnlyList<BattlegroundQueuedCommand> commands = BattlegroundWireCodec.ReadCommands(r);
            Assert.Equal("worker-ek", Assert.Single(commands).WorkerId);
            Assert.Equal(7u, ((BattlegroundStatusCommand)commands[0].Command).Level);
        }

        byte[] post = PresenceWireCodec.EncodeRequest(PresenceOp.BattlegroundPost, w =>
        {
            w.Write("worker-instances");
            BattlegroundWireCodec.WriteNotices(w, [new BattlegroundQueuedNotice("worker-ek", new BattlegroundPrepareToPortNotice(Someone))]);
        });
        using (BinaryReader r = PresenceWireCodec.OpenReply(service.Handle(post)))
        {
            Assert.True(r.ReadBoolean());
        }

        using (BinaryReader r = PresenceWireCodec.OpenReply(service.Handle(PresenceWireCodec.EncodeRequest(PresenceOp.BattlegroundDrain, w => w.Write("worker-ek")))))
        {
            IReadOnlyList<BattlegroundQueuedNotice> notices = BattlegroundWireCodec.ReadNotices(r);
            Assert.IsType<BattlegroundPrepareToPortNotice>(Assert.Single(notices).Notice);
        }
    }

    [Fact]
    public void Handle_AMalformedBattlegroundFrame_IsAnErrorFrame_NotAThrow()
    {
        PresenceHolderService service = NewService();
        byte[] bad = [(byte)PresenceOp.BattlegroundSubmit, 0x02, (byte)'w', (byte)'k', 0xFF, 0xFF, 0xFF, 0x7F];

        byte[] reply = service.Handle(bad);

        Assert.Equal(1, reply[0]);
        Assert.Equal(0, service.Battlegrounds.QueuedCommands);
    }
}
