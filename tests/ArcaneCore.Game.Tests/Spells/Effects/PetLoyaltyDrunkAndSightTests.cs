using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Effects;

/// <summary>
/// The second spell-effects pass: tamed pets' create spells and loyalty, LEARN_PET_SPELL (57), drunk persistence and drunk invisibility
/// detection, ADD_FARSIGHT (72), the BIND_SIGHT / UNTRACKABLE / MOD_UNATTACKABLE auras, and SUMMON_CHANGE_ITEM (34).
/// </summary>
public sealed class PetLoyaltyDrunkAndSightTests
{
    private const uint BeastEntry = 5111;
    private const uint LearnBite = 921001;
    private const uint Bite = 921002;
    private const uint TeachGrowl = 921003;
    private const uint Growl = 921004;
    private const uint HighGrowl = 921005;
    private const uint TeachHighGrowl = 921006;
    private const uint Invisible = 921010;
    private const uint MindVision = 921011;
    private const uint EagleEye = 921012;
    private const uint Untrack = 921013;
    private const uint Unattackable = 921014;

    private static PetTestKit PetKit() => new(
        extraSpells:
        [
            Spell(LearnBite, Effect(SpellEffectName.LearnPetSpell, 0, trigger: Bite)),
            Spell(Bite, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)),
            Spell(TeachGrowl, Effect(SpellEffectName.LearnPetSpell, 0, trigger: Growl)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(Growl, Effect(SpellEffectName.Dummy, 1)),
            Spell(HighGrowl, Effect(SpellEffectName.Dummy, 1)) with { SpellLevel = 40 },
            Spell(TeachHighGrowl, Effect(SpellEffectName.LearnPetSpell, 0, trigger: HighGrowl)) with { SpellLevel = 40, StartRecoveryCategory = 0, StartRecoveryTime = 0 }, // Spell.cpp:5856 reads the teach spell
        ],
        petContent: new PetContent([], [new PetCreateSpells(BeastEntry, [LearnBite])]),
        extraTemplates:
        [
            CreatureTestSupport.Template(BeastEntry, b =>
            {
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 14;
            }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable },
        ]);

    private static (Player Hunter, Creature Pet) Tame(PetTestKit kit)
    {
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 10;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Fact]
    public void Tame_GivesThePetItsCreateSpells_AndStartsItRebelliousWith1000Points()
    {
        using PetTestKit kit = PetKit();
        (_, Creature pet) = Tame(kit);

        Assert.True(pet.Summon!.Charm!.HasSpell(Bite)); // the "learn" row resolves to the spell it teaches
        Assert.False(pet.Summon.Charm.HasSpell(LearnBite));
        Assert.Equal(PetLoyalty.Rebellious, PetLoyalty.Level(pet));
        Assert.Equal(1000, pet.Summon.Charm.LoyaltyPoints);
        Assert.Equal(unchecked((uint)-1), pet.GetUInt32(UpdateFields.UnitTrainingPoints)); // Pet::GetDispTP for 0 points spent
    }

    [Fact]
    public void RaiseTo_LevelsLoyaltyAsModifyLoyaltyDoes_AndEachLevelEarnsThePetsLevelInTrainingPoints()
    {
        using PetTestKit kit = PetKit();
        (_, Creature pet) = Tame(kit);

        PetLoyalty.RaiseTo(pet, 3);

        Assert.Equal(3, PetLoyalty.Level(pet));
        Assert.Equal(PetLoyalty.StartPoints(3), pet.Summon!.Charm!.LoyaltyPoints);
        Assert.Equal(10, pet.Summon.Charm.TrainingPoints); // two level-ups of a level 5 pet
    }

    [Fact]
    public void Modify_ARebelliousPetBelowZero_RunsAway_AndAHigherOneDropsALevel()
    {
        using PetTestKit kit = PetKit();
        (_, Creature pet) = Tame(kit);
        PetLoyalty.RaiseTo(pet, 2);

        Assert.True(PetLoyalty.Modify(pet, -100_000));
        Assert.Equal(PetLoyalty.Rebellious, PetLoyalty.Level(pet));
        Assert.False(PetLoyalty.Modify(pet, -100_000));
    }

    [Fact]
    public void LearnPetSpell_TeachesTheLivePet_UnlessItIsBelowTheSpellLevel()
    {
        using PetTestKit kit = PetKit();
        (Player hunter, Creature pet) = Tame(kit);

        kit.Spells.System.CastSpell(hunter, TeachGrowl, SpellCastTargets.ForSelf(), triggered: true);
        kit.Spells.System.CastSpell(hunter, TeachHighGrowl, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(pet.Summon!.Charm!.HasSpell(Growl));
        Assert.False(pet.Summon.Charm.HasSpell(HighGrowl));
    }

    [Fact]
    public void PetDiet_UsesTheFamilyFoodMask()
    {
        using PetTestKit kit = PetKit();
        (_, Creature pet) = Tame(kit);
        kit.Service.PetFoodMask = family => family == pet.Template.Family ? 0x1u : null; // meat only
        Item meat = Item.Create(1, new ItemTemplate { Entry = 1, FoodType = 1 }, default);
        Item fish = Item.Create(2, new ItemTemplate { Entry = 2, FoodType = 2 }, default);

        Assert.True(kit.Service.HaveInDiet(pet, meat));
        Assert.False(kit.Service.HaveInDiet(pet, fish));
    }

    [Theory]
    [InlineData(12800, 0, 12800)]
    [InlineData(12800, 450, 6400)]
    [InlineData(12800, 901, 0)]
    public void SoberedAfterLogout_FallsLinearlyOverFifteenMinutes(int saved, long seconds, int expected)
        => Assert.Equal(expected, SpellSystem.SoberedAfterLogout((ushort)saved, seconds));

    [Fact]
    public void DrunkPlayers_SeeTypeSixInvisibility_UpToTheirDrunkValue()
    {
        using SpellTestKit kit = new([Spell(Invisible, Effect(SpellEffectName.ApplyAura, 5000, aura: AuraType.ModInvisibility, misc: InvisibilityAuras.DrunkInvisibilityType)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }]);
        (Player viewer, _) = kit.AddPlayer(1);
        (Player spirit, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(spirit, Invisible, SpellCastTargets.ForSelf(), triggered: true);

        Assert.False(InvisibilityAuras.CanDetect(kit.System, viewer, spirit));
        kit.System.SetDrunkValue(viewer, 4000);
        Assert.False(InvisibilityAuras.CanDetect(kit.System, viewer, spirit));
        kit.System.SetDrunkValue(viewer, 12800);
        Assert.True(InvisibilityAuras.CanDetect(kit.System, viewer, spirit));
    }

    [Fact]
    public void MindVision_MovesTheCastersCameraToTheTarget_AndBack()
    {
        using SpellTestKit kit = new([Spell(MindVision, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.BindSight)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }]);
        (Player priest, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(priest, MindVision, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(target.Guid.Value, priest.GetUInt64(UpdateFields.PlayerFarsight));

        kit.System.RemoveAuras(target, MindVision);
        Assert.Equal(0ul, priest.GetUInt64(UpdateFields.PlayerFarsight));
    }

    [Fact]
    public void EagleEye_PutsAFarSightPointAtTheDestination_UntilItsDurationEnds()
    {
        using SpellTestKit kit = new([Spell(EagleEye, Effect(SpellEffectName.AddFarsight, 0)) with
        {
            Duration = new SpellDuration(1_000, 0, 1_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }]);
        (Player hunter, _) = kit.AddPlayer(1);

        kit.System.CastSpell(hunter, EagleEye, new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (40f, 5f, 0f) }, triggered: true);
        var focus = Assert.Single(kit.System.DynamicObjects);
        Assert.True(focus.IsFarSightFocus);
        Assert.Equal((40f, 5f), (focus.X, focus.Y));
        Assert.Equal(focus.Guid.Value, hunter.GetUInt64(UpdateFields.PlayerFarsight));

        kit.Advance(1_200);
        Assert.Empty(kit.System.DynamicObjects);
        Assert.Equal(0ul, hunter.GetUInt64(UpdateFields.PlayerFarsight));
    }

    [Fact]
    public void Untrackable_And_Unattackable_SetTheirFlagsWhileTheyLast()
    {
        SpellInfo Aura(uint id, AuraType type) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: type)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        using SpellTestKit kit = new([Aura(Untrack, AuraType.Untrackable), Aura(Unattackable, AuraType.ModUnattackable)]);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Untrack, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, Unattackable, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(SpellSystem.UntrackableVisFlag, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & SpellSystem.UntrackableVisFlag);
        Assert.NotEqual(0u, (uint)(player.UnitFlags & UnitFlags.NonAttackable2));

        kit.System.RemoveAuras(player, Untrack);
        kit.System.RemoveAuras(player, Unattackable);
        Assert.Equal(0, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & SpellSystem.UntrackableVisFlag);
        Assert.Equal(0u, (uint)(player.UnitFlags & UnitFlags.NonAttackable2));
    }

    [Fact]
    public void ChangeItem_ReplacesTheItemInItsSlot_KeepingItsEnchantmentAndDurabilityLoss()
    {
        using SpellTestKit kit = new([]);
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore(
        [
            new ItemTemplate { Entry = 990101, Class = (uint)ItemClass.Weapon, Stackable = 1, MaxDurability = 100 },
            new ItemTemplate { Entry = 990102, Class = (uint)ItemClass.Weapon, Stackable = 1, MaxDurability = 50 },
        ]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(990101, 1, out Item? old));
        (byte bag, byte slot) = (old!.BagSlot, old.Slot);
        old.Durability = 40;
        old.SetUInt32(UpdateFields.ItemFieldEnchantment, 1897);

        Item changed = Assert.IsType<Item>(player.Inventory.ChangeItem(old, 990102));

        Assert.Equal(990102u, changed.Entry);
        Assert.Equal((bag, slot), (changed.BagSlot, changed.Slot));
        Assert.Equal(1897u, changed.EnchantmentId(0));
        Assert.Equal(20u, changed.Durability); // 60 % lost of 50
    }
}
