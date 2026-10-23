using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Pet untraining (vmangos HandlePetUnlearnOpcode, Pet::resetTalentsCost, Player::SendPetSkillWipeConfirm) and the training points a
/// pet earns on a level-up (Pet::GivePetLevel).
/// </summary>
public sealed class PetUnlearnTests
{
    private const uint BeastEntry = 5221;
    private const uint WolfFamily = 1;
    private const uint WolfSkillLine = 208;
    private const uint Bite1 = 923002;
    private const uint Bite2 = 923003;
    private const uint FreePassive = 923010;
    private const uint CostedPassive = 923011;

    private static readonly SkillLineAbilityRecord[] Rows =
    [
        new(1, WolfSkillLine, Bite1, 0, 0, 0, Bite2, 0, 0, 0, 1),
        new(2, WolfSkillLine, Bite2, 0, 0, 0, 0, 0, 0, 0, 4),
        new(3, 270, CostedPassive, 0, 0, 0, 0, 0, 0, 0, 5),
    ];

    private static SpellInfo Passive(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static PetTestKit Kit()
    {
        var kit = new PetTestKit(
            extraSpells: [Spell(Bite1, Effect(SpellEffectName.Dummy, 1)), Spell(Bite2, Effect(SpellEffectName.Dummy, 1)), Passive(FreePassive), Passive(CostedPassive)],
            extraTemplates:
            [
                CreatureTestKit.Template(BeastEntry, b =>
                {
                    b.MinLevel = 20;
                    b.MaxLevel = 20;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                    b.Faction = 14;
                }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = WolfFamily },
            ]);
        kit.Service.Training = new PetTraining(new SkillLineAbilityCatalog(Rows, hasTrainingPoints: true),
            new Dictionary<uint, uint> { [WolfFamily] = WolfSkillLine }, kit.Spells.System.Store.Get);
        return kit;
    }

    private static (Player Hunter, FakeSession Session, Creature Pet, CharmInfo Charm) Tame(PetTestKit kit)
    {
        (Player hunter, FakeSession session) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515));
        return (hunter, session, pet, pet.Summon!.Charm!);
    }

    [Theory]
    [InlineData(0u, 0L, 1000L, 1000u)]                 // first untraining: 10 silver
    [InlineData(1000u, 0L, 1000L, 5000u)]              // then 50 silver
    [InlineData(5000u, 0L, 1000L, 10000u)]             // then 1 gold
    [InlineData(10000u, 0L, 1000L, 20000u)]            // then one gold more
    [InlineData(90000u, 0L, 1000L, 100000u)]           // up to 10 gold
    [InlineData(100000u, 0L, 1000L, 100000u)]          // capped
    [InlineData(100000u, 0L, 86400L, 1000u)]           // a day later it is 10 silver again
    public void ResetCost_FollowsResetTalentsCost(uint last, long lastTime, long now, uint expected)
        => Assert.Equal(expected, PetController.ResetCost(last, lastTime, now));

    [Fact]
    public void Unlearn_ForgetsTrainedSpells_ResetsPoints_ChargesAndResends()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet, CharmInfo charm) = Tame(kit);
        charm.LearnSpell(Bite2);
        charm.LearnSpell(FreePassive, ActionType.Passive);
        charm.LearnSpell(CostedPassive, ActionType.Passive);
        PetLoyalty.SetTrainingPoints(pet, -9);
        hunter.Money = 5_000;
        kit.Controller.UnixNow = () => 1_000;
        int saved = 0;
        kit.Controller.PetSkillsUnlearned += _ => saved++;
        session.Clear();

        Assert.True(kit.Controller.HandleUnlearn(hunter, pet.Guid));

        Assert.False(charm.HasSpell(Bite2));
        Assert.False(charm.HasSpell(CostedPassive));
        Assert.True(charm.HasSpell(FreePassive));
        Assert.DoesNotContain(charm.ActionBar, b => b.IsForSpell && b.Action == Bite2);
        Assert.Equal(pet.Level * (PetLoyalty.Level(pet) - 1), charm.TrainingPoints);
        Assert.Equal(4_000u, hunter.Money);
        Assert.Equal((1_000u, 1_000L), (charm.ResetTalentsCost, charm.ResetTalentsTime));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetSpells);
        Assert.Equal(1, saved);

        // The next one costs 50 silver, and nothing trained is left to untrain.
        Assert.Equal(5_000u, kit.Controller.ResetCost(charm));
        Assert.False(kit.Controller.HandleUnlearn(hunter, pet.Guid));
    }

    [Fact]
    public void Unlearn_WithoutTheMoney_SendsBuyFailed_AndKeepsTheSpells()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet, CharmInfo charm) = Tame(kit);
        charm.LearnSpell(Bite1);
        hunter.Money = 999;
        session.Clear();

        Assert.False(kit.Controller.HandleUnlearn(hunter, pet.Guid));
        Assert.True(charm.HasSpell(Bite1));
        Assert.Equal(999u, hunter.Money);
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgBuyFailed);
    }

    [Fact]
    public void Unlearn_IgnoresAnotherGuid_AndNonHunters()
    {
        using PetTestKit kit = Kit();
        (Player hunter, _, Creature pet, CharmInfo charm) = Tame(kit);
        charm.LearnSpell(Bite1);
        hunter.Money = 10_000;

        Assert.False(kit.Controller.HandleUnlearn(hunter, new ObjectGuid(pet.Guid.Value + 1)));
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        Assert.False(kit.Controller.HandleUnlearn(hunter, pet.Guid));
        Assert.True(charm.HasSpell(Bite1));
    }

    [Fact]
    public void SendUnlearnConfirm_CarriesThePetGuidAndPrice()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet, CharmInfo charm) = Tame(kit);
        session.Clear();
        Assert.False(kit.Controller.SendUnlearnConfirm(hunter)); // nothing trained yet

        charm.LearnSpell(Bite1);
        Assert.True(kit.Controller.SendUnlearnConfirm(hunter));
        (WorldOpcode opcode, byte[] payload) = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetUnlearnConfirm);
        Assert.Equal(PetController.BuildUnlearnConfirm(pet.Guid, 1_000), payload);
        Assert.Equal(pet.Guid.Value, BitConverter.ToUInt64(payload, 0));
        Assert.Equal(1_000u, BitConverter.ToUInt32(payload, 8));
    }

    [Fact]
    public void GivePetLevel_AddsLoyaltyLevelMinusOne_AndRestartsExperience()
    {
        using PetTestKit kit = Kit();
        (_, _, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.Restore(pet, 4, 1000, 7);
        pet.SetUInt32(UpdateFields.UnitFieldPetexperience, 123);

        PetLoyalty.GivePetLevel(pet, pet.Level + 1, nextLevelXp: 4_000);

        Assert.Equal(21, (int)pet.Level);
        Assert.Equal(7 + 3, charm.TrainingPoints);
        Assert.Equal((uint)PetLoyalty.DisplayTrainingPoints(10), pet.GetUInt32(UpdateFields.UnitTrainingPoints));
        Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
        Assert.Equal(4_000u, pet.GetUInt32(UpdateFields.UnitFieldPetnextlevelexp));

        PetLoyalty.GivePetLevel(pet, pet.Level); // same level: nothing
        Assert.Equal(10, charm.TrainingPoints);
    }
}
