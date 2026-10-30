using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>Hunter pet experience and level-ups (vmangos Pet::GivePetXP, GetXPForPetLevel, the pet share of kill rewards).</summary>
public sealed class PetExperienceTests
{
    private const uint BeastEntry = 5231;
    private const uint VictimEntry = 5232;
    private const uint NoXpVictimEntry = 5233;

    private static CreatureTemplate Beast(uint entry, byte level) => CreatureTestKit.Template(entry, b =>
    {
        b.MinLevel = level;
        b.MaxLevel = level;
        b.MinLevelHealth = 100;
        b.MaxLevelHealth = 100;
        b.Faction = 14;
    }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 };

    private static PetTestKit Kit() => new(extraTemplates: [Beast(BeastEntry, 20), Beast(VictimEntry, 22), Beast(NoXpVictimEntry, 22) with { StaticFlags1 = 0x2 }]);

    private static (Player Hunter, Creature Pet) Tame(PetTestKit kit, uint guid = 1, byte ownerLevel = 30)
    {
        (Player hunter, _) = kit.AddPlayer(guid);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = ownerLevel;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Theory]
    [InlineData(1u, 20u)]       // 400 × 5%
    [InlineData(20u, 1160u)]    // 23200 × 5%
    [InlineData(59u, 10490u)]   // 209800 × 5%
    [InlineData(60u, 0u)]
    public void PetXpForLevel_IsFivePercentOfThePlayerTable(uint level, uint expected)
        => Assert.Equal(expected, SummonService.PetXpForLevel(level));

    [Fact]
    public void GivePetXp_LevelsUp_KeepsTheRemainder_AndAddsTrainingPoints()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit);
        CharmInfo charm = pet.Summon!.Charm!;
        PetLoyalty.Restore(pet, 3, 1000, 0);

        Assert.Equal(1, kit.Service.GivePetXp(pet, SummonService.PetXpForLevel(20) + 7));

        Assert.Equal(21, (int)pet.Level);
        Assert.Equal(7u, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
        Assert.Equal(SummonService.PetXpForLevel(21), pet.GetUInt32(UpdateFields.UnitFieldPetnextlevelexp));
        Assert.Equal(2, charm.TrainingPoints); // loyalty level 3 - 1
    }

    [Fact]
    public void GivePetXp_StopsAtTheOwnersLevel_WithNoExperienceLeft()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit, ownerLevel: 21);

        Assert.Equal(1, kit.Service.GivePetXp(pet, 100_000));
        Assert.Equal(21, (int)pet.Level);
        Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
        Assert.Equal(0, kit.Service.GivePetXp(pet, 100_000)); // at the cap nothing happens
    }

    [Fact]
    public void AwardKillExperience_Solo_GivesThePetItsOwnGain_Group_HalfTheMemberShare()
    {
        using PetTestKit kit = Kit();
        (Player hunter, Creature pet) = Tame(kit, ownerLevel: 24);
        Creature victim = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(VictimEntry)!, 5, 0, 0, 0);

        kit.Service.AwardKillExperience([hunter], victim, false, 1f, 1f);
        uint solo = ExperienceFormulas.KillGain(20, 22, false, false);
        Assert.Equal(solo, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));

        (Player other, _) = kit.AddPlayer(2);
        other.Level = 24;
        IReadOnlyList<KillShare> shares = KillExperience.Distribute([new KillCandidate(24, true, false), new KillCandidate(24, true, false)], 22, false, false);
        kit.Service.AwardKillExperience([hunter, other], victim, false, 1f, 1f);
        Assert.Equal(solo + (shares[0].Experience / 2), pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
    }

    [Fact]
    public void AwardKillExperience_NoXpStaticFlagVictim_GivesThePetNothing()
    {
        // vmangos MaNGOS::XP::Gain returns 0 for CREATURE_STATIC_FLAG_NO_XP (0x2; Formulas.h:112), so the pet share is 0 as well.
        using PetTestKit kit = Kit();
        (Player hunter, Creature pet) = Tame(kit, ownerLevel: 24);
        Creature victim = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NoXpVictimEntry)!, 5, 0, 0, 0);

        kit.Service.AwardKillExperience([hunter], victim, false, 1f, 1f);

        Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
    }

    [Fact]
    public void AwardKillExperience_GreyForTheOwner_GivesNothing_MaxLevelOwner_StillLevelsThePet()
    {
        using PetTestKit kit = Kit();
        (Player hunter, Creature pet) = Tame(kit, ownerLevel: 60);
        Creature victim = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(VictimEntry)!, 5, 0, 0, 0);

        kit.Service.AwardKillExperience([hunter], victim, false, 1f, 1f); // level 22 is grey to a level 60 owner
        Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));

        hunter.Level = 25;
        kit.Service.AwardKillExperience([hunter], victim, false, 1f, 1f);
        Assert.Equal(ExperienceFormulas.KillGain(20, 22, false, false), pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
    }

    [Fact]
    public void SummonedHunterPet_StartsWithTheRealNextLevelExperience()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit);
        Assert.Equal(SummonService.PetXpForLevel(pet.Level), pet.GetUInt32(UpdateFields.UnitFieldPetnextlevelexp));
    }
}
