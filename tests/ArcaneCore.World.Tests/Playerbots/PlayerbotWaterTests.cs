using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
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
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotWaterTests
{
    /// <summary>
    /// Bot QA #271 (drownings on the Sen'jin and Durotar coasts): the exit search planned at most 8 dry candidates, nearest ring first,
    /// and the caller retries the same search every two seconds from the same place. When the near rings' dry candidates are ground no
    /// walk reaches (here every one within 40 yards: a cliff top over a cove), the budget was spent on them every time, the beach 64
    /// yards out was never asked for, and the bot stood under water until the breath timer killed it. Every ring is now asked within
    /// the budget.
    /// </summary>
    [Fact]
    public async Task ExitSearch_ReachesTheFarShore_WhenEveryNearDryCandidateIsUnreachable()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-water-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDir, "maps"));
        try
        {
            // Tile (32, 32): x in (-533.33, 0], water at 88 over ground at 83 (submerged); tile (31, 32): x in (0, 533.33], dry ground.
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 32, 32)), MapTileBytes(groundHeight: 83.0f, liquidLevel: 88f));
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 31, 32)), MapTileBytes(groundHeight: 83.53f, liquidLevel: null));
            await using WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = dataDir);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new FarOnlyPathfinder(40f));
                    Player player = session.Player!;
                    player.Relocate(-10f, -30f, 83.0f, 0f, host.World.NowMs);
                    Map map = player.Map!;
                    Assert.True(PlayerbotWater.Submerged(map, new Vector3(player.X, player.Y, player.Z), player.Locomotion.CollisionHeight));
                    var options = new PlayerbotOptions { Enabled = true, MaxRouteYards = 300, MaxPathPoints = 128 };

                    PlayerbotRoute? route = PlayerbotWater.FindExit(player, options);
                    Assert.NotNull(route);
                    Vector3 end = route!.Points[^1];
                    Assert.False(PlayerbotWater.Submerged(map, end, player.Locomotion.CollisionHeight));
                    Assert.True(Vector2.Distance(new(end.X, end.Y), new(player.X, player.Y)) >= 40f);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #1501 review: the mmap generator meshes only the liquid's surface over deep water, so a bot swimming 25 yards down is
    /// found on its polygon only by the far box (<see cref="PathResult.StartFar"/>) and the mesh's route rises from it to the surface.
    /// That route stays the bot's (the exit search plans from where the bot is under water); the off-mesh ledge refusal is for a bot
    /// standing above or below the mesh on dry ground.
    /// </summary>
    [Fact]
    public async Task DeepSubmergedStart_KeepsTheMeshRouteUpToTheSurface()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-water-deep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDir, "maps"));
        try
        {
            // Tile (32, 32): water at 88 over ground at 63 (25 yards deep); tile (31, 32): dry ground.
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 32, 32)), MapTileBytes(groundHeight: 63.0f, liquidLevel: 88f));
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 31, 32)), MapTileBytes(groundHeight: 63.53f, liquidLevel: null));
            await using WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = dataDir);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    const float Surface = 88f;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new SurfaceMeshPathfinder(Surface));
                    Player player = session.Player!;
                    player.Relocate(-10f, -30f, 63.0f, 0f, host.World.NowMs);
                    Vector3 start = new(player.X, player.Y, player.Z);
                    Assert.True(PlayerbotWater.Submerged(player.Map!, start, player.Locomotion.CollisionHeight));
                    var options = new PlayerbotOptions { Enabled = true, MaxRouteYards = 300, MaxPathPoints = 128 };

                    Assert.True(PlayerbotNavigation.TryPlan(player, new Vector3(40f, -30f, 63.53f), options, out PlayerbotRoute? route, trial: true));
                    Assert.NotNull(route);
                    Assert.True(route!.Navigated, "the route is the mesh's");
                    Assert.Equal(Surface, route.Points[1].Z, 2);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    /// <summary>A mesh of the liquid's surface only: from the start straight up to it (the far box's start), then to the end.</summary>
    private sealed class SurfaceMeshPathfinder(float surface) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, start with { Z = surface }, end]) { StartFar = start.Z < surface - 5f };
    }

    /// <summary>No route to anything within <paramref name="nearYards"/> (flat): a cliff band round the cove; straight lines beyond.</summary>
    private sealed class FarOnlyPathfinder(float nearYards) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector2.Distance(new(start.X, start.Y), new(end.X, end.Y)) < nearYards
                ? PathResult.None(start)
                : PathResult.StraightLine(start, end, PathType.Normal);
    }

    /// <summary>A one-cell map tile of flat ground, with flat water over it when <paramref name="liquidLevel"/> is set.</summary>
    private static byte[] MapTileBytes(float groundHeight, float? liquidLevel)
    {
        bool hasLiquid = liquidLevel.HasValue;
        byte[] bytes = new byte[hasLiquid ? 73 : 56];
        Span<byte> span = bytes;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[0..], TerrainTile.MapMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[4..], TerrainTile.MapVersionMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 40u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 16u);
        if (hasLiquid)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 56u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 17u);
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 0x5447484Du); // "MHGT"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 1u); // MapHeightNoHeight
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span[48..], groundHeight);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span[52..], groundHeight);
        if (hasLiquid)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 0x51494C4Du); // "MLIQ"
            span[60] = 0x03; // MapLiquidNoType | MapLiquidNoHeight
            span[61] = (byte)LiquidTypeFlags.Water;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[62..], 1);
            span[64] = 0;
            span[65] = 0;
            span[66] = 128;
            span[67] = 128;
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span[68..], liquidLevel!.Value);
        }

        return bytes;
    }

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
    /// Bot QA #271: three bots drowned in the same Sen'jin cove while their move flag was still set, so the standing-still escape
    /// never ran. Once the breath bar is nearly empty the swim is abandoned even though the bot is moving.
    /// </summary>
    [Fact]
    public async Task MovingBotOutOfBreath_LeavesTheWater()
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
                MovementInfo moving = player.Movement; moving.Flags |= MovementFlags.Forward;
                var packet = new PacketWriter(); moving.Write(packet);
                session.ManagedBudget = new ManagedActionBudget(4);
                session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                MirrorTimer breath = player.Locomotion.MirrorTimers[(int)MirrorTimerType.Breath];
                breath.SetScale(-1);
                breath.Start(60_000);
                breath.SetRemaining(10_000);
                Assert.True(PlayerbotWater.BreathRunningOut(player));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A bot fighting under water still has to breathe: the out-of-breath escape was gated on being out of combat, so a bot that a
    /// murloc or a crocolisk held in a fight below the surface stood its ground until the breath timer killed it. The breath timer has
    /// no combat exemption (LiquidEnvironment runs it whatever the combat state, as a player's does), so the swim out runs in combat too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FightingBotOutOfBreath_LeavesTheWater(bool moving)
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
                if (moving)
                {
                    MovementInfo info = player.Movement; info.Flags |= MovementFlags.Forward;
                    var packet = new PacketWriter(); info.Write(packet);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
                }
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                player.Map!.Combat.SetInCombatState(player, 60_000);
                Assert.True(player.Combat.IsInCombat);
                MirrorTimer breath = player.Locomotion.MirrorTimers[(int)MirrorTimerType.Breath];
                breath.SetScale(-1);
                breath.Start(60_000);
                breath.SetRemaining(10_000);
                Assert.True(PlayerbotWater.BreathRunningOut(player));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "the fighting bot must start toward the shore");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Control for <see cref="FightingBotOutOfBreath_LeavesTheWater"/>: with breath to spare the fight below the surface goes on.</summary>
    [Fact]
    public async Task FightingBotWithBreathToSpare_IsNotDivertedByTheEscape()
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
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                player.Map!.Combat.SetInCombatState(player, 60_000);
                Assert.True(player.Combat.IsInCombat);
                MirrorTimer breath = player.Locomotion.MirrorTimers[(int)MirrorTimerType.Breath];
                breath.SetScale(-1);
                breath.Start(60_000);
                breath.SetRemaining(50_000);
                Assert.False(PlayerbotWater.BreathRunningOut(player));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.NotEqual(PlayerbotGoalKind.Escape, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A sick bot that is walking is not diverted into the water escape by its sickness alone: on its first think the sickness stand-down
    /// stops it and the goal is Rest, not Escape. The escape then takes it out on a later think (see SickSubmergedBotOffRoute_IsStoodDownThenEscapes).
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

                Assert.Equal(PlayerbotGoalKind.Rest, brain.Goal);
                Assert.NotEqual(PlayerbotGoalKind.Escape, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The drowning-risk path: a sick bot walking off its route in water is stood down first (goal Rest, StandDownWhileSick), and once the
    /// stop is acknowledged the next think finds it standing under water and the escape takes it to the shore.
    /// </summary>
    [Fact]
    public async Task SickSubmergedBotOffRoute_IsStoodDownThenEscapes()
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
                MovementInfo moving = player.Movement; moving.Flags |= MovementFlags.Forward;
                var packet = new PacketWriter(); moving.Write(packet);
                session.ManagedBudget = new ManagedActionBudget(4);
                session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                Assert.True(PlayerbotWater.Submerged(player));

                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Equal(PlayerbotGoalKind.Rest, brain.Goal);

                // Acknowledge the stop: pump the movement orders until the bot stands still.
                for (int pump = 0; pump < 8 && player.Movement.HasFlag(MovementFlags.Forward); pump++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMovementControl.Update(session, player);
                }
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward), "the stand-down must have stopped the bot");
                player.Locomotion.Environment |= EnvironmentFlags.Underwater; // the stop's heartbeat re-sampled the terrain
                Assert.True(PlayerbotWater.Submerged(player));

                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "the escape must start toward the shore");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The still variant: a sick bot that already stands under water is taken out by the escape on its first think.</summary>
    [Fact]
    public async Task SickSubmergedStillBot_EscapesAfterOneThink()
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
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                Assert.True(PlayerbotWater.Submerged(player));
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);

                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "the escape must start toward the shore");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A group handing the bot back clears the water route planned before it: a submerged, standing bot then plans afresh.</summary>
    [Fact]
    public async Task ResumeAfterGroup_ClearsTheWaterRoute_AndTheStillBotEscapesAfresh()
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
                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal); // a water route is now planned
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));

                brain.ResumeAfterGroup();
                Assert.Equal(PlayerbotGoalKind.Explore, brain.Goal);
                for (int pump = 0; pump < 8 && player.Movement.HasFlag(MovementFlags.Forward); pump++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMovementControl.Stop(session, player);
                    PlayerbotMovementControl.Update(session, player);
                }
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                player.Locomotion.Environment |= EnvironmentFlags.Underwater;
                Assert.True(PlayerbotWater.Submerged(player));

                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Equal(PlayerbotGoalKind.Escape, brain.Goal);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "a fresh escape must start toward the shore");
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }
}

public sealed class PlayerbotWaterRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotWaterRealTerrainTests(ITestOutputHelper output) : base(output) { }

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
