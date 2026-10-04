using ArcaneCore.Game;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// CONDITION_ACTIVE_GAME_EVENT and CONDITION_ACTIVE_HOLIDAY follow the live game-event state (cmangos Conditions.cpp:245-248 and
/// :318-321, vmangos Conditions.cpp:245 and :350), in the same tick an event starts or stops; the configured arrays stay as an
/// operator override.
/// </summary>
public sealed class ConditionGameEventTests
{
    private const uint EventSeven = 1;
    private const uint HolidayRow = 2;
    private const uint NotEventSeven = 3;

    private sealed class MemoryConditionStore : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>(
            [
                new(EventSeven, (int)ConditionType.ActiveGameEvent, 7, 0, 0, 0, 0),
                new(HolidayRow, (int)ConditionType.ActiveHoliday, 141, 0, 0, 0, 0),
                new(NotEventSeven, (int)ConditionType.Not, EventSeven, 0, 0, 0, 0),
            ]);
    }

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services;

        public Rig(params (string Key, string Value)[] settings)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());
            collection.AddSingleton<IConditionContentStore, MemoryConditionStore>();
            collection.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConditionFeature>.Instance));
            collection.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(
                new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Clock = new FixedGameTime(Utc(2026, 10, 3, 12, 30));
            WorldStateHooks.For(World).Time = Clock;
            Events = _services.GetRequiredService<GameEventFeature>();
            Conditions = _services.GetRequiredService<ConditionFeature>();
            Events.Attach(World);
            Conditions.Attach(World);
        }

        public WorldRuntime World { get; }

        public FixedGameTime Clock { get; }

        public GameEventFeature Events { get; }

        public ConditionFeature Conditions { get; }

        public void Dispose()
        {
            World.Dispose();
            _services.Dispose();
        }
    }

    /// <summary>Event 7 (holiday 141) runs 12:00 to 14:00 every day.</summary>
    private static GameEventContent EventSevenContent()
        => new(
            [new GameEventRecord(7, 1, 1440, 120, 141, 0, "Seven")],
            [new GameEventTimeRecord(7, "2026-10-03 12:00:00", "2030-12-31 22:59:59")],
            [], [], [], [], []);

    private static Player CreatePlayer()
    {
        var character = new CharacterRecord { Id = 1, AccountId = 1, Name = "Cond", Race = 1, Class = 1, Gender = 0, Level = 5, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = 83.5f };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400);
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

    [Fact]
    public void AConditionedOption_AppearsWhileTheEventRuns_AndDisappearsInTheSameTickItStops()
    {
        using var rig = new Rig();
        Player player = CreatePlayer();
        rig.Events.UseContent(EventSevenContent(), new HashSet<ushort>());
        Assert.False(rig.Conditions.IsSatisfied(EventSeven, player, null)); // nothing runs before the first tick

        rig.World.RunTick(10); // 12:30: the event runs
        Assert.True(rig.Conditions.IsSatisfied(EventSeven, player, null));
        Assert.True(rig.Conditions.IsSatisfied(HolidayRow, player, null));      // holiday 141 is the event's holiday
        Assert.False(rig.Conditions.IsSatisfied(NotEventSeven, player, null));  // composed conditions follow

        rig.Clock.UtcNow = Utc(2026, 10, 3, 15);
        rig.World.RunTick(uint.MaxValue / 2); // far past the delay: the update runs and stops the event in this tick

        Assert.False(rig.Conditions.IsSatisfied(EventSeven, player, null));
        Assert.False(rig.Conditions.IsSatisfied(HolidayRow, player, null));
        Assert.True(rig.Conditions.IsSatisfied(NotEventSeven, player, null));
    }

    [Fact]
    public void TheConfiguredArrays_StayAnOperatorOverride()
    {
        using var rig = new Rig(("Conditions:ActiveGameEvents:0", "7"), ("Conditions:ActiveHolidays:0", "141"));
        Player player = CreatePlayer();
        rig.Events.UseContent(GameEventContent.Empty, new HashSet<ushort>());
        rig.World.RunTick(10);

        Assert.True(rig.Conditions.IsSatisfied(EventSeven, player, null));
        Assert.True(rig.Conditions.IsSatisfied(HolidayRow, player, null));
    }

    [Fact]
    public void WithoutAGameEventFeature_ConditionsStillWork_AndNoEventIsActive()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        collection.AddSingleton<IConditionContentStore, MemoryConditionStore>();
        collection.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConditionFeature>.Instance));
        using ServiceProvider services = collection.BuildServiceProvider();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        ConditionFeature conditions = services.GetRequiredService<ConditionFeature>();
        conditions.Attach(world);

        Assert.False(conditions.IsSatisfied(EventSeven, CreatePlayer(), null));
    }
}
