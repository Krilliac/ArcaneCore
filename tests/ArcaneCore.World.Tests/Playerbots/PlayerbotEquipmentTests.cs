using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotEquipmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuperiorCarriedWarriorBootsUseOrdinaryAutoEquipAndSettle(bool brokenStarter)
    {
        var items = new ItemTestContent();
        // The loaded ClassicDB Recruit's Boots have zero armor. A worn plain item
        // need not contribute armor to be safely replaced by a positive upgrade.
        items.Templates.Templates.Add(Template(40, InventoryType.Feet, 0) with { MaxDurability = 10 });
        items.Templates.Templates.Add(Template(2967, InventoryType.Feet, 70, requiredLevel: 5));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 5;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(40, 1, out _));
                Item starter = player.Inventory.AllItems.Single(item => item.Entry == 40);
                _ = player.Inventory.AutoEquipItem(starter.BagSlot, starter.Slot);
                if (brokenStarter) starter.Durability = 0;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2967, 1, out _));
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(2967u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                Assert.Equal(0, session.ManagedBudget.Remaining);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task EquipmentRefusesDowngradeUnusableBrokenAndCombatOrZeroBudget()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Template(40, InventoryType.Feet, 70));
        items.Templates.Templates.Add(Template(2967, InventoryType.Feet, 20, requiredLevel: 5));
        items.Templates.Templates.Add(Template(2968, InventoryType.Feet, 80, requiredLevel: 60));
        items.Templates.Templates.Add(Template(2970, InventoryType.Feet, 100) with { MaxDurability = 10 });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 5;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(40, 1, out _));
                Item starter = player.Inventory.AllItems.Single(item => item.Entry == 40);
                _ = player.Inventory.AutoEquipItem(starter.BagSlot, starter.Slot);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2967, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2968, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2970, 1, out _));
                player.Inventory.AllItems.Single(item => item.Entry == 2970).Durability = 0;
                var helper = new PlayerbotEquipment(session);
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.False(helper.Update(player));
                session.ManagedBudget = new ManagedActionBudget(1);
                player.Map!.Combat.SetInCombatState(player, 10_000);
                Assert.False(helper.Update(player));
                player.Map!.Combat.CombatStop(player);
                // The worn 70 armor beats the 20; the level-60 boots are unusable; the 100-armor pair is broken.
                Assert.False(helper.Update(player));
                Assert.Equal(1, session.ManagedBudget.Remaining);
                Assert.Equal(40u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Items with stats are no longer skipped: the build's weights rank them, so stats can outweigh raw armor.</summary>
    [Fact]
    public async Task ItemsWithStatsAreScored_AndTheBestUpgradeWins()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Template(40, InventoryType.Feet, 70));
        items.Templates.Templates.Add(Template(2969, InventoryType.Feet, 90) with { Stats = [new ItemStat((uint)ItemStatType.Stamina, 1)] });
        items.Templates.Templates.Add(Template(2972, InventoryType.Feet, 75) with { Stats = [new ItemStat((uint)ItemStatType.Strength, 8)] });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 5;
                EquipFromBags(player, 40);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2969, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2972, 1, out _));
                var helper = new PlayerbotEquipment(session);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(helper.Update(player));
                Assert.Equal(2972u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(helper.Update(player)); // 2969 and the old pair are no upgrade over eight strength
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Rings fill both fingers, the second through the server's own slot choice; a better ring then replaces the worse finger
    /// through CMSG_AUTOEQUIP_ITEM_SLOT (the server would otherwise pick the first finger).
    /// </summary>
    [Fact]
    public async Task RingsFillBothFingersThenReplaceTheWorseOne()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Ring(3001, strength: 3));
        items.Templates.Templates.Add(Ring(3002, strength: 5));
        items.Templates.Templates.Add(Ring(3003, strength: 4));
        items.Templates.Templates.Add(Ring(3004, strength: 6));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                foreach (uint entry in new uint[] { 3001, 3002, 3003 })
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out _));
                var helper = new PlayerbotEquipment(session);
                for (int i = 0; i < 2; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(helper.Update(player));
                }

                Assert.Equal(3002u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Finger1)!.Entry);
                Assert.Equal(3003u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Finger2)!.Entry);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(helper.Update(player)); // the +3 ring is worse than both

                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(3004, 1, out _));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(helper.Update(player));
                Assert.Equal(3002u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Finger1)!.Entry);
                Assert.Equal(3004u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Finger2)!.Entry);
                Assert.Equal(1u, player.Inventory.GetItemCount(3003));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AFasterWeaponReplacesTheWornOne_AndABagGoesIntoAnEmptyBagSlot()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Sword(4001, min: 2, max: 4, delay: 2000));
        items.Templates.Templates.Add(Sword(4002, min: 8, max: 12, delay: 2000));
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 4100, Name = "Small bag", Class = (uint)ItemClass.Container, SubClass = ItemSubClasses.Container,
            InventoryType = (uint)InventoryType.Bag, ContainerSlots = 6, Stackable = 1,
        });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                EquipFromBags(player, 4001);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(4002, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(4100, 1, out _));
                var helper = new PlayerbotEquipment(session);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(helper.Update(player));
                Assert.Equal(4100u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.BagStart)!.Entry);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(helper.Update(player));
                Assert.Equal(4002u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Entry);
                Assert.Equal(1u, player.Inventory.GetItemCount(4001));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(helper.Update(player));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A refused CMSG_AUTOEQUIP_ITEM is no fault: the item is set aside and the bot does not ask again at once.</summary>
    [Fact]
    public async Task ARefusedEquip_IsNotRetriedOnTheNextThink()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Template(2971, InventoryType.Feet, 100));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2971, 1, out _));
                var helper = new PlayerbotEquipment(session);
                // A stun refuses every equipment change (patch 1.6.0, CanEquipItem) after the bot's own pre-check.
                player.UnitFlags |= UnitFlags.Stunned;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(helper.Update(player)); // the pre-check already sees the refusal: nothing is sent
                Assert.Equal(1, session.ManagedBudget.Remaining);
                player.UnitFlags &= ~UnitFlags.Stunned;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(helper.Update(player));
                Assert.Equal(2971u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroBudgetOrCombatPreservesAnOtherwiseValidUpgrade(bool combat)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Template(40, InventoryType.Feet, 1));
        items.Templates.Templates.Add(Template(2971, InventoryType.Feet, 100, requiredLevel: 5));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 5;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(40, 1, out _));
                Item starter = player.Inventory.AllItems.Single(item => item.Entry == 40);
                _ = player.Inventory.AutoEquipItem(starter.BagSlot, starter.Slot);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2971, 1, out _));
                var helper = new PlayerbotEquipment(session);
                session.ManagedBudget = new ManagedActionBudget(combat ? 1 : 0);
                if (combat) player.Map!.Combat.SetInCombatState(player, 10_000);
                int before = session.ManagedBudget.Remaining;
                Assert.False(helper.Update(player));
                Assert.Equal(before, session.ManagedBudget.Remaining);
                Assert.Equal(40u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ActiveCastRejectsOtherwiseValidArmorUpgrade()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Template(40, InventoryType.Feet, 1));
        items.Templates.Templates.Add(Template(2967, InventoryType.Feet, 70, requiredLevel: 5));
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 5;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(40, 1, out _));
                Item starter = player.Inventory.AllItems.Single(item => item.Entry == 40);
                _ = player.Inventory.AutoEquipItem(starter.BagSlot, starter.Slot);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2967, 1, out _));
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([new SpellInfo
                {
                    Id = 994001, CastTime = new SpellCastTime(5_000, 0, 0),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.Heal, BasePoints = 1,
                        TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                }], [], []);
                Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, 994001,
                    SpellCastTargets.ForSelf(), triggered: false));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(new PlayerbotEquipment(session).Update(player));
                Assert.Equal(40u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    internal static void EquipFromBags(Player player, uint entry)
    {
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out _));
        Item item = player.Inventory.AllItems.Single(candidate => candidate.Entry == entry);
        _ = player.Inventory.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.True(InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot), $"item {entry} could not be equipped for the test");
    }

    internal static ItemTemplate Template(uint entry, InventoryType type, int armor, uint requiredLevel = 0)
        => new()
        {
            Entry = entry, Name = $"armor-{entry}", Class = (uint)ItemClass.Armor,
            SubClass = ItemSubClasses.ArmorMisc, InventoryType = (uint)type, Armor = armor,
            RequiredLevel = requiredLevel, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
            Stackable = 1,
        };

    internal static ItemTemplate Ring(uint entry, int strength)
        => Template(entry, InventoryType.Finger, 0) with { Stats = [new ItemStat((uint)ItemStatType.Strength, strength)] };

    /// <summary>A one-handed sword (sub-class 7, the human warrior's starting weapon skill).</summary>
    internal static ItemTemplate Sword(uint entry, float min, float max, uint delay)
        => new()
        {
            Entry = entry, Name = $"sword-{entry}", Class = (uint)ItemClass.Weapon, SubClass = 7,
            InventoryType = (uint)InventoryType.Weapon, Delay = delay, Damages = [new ItemDamage(min, max, 0)],
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue, Stackable = 1,
        };
}
