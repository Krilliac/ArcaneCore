using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Beast training (vmangos Pet::GetTPForSpell, HasTPForSpell, CanLearnPetSpell, CanTakeMoreActiveSpells, InitPetCreateSpells and the
/// LEARN_PET_SPELL cast checks, Spell.cpp:5837-5862): a Bite chain of three ranks costing 1, 4 and 9 points on the wolf skill line 208.
/// </summary>
public sealed class PetTrainingTests
{
    private const uint BeastEntry = 5211;
    private const uint CreateBeastEntry = 5212;
    private const uint WolfFamily = 1;
    private const uint WolfSkillLine = 208;
    private const uint ForeignSkillLine = 999;

    private const uint LearnBite = 922001;
    private const uint Bite1 = 922002;
    private const uint Bite2 = 922003;
    private const uint Bite3 = 922004;
    private const uint Teach1 = 922011;
    private const uint Teach2 = 922012;
    private const uint Teach3 = 922013;
    private const uint TeachHigh = 922014;
    private const uint Foreign = 922030;
    private const uint TeachForeign = 922031;
    private const uint PetTalent = 922050;
    private const uint Claw = 922060;
    private const uint Dash = 922061;
    private const uint Cower = 922062;
    private const uint Prowl = 922063;

    private const uint Stamina1 = 922070;
    private const uint Stamina2 = 922071;
    private const uint TeachStamina1 = 922072;
    private const uint TeachStamina2 = 922073;

    private static SpellInfo PassiveAura(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static readonly SkillLineAbilityRecord[] Rows =
    [
        new(1, WolfSkillLine, Bite1, 0, 0, 0, Bite2, 0, 0, 0, 1),
        new(2, WolfSkillLine, Bite2, 0, 0, 0, Bite3, 0, 0, 0, 4),
        new(3, WolfSkillLine, Bite3, 0, 0, 0, 0, 0, 0, 0, 9),
        new(4, ForeignSkillLine, Foreign, 0, 0, 0, 0, 0, 0, 0, 0),
        new(5, 270, PetTalent, 0, 0, 0, 0, 0, 0, 0, 5),
        new(6, 270, Stamina1, 0, 0, 0, Stamina2, 0, 0, 0, 5),
        new(7, 270, Stamina2, 0, 0, 0, 0, 0, 0, 0, 10),
    ];

    private static SpellInfo Teach(uint id, uint learns, uint level = 0)
        => Spell(id, Effect(SpellEffectName.LearnPetSpell, 0, trigger: learns)) with { SpellLevel = level, StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static PetTestKit PetKit(PetOptions? options = null) => new(
        extraSpells:
        [
            Teach(LearnBite, Bite1),
            Spell(Bite1, Effect(SpellEffectName.Dummy, 1)),
            Spell(Bite2, Effect(SpellEffectName.Dummy, 1)),
            Spell(Bite3, Effect(SpellEffectName.Dummy, 1)),
            Teach(Teach1, Bite1),
            Teach(Teach2, Bite2),
            Teach(Teach3, Bite3),
            Teach(TeachHigh, Bite1, level: 40),
            Spell(Foreign, Effect(SpellEffectName.Dummy, 1)),
            Teach(TeachForeign, Foreign),
            Spell(PetTalent, Effect(SpellEffectName.Dummy, 1)),
            Spell(Claw, Effect(SpellEffectName.Dummy, 1)),
            Spell(Dash, Effect(SpellEffectName.Dummy, 1)),
            Spell(Cower, Effect(SpellEffectName.Dummy, 1)),
            Spell(Prowl, Effect(SpellEffectName.Dummy, 1)),
            PassiveAura(Stamina1),
            PassiveAura(Stamina2),
            Teach(TeachStamina1, Stamina1),
            Teach(TeachStamina2, Stamina2),
        ],
        petContent: new PetContent([], [new PetCreateSpells(CreateBeastEntry, [LearnBite])]),
        extraTemplates:
        [
            BeastTemplate(BeastEntry),
            BeastTemplate(CreateBeastEntry),
        ],
        petOptions: options);

    private static CreatureTemplate BeastTemplate(uint entry) => CreatureTestSupport.Template(entry, b =>
    {
        b.MinLevel = 5;
        b.MaxLevel = 5;
        b.MinLevelHealth = 100;
        b.MaxLevelHealth = 100;
        b.Faction = 14;
    }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = WolfFamily };

    private static PetTraining Training(PetTestKit kit, uint? family = WolfFamily)
        => new(new SkillLineAbilityCatalog(Rows, hasTrainingPoints: true),
            family is { } id ? new Dictionary<uint, uint> { [id] = WolfSkillLine } : new Dictionary<uint, uint>(),
            kit.Spells.System.Store.Get);

    private static Player Hunter(PetTestKit kit)
    {
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 10;
        return hunter;
    }

    private static (Player Hunter, Creature Pet, CharmInfo Charm) Tame(PetTestKit kit, uint entry = BeastEntry)
    {
        Player hunter = Hunter(kit);
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(entry)!, 3, 0, 0, 0);
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515));
        return (hunter, pet, pet.Summon!.Charm!);
    }

    private static SpellCastResult Learn(PetTestKit kit, Player hunter, uint teach)
        => kit.Spells.System.CastSpell(hunter, teach, SpellCastTargets.ForSelf(), triggered: true);

    [Fact]
    public void Cost_IsTheRankPointsMinusTheHighestKnownRankOfTheChain()
    {
        using PetTestKit kit = PetKit();
        PetTraining training = Training(kit);
        (_, _, CharmInfo charm) = Tame(kit);

        Assert.Equal(1, training.Cost(charm, Bite1));
        Assert.Equal(4, training.Cost(charm, Bite2));
        charm.LearnSpell(Bite1);
        Assert.Equal(3, training.Cost(charm, Bite2));
        Assert.Equal(8, training.Cost(charm, Bite3));
        charm.LearnSpell(Bite2);
        Assert.Equal(5, training.Cost(charm, Bite3));
        Assert.Equal(-3, training.Cost(charm, Bite1)); // a lower rank than one known is a negative cost, which HasPoints refuses
    }

    [Fact]
    public void Cost_IsZeroWhenTheSpellHasNoTrainingPoints()
    {
        using PetTestKit kit = PetKit();
        PetTraining training = Training(kit);
        (_, _, CharmInfo charm) = Tame(kit);

        Assert.Equal(0, training.Cost(charm, Foreign));
        Assert.Equal(0, training.Cost(charm, 999_999)); // no ability row
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(4, 5, false)]
    [InlineData(-3, 0, true)]
    [InlineData(10, -1, false)]
    public void HasPoints_FollowsHasTPForSpell(int trainingPoints, int need, bool expected)
        => Assert.Equal(expected, PetTraining.HasPoints(trainingPoints, need));

    [Fact]
    public void CanLearn_RequiresTheFamilySkillLineOrPetTalentsForHunterPets()
    {
        using PetTestKit kit = PetKit();
        PetTraining training = Training(kit);
        (Player hunter, Creature pet, _) = Tame(kit);

        Assert.True(training.CanLearn(pet, Bite1));
        Assert.False(training.CanLearn(pet, Foreign));
        Assert.True(training.CanLearn(pet, PetTalent));
        Assert.False(Training(kit, family: null).CanLearn(pet, Bite1)); // a family without a skill line learns nothing

        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        Assert.True(training.CanLearn(pet, Bite1));
        Assert.False(training.CanLearn(pet, PetTalent)); // the talent line is for a hunter's pet only
    }

    [Fact]
    public void CanTakeMoreActiveSpells_AllowsFourActiveChains_PassivesAndKnownChainsFree()
    {
        using PetTestKit kit = PetKit();
        PetTraining training = Training(kit);
        (_, _, CharmInfo charm) = Tame(kit);
        charm.LearnSpell(Claw);
        charm.LearnSpell(Dash);
        charm.LearnSpell(Bite1);
        Assert.True(training.CanTakeMoreActiveSpells(charm, Cower)); // the fourth chain still fits

        charm.LearnSpell(Cower);
        Assert.False(training.CanTakeMoreActiveSpells(charm, Prowl));
        Assert.True(training.CanTakeMoreActiveSpells(charm, Bite2));                       // a rank of a known chain
        Assert.True(training.CanTakeMoreActiveSpells(charm, PetTestKit.PetPassiveSpell)); // passives are free
    }

    [Fact]
    public void Tame_ChargesTheCreateSpellsTrainingPoints_BeforeTheLoyaltyRaise()
    {
        using PetTestKit kit = PetKit(new PetOptions { DefaultLoyalty = 3 });
        kit.Service.Training = Training(kit);

        (_, Creature pet, CharmInfo charm) = Tame(kit, CreateBeastEntry);

        Assert.True(charm.HasSpell(Bite1));
        // -1 for the Bite rank 1 create spell, then two loyalty levels of a level 5 pet add 5 each.
        Assert.Equal(-1 + (2 * 5), charm.TrainingPoints);
        Assert.Equal((uint)PetLoyalty.DisplayTrainingPoints(charm.TrainingPoints), pet.GetUInt32(UpdateFields.UnitTrainingPoints));
    }

    [Fact]
    public void LearnPetSpell_SpendsTrainingPoints_AndShowsThemInTheUpdateField()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 10);

        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, Teach1));

        Assert.True(charm.HasSpell(Bite1));
        Assert.Equal(9, charm.TrainingPoints);
        Assert.Equal((uint)PetLoyalty.DisplayTrainingPoints(9), pet.GetUInt32(UpdateFields.UnitTrainingPoints));
    }

    [Fact]
    public void LearnPetSpell_RefusedWithoutEnoughTrainingPoints()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 0);

        Assert.Equal(SpellCastResult.TrainingPoints, Learn(kit, hunter, Teach1));

        Assert.False(charm.HasSpell(Bite1));
        Assert.Equal(0, charm.TrainingPoints);
    }

    [Fact]
    public void LearnPetSpell_RefusedWithoutAPet()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        Player hunter = Hunter(kit);

        Assert.Equal(SpellCastResult.NoPet, Learn(kit, hunter, Teach1));
    }

    [Fact]
    public void LearnPetSpell_RefusedWhenThePetHasFourActiveChains()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 10);
        foreach (uint spell in new[] { Claw, Dash, Cower, Prowl })
        {
            charm.LearnSpell(spell);
        }

        Assert.Equal(SpellCastResult.TooManySkills, Learn(kit, hunter, Teach1));
        Assert.False(charm.HasSpell(Bite1));
    }

    [Fact]
    public void LearnPetSpell_RefusedBelowTheTeachSpellLevel()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 0); // Lowlevel is reported before TrainingPoints (Spell.cpp:5856 before :5859)

        Assert.Equal(SpellCastResult.Lowlevel, Learn(kit, hunter, TeachHigh));
        Assert.False(charm.HasSpell(Bite1));
    }

    [Fact]
    public void LearnPetSpell_IgnoresAnotherFamilysAbility()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 10);

        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, TeachForeign));

        Assert.False(charm.HasSpell(Foreign));
        Assert.Equal(10, charm.TrainingPoints);
    }

    [Fact]
    public void LearnPetSpell_HigherRankReplacesTheLowerRankInItsBarSlot_AndPaysTheDifference()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 10);
        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, Teach1));
        charm.SetSpellAutocast(Bite1, true);
        int slot = Enumerable.Range(0, CharmInfo.ActionBarSize).Single(i => charm.GetButton(i).Action == Bite1);

        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, Teach2));

        Assert.False(charm.HasSpell(Bite1));
        Assert.Equal(ActionType.Enabled, charm.SpellStates[Bite2]);
        Assert.Equal(Bite2, charm.GetButton(slot).Action);
        Assert.Equal((byte)ActionType.Enabled, charm.GetButton(slot).Type);
        Assert.Equal(6, charm.TrainingPoints); // 10 - 1 - (4 - 1)

        Learn(kit, hunter, Teach1); // a lower rank than the one known is not learned and costs nothing
        Assert.False(charm.HasSpell(Bite1));
        Assert.Equal(6, charm.TrainingPoints);
    }

    [Fact]
    public void LearnPetSpell_PassiveHigherRankReplacesTheLowerRankAura()
    {
        using PetTestKit kit = PetKit();
        kit.Service.Training = Training(kit);
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        PetLoyalty.SetTrainingPoints(pet, 20);

        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, TeachStamina1));
        Assert.True(kit.Spells.System.HasAura(pet, Stamina1));

        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, TeachStamina2));

        Assert.False(charm.HasSpell(Stamina1));
        Assert.True(charm.HasSpell(Stamina2));
        Assert.False(kit.Spells.System.HasAura(pet, Stamina1));
        Assert.True(kit.Spells.System.HasAura(pet, Stamina2));
    }

    [Fact]
    public void LearnPetSpell_WithoutTrainingData_KeepsTheUncostedPath()
    {
        using PetTestKit kit = PetKit();
        (Player hunter, Creature pet, CharmInfo charm) = Tame(kit);
        Assert.Null(kit.Service.Training);

        Assert.Equal(SpellCastResult.CastOk, Learn(kit, hunter, Teach3));

        Assert.True(charm.HasSpell(Bite3));
        Assert.Equal(0, charm.TrainingPoints);
        Assert.Equal((uint)PetLoyalty.DisplayTrainingPoints(0), pet.GetUInt32(UpdateFields.UnitTrainingPoints));
    }
}
