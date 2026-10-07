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
                    session.ManagedBudget = new ManagedActionBudget(4);
                    Assert.True(goals.Update(player, 500));
                    Assert.True(player.X > first);
                    float second = player.X;
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
        services.AddSingleton<INpcTemplateServiceMetadataSource>(fixture);
        services.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
        ]));
    }
}

internal sealed class TownVendorFixture : ICreatureDataStore, INpcContentStore, IQuestContentStore, ISpellContentStore, INpcTemplateServiceMetadataSource
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

    Task<IReadOnlyList<NpcTemplateServiceMetadata>> INpcTemplateServiceMetadataSource.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NpcTemplateServiceMetadata>>([new() { Entry = Entry, TrainerType = 0, TrainerClass = 1 }]);

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
                    NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Trainer), DisplayIds = [49] },
                   new CreatureTemplate { Entry = 923, Name = "Closer vendor", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] }]
                : [new CreatureTemplate { Entry = Entry, Name = "Town vendor", Faction = 900011,
                    NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Trainer), DisplayIds = [49] }],
            IncludeDistractingVendor
                ? [new CreatureSpawn { Guid = 92201, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f },
                   new CreatureSpawn { Guid = 92301, Entry = 923, MapId = 0, X = -8949.45f, Y = -132.49f, Z = 83.53f }]
                : [new CreatureSpawn { Guid = 92201, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
}
