using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Regeneration and the PvP flag.</summary>
public sealed class CombatRegenTests
{
    [Fact]
    public void PlayerRegen_DecaysRage_AndHealsBySpirit_OutOfCombat()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + 1, 100);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 500;

        world.RunTick(50);

        Assert.Equal(80u, player.GetUInt32(UpdateFields.UnitFieldPower1 + 1)); // −2 rage per tick
        Assert.Equal(515u, player.Health);                                     // warrior 1.26 · 30 − 22.6 = 15.2

        world.RunTick(1000);
        Assert.Equal(515u, player.Health); // every 2 s
        world.RunTick(1000);
        Assert.Equal(530u, player.Health);
    }

    [Fact]
    public void PlayerRegen_NoHealthOrRageDecayInCombat()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + 1, 100);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 500;
        map.Combat.SetInCombatState(player, 60000);

        world.RunTick(50);

        Assert.Equal(100u, player.GetUInt32(UpdateFields.UnitFieldPower1 + 1));
        Assert.Equal(500u, player.Health);
    }

    [Theory]
    [InlineData(Class.Warrior, 30f, 15.2f)]
    [InlineData(Class.Mage, 30f, 4.3f)]
    [InlineData(Class.Rogue, 30f, 12.2f)]
    public void HealthPerSpirit_FollowsVmangos(Class @class, float spirit, float expected)
        => Assert.Equal(expected, MapCombat.RegenHealthPerSpirit(@class, spirit), 3);

    [Theory]
    [InlineData(Class.Mage, 100f, 18.75f)]
    [InlineData(Class.Priest, 100f, 18.75f)]
    [InlineData(Class.Warrior, 100f, 0f)]
    public void ManaPerSpirit_FollowsVmangos(Class @class, float spirit, float expected)
        => Assert.Equal(expected, MapCombat.RegenManaPerSpirit(@class, spirit), 3);

    [Fact]
    public void CreatureRegen_RestoresAThirdEveryFiveSecondsOutOfCombat()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        var mob = new CombatTestUnit();
        mob.Spawn(map, 10, 10);
        mob.Health = 100;

        world.RunTick(50);
        Assert.Equal(433u, mob.Health);
        world.RunTick(4900);
        Assert.Equal(433u, mob.Health);
        world.RunTick(100);
        Assert.Equal(766u, mob.Health);

        mob.RegeneratesHealth = false;
        world.RunTick(5000);
        Assert.Equal(766u, mob.Health);
    }

    [Fact]
    public void TogglePvp_FlagsAtOnce_AndTheFlagLingersFiveMinutes()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));

        map.Combat.TogglePvp(player, null);
        Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.PvpDesired));
        Assert.NotEqual(0u, (uint)(player.UnitFlags & UnitFlags.Pvp));

        map.Combat.TogglePvp(player, null);
        Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.PvpDesired));
        Assert.NotEqual(0u, (uint)(player.UnitFlags & UnitFlags.Pvp));

        world.RunTick(CombatConstants.PvpFlagTimerMs - 1000);
        Assert.NotEqual(0u, (uint)(player.UnitFlags & UnitFlags.Pvp));
        world.RunTick(2000);
        Assert.Equal(0u, (uint)(player.UnitFlags & UnitFlags.Pvp));

        map.Combat.TogglePvp(player, true);
        map.Combat.TogglePvp(player, true);
        Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.PvpDesired));
    }

    [Fact]
    public void AttackingAFlaggedPlayer_FlagsTheAttacker()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player v = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        map.Combat.TogglePvp(v, true);
        Assert.True(map.Combat.Hooks.CanAttack(a, v));

        map.Combat.Attack(a, v);
        world.RunTick(50);

        Assert.NotEqual(0u, (uint)(a.UnitFlags & UnitFlags.Pvp));
    }

    [Fact]
    public void CanAttack_RefusesUnflaggedEnemiesAndFriends()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player orc = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        Player dwarf = CombatTestKit.AddPlayer(world, 3, 2, 2, new FakeSession(3), Race.Dwarf);
        dwarf.UnitFlags |= UnitFlags.Pvp;
        var mob = new CombatTestUnit();
        mob.Spawn(map, 3, 0);

        Assert.False(map.Combat.Hooks.CanAttack(a, orc));
        Assert.True(map.Combat.Hooks.IsFriendly(a, dwarf));
        Assert.False(map.Combat.Hooks.CanAttack(a, dwarf));
        Assert.True(map.Combat.Hooks.CanAttack(a, mob));
        mob.IsInEvadeMode = true;
        Assert.False(map.Combat.Hooks.CanAttack(a, mob));
    }
}
