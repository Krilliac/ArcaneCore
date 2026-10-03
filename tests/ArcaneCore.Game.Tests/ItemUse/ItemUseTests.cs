using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemUse;

/// <summary>
/// Crafting lane, slice use-item: CMSG_USE_ITEM after vmangos HandleUseItemOpcode (Handlers/SpellHandler.cpp:36-140), Player::CastItemUseSpell
/// (Objects/Player.cpp:7362-7396), the cast-item checks (Spells/Spell.cpp:7109-7175) and Spell::TakeCastItem (:4991-5048).
/// </summary>
public sealed class ItemUseTests
{
    private const uint HealSpell = 93001;
    private const uint EnergizeSpell = 93002;
    private const uint HealAndEnergizeSpell = 93003;
    private const uint PlainSpell = 93004;
    private const uint CombatForbiddenSpell = 93005;
    private const uint CostlySpell = 93006;

    private const uint Potion = 93101;
    private const uint ChargedWand = 93102;
    private const uint SingleUseCharm = 93103;
    private const uint BindOnUseTrinket = 93104;
    private const uint ManaPotion = 93105;
    private const uint RejuvPotion = 93106;
    private const uint PeacefulItem = 93107;
    private const uint NonUseItem = 93108;
    private const uint CategoryPotion = 93109;
    private const uint Cloak = 93110;
    private const uint TwoSpellItem = 93111;
    private const uint CostlyWand = 93112;

    private static ItemSpell OnUse(uint spell, int charges = 0, int cooldown = -1, uint category = 0, int categoryCooldown = -1)
        => new(spell, ItemSpellTriggers.OnUse, charges, 0, cooldown, category, categoryCooldown);

    private static ItemTemplate Item(uint entry, uint inventoryType = 0, uint stack = 1, uint itemClass = 0, uint bonding = 0, params ItemSpell[] spells)
        => new ItemTemplate
        {
            Entry = entry, Name = $"Item {entry}", DisplayId = entry, Class = itemClass, InventoryType = inventoryType, Stackable = stack,
            Bonding = bonding, Quality = 1, Spells = spells,
        }.Normalized();

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            SpellInfo[] spells =
            [
                CraftingTestKit.Craft(HealSpell, SpellTestKit.Effect(SpellEffectName.Heal, 30)),
                CraftingTestKit.Craft(EnergizeSpell, SpellTestKit.Effect(SpellEffectName.Energize, 50, misc: (int)PowerType.Rage)),
                CraftingTestKit.Craft(HealAndEnergizeSpell, SpellTestKit.Effect(SpellEffectName.Heal, 30), SpellTestKit.Effect(SpellEffectName.Energize, 50, misc: (int)PowerType.Rage)),
                CraftingTestKit.Craft(PlainSpell),
                CraftingTestKit.Craft(CostlySpell) with { PowerType = (int)PowerType.Rage, ManaCost = 30 },
                CraftingTestKit.Craft(CombatForbiddenSpell) with { Attributes = (SpellAttributes)(uint)SpellAttributesCombat.NotInCombatOnlyPeaceful },
            ];
            ItemTemplate[] items =
            [
                Item(Potion, stack: 5, itemClass: 0, spells: OnUse(HealSpell, charges: -1)),
                Item(ChargedWand, spells: OnUse(PlainSpell, charges: 3)),
                Item(SingleUseCharm, spells: OnUse(PlainSpell, charges: -3)),
                Item(BindOnUseTrinket, inventoryType: 12, bonding: (uint)ItemBonding.WhenUse, spells: OnUse(PlainSpell)),
                Item(ManaPotion, stack: 5, spells: OnUse(EnergizeSpell, charges: -1)),
                Item(RejuvPotion, stack: 5, spells: OnUse(HealAndEnergizeSpell, charges: -1)),
                Item(PeacefulItem, stack: 5, itemClass: 1, spells: OnUse(CombatForbiddenSpell, charges: -1)),
                Item(NonUseItem, inventoryType: 12, spells: new ItemSpell(PlainSpell, ItemSpellTriggers.OnEquip, 0, 0, -1, 0, -1)),
                Item(CategoryPotion, stack: 5, spells: OnUse(PlainSpell, charges: -1, category: 4, categoryCooldown: 120_000)),
                Item(Cloak, inventoryType: 16, spells: OnUse(PlainSpell)),
                Item(TwoSpellItem, spells: [OnUse(PlainSpell), OnUse(EnergizeSpell)]),
                Item(CostlyWand, spells: OnUse(CostlySpell, charges: 5)),
            ];
            Kit = new CraftingTestKit(spells, items);
            Service = ItemUseService.Install(Kit.System, (_, item) => InTrade.Contains(item.Guid));
            Kit.Player.MaxHealth = 100;
            Kit.Player.Health = 100;
            Kit.Player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 1000);
            SpellSystem.SetPower(Kit.Player, PowerType.Rage, 500);
        }

        public CraftingTestKit Kit { get; }

        public ItemUseService Service { get; }

        public HashSet<ObjectGuid> InTrade { get; } = [];

        public Player Player => Kit.Player;

        public PlayerInventory Inventory => Kit.Inventory;

        public Item Give(uint entry, uint count = 1)
        {
            Assert.Equal(InventoryResult.Ok, Inventory.AddItem(entry, count, out Item? item));
            return item!;
        }

        public Item Equip(uint entry, byte slot)
        {
            Item item = Give(entry);
            Inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, slot);
            Assert.Same(item, Inventory.GetItem(InventorySlots.Bag0, slot));
            return item;
        }

        public void Use(Item item, byte spellIndex = 0, SpellCastTargets? targets = null)
        {
            Kit.Session.Clear();
            Service.UseItem(Player, item.BagSlot, item.Slot, spellIndex, targets ?? SpellCastTargets.ForSelf());
        }

        public InventoryResult? LastEquipError()
        {
            byte[]? payload = SpellTestKit.Packets(Kit.Session, WorldOpcode.SmsgInventoryChangeFailure).LastOrDefault();
            return payload is null ? null : (InventoryResult)payload[0];
        }

        public SpellCastResult? LastCastResult()
        {
            byte[]? payload = SpellTestKit.Packets(Kit.Session, WorldOpcode.SmsgCastResult).LastOrDefault();
            return payload is null ? null : payload.Length < 6 ? SpellCastResult.CastOk : (SpellCastResult)payload[5];
        }

        public void Dispose() => Kit.Dispose();
    }

    // --- HandleUseItemOpcode ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptySlot_AnswersItemNotFound()
    {
        using var rig = new Rig();

        rig.Kit.Session.Clear();
        rig.Service.UseItem(rig.Player, InventorySlots.Bag0, InventorySlots.ItemStart + 3, 0, SpellCastTargets.ForSelf());

        Assert.Equal(InventoryResult.ItemNotFound, rig.LastEquipError());
    }

    [Theory]
    [InlineData(5)]    // beyond the five spell slots
    [InlineData(1)]    // a slot without a spell
    public void ASpellIndex_WithoutAnItemSpell_AnswersItemNotFound_AndNothingIsConsumed(byte index)
    {
        using var rig = new Rig();
        Item potion = rig.Give(Potion, 2);
        rig.Player.Health = 10;

        rig.Use(potion, index);

        Assert.Equal(InventoryResult.ItemNotFound, rig.LastEquipError());
        Assert.Equal(2u, rig.Inventory.GetItemCount(Potion));
    }

    [Fact]
    public void ASpellThatIsNotOnUse_AnswersItemNotFound()
    {
        using var rig = new Rig();
        Item item = rig.Equip(NonUseItem, InventorySlots.Trinket1);

        rig.Use(item);

        Assert.Equal(InventoryResult.ItemNotFound, rig.LastEquipError());
    }

    [Fact]
    public void AnEquippableItem_OnlyWorksWhenWorn()
    {
        using var rig = new Rig();
        Item inBag = rig.Give(BindOnUseTrinket);

        rig.Use(inBag);
        Assert.Equal(InventoryResult.ItemNotFound, rig.LastEquipError());
        Assert.False(inBag.IsSoulBound);

        Item worn = rig.Equip(Cloak, InventorySlots.Back);
        rig.Use(worn);
        Assert.Null(rig.LastEquipError());
        Assert.Equal(SpellCastResult.CastOk, rig.LastCastResult());
    }

    [Fact]
    public void AnItemInTheTradeWindow_IsRefused()
    {
        using var rig = new Rig();
        Item potion = rig.Give(Potion, 2);
        rig.Player.Health = 10;
        rig.InTrade.Add(potion.Guid);

        rig.Use(potion);

        Assert.Equal(InventoryResult.ItemNotFound, rig.LastEquipError());
        Assert.Equal(2u, rig.Inventory.GetItemCount(Potion));
    }

    [Fact]
    public void ANonCombatSpellItem_InCombat_AnswersNotInCombat()
    {
        using var rig = new Rig();
        Item food = rig.Give(PeacefulItem, 2);
        rig.Player.UnitFlags |= UnitFlags.InCombat;

        rig.Use(food);

        Assert.Equal(InventoryResult.NotInCombat, rig.LastEquipError());
        Assert.Equal(2u, rig.Inventory.GetItemCount(PeacefulItem));

        rig.Player.UnitFlags &= ~UnitFlags.InCombat;
        rig.Use(food);
        Assert.Equal(1u, rig.Inventory.GetItemCount(PeacefulItem));
    }

    [Fact]
    public void ABindOnUseItem_BecomesSoulbound_WhenUsed()
    {
        using var rig = new Rig();
        Item trinket = rig.Equip(BindOnUseTrinket, InventorySlots.Trinket1);
        Assert.False(trinket.IsSoulBound);

        rig.Use(trinket);

        Assert.True(trinket.IsSoulBound);
    }

    [Fact]
    public void AShapeshiftedPlayer_CannotUseABagItem_ButCanUseAWornOne()
    {
        using var rig = new Rig();
        rig.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);   // bear form
        Item potion = rig.Give(Potion, 2);
        rig.Player.Health = 10;

        rig.Use(potion);

        Assert.Equal(SpellCastResult.NoItemsWhileShapeshifted, rig.LastCastResult());
        Assert.Equal(InventoryResult.None, rig.LastEquipError());   // frees a grey item after a failed use
        Assert.Equal(2u, rig.Inventory.GetItemCount(Potion));

        Item worn = rig.Equip(BindOnUseTrinket, InventorySlots.Trinket1);   // client patch 1.10.0: all forms can use equipped items
        rig.Use(worn);
        Assert.True(worn.IsSoulBound);
    }

    // --- the cast item checks ----------------------------------------------------------------------------------------

    [Fact]
    public void ARejuvenationPotion_AtFullHealth_IsRefused_AndNotConsumed()
    {
        using var rig = new Rig();
        Item potion = rig.Give(Potion, 2);

        rig.Use(potion);

        Assert.Equal(SpellCastResult.AlreadyAtFullHealth, rig.LastCastResult());
        Assert.Equal(2u, rig.Inventory.GetItemCount(Potion));
        Assert.Equal(100u, rig.Player.Health);
    }

    [Fact]
    public void AHealingPotion_HealsAndLosesOneFromTheStack()
    {
        using var rig = new Rig();
        Item potion = rig.Give(Potion, 2);
        rig.Player.Health = 40;

        rig.Use(potion);

        Assert.Equal(70u, rig.Player.Health);
        Assert.Equal(1u, rig.Inventory.GetItemCount(Potion));
    }

    [Fact]
    public void AManaPotion_AtFullPower_IsRefused_ButWorksBelowMax()
    {
        using var rig = new Rig();
        Item potion = rig.Give(ManaPotion, 2);
        SpellSystem.SetPower(rig.Player, PowerType.Rage, 1000);

        rig.Use(potion);
        Assert.Equal(SpellCastResult.AlreadyAtFullPower, rig.LastCastResult());
        Assert.Equal(2u, rig.Inventory.GetItemCount(ManaPotion));

        SpellSystem.SetPower(rig.Player, PowerType.Rage, 100);
        rig.Use(potion);
        Assert.Equal(150u, SpellSystem.GetPower(rig.Player, PowerType.Rage));
        Assert.Equal(1u, rig.Inventory.GetItemCount(ManaPotion));
    }

    [Theory]
    [InlineData(100u, 1000u, false)]   // full health, full rage: no effect usable
    [InlineData(40u, 1000u, true)]     // heal usable: the energize is not needed (Spell.cpp:7131-7165 ends at the first usable effect)
    [InlineData(100u, 100u, true)]     // rage usable: a later effect clears the earlier health refusal
    public void ARejuvenationPotion_IsRefusedOnlyWhenNoEffectIsUsable(uint health, uint rage, bool allowed)
    {
        using var rig = new Rig();
        Item potion = rig.Give(RejuvPotion, 2);
        rig.Player.Health = health;
        SpellSystem.SetPower(rig.Player, PowerType.Rage, rage);

        rig.Use(potion);

        Assert.Equal(allowed ? 1u : 2u, rig.Inventory.GetItemCount(RejuvPotion));
        if (!allowed)
        {
            Assert.Equal(SpellCastResult.AlreadyAtFullPower, rig.LastCastResult());   // the last refusal seen
        }
    }

    [Fact]
    public void LimitedChargesCountDown_ThenTheItemRefusesWithNoChargesRemain()
    {
        using var rig = new Rig();
        Item wand = rig.Give(ChargedWand);
        Assert.Equal(3, wand.GetInt32(UpdateFields.ItemFieldSpellCharges));

        for (int expected = 2; expected >= 0; expected--)
        {
            rig.Use(wand);
            Assert.Equal(expected, wand.GetInt32(UpdateFields.ItemFieldSpellCharges));
        }

        Assert.NotNull(rig.Inventory.GetItemByGuid(wand.Guid));   // positive charges: the item is not destroyed

        rig.Use(wand);
        Assert.Equal(SpellCastResult.NoChargesRemain, rig.LastCastResult());
    }

    [Fact]
    public void NegativeCharges_MeanExpendable_AndTheItemIsDestroyedAtZero()
    {
        using var rig = new Rig();
        Item charm = rig.Give(SingleUseCharm);
        Assert.Equal(-3, charm.GetInt32(UpdateFields.ItemFieldSpellCharges));

        rig.Use(charm);
        rig.Use(charm);
        Assert.Equal(-1, charm.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.NotNull(rig.Inventory.GetItemByGuid(charm.Guid));

        rig.Use(charm);
        Assert.Null(rig.Inventory.GetItemByGuid(charm.Guid));
    }

    [Fact]
    public void AStackableItem_KeepsNoChargeOnTheItem_ButLosesOneOfTheStack()
    {
        using var rig = new Rig();
        Item potion = rig.Give(CategoryPotion, 3);

        rig.Use(potion);

        Assert.Equal(2u, rig.Inventory.GetItemCount(CategoryPotion));
        Assert.Equal(-1, potion.GetInt32(UpdateFields.ItemFieldSpellCharges));   // stackable: SetSpellCharges is skipped (Stackable < 2 only)
    }

    // --- the item cooldown ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheItemCategoryCooldown_BlocksTheNextUse_UntilItExpires()
    {
        using var rig = new Rig();
        Item potion = rig.Give(CategoryPotion, 3);

        rig.Use(potion);
        Assert.Equal(2u, rig.Inventory.GetItemCount(CategoryPotion));

        rig.Use(potion);   // the spell has no category: only the item data (category 4, 120 s) can refuse this
        Assert.Equal(SpellCastResult.NotReady, rig.LastCastResult());
        Assert.Equal(2u, rig.Inventory.GetItemCount(CategoryPotion));

        rig.Kit.Kit.Now += 120_001;
        rig.Kit.System.Update(100);
        rig.Use(potion);
        Assert.Equal(1u, rig.Inventory.GetItemCount(CategoryPotion));
    }

    [Fact]
    public void PickCooldowns_TakesTheItemValues_OnlyWhenSetAndNotNegative()
    {
        var spell = new SpellInfo { Id = 7, Category = 9, RecoveryTime = 1000, CategoryRecoveryTime = 2000 };
        var item = new Item(1, Item(1, spells: [OnUse(7, cooldown: -1, category: 0, categoryCooldown: -1)]), ObjectGuid.Empty);
        Assert.Equal(new ItemSpellCooldown(9, 1000, 2000), ItemSpellCooldowns.Pick(spell, item));
        Assert.Equal(new ItemSpellCooldown(9, 1000, 2000), ItemSpellCooldowns.Pick(spell, null));

        var overridden = new Item(2, Item(2, spells: [OnUse(7, cooldown: 0, category: 4, categoryCooldown: 60_000)]), ObjectGuid.Empty);
        Assert.Equal(new ItemSpellCooldown(4, 0, 60_000), ItemSpellCooldowns.Pick(spell, overridden));

        var otherSpell = new Item(3, Item(3, spells: [OnUse(8, cooldown: 5, category: 4, categoryCooldown: 6)]), ObjectGuid.Empty);
        Assert.Equal(new ItemSpellCooldown(9, 1000, 2000), ItemSpellCooldowns.Pick(spell, otherSpell));
    }

    // --- several spells and triggered casts -------------------------------------------------------------------------------

    [Fact]
    public void AnItemWithTwoUseSpells_CastsBoth_TheSecondTriggered_AndConsumesNothingTwice()
    {
        using var rig = new Rig();
        Item item = rig.Give(TwoSpellItem);
        SpellSystem.SetPower(rig.Player, PowerType.Rage, 100);

        rig.Use(item);

        Assert.Equal(150u, SpellSystem.GetPower(rig.Player, PowerType.Rage));   // the second (energize) spell ran
        Assert.NotNull(rig.Inventory.GetItemByGuid(item.Guid));
    }

    [Fact]
    public void ATriggeredCast_NeverTakesTheCastItem()
    {
        using var rig = new Rig();
        Item charm = rig.Give(SingleUseCharm);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastItemSpell(rig.Player, charm, PlainSpell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(-3, charm.GetInt32(UpdateFields.ItemFieldSpellCharges));
        Assert.NotNull(rig.Inventory.GetItemByGuid(charm.Guid));
    }

    [Fact]
    public void AnItemCast_TakesNoPower()
    {
        // vmangos Spell::TakePower returns at once when the spell has a cast item (Spell.cpp:5053).
        using var rig = new Rig();
        Item wand = rig.Give(CostlyWand);
        SpellSystem.SetPower(rig.Player, PowerType.Rage, 500);

        rig.Use(wand);

        Assert.Equal(4, wand.GetInt32(UpdateFields.ItemFieldSpellCharges));   // the cast happened
        Assert.Equal(500u, SpellSystem.GetPower(rig.Player, PowerType.Rage));  // and cost no power
    }

    [Fact]
    public void InstallingTheCheckTwice_Throws()
    {
        using var rig = new Rig();

        Assert.Throws<InvalidOperationException>(() => ItemUseService.Install(rig.Kit.System));
    }
}
