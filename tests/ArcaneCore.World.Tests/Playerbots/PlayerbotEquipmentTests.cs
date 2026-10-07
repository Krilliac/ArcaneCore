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
    public async Task EquipmentRefusesDowngradeAmbiguousArmorAndCombatOrZeroBudget()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Template(40, InventoryType.Feet, 70));
        items.Templates.Templates.Add(Template(2967, InventoryType.Feet, 20, requiredLevel: 5));
        items.Templates.Templates.Add(Template(2968, InventoryType.Feet, 80, requiredLevel: 60));
        items.Templates.Templates.Add(Template(2969, InventoryType.Feet, 90) with { Stats = [new ItemStat(7, 1)] });
        items.Templates.Templates.Add(Template(2970, InventoryType.Feet, 100) with { MaxDurability = 10 });
        items.Templates.Templates.Add(Template(2972, InventoryType.Feet, 110) with { Stats = [new ItemStat(0, 1)] });
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
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2969, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2970, 1, out _));
                player.Inventory.AllItems.Single(item => item.Entry == 2970).Durability = 0;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(2972, 1, out _));
                var helper = new PlayerbotEquipment(session);
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.False(helper.Update(player));
                session.ManagedBudget = new ManagedActionBudget(1);
                player.Map!.Combat.SetInCombatState(player, 10_000);
                Assert.False(helper.Update(player));
                player.Map!.Combat.CombatStop(player);
                Assert.False(helper.Update(player)); // existing 70 armor beats the plain 20 upgrade; level-60 item is unusable.
                Assert.Equal(40u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Entry);
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

    private static ItemTemplate Template(uint entry, InventoryType type, int armor, uint requiredLevel = 0)
        => new()
        {
            Entry = entry, Name = $"armor-{entry}", Class = (uint)ItemClass.Armor,
            SubClass = ItemSubClasses.ArmorMisc, InventoryType = (uint)type, Armor = armor,
            RequiredLevel = requiredLevel, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
            Stackable = 1,
        };
}
