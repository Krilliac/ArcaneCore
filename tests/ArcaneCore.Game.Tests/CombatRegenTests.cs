using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
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
    public void AHitThatDoesNotStartCombat_RefreshesThePvpFlag_WithoutEnteringPvpCombat()
    {
        // vmangos Spell.cpp:1650: a hunter trap on a player does not start combat. The else at :1688-1692
        // still SetOutOfCombatWithVictim, which UpdatePvP(true)s the attacker without inPvPCombat (Unit.cpp:6049).
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player victim = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        Player calm = CombatTestKit.AddPlayer(world, 3, 4, 0, new FakeSession(3), Race.Orc);
        victim.UnitFlags |= UnitFlags.Pvp;
        victim.Combat.PvpFlagTimer = CombatConstants.PvpFlagTimerMs;
        attacker.UnitFlags |= UnitFlags.Pvp;
        attacker.Combat.PvpFlagTimer = 1_000;

        map.Combat.DealDamage(attacker, calm, 10, meleeDamage: false, startsCombat: false);
        Assert.Equal(1_000u, attacker.Combat.PvpFlagTimer); // an unflagged victim does not pulse
        Assert.False(attacker.Combat.InPvpCombat);

        map.Combat.DealDamage(attacker, victim, 10, meleeDamage: false, startsCombat: false);

        Assert.NotEqual(0u, (uint)(attacker.UnitFlags & UnitFlags.Pvp));
        Assert.Equal(CombatConstants.PvpFlagTimerMs, attacker.Combat.PvpFlagTimer);
        Assert.False(attacker.Combat.InPvpCombat);
        Assert.False(attacker.Combat.IsInCombat);
        Assert.False(victim.Combat.InPvpCombat);
        Assert.False(victim.Combat.IsInCombat);
        Assert.Equal(CombatConstants.PvpFlagTimerMs, victim.Combat.PvpFlagTimer);

        // Unit.cpp:704: a damage shield is not that pulse. The shielded player stays unflagged.
        Player shielded = CombatTestKit.AddPlayer(world, 4, 6, 0, new FakeSession(4));
        SpellInfo shield = new()
        {
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.DamageShield }],
        };
        map.Combat.DealDamage(shielded, victim, 10, meleeDamage: false, startsCombat: false, threatSpell: shield);
        Assert.Equal(0u, (uint)(shielded.UnitFlags & UnitFlags.Pvp));
        Assert.Equal(0u, shielded.Combat.PvpFlagTimer);
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
