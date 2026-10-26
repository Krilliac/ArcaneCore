using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Risk;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// H1 outcome checks of the bot's fight, risk and gear (areas <c>combat</c>, <c>risk</c>, <c>gear</c>). Each check is proved both ways:
/// the failing scenario reports its key, the correct one stays silent.
/// </summary>
public sealed class PlayerbotCombatAnomalyTests
{
    private const uint FoodSpell = 993001;
    private const uint FoodItem = 993101;

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private static string Key(string area, string code) => PlayerbotAnomalies.Key(area, code);

    // --- combat/cast-refused-repeatedly ---------------------------------------------------------------------------------

    [Fact]
    public async Task ASpellRefusedTenTimesInARow_IsReported_NineAreNot_AndAnAcceptanceStartsOver()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellInfo bolt = session.Services.GetRequiredService<SpellFeature>().System.Store.Get(SpellTestServices.Bolt)!;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

                var nine = new PlayerbotCombatWatch();
                for (int i = 0; i < PlayerbotCombatWatch.RefusalLimit - 1; i++) nine.Refused(player, bolt, (uint)(1000 + i * 4000));
                Assert.False(cap.Any(Key("combat", "cast-refused-repeatedly")), cap.Describe());

                var interrupted = new PlayerbotCombatWatch();
                for (int i = 0; i < PlayerbotCombatWatch.RefusalLimit - 1; i++) interrupted.Refused(player, bolt, (uint)(1000 + i * 4000));
                interrupted.Accepted(bolt);
                for (int i = 0; i < PlayerbotCombatWatch.RefusalLimit - 1; i++) interrupted.Refused(player, bolt, (uint)(100_000 + i * 4000));
                Assert.False(cap.Any(Key("combat", "cast-refused-repeatedly")), "an acceptance must start the count over: " + cap.Describe());

                var stale = new PlayerbotCombatWatch();
                for (int i = 0; i < PlayerbotCombatWatch.RefusalLimit - 1; i++) stale.Refused(player, bolt, (uint)(1000 + i * 4000));
                stale.Refused(player, bolt, 1000 + 9 * 4000 + PlayerbotCombatWatch.RefusalStaleMs + 1);
                Assert.False(cap.Any(Key("combat", "cast-refused-repeatedly")), "a streak older than the stale limit is over: " + cap.Describe());

                var ten = new PlayerbotCombatWatch();
                for (int i = 0; i < PlayerbotCombatWatch.RefusalLimit; i++) ten.Refused(player, bolt, (uint)(1000 + i * 4000));
                Assert.Equal(1, cap.Count(Key("combat", "cast-refused-repeatedly")));
                ten.Refused(player, bolt, 200_000); // still the same episode: reported once
                Assert.Equal(1, cap.Count(Key("combat", "cast-refused-repeatedly")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The wiring: a bot whose only spell the server refuses (no mana) keeps submitting it and the refusals are counted by the real Update.</summary>
    [Fact]
    public async Task ASpellTheServerRefusesEveryThink_IsReportedThroughUpdate_AndAWorkingSpellIsNot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 3, 61);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
                var helper = new PlayerbotCombatSpells(session);

                // Refused every think: the same spell at a price the bot cannot pay.
                feature.System.Store = new SpellStore(feature.System.Store.All.Select(s => s.Id == bolt.Id ? bolt with { ManaCost = 1_000_000 } : s), [], []);
                for (int i = 0; i < PlayerbotCombatWatch.RefusalLimit + 2; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(2);
                    Assert.False(helper.Update(player, target, 1000), $"think {i}");
                }

                Assert.True(cap.Any(Key("combat", "cast-refused-repeatedly")), cap.Describe());
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AWorkingSpell_IsAcceptedAndNothingIsReported()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 3, 63);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
                var helper = new PlayerbotCombatSpells(session);
                session.ManagedBudget = new ManagedActionBudget(2);
                Assert.True(helper.Update(player, target, 0));
                Assert.Empty(cap.OfSubsystem(PlayerbotAnomalies.Subsystem("combat")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- combat/cast-stuck -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACastStillRunningLongAfterItsTime_IsReported_AFreshCastIsNot()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 62);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var helper = new PlayerbotCombatSpells(session);
                session.ManagedBudget = new ManagedActionBudget(2);
                Assert.True(helper.Update(player, target, 0));
                SpellCast cast = session.Services.GetRequiredService<SpellFeature>().System.GetState(player.Guid)!.CurrentCast
                    ?? throw new InvalidOperationException("no cast in progress");
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

                var watch = new PlayerbotCombatWatch();
                watch.Casting(player, cast, 1000);
                watch.Casting(player, cast, 1000 + (uint)cast.CastTime + 2000);
                Assert.False(cap.Any(Key("combat", "cast-stuck")), "a cast inside its time is fine: " + cap.Describe());

                watch.NotCasting();
                watch.Casting(player, cast, 100_000);
                watch.Casting(player, cast, 100_000 + 5000);
                Assert.False(cap.Any(Key("combat", "cast-stuck")), "a cast that ended and began again starts over: " + cap.Describe());

                watch.Casting(player, cast, 100_000 + (uint)cast.CastTime + (uint)Math.Max(0, cast.Duration) + PlayerbotCombatWatch.StuckSlackMs + 1);
                Assert.Equal(1, cap.Count(Key("combat", "cast-stuck")));
                watch.Casting(player, cast, 300_000);
                Assert.Equal(1, cap.Count(Key("combat", "cast-stuck")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ACastWithPushback_IsNotReportedUntilThePushbackAllowanceExpires()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 62);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                feature.System.Store = new SpellStore(
                    feature.System.Store.All.Select(s => s.Id == bolt.Id ? bolt with { InterruptFlags = bolt.InterruptFlags | SpellInterruptFlags.DamagePushback } : s),
                    [],
                    []);
                var helper = new PlayerbotCombatSpells(session);
                session.ManagedBudget = new ManagedActionBudget(2);
                Assert.True(helper.Update(player, target, 0));
                SpellSystem spells = feature.System;
                SpellCast cast = spells.GetState(player.Guid)!.CurrentCast
                    ?? throw new InvalidOperationException("no cast in progress");
                spells.OnDamageTaken(player, target, 10, periodic: false);
                spells.OnDamageTaken(player, target, 10, periodic: false);
                Assert.Equal(2, cast.PushbackCount);
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

                var watch = new PlayerbotCombatWatch();
                watch.Casting(player, cast, 1000);
                uint unpushedDeadline = 1000 + (uint)cast.CastTime + (uint)Math.Max(0, cast.Duration) + PlayerbotCombatWatch.StuckSlackMs;
                watch.Casting(player, cast, unpushedDeadline + 1500);
                Assert.False(cap.Any(Key("combat", "cast-stuck")), "pushback allowance (2000 ms) must extend the cast-stuck deadline: " + cap.Describe());

                watch.Casting(player, cast, unpushedDeadline + 2001);
                Assert.Equal(1, cap.Count(Key("combat", "cast-stuck")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- combat/out-of-mana-loop -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ACasterBelowTenPercentManaFor45Seconds_IsReported_NotBefore_NotAfterARefill_NotAWarrior()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

                // A warrior (rage) is never out of mana.
                var warrior = new PlayerbotCombatWatch();
                for (uint t = 1000; t < 200_000; t += 10_000) warrior.Fight(player, t);
                Assert.False(cap.Any(Key("combat", "out-of-mana-loop")));

                player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 10_000);
                SpellSystem.SetPower(player, PowerType.Mana, 10);

                var watch = new PlayerbotCombatWatch();
                watch.Fight(player, 1000);
                watch.Fight(player, 20_000);
                watch.Fight(player, 1000 + PlayerbotCombatWatch.LowManaMs - 1);
                Assert.False(cap.Any(Key("combat", "out-of-mana-loop")), "not yet 45 s: " + cap.Describe());

                SpellSystem.SetPower(player, PowerType.Mana, 5000); // a refill (potion, drink) ends the streak
                watch.Fight(player, 50_000);
                SpellSystem.SetPower(player, PowerType.Mana, 10);
                watch.Fight(player, 60_000);
                watch.Fight(player, 80_000);
                watch.Fight(player, 100_000);
                watch.Fight(player, 60_000 + PlayerbotCombatWatch.LowManaMs - 1);
                Assert.False(cap.Any(Key("combat", "out-of-mana-loop")), "the streak started over after the refill: " + cap.Describe());

                // A gap between fight decisions longer than a fight is not one streak.
                var gap = new PlayerbotCombatWatch();
                gap.Fight(player, 1000);
                gap.Fight(player, 1000 + PlayerbotCombatWatch.FightGapMs + 1);
                gap.Fight(player, 1000 + PlayerbotCombatWatch.FightGapMs + 20_000);
                Assert.False(cap.Any(Key("combat", "out-of-mana-loop")), cap.Describe());

                watch.Fight(player, 60_000 + PlayerbotCombatWatch.LowManaMs);
                Assert.Equal(1, cap.Count(Key("combat", "out-of-mana-loop")));
                watch.Fight(player, 120_000);
                Assert.Equal(1, cap.Count(Key("combat", "out-of-mana-loop")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task OutOfCombatPullWalkAndBetweenFightUpkeep_ResetLowManaWatch_AcrossBackToBackFights()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 64);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 1000);
                SpellSystem.SetPower(player, PowerType.Mana, 10);
                player.Map!.Combat.SetInCombatState(player, 60_000);
                return true;
            });
            using AnomalyCapture cap = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(session.Player!));
            try
            {
                var helper = new PlayerbotCombatSpells(session);

                // Fight 1: 25 s in combat below 10% mana.
                await host.World.AdvanceClockAsync(1_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!, target, 1000); return true; });
                await host.World.AdvanceClockAsync(25_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!, target, 1000); return true; });

                // Combat drops between pulls; out-of-combat upkeep and pre-combat pull walk run within FightGapMs (30 s).
                await host.OnWorldAsync(() =>
                {
                    session.Services.GetRequiredService<SpellFeature>().System.CancelCast(session.Player!, 0);
                    session.Player!.Map!.Combat.CombatStop(target);
                    session.Player!.Map!.Combat.CombatStop(session.Player!);
                    Assert.False(session.Player!.Combat.IsInCombat);
                    return true;
                });
                await host.World.AdvanceClockAsync(1_000);
                await host.OnWorldAsync(() =>
                {
                    Assert.False(session.Player!.Combat.IsInCombat);
                    helper.UpdateOutOfCombat(session.Player!, 1000);
                    return true;
                });
                await host.World.AdvanceClockAsync(1_000);
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(0);
                    helper.Update(session.Player!, target, 1000);
                    return true;
                });

                // Fight 2: another 25 s in combat below 10% mana (total 50 s > 45 s, but across two separate fights).
                await host.OnWorldAsync(() => { session.Player!.Map!.Combat.SetInCombatState(session.Player!, 60_000); return true; });
                await host.World.AdvanceClockAsync(1_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!, target, 1000); return true; });
                await host.World.AdvanceClockAsync(25_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!, target, 1000); return true; });

                Assert.False(cap.Any(Key("combat", "out-of-mana-loop")), cap.Describe());
            }
            finally { cap.Dispose(); }
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task CasterBelowTenPercentManaForTenSecondsInCombat_WithholdsOffensiveManaSpells_UntilManaRecoversAboveTenPercent()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 65);
            var helper = new PlayerbotCombatSpells(session);
            SpellInfo costlyBolt = null!;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                feature.System.Store = new SpellStore(feature.System.Store.All.Select(s =>
                    s.Id == bolt.Id ? bolt with { ManaCost = 30, PowerType = (int)PowerType.Mana } : s), [], []);
                costlyBolt = feature.System.Store.Get(SpellTestServices.Bolt)!;

                player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 1000);
                player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
                player.Health = 800; // > 40% HP
                SpellSystem.SetPower(player, PowerType.Mana, 50); // 5% mana (< 10%), but enough to afford ManaCost=30
                player.Map!.Combat.SetInCombatState(player, 120_000);
                return true;
            });

            await host.World.AdvanceClockAsync(1_000);
            await host.OnWorldAsync(() =>
            {
                helper.Update(session.Player!, target, 1000);
                session.Services.GetRequiredService<SpellFeature>().System.CancelCast(session.Player!, 0);
                SpellSystem.SetPower(session.Player!, PowerType.Mana, 50);
                return true;
            });

            // After >= 10 s of continuous < 10% mana in combat, offensive mana spells are withheld so Spirit regen can recover above 10%.
            await host.World.AdvanceClockAsync(11_000);
            await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                Assert.False(helper.Update(session.Player!, target, 1000));
                Assert.False(helper.CanCastNow(session.Player!, costlyBolt, target));
                SpellSystem.SetPower(session.Player!, PowerType.Mana, 120);
                return true;
            });

            // Once mana recovers to >= 10% (120/1000), the next combat think clears the low-mana hold and allows casting again.
            await host.World.AdvanceClockAsync(2_000);
            await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                Assert.True(helper.Update(session.Player!, target, 1000));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- combat/phantom-combat ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACombatFlagNothingHoldsUp_IsReportedAfter45Seconds_AFlagWithAnAttackerIsNot()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993201, health: 5000, minDamage: 0, maxDamage: 0), 3, 0));
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

            // Flagged, but a living creature attacks the bot: a real fight, never reported.
            world.Engage(mob);
            Assert.True(player.Combat.IsInCombat);
            var fighting = new PlayerbotPhantomCombatWatch();
            fighting.Look(player, 1000);
            fighting.Look(player, 1000 + PlayerbotPhantomCombatWatch.PhantomMs + 10_000);
            Assert.False(cap.Any(Key("combat", "phantom-combat")), cap.Describe());

            // Flagged with nothing behind it.
            player.Map!.Combat.CombatStop(player);
            world.Creatures.Despawn(mob);
            player.Map!.Combat.SetInCombatState(player, 3_600_000);
            Assert.True(player.Combat.IsInCombat);
            Assert.Empty(player.Combat.Attackers);
            var phantom = new PlayerbotPhantomCombatWatch();
            phantom.Look(player, 1000);
            phantom.Look(player, 1000 + PlayerbotPhantomCombatWatch.PhantomMs - 1);
            Assert.False(cap.Any(Key("combat", "phantom-combat")), "not yet 45 s: " + cap.Describe());

            // The flag drops and comes back: the clock starts over.
            player.Map!.Combat.CombatStop(player);
            phantom.Look(player, 40_000);
            player.Map!.Combat.SetInCombatState(player, 3_600_000);
            phantom.Look(player, 50_000);
            phantom.Look(player, 50_000 + PlayerbotPhantomCombatWatch.PhantomMs - 1);
            Assert.False(cap.Any(Key("combat", "phantom-combat")), cap.Describe());

            phantom.Look(player, 50_000 + PlayerbotPhantomCombatWatch.PhantomMs);
            Assert.Equal(1, cap.Count(Key("combat", "phantom-combat")));
            phantom.Look(player, 500_000);
            Assert.Equal(1, cap.Count(Key("combat", "phantom-combat")));
            return true;
        });
    }

    /// <summary>The wiring: the risk's per-think <see cref="PlayerbotRisk.Track"/> feeds the watch with the world clock.</summary>
    [Fact]
    public async Task TheRisksTrack_ReportsAPhantomCombatFlagOnTheWorldClock()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        AnomalyCapture cap = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        try
        {
            await world.OnWorldAsync(() =>
            {
                world.Player.Map!.Combat.SetInCombatState(world.Player, 3_600_000);
                world.Brain.Risk.Track(world.Player);
                return true;
            });
            await world.Host.World.AdvanceClockAsync(PlayerbotPhantomCombatWatch.PhantomMs + 2_000);
            await world.OnWorldAsync(() =>
            {
                Assert.True(world.Player.Combat.IsInCombat, "the combat flag expired by itself, so this test proves nothing");
                world.Brain.Risk.Track(world.Player);
                return true;
            });
            Assert.True(cap.Any(Key("combat", "phantom-combat")), cap.Describe());
        }
        finally { cap.Dispose(); }
    }

    [Fact]
    public async Task ACombatFlagResetEveryFewSeconds_IsNotReportedAsPhantomCombat()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

            var watch = new PlayerbotPhantomCombatWatch();
            // The flag is re-set every 3 seconds for 60 seconds (exceeding PhantomMs 45 s total elapsed).
            for (uint nowMs = 1000; nowMs <= 60_000; nowMs += 3000)
            {
                player.Map!.Combat.SetInCombatState(player, 3_600_000);
                watch.Look(player, nowMs);
            }

            Assert.False(cap.Any(Key("combat", "phantom-combat")), cap.Describe());
            return true;
        });
    }

    [Fact]
    public async Task ASingleCombatSet_PastThresholdWithNoAttacker_IsReportedAndNamesSetter()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);

            var watch = new PlayerbotPhantomCombatWatch();
            player.Map!.Combat.SetInCombatState(player, 3_600_000, caller: "AssistPropagation", via: "SetInCombatWithAssisted");

            watch.Look(player, 1000);
            watch.Look(player, 1000 + PlayerbotPhantomCombatWatch.PhantomMs - 1);
            Assert.False(cap.Any(Key("combat", "phantom-combat")), "not yet at threshold: " + cap.Describe());

            watch.Look(player, 1000 + PlayerbotPhantomCombatWatch.PhantomMs);
            Assert.True(cap.Any(Key("combat", "phantom-combat")), "must report once threshold reached: " + cap.Describe());

            AnomalyNotice notice = Assert.Single(cap.Notices, n => n.Key == Key("combat", "phantom-combat"));
            Assert.Contains("AssistPropagation", notice.Detail);
            Assert.Contains("SetInCombatWithAssisted", notice.Detail);
            return true;
        });
    }

    // --- risk/retreat-loop ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThreeRetreatsInThreeMinutes_AreALoop_ThreeSpreadOutAreNot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
                var two = new PlayerbotRetreatLoopWatch();
                two.Started(player, 1000, "losing");
                two.Started(player, 20_000, "losing");
                var spread = new PlayerbotRetreatLoopWatch();
                spread.Started(player, 1000, "losing");
                spread.Started(player, 100_000, "losing");
                spread.Started(player, 1000 + PlayerbotRetreatLoopWatch.WindowMs + 1, "losing");
                Assert.False(cap.Any(Key("risk", "retreat-loop")), cap.Describe());

                var loop = new PlayerbotRetreatLoopWatch();
                loop.Started(player, 1000, "losing");
                loop.Started(player, 60_000, "losing");
                loop.Started(player, 120_000, "losing");
                Assert.Equal(1, cap.Count(Key("risk", "retreat-loop")));
                loop.Started(player, 130_000, "losing");
                Assert.Equal(1, cap.Count(Key("risk", "retreat-loop")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- risk/retreat-cornered, risk/retreat-died --------------------------------------------------------------------------

    [Fact]
    public async Task ARetreatThatEndsWithTheBotStillThreatened_IsReported_OneThatGotAwayIsNot()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993211, health: 5000, minDamage: 0, maxDamage: 0), 2, 0));
        await world.SeeAsync(mob);
        AnomalyCapture cap = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        try
        {
            // Got away: nothing threatens the bot when the retreat looks.
            await world.OnWorldAsync(() =>
            {
                // A minute of blows must not end the bot: this test is about the retreat's end, not its death.
                world.Player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 50_000_000);
                world.Player.Health = 50_000_000;
                world.Engage(mob);
                world.Brain.Risk.StartRetreat(world.Player, [mob], "test");
                world.Player.Map!.Combat.DealDamage(world.Player, mob, mob.Health, direct: false);
                Assert.False(world.Brain.Risk.UpdateRetreat(world.Player, 100));
                Assert.Equal("safe", world.Brain.Risk.Retreat.Outcome);
                return true;
            });
            Assert.False(cap.Any(Key("risk", "retreat-cornered")), cap.Describe());

            // Cornered: the sixty seconds pass and the creature still has the bot.
            Creature second = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993212, health: 5000, minDamage: 0, maxDamage: 0), 2, 1));
            await world.SeeAsync(second);
            await world.OnWorldAsync(() =>
            {
                world.Engage(second);
                world.Brain.Risk.StartRetreat(world.Player, [second], "test");
                return true;
            });
            await world.Host.World.AdvanceClockAsync(PlayerbotRetreat.MaxMs + 1_000);
            if (!await world.OnWorldAsync(() => PlayerbotRetreat.Threatened(world.Player)))
            {
                // The creature gave up and evaded in the minute: another one has the bot, as a pursuer that never let go.
                Creature third = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993213, health: 5000, minDamage: 0, maxDamage: 0), 2, -1));
                await world.SeeAsync(third);
                await world.OnWorldAsync(() => { world.Engage(third); return true; });
            }

            await world.OnWorldAsync(() =>
            {
                Assert.True(PlayerbotRetreat.Threatened(world.Player), "the creature let go, so this test proves nothing");
                Assert.False(world.Brain.Risk.UpdateRetreat(world.Player, 100));
                Assert.Equal("cornered", world.Brain.Risk.Retreat.Outcome);
                return true;
            });
            Assert.Equal(1, cap.Count(Key("risk", "retreat-cornered")));
        }
        finally { cap.Dispose(); }
    }

    public enum DeathOrder { OnDeathFirst, UpdateFirst }

    [Theory]
    [InlineData(DeathOrder.OnDeathFirst)]
    [InlineData(DeathOrder.UpdateFirst)]
    public async Task ADeathDuringARetreat_IsReportedOnce_AndIsNotKnownDanger_WhicheverCallSeesItFirst(DeathOrder order)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993221, health: 400, minDamage: 5, maxDamage: 8), 2, 0));
        await world.SeeAsync(mob);
        AnomalyCapture cap = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        try
        {
            await world.OnWorldAsync(() =>
            {
                PlayerbotRisk risk = world.Brain.Risk;
                world.Engage(mob);
                risk.StartRetreat(world.Player, [mob], "test");
                world.Player.Map!.Combat.DealDamage(mob, world.Player, world.Player.Health, direct: false);
                Assert.False(world.Player.IsAlive);
                if (order == DeathOrder.UpdateFirst) Assert.False(risk.UpdateRetreat(world.Player, 100));
                risk.OnDeath(world.Player, [(mob.Guid, mob.Entry)]);
                return true;
            });
            Assert.Equal(1, cap.Count(Key("risk", "retreat-died")));
            Assert.False(cap.Any(Key("risk", "died-in-known-hazard")), "the retreat's own memory is not older knowledge: " + cap.Describe());
        }
        finally { cap.Dispose(); }
    }

    // --- risk/died-in-known-hazard -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABotKilledByACreatureItRemembered_IsReported_ByAnUnknownOneIsNot()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993231, health: 400, minDamage: 5, maxDamage: 8), 2, 0));
        await world.SeeAsync(mob);
        AnomalyCapture cap = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        try
        {
            // Unknown killer, no hazards: silent.
            await world.OnWorldAsync(() =>
            {
                world.Engage(mob);
                world.Player.Map!.Combat.DealDamage(mob, world.Player, world.Player.Health, direct: false);
                world.Brain.Risk.OnDeath(world.Player, [(mob.Guid, mob.Entry)]);
                return true;
            });
            Assert.False(cap.Any(Key("risk", "died-in-known-hazard")), cap.Describe());

            // The death just recorded put the killer in the danger memory (and a death mark under the body). Died again to it: known.
            await world.OnWorldAsync(() =>
            {
                world.Player.Relocate(world.Player.X, world.Player.Y + 200, world.Player.Z, 0, world.Host.World.NowMs);
                world.Brain.Risk.OnDeath(world.Player, [(mob.Guid, mob.Entry)]);
                return true;
            });
            Assert.Equal(1, cap.Count(Key("risk", "died-in-known-hazard")));
        }
        finally { cap.Dispose(); }
    }

    [Fact]
    public async Task ABotKilledInsideALethalCreatureHazard_IsReported_InsideAPlaceMarkOrAStrongHazardItIsNot()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        AnomalyCapture cap = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        try
        {
            await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                PlayerbotRisk risk = world.Brain.Risk;
                var here = new Vector3(player.X, player.Y, player.Z);
                uint now = world.Host.World.NowMs;
                // A reclaim at the body (place marks) and a pull of a strong creature are by design.
                risk.Hazards.Add(player.MapId, here, 30f, now, 600, "death");
                risk.Hazards.SetStrong(player.MapId, here, 30f, now, 60_000, new ObjectGuid(0xF130000000000001UL));
                risk.OnDeath(player);
                return true;
            });
            Assert.False(cap.Any(Key("risk", "died-in-known-hazard")), cap.Describe());

            await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                var here = new Vector3(player.X, player.Y, player.Z);
                world.Brain.Risk.Hazards.Add(player.MapId, here, 30f, world.Host.World.NowMs, 600, "lethal", new ObjectGuid(0xF130000000000002UL));
                world.Brain.Risk.OnDeath(player);
                return true;
            });
            Assert.Equal(1, cap.Count(Key("risk", "died-in-known-hazard")));
        }
        finally { cap.Dispose(); }
    }

    // --- gear/equip-* --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEquipThatLeftTheItemInTheBag_IsReported_AWornUpgradeInItsSlotIsNot_AWrongSlotIs()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(40, InventoryType.Feet, 70));
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(2967, InventoryType.Feet, 90, requiredLevel: 5));
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(2968, InventoryType.Head, 90, requiredLevel: 5));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 5;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
                PlayerbotEquipmentTests.EquipFromBags(player, 40);
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(2967, 1, out _));
                var helper = new PlayerbotEquipment(session);

                // The real upgrade through the real handlers: worn in its slot, nothing reported.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(helper.Update(player));
                Assert.Equal(2967u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                Assert.Empty(cap.OfSubsystem(PlayerbotAnomalies.Subsystem("gear")));

                // Chosen for the feet, but the item is still in the bags (the old boots went back there).
                Item old = player.Inventory.AllItems.Single(item => item.Entry == 40);
                helper.CheckEquipOutcome(player, old, InventorySlots.Feet);
                Assert.Equal(1, cap.Count(Key("gear", "equip-not-applied")));

                // Worn in the feet slot but the choice said the head.
                Item boots = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!;
                helper.CheckEquipOutcome(player, boots, InventorySlots.Head);
                Assert.Equal(1, cap.Count(Key("gear", "equip-wrong-slot")));
                helper.CheckEquipOutcome(player, boots, InventorySlots.NullSlot); // a bag was meant, boots sit in the feet slot
                Assert.Equal(2, cap.Count(Key("gear", "equip-wrong-slot")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TheSameItemWornThreeTimesInTenMinutes_IsASwapLoop_TwoIsNot()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(40, InventoryType.Feet, 70));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using AnomalyCapture cap = PlayerbotAnomalies.CaptureFor(player);
                PlayerbotEquipmentTests.EquipFromBags(player, 40);
                Item boots = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!;
                var helper = new PlayerbotEquipment(session);
                helper.CheckEquipOutcome(player, boots, InventorySlots.Feet);
                helper.CheckEquipOutcome(player, boots, InventorySlots.Feet);
                Assert.False(cap.Any(Key("gear", "equip-flapping")), cap.Describe());
                helper.CheckEquipOutcome(player, boots, InventorySlots.Feet);
                Assert.Equal(1, cap.Count(Key("gear", "equip-flapping")));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- gear/broken-unrepaired -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WornGearBrokenForTwentyMinutes_IsReported_RepairedOrBrieflyBrokenIsNot()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(40, InventoryType.Feet, 0) with { MaxDurability = 10 });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            AnomalyCapture cap = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(session.Player!));
            try
            {
                var helper = new PlayerbotEquipment(session);
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotEquipmentTests.EquipFromBags(player, 40);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    session.ManagedBudget = new ManagedActionBudget(0);
                    helper.Update(player);
                    return true;
                });

                await host.World.AdvanceClockAsync(PlayerbotEquipment.BrokenGearMs - 60_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!); return true; });
                Assert.False(cap.Any(Key("gear", "broken-unrepaired")), "not yet 20 minutes: " + cap.Describe());

                // Repaired: the next look stops the clock; broken again later it starts over.
                await host.OnWorldAsync(() => { session.Player!.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 10; return true; });
                await host.World.AdvanceClockAsync(PlayerbotEquipment.BrokenLookMs + 1_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!); return true; });
                await host.OnWorldAsync(() => { session.Player!.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0; return true; });
                await host.World.AdvanceClockAsync(PlayerbotEquipment.BrokenLookMs + 1_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!); return true; });
                await host.World.AdvanceClockAsync(PlayerbotEquipment.BrokenGearMs - 120_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!); return true; });
                Assert.False(cap.Any(Key("gear", "broken-unrepaired")), "the repair started the clock over: " + cap.Describe());

                await host.World.AdvanceClockAsync(130_000);
                await host.OnWorldAsync(() => { helper.Update(session.Player!); helper.Update(session.Player!); return true; });
                Assert.Equal(1, cap.Count(Key("gear", "broken-unrepaired")));
            }
            finally { cap.Dispose(); }
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- gear/consumable-* ------------------------------------------------------------------------------------------------------

    private static WorldTestHost StartWithFood(out ItemTestContent items)
    {
        items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = FoodItem, Name = "food", Class = 0, InventoryType = 0, Stackable = 20,
            Spells = [new ItemSpell(FoodSpell, 0, -1, 0, 0, 0, 0)],
        });
        using (items.Use())
        {
            return WorldTestHost.Start();
        }
    }

    private static void InstallFood(WorldTestHost host)
        => host.WorldServices.GetRequiredService<SpellFeature>().System.Store = new SpellStore([new SpellInfo
        {
            Id = FoodSpell, Name = "playerbot consumable", Duration = new SpellDuration(60_000, 0, 60_000),
            Attributes = SpellAttributes.AllowWhileSitting, AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRegen, BasePoints = 4, BaseDice = 1,
                DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
        }], [], []);

    [Fact]
    public async Task FoodThatWasUsedAndConsumedWithItsAura_IsSilent_FoodAtFullHealthIsWasted()
    {
        await using WorldTestHost host = StartWithFood(out _);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            AnomalyCapture cap = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(session.Player!));
            try
            {
                var watch = new PlayerbotConsumableWatch();
                SpellSystem system = host.WorldServices.GetRequiredService<SpellFeature>().System;
                await host.OnWorldAsync(() =>
                {
                    InstallFood(host);
                    Player player = session.Player!;
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 3, out _));
                    player.Health = 20;
                    Item food = player.Inventory.AllItems.Single(item => item.Entry == FoodItem);
                    PlayerbotConsumableWatch.Use use = PlayerbotConsumableWatch.Begin(player, food, system.Store.Get(FoodSpell));
                    session.ManagedBudget = new ManagedActionBudget(2);
                    Assert.True(session.TryManagedAction(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(food.BagSlot, food.Slot)));
                    watch.Sent(player, use, system, 1000);
                    return true;
                });
                await host.WaitForWorldAsync(() => system.HasAura(session.Player!, FoodSpell), "food aura");
                await host.OnWorldAsync(() =>
                {
                    watch.Poll(session.Player!, system, 2000);
                    Assert.Empty(cap.OfSubsystem(PlayerbotAnomalies.Subsystem("gear")));

                    // Full health: eating is a waste.
                    Player player = session.Player!;
                    player.Health = player.MaxHealth;
                    Item food = player.Inventory.AllItems.Single(item => item.Entry == FoodItem);
                    watch.Sent(player, PlayerbotConsumableWatch.Begin(player, food, system.Store.Get(FoodSpell)), system, 3000);
                    Assert.Equal(1, cap.Count(Key("gear", "consumable-wasted")));
                    return true;
                });
            }
            finally { cap.Dispose(); }
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FoodUsedTwiceButNeverConsumed_IsReported_OnceItIsASecondTime()
    {
        await using WorldTestHost host = StartWithFood(out _);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            AnomalyCapture cap = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(session.Player!));
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallFood(host);
                    Player player = session.Player!;
                    SpellSystem system = host.WorldServices.GetRequiredService<SpellFeature>().System;
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 3, out _));
                    player.Health = 20;
                    Item food = player.Inventory.AllItems.Single(item => item.Entry == FoodItem);
                    var watch = new PlayerbotConsumableWatch();

                    // The packet "went out" (nothing consumed it): two uses in a row, the second one is a pattern.
                    watch.Sent(player, PlayerbotConsumableWatch.Begin(player, food, system.Store.Get(FoodSpell)), system, 1000);
                    watch.Poll(player, system, 2000);
                    Assert.False(cap.Any(Key("gear", "consumable-not-consumed")), "one failure is a race with the server: " + cap.Describe());
                    watch.Sent(player, PlayerbotConsumableWatch.Begin(player, food, system.Store.Get(FoodSpell)), system, 5000);
                    watch.Poll(player, system, 6000);
                    Assert.Equal(1, cap.Count(Key("gear", "consumable-not-consumed")));
                    watch.Sent(player, PlayerbotConsumableWatch.Begin(player, food, system.Store.Get(FoodSpell)), system, 9000);
                    watch.Poll(player, system, 10_000);
                    Assert.Equal(1, cap.Count(Key("gear", "consumable-not-consumed")));
                    return true;
                });
            }
            finally { cap.Dispose(); }
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FoodConsumedButWithoutAnyEffect_IsReported_OnTheSecondTime()
    {
        await using WorldTestHost host = StartWithFood(out _);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            AnomalyCapture cap = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(session.Player!));
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallFood(host);
                    Player player = session.Player!;
                    SpellSystem system = host.WorldServices.GetRequiredService<SpellFeature>().System;
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 5, out _));
                    player.Health = 20;
                    var watch = new PlayerbotConsumableWatch();
                    for (int use = 0; use < 2; use++)
                    {
                        Item food = player.Inventory.AllItems.Single(item => item.Entry == FoodItem);
                        PlayerbotConsumableWatch.Use snapshot = PlayerbotConsumableWatch.Begin(player, food, system.Store.Get(FoodSpell));
                        // The server took the item (one gone) but applied no aura and healed nothing.
                        Assert.Equal(1u, player.Inventory.DestroyItemCount(food, 1));
                        watch.Sent(player, snapshot, system, (uint)(1000 + use * 4000));
                        watch.Poll(player, system, (uint)(2000 + use * 4000));
                        Assert.Equal(use == 0 ? 0 : 1, cap.Count(Key("gear", "consumable-no-effect")));
                    }

                    return true;
                });
            }
            finally { cap.Dispose(); }
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static async Task<Creature> AddTargetAsync(WorldTestHost host, WorldSession session, float xOffset, uint low)
    {
        Creature target = null!;
        await host.World.InvokeAsync(() =>
        {
            Player player = session.Player!;
            target = new Creature(low, new CreatureTemplate
            {
                Entry = 900020,
                Name = "spell target",
                CreatureType = 1,
                MinLevelHealth = 2000,
                MaxLevelHealth = 2000,
            }, null, new CreatureContent([], [], [], [], []), new Random(1));
            target.Relocate(player.X + xOffset, player.Y, player.Z, 0, host.World.NowMs);
            player.Map!.AddObject(target);
            return true;
        });
        await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(target.Guid), "spell target visibility");
        return target;
    }
}
