using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
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
    private const uint BandageSpellId = 992010;
    private const uint BandageItem = 992110;
    private const uint StanceSpellId = 992011;

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
    public async Task BatchedFoodUse_KeepsTheBotSitting_UntilTheFoodLands()
    {
        // With a spell batch window the food's go waits for the boundary: the rest has begun, not ended. Standing up at the next update
        // would cancel the food the moment it lands (STANDING_CANCELS), and the bot would eat again and again.
        await using WorldTestHost host = Start(out _, FoodTemplate(FoodItem, FoodSpell));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            await host.OnWorldAsync(() =>
            {
                spells.SpellBatchWindowMs = 400;
                InstallSpells(host, Food(FoodSpell));
                Player player = session.Player!;
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(FoodItem, 2, out _));
                player.Health = 20;
                session.ManagedBudget = new ManagedActionBudget(6);
                brain.Update(500); // Sit.
                brain.Update(500); // Use the food: held for the batch boundary.
                Assert.True(spells.HasPendingGo(player));
                brain.Update(500); // Not landed yet: the rest goes on.
                Assert.Equal(StandState.Sit, player.StandState);
                return true;
            });
            await host.WaitForWorldAsync(() => spells.HasAura(session.Player!, FoodSpell), "the batched food landed");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                session.ManagedBudget = new ManagedActionBudget(6);
                brain.Update(500);
                Assert.Equal(StandState.Sit, player.StandState);
                Assert.True(spells.HasAura(player, FoodSpell));
                Assert.Equal(1u, player.Inventory.GetItemCount(FoodItem));
                return true;
            });
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

    /// <summary>
    /// A bandage is a channel (cast time 0) whose channel-interrupt flags include moving. Combat use used to start it
    /// while the chase was still walking, so the next heartbeat cancelled it before the cast-in-progress check could
    /// wait it out. The bot stops and channels in the same think (vmangos DoCastSpell stops the mover first).
    /// </summary>
    [Fact]
    public async Task MovingCombatBandage_StopsAndChannelsInTheSameThink()
    {
        // The 700 ms wait below is game time (AdvanceClockAsync), so this host needs the manual clock.
        await using WorldTestHost host = Start(out _, manualClock: true, BandageTemplate(BandageItem, BandageSpellId));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            float x = 0, y = 0;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(BandageChannel()).Append(BattleStance()), [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, StanceSpellId));
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(BandageItem, 5, out _));
                player.MaxHealth = 100;
                player.Health = 30; // below 40 (bandage), at or above 25 (not the healing potion)
                player.Map!.Combat.SetInCombatState(player, 60_000);
                var target = new Creature(77, new CreatureTemplate
                {
                    Entry = 900020, Name = "spell target", CreatureType = 1, MinLevelHealth = 20, MaxLevelHealth = 20,
                }, null, new CreatureContent([], [], [], [], []), new Random(1));
                target.Relocate(player.X + 40, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(target);

                var options = new PlayerbotOptions { MaxPathPoints = 8, MaxRouteYards = 400 };
                Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 100, player.Y, player.Z), options, out PlayerbotRoute? route));
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route!, options, 500, host.World.NowMs));
                Assert.True(player.Movement.HasFlag(MovementFlags.MaskMoving));

                var helper = new PlayerbotCombatSpells(session);
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(helper.Update(player, target, 0));
                Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                Assert.False(PlayerbotMotion.IsActive(player));
                SpellCast cast = feature.System.GetState(player.Guid)!.CurrentCast!;
                Assert.Equal(SpellCastState.Casting, cast.State);
                Assert.Equal(BandageSpellId, cast.Spell.Id);
                x = player.X;
                y = player.Y;
                return true;
            });

            await host.World.AdvanceClockAsync(700);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellCast? cast = host.WorldServices.GetRequiredService<SpellFeature>().System.GetState(player.Guid)?.CurrentCast;
                Assert.NotNull(cast);
                Assert.Equal(SpellCastState.Casting, cast.State);
                Assert.Equal(BandageSpellId, cast.Spell.Id);
                Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                Assert.True(Math.Abs(player.X - x) <= 0.5f && Math.Abs(player.Y - y) <= 0.5f);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private static WorldTestHost Start(out ItemTestContent items, params ItemTemplate[] templates)
        => Start(out items, manualClock: false, templates);

    private static WorldTestHost Start(out ItemTestContent items, bool manualClock, params ItemTemplate[] templates)
    {
        items = new ItemTestContent();
        items.Templates.Templates.AddRange(templates);
        using (items.Use())
        {
            return manualClock
                ? WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>())
                : WorldTestHost.Start();
        }
    }

    private static ItemTemplate FoodTemplate(uint entry, uint spell)
        => new() { Entry = entry, Name = "food", Class = 0, InventoryType = 0, Stackable = 20,
            Spells = [new ItemSpell(spell, 0, -1, 0, 0, 0, 0)] };

    private static ItemTemplate DrinkTemplate(uint entry, uint spell) => FoodTemplate(entry, spell) with { Name = "drink" };

    private static ItemTemplate BandageTemplate(uint entry, uint spell) => new()
    {
        Entry = entry, Name = "linen bandage", Class = 0, SubClass = 7, InventoryType = 0, Stackable = 20,
        Spells = [new ItemSpell(spell, 0, -1, 0, -1, 0, -1)],
    };

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

    /// <summary>Spell 746's shape: a self channel of periodic heal, cast time 0, channel interrupt flags include moving (0x3C0E).</summary>
    private static SpellInfo BandageChannel() => new()
    {
        Id = BandageSpellId, Name = "Linen Bandage", Mechanic = 16,
        AttributesEx = SpellAttributesEx.IsChanneled | SpellAttributesEx.IsSelfChanneled,
        Duration = new SpellDuration(8000, 0, 8000),
        ChannelInterruptFlags = (SpellAuraInterruptFlags)0x3C0E,
        AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicHeal, BasePoints = 10,
                Amplitude = 1000, TargetA = SpellImplicitTarget.UnitFriend },
            new(), new(),
        ],
    };

    private static SpellInfo BattleStance() => new()
    {
        Id = StanceSpellId, Name = "Battle Stance", RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModShapeshift, MiscValue = 17,
            TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private static void InstallSpells(WorldTestHost host, params SpellInfo[] spells)
    {
        host.WorldServices.GetRequiredService<SpellFeature>().System.Store = new SpellStore(spells, [], []);
    }
}
