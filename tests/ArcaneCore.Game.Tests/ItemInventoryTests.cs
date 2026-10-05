using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests;

/// <summary>Inventory rules (vmangos Player.cpp storage/equip logic) on a player outside the world.</summary>
public sealed class ItemInventoryTests
{
    [Fact]
    public void StartingOutfit_HumanWarrior_EquipsGearAndPacksTheRest()
    {
        var inventory = Wire(new PlayerInventory(ObjectGuid.Player(5), Race.Human, Class.Warrior, 1));
        inventory.AddStartingItems();

        Assert.Equal(WornShortsword, inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Entry);
        Assert.Equal(WornWoodenShield, inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand)!.Entry);
        Assert.Equal(RecruitsShirt, inventory.GetItem(InventorySlots.Bag0, InventorySlots.Body)!.Entry);
        Assert.Equal(RecruitsPants, inventory.GetItem(InventorySlots.Bag0, InventorySlots.Legs)!.Entry);
        Assert.Equal(RecruitsBoots, inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
        Item jerky = inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!;
        Assert.Equal((ToughJerky, 4u), (jerky.Entry, jerky.Count));
        Item hearthstone = inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart + 1)!;
        Assert.Equal(Hearthstone, hearthstone.Entry);
        Assert.True(hearthstone.IsSoulBound); // bind on pickup
        Assert.Equal(7, inventory.AllItems.Count());
        Assert.All(inventory.AllItems, i => Assert.Equal(ObjectGuid.Player(5), i.OwnerGuid));
        Assert.Equal(7, inventory.CreateSnapshot().Items.Count);
        Assert.NotNull(inventory.TakeSnapshotIfChanged());
    }

    [Fact]
    public void Snapshot_IsNullUntilLoaded()
    {
        (Player player, _) = CreatePlayer();
        Assert.Null(player.Inventory.TakeSnapshotIfChanged());
        player.Inventory.Load([]);
        Assert.Empty(player.Inventory.TakeSnapshotIfChanged()!.Items);
    }

    [Fact]
    public void AddItem_MergesStacks_ThenUsesFreeSlots_BackpackBeforeBags()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item bag = Give(inv, SmallBrownPouch);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        Assert.Same(bag, inv.GetItem(InventorySlots.Bag0, InventorySlots.BagStart));

        Give(inv, ToughJerky, 15);
        Give(inv, ToughJerky, 10);
        Assert.Equal(20u, inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Count);
        Assert.Equal(5u, inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart + 1)!.Count);
        Assert.Equal(25u, inv.GetItemCount(ToughJerky));

        // Fill the backpack: the next item goes into the bag.
        for (int i = 0; i < 14; i++)
        {
            Give(inv, RecruitsShirt);
        }

        Item intoBag = Give(inv, RecruitsShirt);
        Assert.Equal((InventorySlots.BagStart, (byte)0), (intoBag.BagSlot, intoBag.Slot));
        Assert.Same(bag, intoBag.Container);
        Assert.Equal(bag.Guid, intoBag.ContainedIn);
    }

    [Fact]
    public void AddItem_WhenFull_FailsWithoutChanges()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        for (int i = 0; i < 16; i++)
        {
            Give(inv, RecruitsShirt);
        }

        int before = inv.AllItems.Count();
        Assert.Equal(InventoryResult.InventoryFull, inv.AddItem(RecruitsBoots, 1, out Item? item));
        Assert.Null(item);
        Assert.Equal(before, inv.AllItems.Count());

        var dest = new List<ItemPosCount>();
        Assert.Equal(InventoryResult.InventoryFull, inv.CanStoreNewItem(ToughJerky, 3, dest, out uint noSpace));
        Assert.Equal(3u, noSpace);
        Assert.Equal(InventoryResult.ItemNotFound, inv.CanStoreNewItem(123456, 1, dest, out _));
    }

    [Fact]
    public void MaxCount_LimitsCarriedCopies()
    {
        (Player player, _) = CreatePlayer();
        Give(player.Inventory, UniqueKey);
        Assert.Equal(InventoryResult.CantCarryMoreOfThis, player.Inventory.AddItem(UniqueKey, 1, out _));
    }

    [Fact]
    public void Keys_GoToTheKeyring()
    {
        (Player player, _) = CreatePlayer();
        Item key = Give(player.Inventory, UniqueKey);
        Assert.Equal((InventorySlots.Bag0, InventorySlots.KeyringStart), (key.BagSlot, key.Slot));
    }

    [Fact]
    public void SpecialBags_TakeOnlyTheirFamily()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item quiver = Give(inv, LightQuiver);
        inv.AutoEquipItem(quiver.BagSlot, quiver.Slot);
        Item arrows = Give(inv, RoughArrow, 200);
        Assert.Same(quiver, arrows.Container); // special bag free slots come before the backpack
        Item shirt = Give(inv, RecruitsShirt);
        Assert.Null(shirt.Container);
    }

    [Fact]
    public void Equip_UsesSlotRules_AndSetsVisibleAndInventoryFields()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item ring = Give(inv, StrengthRing);
        Assert.False(inv.AutoEquipItem(ring.BagSlot, ring.Slot));
        Assert.Same(ring, inv.GetItem(InventorySlots.Bag0, InventorySlots.Finger1));
        Assert.Equal(ring.Guid.Value, player.GetUInt64(UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.Finger1 * 2)));
        Assert.Equal(StrengthRing, player.GetUInt32(UpdateFields.PlayerVisibleItem10 + (InventorySlots.Finger1 * 12)));
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.ItemStart * 2)));

        // A second ring takes the second finger.
        Item ring2 = Give(inv, StrengthRing);
        inv.AutoEquipItem(ring2.BagSlot, ring2.Slot);
        Assert.Same(ring2, inv.GetItem(InventorySlots.Bag0, InventorySlots.Finger2));

        // Unequip into the backpack clears the visible fields.
        inv.SwapItem(InventorySlots.Bag0, InventorySlots.Finger1, InventorySlots.Bag0, InventorySlots.ItemStart + 5);
        Assert.Same(ring, inv.GetItem(InventorySlots.Bag0, (byte)(InventorySlots.ItemStart + 5)));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerVisibleItem10 + (InventorySlots.Finger1 * 12)));
        Assert.Empty(EquipErrors(session));
    }

    [Fact]
    public void StatsHook_AppliesWhileWorn_AndRemovesSymmetrically()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        uint str = player.GetUInt32(UpdateFields.UnitFieldStat0);
        uint sta = player.GetUInt32(UpdateFields.UnitFieldStat0 + 2);
        uint armor = player.GetUInt32(UpdateFields.UnitFieldResistances);

        Item ring = Give(inv, StrengthRing);
        Assert.Equal(str, player.GetUInt32(UpdateFields.UnitFieldStat0)); // carried, not worn
        inv.AutoEquipItem(ring.BagSlot, ring.Slot);
        Assert.Equal(str + 5, player.GetUInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(5f, player.GetFloat(UpdateFields.PlayerFieldPosstat0));
        Assert.Equal(sta - 2, player.GetUInt32(UpdateFields.UnitFieldStat0 + 2));
        Assert.Equal(-2f, player.GetFloat(UpdateFields.PlayerFieldNegstat0 + 2));
        Assert.Equal(armor + 10, player.GetUInt32(UpdateFields.UnitFieldResistances));

        inv.SwapItem(InventorySlots.Bag0, InventorySlots.Finger1, InventorySlots.Bag0, InventorySlots.ItemStart);
        Assert.Equal((str, sta, armor), (player.GetUInt32(UpdateFields.UnitFieldStat0), player.GetUInt32(UpdateFields.UnitFieldStat0 + 2), player.GetUInt32(UpdateFields.UnitFieldResistances)));
        Assert.Equal(0f, player.GetFloat(UpdateFields.PlayerFieldPosstat0));
    }

    [Fact]
    public void StatsHook_IsReplaceable()
    {
        (Player player, _) = CreatePlayer();
        var calls = new List<(uint Entry, byte Slot, bool Apply)>();
        player.Inventory.StatsApplier = new RecordingApplier(calls);
        Item ring = Give(player.Inventory, StrengthRing);
        player.Inventory.AutoEquipItem(ring.BagSlot, ring.Slot);
        player.Inventory.DestroyItem(InventorySlots.Bag0, InventorySlots.Finger1);
        Assert.Equal([(StrengthRing, InventorySlots.Finger1, true), (StrengthRing, InventorySlots.Finger1, false)], calls);
    }

    [Fact]
    public void Equip_Errors_FollowVmangosOrder()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;

        Item helm = Give(inv, LevelTenHelm);
        inv.AutoEquipItem(helm.BagSlot, helm.Slot);
        Item robe = Give(inv, MageRobe);
        inv.AutoEquipItem(robe.BagSlot, robe.Slot);
        Item jerky = Give(inv, ToughJerky);
        inv.AutoEquipItem(jerky.BagSlot, jerky.Slot);

        Assert.Equal([InventoryResult.CantEquipLevelI, InventoryResult.YouCanNeverUseThatItem, InventoryResult.ItemCantBeEquipped], EquipErrors(session));
        byte[] levelError = session.Sent.First(p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure).Payload;
        Assert.Equal(22, levelError.Length);
        Assert.Equal(10u, BitConverter.ToUInt32(levelError, 1)); // required level before the GUIDs
        Assert.Equal(helm.Guid.Value, BitConverter.ToUInt64(levelError, 5));
    }

    [Fact]
    public void Equip_StateChecks_StunnedCombatDisarmed()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item sword = Give(inv, WornShortsword);

        player.UnitFlags |= UnitFlags.Stunned;
        inv.AutoEquipItem(sword.BagSlot, sword.Slot);
        player.UnitFlags &= ~UnitFlags.Stunned;

        // Weapons may change in combat; armor may not.
        player.UnitFlags |= UnitFlags.InCombat;
        Item boots = Give(inv, RecruitsBoots);
        inv.AutoEquipItem(boots.BagSlot, boots.Slot);
        inv.AutoEquipItem(sword.BagSlot, sword.Slot);
        Assert.Same(sword, inv.GetItem(InventorySlots.Bag0, InventorySlots.MainHand));
        player.UnitFlags &= ~UnitFlags.InCombat;

        player.UnitFlags |= UnitFlags.Disarmed;
        inv.SwapItem(InventorySlots.Bag0, InventorySlots.MainHand, InventorySlots.Bag0, InventorySlots.ItemStart + 10);
        player.UnitFlags &= ~UnitFlags.Disarmed;

        Assert.Equal([InventoryResult.YouAreStunned, InventoryResult.NotInCombat, InventoryResult.NotWhileDisarmed], EquipErrors(session));
    }

    [Fact]
    public void Offhand_Rules_DualWieldAndTwoHanders()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item sword = Give(inv, WornShortsword);
        inv.AutoEquipItem(sword.BagSlot, sword.Slot);
        Item axe = Give(inv, TwoHandAxe);

        // A two-hander cannot go to the off hand.
        inv.SwapItem(axe.BagSlot, axe.Slot, InventorySlots.Bag0, InventorySlots.OffHand);
        Assert.Equal([InventoryResult.ItemCantBeEquipped], EquipErrors(session));
        session.Clear();

        // Shield in the off hand; then a two-hander pushes it to the bags.
        Item shield = Give(inv, WornWoodenShield);
        inv.AutoEquipItem(shield.BagSlot, shield.Slot);
        Assert.Same(shield, inv.GetItem(InventorySlots.Bag0, InventorySlots.OffHand));
        inv.AutoEquipItem(axe.BagSlot, axe.Slot);
        Assert.Same(axe, inv.GetItem(InventorySlots.Bag0, InventorySlots.MainHand));
        Assert.Null(inv.GetItem(InventorySlots.Bag0, InventorySlots.OffHand));
        Assert.Null(shield.Container);
        Assert.InRange(shield.Slot, InventorySlots.ItemStart, InventorySlots.ItemEnd - 1);
        Assert.InRange(sword.Slot, InventorySlots.ItemStart, InventorySlots.ItemEnd - 1);

        // With a two-hander worn the off hand is blocked.
        inv.SwapItem(shield.BagSlot, shield.Slot, InventorySlots.Bag0, InventorySlots.OffHand);
        Assert.Equal([InventoryResult.CantEquipWithTwohanded], EquipErrors(session));
        Assert.DoesNotContain(session.Sent, p => p.Opcode != WorldOpcode.SmsgInventoryChangeFailure);
    }

    [Fact]
    public void Offhand_OneHandWeaponNeedsDualWield()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item sword = Give(inv, WornShortsword);
        inv.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.OffHand);
        Assert.Equal([InventoryResult.ItemCantBeEquipped], EquipErrors(session)); // main-hand only weapon
    }

    [Fact]
    public void TwoHander_RefusedWhenTheOffhandCannotBeStored()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item shield = Give(inv, WornWoodenShield);
        inv.AutoEquipItem(shield.BagSlot, shield.Slot);
        Item axe = Give(inv, TwoHandAxe);
        for (int i = 0; i < 15; i++)
        {
            Give(inv, RecruitsShirt);
        }

        inv.AutoEquipItem(axe.BagSlot, axe.Slot);
        Assert.Equal([InventoryResult.ItemsCantBeSwapped], EquipErrors(session));
        Assert.Same(shield, inv.GetItem(InventorySlots.Bag0, InventorySlots.OffHand));
    }

    [Fact]
    public void QuiverAndAmmoPouch_OnlyOne()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item quiver = Give(inv, LightQuiver);
        inv.AutoEquipItem(quiver.BagSlot, quiver.Slot);
        Item pouch = Give(inv, SmallAmmoPouch);
        inv.AutoEquipItem(pouch.BagSlot, pouch.Slot);
        Assert.Equal([InventoryResult.CanEquipOnly1Quiver], EquipErrors(session));
    }

    [Fact]
    public void UniqueEquipped_OnlyOneWorn()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item a = Give(inv, UniqueTrinket);
        Item b = Give(inv, UniqueTrinket);
        inv.AutoEquipItem(a.BagSlot, a.Slot);
        inv.AutoEquipItem(b.BagSlot, b.Slot);
        Assert.Equal([InventoryResult.ItemCantBeEquipped], EquipErrors(session));
    }

    [Fact]
    public void Swap_MergesFillsAndExchanges()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item a = Give(inv, ToughJerky, 15);                               // slot 23
        inv.SplitItem(InventorySlots.Bag0, InventorySlots.ItemStart, InventorySlots.Bag0, InventorySlots.ItemStart + 3, 10);
        Item b = inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart + 3)!;
        Assert.Equal((5u, 10u), (a.Count, b.Count));
        Assert.NotEqual(a.Guid, b.Guid);

        // Fill: 5 + 10 → 15 in one stack, the source goes away.
        inv.SwapItem(InventorySlots.Bag0, InventorySlots.ItemStart, InventorySlots.Bag0, InventorySlots.ItemStart + 3);
        Assert.Null(inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart));
        Assert.Equal(15u, b.Count);
        Assert.Null(a.Inventory);

        // Partial fill: 15 + 10 → 20 and 5.
        inv.SplitItem(b.BagSlot, b.Slot, InventorySlots.Bag0, InventorySlots.ItemStart + 7, 5);
        Item c = inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart + 7)!;
        c.Count = 15;
        inv.SwapItem(c.BagSlot, c.Slot, b.BagSlot, b.Slot);
        Assert.Equal((5u, 20u), (c.Count, b.Count));

        // Different items exchange places.
        Item shirt = Give(inv, RecruitsShirt);
        byte shirtSlot = shirt.Slot;
        byte jerkySlot = b.Slot;
        inv.SwapItem(InventorySlots.Bag0, shirtSlot, InventorySlots.Bag0, jerkySlot);
        Assert.Equal((jerkySlot, shirtSlot), (shirt.Slot, b.Slot));
        Assert.Equal(25u, inv.GetItemCount(ToughJerky));
    }

    [Fact]
    public void Split_Errors()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Give(inv, ToughJerky, 5);
        inv.SplitItem(InventorySlots.Bag0, InventorySlots.ItemStart, InventorySlots.Bag0, InventorySlots.ItemStart + 1, 5);
        inv.SplitItem(InventorySlots.Bag0, InventorySlots.ItemStart, InventorySlots.Bag0, InventorySlots.ItemStart + 1, 6);
        inv.SplitItem(InventorySlots.Bag0, InventorySlots.ItemStart + 2, InventorySlots.Bag0, InventorySlots.ItemStart + 1, 1);
        Item shirt = Give(inv, RecruitsShirt);
        inv.SplitItem(InventorySlots.Bag0, InventorySlots.ItemStart, InventorySlots.Bag0, shirt.Slot, 2); // onto another item
        Assert.Equal([InventoryResult.CouldntSplitItems, InventoryResult.TriedToSplitMoreThanCount, InventoryResult.ItemNotFound, InventoryResult.ItemCantStack], EquipErrors(session));
        Assert.Equal(5u, inv.GetItemCount(ToughJerky));
    }

    [Fact]
    public void Destroy_WholeStackPartialAndIndestructible()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        var removed = new List<(uint, int)>();
        inv.ItemCountChanged += (entry, delta) => removed.Add((entry, delta));
        Item jerky = Give(inv, ToughJerky, 10);
        inv.DestroyItemRequest(jerky.BagSlot, jerky.Slot, 3);
        Assert.Equal(7u, jerky.Count);
        inv.DestroyItemRequest(jerky.BagSlot, jerky.Slot, 0);
        Assert.Null(inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart));

        Item rock = Give(inv, IndestructibleRock);
        inv.DestroyItemRequest(rock.BagSlot, rock.Slot, 0);
        Assert.Same(rock, inv.GetItem(rock.BagSlot, rock.Slot));
        inv.DestroyItemRequest(InventorySlots.Bag0, InventorySlots.ItemEnd - 1, 0);
        Assert.Equal([InventoryResult.CantDropSoulbound, InventoryResult.ItemNotFound], EquipErrors(session));
        Assert.Equal([(ToughJerky, 10), (ToughJerky, -3), (ToughJerky, -7), (IndestructibleRock, 1)], removed);
    }

    [Fact]
    public void DestroyItemCount_ByEntry_AcrossStacks()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Give(inv, ToughJerky, 20);
        Give(inv, ToughJerky, 20);
        Give(inv, ToughJerky, 5);
        Assert.Equal(30u, inv.DestroyItemCount(ToughJerky, 30));
        Assert.Equal(15u, inv.GetItemCount(ToughJerky));
        Assert.Equal(15u, inv.DestroyItemCount(ToughJerky, 100));
        Assert.Equal(0u, inv.GetItemCount(ToughJerky));
    }

    [Fact]
    public void Bags_NonEmptyCannotComeOff_AndEmptyBagTakesTheContentsOnSwap()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        var bag = (Container)Give(inv, SmallBrownPouch);
        inv.AutoEquipItem(bag.BagSlot, bag.Slot);
        for (int i = 0; i < 16; i++)
        {
            Give(inv, RecruitsShirt);
        }

        Item inBag = Give(inv, RecruitsBoots);
        Assert.Same(bag, inBag.Container);

        // Make room, then try to unequip the non-empty bag.
        inv.DestroyItem(InventorySlots.Bag0, InventorySlots.ItemStart);
        inv.SwapItem(InventorySlots.Bag0, InventorySlots.BagStart, InventorySlots.Bag0, InventorySlots.ItemStart);
        Assert.Equal([InventoryResult.CanOnlyDoWithEmptyBags], EquipErrors(session));
        Assert.Same(bag, inv.GetItem(InventorySlots.Bag0, InventorySlots.BagStart));

        // A bigger empty bag from the backpack takes the worn bag's place and its contents.
        var bigger = (Container)Give(inv, SmallBrownPouch);
        Assert.Equal(InventorySlots.ItemStart, bigger.Slot);
        inv.SwapItem(InventorySlots.Bag0, bigger.Slot, InventorySlots.Bag0, InventorySlots.BagStart);
        Assert.Same(bigger, inv.GetItem(InventorySlots.Bag0, InventorySlots.BagStart));
        Assert.Same(bag, inv.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart));
        Assert.True(bag.IsEmpty);
        Assert.Same(bigger, inBag.Container);
        Assert.Equal(bigger.Guid, inBag.ContainedIn);
    }

    [Fact]
    public void Bags_CannotGoIntoThemselves_AndDestroyTakesContents()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        var bag = (Container)Give(inv, SmallBrownPouch);
        inv.AutoEquipItem(bag.BagSlot, bag.Slot);
        inv.SwapItem(InventorySlots.Bag0, InventorySlots.BagStart, InventorySlots.BagStart, 0);
        Assert.Equal([InventoryResult.NonemptyBagOverOtherBag], EquipErrors(session));

        Item boots = Give(inv, RecruitsBoots);
        inv.SwapItem(boots.BagSlot, boots.Slot, InventorySlots.BagStart, 2);
        Assert.Same(bag, boots.Container);
        Assert.Equal((byte)2, boots.Slot);
        inv.DestroyItem(InventorySlots.Bag0, InventorySlots.BagStart);
        Assert.Empty(inv.AllItems);
    }

    [Fact]
    public void AutoStoreBag_MovesIntoTheBag_AndSoulBagRefusesOthers()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        var bag = (Container)Give(inv, SmallBrownPouch);
        inv.AutoEquipItem(bag.BagSlot, bag.Slot);
        var soul = (Container)Give(inv, SoulPouch);
        inv.AutoEquipItem(soul.BagSlot, soul.Slot);
        Item shirt = Give(inv, RecruitsShirt);
        inv.AutoStoreBagItem(shirt.BagSlot, shirt.Slot, bag.Slot);
        Assert.Same(bag, shirt.Container);
        inv.AutoStoreBagItem(shirt.BagSlot, shirt.Slot, soul.Slot);
        Assert.Equal([InventoryResult.ItemDoesntGoIntoBag2], EquipErrors(session));
        Assert.Same(bag, shirt.Container);
    }

    [Fact]
    public void Bank_NeedsABanker()
    {
        (Player player, FakeSession session) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Item shirt = Give(inv, RecruitsShirt);
        var dest = new List<ItemPosCount>();
        Assert.Equal(InventoryResult.Ok, inv.CanBankItem(InventorySlots.Bag0, InventorySlots.BankItemStart, dest, shirt, swap: false, out _));
        Assert.False(inv.BankUsable);
        inv.CanUseBank = () => true;
        inv.SwapItem(shirt.BagSlot, shirt.Slot, InventorySlots.Bag0, InventorySlots.BankItemStart);
        Assert.Same(shirt, inv.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));
        Assert.Equal(0u, inv.GetItemCount(RecruitsShirt));
        Assert.Equal(1u, inv.GetItemCount(RecruitsShirt, inBankAlso: true));

        // Bank bag slots must be bought first.
        Item pouch = Give(inv, SmallBrownPouch);
        inv.SwapItem(pouch.BagSlot, pouch.Slot, InventorySlots.Bag0, InventorySlots.BankBagStart);
        Assert.Equal([InventoryResult.MustPurchaseThatBagSlot], EquipErrors(session));
        inv.BankBagSlotCount = 1;
        inv.SwapItem(pouch.BagSlot, pouch.Slot, InventorySlots.Bag0, InventorySlots.BankBagStart);
        Assert.Same(pouch, inv.GetItem(InventorySlots.Bag0, InventorySlots.BankBagStart));
    }

    [Fact]
    public void SnapshotRoundTrip_RestoresPositionsBagsAndState()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        inv.Load([]);
        var bag = (Container)Give(inv, SmallBrownPouch);
        inv.AutoEquipItem(bag.BagSlot, bag.Slot);
        Item ring = Give(inv, StrengthRing);
        inv.AutoEquipItem(ring.BagSlot, ring.Slot);
        Item jerky = Give(inv, ToughJerky, 12);
        inv.SwapItem(jerky.BagSlot, jerky.Slot, bag.Slot, 4);
        Item stone = Give(inv, Hearthstone);
        InventorySnapshot snapshot = inv.TakeSnapshotIfChanged()!;

        (Player again, _) = CreatePlayer();
        again.Inventory.Load(snapshot.Items);
        PlayerInventory restored = again.Inventory;
        Item ring2 = restored.GetItem(InventorySlots.Bag0, InventorySlots.Finger1)!;
        Assert.Equal((ring.Guid, StrengthRing), (ring2.Guid, ring2.Entry));
        Item jerky2 = restored.GetItem(InventorySlots.BagStart, 4)!;
        Assert.Equal((jerky.Guid, 12u), (jerky2.Guid, jerky2.Count));
        Assert.True(restored.GetItemByGuid(stone.Guid)!.IsSoulBound);
        Assert.Equal(ring.Guid.Value, again.GetUInt64(UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.Finger1 * 2)));
        Assert.Equal(StrengthRing, again.GetUInt32(UpdateFields.PlayerVisibleItem10 + (InventorySlots.Finger1 * 12)));
        Assert.Equal(player.GetUInt32(UpdateFields.UnitFieldStat0), again.GetUInt32(UpdateFields.UnitFieldStat0)); // mods re-applied
        Assert.Equal(Rows(snapshot), Rows(restored.CreateSnapshot()));

        static IEnumerable<(uint, byte, uint, uint, uint, uint, uint)> Rows(InventorySnapshot s) => s.Items
            .Select(r => (r.ContainerGuid, r.Slot, r.Item.Guid, r.Item.Entry, r.Item.Count, r.Item.Flags, r.Item.Durability))
            .OrderBy(r => r.Item3);
    }

    [Fact]
    public void Load_KeepsRowsItCannotPlace()
    {
        (Player player, _) = CreatePlayer();
        var shirt = new ItemInstanceData { Guid = 10, Entry = RecruitsShirt, Count = 1 };
        var unknown = new ItemInstanceData { Guid = 11, Entry = 424242, Count = 1 };
        var wrongSlot = new ItemInstanceData { Guid = 12, Entry = RecruitsBoots, Count = 1 };
        var orphan = new ItemInstanceData { Guid = 13, Entry = RecruitsPants, Count = 1 };
        InventoryItemData[] rows =
        [
            new(0, InventorySlots.Body, shirt),
            new(0, InventorySlots.ItemStart, unknown),
            new(0, InventorySlots.Head, wrongSlot),
            new(999, 0, orphan),
        ];

        player.Inventory.Load(rows);
        Assert.Single(player.Inventory.AllItems);
        Assert.Equal(4, player.Inventory.CreateSnapshot().Items.Count);
        Assert.Contains(player.Inventory.CreateSnapshot().Items, r => r.Item.Guid == 13 && r.ContainerGuid == 999);
    }

    [Fact]
    public void Validation_IsValidPosition()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        Assert.True(inv.IsValidPosition(InventorySlots.Bag0, InventorySlots.ItemStart, explicitPos: true));
        Assert.False(inv.IsValidPosition(InventorySlots.BagStart, 0, explicitPos: true)); // no bag there
        var bag = (Container)Give(inv, SmallBrownPouch);
        inv.AutoEquipItem(bag.BagSlot, bag.Slot);
        Assert.True(inv.IsValidPosition(InventorySlots.BagStart, 5, explicitPos: true));
        Assert.False(inv.IsValidPosition(InventorySlots.BagStart, 6, explicitPos: true)); // a 6-slot bag
    }

    private sealed class RecordingApplier(List<(uint Entry, byte Slot, bool Apply)> calls) : IItemStatsApplier
    {
        public void Apply(Player player, Item item, byte slot, bool apply) => calls.Add((item.Entry, slot, apply));
    }
}
