using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>PLAYER_AMMO_ID lifecycle: vmangos Player::CanUseAmmo / SetAmmo / RemoveAmmo and the ammo DPS.</summary>
public sealed class PlayerAmmoTests
{
    private const uint Bow = 91001;
    private const uint Gun = 91002;
    private const uint Wand = 91003;
    private const uint Arrow = 91010;
    private const uint Bullet = 91011;
    private const uint FastTestAmmo = 91012;
    private const uint HighLevelArrow = 91013;
    private const uint HunterOnlyBullet = 91014;

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = Bow, Class = 2, SubClass = 2, Name = "Test Bow", DisplayId = 200, InventoryType = 15, Delay = 2500, MaxDurability = 40, Damages = [new ItemDamage(10, 20, 0)] },
        new() { Entry = Gun, Class = 2, SubClass = 3, Name = "Test Gun", DisplayId = 201, InventoryType = 26, Delay = 2800, MaxDurability = 40, Damages = [new ItemDamage(10, 20, 0)] },
        new() { Entry = Wand, Class = 2, SubClass = 19, Name = "Test Wand", DisplayId = 202, InventoryType = 26, Delay = 1500, MaxDurability = 40 },
        new() { Entry = Arrow, Class = 6, SubClass = 2, Name = "Test Arrow", DisplayId = 5996, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(4, 5, 0)] },
        new() { Entry = Bullet, Class = 6, SubClass = 3, Name = "Test Bullet", DisplayId = 5998, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(10, 11, 0)] },
        new() { Entry = FastTestAmmo, Class = 6, SubClass = 2, Name = "Not ammo slot", DisplayId = 5999, InventoryType = 21, Stackable = 200 },
        new() { Entry = HighLevelArrow, Class = 6, SubClass = 2, Name = "High arrow", DisplayId = 5997, InventoryType = 24, Stackable = 200, RequiredLevel = 50, Damages = [new ItemDamage(20, 21, 0)] },
        new() { Entry = HunterOnlyBullet, Class = 6, SubClass = 3, Name = "Hunter bullet", DisplayId = 5995, InventoryType = 24, Stackable = 200, AllowableClass = 1 << 8 },
    ];

    private static (Player Player, FakeSession Session) NewPlayer()
    {
        (Player player, FakeSession session) = ItemTestData.CreatePlayer(level: 10);
        player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
        return (player, session);
    }

    private static void Give(Player player, uint entry, uint count = 20)
        => Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, count, out _));

    private static void Equip(Player player, uint entry)
    {
        Give(player, entry, 1);
        Item item = player.Inventory.AllItems.First(i => i.Entry == entry);
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item.Template, item, swap: false));
        Assert.Equal(InventorySlots.Ranged, dest);
        player.Inventory.RemoveItem(InventorySlots.Bag0, item.Slot);
        player.Inventory.EquipItem(dest, item);
    }

    private static InventoryResult LastEquipError(FakeSession session)
        => (InventoryResult)session.Sent.Last(p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure).Payload[0];

    [Fact]
    public void SetAmmo_WritesTheFieldAndTheDpsFollowsTheEquippedLauncher()
    {
        (Player player, _) = NewPlayer();
        Give(player, Arrow);
        Equip(player, Bow);

        PlayerAmmo.SetAmmo(player, Arrow);

        Assert.Equal(Arrow, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.Equal(4.5f, PlayerAmmo.CurrentDps(player));
    }

    [Fact]
    public void AmmoDps_IsZeroWhenTheLauncherTakesOtherAmmo_AndComesBackWithTheRightWeapon()
    {
        (Player player, _) = NewPlayer();
        Give(player, Arrow);
        Equip(player, Gun);
        PlayerAmmo.SetAmmo(player, Arrow);
        Assert.Equal(Arrow, player.GetUInt32(UpdateFields.PlayerAmmoId)); // the slot accepts it (CanUseAmmo has no weapon rule)
        Assert.Equal(0f, PlayerAmmo.CurrentDps(player));

        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Assert.Equal(0f, PlayerAmmo.CurrentDps(player)); // no weapon at all

        Equip(player, Bow);
        Assert.Equal(4.5f, PlayerAmmo.CurrentDps(player));
    }

    [Fact]
    public void AmmoDps_IgnoresABrokenWeapon()
    {
        (Player player, _) = NewPlayer();
        Give(player, Arrow);
        Equip(player, Bow);
        PlayerAmmo.SetAmmo(player, Arrow);
        player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!.Durability = 0;

        Assert.Equal(0f, PlayerAmmo.CurrentDps(player)); // GetWeaponForAttack(RANGED_ATTACK, nonbroken = true)
        Assert.Null(PlayerAmmo.RangedWeapon(player, nonBroken: true));
        Assert.NotNull(PlayerAmmo.RangedWeapon(player, nonBroken: false));
    }

    [Fact]
    public void SetAmmo_RefusesNonAmmo_AndLeavesTheFieldAlone()
    {
        (Player player, FakeSession session) = NewPlayer();
        Give(player, FastTestAmmo);

        PlayerAmmo.SetAmmo(player, FastTestAmmo);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.Equal(InventoryResult.OnlyAmmoCanGoHere, LastEquipError(session));
    }

    [Fact]
    public void SetAmmo_AppliesTheItemRequirements()
    {
        (Player player, FakeSession session) = NewPlayer();
        Give(player, HighLevelArrow);
        PlayerAmmo.SetAmmo(player, HighLevelArrow);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.Equal(InventoryResult.CantEquipLevelI, LastEquipError(session));

        Give(player, HunterOnlyBullet);
        PlayerAmmo.SetAmmo(player, HunterOnlyBullet);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.Equal(InventoryResult.YouCanNeverUseThatItem, LastEquipError(session));
    }

    [Fact]
    public void SetAmmo_RefusesADeadPlayer_UnknownItems_AndIgnoresZeroAndRepeats()
    {
        (Player player, FakeSession session) = NewPlayer();
        Give(player, Arrow);
        PlayerAmmo.SetAmmo(player, 0);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure);

        PlayerAmmo.SetAmmo(player, 99999);
        Assert.Equal(InventoryResult.ItemNotFound, LastEquipError(session));

        PlayerAmmo.SetAmmo(player, Arrow);
        session.Clear();
        PlayerAmmo.SetAmmo(player, Arrow); // already set: nothing happens
        Assert.Empty(session.Sent);

        player.Health = 0;
        Assert.Equal(InventoryResult.YouAreDead, PlayerAmmo.CanUseAmmo(player, Bullet));
    }

    [Fact]
    public void RemoveAmmo_ZeroesTheFieldAndTheDps()
    {
        (Player player, _) = NewPlayer();
        Give(player, Arrow);
        Equip(player, Bow);
        PlayerAmmo.SetAmmo(player, Arrow);

        PlayerAmmo.RemoveAmmo(player);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.Equal(0f, PlayerAmmo.CurrentDps(player));
    }

    [Fact]
    public void TheFieldSurvivesTheLastArrowBeingSpent()
    {
        // vmangos has no code that clears PLAYER_AMMO_ID when the stack runs out.
        (Player player, _) = NewPlayer();
        Give(player, Arrow, 1);
        Equip(player, Bow);
        PlayerAmmo.SetAmmo(player, Arrow);

        player.Inventory.DestroyItemCount(Arrow, 1);

        Assert.Equal(Arrow, player.GetUInt32(UpdateFields.PlayerAmmoId));
        Assert.Equal(0u, player.Inventory.GetItemCount(Arrow));
        Assert.Equal(4.5f, PlayerAmmo.CurrentDps(player));
    }
}
