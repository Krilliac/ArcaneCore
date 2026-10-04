using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Ranged lane S06: quivers and ammo pouches speed up ranged weapons. classic-db: Light Quiver 2101 carries ON_EQUIP spell 14824
/// (aura 141, +10), Ribbly's Quiver 2662 spell 14828 (+14), Gnoll Skin Bandolier 19320 spell 14829 (+15); the trigger is 1 (ON_EQUIP).
/// vmangos Player::ApplyEquipSpell (called for the bag slots too, Player.cpp:6828-6833) and Aura::HandleRangedAmmoHaste
/// (SpellAuras.cpp:5141-5166: only with a ranged weapon whose ammo_type is not 0). Expected numbers follow the float32 sequence of
/// vmangos (see AttackSpeedTests).
/// </summary>
public sealed class QuiverHasteTests
{
    private const uint LightQuiverSpell = 14824;   // +10
    private const uint RibblysSpell = 14828;       // +14
    private const uint BandolierSpell = 14829;     // +15

    private const uint Quiver10 = 94501;
    private const uint Quiver14 = 94502;
    private const uint Pouch15 = 94503;
    private const uint PlainBag = 94504;           // not a quiver: its equip spell is ignored by this rule

    private const uint Bow = 94510;
    private const uint Wand = 94511;
    private const uint Thrown = 94512;

    private static ItemSpell OnEquip(uint spell) => new(spell, 1, 0, 0, -1, 0, -1);

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = Quiver10, Class = 11, SubClass = 2, DisplayId = 21328, InventoryType = 18, ContainerSlots = 8, BagFamily = 1, Spells = [OnEquip(LightQuiverSpell)] },
        new() { Entry = Quiver14, Class = 11, SubClass = 2, DisplayId = 21329, InventoryType = 18, ContainerSlots = 8, BagFamily = 1, Spells = [OnEquip(RibblysSpell)] },
        new() { Entry = Pouch15, Class = 11, SubClass = 3, DisplayId = 21330, InventoryType = 18, ContainerSlots = 8, BagFamily = 2, Spells = [OnEquip(BandolierSpell)] },
        new() { Entry = PlainBag, Class = 1, SubClass = 0, DisplayId = 1281, InventoryType = 18, ContainerSlots = 6, Spells = [OnEquip(LightQuiverSpell)] },
        new() { Entry = Bow, Class = 2, SubClass = 2, DisplayId = 300, InventoryType = 15, Delay = 2500, MaxDurability = 40, AmmoType = 2 },
        new() { Entry = Wand, Class = 2, SubClass = 19, DisplayId = 302, InventoryType = 26, Delay = 1500, MaxDurability = 40, AmmoType = 0 },
        new() { Entry = Thrown, Class = 2, SubClass = 16, DisplayId = 303, InventoryType = 25, Delay = 2000, Stackable = 200, AmmoType = 4 },
    ];

    private static SpellInfo EquipSpell(uint id, int amount) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ModRangedAmmoHaste)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(EquipSpell(LightQuiverSpell, 10), EquipSpell(RibblysSpell, 14), EquipSpell(BandolierSpell, 15));
            (Player, _) = Kit.AddPlayer(1);
            Player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
            Quivers = new QuiverHaste(Kit.System);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public QuiverHaste Quivers { get; }

        public uint Ranged => Player.GetUInt32(UpdateFields.UnitFieldRangedattacktime);

        public void EquipWeapon(uint entry)
        {
            Item item = ItemTestData.Give(Player.Inventory, entry, entry == Thrown ? 20u : 1u);
            Assert.Equal(InventoryResult.Ok, Player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item.Template, item, swap: false));
            Player.Inventory.RemoveItem(item.BagSlot, item.Slot);
            Player.Inventory.EquipItem(dest, item);
        }

        public Item WearBag(uint entry)
        {
            Item bag = ItemTestData.Give(Player.Inventory, entry);
            Player.Inventory.AutoEquipItem(bag.BagSlot, bag.Slot);
            return bag;
        }

        public void Dispose() => Kit.Dispose();
    }

    private static float Hasten(float value, float percent) => value * (100.0f / (100.0f + percent));

    [Fact]
    public void EquippingAQuiverWithABow_ShortensTheRangedTime_AndUnequippingRestoresIt()
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.Quivers.Attach(rig.Player);

        Item quiver = rig.WearBag(Quiver10);

        Assert.Equal((uint)Hasten(2500f, 10f), rig.Ranged);
        Assert.Equal(Hasten(1f, 10f), rig.Player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.00001f);

        rig.Player.Inventory.RemoveItem(InventorySlots.Bag0, quiver.Slot);
        Assert.InRange(rig.Ranged, 2499u, 2500u);
        Assert.Equal(1.0f, rig.Player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.0001f);
    }

    [Theory]
    [InlineData(Quiver14, 14f)]
    [InlineData(Pouch15, 15f)]
    public void TheOtherQuiversAndPouches_CarryTheirOwnPercent(uint entry, float percent)
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.Quivers.Attach(rig.Player);

        rig.WearBag(entry);

        Assert.Equal((uint)Hasten(2500f, percent), rig.Ranged);
    }

    [Theory]
    [InlineData(Wand, false)]     // ammo_type 0: HandleRangedAmmoHaste refuses
    [InlineData(Thrown, true)]    // ammo_type 4: thrown weapons count as ammo users (retail quirk)
    [InlineData(0u, false)]       // no ranged weapon
    public void TheHasteNeedsARangedWeaponThatTakesAmmo(uint weapon, bool applies)
    {
        using var rig = new Rig();
        if (weapon != 0)
        {
            rig.EquipWeapon(weapon);
        }

        rig.Quivers.Attach(rig.Player);
        rig.WearBag(Quiver10);

        Assert.Equal(applies ? (uint)Hasten(2500f, 10f) : 2500u, rig.Ranged);
    }

    [Fact]
    public void ASwappedWeapon_DoesNotReEvaluateTheAura_AsInVmangos()
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.Quivers.Attach(rig.Player);
        rig.WearBag(Quiver10);
        uint hasted = rig.Ranged;

        rig.Player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        rig.EquipWeapon(Wand);

        Assert.Equal(hasted, rig.Ranged); // applied once, never revisited (SpellAuras.cpp:5141-5166)
    }

    [Fact]
    public void LoginReplay_AppliesAQuiverAlreadyWorn_ExactlyOnce_EvenWhenAttachedTwice()
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.WearBag(Quiver10); // worn before anything listens: the loaded character
        Assert.Equal(2500u, rig.Ranged);

        rig.Quivers.Attach(rig.Player);
        uint once = rig.Ranged;
        Assert.Equal((uint)Hasten(2500f, 10f), once);

        rig.Quivers.Attach(rig.Player); // a second attach must not stack the aura
        Assert.Equal(once, rig.Ranged);
    }

    [Fact]
    public void ASecondQuiver_IsRefused_AndNeverDoubleApplies()
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.Quivers.Attach(rig.Player);
        rig.WearBag(Quiver10);
        uint hasted = rig.Ranged;

        rig.WearBag(Pouch15); // CanEquipOnly1Quiver

        Assert.Equal(hasted, rig.Ranged);
    }

    [Fact]
    public void ABagThatIsNotAQuiver_IsIgnored()
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.Quivers.Attach(rig.Player);

        rig.WearBag(PlainBag);

        Assert.Equal(2500u, rig.Ranged); // only quivers: a general ON_EQUIP engine is a recorded limit
    }

    [Fact]
    public void QuiverAndRapidFire_StackMultiplicatively()
    {
        using var rig = new Rig();
        rig.EquipWeapon(Bow);
        rig.Quivers.Attach(rig.Player);
        rig.WearBag(Quiver10);
        rig.Player.Combat.ApplyAttackTimePercentMod(WeaponAttackType.RangedAttack, 40f, apply: true); // Rapid Fire

        Assert.Equal((uint)Hasten(Hasten(2500f, 10f), 40f), rig.Ranged);
    }
}
