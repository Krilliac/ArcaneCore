using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The elemental invasion kill count is vmangos npc_invaderAI::JustDied (elemental_invasions.cpp:198-204), an AI death hook: a real kill of an
/// invader counts, a Creature::ForcedDespawn of a living one (EventAI respawn, GM respawn, script despawn) does not, although it also runs
/// SetDeathState(JUST_DIED) and so raises SpellSystem.UnitDied. The real SpellFeature is registered so the test would see the credit leak
/// if the counter were hooked to that event.
/// </summary>
public sealed class ElementalInvasionKillCreditTests : IDisposable
{
    private const ushort FireInvasionEvent = 13;
    private const int Fire = 0;
    private static readonly uint FireInvader = ElementalInvasionController.Elements[Fire].Invader;

    private sealed class Events : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent([new GameEventRecord(FireInvasionEvent, 0, 525600, 1, 0, 0, "Fire invasion")], [], [], [], [], [], []));
        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private readonly ServiceProvider _provider;
    private readonly WorldRuntime _world;
    private readonly HolidayObjectsFeature _holiday;
    private readonly CreatureMapSystem _creatures;
    private readonly Creature _invader;

    public ElementalInvasionKillCreditTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IGameEventDataStore, Events>();
        services.AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance));
        _provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = _provider.GetRequiredService<IServiceScopeFactory>();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance), NullLogger<WorldRuntime>.Instance);
        var events = new GameEventFeature(_provider, NullLogger<GameEventFeature>.Instance);
        events.Attach(_world);
        _holiday = NewHoliday(_provider, scopes, events);
        _holiday.Attach(_world);

        var template = new CreatureTemplate { Entry = FireInvader, Name = "Fire invader", MinLevelHealth = 50, MaxLevelHealth = 50 };
        var loader = new CreatureTemplate { Entry = FireInvader + 1, Name = "Grid loader", MinLevelHealth = 50, MaxLevelHealth = 50 };
        Map map = _world.GetMap(0);
        _creatures = new CreatureMapSystem(map,
            new CreatureContent([template], [new CreatureSpawn { Guid = 1, Entry = FireInvader, MapId = 0, X = 1, Y = 0, Z = 83.5f, SpawnTimeMinSeconds = 120, SpawnTimeMaxSeconds = 120 }], [], [], []),
            random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(_provider.GetRequiredService<SpellFeature>().System) });
        map.AddUpdater(_creatures);
        _creatures.Despawn(_creatures.SpawnTemporary(loader, 1, 0, 83.5f, 0)); // a temporary creature at the spawn loads its grid
        _invader = Assert.Single(_creatures.Creatures, c => c.Entry == FireInvader);

        _world.RunTick(5_000); // the feature installs on the world tick; the event service initialises
        Assert.True(events.Service!.StartEvent(FireInvasionEvent));
        _world.RunTick(5_000);
        Assert.True(_holiday.Invasion.IsActive(Fire));
        Assert.Equal(0, _holiday.Invasion.Kills(Fire));
    }

    private static HolidayObjectsFeature NewHoliday(IServiceProvider provider, IServiceScopeFactory scopes, GameEventFeature events)
        => new(scopes, events);

    public void Dispose()
    {
        _world.Dispose();
        _provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); // the SpellFeature is async-disposable only
    }

    [Fact]
    public void AForcedDespawnOfALivingInvaderGivesNoInvasionCredit()
    {
        Assert.True(_invader.IsAlive);

        _creatures.ForcedDespawn(_invader, 0);

        Assert.False(_invader.IsAlive);
        Assert.Equal(0, _holiday.Invasion.Kills(Fire));
    }

    [Fact]
    public void ARealKillOfAnInvaderRaisesTheKillCount()
    {
        _creatures.KillCreature(_invader);

        Assert.False(_invader.IsAlive);
        Assert.Equal(1, _holiday.Invasion.Kills(Fire));
    }
}
