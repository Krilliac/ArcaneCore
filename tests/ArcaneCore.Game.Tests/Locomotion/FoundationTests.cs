using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Options, packet layouts, the aura ledger, the observer seam and the ack timeout tick.</summary>
public sealed class FoundationTests
{
    // --- options -------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultToTheVmangosValues()
    {
        var options = new LocomotionOptions();
        Assert.Equal(4000u, options.PendingAckResponseTimeMs); // World.cpp:985 Movement.PendingAckResponseTime
        Assert.Equal(1.0f, options.RateDamageFall);            // World.cpp:533 Rate.Damage.Fall
        Assert.Empty(options.Normalize());
    }

    [Fact]
    public void NegativeFallRate_FallsBackToOne_LikeSetConfigPos()
    {
        var options = new LocomotionOptions { RateDamageFall = -0.5f };
        Assert.Equal([nameof(LocomotionOptions.RateDamageFall)], options.Normalize());
        Assert.Equal(1.0f, options.RateDamageFall);
    }

    [Fact]
    public void Environment_IsPerWorld_AndDefaultsToRetail()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Assert.Same(LocomotionEnvironment.Default, LocomotionEnvironment.For(world));

        var custom = new LocomotionEnvironment(new LocomotionOptions { PendingAckResponseTimeMs = 100 });
        LocomotionEnvironment.Register(world, custom);
        Assert.Same(custom, LocomotionEnvironment.For(world));
        Assert.Same(LocomotionEnvironment.Default, LocomotionEnvironment.For(TestWorld.CreateRuntime()));
    }

    // --- packets -------------------------------------------------------------------------

    [Fact]
    public void FlagChange_IsPackedGuidAndCounter()
    {
        // gtker smsg_move_water_walk.wowm: PackedGuid guid, u32 counter. GUID 6 packs to mask 0x01 + 0x06.
        Assert.Equal(new byte[] { 0x01, 0x06, 0x02, 0x00, 0x00, 0x00 }, MovementChangePackets.BuildFlagChange(6, 2));
        Assert.Equal(new byte[] { 0x01, 0x06 }, MovementChangePackets.BuildEnforced(6));
    }

    [Fact]
    public void Acks_ParseGuidCounterBlockAndApply()
    {
        var info = new MovementInfo { Flags = MovementFlags.Root, Time = 500, X = 1, Y = 2, Z = 3, Orientation = 4 };

        var root = new PacketWriter(48);
        root.WriteUInt64(6);
        root.WriteUInt32(11);
        info.Write(root);
        MovementChangeAck rootAck = MovementChangePackets.ReadRootAck(root.AsSpan(), apply: true);
        Assert.Equal((6ul, 11u, true, MovementFlags.Root), (rootAck.Guid, rootAck.Counter, rootAck.Apply, rootAck.Movement.Flags));

        var flag = new PacketWriter(48);
        flag.WriteUInt64(6);
        flag.WriteUInt32(12);
        info.Write(flag);
        flag.WriteUInt32(1);
        MovementChangeAck flagAck = MovementChangePackets.ReadFlagAck(flag.AsSpan());
        Assert.Equal((6ul, 12u, true), (flagAck.Guid, flagAck.Counter, flagAck.Apply));
        Assert.Equal(3f, flagAck.Movement.Z);
    }

    [Fact]
    public void ObserverRelay_IsPackedGuidThenTheMovementBlock()
    {
        var info = new MovementInfo { Flags = MovementFlags.WaterWalking, Time = 9, X = 1, Y = 2, Z = 3, Orientation = 4 };
        byte[] relay = MovementChangePackets.BuildObserverRelay(6, info);

        var reader = new PacketReader(relay);
        Assert.Equal(6ul, reader.ReadPackedGuid());
        MovementInfo read = MovementInfo.Read(ref reader);
        Assert.Equal((MovementFlags.WaterWalking, 9u, 3f), (read.Flags, read.Time, read.Z));
        Assert.Equal(0, reader.Remaining);
    }

    // --- aura ledger ---------------------------------------------------------------------

    [Fact]
    public void AuraLedger_AnswersTheVmangosAuraQueries()
    {
        var ledger = new AuraLedger();
        var a = new SpellAura(0, AuraType.SafeFall, 17, 0, 0);
        var b = new SpellAura(0, AuraType.SafeFall, 5, 0, 0);
        var slow = new SpellAura(0, AuraType.ModDecreaseSpeed, -30, 0, 0);

        Assert.False(ledger.Has(AuraType.SafeFall));
        ledger.Add(a);
        ledger.Add(a); // the same instance is not counted twice
        ledger.Add(b);
        ledger.Add(slow);

        Assert.True(ledger.Has(AuraType.SafeFall));
        Assert.Equal(22, ledger.Total(AuraType.SafeFall));
        Assert.Equal(17, ledger.MaxPositive(AuraType.SafeFall));
        Assert.Equal(-30, ledger.MaxNegative(AuraType.ModDecreaseSpeed));
        Assert.Equal(0, ledger.MaxNegative(AuraType.SafeFall));

        ledger.Remove(a);
        Assert.Equal(5, ledger.Total(AuraType.SafeFall));
        ledger.Remove(b);
        Assert.False(ledger.Has(AuraType.SafeFall));
    }

    // --- observer seam -------------------------------------------------------------------

    private sealed class Recording(List<string> log, string name, bool throws = false, bool correct = false) : IClientMovementObserver
    {
        public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
        {
            log.Add($"{name}:before prev={previous.Z} in={incoming.Z}");
            if (correct)
            {
                incoming.Z = 99;
            }

            if (throws)
            {
                throw new InvalidOperationException("boom");
            }
        }

        public void AfterApply(MovementObserverContext context, in MovementInfo previous) => log.Add($"{name}:after prev={previous.Z}");
    }

    [Fact]
    public void Observers_SeeThePreviousBlock_MayCorrectTheIncomingOne_AndAFailureDoesNotStopTheRest()
    {
        var log = new List<string>();
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        WorldRuntime world = TestWorld.CreateRuntime();
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        var previous = new MovementInfo { Z = 5 };
        var incoming = new MovementInfo { Z = 6 };
        IClientMovementObserver[] observers = [new Recording(log, "a", throws: true), new Recording(log, "b", correct: true)];

        MovementObservers.Before(observers, context, in previous, ref incoming);
        MovementObservers.After(observers, context, in previous);

        Assert.Equal(["a:before prev=5 in=6", "b:before prev=5 in=6", "a:after prev=5", "b:after prev=5"], log);
        Assert.Equal(99f, incoming.Z);
    }

    [Fact]
    public void DiscoveredObservers_AreAllMarked_AndRunInAscendingOrder()
    {
        int[] orders = [.. MovementObservers.Types.Select(t => ((MovementObserverAttribute)Attribute.GetCustomAttribute(t, typeof(MovementObserverAttribute))!).Order)];
        Assert.Equal(orders.OrderBy(o => o), orders);
    }

    [Fact]
    public void RegisteredObservers_RunAmongTheDiscoveredOnesByOrder()
    {
        var log = new List<string>();
        WorldRuntime world = TestWorld.CreateRuntime();
        MovementObservers.Register(world, new Recording(log, "late"), order: 100);
        MovementObservers.Register(world, new Recording(log, "early"), order: int.MinValue);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        var previous = new MovementInfo { Z = 1 };
        var incoming = new MovementInfo { Z = 2 };

        MovementObservers.Before(new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat), in previous, ref incoming);

        string[] mine = [.. log.Where(l => l.StartsWith("early") || l.StartsWith("late"))];
        Assert.Equal(["early:before prev=1 in=2", "late:before prev=1 in=2"], mine);
    }
    // --- ack timeout tick ----------------------------------------------------------------

    [Fact]
    public void UnackedRoot_IsEnforcedAfterTheAckTimeout_AndEveryoneIsTold()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        var session = new FakeSession();
        var watcherSession = new FakeSession();
        Player player = CombatTestKit.AddPlayer(world, 1, 100, 100, session);
        Player watcher = CombatTestKit.AddPlayer(world, 2, 101, 100, watcherSession);
        world.RunTick(50); // make the two players see each other
        session.Clear();
        watcherSession.Clear();

        player.SetRooted(true);
        Assert.Equal(WorldOpcode.SmsgForceMoveRoot, session.Next().Opcode);
        Assert.True(player.Locomotion.Pending.HasPending);
        Assert.False(player.Movement.HasFlag(MovementFlags.Root)); // the order alone does not set the flag

        world.RunTick(3900);
        Assert.False(player.Movement.HasFlag(MovementFlags.Root));
        world.RunTick(101); // 4001 ms in total

        Assert.True(player.Movement.HasFlag(MovementFlags.Root));
        Assert.False(player.Locomotion.Pending.HasPending);
        Assert.Equal(1, player.Locomotion.FailedAckCount);
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgSplineMoveRoot);
        Assert.Contains(watcherSession.Sent, p => p.Opcode == WorldOpcode.SmsgSplineMoveRoot);
        Assert.NotNull(watcher);
    }

    [Fact]
    public void AnAcknowledgedRoot_IsNotEnforcedAgain()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        var session = new FakeSession();
        Player player = CombatTestKit.AddPlayer(world, 1, 100, 100, session);

        player.SetRooted(true);
        session.Clear();
        Assert.True(MovementControl.Acknowledge(player, MovementChangeType.Root, 0, apply: true));
        world.RunTick(5000);

        Assert.Equal(0, player.Locomotion.FailedAckCount);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgSplineMoveRoot);
    }

    [Fact]
    public void WrongAck_IsCounted_AndChangesNothing()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 1, 100, 100, new FakeSession());
        player.SetRooted(true);

        Assert.False(MovementControl.Acknowledge(player, MovementChangeType.Root, 99, apply: true));
        Assert.False(MovementControl.Acknowledge(player, MovementChangeType.Root, 0, apply: false));
        Assert.Equal(2, player.Locomotion.WrongAckCount);
        Assert.True(player.Locomotion.Pending.HasPending);
    }

    [Fact]
    public void LeavingTheMap_AppliesTheLatestPendingChangesSilently()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        var session = new FakeSession();
        Player player = CombatTestKit.AddPlayer(world, 1, 100, 100, session);
        player.SetRooted(true);
        session.Clear();

        world.GetMap(0).RemovePlayer(player);

        Assert.True(player.Movement.HasFlag(MovementFlags.Root));
        Assert.Empty(session.Sent); // sendToClient is false: the client is gone from this map
    }

    [Fact]
    public void RelocationKeepsTheServerOwnedFlags()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        player.AddMovementFlags(MovementFlags.Root | MovementFlags.Forward | MovementFlags.WaterWalking);
        player.Relocate(5, 6, 7, 0, 100);

        Assert.Equal(MovementFlags.Root | MovementFlags.WaterWalking, player.Movement.Flags);
        player.RemoveMovementFlags(MovementFlags.Root);
        Assert.Equal(MovementFlags.WaterWalking, player.Movement.Flags);
    }
}
