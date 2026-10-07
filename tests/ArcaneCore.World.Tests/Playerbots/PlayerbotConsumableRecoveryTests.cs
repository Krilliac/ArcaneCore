using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotConsumableRecoveryTests
{
    private const uint FoodSpell = 992001;
    private const uint DrinkSpell = 992002;
    private const uint AmbiguousSpell = 992003;
    private const uint FoodItem = 992101;
    private const uint DrinkItem = 992102;

    [Fact]
    public async Task FoodRestStopsMovementAndStandingCancelsFoodBeforeRouteStarts()
    {
        await using WorldTestHost host = Start(out _, FoodTemplate(FoodItem, FoodSpell));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 };
        var brain = new PlayerbotBrain(session, options);
        try
        {
            await host.OnWorldAsync(() =>
            {
                InstallSpells(host, Food(FoodSpell));
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 1, out _));
                player.Health = 20;
                MovementInfo moving = player.Movement; moving.Flags |= MovementFlags.Forward;
                var packet = new PacketWriter(); moving.Write(packet);
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(session.TryManagedAction(WorldOpcode.MsgMoveStartForward, packet.ToArray()));
                brain.Update(500); // Stop before sitting or consuming.
                Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                brain.Update(500); // Sit.
                brain.Update(500); // Use the real food item.
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System
                .HasAura(session.Player!, FoodSpell), "rest food active");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                float x = player.X, y = player.Y;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Equal(StandState.Sit, player.StandState);
                Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                Assert.Equal(x, player.X); Assert.Equal(y, player.Y);
                Assert.True(PlayerbotNavigation.TryPlan(player, new(x + 8, y, player.Z), options, out PlayerbotRoute? route));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route!, options, 500, host.World.NowMs));
                Assert.Equal(StandState.Stand, player.StandState);
                Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                Assert.Equal(x, player.X); Assert.Equal(y, player.Y);
                Assert.False(host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(player, FoodSpell));
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task NonStarterFoodUsesRealItemPathAndConsumesOneStack()
    {
        await using WorldTestHost host = Start(out ItemTestContent items, FoodTemplate(FoodItem, FoodSpell),
            new ItemTemplate { Entry = 992199, Name = "plain upgrade boots", Class = 4,
                SubClass = 0, InventoryType = 8, Armor = 70, Stackable = 1 });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                InstallSpells(host, Food(FoodSpell));
                Player player = session.Player!;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(992199, 1, out _));
                player.Health = 20;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500); // sit
                brain.Update(500); // CMSG_USE_ITEM
                Assert.NotEqual(992199u, player.Inventory.GetItem(ArcaneCore.Game.Items.InventorySlots.Bag0,
                    ArcaneCore.Game.Items.InventorySlots.Feet)?.Entry);
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, FoodSpell), "food aura");
            Assert.Equal(0u, await host.PlayerStateAsync(session.Player!.Name, p => p.Inventory.GetItemCount(FoodItem)));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ManaOnlyDrinkIsSelectedWhenHealthIsFull()
    {
        await using WorldTestHost host = Start(out ItemTestContent items, DrinkTemplate(DrinkItem, DrinkSpell));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                InstallSpells(host, Drink(DrinkSpell));
                Player player = session.Player!;
                player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
                SpellSystem.SetPower(player, PowerType.Mana, 10);
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(DrinkItem, 1, out _));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500); // sit despite full health
                brain.Update(500); // CMSG_USE_ITEM
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, DrinkSpell), "drink aura");
            await host.WaitForWorldAsync(() => SpellSystem.GetPower(host.World.FindOnlinePlayer(session.Player!.Name)!, PowerType.Mana) > 10, "mana regeneration");
            Assert.Equal(0u, await host.PlayerStateAsync(session.Player!.Name, p => p.Inventory.GetItemCount(DrinkItem)));
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellSystem.SetPower(player, PowerType.Mana, 40);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(StandState.Sit, player.StandState); // Start threshold is not the stop threshold.
                SpellSystem.SetPower(player, PowerType.Mana, 100);
                brain.Update(500);
                Assert.Equal(StandState.Stand, player.StandState);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AmbiguousConsumableIsIgnoredWhileStarterFoodStillWorks()
    {
        await using WorldTestHost host = Start(out ItemTestContent items,
            new ItemTemplate { Entry = 992103, Name = "ambiguous", Class = 0, InventoryType = 0,
                Spells = [new ItemSpell(AmbiguousSpell, 0, 0, 0, 0, 0, 0)] }, FoodTemplate(117, FoodSpell));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                InstallSpells(host, Food(FoodSpell), Drink(DrinkSpell), Ambiguous());
                Player player = session.Player!;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(992103, 1, out _));
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(117, 1, out _));
                player.Health = 20;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                brain.Update(500);
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, FoodSpell), "starter food aura");
            Assert.Equal(1u, await host.PlayerStateAsync(session.Player!.Name, p => p.Inventory.GetItemCount(992103)));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task CombatOrRemovedRecoveryAuraEndsRestWaitImmediately()
    {
        await using WorldTestHost host = Start(out _, FoodTemplate(FoodItem, FoodSpell));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                InstallSpells(host, Food(FoodSpell));
                Player player = session.Player!;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 2, out _));
                player.Health = 20;
                session.ManagedBudget = new ManagedActionBudget(6);
                brain.Update(500);
                brain.Update(500);
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, FoodSpell), "active food aura");

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Map!.Combat.SetInCombatState(player, 60_000);
                session.ManagedBudget = new ManagedActionBudget(0);
                brain.Update(500);
                Assert.Equal(StandState.Sit, player.StandState);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(500);
                Assert.Equal(StandState.Stand, player.StandState);
                return true;
            });
            await host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, FoodSpell), "combat interrupts food aura");

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Map!.Combat.CombatStop(player);
                player.SetStandState(StandState.Sit);
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                brain.Update(500);
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, FoodSpell), "food aura reapply");
            await host.OnWorldAsync(() =>
            {
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                spells.System.RemoveAuras(session.Player!, FoodSpell);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(500);
                Assert.Equal(StandState.Stand, session.Player!.StandState);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private static WorldTestHost Start(out ItemTestContent items, params ItemTemplate[] templates)
    {
        items = new ItemTestContent();
        items.Templates.Templates.AddRange(templates);
        using (items.Use())
        {
            return WorldTestHost.Start();
        }
    }

    private static ItemTemplate FoodTemplate(uint entry, uint spell)
        => new() { Entry = entry, Name = "food", Class = 0, InventoryType = 0, Stackable = 20,
            Spells = [new ItemSpell(spell, 0, -1, 0, 0, 0, 0)] };

    private static ItemTemplate DrinkTemplate(uint entry, uint spell) => FoodTemplate(entry, spell) with { Name = "drink" };

    private static SpellInfo Food(uint id) => Aura(id, AuraType.ModRegen);
    private static SpellInfo Drink(uint id) => Aura(id, AuraType.ModPowerRegen, (int)PowerType.Mana);
    private static SpellInfo Ambiguous() => new()
    {
        Id = AmbiguousSpell, Name = "ambiguous consumable", AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
        Duration = new SpellDuration(60_000, 0, 60_000), Effects =
        [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRegen, TargetA = SpellImplicitTarget.UnitCaster },
         new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModPowerRegen, MiscValue = (int)PowerType.Mana, TargetA = SpellImplicitTarget.UnitCaster }, new()],
    };

    private static SpellInfo Aura(uint id, AuraType type, int misc = 0) => new()
    {
        Id = id, Name = "playerbot consumable", Duration = new SpellDuration(60_000, 0, 60_000),
        Attributes = SpellAttributes.AllowWhileSitting,
        AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = type,
            BasePoints = 4, BaseDice = 1, DieSides = 1, MiscValue = misc, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private static void InstallSpells(WorldTestHost host, params SpellInfo[] spells)
    {
        host.WorldServices.GetRequiredService<SpellFeature>().System.Store = new SpellStore(spells, [], []);
    }
}
