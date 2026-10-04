using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>
/// <see cref="MapCombat.DamageTaken"/>: vmangos records the per-attacker damage history (Unit::UnitDamaged) before the
/// lethal check, after the duel clamp, and never for self damage (Unit.cpp:762-796, 825). The existing
/// <see cref="MapCombat.DamageDealt"/> stays a non-lethal event.
/// </summary>
public sealed class DamageTakenEventTests
{
    private static (WorldRuntime World, Map Map, Player A, Player V) Pair(uint victimHealth = 1000)
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player v = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        v.Health = victimHealth;
        world.RunTick(1);
        return (world, map, a, v);
    }

    [Fact]
    public void A_non_lethal_hit_raises_it_with_the_dealt_damage()
    {
        (WorldRuntime world, Map map, Player a, Player v) = Pair();
        using WorldRuntime _ = world;
        var seen = new List<(Unit Attacker, Unit Victim, uint Damage)>();
        map.Combat.DamageTaken += (attacker, victim, damage) => seen.Add((attacker, victim, damage));

        map.Combat.DealDamage(a, v, 100);

        Assert.Equal([(a, (Unit)v, 100u)], seen);
    }

    [Fact]
    public void The_lethal_blow_is_recorded_too_while_damage_dealt_still_ignores_it()
    {
        (WorldRuntime world, Map map, Player a, Player v) = Pair(victimHealth: 40);
        using WorldRuntime _ = world;
        var taken = new List<uint>();
        int dealt = 0;
        map.Combat.DamageTaken += (_, _, damage) => taken.Add(damage);
        map.Combat.DamageDealt += (_, _, _, _, _) => dealt++;

        map.Combat.DealDamage(a, v, 500);

        Assert.Equal([500u], taken);
        Assert.Equal(0, dealt);
        Assert.False(v.IsAlive);
    }

    [Fact]
    public void Self_damage_and_zero_damage_raise_nothing()
    {
        (WorldRuntime world, Map map, Player a, Player v) = Pair();
        using WorldRuntime _ = world;
        int count = 0;
        map.Combat.DamageTaken += (_, _, _) => count++;

        map.Combat.DealDamage(a, a, 10);
        map.Combat.DealDamage(a, v, 0);

        Assert.Equal(0, count);
    }

    [Fact]
    public void It_is_raised_before_the_victim_dies_so_a_subscriber_sees_the_victim_alive()
    {
        (WorldRuntime world, Map map, Player a, Player v) = Pair(victimHealth: 40);
        using WorldRuntime _ = world;
        bool aliveAtEvent = false;
        map.Combat.DamageTaken += (_, victim, _) => aliveAtEvent = victim.IsAlive;

        map.Combat.DealDamage(a, v, 500);

        Assert.True(aliveAtEvent);
    }
}
