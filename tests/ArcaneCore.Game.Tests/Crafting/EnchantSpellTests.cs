using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice enchant-spells: the three enchant effects and their checks (vmangos SpellEffects.cpp:3009-3099, :5009-5057; Spell.cpp:7177-7200 and
/// :7311-7375), the item-target fit with the inventory-type mask and the Enchant Cloak - Minor Agility data fix (Item.cpp:975-1003). The spells are fixtures
/// with the field layout of classic-db enchant spells (Targets 16 = item target, no unit target).
/// </summary>
public sealed class EnchantSpellTests
{
    private const uint Bracer = SkillTestKit.SmeltCopper;   // 2657: the fixture skill ability of Blacksmithing is the craft the skill-up reads
    private const uint WeaponOil = 95302;
    private const uint HeldOil = 95303;
    private const uint MinorAgility = 13419;
    private const uint OwnOnly = 95305;
    private const uint HighLevel = 95306;
    private const uint SoulboundEnchantSpell = 95307;

    private const uint Wrist = 95401;
    private const uint Helm = 95402;
    private const uint Cloak = 95403;
    private const uint Weapon = 95404;
    private const uint LowItem = 95405;

    private const uint StrengthEnchant = 701;
    private const uint SoulboundEnchant = 702;

    private static EnchantCatalog Catalog() => new(
    [
        new SpellItemEnchantment(StrengthEnchant, [5, 0, 0], [4, 0, 0], [(uint)ItemStatType.Strength, 0, 0], "Strength", 0, 0),
        new SpellItemEnchantment(SoulboundEnchant, [5, 0, 0], [2, 0, 0], [(uint)ItemStatType.Strength, 0, 0], "Soulbound", 0, EnchantCatalog.CanSoulboundFlag),
    ]);

    private static SpellInfo EnchantSpell(uint id, SpellEffectName effect, uint enchant, int value, int itemClass, int subMask = 0, int invMask = 0, uint baseLevel = 0, uint targets = 0x10) =>
        CraftingTestKit.Craft(id, SpellTestKit.Effect(effect, value, SpellImplicitTarget.None, misc: (int)enchant)) with
        {
            Targets = targets,
            EquippedItemClass = itemClass,
            EquippedItemSubClassMask = subMask,
            EquippedItemInventoryTypeMask = invMask,
            BaseLevel = baseLevel,
        };

    private sealed class Rig : IDisposable
    {
        public Rig(Func<Player, SpellCastTargets, Item?>? tradeItems = null, Func<bool>? gmAllowTrades = null, ArcaneCore.Kernel.Accounts.AccountSecurity security = ArcaneCore.Kernel.Accounts.AccountSecurity.Player)
        {
            SpellInfo[] spells =
            [
                EnchantSpell(Bracer, SpellEffectName.EnchantItem, StrengthEnchant, 1, itemClass: 4, invMask: 1 << 9),
                EnchantSpell(WeaponOil, SpellEffectName.EnchantItemTemporary, StrengthEnchant, 1800, itemClass: 2),
                EnchantSpell(HeldOil, SpellEffectName.EnchantHeldItem, StrengthEnchant, 600, itemClass: -1, targets: 0) with { Attributes = (SpellAttributes)0x200 },
                EnchantSpell(MinorAgility, SpellEffectName.EnchantItem, StrengthEnchant, 1, itemClass: 2, invMask: 1 << 16),
                EnchantSpell(OwnOnly, SpellEffectName.EnchantItem, StrengthEnchant, 1, itemClass: 4) with { AttributesEx2 = (SpellAttributesEx2)0x2000 },
                EnchantSpell(HighLevel, SpellEffectName.EnchantItem, StrengthEnchant, 1, itemClass: 4, baseLevel: 40),
                EnchantSpell(SoulboundEnchantSpell, SpellEffectName.EnchantItem, SoulboundEnchant, 1, itemClass: 4),
            ];
            ItemTemplate[] items =
            [
                new() { Entry = Wrist, Class = 4, SubClass = 1, Name = "Test Bracers", DisplayId = 1, InventoryType = 9, Quality = 2, ItemLevel = 30 },
                new() { Entry = Helm, Class = 4, SubClass = 1, Name = "Test Helm", DisplayId = 2, InventoryType = 1, Quality = 2, ItemLevel = 30 },
                new() { Entry = Cloak, Class = 4, SubClass = 1, Name = "Test Cloak", DisplayId = 3, InventoryType = 16, Quality = 2, ItemLevel = 30 },
                new() { Entry = Weapon, Class = 2, SubClass = 7, Name = "Test Sword", DisplayId = 4, InventoryType = 13, Delay = 2000, MaxDurability = 50, Quality = 2, ItemLevel = 30, Damages = [new ItemDamage(5, 9, 0)] },
                new() { Entry = LowItem, Class = 4, SubClass = 1, Name = "Test Rag Bracers", DisplayId = 5, InventoryType = 9, Quality = 2, ItemLevel = 10 },
            ];
            Kit = new CraftingTestKit(spells, items, security);
            Kit.AttachSkills().Set(SkillIds.Blacksmithing, 10, 150, 1);
            Kit.Player.AttachEnchantments(new PlayerEnchantments(Kit.Player, Catalog(), Kit.System));
            Kit.Inventory.StatsApplier = new EnchantStatsApplier(Kit.Inventory.StatsApplier);
            new EnchantItemSpells(Catalog(), gmAllowTrades, tradeItems).Install(Kit.System);
        }

        public CraftingTestKit Kit { get; }

        public Player Player => Kit.Player;

        public uint Strength => Player.GetUInt32(UpdateFields.UnitFieldStat0);

        public Item Give(uint entry) => Kit.Give(entry);

        public Item Equip(uint entry, byte slot)
        {
            Item item = Give(entry);
            Kit.Inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, slot);
            return item;
        }

        public SpellCastResult Cast(uint spell, Item? item)
        {
            Kit.Session.Clear();
            Kit.Kit.Spellbook.Teach(Player, spell);
            var targets = item is null ? SpellCastTargets.ForSelf() : new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid };
            return Kit.Kit.System.HandleCastRequest(Player, spell, targets);
        }

        public byte[] LastCastResult() => SpellTestKit.Packets(Kit.Session, WorldOpcode.SmsgCastResult).Last();

        public void Dispose() => Kit.Dispose();
    }

    // --- the fit check -------------------------------------------------------------------------------------------------------

    [Fact]
    public void EnchantBracer_OnAHelm_AnswersEquippedItemClass_BecauseOfTheInventoryTypeMask()
    {
        using var rig = new Rig();
        Item helm = rig.Give(Helm);

        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(Bracer, helm));

        Assert.Equal(0u, ItemEnchantments.Id(helm, EnchantSlots.Permanent));
    }

    [Fact]
    public void EnchantBracer_OnABracer_Succeeds_SetsTheEnchantment_AndLogsItToTheOwner()
    {
        using var rig = new Rig();
        Item wrist = rig.Give(Wrist);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Bracer, wrist));

        Assert.Equal(StrengthEnchant, ItemEnchantments.Id(wrist, EnchantSlots.Permanent));
        Assert.Single(SpellTestKit.Packets(rig.Kit.Session, WorldOpcode.SmsgEnchantmentlog));   // the owner hears the new enchantment once
    }

    [Fact]
    public void AnEnchantment_OnAWornItem_RaisesTheStatAtOnce_AndReplacingItSwapsThePermanentSlot()
    {
        using var rig = new Rig();
        Item wrist = rig.Equip(Wrist, InventorySlots.Wrists);
        uint before = rig.Strength;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Bracer, wrist));
        Assert.Equal(before + 4, rig.Strength);

        // The same enchant again: remove, set (unchanged triple writes nothing), apply: still +4, never +8.
        rig.Kit.Kit.Now += 5000;
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Bracer, wrist));
        Assert.Equal(before + 4, rig.Strength);
    }

    [Fact]
    public void ACraftSkillUp_IsRolledOnce_ForAPermanentEnchant()
    {
        using var rig = new Rig();
        Item wrist = rig.Give(Wrist);
        rig.Kit.SkillRandom.Ints.Enqueue(1);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Bracer, wrist));

        Assert.Equal((ushort)11, rig.Kit.Skills!.GetValuePure(SkillIds.Blacksmithing));
        Assert.Empty(rig.Kit.SkillRandom.Ints);
    }

    [Fact]
    public void ASpellWithoutAnItemTarget_AnswersItemGone()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.ItemGone, rig.Cast(Bracer, null));
    }

    [Fact]
    public void MinorAgilityCloak_PassesTheClassCheckThroughTheDataFix_ButNotOnAHelm()
    {
        using var rig = new Rig();
        Item cloak = rig.Give(Cloak);
        Item helm = rig.Give(Helm);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(MinorAgility, cloak));   // class 2 spell on an armor cloak: Item.cpp:979-982
        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(MinorAgility, helm));
    }

    [Fact]
    public void IsFit_NeedsTheClass_TheSubclassBit_AndTheInventoryTypeBit()
    {
        SpellInfo spell = EnchantSpell(1, SpellEffectName.EnchantItem, 1, 1, itemClass: 4, subMask: 1 << 1, invMask: 1 << 1);
        var fits = new ItemTemplate { Entry = 1, Class = 4, SubClass = 1, InventoryType = 1 };

        Assert.True(ItemTargetRules.IsFit(spell, fits));
        Assert.False(ItemTargetRules.IsFit(spell, fits with { Class = 2 }));            // wrong class
        Assert.False(ItemTargetRules.IsFit(spell, fits with { SubClass = 2 }));         // subclass bit missing
        Assert.False(ItemTargetRules.IsFit(spell, fits with { InventoryType = 2 }));    // inventory type bit missing
        Assert.True(ItemTargetRules.IsFit(spell with { EquippedItemClass = -1 }, fits with { Class = 2, SubClass = 9 }));   // any class: the subclass mask is skipped too
    }

    [Fact]
    public void TheInventoryMask_AppliesOnlyToAnItemTargetSpell()
    {
        var helm = new ItemTemplate { Entry = 1, Class = 4, SubClass = 1, InventoryType = 1 };
        SpellInfo masked = EnchantSpell(1, SpellEffectName.EnchantItem, 1, 1, itemClass: 4, invMask: 1 << 9);
        SpellInfo notItemTarget = masked with { Targets = 0 };

        Assert.False(ItemTargetRules.IsFit(masked, helm));
        Assert.True(ItemTargetRules.IsFit(notItemTarget, helm));   // Item.cpp:995-1000: "only check for item enchantments (TARGET_FLAG_ITEM)"
    }

    // --- cast checks ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnItemBelowTheSpellBaseLevel_AnswersLowlevel()
    {
        using var rig = new Rig();
        Item rags = rig.Give(LowItem);   // item level 10, the spell needs 40

        Assert.Equal(SpellCastResult.Lowlevel, rig.Cast(HighLevel, rags));
        Assert.Equal(0u, ItemEnchantments.Id(rags, EnchantSlots.Permanent));
    }

    [Fact]
    public void ATradeWindowItem_OfAnotherOwner_IsRefused_ForOwnItemOnlySpells_AndSoulboundEnchants_ButAllowedOtherwise()
    {
        Item? offered = null;
        using var rig = new Rig(tradeItems: (_, _) => offered);
        (Player other, _) = rig.Kit.Kit.AddPlayer(2, 1, 0);
        other.Inventory.Templates = rig.Player.Inventory.Templates;
        other.Inventory.GuidAllocator = new ItemGuidAllocator();
        other.Inventory.Load([]);
        other.AttachEnchantments(new PlayerEnchantments(other, Catalog(), rig.Kit.System));
        other.Inventory.StatsApplier = new EnchantStatsApplier(other.Inventory.StatsApplier);
        Assert.Equal(InventoryResult.Ok, other.Inventory.AddItem(Wrist, 1, out offered));
        var tradeTarget = new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = new ObjectGuid(0) };

        rig.Kit.Kit.Spellbook.Teach(rig.Player, OwnOnly, SoulboundEnchantSpell, Bracer);
        Assert.Equal(SpellCastResult.NotTradeable, rig.Kit.Kit.System.HandleCastRequest(rig.Player, OwnOnly, tradeTarget));
        Assert.Equal(SpellCastResult.NotTradeable, rig.Kit.Kit.System.HandleCastRequest(rig.Player, SoulboundEnchantSpell, tradeTarget));

        rig.Kit.Kit.Now += 5000;
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.Kit.System.HandleCastRequest(rig.Player, Bracer, tradeTarget));
        Assert.Equal(StrengthEnchant, ItemEnchantments.Id(offered!, EnchantSlots.Permanent));   // the other player's item
    }

    [Fact]
    public void AGameMasterCaster_IsRefused_WhenGmAllowTradesIsOff_ButASkillUpStillCountedFirst()
    {
        using var rig = new Rig(gmAllowTrades: () => false, security: ArcaneCore.Kernel.Accounts.AccountSecurity.GameMaster);
        Item wrist = rig.Give(Wrist);
        rig.Kit.SkillRandom.Ints.Enqueue(1);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Bracer, wrist));

        Assert.Equal(0u, ItemEnchantments.Id(wrist, EnchantSlots.Permanent));          // nothing was enchanted (SpellEffects.cpp:3029)
        Assert.Equal((ushort)11, rig.Kit.Skills!.GetValuePure(SkillIds.Blacksmithing)); // but UpdateCraftSkill ran before (:3019)
    }

    // --- temporary and held enchantments -------------------------------------------------------------------------------------

    [Fact]
    public void ATemporaryEnchantment_LastsTheEffectValueInSeconds_AndStartsItsTimer()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Weapon, InventorySlots.MainHand);
        uint before = rig.Strength;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(WeaponOil, sword));

        Assert.Equal(StrengthEnchant, ItemEnchantments.Id(sword, EnchantSlots.Temporary));
        Assert.Equal(1_800_000u, ItemEnchantments.Duration(sword, EnchantSlots.Temporary));
        Assert.Equal(0u, ItemEnchantments.Charges(sword, EnchantSlots.Temporary));   // spell_enchant_charges is not imported: duration only
        Assert.Equal(1_800_000u, rig.Player.Enchantments!.LeftMs(sword, EnchantSlots.Temporary));
        Assert.Equal(before + 4, rig.Strength);
        Assert.Single(SpellTestKit.Packets(rig.Kit.Session, WorldOpcode.SmsgItemEnchantTimeUpdate));
    }

    [Fact]
    public void AHeldItemEnchantment_GoesOnTheMainHand_ForItsBasePointsInSeconds_AndNeedsAWornWeapon()
    {
        using var rig = new Rig();
        rig.Kit.Kit.Spellbook.Teach(rig.Player, HeldOil);

        // Nothing worn: the effect does nothing (and the held-item attribute lets the cast through without an item target).
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.Kit.System.HandleCastRequest(rig.Player, HeldOil, SpellCastTargets.ForSelf()));
        Item sword = rig.Equip(Weapon, InventorySlots.MainHand);
        rig.Kit.Kit.Now += 5000;

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.Kit.System.HandleCastRequest(rig.Player, HeldOil, SpellCastTargets.ForSelf()));

        Assert.Equal(StrengthEnchant, ItemEnchantments.Id(sword, EnchantSlots.Temporary));
        Assert.Equal(600_000u, ItemEnchantments.Duration(sword, EnchantSlots.Temporary));
    }

    [Fact]
    public void AHeldItemEnchantment_DoesNotReplaceADifferentOne()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Weapon, InventorySlots.MainHand);
        ItemEnchantments.Set(sword, EnchantSlots.Temporary, SoulboundEnchant, 90_000, 0);
        rig.Kit.Kit.Spellbook.Teach(rig.Player, HeldOil);

        rig.Kit.Kit.System.HandleCastRequest(rig.Player, HeldOil, SpellCastTargets.ForSelf());

        Assert.Equal(SoulboundEnchant, ItemEnchantments.Id(sword, EnchantSlots.Temporary));
    }

    [Fact]
    public void InstallingTwice_Throws()
    {
        using var rig = new Rig();

        Assert.Throws<InvalidOperationException>(() => new EnchantItemSpells(Catalog()).Install(rig.Kit.System));
    }
}
