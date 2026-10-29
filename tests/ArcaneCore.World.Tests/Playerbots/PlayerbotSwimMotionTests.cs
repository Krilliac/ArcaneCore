using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot motion in deep water. A player in swimmable water (liquid level above z + 0.75 collision height, LiquidEnvironment's HighLiquid) moves
/// at swim speed (Unit.BaseSwimSpeed, 4.72 yd/s), reports MOVEFLAG_SWIMMING, and loses its mount (every 1.12.1 mount spell has
/// AuraInterruptFlags 0x80, AURA_INTERRUPT_UNDER_WATER_CANCELS; vmangos Player::SetEnvironmentFlags). The motion walked its routes at
/// run speed (or mounted run speed) whatever the water.
/// <para>
/// World layout: map 0 tile (32,32) is x in (-533.33, 0] with flat ground at 83 under flat water at 88 (5 yd deep: swim depth); tile (31,32)
/// is x in (0, 533.33] and dry. Routes run east (heading 0) along y = -30. Time moves only through <see cref="PlayerbotMotion.ElapseForTests"/>.
/// </para>
/// </summary>
public sealed class PlayerbotSwimMotionTests
{
    private const float Ground = 83.0f;
    private const float Surface = 88f;
    private const float Y = -30f;
    private const uint MountSpellId = 97300;
    private const uint MountCreature = 97301;
    private const uint MountDisplay = 14337;

    /// <summary>Unit.BaseSwimSpeed; written out so a wrong constant in the code under test cannot make the expectation agree with it.</summary>
    private const float SwimYardsPerSecond = 4.72f;
    private const float RunYardsPerSecond = 7f;

    [Fact]
    public async Task BotInDeepWater_MovesAtSwimSpeed_AndReportsSwimming()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Place(host, player, -40f);
                LiquidEnvironment.UpdateTerrainFlags(player);
                Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid), "the fixture water must be swim depth");
                float swim = UnitSpeed.Get(player, MoveType.Swim);
                Assert.Equal(SwimYardsPerSecond, swim, 2);

                Assert.True(Follow(host, session, player, new Vector3(-40f, Y, Ground), new Vector3(10f, Y, Ground)), "the route did not start");
                float moved = Step(host, session, player);

                Assert.True(Math.Abs(moved - swim) <= 0.1f, $"moved {moved:F3} yd in 1000 ms in deep water; swim speed is {swim:F3}");
                Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"the heartbeat in deep water must carry MOVEFLAG_SWIMMING (flags {player.Movement.Flags})");
                return true;
            });
        });

    [Fact]
    public async Task MountedBotEnteringDeepWater_IsDismounted_AndMovesAtSwimSpeed()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                [new CreatureTemplate { Entry = MountCreature, Name = "Test Horse", DisplayIds = [MountDisplay], DisplayProbabilities = [100] }], [], [], [], [])));
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                spells.Store = new SpellStore([.. spells.Store.All, MountSpell()], [], []);

                // Mount on dry land, as a bot does, then ride into the lake: the terrain flags are not re-sampled until a movement packet.
                Place(host, player, 40f);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, MountSpellId, SpellCastTargets.ForSelf(), triggered: true));
                Assert.True(UnitSpeed.IsMounted(player), "the fixture mount did not take");
                for (int ack = 0; ack < 4; ack++) { session.ManagedBudget = new ManagedActionBudget(4); PlayerbotMovementControl.Update(session, player); } // the speed change is applied when the bot acknowledges it
                Assert.True(UnitSpeed.Get(player, MoveType.Run) > RunYardsPerSecond + 0.5f, "the fixture mount must give a mounted run speed");
                Place(host, player, -40f);

                Assert.True(Follow(host, session, player, new Vector3(-40f, Y, Ground), new Vector3(10f, Y, Ground)), "the route did not start");
                Step(host, session, player); // the heartbeat that puts the rider at swim depth
                Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid), "the heartbeat did not put the bot at swim depth");
                Assert.False(UnitSpeed.IsMounted(player), "a mount (AuraInterruptFlags 0x80) must be gone at swim depth");

                float swim = UnitSpeed.Get(player, MoveType.Swim);
                Assert.Equal(SwimYardsPerSecond, swim, 2);
                float moved = Step(host, session, player);
                Assert.True(Math.Abs(moved - swim) <= 0.1f, $"moved {moved:F3} yd in 1000 ms after the dismount; swim speed is {swim:F3}");
                Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"the heartbeat in deep water must carry MOVEFLAG_SWIMMING (flags {player.Movement.Flags})");
                return true;
            });
        });

    /// <summary>
    /// A route from deep water onto the beach. Steps 1 and 2 start in the water (swim speed, Swimming); step 3 starts in the water and ends on land
    /// (the transition, anywhere between swim and run distance); steps 4 and 5 are wholly on land (run speed, no Swimming). No step is longer than
    /// the run speed allows: the position never jumps.
    /// </summary>
    [Fact]
    public async Task RouteLeavingTheWater_SwimsThenRuns_WithoutAJumpAtTheShore()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Place(host, player, -12f);
                LiquidEnvironment.UpdateTerrainFlags(player);
                Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid), "the fixture water must be swim depth");
                Vector3 end = new(60f, Y, Ground);
                Assert.True(Follow(host, session, player, new Vector3(-12f, Y, Ground), end), "the route did not start");

                var steps = new List<(float Moved, float X, MovementFlags Flags)>();
                for (int i = 0; i < 3; i++)
                {
                    float moved = Step(host, session, player);
                    steps.Add((moved, player.X, player.Movement.Flags));
                }

                // A think renews the lease as the brain does, then the bot is on land for two more heartbeats.
                Assert.True(Follow(host, session, player, new Vector3(player.X, player.Y, Ground), end), "the route did not continue");
                for (int i = 0; i < 2; i++)
                {
                    float moved = Step(host, session, player);
                    steps.Add((moved, player.X, player.Movement.Flags));
                }

                string trace = string.Join("; ", steps.Select((s, i) => $"#{i + 1}: {s.Moved:F2} yd to x={s.X:F2} {s.Flags}"));
                for (int i = 0; i < 2; i++)
                {
                    Assert.True(Math.Abs(steps[i].Moved - SwimYardsPerSecond) <= 0.1f, $"step {i + 1} in the water moved {steps[i].Moved:F3}, swim speed is {SwimYardsPerSecond} ({trace})");
                    Assert.True(steps[i].X < 0f, $"step {i + 1} should still be in the water ({trace})");
                    Assert.True(steps[i].Flags.HasFlag(MovementFlags.Swimming), $"step {i + 1} in the water lacks MOVEFLAG_SWIMMING ({trace})");
                }

                Assert.True(steps[2].X > 0f, $"step 3 should end on land ({trace})");
                foreach ((float moved, _, _) in steps)
                    Assert.True(moved <= RunYardsPerSecond + 0.1f, $"a step of {moved:F3} yd in 1000 ms is longer than run speed allows ({trace})");

                for (int i = 3; i < 5; i++)
                {
                    Assert.True(steps[i].X > 0f, $"step {i + 1} should be on land ({trace})");
                    Assert.True(Math.Abs(steps[i].Moved - RunYardsPerSecond) <= 0.1f, $"step {i + 1} on land moved {steps[i].Moved:F3}, run speed is {RunYardsPerSecond} ({trace})");
                    Assert.False(steps[i].Flags.HasFlag(MovementFlags.Swimming), $"step {i + 1} on land still has MOVEFLAG_SWIMMING ({trace})");
                }

                return true;
            });
        });

    /// <summary>A heartbeat that covers more than 1.05 times the speed the bot is entitled to is reported (the motion, not the movement handler, set that speed).</summary>
    [Fact]
    public async Task HeartbeatFasterThanTheExpectedSpeed_ReportsAnOverspeed()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture capture = H1AnomalyKit.CaptureFor(player);
                Place(host, player, 20f);
                float run = UnitSpeed.Get(player, MoveType.Run);
                Assert.Equal(RunYardsPerSecond, run, 2);
                Assert.True(Follow(host, session, player, new Vector3(20f, Y, Ground), new Vector3(120f, Y, Ground)), "the route did not start");
                PlayerbotMotion.Of(player).Speed = run * 1.5f; // the injected fault: the motion walks 50% faster than the bot's run speed
                float moved = Step(host, session, player);

                Assert.True(moved > run * 1.05f, $"fixture: the injected speed moved {moved:F3} yd, not past 1.05 x run ({run * 1.05f:F3})");
                string key = PlayerbotAnomalies.Key("motion", "overspeed");
                Assert.True(capture.Any(key), $"no {key} reported for {moved:F3} yd in 1000 ms at run speed {run}; reported: {string.Join(", ", capture.Notices.Select(n => n.Key))}");
                return true;
            });
        });

    /// <summary>Control for the overspeed check: ordinary walking on land and swimming report nothing.</summary>
    [Fact]
    public async Task NormalMotionOnLandAndInWater_ReportsNoOverspeed()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture capture = H1AnomalyKit.CaptureFor(player);

                Place(host, player, 20f);
                Assert.True(Follow(host, session, player, new Vector3(20f, Y, Ground), new Vector3(120f, Y, Ground)), "the land route did not start");
                for (int i = 0; i < 3; i++)
                    Assert.True(Step(host, session, player) > 1f, "the bot did not move on land");

                Place(host, player, -60f);
                LiquidEnvironment.UpdateTerrainFlags(player);
                Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid), "the fixture water must be swim depth");
                Assert.True(Follow(host, session, player, new Vector3(-60f, Y, Ground), new Vector3(-5f, Y, Ground)), "the water route did not start");
                for (int i = 0; i < 3; i++)
                    Assert.True(Step(host, session, player) > 1f, "the bot did not move in the water");

                string key = PlayerbotAnomalies.Key("motion", "overspeed");
                Assert.False(capture.Any(key), $"unexpected {key}; reported: {string.Join(", ", capture.Notices.Select(n => n.Key))}");
                return true;
            });
        });

    /// <summary>The seabed of the mesh test lake: 18 yd under the surface, far past <see cref="PlayerbotMotion.MeshFloorToleranceYards"/> (4).</summary>
    private const float DeepGround = 70f;

    /// <summary>
    /// Live terrain meshes only the liquid surface over deep water. A navigated mesh route over water deeper than the mesh floor tolerance
    /// had its steps placed on that surface (PlayerbotMotion.Validate / MeshSurface), where the water is not swim depth by the liquid rule
    /// (level > z + 0.75 collision height): no HighLiquid, no swimming, run speed, and a mount that never drops.
    /// </summary>
    [Fact]
    public async Task MeshRouteOverDeepWater_Swims_AtSwimSpeed_AndSetsTheHighLiquidFlag()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(Surface - DeepGround > PlayerbotMotion.MeshFloorToleranceYards + 1f, "fixture: the seabed must be out of the mesh floor tolerance");
                Vector3 a = new(-40f, Y, Surface), b = new(10f, Y, Surface); // the mesh polygon is the water surface
                player.Relocate(a.X, a.Y, a.Z, 0f, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(4);
                var route = new PlayerbotRoute([a, b], 50f, navigated: true) { MeshPoints = [a, b] };
                Assert.True(PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs), "the route did not start");

                float swim = UnitSpeed.Get(player, MoveType.Swim);
                Assert.Equal(SwimYardsPerSecond, swim, 2);
                Step(host, session, player); // the first heartbeat may still be at the speed the route started with
                var trace = new List<string>();
                for (int i = 2; i <= 3; i++)
                {
                    float moved = Step(host, session, player);
                    trace.Add($"#{i}: {moved:F2} yd z={player.Z:F2} {player.Movement.Flags} env={player.Locomotion.Environment}");
                    string all = string.Join("; ", trace);
                    Assert.True(Math.Abs(moved - swim) <= 0.1f, $"step {i} over deep water on a mesh route moved {moved:F3} yd in 1000 ms; swim speed is {swim:F3} ({all})");
                    Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"step {i}: no MOVEFLAG_SWIMMING ({all})");
                    Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid), $"step {i}: the server HighLiquid flag is not set, so a mount would not drop ({all})");
                    Assert.Equal(86.45f, player.Z, 2); // vmangos: liquid level - 0.75 * collision height (2.0) - the epsilon; not the liquid surface
                }

                return true;
            });
        }, ground: DeepGround, pathfinder: new SurfaceOnlyMesh(Surface));

    /// <summary>The height of a swimming bot (SwimHeight): the liquid level less 0.75 collision height and the 0.05 yd epsilon. 86.45 for the default height of 2.0.</summary>
    private static float SwimZ(float collisionHeight = 2.0f) => Surface - collisionHeight * 0.75f - 0.05f;

    private static void AssertSwimmingAt(Player player, float expectedZ, string what)
    {
        string state = $"z={player.Z:F3} {player.Movement.Flags} env={player.Locomotion.Environment}";
        Assert.True(Math.Abs(player.Z - expectedZ) <= 1e-3f, $"{what}: expected z {expectedZ:F3}, the bot is at {state}");
        Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"{what}: MOVEFLAG_SWIMMING is missing ({state})");
        Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid), $"{what}: the HighLiquid flag is missing ({state})");
    }

    /// <summary>Record every opcode the motion sends through the movement handler from now on.</summary>
    private static List<WorldOpcode> RecordOpcodes(WorldSession session)
    {
        var ops = new List<WorldOpcode>();
        session.ManagedDispatchObserver = op => ops.Add(op);
        return ops;
    }

    private static bool FollowSurface(WorldTestHost host, WorldSession session, Player player, float fromX, float toX)
    {
        Vector3 a = new(fromX, Y, Surface), b = new(toX, Y, Surface); // the mesh polygon is the water surface
        player.Relocate(a.X, a.Y, a.Z, 0f, host.World.NowMs);
        session.ManagedBudget = new ManagedActionBudget(4);
        var route = new PlayerbotRoute([a, b], MathF.Abs(toX - fromX), navigated: true) { MeshPoints = [a, b] };
        return PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs);
    }

    /// <summary>
    /// Flush runs before every movement-change acknowledgement. It used to send a heartbeat at the raw mesh (liquid surface) position with no
    /// SwimHeight and no swim decision: the swimming bot popped to the surface, Swimming and HighLiquid cleared, and the next Pump lowered it again.
    /// </summary>
    [Fact]
    public async Task Flush_BeforeAMovementAck_MidSwim_KeepsSwimDepthAndFlags()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(FollowSurface(host, session, player, -40f, 10f), "the route did not start");
                Step(host, session, player);
                Step(host, session, player);
                AssertSwimmingAt(player, SwimZ(), "before the flush");
                PlayerbotMotion.ElapseForTests(player, 300);

                UnitSpeed.SetRate(player, MoveType.Run, 0.5f);
                Assert.True(player.Locomotion.Pending.HasPending);
                session.ManagedBudget = new ManagedActionBudget(1);
                List<WorldOpcode> ops = RecordOpcodes(session);
                Assert.True(PlayerbotMovementControl.Update(session, player));

                AssertSwimmingAt(player, SwimZ(), "after the flush");
                Assert.DoesNotContain(WorldOpcode.MsgMoveStartSwim, ops);
                Assert.DoesNotContain(WorldOpcode.MsgMoveStopSwim, ops);
                Assert.False(player.Locomotion.Pending.HasPending, "the speed order was not acknowledged");

                Step(host, session, player); // the next pump: no bob back down
                AssertSwimmingAt(player, SwimZ(), "the step after the flush");
                return true;
            });
        }, ground: DeepGround, pathfinder: new SurfaceOnlyMesh(Surface));

    /// <summary>A route switch (a second Follow while moving) that catches the bot in the water on the shore tile announces the swim once.</summary>
    [Fact]
    public async Task RouteSwitch_AtTheShore_LandToDeepWater_SendsMsgMoveStartSwimOnce()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Place(host, player, 1f);
                LiquidEnvironment.UpdateTerrainFlags(player);
                Assert.True(Follow(host, session, player, new Vector3(1f, Y, Ground), new Vector3(-40f, Y, Ground)), "the first route did not start");
                Assert.False(player.Movement.HasFlag(MovementFlags.Swimming), "fixture: the bot starts on land");

                // One second at run speed (7 yd) carries the old route to x = -6, in the water; no pump ran, so the bot was not announced there yet.
                PlayerbotMotion.ElapseForTests(player, 1000);
                List<WorldOpcode> ops = RecordOpcodes(session);
                Assert.True(Follow(host, session, player, new Vector3(-6f, Y, Ground), new Vector3(-45f, Y, Ground)), "the second route did not start");
                Step(host, session, player);

                Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveStartSwim));
                Assert.DoesNotContain(WorldOpcode.MsgMoveStopSwim, ops);
                Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"the bot is not swimming ({player.Movement.Flags})");
                return true;
            });
        });

    /// <summary>Arrival in deep water: MSG_MOVE_STOP at swim depth, still Swimming.</summary>
    [Fact]
    public async Task ArrivalStop_InDeepWater_IsAtSwimDepth_WithSwimmingAndHighLiquid()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(FollowSurface(host, session, player, -40f, -35f), "the route did not start"); // 5 yd: arrives on the second step
                Step(host, session, player);
                List<WorldOpcode> ops = RecordOpcodes(session);
                Step(host, session, player);

                Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveStop));
                AssertSwimmingAt(player, SwimZ(), "the arrival stop");
                Assert.Equal(MovementFlags.None, player.Movement.Flags & MovementFlags.MaskMoving);
                return true;
            });
        }, ground: DeepGround, pathfinder: new SurfaceOnlyMesh(Surface));

    /// <summary>PlayerbotMotion.Stop mid-route in deep water: the same swim-depth stop.</summary>
    [Fact]
    public async Task Stop_MidWater_IsAtSwimDepth_WithSwimmingAndHighLiquid()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(FollowSurface(host, session, player, -40f, 10f), "the route did not start");
                Step(host, session, player);
                PlayerbotMotion.ElapseForTests(player, 300);
                List<WorldOpcode> ops = RecordOpcodes(session);

                Assert.True(PlayerbotMotion.Stop(session, player));

                Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveStop));
                AssertSwimmingAt(player, SwimZ(), "Stop");
                return true;
            });
        }, ground: DeepGround, pathfinder: new SurfaceOnlyMesh(Surface));

    /// <summary>The mesh stops answering mid-route (the next step cannot be validated): the stop at the anchor stays at swim depth with the flags.</summary>
    [Fact]
    public async Task RouteBlockedStop_AtTheAnchor_KeepsSwimDepthAndFlags()
    {
        var mesh = new SurfaceOnlyMesh(Surface);
        await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(FollowSurface(host, session, player, -40f, 10f), "the route did not start");
                Step(host, session, player);
                AssertSwimmingAt(player, SwimZ(), "before the block");

                // Without a mesh walk the surface route has no ground support 18 yd over the seabed (Validate: NoGroundSupport).
                mesh.RayAvailable = false;
                List<WorldOpcode> ops = RecordOpcodes(session);
                Step(host, session, player);

                Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveStop));
                AssertSwimmingAt(player, SwimZ(), "the route-blocked stop");
                return true;
            });
        }, ground: DeepGround, pathfinder: mesh);
    }

    /// <summary>
    /// SendStepOff into swim-depth water carries Swimming and never OnTransport. NARROWED: it is called with no transport here. The hazard of a
    /// transport still attached at call time needs a ship harness (Ship/ShipTestWorld), which is too heavy for this file.
    /// </summary>
    [Fact]
    public async Task SendStepOff_IntoSwimDepthWater_CarriesSwimming_AndNotOnTransport()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Place(host, player, 40f);
                List<WorldOpcode> ops = RecordOpcodes(session);

                Assert.True(PlayerbotMotion.SendStepOff(session, player, new Vector3(-40f, Y, Ground), 0f));

                Assert.Contains(WorldOpcode.MsgMoveHeartbeat, ops);
                Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"the step-off into deep water lacks MOVEFLAG_SWIMMING ({player.Movement.Flags})");
                Assert.False(player.Movement.HasFlag(MovementFlags.OnTransport), $"the step-off carries ONTRANSPORT ({player.Movement.Flags})");

                // Control: onto the beach it does not swim.
                Assert.True(PlayerbotMotion.SendStepOff(session, player, new Vector3(40f, Y, Ground), 0f));
                Assert.False(player.Movement.HasFlag(MovementFlags.Swimming), $"the step-off onto land carries MOVEFLAG_SWIMMING ({player.Movement.Flags})");
                Assert.False(player.Movement.HasFlag(MovementFlags.OnTransport));
                return true;
            });
        });

    /// <summary>A fresh start in the water is a start-forward that already swims; the next step is a plain heartbeat, never a swim transition.</summary>
    [Fact]
    public async Task FreshStartInWater_SendsStartForwardSwimming_ThenPlainHeartbeats_NoStartSwim()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                List<WorldOpcode> ops = RecordOpcodes(session);
                Assert.True(FollowSurface(host, session, player, -40f, 10f), "the route did not start");

                Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveStartForward));
                Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"the start in deep water lacks MOVEFLAG_SWIMMING ({player.Movement.Flags})");

                ops.Clear();
                Step(host, session, player);
                Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveHeartbeat));
                Step(host, session, player);
                Assert.Equal(2, ops.Count(o => o == WorldOpcode.MsgMoveHeartbeat));
                Assert.DoesNotContain(WorldOpcode.MsgMoveStartSwim, ops);
                Assert.DoesNotContain(WorldOpcode.MsgMoveStopSwim, ops);
                AssertSwimmingAt(player, SwimZ(), "after the steps");
                return true;
            });
        }, ground: DeepGround, pathfinder: new SurfaceOnlyMesh(Surface));

    /// <summary>
    /// With sight lines blocked and the mesh walk unavailable, a surface route over deep water must still heartbeat at the swim depth of the
    /// bot's own collision height; a taller bot's swim depth is more than a step-up (2 yd) below the surface.
    /// </summary>
    [Theory]
    [InlineData(2.0f)]
    [InlineData(2.4f)]
    [InlineData(2.8f)]
    public async Task ThreeConsecutiveHeartbeats_Succeed_WithBlockedSightAndNoMeshWalk(float collisionHeight)
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Locomotion.CollisionHeight = collisionHeight;
                Vector3 a = new(-40f, Y, Surface), b = new(10f, Y, Surface);
                player.Relocate(a.X, a.Y, a.Z, 0f, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(4);
                var route = new PlayerbotRoute([a, b], 50f, navigated: true); // no MeshPoints: without a mesh walk the route's own height stands
                Assert.True(PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs), "the route did not start");

                List<WorldOpcode> ops = RecordOpcodes(session);
                for (int i = 1; i <= 3; i++)
                {
                    ops.Clear();
                    float moved = Step(host, session, player);
                    Assert.True(moved > 1f, $"heartbeat {i} did not move the bot (h={collisionHeight})");
                    Assert.Equal(1, ops.Count(o => o == WorldOpcode.MsgMoveHeartbeat));
                    AssertSwimmingAt(player, SwimZ(collisionHeight), $"heartbeat {i} (h={collisionHeight})");
                }

                return true;
            });
        }, ground: DeepGround, pathfinder: new SurfaceOnlyMesh(Surface, rayAvailable: false), blockedSight: true);

    [Fact]
    public async Task ShoreCrossing_LandToWater_SendsOneStartSwimWithinAStep()
        => await RunAsync((host, session) => host.OnWorldAsync(() => { WalkShore(host, session, intoWater: true); return true; }));

    [Fact]
    public async Task ShoreCrossing_WaterToLand_SendsOneStopSwimWithinAStep()
        => await RunAsync((host, session) => host.OnWorldAsync(() => { WalkShore(host, session, intoWater: false); return true; }));

    /// <summary>
    /// 100 ms steps across x = 0. Packets are what the motion sends through the movement handler (ManagedDispatchObserver); the position and
    /// flags are read after each step. The swim packet must come at the shore, not at the next 500 ms heartbeat.
    /// </summary>
    private static void WalkShore(WorldTestHost host, WorldSession session, bool intoWater)
    {
        Player player = session.Player!;
        float startX = intoWater ? 3f : -3f, endX = intoWater ? -40f : 40f;
        Place(host, player, startX);
        LiquidEnvironment.UpdateTerrainFlags(player);
        var ops = new List<WorldOpcode>();
        session.ManagedDispatchObserver = op => ops.Add(op);
        try
        {
            Assert.True(Follow(host, session, player, new Vector3(startX, Y, Ground), new Vector3(endX, Y, Ground)), "the route did not start");
            var packets = new List<(int Step, WorldOpcode[] Ops, Vector2 Pos, MovementFlags Flags)> { (0, [.. ops], new(player.X, player.Y), player.Movement.Flags) };
            for (int step = 1; step <= 14; step++)
            {
                ops.Clear();
                PlayerbotMotion.ElapseForTests(player, 100);
                PlayerbotMotion.Pump(session, player, host.World.NowMs);
                if (ops.Count > 0) packets.Add((step, [.. ops], new(player.X, player.Y), player.Movement.Flags));
            }

            string trace = string.Join("; ", packets.Select(p => $"@{p.Step}: [{string.Join(",", p.Ops)}] x={p.Pos.X:F2} {p.Flags}"));
            WorldOpcode swimOp = intoWater ? WorldOpcode.MsgMoveStartSwim : WorldOpcode.MsgMoveStopSwim;
            WorldOpcode otherOp = intoWater ? WorldOpcode.MsgMoveStopSwim : WorldOpcode.MsgMoveStartSwim;
            Assert.True(intoWater ? player.X < -1f : player.X > 1f, $"the bot did not cross the shore ({trace})");
            Assert.True(1 == packets.Sum(p => p.Ops.Count(o => o == swimOp)), $"expected exactly one {swimOp} ({trace})");
            Assert.True(0 == packets.Sum(p => p.Ops.Count(o => o == otherOp)), $"expected no {otherOp} ({trace})");

            // At the shore: within one step of it at run speed (0.7 yd), not at a later heartbeat.
            var swimPacket = packets.Single(p => p.Ops.Contains(swimOp));
            if (intoWater) Assert.True(swimPacket.Pos.X is >= -0.75f and <= 0.05f, $"{swimOp} sent at x={swimPacket.Pos.X:F2}, not at the shore ({trace})");
            else Assert.True(swimPacket.Pos.X is >= -0.05f and <= 0.75f, $"{swimOp} sent at x={swimPacket.Pos.X:F2}, not at the shore ({trace})");

            for (int i = 0; i < packets.Count; i++)
            {
                var p = packets[i];
                if (MathF.Abs(p.Pos.X) > 0.05f)
                    Assert.True(p.Flags.HasFlag(MovementFlags.Swimming) == p.Pos.X < 0f, $"packet @{p.Step}: Swimming flag disagrees with x={p.Pos.X:F2} ({trace})");
                if (i == 0) continue;
                var q = packets[i - 1];
                float bound = (q.Pos.X < -0.05f && p.Pos.X < -0.05f ? SwimYardsPerSecond : RunYardsPerSecond) * ((p.Step - q.Step) * 0.1f) + 0.06f;
                Assert.True(Vector2.Distance(q.Pos, p.Pos) <= bound, $"packet @{p.Step} jumped {Vector2.Distance(q.Pos, p.Pos):F2} yd from @{q.Step} (bound {bound:F2}) ({trace})");
            }
        }
        finally { session.ManagedDispatchObserver = null; }
    }

    /// <summary>A bot mounted on land rides into the lake: after the crossing it has lost the mount, swims at swim speed and nothing reports an overspeed.</summary>
    [Fact]
    public async Task MountedBotRidingFromLandIntoWater_IsDismounted_Swims_AndReportsNoOverspeed()
        => await RunAsync(async (host, session) =>
        {
            await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                [new CreatureTemplate { Entry = MountCreature, Name = "Test Horse", DisplayIds = [MountDisplay], DisplayProbabilities = [100] }], [], [], [], [])));
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture capture = H1AnomalyKit.CaptureFor(player);
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                spells.Store = new SpellStore([.. spells.Store.All, MountSpell()], [], []);
                Place(host, player, 10f);
                LiquidEnvironment.UpdateTerrainFlags(player);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, MountSpellId, SpellCastTargets.ForSelf(), triggered: true));
                Assert.True(UnitSpeed.IsMounted(player), "the fixture mount did not take");
                for (int ack = 0; ack < 4; ack++) { session.ManagedBudget = new ManagedActionBudget(4); PlayerbotMovementControl.Update(session, player); }
                float mounted = UnitSpeed.Get(player, MoveType.Run);
                Assert.True(mounted > RunYardsPerSecond + 0.5f, "fixture: the mount must give a mounted run speed");

                Assert.True(Follow(host, session, player, new Vector3(10f, Y, Ground), new Vector3(-60f, Y, Ground)), "the route did not start");
                float crossing = Step(host, session, player); // 11.2 yd west from x = 10: across the shore
                Assert.True(player.X < 0f, $"fixture: the first step did not reach the water (x {player.X:F2}, moved {crossing:F2})");

                float swim = UnitSpeed.Get(player, MoveType.Swim);
                var trace = new List<string> { $"#1: {crossing:F2} yd x={player.X:F2} mounted={UnitSpeed.IsMounted(player)} {player.Movement.Flags}" };
                for (int i = 2; i <= 3; i++)
                {
                    float moved = Step(host, session, player);
                    trace.Add($"#{i}: {moved:F2} yd x={player.X:F2} mounted={UnitSpeed.IsMounted(player)} {player.Movement.Flags}");
                    string all = string.Join("; ", trace);
                    Assert.False(UnitSpeed.IsMounted(player), $"step {i}: still mounted in deep water ({all})");
                    Assert.True(Math.Abs(moved - swim) <= 0.1f, $"step {i} in the water moved {moved:F3} yd in 1000 ms; swim speed is {swim:F3} ({all})");
                    Assert.True(player.Movement.HasFlag(MovementFlags.Swimming), $"step {i}: no MOVEFLAG_SWIMMING ({all})");
                }

                string key = PlayerbotAnomalies.Key("motion", "overspeed");
                Assert.False(capture.Any(key), $"unexpected {key} along the ride; reported: {string.Join(", ", capture.Notices.Select(n => n.Key))} ({string.Join("; ", trace)})");
                return true;
            });
        });

    /// <summary>A navigation mesh that is the liquid surface only (what the generator builds over deep water): every walk ends on it.</summary>
    private sealed class SurfaceOnlyMesh(float surface, bool rayAvailable = true) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        /// <summary>Whether the mesh answers a walk along its surface (false: MeshRaycastResult.Available is false, as for an unloaded tile).</summary>
        public bool RayAvailable { get; set; } = rayAvailable;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(RayAvailable, false, 1f, end with { Z = surface });
    }

    private static SpellInfo MountSpell() => new()
    {
        Id = MountSpellId,
        Name = "Test Mount",
        // Like every mount in 1.12.1 Spell.dbc: AuraInterruptFlags 0x80, AURA_INTERRUPT_UNDER_WATER_CANCELS.
        AuraInterruptFlags = SpellAuraInterruptFlags.UnderWaterCancels,
        Duration = new SpellDuration(-1, 0, -1),
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountCreature, TargetA = SpellImplicitTarget.UnitCaster },
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModIncreaseMountedSpeed, BasePoints = 59, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster },
            new(),
        ],
    };

    private static void Place(WorldTestHost host, Player player, float x) => player.Relocate(x, Y, Ground, 0f, host.World.NowMs);

    private static bool Follow(WorldTestHost host, WorldSession session, Player player, params Vector3[] points)
    {
        float length = 0;
        for (int i = 1; i < points.Length; i++) length += Vector2.Distance(new(points[i - 1].X, points[i - 1].Y), new(points[i].X, points[i].Y));
        session.ManagedBudget = new ManagedActionBudget(4);
        return PlayerbotMotion.Follow(session, player, new PlayerbotRoute(points, length, navigated: true), new PlayerbotOptions { Enabled = true }, host.World.NowMs);
    }

    /// <summary>One second passes and the motion pumps: the horizontal distance the bot moved.</summary>
    private static float Step(WorldTestHost host, WorldSession session, Player player)
    {
        Vector2 before = new(player.X, player.Y);
        PlayerbotMotion.ElapseForTests(player, 1000);
        PlayerbotMotion.Pump(session, player, host.World.NowMs);
        return Vector2.Distance(before, new(player.X, player.Y));
    }

    private static async Task RunAsync(Func<WorldTestHost, WorldSession, Task> body, float ground = Ground, IPathfinder? pathfinder = null, bool blockedSight = false)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-swim-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDir, "maps"));
        try
        {
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 32, 32)), MapTileBytes(ground, Surface));
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 31, 32)), MapTileBytes(ground, null));
            await using WorldTestHost host = WorldTestHost.Start(
                configure: options => options.Maps.DataDirectory = dataDir,
                configureServices: services => services.AddSingleton<ArcaneCore.World.Features.IWorldFeature, H1AnomalyKit.ManualClock>());
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new SwimFloor(ground, blockedSight), pathfinder: pathfinder ?? new OpenPathfinder());
                    return true;
                });
                await body(host, session);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    private sealed class SwimFloor(float z, bool blocked = false) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => !blocked;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zIn, float maxSearchDistance) => z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zIn, out ModelAreaInfo info) { info = default; return false; }
    }

    /// <summary>A one-cell map tile of flat ground, with flat water over it when <paramref name="liquidLevel"/> is set (as in PlayerbotWaterTests).</summary>
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
}
