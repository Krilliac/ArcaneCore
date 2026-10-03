using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The equipped item requirement of a cast (vmangos Spell::CheckItems :7208-7236 over Player::HasItemFitToSpellReqirements :19753-19797):
/// fishing needs a fishing pole (weapon class 2, subclass 20) in a weapon slot, an unbroken one; the offhand weapon does not count for a main-hand
/// ability; shields count for armor requirements.
/// </summary>
public sealed class EquippedItemCastCheckTests
{
    private const uint FishingPole = 6256;
    private const uint Sword = 25;
    private const uint Dagger = 2092;
    private const uint Shield = 2362;
    private const uint Bow = 2504;
    private const uint Chest = 1000;
    private const uint FishingSpell = 7620;
    private const uint WeaponSpell = 9501;
    private const uint ArmorSpell = 9502;
    private const uint RangedSpell = 9503;
    private const uint OffhandSpell = 9504;
    private const uint FreeSpell = 9505;

    private static SpellInfo Requiring(uint id, int itemClass, int subClassMask, SpellDamageClass damage = SpellDamageClass.None, uint ex3 = 0) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            EquippedItemClass = itemClass,
            EquippedItemSubClassMask = subClassMask,
            DamageClass = damage,
            AttributesEx3 = ex3,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private sealed class DualWielder : IItemRequirements
    {
        public bool CanDualWield(PlayerInventory inventory) => true;

        public uint SkillValue(PlayerInventory inventory, uint skill) => 300;

        public bool HasSpell(PlayerInventory inventory, uint spellId) => true;

        public byte HonorRank(PlayerInventory inventory) => 0;

        public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;
    }

    private sealed class Rig : IDisposable
    {
        private static readonly ItemTemplateStore Store = new(
        [
            new ItemTemplate { Entry = FishingPole, Class = 2, SubClass = 20, Name = "Fishing Pole", DisplayId = 1, InventoryType = 17, Quality = 1 },
            new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Sword", DisplayId = 2, InventoryType = 21, MaxDurability = 20, Quality = 1 },
            new ItemTemplate { Entry = Dagger, Class = 2, SubClass = 15, Name = "Dagger", DisplayId = 3, InventoryType = 22, MaxDurability = 20, Quality = 1 },
            new ItemTemplate { Entry = Shield, Class = 4, SubClass = 6, Name = "Shield", DisplayId = 4, InventoryType = 14, Armor = 5, MaxDurability = 20, Quality = 1 },
            new ItemTemplate { Entry = Bow, Class = 2, SubClass = 2, Name = "Bow", DisplayId = 5, InventoryType = 15, MaxDurability = 20, Quality = 1 },
            new ItemTemplate { Entry = Chest, Class = 4, SubClass = 1, Name = "Tunic", DisplayId = 6, InventoryType = 5, Armor = 3, MaxDurability = 20, Quality = 1 },
        ], []);

        public Rig()
        {
            Kit = new SpellTestKit(
                Requiring(FishingSpell, 2, 1 << 20),
                Requiring(WeaponSpell, 2, 0),
                Requiring(ArmorSpell, 4, 1 << 6),
                Requiring(RangedSpell, 2, 0, SpellDamageClass.Ranged),
                Requiring(OffhandSpell, 2, 0, SpellDamageClass.Melee, 0x01000000),
                Requiring(FreeSpell, -1, 0));
            EquippedItemCastCheck.Install(Kit.System);
            (Player, Session) = Kit.AddPlayer(1);
            Player.Inventory.Templates = Store;
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.Inventory.Load([]);
            Player.Inventory.Requirements = new DualWielder();
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Item Equip(uint entry, byte slot)
        {
            Assert.Equal(InventoryResult.Ok, Player.Inventory.AddItem(entry, 1, out Item? item));
            Player.Inventory.SwapItem(item!.BagSlot, item.Slot, InventorySlots.Bag0, slot);
            Assert.Same(item, Player.Inventory.GetItem(InventorySlots.Bag0, slot));
            return item;
        }

        public SpellCastResult Cast(uint spell, bool triggered = false)
        {
            Session.Clear();
            Kit.Spellbook.Teach(Player, spell);
            return triggered
                ? Kit.System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered: true)
                : Kit.System.HandleCastRequest(Player, spell, SpellCastTargets.ForSelf());
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void FishingCast_WithoutAPole_AnswersEquippedItemClass()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(FishingSpell));
        byte[] reply = Assert.Single(SpellTestKit.Packets(rig.Session, ArcaneCore.Protocol.WorldOpcode.SmsgCastResult));
        Assert.Equal((byte)SpellCastResult.EquippedItemClass, reply[5]);
    }

    [Fact]
    public void FishingCast_WithThePoleInTheMainHand_Passes()
    {
        using var rig = new Rig();
        rig.Equip(FishingPole, InventorySlots.MainHand);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(FishingSpell));
    }

    [Fact]
    public void FishingCast_WithAnotherWeaponSubclass_Fails()
    {
        using var rig = new Rig();
        rig.Equip(Sword, InventorySlots.MainHand);
        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(FishingSpell));
    }

    [Fact]
    public void ABrokenItem_DoesNotSatisfyTheRequirement()
    {
        using var rig = new Rig();
        Item sword = rig.Equip(Sword, InventorySlots.MainHand);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(WeaponSpell));

        sword.Durability = 0;
        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(WeaponSpell));
    }

    [Fact]
    public void AMainHandAbility_IgnoresTheOffHandWeapon_ARangedOneDoesNot()
    {
        using var rig = new Rig();
        rig.Equip(Dagger, InventorySlots.OffHand);

        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(WeaponSpell));   // base attack: the off-hand weapon is ignored
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(RangedSpell));              // ranged attack: nothing is ignored
    }

    [Fact]
    public void AnOffHandAbility_NeedsTheOffHandWeapon_AndAnswersTheOffhandVariant()
    {
        using var rig = new Rig();
        rig.Equip(Sword, InventorySlots.MainHand);
        Assert.Equal(SpellCastResult.EquippedItemClassOffhand, rig.Cast(OffhandSpell));
    }

    [Fact]
    public void ARangedWeaponInTheRangedSlot_CountsForAWeaponRequirement()
    {
        using var rig = new Rig();
        rig.Equip(Bow, InventorySlots.Ranged);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(WeaponSpell));
    }

    [Fact]
    public void ArmorRequirements_CountShieldsInTheOffHand_AndArmorSlots()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.EquippedItemClass, rig.Cast(ArmorSpell));
        rig.Equip(Shield, InventorySlots.OffHand);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(ArmorSpell));
    }

    [Fact]
    public void ASpellWithoutARequirement_NeverAsks_AndATriggeredFailureIsNotReported()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(FreeSpell));

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(FishingSpell, triggered: true));
        Assert.Empty(SpellTestKit.Packets(rig.Session, ArcaneCore.Protocol.WorldOpcode.SmsgCastResult));
    }

    [Fact]
    public void ANonPlayerCaster_IsNotChecked()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, new EquippedItemCastCheck().Check(new SpellCastCheckContext(
            rig.Kit.System, new CombatTestUnit(level: 10), rig.Kit.Store.Get(FishingSpell)!, SpellCastTargets.ForSelf(), null, false, true)));
    }
}