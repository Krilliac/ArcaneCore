using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>Hunter pet happiness and loyalty ticks (vmangos Pet::LooseHappiness, TickLoyaltyChange, KillLoyaltyBonus, the runaway).</summary>
public sealed class PetHappinessTests
{
    private const uint BeastEntry = 5241;

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

    private static (Player Hunter, FakeSession Session, Creature Pet) Tame(PetTestKit kit)
    {
        (Player hunter, FakeSession session) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, session, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Theory]
    [InlineData(1, false, 8750)]   // 70 per tick ×125
    [InlineData(2, false, 4375)]
    [InlineData(6, false, 250)]
    [InlineData(1, true, 13125)]
    public void HappinessLoss_FollowsLooseHappiness(int loyalty, bool combat, int expected)
        => Assert.Equal(expected, SummonService.HappinessLoss(loyalty, combat));

    [Theory]
    [InlineData(0u, SummonService.HappinessState.Unhappy)]
    [InlineData(332_999u, SummonService.HappinessState.Unhappy)] // vmangos Pet.h:130 HAPPINESS_LEVEL_SIZE 333000, Pet.cpp:870-875
    [InlineData(333_000u, SummonService.HappinessState.Content)]
    [InlineData(350_000u, SummonService.HappinessState.Content)]
    [InlineData(665_999u, SummonService.HappinessState.Content)]
    [InlineData(666_000u, SummonService.HappinessState.Happy)]
    [InlineData(700_000u, SummonService.HappinessState.Happy)]
    public void StateOf_SplitsTheBarInThirds(uint happiness, SummonService.HappinessState expected)
        => Assert.Equal(expected, SummonService.StateOf(happiness));

    [Theory]
    [InlineData(20u, 1, 13)]
    [InlineData(60u, 6, 4)]
    [InlineData(101u, 1, 0)]
    public void KillLoyaltyBonus_FavoursLowLevelsAndLowLoyalty(uint level, int loyalty, int expected)
        => Assert.Equal(expected, SummonService.KillLoyaltyBonus(level, loyalty));

    [Fact]
    public void PetUpdate_LosesHappinessEvery7500ms_AndTicksLoyaltyEvery12s()
    {
        using PetTestKit kit = Kit();
        (_, _, Creature pet) = Tame(kit);
        PetLoyalty.Restore(pet, 2, 5000, 0);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 900_000);

        kit.Run(7_500);
        Assert.Equal(900_000u - 4_375u, pet.GetUInt32(UpdateFields.UnitFieldPower5));

        kit.Run(4_500); // 12 s: a happy pet gains 20 loyalty points
        Assert.Equal(5020, pet.Summon!.Charm!.LoyaltyPoints);
    }

    [Fact]
    public void UnhappyRebelliousPet_RunsAway_WhenItsLoyaltyRunsOut()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet) = Tame(kit);
        PetLoyalty.Restore(pet, PetLoyalty.Rebellious, 10, 0);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 0);
        session.Clear();

        kit.Run(12_000);

        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetBroken);
        Assert.True(hunter.PetGuid.IsEmpty);
    }

    [Fact]
    public void GmLoyalty_AddsPoints_AndARebelliousPetPushedBelowZeroRunsAway()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet) = Tame(kit);
        Assert.True(SummonService.IsPet(pet));
        PetLoyalty.Restore(pet, 2, 5000, 0);

        Assert.True(kit.Service.ModifyLoyaltyByCommand(pet, hunter, 250));
        Assert.Equal(5250, pet.Summon!.Charm!.LoyaltyPoints);

        PetLoyalty.Restore(pet, PetLoyalty.Rebellious, 10, 0);
        session.Clear();
        Assert.False(kit.Service.ModifyLoyaltyByCommand(pet, hunter, -100));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetBroken);
        Assert.True(hunter.PetGuid.IsEmpty);
    }
}