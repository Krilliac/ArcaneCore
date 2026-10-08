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
    /// the food it carries, not a white item that would be an upgrade; with a slot free again it stops selling.
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
                    Assert.False(goals.HasCandidate(player));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(goals.Update(player, 1500));
                    return true;
                });
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(9001)));
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(9303)));
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(117)));
                Assert.Equal(2u, await host.PlayerStateAsync("Controlone", p => p.Money));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
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
