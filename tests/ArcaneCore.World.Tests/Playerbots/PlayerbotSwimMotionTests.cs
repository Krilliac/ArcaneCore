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

    private static async Task RunAsync(Func<WorldTestHost, WorldSession, Task> body)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-swim-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDir, "maps"));
        try
        {
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 32, 32)), MapTileBytes(Ground, Surface));
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 31, 32)), MapTileBytes(Ground, null));
            await using WorldTestHost host = WorldTestHost.Start(
                configure: options => options.Maps.DataDirectory = dataDir,
                configureServices: services => services.AddSingleton<ArcaneCore.World.Features.IWorldFeature, H1AnomalyKit.ManualClock>());
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new SwimFloor(Ground), pathfinder: new OpenPathfinder());
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

    private sealed class SwimFloor(float z) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
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
