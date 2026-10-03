using ArcaneCore.Game;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
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
        Assert.False(feature.IsSatisfied(Script, player, null));       // unsupported: hidden
        Assert.Equal(1, feature.Current.Summarize().UnavailableByType[(int)ConditionType.WorldScript]);
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

    private static Player CreatePlayer(byte level)
    {
        var character = new CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Cond", Race = 1, Class = 1, Gender = 0, Level = level,
            MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = 83.5f,
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
