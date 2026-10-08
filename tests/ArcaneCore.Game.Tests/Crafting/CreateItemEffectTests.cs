using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice create-item-effect: SPELL_EFFECT_CREATE_ITEM after vmangos Spell::DoCreateItem (SpellEffects.cpp:1885-1990) and its
/// cast check (Spell.cpp:7311-7330). The craft is the fixture Smelt Copper (2657) of the skill test kit: Blacksmithing, trivial high 100
/// (grey), mean 62 (green), low 25 (yellow).
/// </summary>
public sealed class CreateItemEffectTests
{
    private const uint Craft = SkillTestKit.SmeltCopper;

    private static CraftingTestKit Rig(int value = 1, uint output = CraftingTestKit.LinenBandage, uint? reagent = CraftingTestKit.LinenCloth, uint cooldownMs = 0, bool skills = true)
    {
        SpellInfo spell = CraftingTestKit.Craft(Craft, SpellTestKit.Effect(SpellEffectName.CreateItem, value) with { ItemType = output }) with
        {
            Reagents = reagent is { } id ? [new SpellReagent(id, 1)] : [],
            RecoveryTime = cooldownMs,
        };
        var rig = new CraftingTestKit(spell);
        ReagentRules.Install(rig.System);
        CreateItemSpells.Install(rig.System);
        if (skills)
        {
            rig.AttachSkills().Set(SkillIds.Blacksmithing, 10, 150, 1);
        }

        return rig;
    }

    [Fact]
    public void ACraft_ConsumesTheReagent_CreatesTheItem_AndPushesItWithCreatedSet()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 3);
        rig.SkillRandom.Ints.Enqueue(1);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));

        Assert.Equal(2u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.LinenBandage));
        byte[] push = Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgItemPushResult));
        Assert.Equal(1, push[8]);    // received
        Assert.Equal(1, push[12]);   // created (DoCreateItem: SendNewItem(item, count, true, bgType == 0))
    }

    [Fact]
    public void CraftedItem_RollsItsConfiguredRandomPropertyAndPropertyEnchantments()
    {
        // vmangos Spell::DoCreateItem calls StoreNewItem with GenerateItemRandomPropertyId
        // (SpellEffects.cpp:1950). This pins the full craft -> inventory -> property seam.
        const uint output = 94011;
        SpellInfo spell = CraftingTestKit.Craft(Craft,
            SpellTestKit.Effect(SpellEffectName.CreateItem, 1) with { ItemType = output });
        var template = new ItemTemplate
        {
            Entry = output, Class = 2, SubClass = 7, Name = "Crafted Sword", DisplayId = 7,
            InventoryType = 13, Stackable = 1, Quality = 2, RandomProperty = 5,
        };
        using var rig = new CraftingTestKit([spell], [template]);
        var catalog = new ItemRandomPropertyCatalog(
            [new ItemRandomPropertyRecord(1001, "of the Bear", [74, 75, 0])],
            [new ItemEnchantmentChance(5, 1001, 100f)]);
        rig.Inventory.RandomProperties = new ItemRandomProperties(catalog, () => 40f);
        ReagentRules.Install(rig.System);
        CreateItemSpells.Install(rig.System);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));

        Item crafted = Assert.Single(rig.Inventory.AllItems, item => item.Entry == output);
        Assert.Equal(1001, crafted.RandomPropertyId);
        Assert.Equal(74u, ItemEnchantments.Id(crafted, EnchantSlots.Property0));
        Assert.Equal(75u, ItemEnchantments.Id(crafted, EnchantSlots.Property0 + 1));
    }

    [Theory]
    [InlineData(10u, 1000, 1)]   // below the yellow limit 25: orange, 100 %
    [InlineData(50u, 750, 1)]    // yellow, 75 %
    [InlineData(50u, 751, 0)]
    [InlineData(70u, 250, 1)]    // green (>= 62), 25 %
    [InlineData(70u, 251, 0)]
    public void TheCraftSkillRisesWithTheRecipeColour_AtTheRollBoundary(uint skill, int roll, int expectedGain)
    {
        using CraftingTestKit rig = Rig();
        rig.Skills!.Set(SkillIds.Blacksmithing, (ushort)skill, 150, 1);
        rig.Give(CraftingTestKit.LinenCloth, 1);
        rig.SkillRandom.Ints.Enqueue(roll);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));

        Assert.Equal((ushort)(skill + expectedGain), rig.Skills.GetValuePure(SkillIds.Blacksmithing));
    }

    [Fact]
    public void AGreyRecipe_NeverRollsAndNeverRaisesTheSkill()
    {
        using CraftingTestKit rig = Rig();
        rig.Skills!.Set(SkillIds.Blacksmithing, 100, 150, 1);   // trivial high 100: grey
        rig.Give(CraftingTestKit.LinenCloth, 1);
        rig.SkillRandom.Ints.Enqueue(1);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));

        Assert.Equal((ushort)100, rig.Skills.GetValuePure(SkillIds.Blacksmithing));
        Assert.Single(rig.SkillRandom.Ints);   // the roll was never taken: GainChance of grey is 0
    }

    [Fact]
    public void ACraft_WithNoSkillsAttached_StillCreatesTheItem()
    {
        using CraftingTestKit rig = Rig(skills: false);
        rig.Give(CraftingTestKit.LinenCloth, 1);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));

        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.LinenBandage));
    }

    [Fact]
    public void FullBags_RefuseWithTheEquipError_AndDontReport_AndConsumeNothing()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(CraftingTestKit.LinenCloth, 1);
        while (rig.Inventory.AddItem(CraftingTestKit.Filler, 1, out _) == InventoryResult.Ok)
        {
        }

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(Craft));

        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
        Assert.Equal(0u, rig.Inventory.GetItemCount(CraftingTestKit.LinenBandage));
        Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgInventoryChangeFailure));
        byte[] reply = Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgCastResult));   // vmangos still sends the result, with DONT_REPORT, so the client drops its cast bar without a message
        Assert.Equal((byte)SpellCastResult.DontReport, reply[5]);
    }

    [Fact]
    public void TheBagCheck_RunsBeforeTheReagentIsTaken()
    {
        // vmangos checks CanStoreNewItem before TakeReagents (Spell.cpp:7311 vs :3717): a bag that is full BEFORE the reagent is taken refuses
        // even if the reagent would have freed a slot. Pinned so nobody reorders the two.
        using CraftingTestKit rig = Rig(output: CraftingTestKit.Filler);
        rig.Give(CraftingTestKit.LinenCloth, 1);
        while (rig.Inventory.AddItem(CraftingTestKit.Filler, 1, out _) == InventoryResult.Ok)
        {
        }

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(Craft));
        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
    }

    [Fact]
    public void AMultiCountCraft_CreatesThatMany_ClampedToTheStackSize()
    {
        using CraftingTestKit three = Rig(value: 3);
        three.Give(CraftingTestKit.LinenCloth, 1);
        Assert.Equal(SpellCastResult.CastOk, three.Cast(Craft));
        Assert.Equal(3u, three.Inventory.GetItemCount(CraftingTestKit.LinenBandage));

        using CraftingTestKit clamped = Rig(value: 30);   // the bandage stacks to 20: DoCreateItem clamps (SpellEffects.cpp:1931-1934)
        clamped.Give(CraftingTestKit.LinenCloth, 1);
        Assert.Equal(SpellCastResult.CastOk, clamped.Cast(Craft));
        Assert.Equal(20u, clamped.Inventory.GetItemCount(CraftingTestKit.LinenBandage));
    }

    [Fact]
    public void AnUnknownItemTemplate_IsRefusedAtTheCheck_AndNothingIsConsumed()
    {
        using CraftingTestKit rig = Rig(output: 424242);
        rig.Give(CraftingTestKit.LinenCloth, 1);
        rig.SkillRandom.Ints.Enqueue(1);

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(Craft));

        Assert.Equal(1u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
        Assert.Single(rig.SkillRandom.Ints);
    }

    [Fact]
    public void DoCreateItem_WithoutSkillUp_IsTheBattlegroundMarkPath()
    {
        using CraftingTestKit rig = Rig();
        rig.SkillRandom.Ints.Enqueue(1);

        CreateItemSpells.DoCreateItem(rig.Player, rig.Kit.Store.Get(Craft)!, CraftingTestKit.LinenBandage, 2, grantSkillUp: false);

        Assert.Equal(2u, rig.Inventory.GetItemCount(CraftingTestKit.LinenBandage));
        Assert.Equal((ushort)10, rig.Skills!.GetValuePure(SkillIds.Blacksmithing));
        Assert.Single(rig.SkillRandom.Ints);
    }

    [Fact]
    public void ACooldownCraft_RefusesASecondCast_UntilTheRecoveryTimeHasPassed()
    {
        // Transmute: Arcanite 17187 carries RecoveryTime 172800000 ms (2 days). The cooldown is the existing AddCooldown path.
        using CraftingTestKit rig = Rig(cooldownMs: 172_800_000);
        rig.Give(CraftingTestKit.LinenCloth, 5);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));
        Assert.Equal(SpellCastResult.NotReady, rig.Cast(Craft));
        Assert.Equal(4u, rig.Inventory.GetItemCount(CraftingTestKit.LinenCloth));   // the refused cast took nothing

        rig.Kit.Now += 172_800_001;
        rig.Kit.System.Update(100);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Craft));
    }

    [Fact]
    public void InstallingWithoutTheReagentPair_Throws_SoNoCraftIsEverFree()
    {
        using var kit = new SpellTestKit();

        Assert.Throws<InvalidOperationException>(() => CreateItemSpells.Install(kit.System));
        Assert.False(kit.System.HasEffectHandler(SpellEffectName.CreateItem));
    }

    [Fact]
    public void InstallingTwice_Throws()
    {
        using CraftingTestKit rig = Rig();

        Assert.Throws<InvalidOperationException>(() => CreateItemSpells.Install(rig.System));
    }
}
