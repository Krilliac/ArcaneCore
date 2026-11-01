using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A hunter pet's power fields (vmangos Unit::GetCreatePowers, Pet::LoadPetFromDB, Pet::RegenerateFocus, Pet::GetHappinessState):
/// Focus power type, 100 focus regenerating 25 per 4 s scaled by the regeneration auras, the 1 050 000 happiness bar split every 333 000,
/// and all of it kept across a level-up.
/// </summary>
public sealed class HunterPetPowerTests
{
    private const uint BeastEntry = 5241;
    private const int FocusIndex = UpdateFields.UnitFieldPower1 + (int)PowerType.Focus;
    private const int MaxFocusIndex = UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Focus;

    private static PetTestKit Kit() => new(extraTemplates:
    [
        CreatureTestKit.Template(BeastEntry, b =>
        {
            b.MinLevel = 20;
            b.MaxLevel = 20;
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
            b.Faction = 14;
        }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
    ]);

    private static (Player Hunter, Creature Pet) Tame(PetTestKit kit)
    {
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Fact]
    public void TamedPet_HasFocusPowerType_100Focus_AndTheHappinessBar()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit);

        Assert.Equal(PowerType.Focus, pet.PowerType);
        Assert.Equal(Class.Warrior, pet.Class);
        Assert.Equal(100u, pet.GetUInt32(MaxFocusIndex));
        Assert.Equal(100u, pet.GetUInt32(FocusIndex));
        Assert.Equal(1_050_000u, pet.GetUInt32(UpdateFields.UnitFieldMaxpower5));
        Assert.Equal(SummonService.TamedHappiness, pet.GetUInt32(UpdateFields.UnitFieldPower5));
    }

    [Fact]
    public void Focus_Regenerates25Every4Seconds_AndStopsAtTheMaximum()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit);
        pet.SetUInt32(FocusIndex, 50);

        kit.Run(3_900);
        Assert.Equal(50u, pet.GetUInt32(FocusIndex));

        kit.Run(100);
        Assert.Equal(75u, pet.GetUInt32(FocusIndex));

        kit.Run(4_000);
        Assert.Equal(100u, pet.GetUInt32(FocusIndex));

        kit.Run(4_000);
        Assert.Equal(100u, pet.GetUInt32(FocusIndex));
    }

    [Fact]
    public void Focus_NearTheMaximum_IsClamped()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit);
        pet.SetUInt32(FocusIndex, 90);

        kit.Run(4_000);

        Assert.Equal(100u, pet.GetUInt32(FocusIndex));
    }

    [Theory]
    [InlineData(25u)]
    [InlineData(25u, 0)]
    [InlineData(37u, 50)]            // 25 x 1.5 truncated (Pet.cpp:778-781)
    [InlineData(50u, 100)]
    [InlineData(62u, 50, 66)]        // 25 x 1.5 x 1.66 = 62.25: one factor per aura
    public void FocusGain_ScalesAuraByAura(uint expected, params int[] amounts)
        => Assert.Equal(expected, PetPower.FocusGain(amounts));

    [Theory]
    [InlineData(0u, SummonService.HappinessState.Unhappy)]
    [InlineData(332_999u, SummonService.HappinessState.Unhappy)]
    [InlineData(333_000u, SummonService.HappinessState.Content)]
    [InlineData(665_999u, SummonService.HappinessState.Content)]
    [InlineData(666_000u, SummonService.HappinessState.Happy)]
    [InlineData(1_050_000u, SummonService.HappinessState.Happy)]
    public void HappinessLevels_ChangeAtTheBoundaries(uint happiness, SummonService.HappinessState expected)
        => Assert.Equal(expected, SummonService.StateOf(happiness));

    [Fact]
    public void FocusAndHappiness_AreKeptOnLevelUp()
    {
        using PetTestKit kit = Kit();
        (Player hunter, Creature pet) = Tame(kit);
        pet.SetUInt32(FocusIndex, 40);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 700_000);

        kit.Service.GivePetLevel(pet, hunter, 25);

        Assert.Equal((byte)25, pet.Level);
        Assert.Equal(PowerType.Focus, pet.PowerType);
        Assert.Equal(Class.Warrior, pet.Class);
        Assert.Equal(100u, pet.GetUInt32(MaxFocusIndex));
        Assert.Equal(40u, pet.GetUInt32(FocusIndex));
        Assert.Equal(1_050_000u, pet.GetUInt32(UpdateFields.UnitFieldMaxpower5));
        Assert.Equal(700_000u, pet.GetUInt32(UpdateFields.UnitFieldPower5));
        Assert.Equal(SummonService.HappinessState.Happy, SummonService.StateOf(pet.GetUInt32(UpdateFields.UnitFieldPower5)));
    }
}
