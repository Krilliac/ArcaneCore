using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatCore;

/// <summary>
/// Out-of-combat health of a creature a player owns or charms (vmangos Creature::RegenerateHealth, Creature.cpp:1139-1150):
/// <c>uint32(GetRegenHPPerSpirit() * Rate.Health) * 4</c> per 5 s tick instead of the third of the maximum an unowned creature gets.
/// </summary>
public sealed class CreatureRegenTests
{
    private static CombatTestUnit OwnedPet(Map map, Class @class, uint spirit)
    {
        var pet = new CombatTestUnit(level: 60, health: 10_000);
        pet.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)@class);
        pet.SetUInt32(UpdateFields.UnitFieldStat0 + 4, spirit);
        pet.SetUInt64(UpdateFields.UnitFieldSummonedby, ObjectGuid.Player(1).Value);
        pet.Spawn(map, 10, 10);
        pet.Health = 100;
        return pet;
    }

    [Fact]
    public void AnOwnedHunterPet_RegeneratesFourTimesTheSpiritTable_NotAThird()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        CombatTestUnit pet = OwnedPet(map, Class.Hunter, spirit: 100);

        world.RunTick(50);

        Assert.Equal(100u + (37u * 4), pet.Health); // uint(100 * 0.43 - 5.5) = 37, x 4
    }

    [Fact]
    public void AnOwnedPet_ScalesTheSpiritRegenByRateHealth_BeforeTruncating()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions { RateHealth = 2.0f })); // Default is shared: never mutate it
        CombatTestUnit pet = OwnedPet(map, Class.Hunter, spirit: 100);

        world.RunTick(50);

        Assert.Equal(100u + (75u * 4), pet.Health); // uint(37.5 x 2) = 75, x 4
    }

    [Fact]
    public void AnOwnedPetWhoseClassTableIsZero_RegeneratesNothing()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        CombatTestUnit pet = OwnedPet(map, Class.Warrior, spirit: 10); // 10 x 1.26 - 22.6 < 0: floored at 0

        world.RunTick(50);

        Assert.Equal(100u, pet.Health);
    }
}
