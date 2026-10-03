using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// Health from stamina and mana from intellect follow the TOTAL stat, items included (vmangos
/// Player::UpdateMaxHealth / UpdateMaxPower, StatSystem.cpp:165-192): the first 20 points give 1 each, every further
/// point 10 health or 15 mana.
/// </summary>
public sealed class StaminaIntellectBonusTests
{
    private const uint StaminaRing = 92001;
    private const uint IntellectRing = 92002;

    // Synthetic rows: race,class,level,basehp,basemana,str,agi,sta,int,spi.
    private const string LevelStats = """
        1,1,1,20,0,23,20,22,20,20
        1,1,2,29,0,24,21,23,20,21
        1,8,1,40,100,20,20,25,30,20
        1,8,2,50,120,20,20,26,31,20
        """;

    private static readonly ItemTemplateStore s_store = new(
    [
        new() { Entry = StaminaRing, Class = 4, SubClass = 0, Name = "Test Stamina Ring", InventoryType = 11, Stats = [new ItemStat((uint)ItemStatType.Stamina, 10)] },
        new() { Entry = IntellectRing, Class = 4, SubClass = 0, Name = "Test Intellect Ring", InventoryType = 11, Stats = [new ItemStat((uint)ItemStatType.Intellect, 10)] },
    ], []);

    private static (Player Player, PlayerProgression Progression, PlayerStatSystem System) Create(bool mana = false, bool attach = true)
    {
        (Player player, _) = ItemTestData.CreatePlayer(level: 1);
        player.Inventory.Templates = s_store;
        if (mana)
        {
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
            player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        }

        var progression = new PlayerProgression(new ProgressionOptions(), PlayerLevelStatsTable.Parse(new StringReader(LevelStats)));
        var system = new PlayerStatSystem();
        progression.BaseValuesApplied += p => p.StatState.Maintainer?.UpdateAll(p);
        progression.InitializeLoadedPlayer(player);
        if (attach)
        {
            system.Attach(player);
        }

        return (player, progression, system);
    }

    private static void Equip(Player player, uint entry)
    {
        Item item = ItemTestData.Give(player.Inventory, entry);
        player.Inventory.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.Contains(player.Inventory.Equipped, e => ReferenceEquals(e.Item, item));
    }

    [Fact]
    public void LoginHealthIsTheBasePlusTheStaminaBonus_WhetherOrNotTheSystemIsAttached()
    {
        (Player attached, _, _) = Create();
        (Player legacy, _, _) = Create(attach: false);

        Assert.Equal(60u, attached.MaxHealth);       // 20 base + (20 + 2 * 10) from 22 stamina
        Assert.Equal(60u, attached.Health);
        Assert.Equal(60u, legacy.MaxHealth);
    }

    [Fact]
    public void AStaminaItemRaisesMaxHealthByTenPerPoint_AndRemovingItLowersItAndClampsHealth()
    {
        (Player player, _, _) = Create();

        Equip(player, StaminaRing);

        Assert.Equal(32u, player.GetUInt32(UpdateFields.UnitFieldStat0 + 2));
        Assert.Equal(160u, player.MaxHealth);        // 20 + (20 + 12 * 10)
        Assert.Equal(60u, player.Health);            // equipping does not heal

        player.Health = 160;
        player.Inventory.DestroyItem(InventorySlots.Bag0, InventorySlots.Finger1);

        Assert.Equal(60u, player.MaxHealth);
        Assert.Equal(60u, player.Health);            // vmangos Unit::SetMaxHealth clamps the current health
    }

    [Fact]
    public void AnIntellectItemRaisesMaxManaByFifteenPerPointForAManaUser()
    {
        (Player player, _, _) = Create(mana: true);
        int maxMana = UpdateFields.UnitFieldMaxpower1;
        Assert.Equal(100u + 20u + (10u * 15u), player.GetUInt32(maxMana));     // base 100 + (20 + 10 * 15) from 30 intellect

        Equip(player, IntellectRing);

        Assert.Equal(100u + 20u + (20u * 15u), player.GetUInt32(maxMana));     // 40 intellect
        player.SetUInt32(UpdateFields.UnitFieldPower1, player.GetUInt32(maxMana));

        player.Inventory.DestroyItem(InventorySlots.Bag0, InventorySlots.Finger1);

        Assert.Equal(270u, player.GetUInt32(maxMana));
        Assert.Equal(270u, player.GetUInt32(UpdateFields.UnitFieldPower1));    // the current mana is clamped too
    }

    [Fact]
    public void AnIntellectItemGivesAClassWithoutManaNothing()
    {
        (Player player, _, _) = Create();

        Equip(player, IntellectRing);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
    }

    [Fact]
    public void ALevelUpWithTheSystemAttachedGivesTheSameHealthAsWithout()
    {
        (Player attached, PlayerProgression withSystem, _) = Create();
        (Player legacy, PlayerProgression without, _) = Create(attach: false);

        withSystem.GiveLevel(attached, 2);
        without.GiveLevel(legacy, 2);

        Assert.Equal(79u, legacy.MaxHealth);         // 29 base + (20 + 3 * 10) from 23 stamina
        Assert.Equal(legacy.MaxHealth, attached.MaxHealth);
        Assert.Equal(attached.MaxHealth, attached.Health);   // a level-up refills after the new maximum is known
    }

    [Fact]
    public void ALevelUpKeepsTheItemBonus()
    {
        (Player player, PlayerProgression progression, _) = Create();
        Equip(player, StaminaRing);

        progression.GiveLevel(player, 2);

        Assert.Equal(179u, player.MaxHealth);        // 29 base + (20 + 13 * 10) from 33 stamina
        Assert.Equal(179u, player.Health);
    }

    [Fact]
    public void UpdatingTwiceChangesNothing()
    {
        (Player player, _, PlayerStatSystem system) = Create();
        Equip(player, StaminaRing);
        uint max = player.MaxHealth;

        system.UpdateAll(player);
        system.UpdateAll(player);

        Assert.Equal(max, player.MaxHealth);
    }
}
