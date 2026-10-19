using System.IO;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Chat;
using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemSets;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Fuzz;

/// <summary>
/// Exception guard tests and deterministic seeded fuzz tests across Items, Loot, Quests, Crafting,
/// Economy, Guilds, Groups, Social, Chat, Channels, Battlegrounds, Honor, Progression, Talents,
/// Skills, Reputation, AntiCheat, WorldState, ClientErrors, and Reload subsystems.
/// </summary>
public sealed class GameEconomySocialPvpSystemsFuzzAndExceptionTests
{
    private static Player MakePlayer(uint low = 1, byte level = 60)
    {
        Player player = TestWorld.CreatePlayer(low, 0f, 0f, new FakeSession());
        player.Level = level;
        return player;
    }

    // -------------------------------------------------------------------------
    // 1. Items & Inventory Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Item_And_ConjuredItems_NullGuards_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Item(1, null!, ObjectGuid.Player(1)));
        Assert.Throws<NullReferenceException>(() => Item.Create(1, null!, ObjectGuid.Player(1)));
        Assert.Throws<ArgumentNullException>(() => ConjuredItems.VanishesAfter(null!, 1000));
        Assert.Throws<ArgumentNullException>(() => ConjuredItems.WithoutVanished(null!, ItemTemplateStore.Empty, 1000));
        Assert.Throws<ArgumentNullException>(() => ConjuredItems.WithoutVanished([], null!, 1000));
        Assert.Throws<ArgumentNullException>(() => new ItemTemplateStore(null!));
        Assert.Throws<ArgumentNullException>(() => new LiveItemTemplateStore().Replace(null!));
    }

    [Fact]
    public void ItemRandomProperties_And_ItemSetBonuses_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new ItemRandomProperties(null!));
        var randomProps = new ItemRandomProperties(new ItemRandomPropertyCatalog([], []));
        Assert.Throws<ArgumentNullException>(() => randomProps.Generate(null!));

        var item = new Item(1, new ItemTemplate { Entry = 1 }.Normalized(), ObjectGuid.Player(1));
        var propRecord = new ItemRandomPropertyRecord(1, "of the Bear", [1, 0, 0]);
        Assert.Throws<ArgumentNullException>(() => ItemRandomProperties.Apply(null!, propRecord));
        Assert.Throws<ArgumentNullException>(() => ItemRandomProperties.Apply(item, null!));

        Assert.Throws<ArgumentNullException>(() => new ItemSetRecord(1, null!, new uint[8], new uint[8], 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ItemSetRecord(1, "Set", null!, new uint[8], 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ItemSetRecord(1, "Set", new uint[8], null!, 0, 0));
        Assert.Throws<ArgumentException>(() => new ItemSetRecord(1, "Set", [1, 2], new uint[8], 0, 0));
        Assert.Throws<ArgumentException>(() => new ItemSetRecord(1, "Set", new uint[8], [1, 2], 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ItemSetCatalog(null!));

        var spellSys = new SpellSystem(SpellStore.Empty, () => 0);
        Assert.Throws<ArgumentNullException>(() => new ItemSetBonuses(null!, spellSys));
        Assert.Throws<ArgumentNullException>(() => new ItemSetBonuses(ItemSetCatalog.Empty, null!));

        var setBonuses = new ItemSetBonuses(ItemSetCatalog.Empty, spellSys);
        Player player = MakePlayer(1);
        Assert.Throws<ArgumentNullException>(() => setBonuses.ReconcileAtFormChange(null!, new PlayerItemSets()));
        Assert.Throws<ArgumentNullException>(() => setBonuses.ReconcileAtFormChange(player, null!));
    }

    [Fact]
    public void ItemUseService_And_PlayerInventory_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => ItemUseService.Install(null!));
        var spellSys = new SpellSystem(SpellStore.Empty, () => 0);
        ItemUseService service = ItemUseService.Install(spellSys);
        Assert.Throws<InvalidOperationException>(() => ItemUseService.Install(spellSys));

        Player player = MakePlayer(1);
        Assert.Throws<ArgumentNullException>(() => service.UseItem(null!, 0, 0, 0, SpellCastTargets.ForSelf()));
        Assert.Throws<ArgumentNullException>(() => service.UseItem(player, 0, 0, 0, null!));

        Assert.Throws<ArgumentNullException>(() => new PlayerInventory((Player)null!));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.Load(null!));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.CanUseItem((ItemTemplate)null!));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.CanUseItem((Item)null!));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.CanStoreNewItem(1, 1, null!, out _));

        var item = new Item(1, new ItemTemplate { Entry = 1 }.Normalized(), player.Guid);
        Assert.Throws<ArgumentNullException>(() => player.Inventory.StoreItem(null!, item));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.StoreItem([new ItemPosCount(InventorySlots.Bag0, InventorySlots.ItemStart, 1)], null!));
        Assert.Throws<ArgumentException>(() => player.Inventory.StoreItem([], item));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.EquipItem(InventorySlots.Head, null!));
        Assert.Throws<ArgumentNullException>(() => player.Inventory.DestroyItemCount(null!, 1));
    }

    // -------------------------------------------------------------------------
    // 2. Loot Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void LootGenerator_LootMoneyRules_And_LootService_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new LootGenerator(null!));
        Assert.Throws<ArgumentNullException>(() => LootMoneyRules.Generate(1, 10, 1f, null!));
        Assert.Throws<ArgumentNullException>(() => LootMoneyRules.GenerateForGameObject(null!, 1, 1f, new Random(1)));
        Assert.Throws<ArgumentNullException>(() => new LootService(null!));

        var lootSvc = new LootService(LootContent.Empty);
        Player player = MakePlayer(1);
        var item = new Item(1, new ItemTemplate { Entry = 1 }.Normalized(), player.Guid);

        Assert.Throws<ArgumentNullException>(() => lootSvc.ReplaceContent(null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.Generate(ObjectGuid.Empty, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, 1, null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.RecipientsFor(null!, player, out _));
        Assert.Throws<ArgumentNullException>(() => lootSvc.RecipientsFor(player, null!, out _));
        Assert.Throws<ArgumentNullException>(() => lootSvc.OpenSkinning(null!, null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.OpenSkinning(player, null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.OpenItem(null!, item));
        Assert.Throws<ArgumentNullException>(() => lootSvc.OpenItem(player, null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.Open(null!, ObjectGuid.Empty));
        Assert.Throws<ArgumentNullException>(() => lootSvc.TakeItem(null!, 0));
        Assert.Throws<ArgumentNullException>(() => lootSvc.GiveMasterLoot(null!, ObjectGuid.Empty, 0, ObjectGuid.Empty));
        Assert.Throws<ArgumentNullException>(() => lootSvc.TakeMoney(null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.Release(null!, ObjectGuid.Empty));
        Assert.Throws<ArgumentNullException>(() => lootSvc.OnPlayerLeft(null!));
        Assert.Throws<ArgumentNullException>(() => lootSvc.Rolls.Vote(null!, ObjectGuid.Empty, 0, RollVote.Pass));
    }

    // -------------------------------------------------------------------------
    // 3. Crafting & Enchanting Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Crafting_And_Enchanting_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => ReagentRules.Install(null!));
        Assert.Throws<ArgumentNullException>(() => ReagentRules.IsInstalled(null!));
        Assert.Throws<ArgumentNullException>(() => CreateItemSpells.Install(null!));

        var sysWithoutReagents = new SpellSystem(SpellStore.Empty, () => 0);
        Assert.Throws<InvalidOperationException>(() => CreateItemSpells.Install(sysWithoutReagents));

        var sys = new SpellSystem(SpellStore.Empty, () => 0);
        ReagentRules.Install(sys);
        Assert.Throws<InvalidOperationException>(() => ReagentRules.Install(sys));
        CreateItemSpells.Install(sys);
        Assert.Throws<InvalidOperationException>(() => CreateItemSpells.Install(sys));

        Assert.Throws<ArgumentNullException>(() => CreateItemSpells.Check(null!));
        Assert.Throws<ArgumentNullException>(() => CreateItemSpells.Handle(null!));
        Assert.Throws<ArgumentNullException>(() => CreateItemSpells.DoCreateItem(null!, null!, 1, 1, false));
        Assert.Throws<ArgumentNullException>(() => EnchantCatalogDefinitions.From(null!));
        Assert.Throws<ArgumentNullException>(() => EnchantCatalogDefinitions.Convert(null!));
    }

    // -------------------------------------------------------------------------
    // 4. Economy & AuctionBot Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Economy_And_AuctionBot_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new AuctionBotPricing(null!));
        var opts = new AuctionBotOptions();
        var pricing = new AuctionBotPricing(opts);
        Assert.Throws<ArgumentNullException>(() => pricing.ClassValue(null!));
        Assert.Throws<ArgumentNullException>(() => pricing.BuyoutPerItem(null!));
        Assert.Throws<ArgumentNullException>(() => pricing.WithVariance(100, null!));

        Assert.Throws<ArgumentNullException>(() => AuctionBotCustodyLedger.OperationIdFor(null!));
        var ledger = new AuctionBotCustodyLedger(opts);
        Assert.Throws<ArgumentNullException>(() => ledger.Load(null!));
        Assert.Throws<ArgumentNullException>(() => ledger.Reconcile(null!, 0));

        Assert.Throws<ArgumentNullException>(() => new AuctionBotItemPool(null!, opts, pricing));
        Assert.Throws<ArgumentNullException>(() => new AuctionBotItemPool([], null!, pricing));
        Assert.Throws<ArgumentNullException>(() => new AuctionBotItemPool([], opts, null!));

        var pool = new AuctionBotItemPool([], opts, pricing);
        var rng = new Random(1);
        Assert.Throws<ArgumentNullException>(() => new AuctionBotPlanner(null!, pricing, pool, rng));
        Assert.Throws<ArgumentNullException>(() => new AuctionBotPlanner(opts, null!, pool, rng));
        Assert.Throws<ArgumentNullException>(() => new AuctionBotPlanner(opts, pricing, null!, rng));
        Assert.Throws<ArgumentNullException>(() => new AuctionBotPlanner(opts, pricing, pool, null!));

        var planner = new AuctionBotPlanner(opts, pricing, pool, rng);
        Assert.Throws<ArgumentNullException>(() => planner.Overrides = null!);
        Assert.Throws<ArgumentNullException>(() => planner.ValuePerItem(null!));
        Assert.Throws<ArgumentNullException>(() => planner.PlanBuy(null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => planner.PlanBuy([], null!));
    }

    // -------------------------------------------------------------------------
    // 5. Groups, Guilds, Social, Chat, Channels, Battlegrounds, Honor Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Groups_Guilds_Social_Chat_Channels_Battlegrounds_And_Honor_Guards_ThrowExpectedExceptions()
    {
        Player p1 = MakePlayer(1);
        Player p2 = MakePlayer(2);
        var group = new Group(1);
        group.AddMemberSlot(p1.Guid, p1.Name);
        group.AddMemberSlot(p2.Guid, p2.Name);

        Assert.Throws<ArgumentNullException>(() => { _ = GroupLooterSelection.Next(null!, ObjectGuid.Empty, true, _ => true); });
        Assert.Throws<ArgumentNullException>(() => { _ = GroupLooterSelection.Next(group, ObjectGuid.Empty, true, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = GroupLooterSelection.MasterLooterFallback(null!, _ => true); });
        Assert.Throws<ArgumentNullException>(() => { _ = GroupLooterSelection.MasterLooterFallback(group, null!); });

        var rewardOpts = new GroupRewardOptions();
        Assert.Throws<ArgumentNullException>(() => GroupRewardRange.IsWithinLootXpDist(null!, p2, rewardOpts));
        Assert.Throws<ArgumentNullException>(() => GroupRewardRange.IsWithinLootXpDist(p1, null!, rewardOpts));
        Assert.Throws<ArgumentNullException>(() => GroupRewardRange.IsWithinLootXpDist(p1, p2, null!));
        Assert.Throws<ArgumentNullException>(() => GroupRewardRange.IsAtGroupRewardDistance(null!, p2, rewardOpts));
        Assert.Throws<ArgumentNullException>(() => GroupRewardRange.IsAtGroupRewardDistance(p1, null!, rewardOpts));
        Assert.Throws<ArgumentNullException>(() => GroupRewardRange.IsAtGroupRewardDistance(p1, p2, null!));

        Assert.Throws<ArgumentNullException>(() => new PatternCharterAntispamFilter(null!));
        var filter = new PatternCharterAntispamFilter(["spam"]);
        Assert.Throws<ArgumentNullException>(() => filter.IsSpam(null!));

        Assert.Throws<ArgumentNullException>(() => new EmoteCatalog(null!, []));
        Assert.Throws<ArgumentNullException>(() => new EmoteCatalog([], null!));
        Assert.Throws<ArgumentNullException>(() => ChatChannelCatalog.FromDbc(null!));
        Assert.Throws<ArgumentNullException>(() => ChatChannelCatalog.Builtin.Find(null!));

        Assert.Throws<ArgumentNullException>(() => WhoRules.ZoneFilterShows(null!, 0, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => WhoRules.MatchesSearchStrings(null!, "a", "b", "c"));

        Assert.Throws<ArgumentNullException>(() => new BattlegroundManager(null!, null!));
        Assert.Throws<ArgumentNullException>(() => new BattlegroundManager(new BattlegroundOptions(), null!));

        var avTemplate = new BattlegroundTemplate
        {
            Type = BattlegroundType.AlteracValley,
            MapId = 30,
            Name = "Alterac Valley",
            MinPlayersPerTeam = 20,
            MaxPlayersPerTeam = 40,
            MinLevel = 51,
            MaxLevel = 60,
        };
        Assert.Throws<ArgumentNullException>(() => new WarsongGulch(null!, 0, 1, 1, new BattlegroundOptions(), new BattlegroundPorts()));
        Assert.Throws<ArgumentException>(() => new WarsongGulch(avTemplate, 0, 1, 1, new BattlegroundOptions(), new BattlegroundPorts()));

        var wsgTemplate = new BattlegroundTemplate
        {
            Type = BattlegroundType.WarsongGulch,
            MapId = 489,
            Name = "Warsong Gulch",
            MinPlayersPerTeam = 5,
            MaxPlayersPerTeam = 10,
            MinLevel = 10,
            MaxLevel = 60,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new WarsongGulch(wsgTemplate, -1, 1, 1, new BattlegroundOptions(), new BattlegroundPorts()));

        Assert.Throws<ArgumentNullException>(() => HonorStandings.Generate(null!, 0));
        Assert.Throws<ArgumentException>(() => HonorStandings.Generate([], 0));
        Assert.Throws<ArgumentNullException>(() => HonorStandings.Earning(100f, null!));
        Assert.Throws<ArgumentNullException>(() => HonorMaintenancePlanner.Plan(null!, _ => true, HonorMaintenanceOptions.Retail));
        Assert.Throws<ArgumentNullException>(() => HonorMaintenancePlanner.Plan([], null!, HonorMaintenanceOptions.Retail));
        Assert.Throws<ArgumentNullException>(() => HonorMaintenancePlanner.Plan([], _ => true, null!));
    }

    // -------------------------------------------------------------------------
    // 6. Progression, Quests, Talents, Skills, Reputation Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Progression_Quests_Talents_Skills_And_Reputation_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => KillExperience.Distribute(null!, 10, false, false));

        var stats = new PlayerLevelStats(10, 10, 1, 2, 3, 4, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.Stat(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.Stat(5));
        Assert.Throws<ArgumentNullException>(() => new PlayerLevelStatsTable(null!));
        Assert.Throws<ArgumentException>(() => new PlayerLevelStatsTable([new KeyValuePair<(byte, byte, byte), PlayerLevelStats>((1, 1, 0), stats)]));
        Assert.Throws<ArgumentNullException>(() => PlayerLevelStatsTable.Parse(null!));
        Assert.Throws<FormatException>(() => PlayerLevelStatsTable.Parse(new StringReader("invalid,csv")));
        Assert.Throws<ArgumentException>(() => ProgressionPackets.LevelUpInfo(2, 10, 10, [1, 2, 3]));

        Assert.Throws<ArgumentNullException>(() => QuestExperienceRules.FullXp(null!, 1, QuestXpSource.Derived));
        Assert.Throws<ArgumentNullException>(() => QuestExperienceRules.Xp(null!, 1, 1, QuestXpSource.Derived));

        var talentCat = new TalentCatalog([], []);
        Assert.Throws<ArgumentNullException>(() => new TalentCatalog(null!, []));
        Assert.Throws<ArgumentNullException>(() => new TalentCatalog([], null!));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([new TalentTabRecord(1, 1, 0), new TalentTabRecord(1, 1, 1)], []));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([new TalentTabRecord(1, 1, 0)], [new TalentRecord(1, 1, 0, 0, [100, 0], 0, 0, 0)]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([new TalentTabRecord(1, 1, 0)], [new TalentRecord(1, 99, 0, 0, [100, 0, 0, 0, 0], 0, 0, 0)]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([new TalentTabRecord(1, 1, 0)], [new TalentRecord(1, 1, 11, 0, [100, 0, 0, 0, 0], 0, 0, 0)]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog([new TalentTabRecord(1, 1, 0)], [new TalentRecord(1, 1, 0, 0, [100, 0, 101, 0, 0], 0, 0, 0)]));

        Assert.Throws<ArgumentNullException>(() => TalentRules.CostOfSpell(null!, 1));
        Assert.Throws<ArgumentNullException>(() => TalentRules.UsedPoints(null!, _ => false));
        Assert.Throws<ArgumentNullException>(() => TalentRules.UsedPoints(talentCat, null!));
        Assert.Throws<ArgumentNullException>(() => TalentRules.SpentInTab(null!, 1, _ => false));
        Assert.Throws<ArgumentNullException>(() => TalentRules.SpentInTab(talentCat, 1, null!));
        Assert.Throws<ArgumentNullException>(() => TalentRules.HighestKnownRank(null!, _ => false));
        Assert.Throws<ArgumentNullException>(() => TalentRules.EvaluateLearn(null!, _ => false, 1, 0, 1, 1));
        Assert.Throws<ArgumentNullException>(() => TalentRules.EvaluateLearn(talentCat, null!, 1, 0, 1, 1));

        Assert.Throws<ArgumentNullException>(() => RespecCost.Decay(default, 100, null!));
        Assert.Throws<ArgumentNullException>(() => RespecCost.Copper(0, null!));
        Assert.Throws<ArgumentNullException>(() => RespecCost.AfterRespec(default, 100, null!));

        Assert.Throws<ArgumentNullException>(() => SkillRules.GainChance(1, 100, 50, 25, null!));
        Assert.Throws<ArgumentNullException>(() => SkillRules.GatherChance(SkillIds.Mining, 1, 1, 1, null!));
        Assert.Throws<ArgumentNullException>(() => GatheringRules.CanOpenLock(1, null, 1, 0, false, 0, true, null!));
        Assert.Throws<ArgumentNullException>(() => GatheringRules.OrangeGatherFails(SkillIds.Mining, 1, 1, 300, null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => ReputationMath.FirstPointOf((ReputationRank)99));
        Assert.Throws<ArgumentNullException>(() => ReputationMath.GainBeforeDither(ReputationSource.Kill, 10, 60, 60, null!));
        Assert.Throws<ArgumentNullException>(() => new FactionRecord(1, 0, null!, new uint[4], new int[4], new uint[4]));
        Assert.Throws<ArgumentException>(() => new FactionRecord(1, 0, [1, 2], new uint[4], new int[4], new uint[4]));
        Assert.Throws<ArgumentNullException>(() => new FactionCatalog(null!));
        Assert.Throws<ArgumentException>(() => new FactionCatalog([new FactionRecord(0, 0, new uint[4], new uint[4], new int[4], new uint[4])]));
        Assert.Throws<ArgumentException>(() => new FactionCatalog([new FactionRecord(1, 64, new uint[4], new uint[4], new int[4], new uint[4])]));

        Assert.Throws<ArgumentNullException>(() => new ReputationService(null!));
        var repSvc = new ReputationService(FactionCatalog.Empty);
        Player player = MakePlayer(1);
        Assert.Throws<ArgumentNullException>(() => repSvc.Content = null!);
        Assert.Throws<ArgumentNullException>(() => repSvc.ReplaceOnKill(null!));
        Assert.Throws<ArgumentNullException>(() => repSvc.Create(null!, CharacterReputationData.Empty));
        Assert.Throws<ArgumentNullException>(() => repSvc.Create(player, null!));
        Assert.Throws<ArgumentNullException>(() => repSvc.RewardKill(null!, null!));
        Assert.Throws<ArgumentNullException>(() => repSvc.RewardKill(player, null!));
        Assert.Throws<ArgumentNullException>(() => repSvc.RewardQuest(null!, 1, []));
        Assert.Throws<ArgumentNullException>(() => repSvc.RewardQuest(player, 1, null!));
        Assert.Throws<ArgumentNullException>(() => repSvc.Gain(ReputationSource.Kill, null!, 10, 1, 1));
    }

    // -------------------------------------------------------------------------
    // 7. AntiCheat, WorldState, ClientErrors, and Reload Exception Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void AntiCheat_WorldState_ClientErrors_And_Reload_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new MovementAntiCheat(null!));
        Assert.Throws<ArgumentNullException>(() => new SpeedHackDetector(null!));
        var antiCheat = new MovementAntiCheat(new AntiCheatOptions());
        Assert.Throws<ArgumentNullException>(() => antiCheat.Observe(default, null!));
        Assert.Throws<ArgumentNullException>(() => antiCheat.NotifyTimeSkip(10, 10, null!));
        Assert.Throws<ArgumentNullException>(() => antiCheat.NotifyAcknowledgement(10, 10, null!));
        Assert.Throws<ArgumentNullException>(() => AntiCheatEscalation.Decide(10f, null!));

        var ledger = new AutobanLedger();
        Assert.Throws<ArgumentNullException>(() => ledger.Points(1, 0, null!));
        Assert.Throws<ArgumentNullException>(() => ledger.AddKick(1, 0, null!));

        Player player = MakePlayer(1);
        Assert.Throws<ArgumentNullException>(() => ExploredZonesFields.Read(null!));
        Assert.Throws<ArgumentNullException>(() => ExploredZonesFields.Write(null!, new uint[ExploredZones.WordCount]));
        Assert.Throws<ArgumentException>(() => ExploredZonesFields.Write(player, new uint[ExploredZones.WordCount - 1]));
        Assert.Throws<ArgumentNullException>(() => ExploredZonesFields.Mark(null!, 1));
        Assert.Throws<ArgumentNullException>(() => new ExplorationBaseXpTable(null!));
        Assert.Throws<ArgumentNullException>(() => ExplorationXp.Compute(10, 10, null!, 1f));

        Assert.Throws<ArgumentNullException>(() => WorldStateDefaults.Parse(null!));
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("{not-json"));
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1]]"));
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1, 10], [1, 20]]"));
        Assert.Throws<ArgumentException>(() => WorldStateDefaults.Load(""));

        Assert.Throws<ArgumentNullException>(() => ZoneWeatherChances.FromColumns(1, null!));
        Assert.Throws<ArgumentException>(() => ZoneWeatherChances.FromColumns(1, [1, 2, 3]));
        var weatherState = new WeatherState(1, null);
        Assert.Throws<ArgumentNullException>(() => weatherState.ReGenerate(WeatherSeason.Spring, null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientErrorRateLimiter(0, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientErrorRateLimiter(1, 0));

        var tx = new ReloadTransaction();
        Assert.Throws<ArgumentException>(() => tx.Step("", () => { }, () => { }));
        Assert.Throws<ArgumentNullException>(() => tx.Step("step", null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => tx.Step("step", () => { }, null!));
        Assert.Throws<ArgumentException>(() => tx.Note(""));
    }

    // -------------------------------------------------------------------------
    // 8. Deterministic Seeded Fuzz Tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Economy_AuctionHouse_Mail_And_TradeRules_Fuzz_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        var economyOpts = new EconomyOptions();
        var templates = new Dictionary<uint, ItemTemplate>();
        for (uint id = 1; id <= 30; id++)
        {
            templates[id] = new ItemTemplate
            {
                Entry = id,
                Name = $"Item_{id}_Sword_Of_Power",
                Class = (uint)(id % 5),
                SubClass = (uint)(id % 3),
                Quality = (uint)(id % 6),
                InventoryType = (uint)((id % 20) + 1),
                RequiredLevel = (id * 2) % 61,
                SellPrice = (id * 150) % 50_000,
                BuyPrice = (id * 600) % 200_000,
                Stackable = (id % 5 == 0) ? 20u : 1u,
            }.Normalized();
        }

        var auctions = new List<AuctionRecord>();
        for (int i = 0; i < 400; i++)
        {
            uint houseId = (uint)rng.Next(1, 8);
            var house = new AuctionHouseEntry(houseId, houseId == 7 ? 75u : 15u, houseId == 7 ? 15u : 5u);
            uint sellPrice = (uint)rng.Next(0, 5_000_000);
            uint count = (uint)rng.Next(0, 250);
            uint durationMin = (uint)rng.Next(0, 4320);
            float rate = (float)(rng.NextDouble() * 3.0);

            uint deposit = AuctionHouseRules.Deposit(house, sellPrice, count, durationMin, 0, rate);
            uint bid = (uint)rng.Next(1, 50_000_000);
            uint cut = AuctionHouseRules.Cut(house, bid, rate);
            Assert.True(cut <= bid);

            uint proceeds = AuctionHouseRules.Proceeds(bid, deposit, cut);
            ulong expectedRaw = (ulong)bid + deposit;
            ulong expectedProceeds = expectedRaw > cut ? expectedRaw - cut : 0;
            Assert.Equal((uint)Math.Min(expectedProceeds, EconomyOptions.MaxMoney), proceeds);

            uint outBid = AuctionHouseRules.OutBid(bid);
            Assert.True(outBid >= 1);

            int bidderId = rng.Next(0, 2) == 0 ? 0 : rng.Next(1, 50);
            uint currentBid = bidderId == 0 ? 0u : bid;
            var listing = new AuctionRecord
            {
                Id = (uint)(i + 1),
                HouseId = house.Id,
                ItemGuid = (uint)(i + 100),
                ItemEntry = (uint)rng.Next(1, 31),
                ItemCount = Math.Max(1u, count),
                SellerId = rng.Next(1, 100),
                StartBid = bid,
                Buyout = bid + (uint)rng.Next(0, 100_000),
                ExpireTime = 2000,
                BidderId = bidderId,
                Bid = currentBid,
                Deposit = deposit,
            };
            auctions.Add(listing);

            uint minBid = AuctionHouseRules.MinimumBid(listing);
            Assert.True(minBid >= listing.StartBid);

            var action = (AuctionMailAction)rng.Next(0, 6);
            string subject = MailRules.AuctionSubject(listing.ItemEntry, action);
            Assert.False(string.IsNullOrEmpty(subject));

            MailRecord letter = MailRules.AuctionLetter(
                (uint)(i + 1),
                house.Id,
                listing.SellerId,
                subject,
                1000,
                economyOpts,
                proceeds,
                listing.ItemGuid,
                listing.ItemEntry);
            Assert.Equal(subject, letter.Subject);
            Assert.True(letter.ExpireTime > letter.DeliverTime);
        }

        for (int q = 0; q < 60; q++)
        {
            var query = new AuctionQuery(
                (uint)rng.Next(0, 80),
                rng.Next(0, 3) == 0 ? "" : "Sword",
                (byte)rng.Next(0, 30),
                (byte)rng.Next(0, 70),
                rng.Next(0, 2) == 0 ? AuctionSearch.Any : (uint)rng.Next(1, 21),
                rng.Next(0, 2) == 0 ? AuctionSearch.Any : (uint)rng.Next(0, 5),
                rng.Next(0, 2) == 0 ? AuctionSearch.Any : (uint)rng.Next(0, 3),
                rng.Next(0, 2) == 0 ? AuctionSearch.Any : (uint)rng.Next(0, 6),
                rng.Next(0, 2) == 1);

            (IReadOnlyList<AuctionRecord> page, int total) = AuctionSearch.Run(
                auctions,
                query,
                id => templates.GetValueOrDefault(id),
                t => t.RequiredLevel <= 60);

            Assert.True(page.Count <= AuctionHouseRules.PageSize);
            Assert.True(total >= page.Count);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Honor_KillPoints_Standings_Ranks_And_Maintenance_Fuzz_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < 400; i++)
        {
            uint killerLevel = (uint)rng.Next(1, 65);
            uint victimLevel = (uint)rng.Next(1, 65);
            sbyte victimVisualRank = (sbyte)rng.Next(-4, 15);
            uint totalKills = (uint)rng.Next(0, 20);
            uint groupSize = (uint)rng.Next(1, 6);

            float hkPoints = HonorKillPoints.Honorable(killerLevel, victimLevel, victimVisualRank, totalKills, groupSize);
            Assert.True(float.IsFinite(hkPoints));
            Assert.True(hkPoints >= 0f);
            if (totalKills >= 10)
            {
                Assert.Equal(0f, hkPoints);
            }

            float dkPoints = HonorKillPoints.Dishonorable((byte)rng.Next(1, 65));
            Assert.InRange(dkPoints, 10f, 100f);

            float rpInput = (float)((rng.NextDouble() * 80000.0) - 20000.0);
            HonorRankInfo calc = HonorRanks.Calculate(rpInput);
            Assert.InRange((int)calc.VisualRank, -4, HonorRanks.PositiveRankCount);
            Assert.InRange((int)calc.Rank, 0, HonorRanks.RankCount - 1);

            byte bar = HonorRanks.RankBar(rpInput, calc);
            Assert.InRange(bar, 0, 255);
        }

        for (int batch = 0; batch < 30; batch++)
        {
            int count = rng.Next(0, 80);
            var list = new List<HonorWeeklyScore>(count);
            for (int j = 0; j < count; j++)
            {
                int charId = j + 1;
                byte level = (byte)rng.Next(10, 61);
                int accountId = (j % 10 == 0) ? 0 : j + 100;
                byte race = (byte)((j % 2 == 0) ? 1 : 2);
                float oldRp = rng.Next(0, 5) == 0 ? 0f : (float)(rng.NextDouble() * 65000.0);
                byte highestRank = (byte)rng.Next(0, HonorRanks.RankCount);
                uint hk = (uint)rng.Next(0, 200);
                uint dk = (uint)rng.Next(0, 5);
                float cp = (float)(rng.NextDouble() * 50000.0);
                list.Add(new HonorWeeklyScore(charId, level, accountId, race, oldRp, highestRank, hk, dk, cp));
            }

            var opts = new HonorMaintenanceOptions(
                0.2f,
                (uint)rng.Next(0, 20),
                (uint)rng.Next(0, 50));

            HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan(
                list,
                race => race == 1,
                opts);

            Assert.NotNull(plan.Updates);
            Assert.Equal(list.Count, plan.Updates.Count);
            Assert.Equal(list.Count, plan.AllianceCount + plan.HordeCount + plan.InactiveCount);

            foreach (HonorRankUpdate item in plan.Updates)
            {
                Assert.True(float.IsFinite(item.RankPoints));
                Assert.InRange(item.RankPoints, 0f, 65000f);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Progression_Talents_And_Skills_Fuzz_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);

        var tabs = new[]
        {
            new TalentTabRecord(1, 1, 0),
            new TalentTabRecord(2, 1, 1),
        };
        var talents = new[]
        {
            new TalentRecord(10, 1, 0, 0, [1001, 1002, 1003, 1004, 1005], 0, 0, 0),
            new TalentRecord(11, 1, 1, 0, [1011, 1012, 1013, 0, 0], 10, 2, 0),
            new TalentRecord(20, 2, 0, 1, [2001, 2002, 0, 0, 0], 0, 0, 0),
        };
        var catalog = new TalentCatalog(tabs, talents);
        var respecOpts = new TalentOptions();
        var skillOpts = new SkillOptions();

        for (int i = 0; i < 400; i++)
        {
            uint pl = (uint)rng.Next(1, 70);
            uint tl = (uint)rng.Next(1, 70);
            bool elite = rng.Next(0, 2) == 1;

            uint zd = ExperienceFormulas.ZeroDifference(pl);
            Assert.True(zd >= 5);

            float mobXp = ExperienceFormulas.BaseGain(pl, tl);
            Assert.True(mobXp >= 0f);
            if (mobXp > 0f)
            {
                uint scaled = ExperienceFormulas.KillGain(pl, tl, elite, rng.Next(0, 2) == 1, (float)(rng.NextDouble() * 3.0));
                Assert.True(scaled <= (uint)(mobXp * 8f) + 10u);
            }

            if (pl <= PlayerXpTable.MaxTableLevel)
            {
                uint nextXp = PlayerXpTable.XpForLevel(pl);
                Assert.True(nextXp > 0);
            }

            int level = rng.Next(1, 65);
            uint totalTalentPts = TalentRules.PointsForLevel(level, 1.0);
            Assert.Equal(level < 10 ? 0u : (uint)(level - 9), totalTalentPts);

            var knownSpells = new HashSet<uint>();
            if (rng.Next(0, 2) == 1)
            {
                knownSpells.Add(1001);
            }
            if (rng.Next(0, 2) == 1)
            {
                knownSpells.Add(1003);
            }

            uint usedPts = TalentRules.UsedPoints(catalog, knownSpells.Contains);
            Assert.True(usedPts <= 10);

            TalentLearnResult learnPlan = TalentRules.EvaluateLearn(
                catalog,
                knownSpells.Contains,
                (uint)(rng.Next(0, 3) switch { 0 => 10, 1 => 11, _ => 99 }),
                (uint)rng.Next(0, 6),
                (uint)rng.Next(0, 3),
                (uint)rng.Next(0, 5));
            if (learnPlan.Outcome == TalentLearnOutcome.Ok)
            {
                Assert.True(learnPlan.RankSpell != 0);
            }

            var respecState = new RespecState((uint)rng.Next(0, 20), rng.NextInt64(0, 2_000_000_000L));
            RespecState decayed = RespecCost.Decay(respecState, respecState.TimeUnix + rng.NextInt64(-1000, 100_000_000L), respecOpts);
            uint respecCopper = RespecCost.Copper(decayed.Multiplier, respecOpts);
            Assert.InRange(
                respecCopper,
                respecOpts.RespecBaseCostGold * 10000u,
                respecOpts.RespecMultiplicativeCostGold * respecOpts.RespecMaxMultiplier * 10000u);

            int gainChance = SkillRules.GainChance(
                (uint)rng.Next(1, 61),
                (uint)rng.Next(1, 301),
                (uint)rng.Next(0, 350),
                (uint)rng.Next(0, 300),
                skillOpts);
            Assert.InRange(gainChance, 0, 1000);

            uint skillId = rng.Next(0, 3) switch { 0 => SkillIds.Mining, 1 => SkillIds.Herbalism, _ => SkillIds.Skinning };
            uint multiplicator = (uint)rng.Next(0, 5);
            int? gatherChance = SkillRules.GatherChance(skillId, (uint)rng.Next(0, 400), (uint)rng.Next(0, 400), multiplicator, skillOpts);
            if (gatherChance is { } gc)
            {
                Assert.InRange(gc, 0, (int)(1000 * multiplicator));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void ReputationMath_And_ReputationService_Fuzz_PreserveStandingBounds(int seed)
    {
        var rng = new Random(seed);
        var faction0 = new FactionRecord(10, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], 0, "RootFaction");
        var faction1 = new FactionRecord(11, 1, [0, 0, 0, 0], [0, 0, 0, 0], [3000, 0, 0, 0], [0, 0, 0, 0], 10, "ChildFaction");
        var catalog = new FactionCatalog([faction0, faction1]);
        var repSvc = new ReputationService(catalog);
        Player player = MakePlayer(1);
        PlayerReputation rep = repSvc.Create(player, CharacterReputationData.Empty);
        repSvc.Track(player, rep);

        for (int i = 0; i < 400; i++)
        {
            int standing = rng.Next(ReputationMath.Bottom - 20_000, ReputationMath.Cap + 20_000);
            int clamped = ReputationMath.Clamp(standing);
            Assert.InRange(clamped, ReputationMath.Bottom, ReputationMath.Cap);

            ReputationRank rank = ReputationMath.ToRank(standing);
            Assert.InRange((int)rank, (int)ReputationRank.Hated, (int)ReputationRank.Exalted);

            int firstPt = ReputationMath.FirstPointOf(rank);
            Assert.True(clamped >= firstPt);

            uint discounted = ReputationPricing.Round((ulong)rng.Next(1, 100_000), rank >= ReputationRank.Honored ? 0.9f : 1.0f);
            Assert.True(discounted >= 1);

            int baseDelta = rng.Next(-5000, 5000);
            var source = (ReputationSource)rng.Next(0, 3);
            uint factionId = (uint)(rng.Next(0, 2) == 0 ? 10 : 11);
            int gain = repSvc.Gain(source, player, baseDelta, factionId, (uint)rng.Next(1, 61));
            repSvc.ModifyReputation(player, factionId, gain);

            Assert.InRange(repSvc.GetReputation(player, 10), ReputationMath.Bottom, ReputationMath.Cap);
            Assert.InRange(repSvc.GetReputation(player, 11), ReputationMath.Bottom, ReputationMath.Cap);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void AntiCheat_Movement_SpeedHack_Scores_And_AutobanLedger_Fuzz_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        var opts = new AntiCheatOptions
        {
            Enabled = true,
            Action = AntiCheatAction.Kick,
            ScoreGmAlert = 20f,
            ScoreRubberband = 40f,
            ScoreKick = 80f,
            DecayPerSecond = 2f,
        };
        var autobanOpts = new AntiCheatAutobanOptions
        {
            Enabled = true,
            KickPoints = 10f,
            Threshold = 25f,
            DecayPerHour = 1f,
        };

        var detector = new MovementAntiCheat(opts);
        var scores = new AntiCheatScores();
        var ledger = new AutobanLedger();
        var findings = new List<AntiCheatFinding>();
        uint timeMs = 1000;
        float x = 0f, y = 0f, z = 0f;

        for (int i = 0; i < 300; i++)
        {
            uint dt = (uint)rng.Next(0, 2000);
            timeMs += dt;
            x += (float)((rng.NextDouble() - 0.3) * 40.0);
            y += (float)((rng.NextDouble() - 0.3) * 40.0);
            z += (float)((rng.NextDouble() - 0.5) * 15.0);

            var move = new MovementInfo
            {
                Flags = (MovementFlags)rng.Next(0, 0x4000),
                Time = timeMs,
                X = x,
                Y = y,
                Z = z,
                FallTime = (uint)rng.Next(0, 5000),
            };

            var sample = new MovementSample
            {
                Opcode = WorldOpcode.MsgMoveHeartbeat,
                Movement = move,
                ReceivedMs = timeMs,
                LatencyMs = 50,
                AllowedSpeed = 7.0f,
                Alive = true,
                Rooted = rng.Next(0, 10) == 0,
            };

            findings.Clear();
            detector.Observe(in sample, findings);
            foreach (AntiCheatFinding finding in findings)
            {
                scores.Add(1, finding.Weight, timeMs, opts.DecayPerSecond);
            }

            float currentScore = scores.Score(1, timeMs, opts.DecayPerSecond);
            Assert.True(float.IsFinite(currentScore));
            Assert.True(currentScore >= 0f);

            AntiCheatAction decision = AntiCheatEscalation.Decide(currentScore, opts);
            if (decision == AntiCheatAction.Kick)
            {
                bool autoBanned = ledger.AddKick(1, timeMs, autobanOpts);
                float pts = ledger.Points(1, timeMs, autobanOpts);
                if (autoBanned)
                {
                    Assert.Equal(0f, pts);
                }
                else
                {
                    Assert.True(pts > 0f && pts < autobanOpts.Threshold);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void LootGenerator_And_PlayerInventory_Fuzz_NeverCorruptState(int seed)
    {
        var rng = new Random(seed);
        var rows = new List<(LootTableKind Kind, LootStoreRow Row)>
        {
            (LootTableKind.Creature, new LootStoreRow(1, 101, 50f, 0, 1, 3)),
            (LootTableKind.Creature, new LootStoreRow(1, 102, -35f, 0, 1, 2)),
            (LootTableKind.Creature, new LootStoreRow(1, 103, 40f, 1, 1, 1)),
            (LootTableKind.Creature, new LootStoreRow(1, 104, 60f, 1, 1, 1)),
            (LootTableKind.Creature, new LootStoreRow(1, 0, 100f, 0, -10, 2)),
            (LootTableKind.Reference, new LootStoreRow(10, 105, 75f, 0, 1, 4)),
        };

        var lootContent = new LootContent(rows, [new CreatureLootInfo(1, 1, 0, 10, 100)]);
        var generator = new LootGenerator(lootContent, rng);

        var templates = new ItemTemplateStore([
            new ItemTemplate { Entry = 101, Name = "Linen", Stackable = 20, InventoryType = (uint)InventoryType.NonEquip }.Normalized(),
            new ItemTemplate { Entry = 102, Name = "QuestBone", Stackable = 10, InventoryType = (uint)InventoryType.NonEquip }.Normalized(),
            new ItemTemplate { Entry = 103, Name = "Helm", Stackable = 1, InventoryType = (uint)InventoryType.Head }.Normalized(),
            new ItemTemplate { Entry = 104, Name = "Boots", Stackable = 1, InventoryType = (uint)InventoryType.Feet }.Normalized(),
            new ItemTemplate { Entry = 105, Name = "Potion", Stackable = 5, InventoryType = (uint)InventoryType.NonEquip }.Normalized(),
        ]);

        Player player = MakePlayer(1);
        player.Inventory.Templates = templates;
        player.Inventory.GuidAllocator = new ItemGuidAllocator(1000);

        for (int i = 0; i < 150; i++)
        {
            List<RolledLoot> drops = generator.Roll(LootTableKind.Creature, 1);
            foreach (RolledLoot drop in drops)
            {
                Assert.InRange(drop.Count, 1u, 255u);
                player.Inventory.AddItem(drop.ItemId, drop.Count, out _);
            }

            if (i % 10 == 0)
            {
                byte slotA = (byte)rng.Next(InventorySlots.ItemStart, InventorySlots.ItemEnd);
                byte slotB = (byte)rng.Next(InventorySlots.ItemStart, InventorySlots.ItemEnd);
                player.Inventory.SwapItem(InventorySlots.Bag0, slotA, InventorySlots.Bag0, slotB);
            }

            if (i % 15 == 0)
            {
                player.Inventory.DestroyItemCount(101, (uint)rng.Next(1, 10));
            }
        }

        InventorySnapshot snapshot = player.Inventory.CreateSnapshot();
        Assert.NotNull(snapshot);
        foreach (Item item in player.Inventory.AllItems)
        {
            Assert.True(item.Count >= 1 && item.Count <= item.Template.Stackable);
        }
    }
}
