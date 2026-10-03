using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>vmangos Player::CanUseAmmo/SetAmmo/RemoveAmmo (Player.cpp:10100-10160), CheckAmmoCompatibility and ammo DPS (7514-7570), Spell::TakeAmmo and WriteAmmoToPacket (Spell.cpp:4563-4587, 5130-5175).</summary>
public sealed class AmmoTests
{
    private const uint Bow = 96001; // ranged slot, bow (2)
    private const uint Gun = 96002; // gun (3)
    private const uint Wand = 96003; // wand (19)
    private const uint Crossbow = 96004; // crossbow (18)
    private const uint ThrownStack = 96005; // thrown, stack of 200
    private const uint ThrownSingle = 96006; // thrown, not stackable, durability 40
    private const uint Bullet = 96010;
    private const uint Arrow = 96011;
    private const uint HighArrow = 96012; // required level 5

    private static readonly ItemTemplate[] All =
        [
            .. Templates,
            new ItemTemplate { Entry = Bow, Class = 2, SubClass = 2, Name = "Bow", DisplayId = 10, InventoryType = 15, MaxDurability = 40, Delay = 2500 },
            new ItemTemplate { Entry = Gun, Class = 2, SubClass = 3, Name = "Gun", DisplayId = 11, InventoryType = 26, MaxDurability = 40 },
            new ItemTemplate { Entry = Wand, Class = 2, SubClass = 19, Name = "Wand", DisplayId = 12, InventoryType = 26, MaxDurability = 40 },
            new ItemTemplate { Entry = Crossbow, Class = 2, SubClass = 18, Name = "Crossbow", DisplayId = 13, InventoryType = 26, MaxDurability = 40 },
            new ItemTemplate { Entry = ThrownStack, Class = 2, SubClass = 16, Name = "Throwing Knife", DisplayId = 14, InventoryType = 25, Stackable = 200 },
            new ItemTemplate { Entry = ThrownSingle, Class = 2, SubClass = 16, Name = "Heavy Axe", DisplayId = 15, InventoryType = 25, MaxDurability = 40 },
            new ItemTemplate { Entry = Bullet, Class = 6, SubClass = 3, Name = "Bullet", DisplayId = 20, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(7, 11, 0)] },
            new ItemTemplate { Entry = Arrow, Class = 6, SubClass = 2, Name = "Arrow", DisplayId = 21, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(3, 5, 0)] },
            new ItemTemplate { Entry = HighArrow, Class = 6, SubClass = 2, Name = "High Arrow", DisplayId = 22, InventoryType = 24, Stackable = 200, RequiredLevel = 5 },
        ];

    private static readonly ItemTemplateStore Store = new(All, []);

    private static (Player Player, FakeSession Session) Make()
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        return (player, session);
    }

    private static void Equip(PlayerInventory inv, uint entry)
    {
        Item item = Give(inv, entry);
        Assert.True(inv.GetItem(item.BagSlot, item.Slot) is not null);
        inv.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.Same(item, inv.GetItem(InventorySlots.Bag0, InventorySlots.Ranged));
    }

    [Fact]
    public void SetAmmo_WritesPlayerAmmoIdField_AndZeroRemovesIt()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        inv.SetAmmo(Bullet);
        Assert.Equal(Bullet, inv.AmmoId);
        Assert.Equal(Bullet, player.GetUInt32(UpdateFields.PlayerAmmoId));

        inv.SetAmmo(Bullet); // already set: no-op
        inv.SetAmmo(0); // SetAmmo(0) does nothing; removal is RemoveAmmo
        Assert.Equal(Bullet, inv.AmmoId);

        inv.RemoveAmmo();
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerAmmoId));
    }

    [Fact]
    public void CanUseAmmo_RequiresAmmoInventoryType_ExistingEntry_AndAliveAndUsable()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        Assert.Equal(InventoryResult.OnlyAmmoCanGoHere, inv.CanUseAmmo(RecruitsShirt));
        Assert.Equal(InventoryResult.ItemNotFound, inv.CanUseAmmo(999999));
        Assert.Equal(InventoryResult.Ok, inv.CanUseAmmo(Bullet));

        inv.SetAmmo(RecruitsShirt);
        Assert.Equal([InventoryResult.OnlyAmmoCanGoHere], EquipErrors(session));
        Assert.Equal(0u, inv.AmmoId);

        player.Health = 0;
        Assert.Equal(InventoryResult.YouAreDead, inv.CanUseAmmo(Bullet));
    }

    [Fact]
    public void CanUseAmmo_HonoursTheRequiredLevel()
    {
        (Player player, FakeSession session) = Make();
        Assert.Equal(InventoryResult.CantEquipLevelI, player.Inventory.CanUseAmmo(HighArrow));
        player.Inventory.SetAmmo(HighArrow);
        Assert.Equal([InventoryResult.CantEquipLevelI], EquipErrors(session));
        Assert.Equal(0u, player.Inventory.AmmoId);
    }

    [Fact]
    public void Compatibility_BowAndCrossbowTakeArrows_GunTakesBullets_WandAndThrownNone()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        ItemTemplate arrow = Store.Find(Arrow)!;
        ItemTemplate bullet = Store.Find(Bullet)!;
        Assert.False(inv.CheckAmmoCompatibility(arrow)); // nothing equipped

        Equip(inv, Bow);
        Assert.True(inv.CheckAmmoCompatibility(arrow));
        Assert.False(inv.CheckAmmoCompatibility(bullet));
    }

    [Fact]
    public void Compatibility_GunCrossbowWandThrown()
    {
        ItemTemplate arrow = Store.Find(Arrow)!;
        ItemTemplate bullet = Store.Find(Bullet)!;
        foreach ((uint weapon, bool arrows, bool bullets) in new[]
        {
            (Gun, false, true), (Crossbow, true, false), (Wand, false, false), (ThrownStack, false, false),
        })
        {
            (Player player, _) = Make();
            Equip(player.Inventory, weapon);
            Assert.Equal(arrows, player.Inventory.CheckAmmoCompatibility(arrow));
            Assert.Equal(bullets, player.Inventory.CheckAmmoCompatibility(bullet));
        }
    }

    [Fact]
    public void AmmoDps_IsTheAverageOfDamage0_OnlyWhenFittingTheWeapon()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        inv.SetAmmo(Arrow);
        Assert.Equal(0f, inv.AmmoDps); // no weapon

        Equip(inv, Bow);
        Assert.Equal(4f, inv.AmmoDps); // (3 + 5) / 2

        inv.SetAmmo(Bullet);
        Assert.Equal(0f, inv.AmmoDps); // bullets do not fit a bow
    }

    [Fact]
    public void AmmoDps_BrokenWeaponGivesNone()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Equip(inv, Bow);
        inv.SetAmmo(Arrow);
        inv.DurabilityPointsLoss(inv.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!, 1000);
        Assert.Equal(0f, inv.AmmoDps);
    }

    [Fact]
    public void ConsumeRangedAmmo_ArrowDestroysOne_ExemptSpellAndWandNone()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Equip(inv, Bow);
        Give(inv, Arrow, 20);
        inv.SetAmmo(Arrow);

        inv.ConsumeRangedAmmo(75);
        Assert.Equal(19u, inv.GetItemCount(Arrow));
        inv.ConsumeRangedAmmo(2094); // Blind
        inv.ConsumeRangedAmmo(23577); // Expose Weakness
        Assert.Equal(19u, inv.GetItemCount(Arrow));

        (Player wander, _) = Make();
        Equip(wander.Inventory, Wand);
        Give(wander.Inventory, Arrow, 5);
        wander.Inventory.SetAmmo(Arrow);
        wander.Inventory.ConsumeRangedAmmo(5019);
        Assert.Equal(5u, wander.Inventory.GetItemCount(Arrow));
    }

    [Fact]
    public void ConsumeRangedAmmo_Thrown_StackableLosesOne_NonStackableLosesDurability()
    {
        (Player stackPlayer, _) = Make();
        PlayerInventory stack = stackPlayer.Inventory;
        Item knives = Give(stack, ThrownStack, 50);
        stack.AutoEquipItem(knives.BagSlot, knives.Slot);
        stack.ConsumeRangedAmmo(2764);
        Assert.Equal(49u, stack.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!.Count);

        (Player singlePlayer, _) = Make();
        PlayerInventory single = singlePlayer.Inventory;
        Equip(single, ThrownSingle);
        single.ConsumeRangedAmmo(2764);
        Assert.Equal(39u, single.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!.Durability);
    }

    [Fact]
    public void AmmoVisual_ThrownShowsTheWeapon_OtherwiseTheAmmo_ZeroWithoutRanged()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Assert.False(inv.TryGetAmmoVisual(out uint display, out uint type));
        Assert.Equal((0u, 0u), (display, type));

        Equip(inv, Bow);
        Assert.True(inv.TryGetAmmoVisual(out display, out type));
        Assert.Equal((0u, 15u), (display, type)); // no ammo selected: the weapon's inventory type only

        inv.SetAmmo(Arrow);
        Assert.True(inv.TryGetAmmoVisual(out display, out type));
        Assert.Equal((21u, 24u), (display, type));

        (Player thrower, _) = Make();
        Equip(thrower.Inventory, ThrownSingle);
        Assert.True(thrower.Inventory.TryGetAmmoVisual(out display, out type));
        Assert.Equal((15u, 25u), (display, type));
    }

    [Fact]
    public void Snapshot_CarriesTheAmmo_AndRestoreSetsItWithoutChecks()
    {
        (Player player, _) = Make();
        player.Inventory.SetAmmo(Bullet);
        Assert.Equal(Bullet, player.Inventory.CreateSnapshot().AmmoId);

        (Player again, _) = Make();
        again.Inventory.RestoreAmmo(Bullet);
        Assert.Equal(Bullet, again.Inventory.AmmoId);
    }

    [Fact]
    public void StartingItems_AmmoInTheBackpackBecomesTheSelectedAmmo()
    {
        (Player player, _) = CreatePlayer();
        var starting = new ItemTemplateStore(All, [new StartingItem(1, 1, Arrow, 100)]);
        player.Inventory.Templates = starting;
        player.Inventory.AddStartingItems();
        Assert.Equal(Arrow, player.Inventory.AmmoId);
    }
}
