using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The GM control of one lift's schedule (<c>.debug lift</c>): the default schedule is unchanged, and a phase, a freeze, a speed and a reset
/// move the progress every reader sees (<see cref="GameObjectMapSystem.ElevatorPosition"/>, <see cref="GameObjectMapSystem.ElevatorProgressAhead"/>,
/// the create block) and resend the lift to the players who have it.
/// </summary>
public sealed class ElevatorControlTests
{
    private const uint Elevator = 4170;
    private const uint Cycle = 10000;

    // Rests 0-3000 at the bottom, rises to 20 by 5000, rests 5000-8000 at the top, sinks back by 10000.
    private static readonly TransportAnimationCatalog Animation = new(
    [
        (Elevator, new TransportAnimationNode(0, 0, 0, 0)),
        (Elevator, new TransportAnimationNode(3000, 0, 0, 0)),
        (Elevator, new TransportAnimationNode(5000, 0, 0, 20)),
        (Elevator, new TransportAnimationNode(8000, 0, 0, 20)),
        (Elevator, new TransportAnimationNode(Cycle, 0, 0, 0)),
    ]);

    private static (WorldRuntime World, GameObjectMapSystem System, GameObject Lift, Player Player, FakeSession Session) Start(bool animated = true)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, new GameObjectContent(
            [GoTemplate(Elevator, GameObjectType.Transport, (0, 3000))], [GoSpawn(1, Elevator, 3, 0)], [], [], []))
        {
            ElevatorAnimations = animated ? Animation : TransportAnimationCatalog.Empty,
        };
        map.AddUpdater(system);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        GameObject lift = system.GameObjects.Single(g => g.Entry == Elevator);
        Assert.Contains(lift.Guid, player.VisibleObjects);
        session.Clear();
        return (world, system, lift, player, session);
    }

    private static uint DefaultProgress(GameObjectMapSystem system, GameObject lift) => (uint)((system.ClockMs - lift.CreatedAtMs) % Cycle);

    private static void Run(WorldRuntime world, int ticks, uint diffMs = 100)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunTick(diffMs);
        }
    }

    [Fact]
    public void WithoutAnOverride_TheProgressIsTheTimeSinceCreation()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start();
        using (world)
        {
            Run(world, 37, 130);
            Assert.False(system.GetElevatorControl(lift).Overridden);
            Assert.Equal(DefaultProgress(system, lift), lift.PathProgress);
            Assert.Equal(DefaultProgress(system, lift), system.ElevatorProgressAhead(lift, 0));
            Assert.Equal((uint)((system.ClockMs - lift.CreatedAtMs + 2500) % Cycle), system.ElevatorProgressAhead(lift, 2500));
        }
    }

    [Fact]
    public void SetPhase_MovesThePositionToThatPointOfTheCycle_AndRunsOnFromThere()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start();
        using (world)
        {
            Run(world, 5);
            Assert.True(system.SetElevatorPhase(lift, 4000));
            Assert.Equal(4000u, lift.PathProgress);
            Assert.Equal(lift.Z + 10f, system.ElevatorPosition(lift)!.Value.Z, 0.01f);   // halfway up the 3000-5000 rise

            Run(world, 5);
            Assert.Equal(4500u, lift.PathProgress);
            Assert.Equal(4500u + 700, system.ElevatorProgressAhead(lift, 700));

            Assert.True(system.SetElevatorPhase(lift, Cycle + 1234));                    // taken modulo the cycle
            Assert.Equal(1234u, lift.PathProgress);
        }
    }

    [Fact]
    public void Freeze_HoldsTheProgress_AndResumeContinuesWithoutAJump()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, FakeSession session) = Start();
        using (world)
        {
            Assert.True(system.SetElevatorPhase(lift, 2000));
            Run(world, 3);
            uint held = lift.PathProgress;
            Assert.Equal(2300u, held);
            session.Clear();

            Assert.True(system.FreezeElevator(lift));
            Assert.True(system.GetElevatorControl(lift).Frozen);
            Assert.Empty(session.Sent);                                                   // a freeze resends nothing
            Run(world, 20);
            Assert.Equal(held, lift.PathProgress);
            Assert.Equal(held, system.ElevatorProgressAhead(lift, 5000));
            Assert.Equal(held, system.ElevatorProgressAhead(lift, 0));

            Assert.True(system.ResumeElevator(lift));
            Assert.Equal(held, lift.PathProgress);                                        // no jump
            Run(world, 4);
            Assert.Equal(held + 400, lift.PathProgress);
            Assert.False(system.GetElevatorControl(lift).Frozen);
        }
    }

    [Fact]
    public void Speed_TwoAdvancesTwiceAsFast_AndTheFactorIsClamped()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start();
        using (world)
        {
            Assert.True(system.SetElevatorPhase(lift, 1000));
            Assert.True(system.SetElevatorScale(lift, 2.0));
            Run(world, 10);                                                               // 1000 ms
            Assert.Equal(3000u, lift.PathProgress);
            Assert.Equal(3600u, system.ElevatorProgressAhead(lift, 300));

            Assert.True(system.SetElevatorScale(lift, 1000));
            Assert.Equal(10.0, system.GetElevatorControl(lift).Scale);
            Assert.True(system.SetElevatorScale(lift, 0.001));
            Assert.Equal(0.1, system.GetElevatorControl(lift).Scale);
            Assert.False(system.SetElevatorScale(lift, double.NaN));
        }
    }

    [Fact]
    public void ASpeedChange_DoesNotJumpTheProgress_AndAFrozenLiftKeepsItsFreeze()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start();
        using (world)
        {
            Assert.True(system.SetElevatorPhase(lift, 500));
            Run(world, 10);
            Assert.Equal(1500u, lift.PathProgress);
            Assert.True(system.SetElevatorScale(lift, 3.0));
            Assert.Equal(1500u, lift.PathProgress);

            Assert.True(system.FreezeElevator(lift));
            Assert.True(system.SetElevatorScale(lift, 0.5));
            ElevatorControlInfo frozen = system.GetElevatorControl(lift);
            Assert.True(frozen.Frozen);
            Assert.Equal(0.0, frozen.Scale);
            Assert.Equal(0.5, frozen.ResumeScale);
            Run(world, 5);
            Assert.Equal(1500u, lift.PathProgress);
            Assert.True(system.ResumeElevator(lift));
            Run(world, 10);
            Assert.Equal(2000u, lift.PathProgress);                                       // 1000 ms at 0.5
        }
    }

    [Fact]
    public void Reset_ReturnsToTheDefaultSchedule()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start();
        using (world)
        {
            Run(world, 12);
            Assert.True(system.SetElevatorPhase(lift, 9000));
            Assert.True(system.SetElevatorScale(lift, 4.0));
            Assert.True(system.FreezeElevator(lift));
            Assert.NotEqual(DefaultProgress(system, lift), lift.PathProgress);

            Assert.True(system.ResetElevator(lift));
            Assert.False(system.GetElevatorControl(lift).Overridden);
            Assert.Equal(DefaultProgress(system, lift), lift.PathProgress);
            Run(world, 7, 170);
            Assert.Equal(DefaultProgress(system, lift), lift.PathProgress);
            Assert.Equal(1.0, system.GetElevatorControl(lift).Scale);
        }
    }

    [Fact]
    public void TheStops_AreTheStationaryRuns_AndTheNextOneIsTimedAtTheCurrentSpeed()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start();
        using (world)
        {
            IReadOnlyList<ElevatorStop> stops = system.ElevatorStops(lift);
            Assert.Equal(2, stops.Count);
            Assert.Equal((1, 0u, 3000u), (stops[0].Number, stops[0].StartMs, stops[0].DurationMs));
            Assert.Equal((2, 5000u, 3000u), (stops[1].Number, stops[1].StartMs, stops[1].DurationMs));

            Assert.True(system.SetElevatorPhase(lift, 1000));
            Assert.Equal(4000u, system.MsToNextElevatorStop(lift));
            Assert.True(system.SetElevatorPhase(lift, 9000));
            Assert.Equal(1000u, system.MsToNextElevatorStop(lift));                      // wraps to stop 1 at 0
            Assert.True(system.SetElevatorPhase(lift, 1000));
            Assert.True(system.SetElevatorScale(lift, 2.0));
            Assert.Equal(2000u, system.MsToNextElevatorStop(lift));
            Assert.True(system.FreezeElevator(lift));
            Assert.Null(system.MsToNextElevatorStop(lift));
        }
    }

    [Fact]
    public void AResync_SendsOutOfRangeThenACreateWithTheNewProgress_ToAViewer()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, Player player, FakeSession session) = Start();
        using (world)
        {
            Assert.Single(system.ElevatorViewers(lift));
            Assert.True(system.SetElevatorPhase(lift, 7000));
            world.RunTick(50);                                                            // the tick flushes the queued update

            (WorldOpcode opcode, byte[] payload) = Assert.Single(session.Sent);
            Assert.Equal(WorldOpcode.SmsgUpdateObject, opcode);
            var reader = new PacketReader(payload);
            Assert.Equal(2u, reader.ReadUInt32());                                        // the out-of-range list, and the create block
            reader.ReadByte();                                                            // has-transport
            Assert.Equal(ObjectUpdateType.OutOfRangeObjects, (ObjectUpdateType)reader.ReadByte());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(lift.Guid.Value, reader.ReadPackedGuid());
            Assert.Equal(ObjectUpdateType.CreateObject, (ObjectUpdateType)reader.ReadByte());
            Assert.Equal(lift.Guid.Value, reader.ReadPackedGuid());
            reader.ReadByte();                                                            // type id
            reader.ReadByte();                                                            // update flags
            reader.Skip(4 * 4);                                                           // position and facing
            reader.ReadUInt32();                                                          // ALL's unknown uint
            Assert.Equal(7000u, reader.ReadUInt32());                                     // the path progress of the new phase
            Assert.Contains(lift.Guid, player.VisibleObjects);
        }
    }

    [Fact]
    public void ALiftWithoutAnAnimation_IsRefused()
    {
        (WorldRuntime world, GameObjectMapSystem system, GameObject lift, _, _) = Start(animated: false);
        using (world)
        {
            Assert.False(system.SetElevatorPhase(lift, 100));
            Assert.False(system.FreezeElevator(lift));
            Assert.False(system.ResetElevator(lift));
            Assert.False(system.GetElevatorControl(lift).Overridden);
        }
    }
}
