using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The <c>progression-*</c> scenarios on the scenario test world (manual clock, SQLite characters, the synthetic content):
/// bots left to their own brain spend talent points, wear upgrades, repair and quest, all through the ordinary handlers.
/// Extra content (a warrior talent catalog, items, a repair NPC and its prices) is registered through
/// <see cref="ScenarioTestWorld.StartAsync"/>'s configure hook, over the shared content.
/// </summary>
public sealed class BotProgressionScenarioTests
{
    private const uint Ring = 993001;
    private const uint Sword = 993002;
    private const uint Boots = 993003;
    private const uint RepairNpc = 993010;

    /// <summary>
    /// A warrior catalog that holds the first talent of each warrior build (Arms page 0 row 0 column 0, Fury page 1 row 0
    /// column 2, Protection page 2 row 0 column 1), so whichever build the bot's id picks starts with a real talent.
    /// </summary>
    internal static TalentCatalog WarriorCatalog() => new(
        [new TalentTabRecord(161, 1, 0), new TalentTabRecord(164, 1, 1), new TalentTabRecord(163, 1, 2)],
        [
            new TalentRecord(124, 161, 0, 0, [993101, 993102, 993103, 0, 0], 0, 0, 0),
            new TalentRecord(157, 164, 0, 2, [993111, 993112, 993113, 993114, 993115], 0, 0, 0),
            new TalentRecord(1601, 163, 0, 1, [993121, 993122, 993123, 993124, 993125], 0, 0, 0),
        ]);

    [Fact]
    public async Task ProgressionTalents_ALevelTwelveWarriorSpendsThreePointsByItself_AndKeepsThemOverARelog()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services => services.AddSingleton(WarriorCatalog()));
        ScenarioReport report = await world.RunPassingAsync(new ProgressionTalentsScenario());
        Assert.Contains(report.Steps, step => step.Name == "all three points are spent by the bot" && step.Passed);
        Assert.Contains(report.Steps, step => step.Name == "the talents survive the relog" && step.Passed);
    }

    [Fact]
    public async Task ProgressionTalents_WithoutACatalog_FailsAtANamedStep()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        ScenarioReport report = await world.RunAsync(new ProgressionTalentsScenario());
        Assert.False(report.Passed);
        Assert.Equal("a talent catalog is loaded", report.FailedStep);
    }

    [Fact]
    public async Task ProgressionGear_AWarriorWearsTheBetterWeaponAndRingFromItsBags()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services => AddItems(services,
            new ItemTemplate { Entry = Ring, Name = "Scenario band", Class = (uint)ItemClass.Armor, InventoryType = (uint)InventoryType.Finger,
                Stats = [new ItemStat((uint)ItemStatType.Strength, 4)], Stackable = 1 },
            new ItemTemplate { Entry = Sword, Name = "Scenario blade", Class = (uint)ItemClass.Weapon, SubClass = 7,
                InventoryType = (uint)InventoryType.Weapon, Delay = 2000, Damages = [new ItemDamage(6, 10, 0)], Stackable = 1 }));
        await world.RunPassingAsync(new ProgressionGearScenario([Ring, Sword], [Ring, Sword]));
    }

    [Fact]
    public async Task ProgressionRepair_AWarriorWithBrokenArmorRepairsAtARepairNpc()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            AddItems(services, new ItemTemplate
            {
                Entry = Boots, Name = "Scenario boots", Class = (uint)ItemClass.Armor, InventoryType = (uint)InventoryType.Feet,
                Armor = 20, MaxDurability = 30, ItemLevel = 10, Quality = 1, Stackable = 1,
            });
            AddCreatures(services, new CreatureTemplate
            {
                Entry = RepairNpc, Name = "Scenario armorer", Faction = 12, NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Repair),
                DisplayIds = [49], MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
            }, new CreatureSpawn { Guid = RepairNpc, Entry = RepairNpc, MapId = 0, X = StartX - 3f, Y = StartY + 3f, Z = StartZ });
            // DurabilityCosts row for item level 10 (multiplier 2 everywhere), DurabilityQuality factor 1 for quality 1.
            services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(2u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
        });
        await world.RunPassingAsync(new ProgressionRepairScenario(RepairNpc, Boots));
    }

    /// <summary>
    /// Under the default Quests configuration (RewardMode AllSupported, no allowlist) a fresh bot takes the kill quest from
    /// the marshal, travels to the kobold, kills it and is rewarded: before the rewardable predicate the bot never accepted
    /// a quest on a default server. The world gets a flat floor and straight paths, the shape the navigation tests use.
    /// </summary>
    [Fact]
    public async Task ProgressionQuest_AFreshBotCompletesTheKillQuestUnderTheDefaultQuestConfig()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        QuestNpcFeature quests = world.Services.GetRequiredService<QuestNpcFeature>();
        quests.Options.OrdinaryRewardQuestIds = [];
        Assert.Equal(QuestRewardMode.AllSupported, quests.Options.RewardMode);
        await world.Host.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            return true;
        });
        ScenarioReport report = await world.RunPassingAsync(new ProgressionQuestScenario(KillQuest, GiverEntry));
        Assert.Contains(report.Steps, step => step.Name == "the bot completes and turns it in" && step.Passed);
    }

    /// <summary>Register more item templates over the scenario world's own (a later registration replaces the store).</summary>
    private static void AddItems(IServiceCollection services, params ItemTemplate[] extra)
    {
        IItemTemplateSource inner = (IItemTemplateSource)services.Last(d => d.ServiceType == typeof(IItemTemplateSource)).ImplementationInstance!;
        services.AddSingleton<IItemTemplateSource>(new MoreItems(inner, extra));
    }

    private static void AddCreatures(IServiceCollection services, CreatureTemplate template, CreatureSpawn spawn)
    {
        ICreatureDataStore inner = (ICreatureDataStore)services.Last(d => d.ServiceType == typeof(ICreatureDataStore)).ImplementationInstance!;
        services.AddSingleton<ICreatureDataStore>(new MoreCreatures(inner, template, spawn));
    }

    private sealed class MoreItems(IItemTemplateSource inner, IReadOnlyList<ItemTemplate> extra) : IItemTemplateSource
    {
        public async Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default)
            => [.. await inner.LoadTemplatesAsync(cancellationToken), .. extra.Select(t => t with { AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue })];

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => inner.LoadStartingItemsAsync(cancellationToken);
    }

    private sealed class MoreCreatures(ICreatureDataStore inner, CreatureTemplate template, CreatureSpawn spawn) : ICreatureDataStore
    {
        public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureContent content = await inner.LoadAsync(cancellationToken);
            return new CreatureContent([.. content.Templates, template],
                [.. content.MapsWithSpawns.SelectMany(map => content.GetSpawns(map)), spawn], [], [], []);
        }
    }
}
