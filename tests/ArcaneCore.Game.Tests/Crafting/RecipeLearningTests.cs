using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice recipe-learning-acceptance: a recipe item (class 9: Pattern, Plans, Formula, Manual) is used with CMSG_USE_ITEM, its ON_USE
/// spell is SPELL_EFFECT_LEARN_SPELL of the craft (vmangos SpellEffects.cpp:2435-2454), and the item is gated by <c>RequiredSkill</c> /
/// <c>RequiredSkillRank</c> / <c>RequiredSpell</c> through Player::CanUseItem (specialisation plans carry a RequiredSpell: 95 recipe items in classic-db).
/// Nothing here adds code: it pins that the three lanes of this change (use-item, the existing learn path, create-item) compose.
/// </summary>
public sealed class RecipeLearningTests
{
    private const uint LearnSpell = 94001;
    private const uint CraftSpell = 94002;
    private const uint Plans = 94101;
    private const uint SpecPlans = 94102;
    private const uint SpecSpell = 9788;

    private sealed class Requirements : IItemRequirements
    {
        public uint BlacksmithingSkill { get; set; } = 100;

        public HashSet<uint> Spells { get; } = [];

        public bool CanDualWield(PlayerInventory inventory) => false;

        public uint SkillValue(PlayerInventory inventory, uint skill) => skill == 164 ? BlacksmithingSkill : 300;

        public bool HasSpell(PlayerInventory inventory, uint spellId) => Spells.Contains(spellId);

        public byte HonorRank(PlayerInventory inventory) => 0;

        public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            SpellInfo[] spells =
            [
                CraftingTestKit.Craft(LearnSpell, SpellTestKit.Effect(SpellEffectName.LearnSpell, 0, trigger: CraftSpell)),
                CraftingTestKit.Craft(CraftSpell, SpellTestKit.Effect(SpellEffectName.CreateItem, 1) with { ItemType = CraftingTestKit.LinenBandage }) with
                {
                    Reagents = [new SpellReagent(CraftingTestKit.LinenCloth, 1)],
                },
            ];
            ItemTemplate[] items =
            [
                new ItemTemplate
                {
                    Entry = Plans, Class = 9, SubClass = 4, Name = "Plans: Test Sword", DisplayId = 7, Quality = 1, RequiredSkill = 164, RequiredSkillRank = 125,
                    Spells = [new ItemSpell(LearnSpell, ItemSpellTriggers.OnUse, -1, 0, -1, 0, -1)],
                }.Normalized(),
                new ItemTemplate
                {
                    Entry = SpecPlans, Class = 9, SubClass = 4, Name = "Plans: Specialist Sword", DisplayId = 8, Quality = 1, RequiredSkill = 164, RequiredSkillRank = 125,
                    RequiredSpell = SpecSpell, Spells = [new ItemSpell(LearnSpell, ItemSpellTriggers.OnUse, -1, 0, -1, 0, -1)],
                }.Normalized(),
            ];
            Kit = new CraftingTestKit(spells, items);
            Kit.Player.Inventory.Requirements = Skills;
            ReagentRules.Install(Kit.System);
            CreateItemSpells.Install(Kit.System);
            Service = ItemUseService.Install(Kit.System);
        }

        public CraftingTestKit Kit { get; }

        public Requirements Skills { get; } = new();

        public ItemUseService Service { get; }

        public Item Use(uint entry)
        {
            Assert.Equal(InventoryResult.Ok, Kit.Inventory.AddItem(entry, 1, out Item? item));
            Kit.Session.Clear();
            Service.UseItem(Kit.Player, item!.BagSlot, item.Slot, 0, SpellCastTargets.ForSelf());
            return item;
        }

        public InventoryResult? EquipError()
        {
            byte[]? payload = SpellTestKit.Packets(Kit.Session, WorldOpcode.SmsgInventoryChangeFailure).LastOrDefault();
            return payload is null ? null : (InventoryResult)payload[0];
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void Plans_BelowTheRequiredSkillRank_AreRefused_AndNotConsumed()
    {
        using var rig = new Rig();
        rig.Skills.BlacksmithingSkill = 100;

        Item plans = rig.Use(Plans);

        Assert.Equal(InventoryResult.CantEquipSkill, rig.EquipError());
        Assert.NotNull(rig.Kit.Inventory.GetItemByGuid(plans.Guid));
        Assert.False(rig.Kit.Kit.Spellbook.HasSpell(rig.Kit.Player, CraftSpell));
    }

    [Fact]
    public void Plans_AtTheRequiredRank_TeachTheCraftOnce_AndAreConsumed()
    {
        using var rig = new Rig();
        rig.Skills.BlacksmithingSkill = 125;

        Item plans = rig.Use(Plans);

        Assert.True(rig.Kit.Kit.Spellbook.HasSpell(rig.Kit.Player, CraftSpell));
        Assert.Null(rig.Kit.Inventory.GetItemByGuid(plans.Guid));
        Assert.Single(SpellTestKit.Packets(rig.Kit.Session, WorldOpcode.SmsgLearnedSpell));
    }

    [Fact]
    public void SpecialisationPlans_NeedTheSpecialisationSpell()
    {
        using var rig = new Rig();
        rig.Skills.BlacksmithingSkill = 200;

        Item refused = rig.Use(SpecPlans);
        Assert.Equal(InventoryResult.NoRequiredProficiency, rig.EquipError());
        Assert.NotNull(rig.Kit.Inventory.GetItemByGuid(refused.Guid));

        rig.Skills.Spells.Add(SpecSpell);
        Item accepted = rig.Use(SpecPlans);
        Assert.True(rig.Kit.Kit.Spellbook.HasSpell(rig.Kit.Player, CraftSpell));
        Assert.Null(rig.Kit.Inventory.GetItemByGuid(accepted.Guid));
    }

    [Fact]
    public void ALearnedRecipe_CanBeCrafted_AnUnlearnedOneCannot()
    {
        using var rig = new Rig();
        rig.Skills.BlacksmithingSkill = 125;
        rig.Kit.Give(CraftingTestKit.LinenCloth, 2);

        Assert.Equal(SpellCastResult.NotKnown, rig.Kit.System.HandleCastRequest(rig.Kit.Player, CraftSpell, SpellCastTargets.ForSelf()));

        rig.Use(Plans);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(rig.Kit.Player, CraftSpell, SpellCastTargets.ForSelf()));
        Assert.Equal(1u, rig.Kit.Inventory.GetItemCount(CraftingTestKit.LinenBandage));
        Assert.Equal(1u, rig.Kit.Inventory.GetItemCount(CraftingTestKit.LinenCloth));
    }

    [Fact]
    public void AnAlreadyKnownRecipe_IsConsumedAndNothingChanges_LikeVmangos()
    {
        // vmangos has no SPELL_FAILED_SPELL_LEARNED path here (SpellEffects.cpp:2435-2454): the item is used up and the second learn is a no-op.
        using var rig = new Rig();
        rig.Skills.BlacksmithingSkill = 125;
        rig.Use(Plans);
        rig.Kit.Session.Clear();

        Item again = rig.Use(Plans);

        Assert.Null(rig.Kit.Inventory.GetItemByGuid(again.Guid));
        Assert.Empty(SpellTestKit.Packets(rig.Kit.Session, WorldOpcode.SmsgLearnedSpell));
    }
}
