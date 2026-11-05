using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The battleground holder wiring residue (lane Y52): the hub elects a node the gateway prefers (its list names the maps) over a node that merely owns everything, a worker with no sign
/// of life is reported to the holder, sizes are measured without a buffer and outside the hub's lock, the holder's own commands skip the hub, and Login/Logout survive a hub outage.
/// Unproven until the integration gate (written without a build, per the lane rules).
/// </summary>
public sealed class BattlegroundHolderWiringTests
{
    private static readonly ObjectGuid Someone = ObjectGuid.Player(9);

    private static BattlegroundCommand Status() => new BattlegroundStatusCommand(Someone, 1);

    private sealed record NoWireCommand : BattlegroundCommand;

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(w);
        }

        return stream.ToArray();
    }

    // ---- the election ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANodeThatListsTheMaps_TakesTheRoleFromAFallbackHolder_AndTheFallbackIsRefusedAfterwards()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5));
        Assert.True(hub.Take("owns-everything", preferred: false).IsHolder);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(hub.Take("lists-maps", preferred: true).IsHolder);

        Assert.Equal("lists-maps", hub.Holder);
        Assert.False(hub.Take("owns-everything", preferred: false).IsHolder);
        Assert.True(hub.Take("lists-maps", preferred: true).IsHolder);
    }

    [Fact]
    public void TwoNodesThatListTheMaps_FirstAskerHolds()
    {
        var hub = new BattlegroundHub(new BattlegroundHubTests.ManualClock(), TimeSpan.FromSeconds(5));

        Assert.True(hub.Take("first", preferred: true).IsHolder);
        Assert.False(hub.Take("second", preferred: true).IsHolder);
        Assert.True(hub.Take("first", preferred: true).IsHolder);
        Assert.Equal("first", hub.Holder);
    }

    [Fact]
    public void AFallbackNode_DoesNotClaimAFreeRole_WhileANodeThatListsTheMapsIsAsking_AndClaimsItOnceThatNodeIsSilent()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5));
        Assert.True(hub.Take("lists-1", preferred: true).IsHolder);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(hub.Take("lists-2", preferred: true).IsHolder);   // refused, but it has asked just now
        clock.Advance(TimeSpan.FromSeconds(2));                         // lists-1 has been silent for 6 s: its role is free

        Assert.False(hub.Take("owns-everything", preferred: false).IsHolder);
        Assert.True(hub.Take("lists-2", preferred: true).IsHolder);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(hub.Take("owns-everything", preferred: false).IsHolder);
    }

    [Fact]
    public void ANodeWithNoPreference_BehavesAsBefore_FirstAskerHoldsUntilItIsSilent()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5));

        Assert.True(hub.Take("a").IsHolder);
        Assert.False(hub.Take("b").IsHolder);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(hub.Take("b").IsHolder);
        Assert.False(hub.Take("a").IsHolder);
    }

    [Fact]
    public async Task OverTheLink_TheNodeThatListsTheMapsBecomesTheHolder_EvenWhenTheFallbackAskedFirst()
    {
        var hub = new BattlegroundHub();
        await using ClusterBattlegroundLink fallback = NewLink(new DirectHub(hub), "owns-everything", canHold: true, preferred: false);
        await using ClusterBattlegroundLink lists = NewLink(new DirectHub(hub), "lists-maps", canHold: true, preferred: true);
        Assert.False(fallback.Preferred);
        Assert.True(lists.Preferred);

        fallback.PumpOnce();
        Assert.True(fallback.IsHolder);
        lists.PumpOnce();
        fallback.PumpOnce();

        Assert.True(lists.IsHolder);
        Assert.False(fallback.IsHolder);
        Assert.Equal("lists-maps", hub.Holder);
    }

    [Fact]
    public async Task ALinkThatCannotHold_IsNeverPreferred()
    {
        await using ClusterBattlegroundLink worker = NewLink(new DirectHub(new BattlegroundHub()), "ek", canHold: false, preferred: true);

        Assert.False(worker.Preferred);
    }

    [Fact]
    public void ThePreferredFlag_TravelsInTheTakeFrame_AndAFrameWithoutItIsNotPreferred()
    {
        PresenceHolderService service = NewService();

        Assert.True(Take(service, "owns-everything", preferred: null));
        Assert.True(Take(service, "lists-maps", preferred: true));
        Assert.Equal("lists-maps", service.Battlegrounds.Holder);
        Assert.False(Take(service, "owns-everything", preferred: null));
        Assert.False(Take(service, "owns-everything", preferred: false));
    }

    private static PresenceHolderService NewService()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        return new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
    }

    private static bool Take(PresenceHolderService service, string node, bool? preferred)
    {
        byte[] request = PresenceWireCodec.EncodeRequest(PresenceOp.BattlegroundTake, w =>
        {
            w.Write(node);
            if (preferred is { } flag)
            {
                w.Write(flag);
            }
        });
        using BinaryReader r = PresenceWireCodec.OpenReply(service.Handle(request));
        return r.ReadBoolean();
    }

    // ---- a worker that went silent --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AWorkerWithNoSignOfLife_IsReportedToTheHolderOnce_AndItsMailboxIsForgotten()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        Assert.True(hub.Take("holder").IsHolder);
        hub.Drain("ek");
        Assert.True(hub.Post("holder", [new BattlegroundQueuedNotice("ek", new BattlegroundPrepareToPortNotice(Someone))]));

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty(hub.Take("holder").Commands);

        clock.Advance(TimeSpan.FromSeconds(11));
        BattlegroundQueuedCommand gone = Assert.Single(hub.Take("holder").Commands);
        Assert.Equal("ek", gone.WorkerId);
        Assert.IsType<BattlegroundWorkerGoneCommand>(gone.Command);
        Assert.Empty(hub.Take("holder").Commands);
        Assert.Empty(hub.Drain("ek"));   // the stale notice went with the worker
    }

    [Fact]
    public void AHubThatSawNoCallForTheWorkerTtl_DoesNotReportLiveWorkersGone_ButStillReportsOneThatNeverReturns()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        Assert.True(hub.Take("holder").IsHolder);
        hub.Drain("ek");
        hub.Drain("wow");

        // The hub is stalled (or the Social lease was elsewhere) for 60 s: no stamp could have been written.
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Empty(hub.Take("holder").Commands);
        hub.Drain("ek");

        // Both stamps were refreshed by the first call; "ek" keeps draining, "wow" never comes back.
        clock.Advance(TimeSpan.FromSeconds(20));
        hub.Drain("ek");
        Assert.Empty(hub.Take("holder").Commands);
        clock.Advance(TimeSpan.FromSeconds(20));
        hub.Drain("ek");
        BattlegroundQueuedCommand gone = Assert.Single(hub.Take("holder").Commands);
        Assert.Equal("wow", gone.WorkerId);
    }

    [Fact]
    public void ADrainOrASubmit_KeepsAWorkerAlive_AndTheHolderIsNeverReportedGone()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        Assert.True(hub.Take("holder").IsHolder);
        hub.Drain("holder");
        hub.Drain("ek");
        hub.Submit("wow", [Status()]);

        clock.Advance(TimeSpan.FromSeconds(20));
        hub.Drain("ek");
        clock.Advance(TimeSpan.FromSeconds(20));

        IReadOnlyList<BattlegroundQueuedCommand> taken = hub.Take("holder").Commands;

        // "wow" only submitted once, 40 s ago: gone. "ek" drained 20 s ago: alive. The holder itself is never reported.
        Assert.Contains(taken, c => c.WorkerId == "wow" && c.Command is BattlegroundWorkerGoneCommand);
        Assert.DoesNotContain(taken, c => c.WorkerId is "ek" or "holder");
    }

    [Fact]
    public void TheWorkerGoneCommand_HasTheTagOnlyWireForm()
    {
        Assert.Equal(Convert.FromHexString("06"), Bytes(w => BattlegroundWireCodec.WriteCommand(w, new BattlegroundWorkerGoneCommand())));
        Assert.IsType<BattlegroundWorkerGoneCommand>(BattlegroundWireCodec.ReadCommand(new BinaryReader(new MemoryStream([6]), Encoding.UTF8)));
    }

    // ---- sizes ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EncodedSize_IsTheLengthOfTheEncodedItem()
    {
        const string Worker = "worker with a longer name é";
        var member = new BattlegroundMember(Someone, Team.Horde, 60, InWorld: true, Deserter: false);
        BattlegroundCommand[] commands =
        [
            Status(),
            new BattlegroundLoginCommand(member),
            new BattlegroundLogoutCommand(Someone),
            new BattlegroundWorkerGoneCommand(),
            new BattlegroundJoinCommand(new BattlegroundJoinRequest(member, BattlegroundType.WarsongGulch, 0, AsGroup: true, QueuedAtPortal: false, [member, member]), null),
        ];
        foreach (BattlegroundCommand command in commands)
        {
            int expected = Bytes(w =>
            {
                w.Write(Worker);
                BattlegroundWireCodec.WriteCommand(w, command);
            }).Length;
            Assert.Equal(expected, BattlegroundWireCodec.EncodedSize(Worker, command));
        }

        BattlegroundNotice notice = new BattlegroundPrepareToPortNotice(Someone);
        Assert.Equal(
            Bytes(w =>
            {
                w.Write("ek");
                BattlegroundWireCodec.WriteNotice(w, notice);
            }).Length,
            BattlegroundWireCodec.EncodedSize("ek", notice));
    }

    [Fact]
    public void ACommandWithNoWireForm_IsCountedAndNeverQueuedAtTheHub()
    {
        var hub = new BattlegroundHub();

        hub.Submit("ek", [new NoWireCommand(), Status()]);

        Assert.Equal(1, hub.QueuedCommands);
        Assert.Equal(1, hub.Oversized);
    }

    [Fact]
    public void TheBatchLimits_AreAppliedFromTheSizesMeasuredAtSubmit()
    {
        var hub = new BattlegroundHub();
        hub.Take("h");
        int one = BattlegroundWireCodec.EncodedSize("ek", Status());
        int total = Math.Min((BattlegroundHub.MaxBatchBytes / one) + 10, BattlegroundHub.MaxQueuedCommands);
        hub.Submit("ek", [.. Enumerable.Repeat(Status(), total)]);

        BattlegroundTakeResult first = hub.Take("h");

        Assert.True(first.Commands.Count <= BattlegroundHub.MaxBatchItems);
        Assert.True(first.Commands.Sum(c => BattlegroundWireCodec.EncodedSize(c.WorkerId, c.Command)) <= BattlegroundHub.MaxBatchBytes);
        Assert.Equal(total, first.Commands.Count + hub.QueuedCommands);
    }

    // ---- the link -------------------------------------------------------------------------------------------------------------------------------

    private sealed class DirectHub(BattlegroundHub hub) : IBattlegroundHubTransport
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
            return hub.Submit(workerId, commands);
        }

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId)
        {
            Check();
            return hub.Take(nodeId);
        }

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId, bool preferred)
        {
            Check();
            return hub.Take(nodeId, preferred);
        }

        public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices)
        {
            Check();
            return hub.Post(nodeId, notices);
        }

        public IReadOnlyList<BattlegroundNotice> DrainBattlegroundNotices(string workerId)
        {
            Check();
            return hub.Drain(workerId);
        }
    }

    private static ClusterBattlegroundLink NewLink(IBattlegroundHubTransport transport, string node, bool canHold, bool preferred = false, List<(string Worker, BattlegroundCommand Command)>? sink = null)
    {
        var link = new ClusterBattlegroundLink(transport, node);
        link.Bind(
            (worker, command) =>
            {
                if (sink is not null)
                {
                    lock (sink)
                    {
                        sink.Add((worker, command));
                    }
                }
            },
            _ => { },
            canHold,
            preferred);
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
    public async Task TheHoldersOwnCommands_SkipTheHub_SoALogoutIsNotLostWhileTheHubIsUnreachable()
    {
        var hub = new BattlegroundHub();
        var transport = new DirectHub(hub);
        var sink = new List<(string Worker, BattlegroundCommand Command)>();
        await using ClusterBattlegroundLink holder = NewLink(transport, "holder", canHold: true, sink: sink);
        holder.PumpOnce();
        Assert.True(holder.IsHolder);
        transport.Down = true;

        holder.Submit("holder", new BattlegroundLogoutCommand(Someone));

        lock (sink)
        {
            (string worker, BattlegroundCommand command) = Assert.Single(sink);
            Assert.Equal("holder", worker);
            Assert.Equal(new BattlegroundLogoutCommand(Someone), command);
        }

        Assert.Equal(0, holder.CommandsDropped);
        Assert.Equal(0, holder.RetainedCommands);
        Assert.Equal(0, hub.QueuedCommands);
    }

    [Fact]
    public async Task ANodeThatIsNotTheHolder_StillGoesThroughTheHub()
    {
        var hub = new BattlegroundHub();
        var sink = new List<(string Worker, BattlegroundCommand Command)>();
        await using ClusterBattlegroundLink worker = NewLink(new DirectHub(hub), "ek", canHold: false, sink: sink);

        worker.Submit("ek", new BattlegroundLogoutCommand(Someone));

        await Eventually(() => hub.QueuedCommands == 1);
        lock (sink)
        {
            Assert.Empty(sink);
        }
    }

    [Fact]
    public async Task ALogout_SentWhileTheHubIsDown_IsKept_AndResentWhenTheHubIsBack()
    {
        var hub = new BattlegroundHub();
        var transport = new DirectHub(hub) { Down = true };
        await using ClusterBattlegroundLink worker = NewLink(transport, "ek", canHold: false);

        worker.Submit("ek", new BattlegroundLogoutCommand(Someone));
        await Eventually(() => worker.RetainedCommands == 1);
        Assert.Equal(0, hub.QueuedCommands);

        transport.Down = false;
        worker.PumpOnce();

        Assert.Equal(0, worker.RetainedCommands);
        BattlegroundQueuedCommand queued = Assert.Single(hub.Take("h").Commands);
        Assert.Equal(new BattlegroundLogoutCommand(Someone), queued.Command);
        Assert.Equal("ek", queued.WorkerId);
    }

    [Fact]
    public async Task OnlyLoginAndLogoutAreKept_AQueueCommandIsStillDropped()
    {
        var hub = new BattlegroundHub();
        var transport = new DirectHub(hub) { Down = true };
        await using ClusterBattlegroundLink worker = NewLink(transport, "ek", canHold: false);

        worker.Submit("ek", Status());
        await Eventually(() => worker.CommandsDropped == 1);

        Assert.Equal(0, worker.RetainedCommands);
    }

    [Fact]
    public async Task RetainedCommands_AreBounded_AndGoOutAheadOfNewOnes()
    {
        var hub = new BattlegroundHub();
        var transport = new DirectHub(hub) { Down = true };
        await using ClusterBattlegroundLink worker = NewLink(transport, "ek", canHold: false);
        int sent = ClusterBattlegroundLink.MaxRetained + 5;
        for (uint i = 0; i < sent; i++)
        {
            worker.Submit("ek", new BattlegroundLogoutCommand(ObjectGuid.Player(100 + i)));
        }

        await Eventually(() => worker.CommandsDropped >= sent);
        Assert.Equal(ClusterBattlegroundLink.MaxRetained, worker.RetainedCommands);

        transport.Down = false;
        worker.Submit("ek", new BattlegroundLogoutCommand(ObjectGuid.Player(1)));
        await Eventually(() => hub.QueuedCommands == ClusterBattlegroundLink.MaxRetained + 1);

        var order = new List<ObjectGuid>();
        BattlegroundTakeResult taken;
        do
        {
            taken = hub.Take("h");
            order.AddRange(taken.Commands.Select(c => ((BattlegroundLogoutCommand)c.Command).Player));
        }
        while (taken.Commands.Count >= BattlegroundHub.MaxBatchItems);

        Assert.Equal(ClusterBattlegroundLink.MaxRetained + 1, order.Count);
        Assert.Equal(ObjectGuid.Player(1), order[^1]);
        Assert.Equal(ObjectGuid.Player(105), order[0]);   // the five oldest were pushed out of the bounded list
    }

    [Fact]
    public async Task AWorkerThatWentSilent_ReachesTheHoldersQueueAsAWorkerGoneCommand()
    {
        var clock = new BattlegroundHubTests.ManualClock();
        var hub = new BattlegroundHub(clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var sink = new List<(string Worker, BattlegroundCommand Command)>();
        await using ClusterBattlegroundLink holder = NewLink(new DirectHub(hub), "holder", canHold: true, sink: sink);
        await using ClusterBattlegroundLink worker = NewLink(new DirectHub(hub), "ek", canHold: false);
        holder.PumpOnce();
        worker.PumpOnce();

        clock.Advance(TimeSpan.FromSeconds(31));
        holder.PumpOnce();

        lock (sink)
        {
            (string who, BattlegroundCommand command) = Assert.Single(sink);
            Assert.Equal("ek", who);
            Assert.IsType<BattlegroundWorkerGoneCommand>(command);
        }
    }
}
