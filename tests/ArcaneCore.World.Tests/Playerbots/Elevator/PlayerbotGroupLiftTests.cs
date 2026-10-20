using System.Numerics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>The group AI rides the lifts as the brain does: a bot-led group's leader whose way leads up the Great Lift takes it.</summary>
public sealed class PlayerbotGroupLiftTests
{
    [Fact]
    public async Task AGroupLeaderWhoseWayLeadsUpTheGreatLift_RidesIt()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };
        var ai = new PlayerbotGroupAI(world.Session, options);
        PlayerbotGroupCoordinator.BotGroup group = await world.OnWorldAsync(() =>
        {
            Vector3 top = ElevatorTestWorld.Ratchet;
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, 0, 0, ElevatorTestData.Kalimdor, ElevatorTestData.Kalimdor, top, 0, top, 2, 1, "test");
            var built = new PlayerbotGroupCoordinator.BotGroup(1, goal, Guid.NewGuid(), world.Player.Guid, world.Host.World.NowMs)
                { State = PlayerbotGroupState.Travelling, Approach = top };
            built.Members.Add(new PlayerbotGroupCoordinator.Member(built.LeaderBotId, world.Player.Guid, world.Player.Name, PlayerbotGroupRole.Tank,
                PlayerbotRole.Tank, 0, real: false) { Joined = true, AI = ai });
            return built;
        });

        ElevatorStage? stage = null;
        bool rode = false;
        for (int step = 0; step < 3_000 && stage is not (ElevatorStage.Done or ElevatorStage.Failed); step++)
        {
            (ElevatorStage? s, string aiStep) = await world.TickAsync(() =>
            {
                world.Session.ManagedBudget = new ManagedActionBudget(8);
                ai.Update(world.Player, group, group.Members[0], 100);
                return (PlayerbotElevator.TripOf(world.Player)?.Stage, ai.Step);
            });
            stage = s;
            rode |= aiStep == "lift";
        }

        Assert.Equal(ElevatorStage.Done, stage);
        Assert.True(rode, "the group AI never handed its think to the lift ride");
        Assert.True(await world.OnWorldAsync(() => world.Player.Z > ElevatorTestWorld.TopFloor - 5f), "the leader is not up on the plateau");
    }
}
