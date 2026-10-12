using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A level 22 bot between Wetlands Mosshide Fenrunners and crocolisks (#184/#192/#206):
/// each ordinary creature is manageable alone, but two at one aggro spot are not.
/// The template health and damage below are from the read-only ClassicDB world snapshot.
/// </summary>
public sealed class PlayerbotPackHazardTests
{
    [Fact]
    public async Task TwoOrdinaryCreaturesAtOneAggroSpot_BlockTheWalkAndUnsafeLanding()
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
            return (world.Spawn(fenrunner, -3, 35), world.Spawn(crocolisk, 3, 35));
        });
        await world.SeeAsync(first, second);
        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;
            Assert.False(risk.BeyondStrength(bot, first, risk.SpellThreat(first)));
            Assert.False(risk.BeyondStrength(bot, second, risk.SpellThreat(second)));

            risk.ScanHazards(bot);
            Assert.Equal(2, risk.Hazards.Active(bot.MapId, world.Host.World.NowMs).Count(h => h.Kind == "strong"));
            var camp = new Vector3(bot.X, bot.Y + 35, bot.Z);
            PlayerbotNavigation.Guard(bot, risk);
            Assert.False(PlayerbotNavigation.TryPlan(bot, camp, world.Options, out _));
            PlayerbotNavigation.Guard(bot, null);
            Assert.NotNull(risk.TooStrongAt(bot, camp));
            return true;
        });
    }
}
