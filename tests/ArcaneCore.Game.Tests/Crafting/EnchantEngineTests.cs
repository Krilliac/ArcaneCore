using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice enchant-core: the enchantment engine after vmangos Player::ApplyEnchantment (Objects/Player.cpp:11739-11884), the duration list
/// (:11619-11731) and Item::SetEnchantment (Objects/Item.cpp:1028-1090). The catalog rows are synthetic with the DBC field meanings of
/// DBCStructure.h:639-651; the item and spell numbers are fixtures.
/// </summary>
public sealed class EnchantEngineTests
{
    private const uint Sword = 95101;
    private const uint Dagger = 95102;
    private const uint Ring = 95103;
    private const uint Staff = 95104;

    private const uint StrengthRing = 701;        // STAT, +4, ITEM_MOD_STRENGTH (4)
    private const uint FireResist = 702;          // RESISTANCE, +5, school 2 (fire)
    private const uint Damage3 = 703;             // DAMAGE, +3
    private const uint EquipSpellEnchant = 704;   // EQUIP_SPELL, spell 95201 (+10 strength aura)
    private const uint Rockbiter = 705;           // TOTEM, amount 6 per second of weapon delay
    private const uint HealthMana = 706;          // STAT +50 health (1) and STAT +30 mana (0)
    private const uint Crusader = 707;            // COMBAT_SPELL: inert
    private const uint AuraSpell = 95201;

    private static EnchantCatalog Catalog() => new(
    [
        Row(StrengthRing, (EnchantEffectType.Stat, 4, (uint)ItemStatType.Strength)),
        Row(FireResist, (EnchantEffectType.Resistance, 5, 2)),
        Row(Damage3, (EnchantEffectType.Damage, 3, 0)),
        Row(EquipSpellEnchant, (EnchantEffectType.EquipSpell, 0, AuraSpell)),
        Row(Rockbiter, (EnchantEffectType.Totem, 6, 0)),
        Row(HealthMana, (EnchantEffectType.Stat, 50, (uint)ItemStatType.Health), (EnchantEffectType.Stat, 30, (uint)ItemStatType.Mana)),
        Row(Crusader, (EnchantEffectType.CombatSpell, 0, 20007)),
    ]);

    private static SpellItemEnchantment Row(uint id, params (EnchantEffectType Type, int Amount, uint Arg)[] effects)
    {
        uint[] types = new uint[3];
        int[] amounts = new int[3];
        uint[] args = new uint[3];
        for (int i = 0; i < effects.Length; i++)
        {
            (types[i], amounts[i], args[i]) = ((uint)effects[i].Type, effects[i].Amount, effects[i].Arg);
        }

        return new SpellItemEnchantment(id, types, amounts, args, $"Enchant {id}", 0, 0);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool statSystem = false)
        {
            ItemTemplate[] items =
            [
                new() { Entry = Sword, Class = 2, SubClass = 7, Name = "Test Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = 50, Quality = 2, Damages = [new ItemDamage(10, 20, 0)] },
                new() { Entry = Dagger, Class = 2, SubClass = 15, Name = "Test Dagger", DisplayId = 2, InventoryType = 13, Delay = 1500, MaxDurability = 50, Quality = 2, Damages = [new ItemDamage(5, 8, 0)] },
                new() { Entry = Ring, Class = 4, SubClass = 0, Name = "Test Ring", DisplayId = 3, InventoryType = 11, Quality = 2 },
                new() { Entry = Staff, Class = 2, SubClass = 10, Name = "Test Staff", DisplayId = 4, InventoryType = 17, Delay = 3000, MaxDurability = 50, Quality = 2, Damages = [new ItemDamage(20, 30, 0)] },
            ];
            SpellInfo aura = CraftingTestKit.Craft(AuraSpell, SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitCaster, AuraType.ModStat, misc: 0)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
            };
            Kit = new CraftingTestKit([aura], items);
            Player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 100);
            Player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
            if (statSystem)
            {
                Stats = new PlayerStatSystem();
                Stats.Attach(Player);
            }

            Enchantments = new PlayerEnchantments(Player, Catalog(), Kit.System);
            Player.AttachEnchantments(Enchantments);
            Inventory.StatsApplier = new EnchantStatsApplier(Inventory.StatsApplier);
        }

        public CraftingTestKit Kit { get; }

        public PlayerStatSystem? Stats { get; }

        public PlayerEnchantments Enchantments { get; }

        public Player Player => Kit.Player;

        public PlayerInventory Inventory => Kit.Inventory;

        public Item Give(uint entry) => Kit.Give(entry);

        public Item Equip(uint entry, byte slot)
        {
            Item item = Give(entry);
            Inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, slot);
            Assert.Same(item, Inventory.GetItem(InventorySlots.Bag0, slot));
            return item;
        }

        public uint Stat(int index) => Player.GetUInt32(UpdateFields.UnitFieldStat0 + index);

        public static void Enchant(Item item, uint id, int slot = EnchantSlots.Permanent, uint duration = 0, uint charges = 0)
            => ItemEnchantments.Set(item, slot, id, duration, charges);

        public List<byte[]> Sent(WorldOpcode opcode) => SpellTestKit.Packets(Kit.Session, opcode);

        public void Dispose() => Kit.Dispose();
    }

    // --- apply and remove ------------------------------------------------------------------------------------------

    [Fact]
    public void AStatEnchantment_RaisesTheStat_WhileWorn_AndUnequippingRestoresItExactly()
    {
        using var rig = new Rig();
        Item ring = rig.Give(Ring);
        Rig.Enchant(ring, StrengthRing);
        uint before = rig.Stat(0);
        uint posBefore = rig.Player.GetUInt32(UpdateFields.PlayerFieldPosstat0);

        rig.Inventory.SwapItem(ring.BagSlot, ring.Slot, InventorySlots.Bag0, InventorySlots.Finger1);
        Assert.Equal(before + 4, rig.Stat(0));
        Assert.Equal(posBefore + 4, rig.Player.GetUInt32(UpdateFields.PlayerFieldPosstat0));

        rig.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.Finger1, InventorySlots.Bag0, InventorySlots.ItemStart + 5);
        Assert.Equal(before, rig.Stat(0));
        Assert.Equal(posBefore, rig.Player.GetUInt32(UpdateFields.PlayerFieldPosstat0));
    }

    [Fact]
    public void AnUnworn_Item_AppliesNothing()
    {
        using var rig = new Rig();
        Item ring = rig.Give(Ring);
        uint before = rig.Stat(0);

        Rig.Enchant(ring, StrengthRing);
        rig.Enchantments.Apply(ring, apply: true);

        Assert.Equal(before, rig.Stat(0));
        Assert.False(rig.Enchantments.IsApplied(ring, EnchantSlots.Permanent));
    }

    [Fact]
    public void ApplyingTwice_DoesNotDoubleTheEffect_AndRemovingWhatWasNotApplied_DoesNothing()
    {
        using var rig = new Rig();
        Item ring = rig.Equip(Ring, InventorySlots.Finger1);
        Rig.Enchant(ring, StrengthRing);
        uint before = rig.Stat(0);

        rig.Enchantments.Apply(ring, apply: true);
        rig.Enchantments.Apply(ring, apply: true);
        Assert.Equal(before + 4, rig.Stat(0));

        rig.Enchantments.Apply(ring, apply: false);
        rig.Enchantments.Apply(ring, apply: false);
        Assert.Equal(before, rig.Stat(0));
    }

    [Fact]
    public void ABrokenItem_AppliesNoEnchantment()
    {
        using var rig = new Rig();
        Item sword = rig.Give(Sword);
        sword.Durability = 0;
        Rig.Enchant(sword, StrengthRing);
        uint before = rig.Stat(0);

        rig.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.MainHand);

        Assert.Equal(before, rig.Stat(0));
    }

    [Fact]
    public void AnItemThatBreaks_LosesItsEnchantment_AndGetsItBackWhenRepaired()
    {
        using var rig = new Rig();
        Item sword = rig.Give(Sword);
        Rig.Enchant(sword, StrengthRing);
        uint before = rig.Stat(0);
        rig.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.Equal(before + 4, rig.Stat(0));

        rig.Inventory.DurabilityPointsLossAll((int)sword.MaxDurability, inventory: false);   // breaks it
        Assert.Equal(0u, sword.Durability);
        Assert.Equal(before, rig.Stat(0));
        Assert.False(rig.Enchantments.IsApplied(sword, EnchantSlots.Permanent));

        rig.Inventory.RepairDurability(sword);
        Assert.Equal(before + 4, rig.Stat(0));   // vmangos _ApplyItemMods skips a broken item and applies it again when it is whole
    }

    [Fact]
    public void AResistanceEnchantment_MovesTheResistanceOfItsSchool()
    {
        using var rig = new Rig();
        Item ring = rig.Give(Ring);
        Rig.Enchant(ring, FireResist);
        int fire = UpdateFields.UnitFieldResistances + 2;
        uint before = rig.Player.GetUInt32(fire);

        rig.Inventory.SwapItem(ring.BagSlot, ring.Slot, InventorySlots.Bag0, InventorySlots.Finger1);
        Assert.Equal(before + 5, rig.Player.GetUInt32(fire));

        rig.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.Finger1, InventorySlots.Bag0, InventorySlots.ItemStart + 5);
        Assert.Equal(before, rig.Player.GetUInt32(fire));
    }

    [Fact]
    public void HealthAndManaStatEnchantments_MoveTheMaximums()
    {
        using var rig = new Rig();
        Item ring = rig.Give(Ring);
        Rig.Enchant(ring, HealthMana);

        rig.Inventory.SwapItem(ring.BagSlot, ring.Slot, InventorySlots.Bag0, InventorySlots.Finger1);

        Assert.Equal(150u, rig.Player.GetUInt32(UpdateFields.UnitFieldMaxhealth));
        Assert.Equal(130u, rig.Player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
    }

    [Fact]
    public void ADamageEnchantment_AddsFlatDamage_ToTheMainHandOnly()
    {
        using var rig = new Rig(statSystem: true);
        Item sword = rig.Give(Sword);
        Rig.Enchant(sword, Damage3);
        rig.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        float min = rig.Player.GetFloat(UpdateFields.UnitFieldMindamage);

        Assert.Equal(3f, rig.Player.StatState.TotalDamage(WeaponAttackType.BaseAttack));
        Assert.Equal(0f, rig.Player.StatState.TotalDamage(WeaponAttackType.OffAttack));
        Assert.Equal(0f, rig.Player.StatState.TotalDamage(WeaponAttackType.RangedAttack));

        rig.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.MainHand, InventorySlots.Bag0, InventorySlots.ItemStart + 5);
        Assert.Equal(0f, rig.Player.StatState.TotalDamage(WeaponAttackType.BaseAttack));
        Assert.True(rig.Player.GetFloat(UpdateFields.UnitFieldMindamage) < min);   // the weapon (and its +3) are gone
    }

    [Fact]
    public void TheMainHandDamageField_IncludesTheEnchantment_ThroughTheStatSystem()
    {
        using var plain = new Rig(statSystem: true);
        Item a = plain.Give(Sword);
        plain.Inventory.SwapItem(a.BagSlot, a.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        float without = plain.Player.GetFloat(UpdateFields.UnitFieldMindamage);

        using var enchanted = new Rig(statSystem: true);
        Item b = enchanted.Give(Sword);
        Rig.Enchant(b, Damage3);
        enchanted.Inventory.SwapItem(b.BagSlot, b.Slot, InventorySlots.Bag0, InventorySlots.MainHand);

        Assert.Equal(without + 3f, enchanted.Player.GetFloat(UpdateFields.UnitFieldMindamage), 0.01f);
    }

    [Fact]
    public void ATotemEnchantment_AddsRockbiterDamage_ForAShamanOnly()
    {
        using var warrior = new Rig();
        Item w = warrior.Give(Sword);
        Rig.Enchant(w, Rockbiter);
        warrior.Inventory.SwapItem(w.BagSlot, w.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.Equal(0f, warrior.Player.StatState.TotalDamage(WeaponAttackType.BaseAttack));

        using var shaman = new Rig();
        shaman.Player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Shaman);
        Item s = shaman.Give(Sword);   // delay 2000 ms
        Rig.Enchant(s, Rockbiter);
        shaman.Inventory.SwapItem(s.BagSlot, s.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.Equal(12f, shaman.Player.StatState.TotalDamage(WeaponAttackType.BaseAttack));   // 6 * 2000 / 1000
    }

    [Fact]
    public void AnEquipSpellEnchantment_CastsItsSpellAtTheOwner_WithTheItem_AndRemovesItWithTheItem()
    {
        using var rig = new Rig();
        Item ring = rig.Give(Ring);
        Rig.Enchant(ring, EquipSpellEnchant);
        uint before = rig.Stat(0);

        rig.Inventory.SwapItem(ring.BagSlot, ring.Slot, InventorySlots.Bag0, InventorySlots.Finger1);

        SpellAuraHolder holder = Assert.Single(rig.Kit.System.GetAuras(rig.Player), h => h.Spell.Id == AuraSpell);
        Assert.Equal(ring.Guid, holder.CastItemGuid);
        Assert.Equal(before + 10, rig.Stat(0));

        rig.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.Finger1, InventorySlots.Bag0, InventorySlots.ItemStart + 5);
        Assert.DoesNotContain(rig.Kit.System.GetAuras(rig.Player), h => h.Spell.Id == AuraSpell);
        Assert.Equal(before, rig.Stat(0));
    }

    [Fact]
    public void ACombatSpellEnchantment_IsInert_OnApply()
    {
        using var rig = new Rig();
        Item sword = rig.Give(Sword);
        Rig.Enchant(sword, Crusader);
        uint before = rig.Stat(0);

        rig.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.MainHand);

        Assert.Equal(before, rig.Stat(0));
        Assert.Empty(rig.Kit.System.GetAuras(rig.Player));
    }

    [Fact]
    public void TheVisibleItemField_MirrorsTheEnchantmentId_OnlyForInspectedSlots_WhenWorn()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        int visible = UpdateFields.PlayerVisibleItem10 + (InventorySlots.MainHand * 12);

        Rig.Enchant(sword, Crusader, EnchantSlots.Permanent);
        rig.Enchantments.Apply(sword, EnchantSlots.Permanent, apply: true);
        Assert.Equal(Crusader, rig.Player.GetUInt32(visible + 1));

        Rig.Enchant(sword, StrengthRing, EnchantSlots.Property0);
        rig.Enchantments.Apply(sword, EnchantSlots.Property0, apply: true);
        Assert.Equal(0u, rig.Player.GetUInt32(visible + 1 + EnchantSlots.Property0));   // slot 3 is not an inspected slot (Player.cpp:11866)

        rig.Enchantments.Apply(sword, EnchantSlots.Permanent, apply: false);
        Assert.Equal(0u, rig.Player.GetUInt32(visible + 1));
    }

    // --- Item::SetEnchantment --------------------------------------------------------------------------------------

    [Fact]
    public void Set_WithACaster_LogsTheOldEnchantmentFading_ThenTheNewOne_ToTheOwner()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Rig.Enchant(sword, Damage3);
        rig.Kit.Session.Clear();

        ItemEnchantments.Set(sword, EnchantSlots.Permanent, StrengthRing, 0, 0, rig.Player.Guid);

        List<byte[]> logs = rig.Sent(WorldOpcode.SmsgEnchantmentlog);
        Assert.Equal(2, logs.Count);
        Assert.Equal((ulong)0, BitConverter.ToUInt64(logs[0], 8));            // the fade has no caster
        Assert.Equal(Damage3, BitConverter.ToUInt32(logs[0], 20));
        Assert.Equal(rig.Player.Guid.Value, BitConverter.ToUInt64(logs[1], 8));
        Assert.Equal(StrengthRing, BitConverter.ToUInt32(logs[1], 20));
        Assert.Equal(sword.Entry, BitConverter.ToUInt32(logs[1], 16));
        Assert.Equal(0, logs[1][24]);                                         // to the owner: no affiliation flag
        Assert.Equal(StrengthRing, ItemEnchantments.Id(sword, EnchantSlots.Permanent));
    }

    [Fact]
    public void Set_WithTheSameTriple_WritesNothing_AndWithoutACaster_LogsNothing()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Rig.Enchant(sword, Damage3);
        rig.Kit.Session.Clear();

        ItemEnchantments.Set(sword, EnchantSlots.Permanent, Damage3, 0, 0, rig.Player.Guid);
        ItemEnchantments.Set(sword, EnchantSlots.Property0, StrengthRing, 0, 0);

        Assert.Empty(rig.Sent(WorldOpcode.SmsgEnchantmentlog));   // unchanged triple; a non-inspected slot / no caster
    }

    [Fact]
    public void Clear_SendsTheFadeLog_OnlyWhenAskedAndOnlyForInspectedSlots()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Rig.Enchant(sword, Damage3);
        Rig.Enchant(sword, StrengthRing, EnchantSlots.Property0);
        rig.Kit.Session.Clear();

        ItemEnchantments.Clear(sword, EnchantSlots.Property0, sendToClient: true);
        Assert.Empty(rig.Sent(WorldOpcode.SmsgEnchantmentlog));
        Assert.Equal(0u, ItemEnchantments.Id(sword, EnchantSlots.Property0));

        ItemEnchantments.Clear(sword, EnchantSlots.Permanent, sendToClient: true);
        Assert.Single(rig.Sent(WorldOpcode.SmsgEnchantmentlog));
        Assert.Equal(0u, ItemEnchantments.Id(sword, EnchantSlots.Permanent));
    }

    // --- temporary enchantment timers --------------------------------------------------------------------------------

    [Fact]
    public void ATemporaryEnchantment_CountsDown_PerUpdate_AndTheTimeUpdateGoesToTheClient()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Rig.Enchant(sword, StrengthRing, EnchantSlots.Temporary, duration: 600_000);
        rig.Kit.Session.Clear();

        rig.Enchantments.Apply(sword, EnchantSlots.Temporary, apply: true);

        byte[] update = Assert.Single(rig.Sent(WorldOpcode.SmsgItemEnchantTimeUpdate));
        Assert.Equal(sword.Guid.Value, BitConverter.ToUInt64(update, 0));
        Assert.Equal((uint)EnchantSlots.Temporary, BitConverter.ToUInt32(update, 8));
        Assert.Equal(600u, BitConverter.ToUInt32(update, 12));   // whole seconds
        Assert.Equal(rig.Player.Guid.Value, BitConverter.ToUInt64(update, 16));
        Assert.Equal(600_000u, rig.Enchantments.LeftMs(sword, EnchantSlots.Temporary));

        rig.Enchantments.Update(1000);
        rig.Enchantments.Update(2500);

        Assert.Equal(596_500u, rig.Enchantments.LeftMs(sword, EnchantSlots.Temporary));
        Assert.Equal(600_000u, ItemEnchantments.Duration(sword, EnchantSlots.Temporary));   // the field the client shows is not rewritten
    }

    [Fact]
    public void AnExpiredTemporaryEnchantment_IsRemovedFromTheStats_ClearedAndLogged()
    {
        using var rig = new Rig();
        Item sword = rig.Give(Sword);
        uint before = rig.Stat(0);
        Rig.Enchant(sword, StrengthRing, EnchantSlots.Temporary, duration: 5000);
        rig.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.Equal(before + 4, rig.Stat(0));
        rig.Kit.Session.Clear();

        rig.Enchantments.Update(4999);
        Assert.Equal(before + 4, rig.Stat(0));
        rig.Enchantments.Update(1);

        Assert.Equal(before, rig.Stat(0));
        Assert.Equal(0u, ItemEnchantments.Id(sword, EnchantSlots.Temporary));
        Assert.Equal(0, rig.Enchantments.TrackedCount);
        byte[] fade = Assert.Single(rig.Sent(WorldOpcode.SmsgEnchantmentlog));
        Assert.Equal((ulong)0, BitConverter.ToUInt64(fade, 8));
        Assert.Equal(StrengthRing, BitConverter.ToUInt32(fade, 20));
    }

    [Fact]
    public void ARunningTimer_IsSavedWithTheItem_AndAReloadedItemResumesWithTheTimeThatWasLeft()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Rig.Enchant(sword, StrengthRing, EnchantSlots.Temporary, duration: 600_000);
        rig.Enchantments.Apply(sword, EnchantSlots.Temporary, apply: true);
        rig.Enchantments.Update(30_000);

        ItemInstanceData saved = sword.ToData();

        Assert.Equal(StrengthRing, saved.Enchantments[(EnchantSlots.Temporary * 3) + 0]);
        Assert.Equal(570_000u, saved.Enchantments[(EnchantSlots.Temporary * 3) + 1]);
        var reloaded = new Item(sword.Guid.Low, sword.Template, rig.Player.Guid);
        reloaded.Load(saved);
        Assert.Equal(570_000u, ItemEnchantments.Duration(reloaded, EnchantSlots.Temporary));
    }

    [Fact]
    public void AnItemInABag_KeepsItsTimer_AndAnItemThatLeavesStopsIt()
    {
        using var rig = new Rig();
        Item sword = rig.Give(Sword);
        Rig.Enchant(sword, StrengthRing, EnchantSlots.Temporary, duration: 60_000);

        rig.Enchantments.Update(1000);   // the sweep finds the enchanted item in the bag and starts its timer
        Assert.Equal(60_000u, rig.Enchantments.LeftMs(sword, EnchantSlots.Temporary));

        rig.Enchantments.Update(1000);
        Assert.Equal(59_000u, rig.Enchantments.LeftMs(sword, EnchantSlots.Temporary));

        rig.Inventory.DestroyItem(sword.BagSlot, sword.Slot);   // the item leaves: the next sweep drops the timer and writes the time back
        rig.Enchantments.Update(1000);
        Assert.Equal(0, rig.Enchantments.TrackedCount);
        Assert.Equal(58_000u, ItemEnchantments.Duration(sword, EnchantSlots.Temporary));
    }

    [Fact]
    public void TheLoginTimeUpdates_AreSentOnceAfterThePlayerIsInTheWorld()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Rig.Enchant(sword, StrengthRing, EnchantSlots.Temporary, duration: 90_000);
        rig.Enchantments.Attach();
        rig.Kit.Session.Clear();

        rig.Enchantments.Update(100);
        rig.Enchantments.Update(100);

        Assert.Single(rig.Sent(WorldOpcode.SmsgItemEnchantTimeUpdate));
    }

    [Fact]
    public void AttachingLate_AppliesTheEnchantmentsOfTheItemsAlreadyWorn()
    {
        using var kit = new CraftingTestKit([], [new ItemTemplate { Entry = Ring, Class = 4, Name = "Test Ring", DisplayId = 3, InventoryType = 11, Quality = 2 }]);
        Item ring = kit.Give(Ring);
        kit.Inventory.SwapItem(ring.BagSlot, ring.Slot, InventorySlots.Bag0, InventorySlots.Finger1);   // worn before the engine exists
        ItemEnchantments.Set(ring, EnchantSlots.Permanent, StrengthRing, 0, 0);
        uint before = kit.Player.GetUInt32(UpdateFields.UnitFieldStat0);
        var enchantments = new PlayerEnchantments(kit.Player, Catalog(), kit.System);
        kit.Player.AttachEnchantments(enchantments);
        kit.Inventory.StatsApplier = new EnchantStatsApplier(kit.Inventory.StatsApplier);

        enchantments.Attach();
        Assert.Equal(before + 4, kit.Player.GetUInt32(UpdateFields.UnitFieldStat0));

        kit.Inventory.SwapItem(InventorySlots.Bag0, InventorySlots.Finger1, InventorySlots.Bag0, InventorySlots.ItemStart + 5);
        Assert.Equal(before, kit.Player.GetUInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void AttachingTwice_Throws()
    {
        using var rig = new Rig();

        Assert.Throws<InvalidOperationException>(() => rig.Player.AttachEnchantments(rig.Enchantments));
    }
}
