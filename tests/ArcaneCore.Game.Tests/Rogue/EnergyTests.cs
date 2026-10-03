using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.Progression;
using Xunit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Characterisation of the player energy pool, which already follows vmangos (Player.cpp:2269-2353 RegenerateAll /
/// Regenerate: +20 per 2000 ms tick, capped at the maximum, in and out of combat; UnitDefines.h:65
/// REGEN_TIME_PLAYER_FULL = 2000). The tests pin it so the stat, aura and rate lanes that rework regeneration
/// cannot change the base tick unnoticed. Rate.Energy, MOD_POWER_REGEN_PERCENT (Adrenaline Rush), Vigor and the
/// 82 percent miss refund are NOT implemented here (docs/areas/rogue.md, limits).
/// </summary>
public sealed class EnergyTests
{
    private const int Energy = UpdateFields.UnitFieldPower1 + (int)PowerType.Energy;
    private const int MaxEnergy = UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy;

    private static Player AddEnergyPlayer(WorldRuntime world, uint start, uint max = 100)
    {
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetUInt32(MaxEnergy, max);
        player.SetUInt32(Energy, start);
        return player;
    }

    [Fact]
    public void Energy_RegeneratesTwentyEveryTwoSeconds_NotEveryTick()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = AddEnergyPlayer(world, 10);

        world.RunTick(50);
        Assert.Equal(30u, player.GetUInt32(Energy));
        world.RunTick(1000);
        Assert.Equal(30u, player.GetUInt32(Energy));
        world.RunTick(1000);
        Assert.Equal(50u, player.GetUInt32(Energy));
        // The 50 ms first tick fired the timer early, so the 2000 ms period carries a 50 ms remainder (timer 1950 after the third fire).
        world.RunTick(1949);
        Assert.Equal(50u, player.GetUInt32(Energy));
        world.RunTick(1);
        Assert.Equal(70u, player.GetUInt32(Energy));
    }

    [Fact]
    public void Energy_CapsAtTheMaximum()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = AddEnergyPlayer(world, 95);

        world.RunTick(50);

        Assert.Equal(100u, player.GetUInt32(Energy));
        world.RunTick(2000);
        Assert.Equal(100u, player.GetUInt32(Energy));
    }

    [Fact]
    public void Energy_RegeneratesInCombat_WhileRageDecayAndHealthRegenDoNot()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = AddEnergyPlayer(world, 10);
        player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage, 100);
        player.Health = 500;
        map.Combat.SetInCombatState(player, 60000);

        world.RunTick(50);

        Assert.Equal(30u, player.GetUInt32(Energy));
        Assert.Equal(100u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        Assert.Equal(500u, player.Health);
    }

    [Fact]
    public void NoEnergyPool_NoEnergyRegen()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = AddEnergyPlayer(world, 0, max: 0);

        world.RunTick(50);
        world.RunTick(2000);

        Assert.Equal(0u, player.GetUInt32(Energy));
    }

    [Fact]
    public void Resurrection_RestoresThePercentOfMaxEnergy()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = AddEnergyPlayer(world, 0);

        map.Combat.ResurrectPlayer(player, 0.5f, applySickness: false);

        Assert.Equal(50u, player.GetUInt32(Energy));
    }

    [Fact]
    public void LevelUp_RefillsEnergy()
    {
        (Player player, _) = PlayerProgressionTests.Create();
        var progression = PlayerProgressionTests.Progression();
        progression.InitializeLoadedPlayer(player);
        player.SetUInt32(MaxEnergy, 100);
        player.SetUInt32(Energy, 10);

        progression.GiveXp(player, 450, ObjectGuid.Empty);

        Assert.Equal(2, player.Level);
        Assert.Equal(100u, player.GetUInt32(Energy));
    }
}
