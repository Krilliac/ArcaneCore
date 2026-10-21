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
}
