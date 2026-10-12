using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotWaterTests
{
    [Fact]
    public async Task SubmergedBotWaitingForHealth_MovesBeforeItRests()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                player.Health = 20;
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                brain.Risk.BeginWait(player);
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.NotEqual(PlayerbotGoalKind.Rest, brain.Goal);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward),
                    "the bot must start toward a non-submerged point before waiting for health");
                Assert.Equal(StandState.Stand, player.StandState);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Positive control for the two "is not diverted" tests: the same setup with a bot that is idle in a health wait (the case the
    /// escape owns) ends up in the Escape goal, so a negative result there means the water block was reached and declined.</summary>
    [Fact]
    public async Task SubmergedIdleBotInAWait_IsTakenOutByTheEscape()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                player.Health = 20;
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                brain.Risk.BeginWait(player);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task MovingBotCrossingShallowWater_IsNotDivertedByTheEscape()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                MovementInfo moving = player.Movement; moving.Flags |= MovementFlags.Forward; // walking a route through a ford
                var packet = new PacketWriter(); moving.Write(packet);
                session.ManagedBudget = new ManagedActionBudget(4);
                session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                // After the heartbeat: the movement handler re-samples the terrain and clears a flag set before it, which would make the
                // brain see a dry bot and this test pass whatever the water block does.
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                Assert.True(PlayerbotWater.Submerged(player));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.NotEqual(PlayerbotGoalKind.Escape, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A sick bot still walks its quest returns and errands (the sickness stand-down only stops an idle bot): crossing a ford while sick
    /// must not be pulled back to the bank, or the route and the escape fight each other until the sickness ends (review, cycle 2).
    /// </summary>
    [Fact]
    public async Task SickMovingBotCrossingShallowWater_IsNotDivertedByTheEscape()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                PlayerbotSicknessTestKit.Apply(host.WorldServices, player);
                MovementInfo moving = player.Movement; moving.Flags |= MovementFlags.Forward; // walking a quest return through a ford
                var packet = new PacketWriter(); moving.Write(packet);
                session.ManagedBudget = new ManagedActionBudget(4);
                session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                // After the heartbeat: the movement handler re-samples the terrain and clears a flag set before it (the test used to
                // pass for that reason alone: the brain saw a dry bot and never reached the water block).
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                Assert.True(PlayerbotWater.Submerged(player));
                Assert.True(PlayerbotSickness.Holder(host.WorldServices.GetRequiredService<SpellFeature>().System, player)
                    is { IsPermanent: false, Duration: > 0 }, "the bot must carry a running, non-permanent sickness");
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.NotEqual(PlayerbotGoalKind.Escape, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }
}

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    [RealTerrainBotFact]
    public async Task DurotarCoast_RevivedBotEscapesBeforeResting()
    {
        Vector3 coast = new(-300f, -5310f, -14f); // w29 #263/#271 drowning cluster
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(coast, 1);
        Vector3? wet = await terrain.Host.OnWorldAsync(() =>
        {
            for (float x = -350f; x <= -260f; x += 5f)
                for (float y = -5330f; y <= -5290f; y += 5f)
                    for (float z = -14f; z <= -8f; z += 2f)
                    {
                        Vector3 sample = new(x, y, z);
                        if (PlayerbotWater.Submerged(terrain.Player.Map!, sample, terrain.Player.Locomotion.CollisionHeight))
                            return (Vector3?)sample;
                    }
            return null;
        });
        Assert.True(wet.HasValue, "the supplied terrain has no submerged point in the reported Durotar cluster");
        coast = wet.Value;
        await terrain.PlaceAsync(coast, 1);
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 };
        var brain = new PlayerbotBrain(terrain.Session, options);
        try
        {
            await terrain.Host.OnWorldAsync(() =>
            {
                Player player = terrain.Player;
                bool Dry(Vector3 point) => !PlayerbotWater.Submerged(player.Map!, point, player.Locomotion.CollisionHeight);
                Assert.Equal(PlayerbotUnsafeBody.Hazard,
                    PlayerbotRecovery.JudgeBody(false, false, false, false, bodySubmerged: !Dry(coast)));
                Vector3? reviveSpot = PlayerbotRecovery.FindReviveSpot(coast, coast, [], candidate => candidate, Dry);
                Assert.True(reviveSpot.HasValue, "a dry revive spot exists beside the Durotar coast and must be found");
                Assert.True(Dry(reviveSpot.Value), "the revive spot remained submerged");
                Assert.NotEqual(coast, reviveSpot.Value);
                LiquidEnvironment.UpdateTerrainFlags(player);
                Assert.True(PlayerbotWater.Submerged(player.Map!, coast, player.Locomotion.CollisionHeight));
                Assert.True(PlayerbotWater.Submerged(player));
                player.Health = player.MaxHealth / 2;
                brain.Risk.BeginWait(player);
            });

            bool dry = false;
            for (int tick = 0; tick < 400 && !dry; tick++)
            {
                dry = await terrain.Host.OnWorldAsync(() =>
                {
                    terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                    brain.Update(100);
                    PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
                    LiquidEnvironment.UpdateTerrainFlags(terrain.Player);
                    bool stillSubmerged = PlayerbotWater.Submerged(terrain.Player);
                    if (stillSubmerged) Assert.NotEqual(PlayerbotGoalKind.Rest, brain.Goal);
                    return !stillSubmerged;
                });
                await terrain.Host.World.AdvanceClockAsync(100);
            }

            Assert.True(dry, "the bot did not leave the Durotar coast water within 40s of world time");
        }
        finally { await terrain.Host.OnWorldAsync(() => brain.Stop()); }
    }
}
