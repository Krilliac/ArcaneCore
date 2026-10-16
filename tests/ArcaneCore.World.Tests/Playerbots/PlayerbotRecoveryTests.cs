using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotRecoveryTests
{
    [Fact]
    public async Task GhostOutsideCorpseRadius_PlansWithoutDirectResurrection()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                Corpse corpse = player.Combat.Corpse!;
                corpse.SetPosition(player.X + 50, player.Y, player.Z, 0);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 100, AllowedMaps = [0, 1] });
                session.ManagedBudget = new ManagedActionBudget(4);
                float before = MathF.Abs(player.X - corpse.X);
                Assert.True(recovery.Update(player, 1000));
                Assert.Equal(before, MathF.Abs(player.X - corpse.X));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                PlayerbotMotion.ElapseForTests(player, 1000);
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(recovery.Update(player, 1000));
                Assert.True(MathF.Abs(player.X - corpse.X) < before);
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                Assert.NotNull(player.Combat.Corpse);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task GhostAtCorpseAfterDelay_ReclaimsThroughOrdinaryHandler()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var clock = new TestDeathClock(1_000);
            await host.OnWorldAsync(() =>
            {
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                session.ManagedBudget = new ManagedActionBudget(2);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] });
                // Within the delay the bot waits, as the client's Resurrect button does (SMSG_CORPSE_RECLAIM_DELAY): nothing is sent.
                Assert.False(recovery.Update(player, 1000));
                Assert.Equal(2, session.ManagedBudget!.Remaining);
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                clock.Seconds += 31;
                Assert.True(recovery.Update(player, 1000));
                Assert.True(player.IsAlive);
                Assert.Null(player.Combat.Corpse);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The live fault of Dawnrover and Ironwander (2026-10-07): a ghost standing at its body after a second death within five minutes
    /// waits 60 s (vmangos GetCorpseReclaimDelay). At the live think interval of 100 ms the old per-think attempt cap (360) ran out after
    /// 36 s and threw "playerbot-recovery-stalled", and the fault disabled the bot. Waiting out the delay is not a stall.
    /// </summary>
    [Fact]
    public async Task GhostAtCorpse_WaitsOutAScaledReclaimDelay_AtAShortThinkInterval_ThenReclaims()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var clock = new TestDeathClock(1_000);
            await host.OnWorldAsync(() =>
            {
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                Player player = session.Player!;
                MapCombat combat = player.Map!.Combat;
                player.Health = 0;
                combat.KillPlayer(player);
                Assert.True(combat.RepopPlayer(player));
                clock.Seconds += 31;
                Assert.True(combat.TryReclaimCorpse(player));
                clock.Seconds += 5;
                player.Health = 0;
                combat.KillPlayer(player); // the second death in five minutes doubles the delay
                Assert.True(combat.RepopPlayer(player));
                uint delay = combat.GetCorpseReclaimDelay(player, pvp: false);
                Assert.True(delay >= 60, $"delay {delay}");

                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, AllowedMaps = [0, 1] });
                int thinks = 0;
                int readyAt = -1;
                while (!player.IsAlive && thinks < (int)(delay + 10) * 10)
                {
                    if (readyAt < 0 && combat.CorpseReclaimWaitSeconds(player) == 0) readyAt = thinks;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery.Update(player, 100);
                    if (++thinks % 10 == 0) clock.Seconds++;
                }

                // vmangos recomputes the delay from the recent deaths left at each try, so the wait ends at 60 s here, well past the
                // 36 s (360 thinks) where the old cap gave up; the bot reclaims on the first think the server allows it.
                Assert.True(player.IsAlive, $"still a ghost after {thinks} thinks");
                Assert.True(readyAt > 360, $"ready at think {readyAt}");
                Assert.Equal(readyAt + 1, thinks);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task RecoveryRespectsManagedActionBudget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                session.ManagedBudget = new ManagedActionBudget(0);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] });
                Assert.False(recovery.Update(player, 1000));
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Theory]
    [InlineData(true, true, PlayerbotSpiritHealerStep.DetourToHealer)]
    [InlineData(true, false, PlayerbotSpiritHealerStep.DetourToHealer)]
    [InlineData(false, true, PlayerbotSpiritHealerStep.ResumeCorpseRun)]
    [InlineData(false, false, PlayerbotSpiritHealerStep.Fault)]
    internal void DecideSpiritHealer_WhenGraveyardWalkStalls_TriesDetourBeforeCorpseOrFault(
        bool detourLeft, bool bodyRunLeft, PlayerbotSpiritHealerStep expected)
        => Assert.Equal(expected, PlayerbotRecovery.DecideSpiritHealer(
            healerKnown: false, healerInReach: false, graveyardKnown: true, stalled: true,
            bodyRunLeft: bodyRunLeft, detourLeft: detourLeft));

    /// <summary>
    /// Live w46 (2026-10-14, Maesgrer in Mulgore at (-3066.7, 10.5, 84.0) map 1): the ghost took the spirit healer instead of an unsafe
    /// body, stalled walking to graveyard 34 (205 yd away, healer out of sight), resumed the corpse run which also stalled in the same
    /// mountain pocket, and then stalled again on the spirit-healer fallback. Instead of faulting immediately with
    /// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> at the trapped coordinates, recovery relocates the ghost once to the graveyard
    /// safe loc, and if the spirit healer is 12.13 yd away (beyond <see cref="PlayerbotRecovery.ServerReachProbeYards"/>) and walking
    /// those final yards also stalls, relocates the ghost directly onto the spirit healer before ever faulting.
    /// </summary>
    [Fact]
    public async Task TrappedGhost_RelocatesToGraveyardAndSpiritHealer_BeforeGivingUp()
    {
        await using WorldTestHost host = WorldTestHost.Start(
            configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            WorldSafeLoc? graveyard = null;
            Creature? healer = null;
            PlayerbotRecovery? recovery = null;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new BlockedPathfinder());
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                player.Combat.Corpse!.SetPosition(player.X + 250f, player.Y - 700f, player.Z - 75f, 0);
                graveyard = new WorldSafeLoc(34, player.MapId, player.X + 122f, player.Y - 163f, player.Z - 18f, 0f, "Mulgore");
                recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 200, AllowedMaps = [0, 1] })
                {
                    GraveyardLookup = (_, _) => graveyard,
                };
                recovery.TakeSpiritHealer();
                return true;
            });

            bool resumedCorpseRun = false;
            bool relocatedToGraveyard = false;
            for (int think = 0; think < 200 && !relocatedToGraveyard; think++)
            {
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    resumedCorpseRun |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.ResumeCorpseRun;
                    relocatedToGraveyard = recovery.GraveyardRelocated;
                    return true;
                });
                if (!relocatedToGraveyard)
                    await host.World.AdvanceClockAsync(2_500);
            }

            Assert.True(resumedCorpseRun, "expected the ghost to try resuming the corpse run before relocating");
            Assert.True(relocatedToGraveyard, "expected the trapped ghost to relocate to the graveyard safe loc");
            await host.OnWorldAsync(() =>
            {
                Assert.True(Vector3.Distance(new(player.X, player.Y, player.Z), new(graveyard!.X, graveyard.Y, graveyard.Z)) < 0.5f);
                Assert.False(recovery!.HealerRelocated);
                // Spawn a spirit healer 12.13 yards from the graveyard safe loc (as at Mulgore graveyard 34), visible to the ghost.
                healer = Dungeon.DungeonBotHost.AddCreature(
                    player.Map!, 40565u, graveyard.X - 4.44f, graveyard.Y + 11.21f, graveyard.Z + 1.30f,
                    (uint)NpcFlags.SpiritHealer, host.World.NowMs, faction: 1);
                player.VisibleObjects.Add(healer.Guid);
                return true;
            });

            // Next tick after graveyard relocation: progress was reset so stalled is false and the ghost walks toward the healer.
            await host.World.AdvanceClockAsync(500);
            await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                recovery!.Update(player, 500);
                Assert.Equal(PlayerbotSpiritHealerStep.WalkToHealer, recovery.LastSpiritHealerStep);
                Assert.False(recovery.HealerRelocated);
                return true;
            });

            // When walking the final 12.13 yards to the healer also stalls, the ghost relocates directly onto the healer.
            bool relocatedToHealer = false;
            for (int think = 0; think < 100 && !relocatedToHealer; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    relocatedToHealer = recovery.HealerRelocated;
                    return true;
                });
            }

            Assert.True(relocatedToHealer, "expected the ghost to relocate onto the spirit healer after the 12-yard walk stalled");
            await host.OnWorldAsync(() =>
            {
                Assert.True(Vector3.Distance(new(player.X, player.Y, player.Z), new(healer!.X, healer.Y, healer.Z))
                    <= PlayerbotRecovery.SpiritHealerReachYards);
                // Remove the healer so activation cannot revive the ghost: a further stall must still end the fallback rather than
                // loop forever, without an exception (the brain quarantined and then disabled a bot for one).
                player.VisibleObjects.Remove(healer.Guid);
                return true;
            });

            InvalidOperationException? fault = null;
            bool gaveUp = false;
            for (int think = 0; think < 100 && fault is null; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                fault = await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    try
                    {
                        recovery!.Update(player, 500);
                        gaveUp |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.Fault;
                        return null;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return ex;
                    }
                });
            }

            Assert.Null(fault);
            Assert.True(gaveUp, "the fallback never gave up");
            Assert.False(player.IsAlive);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Live 2026-10-16 08:24-08:26 (Stiorou, Laeshei, Warsong Gulch map 489): the released ghost stood 0.1 yd from graveyard 769 beside the
    /// Alliance Spirit Guide, which the recovery did not count as a healer (its flag is SpiritGuide, not SpiritHealer). The "walk" to the
    /// graveyard it already stood on stalled, the ghost went back toward its body 280 yd away, stalled there too and threw
    /// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> from PlayerbotBattlegroundAI.Play. A battleground ghost waits within the guide's
    /// 20-yard wave instead (cmangos battlegrounds.cpp npc_spirit_guideAI), queued for it (CMSG_AREA_SPIRIT_HEALER_QUEUE), for good.
    /// </summary>
    [Fact]
    public async Task BattlegroundGhost_WaitsAtSpiritGuideForTheWave_NeitherRunsToBodyNorFaults()
    {
        await using WorldTestHost host = WorldTestHost.Start(
            configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            Creature? guide = null;
            PlayerbotRecovery? recovery = null;
            int queued = 0;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                player.Combat.Corpse!.SetPosition(player.X + 280f, player.Y + 16f, player.Z, 0);
                var graveyard = new WorldSafeLoc(769, player.MapId, player.X, player.Y, player.Z, 0f, "WSG Alliance graveyard");
                guide = Dungeon.DungeonBotHost.AddCreature(player.Map!, 13116u, player.X - 8f, player.Y, player.Z,
                    (uint)NpcFlags.SpiritGuide, host.World.NowMs, faction: 1);
                player.VisibleObjects.Add(guide.Guid);
                recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 400, AllowedMaps = [0, 1] })
                {
                    GraveyardLookup = (_, _) => graveyard,
                    AreaSpiritGuides = _ => true,
                };
                session.ManagedDispatchObserver = op => queued += op == WorldOpcode.CmsgAreaSpiritHealerQueue ? 1 : 0;
                return true;
            });

            float farthestFromGuide = 0f;
            Exception? fault = null;
            for (int think = 0; think < 80 && fault is null; think++)
            {
                fault = await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    try
                    {
                        recovery!.Update(player, 500);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return ex;
                    }

                    PlayerbotMotion.ElapseForTests(player, 500);
                    farthestFromGuide = MathF.Max(farthestFromGuide,
                        Vector3.Distance(new(player.X, player.Y, player.Z), new(guide!.X, guide.Y, guide.Z)));
                    return (Exception?)null;
                });
                await host.World.AdvanceClockAsync(2_500);
            }

            Assert.Null(fault);
            await host.OnWorldAsync(() =>
            {
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                Assert.True(farthestFromGuide <= 20f, $"the ghost left the spirit guide's wave: {farthestFromGuide:F1} yd away at most");
                Assert.Equal(PlayerbotSpiritHealerStep.AwaitAreaResurrection, recovery!.LastSpiritHealerStep);
                Assert.True(queued >= 1, "the ghost never queued with the spirit guide (CMSG_AREA_SPIRIT_HEALER_QUEUE)");
                Assert.Equal(queued, recovery.AreaQueueRequests);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A battleground ghost with no spirit guide in sight whose every walk stalls (graveyard, body) is reported once and waits for the
    /// match; it never throws <see cref="PlayerbotRecovery.SpiritHealerFailed"/> (the world's fallback still does, see
    /// <see cref="TrappedGhost_RelocatesToGraveyardAndSpiritHealer_BeforeFaulting"/>).
    /// </summary>
    [Fact]
    public async Task BattlegroundGhost_WithNoGuideAndEveryWalkStalled_WaitsInsteadOfFaulting()
    {
        await using WorldTestHost host = WorldTestHost.Start(
            configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            PlayerbotRecovery? recovery = null;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new BlockedPathfinder());
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                player.Combat.Corpse!.SetPosition(player.X + 280f, player.Y + 16f, player.Z, 0);
                var graveyard = new WorldSafeLoc(769, player.MapId, player.X + 0.1f, player.Y, player.Z, 0f, "WSG Alliance graveyard");
                recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 400, AllowedMaps = [0, 1] })
                {
                    GraveyardLookup = (_, _) => graveyard,
                    AreaSpiritGuides = _ => true,
                };
                return true;
            });

            Exception? fault = null;
            bool reachedFault = false;
            for (int think = 0; think < 150 && fault is null; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                fault = await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    try
                    {
                        recovery!.Update(player, 500);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return ex;
                    }

                    reachedFault |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.Fault;
                    return (Exception?)null;
                });
            }

            Assert.Null(fault);
            Assert.True(reachedFault, "the stalled battleground ghost never reached the fallback's end, so the test proved nothing");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task SubmergedCorpseNearDryShoreline_ReclaimsOnDryShoreWithoutSpiritHealer()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-shoreline-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDir, "maps"));
        try
        {
            // Tile (32, 32) covers x in (-533.33, 0], y in (-533.33, 0]: water at level 88 over ground at 83.0 (submerged).
            // Tile (31, 32) covers x in (0, +533.33], y in (-533.33, 0]: dry shoreline at height 83.53 with LiquidStatus.NoWater.
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 32, 32)), BuildMapTileBytes(groundHeight: 83.0f, liquidLevel: 88f));
            File.WriteAllBytes(Path.Combine(dataDir, "maps", TerrainTile.FileName(0, 31, 32)), BuildMapTileBytes(groundHeight: 83.53f, liquidLevel: null));

            await using WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = dataDir);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                var clock = new TestDeathClock(1_000);
                await host.OnWorldAsync(() =>
                {
                    DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                    Player player = session.Player!;
                    player.Level = 14;
                    player.Relocate(-10f, -30f, 83.0f, 0f, host.World.NowMs);
                    player.Health = 0;
                    player.Map!.Combat.KillPlayer(player);
                    Assert.True(player.Map!.Combat.RepopPlayer(player));

                    Corpse corpse = player.Combat.Corpse!;
                    Assert.True(PlayerbotWater.Submerged(player.Map!, new Vector3(corpse.X, corpse.Y, corpse.Z), player.Locomotion.CollisionHeight));

                    var healer = new Creature(
                        999_801,
                        new ArcaneCore.Kernel.WorldData.Creatures.CreatureTemplate
                        {
                            Entry = 6491,
                            Name = "Spirit Healer",
                            MinLevel = 60,
                            MaxLevel = 60,
                            MinLevelHealth = 1000,
                            MaxLevelHealth = 1000,
                            Faction = 35,
                            NpcFlags = (uint)NpcFlags.SpiritHealer,
                        },
                        null,
                        ArcaneCore.Kernel.WorldData.Creatures.CreatureContent.Empty,
                        new Random(1));
                    healer.Relocate(-50f, -30f, 83.53f, 0f, host.World.NowMs);
                    player.Map!.AddObject(healer);
                    player.VisibleObjects.Add(healer.Guid);

                    clock.Seconds += 31;
                    var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 100, AllowedMaps = [0, 1] })
                    {
                        UnsafeBody = (p, c) => (
                            PlayerbotRecovery.JudgeBody(
                                false,
                                false,
                                false,
                                false,
                                bodySubmerged: PlayerbotWater.Submerged(p.Map!, new Vector3(c.X, c.Y, c.Z), p.Locomotion.CollisionHeight)),
                            "submerged"),
                    };

                    Assert.True(recovery.CanReclaimSubmergedFromDryShore(player, corpse));

                    session.ManagedBudget = new ManagedActionBudget(4);
                    Assert.True(recovery.Update(player, 500));
                    Assert.False(recovery.UsingSpiritHealer);
                    Assert.False(recovery.SpiritHealerChosen);
                    Assert.Equal(PlayerbotRecoveryStep.WalkToReviveSpot, recovery.LastStep);
                    Assert.NotNull(recovery.ReviveSpot);
                    Assert.True(PlayerbotRecovery.IsDryShore(player.Map, recovery.ReviveSpot!.Value));

                    for (int step = 0; step < 30 && !player.IsAlive; step++)
                    {
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(4);
                        recovery.Update(player, 500);
                    }

                    Assert.True(player.IsAlive);
                    Assert.Null(player.Combat.Corpse);
                    Assert.False(recovery.UsingSpiritHealer);
                    Assert.True(PlayerbotRecovery.IsDryShore(player.Map, new Vector3(player.X, player.Y, player.Z)));
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

    private static byte[] BuildMapTileBytes(float groundHeight, float? liquidLevel)
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

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class BlockedPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
