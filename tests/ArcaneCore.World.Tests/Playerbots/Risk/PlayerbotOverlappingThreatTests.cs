using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

public sealed class PlayerbotOverlappingThreatTests
{
    [Fact]
    public async Task TwoIndependentHostilesAtSpiritHealerLanding_AreJudgedTogether()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        (byte level, uint health) = await world.OnWorldAsync(() => (world.Player.Level, world.Player.MaxHealth));
        // Each creature costs roughly 35% of full health alone; both attacking together cost about three times that.
        float damage = health * 0.35f * PlayerbotRiskModel.PriorBotDps(level) / 60f * 2f;
        CreatureTemplate template = RiskTestWorld.Template(991091, level, health: 60, minDamage: damage, maxDamage: damage)
            with { ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos, CallForHelp = 0 };

        Creature first = await world.OnWorldAsync(() => world.Spawn(template, 25, 0));
        await world.SeeAsync(first);
        Vector3 landing = await world.OnWorldAsync(() => new Vector3(world.Player.X + 25, world.Player.Y, world.Player.Z));
        Assert.Null(await world.OnWorldAsync(() => world.Brain.Risk.TooStrongAt(world.Player, landing)));

        Creature second = await world.OnWorldAsync(() => world.Spawn(template with { Entry = 991092 }, 28, 0));
        await world.SeeAsync(first, second);
        PlayerbotEngagement? combined = await world.OnWorldAsync(() => world.Brain.Risk.TooStrongAt(world.Player, landing));
        Assert.NotNull(combined);
        Assert.Equal(1, combined.Value.Adds);
        Assert.NotNull(await world.OnWorldAsync(() => world.Brain.Risk.TooStrongNear(world.Player, landing)));
    }

    [Fact]
    public async Task CreatureAtHealerLanding_UnsafeAtResurrectionHealth_EvenWhenSafeAtFullHealth()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        (byte level, uint health) = await world.OnWorldAsync(() => (world.Player.Level, world.Player.MaxHealth));
        // About 55% of full health to kill it, but 110% of the health a spirit healer restores.
        float damage = health * 0.55f * PlayerbotRiskModel.PriorBotDps(level) / 60f * 2f;
        CreatureTemplate template = RiskTestWorld.Template(991095, level, health: 60, minDamage: damage, maxDamage: damage);
        Vector3 landing = await world.OnWorldAsync(() =>
        {
            var creature = world.Spawn(template, 25, 0);
            return new Vector3(creature.X, creature.Y, creature.Z);
        });

        PlayerbotEngagement? unsafeLanding = await world.OnWorldAsync(() => world.Brain.Risk.TooStrongAt(world.Player, landing));
        Assert.NotNull(unsafeLanding);
        Assert.True(unsafeLanding.Value.Risk > 1f);
    }

    [Fact]
    public async Task TwoCreaturesWhoseReachesOverlapBetweenThem_AreMarkedStrongByTheScan()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate fenrunner = RiskTestWorld.Template(1010, level: 22, health: 573, minDamage: 32, maxDamage: 41);
        CreatureTemplate crocolisk = RiskTestWorld.Template(1400, level: 23, health: 617, minDamage: 28, maxDamage: 45);
        (Creature first, Creature second) = await world.OnWorldAsync(() =>
        {
            Player bot = world.Player;
            bot.Level = 22;
            bot.MaxHealth = 550;
            bot.Health = 550;
            return (world.Spawn(fenrunner, -14, 35), world.Spawn(crocolisk, 14, 35));
        });
        await world.SeeAsync(first, second);
        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;
            var threats = PlayerbotRecovery.Threats(bot);
            Assert.Equal(2, threats.Count);
            // The shape under test: neither stands inside the other's reach, yet the reaches share a zone.
            Assert.False(threats[0].Reaches(threats[1].Position, 0f) || threats[1].Reaches(threats[0].Position, 0f));
            Assert.True(threats[0].Overlaps(threats[1]));
            risk.ScanHazards(bot);
            Assert.Equal(2, risk.Hazards.Active(bot.MapId, world.Host.World.NowMs).Count(h => h.Kind == "strong"));
            return true;
        });
    }

    [Fact]
    public async Task AutonomousWalk_DoesNotCrossAnUnchosenLethalPack()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        (byte level, uint health) = await world.OnWorldAsync(() => (world.Player.Level, world.Player.MaxHealth));
        // One creature alone stays within what the bot accepts (0.6 of the 0.9 ceiling); two summed (1.2) pass it, which is what
        // marks an overlapping pair strong (PlayerbotRisk.BeyondStrengthWithNearby).
        float damage = health * 0.6f * PlayerbotRiskModel.PriorBotDps(level) / 60f * 2f;
        CreatureTemplate template = RiskTestWorld.Template(991093, level, health: 60, minDamage: damage, maxDamage: damage)
            with { ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos, CallForHelp = 0 };
        var first = await world.OnWorldAsync(() => world.Spawn(template, 30, 0));
        await world.SeeAsync(first);
        bool loneRoute = await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.ScanHazards(world.Player);
            PlayerbotNavigation.Guard(world.Player, world.Brain.Risk);
            bool planned = PlayerbotNavigation.TryPlan(world.Player,
                new Vector3(world.Player.X + 60, world.Player.Y, world.Player.Z), world.Options, out _);
            PlayerbotNavigation.Guard(world.Player, null);
            return planned;
        });
        Assert.True(loneRoute, "one survivable creature must not close an ordinary route");

        var second = await world.OnWorldAsync(() => world.Spawn(template with { Entry = 991094 }, 33, 0));
        // Past the first look's phase and period so the second scan really looks; still under StrongHazardLifeMs.
        await world.Host.World.AdvanceClockAsync(2 * PlayerbotRisk.HazardScanMs);
        await world.SeeAsync(first, second);

        // The guarded planner walks round the overlapping pair (PlayerbotNavigation.HazardDetourYards: a corner before and past the hazard,
        // each its radius plus 5, 15 or 28 yards off to either side) on this flat ground, where every offset is open: a refusal would be a
        // planner that closes a route it can walk round, and a route straight through the pair would cross it. Both are failures.
        (bool planned, bool crossesPair, float maxSideYards) = await world.OnWorldAsync(() =>
        {
            Player bot = world.Player;
            var destination = new Vector3(bot.X + 60, bot.Y, bot.Z);
            world.Brain.Risk.ScanHazards(bot);
            PlayerbotNavigation.Guard(bot, world.Brain.Risk);
            bool found = PlayerbotNavigation.TryPlan(bot, destination, world.Options, out PlayerbotRoute? route);
            PlayerbotNavigation.Guard(bot, null);
            if (!found || route is null) return (false, false, 0f);
            var threats = PlayerbotRecovery.Threats(bot);
            bool crosses = false;
            float side = 0f;
            for (int i = 0; i < route.Points.Count; i++)
            {
                side = MathF.Max(side, MathF.Abs(route.Points[i].Y - bot.Y));
                if (i == 0) continue;
                Vector3 from = route.Points[i - 1], to = route.Points[i];
                int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / 2f));
                for (int step = 0; step <= steps; step++)
                    if (threats.Count(t => t.Reaches(Vector3.Lerp(from, to, (float)step / steps), 0)) >= 2)
                        crosses = true;
            }

            return (true, crosses, side);
        });
        Assert.True(planned, "the planner refused a walk it can take round the pair");
        Assert.False(crossesPair, "the unchosen walk enters two creatures' simultaneous aggro reach");
        Assert.True(maxSideYards > 4f, $"the route runs straight past the pair (widest sideways step {maxSideYards:F1} yd)");
    }
}
