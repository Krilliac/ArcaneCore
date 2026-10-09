using ArcaneCore.Game;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>The condition feature in the daemon: table loading, configuration, and the hand-over to the NPC services.</summary>
public sealed class ConditionFeatureTests
{
    private const uint LevelFive = 1;
    private const uint EventSeven = 2;
    private const uint Script = 3;
    private const uint Holiday = 4;

    private sealed class MemoryConditionStore : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>(
            [
                new(LevelFive, (int)ConditionType.Level, 5, 1, 0, 0, 0),
                new(EventSeven, (int)ConditionType.ActiveGameEvent, 7, 0, 0, 0, 0),
                new(Script, (int)ConditionType.WorldScript, 0, 0, 0, 0, 0),
                new(Holiday, (int)ConditionType.ActiveHoliday, 141, 0, 0, 0, 0),
                new(9, (int)ConditionType.Not, 99, 0, 0, 0, 0),    // invalid: dropped and reported
            ]);
    }

    private sealed class MapConditionStore : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>(
            [
                new(700001, 42, UldamanInstance.VariableSpawnAnnora, 1, 1, 0, 0),
                new(5310010, 42, 4823, 1, 0, 0, 0),
            ]);
    }

    private static ServiceProvider Services(bool withStore, params (string Key, string Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());
        if (withStore)
        {
            services.AddSingleton<IConditionContentStore, MemoryConditionStore>();
        }

        services.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConditionFeature>.Instance));
        services.AddSingleton(sp => new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance));
        return services.BuildServiceProvider();
    }

    private static WorldRuntime NewWorld(IServiceProvider services)
        => new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);

    private static ServiceProvider MapServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConditionContentStore, MapConditionStore>();
        services.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConditionFeature>.Instance));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void FeatureIsDiscoveredAsAWorldFeature()
    {
        Assert.Contains(typeof(ConditionFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
    }

    [Fact]
    public void AttachLoadsTheTable_RejectsInvalidRows_AndAppliesConfiguredEvents()
    {
        using ServiceProvider services = Services(true, ("Conditions:ActiveGameEvents:0", "7"));
        using WorldRuntime world = NewWorld(services);
        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        Assert.Equal(0, feature.Current.Table.Count);   // before Attach: the empty table

        feature.Attach(world);

        Assert.Equal(4, feature.Current.Table.Count);
        Assert.Equal(9u, Assert.Single(feature.Current.Table.Rejected).Entry);
        Player player = CreatePlayer(level: 5);
        Assert.True(feature.IsSatisfied(LevelFive, player, null));
        Assert.False(feature.IsSatisfied(LevelFive, CreatePlayer(level: 4), null));
        Assert.True(feature.IsSatisfied(EventSeven, player, null));    // configured active
        Assert.False(feature.IsSatisfied(Holiday, player, null));      // default: no holiday active
        Assert.False(feature.IsSatisfied(Script, player, null));       // no world-script producer yet: unknown, hidden
        Assert.Equal(1, feature.Current.Unavailable[(int)ConditionType.WorldScript]);
        ConditionRuntimeState.For(world).SetWorldScriptCondition(0, 0, true);
        Assert.True(feature.IsSatisfied(Script, player, null));
    }

    [Fact]
    public void WithoutAStore_TheTableIsEmpty_AndEveryConditionIsUnsatisfied()
    {
        using ServiceProvider services = Services(false);
        using WorldRuntime world = NewWorld(services);
        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        feature.Attach(world);
        Assert.Equal(0, feature.Current.Table.Count);
        Assert.False(feature.IsSatisfied(LevelFive, CreatePlayer(level: 60), null));
    }

    [Fact]
    public void WorldStateConditionReadsThePlayersMapVariable()
    {
        using ServiceProvider services = Services(false);
        using WorldRuntime world = NewWorld(services);
        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        feature.Attach(world);
        Player player = CreatePlayer(level: 1);
        world.AddPlayer(player);
        var runtime = ConditionRuntimeState.For(world);
        runtime.SetMapVariable(player.Map!, 4811, -2);
        var table = ConditionTable.Build([new ConditionRecord(1, 42, 4811, 1, unchecked((uint)-2), 0, 0)]);
        var evaluator = new ConditionEvaluator(table, feature.Current.Context);

        Assert.True(evaluator.IsSatisfied(1, player, null));
        runtime.SetMapVariable(player.Map!, 4811, 0);
        Assert.False(evaluator.IsSatisfied(1, player, null));
    }

    [Fact]
    public void SpawnGroupWorldStateReadsTheOwningMapAndUldamanInstanceVariable()
    {
        using ServiceProvider services = MapServices();
        using WorldRuntime world = NewWorld(services);
        Map uldamanMap = world.GetMap(UldamanInstance.MapId);
        var uldaman = new UldamanInstance(uldamanMap);
        uldamanMap.AddUpdater(uldaman);
        uldaman.Initialize();
        var uldamanCreatures = new CreatureMapSystem(uldamanMap, new CreatureContent([], [], [], [], []));
        uldamanMap.AddUpdater(uldamanCreatures);
        Map aqMap = world.GetMap(531);
        var aqCreatures = new CreatureMapSystem(aqMap, new CreatureContent([], [], [], [], []));
        aqMap.AddUpdater(aqCreatures);

        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        feature.Attach(world);
        world.RunTick(50);
        var annoraGroup = new SpawnGroupDefinition
        {
            Id = 7000001, Type = SpawnGroupType.Creature, WorldStateCondition = 700001,
        };
        var sarturaTrash = new SpawnGroupDefinition
        {
            Id = 5310014, Type = SpawnGroupType.Creature, WorldStateCondition = 5310010,
        };

        Assert.False(uldamanCreatures.SpawnGroupCondition!(annoraGroup));
        Assert.True(aqCreatures.SpawnGroupCondition!(sarturaTrash));
        Assert.False(aqCreatures.SpawnGroupCondition!(annoraGroup));
        uldaman.SetVariable(UldamanInstance.VariableSpawnAnnora, 1);
        Assert.True(uldamanCreatures.SpawnGroupCondition!(annoraGroup));
    }

    [Fact]
    public void RuinsBossCompletionChangesTheSixContentMapVariableConditions_AndSurvivesLoad()
    {
        using ServiceProvider services = Services(false);
        using WorldRuntime world = NewWorld(services);
        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        feature.Attach(world);
        Map map = world.GetMap(509);
        var raid = new RuinsOfAhnQirajInstance(map);
        map.AddUpdater(raid);
        Player player = CreatePlayer(1, mapId: 509);
        world.AddPlayer(player);
        uint[] variables = [4811, 2174, 4812, 4813, 4814, 4815];
        var table = ConditionTable.Build(variables.Select((id, slot) =>
            new ConditionRecord((uint)(6500 + slot), 42, id, 1, 0, 0, 0)));
        var evaluator = new ConditionEvaluator(table, feature.Current.Context);

        for (uint slot = 0; slot < variables.Length; slot++)
            Assert.True(evaluator.IsSatisfied(6500 + slot, player, null));
        for (uint slot = 0; slot < variables.Length; slot++)
            raid.SetData(slot, EncounterState.Done);
        for (uint slot = 0; slot < variables.Length; slot++)
            Assert.False(evaluator.IsSatisfied(6500 + slot, player, null));

        string saved = raid.GetSaveData()!;
        raid.Initialize();
        for (uint slot = 0; slot < variables.Length; slot++)
            Assert.True(evaluator.IsSatisfied(6500 + slot, player, null));
        raid.Load(saved);
        for (uint slot = 0; slot < variables.Length; slot++)
            Assert.False(evaluator.IsSatisfied(6500 + slot, player, null));
    }

    [Fact]
    public void TempleEncounterConditionsReadTwinsAndOuroFromTheInstanceSave()
    {
        using ServiceProvider services = Services(false);
        using WorldRuntime world = NewWorld(services);
        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        feature.Attach(world);
        Map map = world.GetMap(531);
        var raid = new TempleOfAhnQirajInstance(map);
        map.AddUpdater(raid);
        Player player = CreatePlayer(1, mapId: 531);
        world.AddPlayer(player);
        var table = ConditionTable.Build(
        [
            new ConditionRecord(717, 31, 715, 0, 0, 0, 0),
            new ConditionRecord(718, 31, 716, 0, 0, 0, 0),
        ]);
        var evaluator = new ConditionEvaluator(table, feature.Current.Context);

        Assert.False(evaluator.IsSatisfied(717, player, null));
        Assert.False(evaluator.IsSatisfied(718, player, null));
        raid.SetData(TempleOfAhnQirajInstance.Twins, EncounterState.Done);
        Assert.True(evaluator.IsSatisfied(717, player, null));
        Assert.False(evaluator.IsSatisfied(718, player, null));
        raid.SetData(TempleOfAhnQirajInstance.Ouro, EncounterState.Done);
        string saved = raid.GetSaveData()!;
        raid.Initialize();
        Assert.False(evaluator.IsSatisfied(717, player, null));
        raid.Load(saved);
        Assert.True(evaluator.IsSatisfied(717, player, null));
        Assert.True(evaluator.IsSatisfied(718, player, null));
    }

    [Fact]
    public void TheNpcServicesPickUpTheFeature_AsTheirConditionEvaluator()
    {
        using ServiceProvider services = Services(true);
        NpcServicesFeature npcs = services.GetRequiredService<NpcServicesFeature>();
        QuestNpcDependencies extended = npcs.Extend(new QuestNpcDependencies(), NpcStore.Empty);
        Assert.Same(services.GetRequiredService<ConditionFeature>(), extended.Conditions);

        // A collaborator another feature supplied is kept.
        var other = new ConditionEvaluator(ConditionTable.Empty, new ConditionContext());
        Assert.Same(other, npcs.Extend(new QuestNpcDependencies(Conditions: other), NpcStore.Empty).Conditions);
    }

    [Fact]
    public void AServicesRebuildBeforeOrAfterAttach_SeesTheLoadedTable()
    {
        using ServiceProvider services = Services(true);
        using WorldRuntime world = NewWorld(services);
        NpcServicesFeature npcs = services.GetRequiredService<NpcServicesFeature>();
        IConditionEvaluator early = npcs.Extend(new QuestNpcDependencies(), NpcStore.Empty).Conditions!;
        Player player = CreatePlayer(level: 5);
        Assert.False(early.IsSatisfied(LevelFive, player, null));

        services.GetRequiredService<ConditionFeature>().Attach(world);

        Assert.True(early.IsSatisfied(LevelFive, player, null));
    }

    private static Player CreatePlayer(byte level, uint mapId = 0)
    {
        var character = new CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Cond", Race = 1, Class = 1, Gender = 0, Level = level,
            MapId = mapId, ZoneId = 12, X = 0, Y = 0, Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new NullSession());
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;

        public ArcaneCore.Kernel.Accounts.AccountSecurity Security => ArcaneCore.Kernel.Accounts.AccountSecurity.Player;

        public void Send(ArcaneCore.Protocol.WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }
}
