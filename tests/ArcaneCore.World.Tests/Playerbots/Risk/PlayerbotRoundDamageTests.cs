using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary><see cref="PlayerbotRisk.RoundDamage"/>: the non-crit max hit of each live melee attacker added up.</summary>
public sealed class PlayerbotRoundDamageTests
{
    [Fact]
    public async Task RoundDamage_SumsTheUnitFieldMaxDamageOfEachLiveAttacker_AndSkipsTheRest()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate template = RiskTestWorld.Template(991201, health: 60, minDamage: 4, maxDamage: 6);
        (float none, float live, float afterSkips, float afterZero) = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            float empty = PlayerbotRisk.RoundDamage(player);

            Creature a = world.Spawn(template, 0, -10);
            Creature b = world.Spawn(template with { Entry = 991202 }, 3, -10);
            world.Engage(a);
            world.Engage(b);
            // The real max hit includes damage rate, attack power and mods: set it away from the template's 6.
            a.SetFloat(UpdateFields.UnitFieldMaxdamage, 11f);
            b.SetFloat(UpdateFields.UnitFieldMaxdamage, 7f);
            float both = PlayerbotRisk.RoundDamage(player);

            // Dead, evading, other-map creatures and a player in the attacker set add nothing.
            Creature dead = world.Spawn(template with { Entry = 991203 }, -3, -10);
            world.Engage(dead);
            player.Map!.Combat.DealDamage(player, dead, 10_000);
            Creature evading = world.Spawn(template with { Entry = 991204 }, -6, -10);
            world.Engage(evading);
            evading.IsEvading = true;
            Creature elsewhere = world.Spawn(template with { Entry = 991205 }, -9, -10);
            world.Engage(elsewhere);
            elsewhere.Map = null;
            foreach (Creature extra in new[] { dead, evading, elsewhere })
            {
                extra.SetFloat(UpdateFields.UnitFieldMaxdamage, 100f);
                player.Combat.AttackersInternal.Add(extra);
            }

            player.Combat.AttackersInternal.Add(player);
            float skipped = PlayerbotRisk.RoundDamage(player);

            // No max hit on the unit: the template's rate over one attack time, (4 + 6) / 2 / 2 s * 2 s = 5.
            a.SetFloat(UpdateFields.UnitFieldMaxdamage, 0f);
            float fallback = PlayerbotRisk.RoundDamage(player);
            return (empty, both, skipped, fallback);
        });

        Assert.Equal(0f, none);
        Assert.Equal(18f, live);
        Assert.Equal(18f, afterSkips);
        Assert.Equal(12f, afterZero);
    }
}
