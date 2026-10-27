using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Net;
using ArcaneCore.World.Features;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Creatures;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Kernel.Accounts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotTownGoalsTests
{
    [Fact]
    public async Task TownTravelContinuesBetweenInteractionCooldowns()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "town NPC visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    player.Relocate(player.X - 25, player.Y, player.Z, player.Orientation, host.World.NowMs);
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(4);
                    Assert.True(goals.Update(player, 500)); // Start forward; no position leap.
                    float first = player.X;
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    Assert.True(goals.Update(player, 500));
                    Assert.True(player.X > first);
                    float second = player.X;
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    Assert.True(goals.Update(player, 500));
                    Assert.True(player.X > second); // The 1.5-second service throttle must not pause travel.
                    Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task UsefulTrainerKeepsBrainOwnershipDuringRequestCooldown()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "trainer visible");
                await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    player.Money = 100;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500); // Requests the ordinary trainer list.
                    Assert.Equal(PlayerbotGoalKind.Train, brain.Goal);
                    Assert.Equal(TownVendorFixture.Entry, brain.TargetEntry);
                    MovementInfo moving = player.Movement;
                    moving.Flags |= MovementFlags.Forward;
                    var movement = new PacketWriter(); moving.Write(movement);
                    Assert.True(session.TryManagedAction(WorldOpcode.MsgMoveStartForward, movement.ToArray()));
                    session.ManagedBudget = new ManagedActionBudget(4);
                    float x = player.X, y = player.Y;
                    brain.Update(500); // Cooldown must not fall through to exploration.
                    Assert.Equal(PlayerbotGoalKind.Train, brain.Goal);
                    Assert.Equal(TownVendorFixture.Entry, brain.TargetEntry);
                    Assert.Equal(x, player.X);
                    Assert.Equal(y, player.Y);
                    Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                    Assert.Null(brain.InspectionTarget);
                    return true;
                });
            }
            finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task UnaffordableTrainerDoesNotKeepTheTownGoalActive()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "trainer visible");
                await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    player.Money = 0;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(2);
                    Assert.False(goals.HasCandidate(player));
                    Assert.False(goals.Update(player, 1500));
                    Assert.Equal(2, session.ManagedBudget.Remaining);
                    Assert.Equal(0u, player.Money);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task UsefulTrainerWinsOverCloserUnneededVendorAndRetiresAfterLearning()
    {
        var npc = new TownVendorFixture { IncludeDistractingVendor = true };
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, FoodType = 1 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(npc));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid)
                    && session.Player.VisibleObjects.Contains(ObjectGuid.WithEntry(HighGuid.Unit, 923, 92301)), "trainer and closer vendor");
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    player.Money = 100;
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(117, 1, out _));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500), $"target={goals.TargetEntry}; candidate={goals.HasCandidate(player)}; food={player.Inventory.GetItemCount(117)}; trainer={host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.Deps.Creatures!.Find(player, TownVendorFixture.Guid)}");
                    Assert.Equal(TownVendorFixture.Entry, goals.TargetEntry);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500));
                    return true;
                });
                await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook
                    .HasSpell(session.Player!, TownVendorFixture.LearnedSpell), "useful trainer learned spell");
                await host.OnWorldAsync(() =>
                {
                    Assert.Equal(99u, session.Player!.Money);
                    Assert.False(goals.HasCandidate(session.Player));
                    Assert.False(goals.Update(session.Player, 1500));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task VendorFoodPurchase_UsesRealVendorRowsAndMoney()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, SellPrice = 1, FoodType = 1,
            Spells = [new ItemSpell(922012, 0, -1, 0, 0, 0, 0)] });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9001, Name = "Quest gray", Class = (uint)ItemClass.TradeGoods,
            Stackable = 20, SellPrice = 5 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                    spells.Store = new ArcaneCore.Game.Spells.SpellStore([.. spells.Store.All,
                        new ArcaneCore.Game.Spells.SpellInfo
                        {
                            Id = 922012, AuraInterruptFlags = ArcaneCore.Game.Spells.SpellAuraInterruptFlags.StandingCancels,
                            Effects = [new ArcaneCore.Game.Spells.SpellEffectInfo
                            {
                                Effect = ArcaneCore.Game.Spells.SpellEffectName.ApplyAura,
                                AuraType = ArcaneCore.Game.Spells.AuraType.ModRegen, BasePoints = 4, BaseDice = 1,
                            }, new(), new()],
                        }], [], []);
                    return true;
                });
                await host.OnWorldAsync(() =>
                {
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.True(goals.Update(session.Player!, 1000));
                    return true;
                });
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(117)));
                Assert.Equal(90u, await host.PlayerStateAsync("Controlone", p => p.Money));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task QuestRequiredGrayItem_IsNotSoldAtVendor()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, FoodType = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9001, Name = "Quest gray", Class = (uint)ItemClass.TradeGoods,
            Stackable = 20, SellPrice = 5 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Inventory.AddItem(117, 1, out _);
                    player.Inventory.AddItem(9001, 1, out _);
                    QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
                    QuestStatusData status = feature.Services.StateOf(player)!.Quests.GetOrAdd(TownVendorFixture.QuestId);
                    status.Status = QuestStatus.Incomplete;
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)NpcFlags.Vendor;
                    return true;
                });
                await host.OnWorldAsync(() =>
                {
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.False(goals.Update(session.Player!, 1000));
                    return true;
                });
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(9001)));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task GreenTrainerResponse_TeachesTheLoadedSpellThroughNormalHandler()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, FoodType = 1 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(npc));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "trainer visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    player.Inventory.AddItem(117, 1, out _);
                    return true;
                });
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                await host.OnWorldAsync(() => Assert.True(goals.Update(session.Player!, 1000)));
                await host.OnWorldAsync(() => Assert.True(goals.Update(session.Player!, 1500)));
                await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.HasSpell(session.Player!, TownVendorFixture.LearnedSpell), "trainer learned spell");
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }
    [Fact]
    public async Task CombatTrainer_IsNotAQuartermasterCandidate()
    {
        var fixture = new TrainerMetadataFixture();
        TrainerMetadataWorldServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            bool result = await host.OnWorldAsync(() =>
            {
                Creature trainer = (Creature)session.Player!.Map!.FindObject(TrainerMetadataFixture.Guid)!;
                trainer.UnitFlags |= UnitFlags.InCombat;
                return new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true }).HasCandidate(session.Player);
            });
            Assert.False(result);
            session.Kick();
            await session.ManagedClosed;
        }
        finally
        {
            TrainerMetadataWorldServices.Current.Value = null;
        }
    }

    /// <summary>
    /// mangoszero RepairAllAction: a worn item under a quarter of its durability (or broken) sends the bot's CMSG_REPAIR_ITEM
    /// (empty item guid: everything) at an NPC with the repair flag; the real handler prices it from the repair tables.
    /// </summary>
    [Fact]
    public async Task WornGearUnderAQuarterDurability_IsRepairedAtARepairNpc()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9201, durability: 20));
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(RepairPrices()));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "repair NPC visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    PlayerbotEquipmentTests.EquipFromBags(player, 9201);
                    Item boots = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!;
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Repair);
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    boots.Durability = 5; // exactly a quarter: still fine
                    Assert.False(PlayerbotTownGoals.NeedsRepair(player));
                    boots.Durability = 4;
                    Assert.True(PlayerbotTownGoals.NeedsRepair(player));
                    Assert.True(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500));
                    Assert.Equal(20u, boots.Durability);
                    Assert.Equal(84u, player.Money); // 16 points lost x multiplier 1 x quality factor 1
                    Assert.Equal(PlayerbotGoalKind.Vendor, goals.Goal);
                    Assert.False(PlayerbotTownGoals.NeedsRepair(player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>
    /// A broke bot with broken gear (live 2026-10-17: bot-gear/broken-unrepaired ~2000/h at 0 to 4 copper, "repair trip due True") sells
    /// its junk for the repair money although its bags have room, then repairs; before, it waited for money that never came.
    /// </summary>
    [Fact]
    public async Task ABrokeBotWithBrokenGear_SellsJunkForTheRepair_ThoughItsBagsHaveRoom()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9201, durability: 20));
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9301, Name = "White goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 7 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(RepairPrices()));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotEquipmentTests.EquipFromBags(player, 9201);
                    Item boots = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!;
                    boots.Durability = 0;
                    for (int i = 0; i < 4; i++)
                        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9301, 1, out _));
                    Assert.True(PlayerbotTownGoals.FreeBagSlots(player) >= PlayerbotTownGoals.JunkReserveSlots);
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Repair);
                    player.Money = 0;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.True(goals.HasCandidate(player), "a broke bot with broken gear and junk to sell has nothing to do at the vendor");
                    // 20 durability points at 1 copper each: three goods (21 copper) pay for it, then the repair.
                    for (int think = 0; think < 8 && boots.Durability == 0; think++)
                    {
                        session.ManagedBudget = new ManagedActionBudget(1);
                        goals.Update(player, 1500);
                    }
                    Assert.Equal(20u, boots.Durability);
                    Assert.Equal(1u, player.Inventory.GetItemCount(9301));
                    Assert.Equal(1u, player.Money); // 21 - 20
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>
    /// A vendor refuses to buy a damaged item it has no repair price for (PlayerInventory.SellItem: SELL_ERR_CANT_SELL_ITEM). The
    /// bot offers that gray item once, then neither offers it again nor keeps the vendor as a goal. Live 2026-10-08: Ironwander
    /// stood at Adlin Pridedrift for hours re-sending CMSG_SELL_ITEM for its damaged gray Frayed Pants, goal Vendor, no fault.
    /// </summary>
    [Fact]
    public async Task AGrayItemTheVendorRefuses_IsOfferedOnce_AndTheVendorIsNoLongerAGoal()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9401, durability: 25) with { Quality = 0, SellPrice = 3 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(); // no repair prices: a damaged item cannot be priced for sale
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9401, 1, out _));
                    Item pants = player.Inventory.AllItems.Single(item => item.Entry == 9401);
                    pants.Durability = 10;
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)NpcFlags.Vendor;
                    int offers = 0;
                    session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgSellItem) offers++; };
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.True(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500));
                    Assert.Equal(1, offers);
                    Assert.Same(pants, player.Inventory.GetItemByGuid(pants.Guid)); // refused: still in the bags
                    Assert.False(goals.HasCandidate(player));
                    for (int think = 0; think < 5; think++)
                    {
                        session.ManagedBudget = new ManagedActionBudget(1);
                        Assert.False(goals.Update(player, 1500));
                    }

                    Assert.Equal(1, offers);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>Without repair prices the handler repairs nothing: the bot tries once, then leaves the NPC alone for a minute.</summary>
    [Fact]
    public async Task ARepairThatChangesNothing_IsNotRepeatedEveryThink()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9201, durability: 20));
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "repair NPC visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    PlayerbotEquipmentTests.EquipFromBags(player, 9201);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)NpcFlags.Repair;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500));
                    Assert.Equal(0u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability);
                    Assert.False(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(goals.Update(player, 1500));
                    Assert.Equal(1, session.ManagedBudget.Remaining);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>
    /// With every bag slot taken, the bot sells the cheapest junk (a white trade good) and nothing it needs: not a quest item, not
    /// the food it carries, not a white item that would be an upgrade; it goes on selling junk, cheapest first, until
    /// <see cref="PlayerbotTownGoals.JunkReserveSlots"/> slots are free (one free slot was filled by the next corpse), then stops.
    /// </summary>
    [Fact]
    public async Task FullBags_SellTheCheapestJunk_AndNothingElse()
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, SellPrice = 1, FoodType = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9301, Name = "White goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 7 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9302, Name = "Cheap goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 2 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9001, Name = "Quest goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 1 });
        items.Templates.Templates.Add(Boots(9303, durability: 0) with { SellPrice = 1, Armor = 40 });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    foreach (Item existing in player.Inventory.AllItems.Where(item => item.BagSlot == InventorySlots.Bag0
                        && item.Slot >= InventorySlots.ItemStart && item.Slot < InventorySlots.ItemEnd).ToArray())
                        player.Inventory.DestroyItem(existing.BagSlot, existing.Slot);
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(117, 1, out _));
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9001, 1, out _));
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9302, 1, out _));
                    Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9303, 1, out _));
                    while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9301, 1, out _));
                    QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
                    feature.Services.StateOf(player)!.Quests.GetOrAdd(TownVendorFixture.QuestId).Status = QuestStatus.Incomplete;
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)NpcFlags.Vendor;
                    player.Money = 0;
                    return true;
                });
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.True(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500));
                    Assert.Equal(0u, player.Inventory.GetItemCount(9302));
                    Assert.Equal(1, PlayerbotTownGoals.FreeBagSlots(player));
                    // Then the white goods, one a service request, until the reserve is free.
                    for (int sale = 0; sale < 8 && PlayerbotTownGoals.FreeBagSlots(player) < PlayerbotTownGoals.JunkReserveSlots; sale++)
                    {
                        session.ManagedBudget = new ManagedActionBudget(1);
                        Assert.True(goals.Update(player, 1500));
                    }
                    Assert.Equal(PlayerbotTownGoals.JunkReserveSlots, PlayerbotTownGoals.FreeBagSlots(player));
                    Assert.False(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(goals.Update(player, 1500));
                    return true;
                });
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(9001)));
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(9303)));
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(117)));
                Assert.Equal(2u + (7u * (PlayerbotTownGoals.JunkReserveSlots - 1)), await host.PlayerStateAsync("Controlone", p => p.Money));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>
    /// #1442: a full-bag bot sells an unbound bind-on-equip green that is no upgrade for what it wears, and keeps an upgrade, a bound item, a quest
    /// item and (here) the protected filler. Returns whether a sale was wanted, the money earned and the bag counts of the probed entries.
    /// </summary>
    private static async Task<(bool Wanted, bool Sold, uint Money, uint[] Counts)> FullBagsOfGreensAsync(
        ItemTemplate[] probes, uint[] boundEntries)
    {
        var npc = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9600, durability: 25) with { Armor = 100 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9690, Name = "Skill goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 5, RequiredSkill = 164 });
        foreach (ItemTemplate probe in probes)
            items.Templates.Templates.Add(probe);
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                return await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    foreach (Item existing in player.Inventory.AllItems.Where(item => item.BagSlot == InventorySlots.Bag0
                        && item.Slot >= InventorySlots.ItemStart && item.Slot < InventorySlots.ItemEnd).ToArray())
                        player.Inventory.DestroyItem(existing.BagSlot, existing.Slot);
                    PlayerbotEquipmentTests.EquipFromBags(player, 9600);
                    foreach (ItemTemplate probe in probes)
                        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(probe.Entry, 1, out _));
                    foreach (Item item in player.Inventory.AllItems.Where(candidate => boundEntries.Contains(candidate.Entry)))
                        item.SetBinding(true);
                    while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9690, 1, out _));
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)NpcFlags.Vendor;
                    player.Money = 0;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    bool wanted = goals.HasCandidate(player);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    bool sold = goals.Update(player, 1500);
                    return (wanted, sold, (uint)player.Money, probes.Select(probe => player.Inventory.GetItemCount(probe.Entry)).ToArray());
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    private static ItemTemplate BoeGreen(uint entry, int armor, uint sellPrice, uint bonding = 2)
        => Boots(entry, durability: 25) with { Quality = 2, Bonding = bonding, SellPrice = sellPrice, Armor = armor };

    [Fact]
    public async Task FullBags_UnboundBindOnEquipGreen_NoUpgrade_IsSold()
    {
        (bool wanted, bool sold, uint money, uint[] counts) = await FullBagsOfGreensAsync([BoeGreen(9601, armor: 1, sellPrice: 40)], []);
        Assert.True(wanted, "a full-bag bot holding an unneeded unbound BoE green has no sell trip (#1442)");
        Assert.True(sold);
        Assert.Equal(0u, counts[0]);
        Assert.Equal(40u, money);
    }

    [Fact]
    public async Task FullBags_BindOnEquipUpgrade_IsKept()
    {
        (bool wanted, bool sold, _, uint[] counts) = await FullBagsOfGreensAsync([BoeGreen(9602, armor: 500, sellPrice: 40)], []);
        Assert.False(wanted);
        Assert.False(sold);
        Assert.Equal(1u, counts[0]);
    }

    [Fact]
    public async Task FullBags_BoundBindOnEquipGreen_IsKept()
    {
        (bool wanted, bool sold, _, uint[] counts) = await FullBagsOfGreensAsync([BoeGreen(9603, armor: 1, sellPrice: 40)], [9603]);
        Assert.False(wanted);
        Assert.False(sold);
        Assert.Equal(1u, counts[0]);
    }

    [Fact]
    public async Task FullBags_BindOnPickupGreen_IsKept()
    {
        (bool wanted, bool sold, _, uint[] counts) = await FullBagsOfGreensAsync([BoeGreen(9605, armor: 1, sellPrice: 40, bonding: 1)], []);
        Assert.False(wanted);
        Assert.False(sold);
        Assert.Equal(1u, counts[0]);
    }

    [Fact]
    public async Task FullBags_QuestItemGreen_IsKept()
    {
        ItemTemplate quest = new() { Entry = 9604, Name = "Quest green", Class = (uint)ItemClass.Quest, Quality = 2, Bonding = 2,
            Stackable = 1, SellPrice = 40 };
        (bool wanted, bool sold, _, uint[] counts) = await FullBagsOfGreensAsync([quest], []);
        Assert.False(wanted);
        Assert.False(sold);
        Assert.Equal(1u, counts[0]);
    }

    /// <summary>
    /// A hunter with a gun and no bullets buys the vendor's bullets (one CMSG_BUY_ITEM buys the BuyCount) and the out-of-combat
    /// upkeep then selects them with CMSG_SET_AMMO (vmangos AddHunterAmmo creates and sets them instead).
    /// </summary>
    [Fact]
    public async Task AHunterWithoutAmmo_BuysBulletsAndSelectsThem()
    {
        var npc = new TownVendorFixture { VendorItemsOverride = [new VendorItem { Entry = TownVendorFixture.Entry, Item = 9402, Slot = 0 }] };
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9401, Name = "Test gun", Class = (uint)ItemClass.Weapon, SubClass = 3,
            InventoryType = (uint)InventoryType.RangedRight, Delay = 3000, Damages = [new ItemDamage(5, 9, 0)], Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9402, Name = "Test shot", Class = (uint)ItemClass.Projectile, SubClass = 3,
            InventoryType = (uint)InventoryType.Ammo, Stackable = 200, BuyCount = 200, BuyPrice = 10, Damages = [new ItemDamage(1, 2, 0)] });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter); // as the pet tests make their hunters
                    player.Money = 100;
                    foreach (Item ammo in player.Inventory.AllItems.Where(item => item.Template.GetInventoryType() == InventoryType.Ammo).ToArray())
                        player.Inventory.DestroyItem(ammo.BagSlot, ammo.Slot);
                    player.Inventory.RemoveAmmo();
                    if (player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) is not null)
                        player.Inventory.DestroyItem(InventorySlots.Bag0, InventorySlots.Ranged);
                    PlayerbotEquipmentTests.EquipFromBags(player, 9401);
                    ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)NpcFlags.Vendor;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.True(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1500));
                    Assert.Equal(200u, player.Inventory.GetItemCount(9402));
                    Assert.Equal(90u, player.Money);
                    Assert.False(goals.HasCandidate(player)); // 200 rounds: no further purchase
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(new PlayerbotEquipment(session).Update(player));
                    Assert.Equal(9402u, player.Inventory.AmmoId);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>
    /// Broken gear and no repair NPC in sight (live 2026-10-11, analysis bot-xp-analysis.md 4.1: 45% of the population bots' worn items
    /// at 0 durability; a repair happened only when a repair NPC was in sight). With too little money for even the cheapest repair the
    /// bot does not set out; with enough it walks to the repair NPC 130 yards away (out of sight), and once it sees it, repairs through
    /// the real handler (CMSG_REPAIR_ITEM) and pays.
    /// </summary>
    [Fact]
    public async Task BrokenGear_WithNoRepairNpcInSight_TravelsToOne_AndRepairs_WhenItCanPay()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9211, durability: 20));
        using IDisposable itemScope = items.Use();
        FarRepairServices.Current.Value = new FarRepairFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(RepairPrices()));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                float start = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    PlayerbotEquipmentTests.EquipFromBags(player, 9211);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    Assert.True(PlayerbotTownGoals.NeedsRepairTrip(player));
                    Assert.DoesNotContain(FarRepairFixture.Guid, player.VisibleObjects);

                    // The repair costs 20 copper (20 points x multiplier 1 x quality factor 1): 10 is not enough to set out.
                    player.Money = 10;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(goals.HasCandidate(player));
                    Assert.False(goals.Update(player, 1500));
                    Assert.Equal(1, session.ManagedBudget.Remaining);

                    player.Money = 100;
                    Assert.True(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 500));
                    Assert.Equal(FarRepairFixture.Entry, goals.TargetEntry);
                    Assert.Equal(PlayerbotGoalKind.Vendor, goals.Goal);
                    return player.X;
                });

                // The walk: every think carries the bot further east, toward the repair NPC (the test world's terrain stepper lets it
                // walk only a few yards from the start position, as in the trainer trip test; the arrival is set up below).
                float walked = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    float last = player.X;
                    for (int think = 0; think < 2; think++)
                    {
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(1);
                        Assert.True(goals.Update(player, 500), $"think {think}: the trip stopped at x {player.X:F1}");
                        Assert.True(player.X > last, $"think {think}: no step east from x {last:F1}");
                        Assert.Equal(FarRepairFixture.Entry, goals.TargetEntry);
                        last = player.X;
                    }

                    return player.X;
                });
                Assert.True(walked > start + 5f, $"the bot walked from x {start:F1} only to {walked:F1}");

                // Arrived (the rest of the 130 yards as it would walk them): the NPC comes into sight and the visit repairs and pays.
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotMovementControl.Stop(session, player);
                    player.Relocate(FarRepairFixture.X - 3f, player.Y, player.Z, 0, host.World.NowMs);
                });
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(FarRepairFixture.Guid), "repair NPC visible");
                bool repaired = false;
                for (int think = 0; think < 10 && !repaired; think++)
                {
                    repaired = await host.OnWorldAsync(() =>
                    {
                        Player player = session.Player!;
                        session.ManagedBudget = new ManagedActionBudget(1);
                        goals.Update(player, 1500);
                        return player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability == 20;
                    });
                }

                Assert.True(repaired, "the boots were not repaired at the repair NPC");
                Assert.Equal(80u, await host.OnWorldAsync(() => session.Player!.Money));
                Assert.False(await host.OnWorldAsync(() => PlayerbotTownGoals.NeedsRepairTrip(session.Player!)));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarRepairServices.Current.Value = null; }
    }

    /// <summary>
    /// Cluster 4 of the bot QA stalls (#684, #700, #705, #741, #845, #884 and more: <c>loot-bag-full -> goal=Explore</c> before the stall):
    /// bags full of things to sell and no vendor in sight. The bot walks to the vendor 130 yards away (out of sight), as it walks to a
    /// repair NPC for broken gear, instead of leaving every corpse unlooted and exploring on. It needs no money for that trip.
    /// </summary>
    [Fact]
    public async Task FullBags_WithNoVendorInSight_TravelToOne()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, SellPrice = 1, FoodType = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9301, Name = "White goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 7 });
        using IDisposable itemScope = items.Use();
        FarRepairServices.Current.Value = new FarRepairFixture
        {
            Flags = NpcFlags.Vendor, VendorItems = [new VendorItem { Entry = FarRepairFixture.Entry, Item = 117, Slot = 0 }],
        };
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                float start = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    Assert.DoesNotContain(FarRepairFixture.Guid, player.VisibleObjects);
                    player.Money = 0;
                    Assert.False(PlayerbotTownGoals.NeedsRepairTrip(player));

                    // Free bag slots: no trip.
                    Assert.True(PlayerbotTownGoals.FreeBagSlots(player) > 0);
                    Assert.False(goals.HasCandidate(player));

                    while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9301, 1, out _));
                    Assert.True(goals.HasCandidate(player), "full bags of goods to sell are no reason to walk to the vendor out of sight");
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 500));
                    Assert.Equal(FarRepairFixture.Entry, goals.TargetEntry);
                    Assert.Equal(PlayerbotGoalKind.Vendor, goals.Goal);
                    return player.X;
                });

                float walked = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    float last = player.X;
                    for (int think = 0; think < 2; think++)
                    {
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(1);
                        Assert.True(goals.Update(player, 500), $"think {think}: the trip stopped at x {player.X:F1}");
                        Assert.True(player.X > last, $"think {think}: no step east from x {last:F1}");
                        last = player.X;
                    }

                    return player.X;
                });
                Assert.True(walked > start + 5f, $"the bot walked from x {start:F1} only to {walked:F1}");
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarRepairServices.Current.Value = null; }
    }

    /// <summary>
    /// A holiday vendor (Javnir Nashak 15012, Professor Thaddeus Paleo 14847) keeps a <c>creature</c> row all year and is in the world only
    /// while its game event runs. Full bags must walk to the ordinary vendor farther off, not to the nearer empty spawn
    /// (<c>town/service-npc-missing</c>: stood 0.2 yd from spawn 88829 and found nobody).
    /// </summary>
    [Fact]
    public async Task FullBags_SkipAVendorWhoseEventIsOff_AndWalkToOneThatIsInTheWorld()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, SellPrice = 1, FoodType = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9301, Name = "White goods", Class = (uint)ItemClass.TradeGoods,
            Quality = 1, Stackable = 1, SellPrice = 7 });
        using IDisposable itemScope = items.Use();
        HolidayVendorServices.Current.Value = new HolidayVendorFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                float start = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    player.Map!.FindUpdater<CreatureMapSystem>()!.SpawnGate = new RefuseCreatureSpawn(HolidayVendorFixture.HolidaySpawn);
                    while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9301, 1, out _));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 500));
                    Assert.Equal(HolidayVendorFixture.PresentEntry, goals.TargetEntry);
                    Assert.NotEqual(HolidayVendorFixture.HolidayEntry, goals.TargetEntry);
                    Assert.Equal(PlayerbotGoalKind.Vendor, goals.Goal);
                    return player.X;
                });

                float walked = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    float last = player.X;
                    for (int think = 0; think < 2; think++)
                    {
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(1);
                        Assert.True(goals.Update(player, 500), $"think {think}: the trip stopped at x {player.X:F1}");
                        Assert.True(player.X > last, $"think {think}: no step east from x {last:F1}");
                        Assert.Equal(HolidayVendorFixture.PresentEntry, goals.TargetEntry);
                        last = player.X;
                    }

                    return player.X;
                });
                Assert.True(walked > start + 5f, $"the bot walked from x {start:F1} only to {walked:F1}");
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { HolidayVendorServices.Current.Value = null; }
    }

    private static ItemTemplate Boots(uint entry, uint durability) => new()
    {
        Entry = entry, Name = $"boots-{entry}", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
        InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = durability, ItemLevel = 10, Quality = 1, Stackable = 1,
        AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
    };

    /// <summary>DurabilityCosts row for item level 10 (every multiplier 1) and DurabilityQuality factor 1 for quality 1 ((1 + 1) x 2).</summary>
    private static RepairCostTable RepairPrices() => new([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]);

    [Fact]
    public async Task MissingNpcContent_IsAClosedNoActionFallback()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                Assert.False(goals.HasCandidate(session.Player!));
                Assert.False(goals.Update(session.Player!, 1000));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task WaypointPatrollingVendor_UsesLivePositionWhenLoaded_AndSkipsWhenUnloaded()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var patrollerSpawn = new CreatureSpawn
                {
                    Guid = 99945655,
                    Entry = 844,
                    MapId = player.MapId,
                    X = -9468.2f,
                    Y = 16.5f,
                    Z = 56.9f,
                    MovementType = 2,
                };

                // When the patroller is not loaded in the map, requireLivePatroller=true skips it (no walk to empty static spawn),
                // while requireLivePatroller=false falls back to the static spawn.
                Assert.False(PlayerbotWorldDestinations.TryResolveSpawnPosition(player, patrollerSpawn, requireLivePatroller: true, out _));
                Assert.True(PlayerbotWorldDestinations.TryResolveSpawnPosition(player, patrollerSpawn, requireLivePatroller: false, out Vector3 fallbackPos));
                Assert.Equal(new Vector3(-9468.2f, 16.5f, 56.9f), fallbackPos);

                // Once the patroller is in the world and has walked 500 yards down the road along its waypoints, both resolve its live position.
                var template = new CreatureTemplate { Entry = 844, Name = "Antonio Perelli", NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49], MinLevel = 10, MaxLevel = 10 };
                var creature = new Creature(patrollerSpawn.Guid, template, patrollerSpawn, new CreatureContent([template], [patrollerSpawn], [], [], []), new Random(1));
                creature.Relocate(-9950.0f, -220.0f, 42.0f, 0f, 0);
                player.Map!.AddObject(creature);
                try
                {
                    Assert.True(PlayerbotWorldDestinations.TryResolveSpawnPosition(player, patrollerSpawn, requireLivePatroller: true, out Vector3 livePos));
                    Assert.Equal(new Vector3(-9950.0f, -220.0f, 42.0f), livePos);
                }
                finally
                {
                    player.Map.RemoveObject(creature);
                }

                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Regression for #778/broken-unrepaired: when a visible vendor's exact route is refused (<c>_visitRefused</c>),
    /// <c>FindNpc</c> skips that refused vendor during its 10-second backoff so a bot with broken gear can still
    /// travel to a reachable repair NPC instead of aborting its repair trip on every think.
    /// </summary>
    [Fact]
    public async Task RefusedVisibleVendor_IsSkippedDuringBackoff_AndDoesNotBlockFarRepairTrip()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9211, durability: 20));
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 117, Name = "Starter food", Class = (uint)ItemClass.Consumable,
            Stackable = 20, BuyPrice = 10, SellPrice = 1, FoodType = 1,
        });
        using IDisposable itemScope = items.Use();
        FarRepairServices.Current.Value = new FarRepairFixture
        {
            IncludeNearbyVendor = true,
            VendorItems = [new VendorItem { Entry = 9901, Item = 117, Slot = 0 }],
        };
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(RepairPrices()));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(
                    () => session.Player!.VisibleObjects.Contains(ObjectGuid.WithEntry(HighGuid.Unit, 9901, 990101)),
                    "nearby vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    float startX = player.X;

                    WorldCollision.Of(host.World).Install(
                        lineOfSight: new FlatFloor(),
                        pathfinder: new BlockWestPathfinder(startX - 5f));

                    PlayerbotEquipmentTests.EquipFromBags(player, 9211);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    player.Money = 100;

                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(4);
                    goals.BeginThink();
                    bool first = goals.Update(player, 500);
                    goals.BeginThink();
                    bool second = goals.Update(player, 500);
                    Assert.True(first || second, "the refused visible vendor blocked the bot from starting its far repair trip");
                    Assert.Equal(FarRepairFixture.Entry, goals.TargetEntry);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarRepairServices.Current.Value = null; }
    }

    /// <summary>
    /// GitHub #1476 (Vendor and Train visit class): the visit leg to a visible service NPC planned a route that the bot then never made
    /// progress on (the plan succeeds, the walk does not close). Unlike the repair trip (<see cref="PlayerbotTownGoals.RepairTripStuckMs"/>)
    /// the visit had no stuck clock, so only the 120 s stall watch ended it. The visit must be given up, the NPC set aside, well before.
    /// </summary>
    [Fact]
    public async Task AVisitThatNeverMakesProgress_IsGivenUpLongBeforeTheStallWatch_AndSetsTheNpcAside()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "town NPC visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    player.Relocate(player.X - 25, player.Y, player.Z, player.Orientation, host.World.NowMs);
                    float startX = player.X;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true })
                    {
                        Suspensions = new PlayerbotSuspensions(),
                    };
                    uint stuckMs = 0;
                    bool givenUp = false;
                    const uint stepMs = 500;
                    // 60 s of world time, half of the stall watch's 120 s: the motion is never elapsed, so the bot never leaves where it stands (no progress).
                    for (int step = 0; step < 120 && !givenUp; step++)
                    {
                        session.ManagedBudget = new ManagedActionBudget(4);
                        goals.BeginThink();
                        if (!goals.Update(player, stepMs)) givenUp = true;
                        else stuckMs += stepMs;
                    }

                    Assert.True(Math.Abs(player.X - startX) < 1f, "the bot moved: the test does not model a bot with no progress");
                    Assert.True(givenUp, "the visit leg held the bot for 60 s without any progress");
                    Assert.True(stuckMs <= 20_000, $"given up only after {stuckMs} ms of no progress");
                    Assert.True(goals.Suspensions!.IsEntrySuspended(TownVendorFixture.Entry, host.World.NowMs),
                        "the NPC the bot could not close on was not set aside");
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    private sealed class BlockWestPathfinder(float minX) : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => end.X < minX ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
    }
}

internal sealed class TownVendorServices : IWorldTestServices
{
    public static readonly AsyncLocal<TownVendorFixture?> Current = new();
    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
        ]));
    }
}

internal sealed class TownVendorFixture : ICreatureDataStore, INpcContentStore, IQuestContentStore, ISpellContentStore
{
    public bool IncludeDistractingVendor { get; init; }
    public IReadOnlyList<VendorItem>? VendorItemsOverride { get; init; }
    public const uint Entry = 922;
    public const uint QuestId = 922001;
    public const uint TeachingSpell = 922010;
    public const uint LearnedSpell = 922011;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 92201);

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NpcContent.Empty with { VendorItems = VendorItemsOverride ?? (IncludeDistractingVendor
                ? [new VendorItem { Entry = Entry, Item = 117, Slot = 0 }, new VendorItem { Entry = 923, Item = 117, Slot = 0 }]
                : [new VendorItem { Entry = Entry, Item = 117, Slot = 0 }]),
            TrainerSpells = [new TrainerSpell { Entry = Entry, Spell = TeachingSpell, SpellCost = 1 }] });

    Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new QuestContent([
            new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, ReqItemId1 = 9001, ReqItemCount1 = 1 }
        ], [], []));

    Task<SpellContent> ISpellContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new SpellContent([
            new SpellTemplateRow { Id = TeachingSpell, SpellName = "Town teaching", SpellLevel = 1,
                Effect1 = (uint)ArcaneCore.Game.Spells.SpellEffectName.LearnSpell, EffectTriggerSpell1 = LearnedSpell,
                EffectImplicitTargetA1 = (uint)ArcaneCore.Game.Spells.SpellImplicitTarget.UnitCaster },
            new SpellTemplateRow { Id = LearnedSpell, SpellName = "Town learned", SpellLevel = 1 },
        ], [], [], [], [], [], []));

    Task ISpellContentStore.ReplaceDbcTablesAsync(SpellDbcContent content, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new CreatureContent(
            IncludeDistractingVendor
                ? [new CreatureTemplate { Entry = Entry, Name = "Town trainer", Faction = 900011,
                    NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Trainer), DisplayIds = [49], TrainerType = 0, TrainerClass = 1 },
                   new CreatureTemplate { Entry = 923, Name = "Closer vendor", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] }]
                : [new CreatureTemplate { Entry = Entry, Name = "Town vendor", Faction = 900011,
                    NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Trainer), DisplayIds = [49], TrainerType = 0, TrainerClass = 1 }],
            IncludeDistractingVendor
                ? [new CreatureSpawn { Guid = 92201, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f },
                   new CreatureSpawn { Guid = 92301, Entry = 923, MapId = 0, X = -8949.45f, Y = -132.49f, Z = 83.53f }]
                : [new CreatureSpawn { Guid = 92201, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
}

/// <summary>
/// A repair NPC 130 yards east of the start position (beyond the 100-yard visibility), friendly to the bot; or, with
/// <see cref="Flags"/> and <see cref="VendorItems"/>, another service NPC there (a vendor).
/// </summary>
internal sealed class FarRepairFixture : ICreatureDataStore, INpcContentStore
{
    public const uint Entry = 945;
    public const float X = -8819.95f;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 94501);
    public NpcFlags Flags { get; init; } = NpcFlags.Repair;
    public bool IncludeNearbyVendor { get; init; }
    public IReadOnlyList<VendorItem> VendorItems { get; init; } = [];

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(VendorItems.Count == 0 ? NpcContent.Empty : NpcContent.Empty with { VendorItems = VendorItems });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        IncludeNearbyVendor
            ? [new CreatureTemplate { Entry = Entry, Name = "far armorer", Faction = 900011, NpcFlags = (uint)Flags, DisplayIds = [49] },
               new CreatureTemplate { Entry = 9901, Name = "blocked vendor", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] }]
            : [new CreatureTemplate { Entry = Entry, Name = "far armorer", Faction = 900011, NpcFlags = (uint)Flags, DisplayIds = [49] }],
        IncludeNearbyVendor
            ? [new CreatureSpawn { Guid = 94501, Entry = Entry, MapId = 0, X = X, Y = -132.49f, Z = 83.53f },
               new CreatureSpawn { Guid = 990101, Entry = 9901, MapId = 0, X = -8969.95f, Y = -132.49f, Z = 83.53f }]
            : [new CreatureSpawn { Guid = 94501, Entry = Entry, MapId = 0, X = X, Y = -132.49f, Z = 83.53f }], [], [], []));
}

internal sealed class FarRepairServices : IWorldTestServices
{
    public static readonly AsyncLocal<FarRepairFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}

/// <summary>A nearer holiday vendor the spawn gate refuses, and an ordinary vendor farther east (both beyond visibility).</summary>
internal sealed class HolidayVendorFixture : ICreatureDataStore, INpcContentStore
{
    public const uint HolidayEntry = 946;
    public const uint HolidaySpawn = 94601;
    public const uint PresentEntry = 945;
    public const float PresentX = -8819.95f;

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NpcContent.Empty with
        {
            VendorItems =
            [
                new VendorItem { Entry = HolidayEntry, Item = 117, Slot = 0 },
                new VendorItem { Entry = PresentEntry, Item = 117, Slot = 0 },
            ],
        });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [
            new CreatureTemplate { Entry = HolidayEntry, Name = "harvest hawker", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] },
            new CreatureTemplate { Entry = PresentEntry, Name = "town hawker", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] },
        ],
        [
            new CreatureSpawn { Guid = HolidaySpawn, Entry = HolidayEntry, MapId = 0, X = -8839.95f, Y = -132.49f, Z = 83.53f },
            new CreatureSpawn { Guid = 94501, Entry = PresentEntry, MapId = 0, X = PresentX, Y = -132.49f, Z = 83.53f },
        ], [], [], []));
}

internal sealed class HolidayVendorServices : IWorldTestServices
{
    public static readonly AsyncLocal<HolidayVendorFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}

/// <summary>A spawn gate that keeps one creature spawn out of the world (its game event is off).</summary>
internal sealed class RefuseCreatureSpawn(uint spawnGuid) : ISpawnGate
{
    public bool AllowsCreature(uint guid) => guid != spawnGuid;
    public bool AllowsGameObject(uint guid) => true;
    public IEnumerable<uint> GatedCreatures => [spawnGuid];
    public IEnumerable<uint> GatedGameObjects => [];
}
