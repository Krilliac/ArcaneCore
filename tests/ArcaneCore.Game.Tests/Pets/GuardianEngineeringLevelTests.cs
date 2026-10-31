using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A guardian summoned with an engineering trinket takes level engineering skill / 5 (vmangos Spell::EffectSummonGuardian, SpellEffects.cpp:2823-2831):
/// Mechanical Dragonling (item 4396, required skill 202, inventory type trinket) at skill 200 is level 40, Mithril Mechanical Dragonling (10576) at 250 is 50.
/// </summary>
public sealed class GuardianEngineeringLevelTests
{
    private const uint DragonlingEntry = 2678;
    private const uint DragonlingSpell = 4073;
    private const uint MechanicalDragonling = 4396;
    private const uint MithrilDragonling = 10576;
    private const uint EngineeringNonTrinket = 90_401;
    private const uint CookingTrinket = 90_402;

    private static readonly ItemTemplate[] Items =
    [
        new() { Entry = MechanicalDragonling, Class = 15, Name = "Mechanical Dragonling", DisplayId = 1, InventoryType = (uint)InventoryType.Trinket, RequiredSkill = SkillIds.Engineering, RequiredSkillRank = 200 },
        new() { Entry = MithrilDragonling, Class = 15, Name = "Mithril Mechanical Dragonling", DisplayId = 2, InventoryType = (uint)InventoryType.Trinket, RequiredSkill = SkillIds.Engineering, RequiredSkillRank = 250 },
        new() { Entry = EngineeringNonTrinket, Class = 15, Name = "Engineering Gadget", DisplayId = 3, InventoryType = 0, RequiredSkill = SkillIds.Engineering },
        new() { Entry = CookingTrinket, Class = 15, Name = "Other Trinket", DisplayId = 4, InventoryType = (uint)InventoryType.Trinket, RequiredSkill = SkillIds.Cooking },
    ];

    private static (PetTestKit Kit, Player Player) Rig(ushort engineering)
    {
        var kit = new PetTestKit(
            [Spell(DragonlingSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)DragonlingEntry))],
            extraTemplates: [CreatureTestKit.Template(DragonlingEntry, b =>
            {
                b.Name = "Mechanical Dragonling";
                b.Faction = 14;
                b.MinLevel = 47;
                b.MaxLevel = 47;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            })]);
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore(Items, []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        var skills = new PlayerSkills(
            player,
            SkillTestKit.Catalog(lines => [.. lines, new SkillLineRecord(SkillIds.Engineering, SkillCategories.Profession, "Engineering", 0)]),
            new SkillOptions(),
            new FakeSkillSpellHost { Cascade = player },
            new ScriptedSkillRandom());
        player.AttachSkills(skills);
        if (engineering != 0)
        {
            Assert.True(skills.Set(SkillIds.Engineering, engineering, 300));
        }

        return (kit, player);
    }

    private static int Summon(PetTestKit kit, Player player, uint itemEntry)
    {
        Item? item = itemEntry == 0 ? null : ItemTestData.Give(player.Inventory, itemEntry);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastCustomSpell(player, DragonlingSpell, SpellCastTargets.ForSelf(), null, castItem: item));
        return Assert.Single(kit.Creatures.Creatures, c => c.Entry == DragonlingEntry).Level;
    }

    [Theory]
    [InlineData(MechanicalDragonling, (ushort)200, 40)]
    [InlineData(MithrilDragonling, (ushort)250, 50)]
    [InlineData(MithrilDragonling, (ushort)300, 60)]
    public void AnEngineeringTrinket_SummonsAtSkillOverFive(uint item, ushort skill, int level)
    {
        (PetTestKit kit, Player player) = Rig(skill);
        using PetTestKit owned = kit;

        Assert.Equal(level, Summon(kit, player, item));
    }

    [Theory]
    [InlineData(0u, (ushort)200)]
    [InlineData(EngineeringNonTrinket, (ushort)200)]
    [InlineData(CookingTrinket, (ushort)200)]
    [InlineData(MechanicalDragonling, (ushort)0)]
    public void WithoutAnEngineeringTrinketOrSkill_TheGuardianKeepsTheTemplateLevel(uint item, ushort skill)
    {
        (PetTestKit kit, Player player) = Rig(skill);
        using PetTestKit owned = kit;

        Assert.Equal(47, Summon(kit, player, item));
    }
}
