using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Combat;

/// <summary>
/// A white swing held in the spell batch (MapCombat.SwingBatch.cs) still lands after its player stops attacking within the window (it was
/// committed), but it does not start the player's auto-attack again: the stop stands. Before, the held swing's damage went through
/// DealDamage's "a player with no victim attacks what it hit", and a bot that had set a target aside (CMSG_ATTACKSTOP) was back on it.
/// </summary>
public sealed class HeldSwingAttackStopTests
{
    [Fact]
    public async Task AHeldSwing_LandsAfterTheAttackStop_WithoutStartingTheAutoAttackAgain()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.Enabled = false);
        Creature mob = await world.OnWorldAsync(() =>
        {
            world.Session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            return world.Spawn(RiskTestWorld.Template(960301, health: 500), 2f, 0f);
        });
        await world.SeeAsync(mob);
        SpellSystem spells = world.Session.Services.GetRequiredService<SpellFeature>().System;
        MapCombat combat = await world.OnWorldAsync(() => world.Player.Map!.Combat);
        await world.OnWorldAsync(() =>
        {
            spells.SpellBatchWindowMs = 400;
            combat.Random = new HitDice();
            Assert.True(combat.Attack(world.Player, mob, melee: true));
            return true;
        });

        // The player's first swing is ready at once and held for the boundary (the creature, not hit yet, swings at nobody).
        for (int i = 0; i < 40 && await world.OnWorldAsync(() => combat.PendingSwings) == 0; i++)
            await world.Host.World.AdvanceClockAsync(10);
        uint before = await world.OnWorldAsync(() =>
        {
            Assert.Equal(1, combat.PendingSwings);
            Assert.True(combat.AttackStop(world.Player)); // CMSG_ATTACKSTOP within the window
            return mob.Health;
        });

        await world.Host.World.AdvanceClockAsync(spells.SpellBatchWindowMs);
        await world.OnWorldAsync(() =>
        {
            Assert.True(mob.Health < before, "the held swing did not land"); // (the creature's answer may be held in the next batch)
            Assert.Null(world.Player.Combat.Victim);
            Assert.False(world.Player.Combat.IsMeleeAttacking);
            return true;
        });
    }

    /// <summary>Every roll at its highest: the attack table's roll falls past miss, dodge, parry, glancing, block and crit, an ordinary hit.</summary>
    private sealed class HitDice : ICombatRandom
    {
        public int Next(int minInclusive, int maxInclusive) => maxInclusive;

        public float NextFloat(float min, float max) => max;
    }
}
