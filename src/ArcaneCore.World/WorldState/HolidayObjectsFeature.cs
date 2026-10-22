using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ArcaneCore.Kernel.Diagnostics;
namespace ArcaneCore.World.WorldState;

/// <summary>
/// The object scripts of vmangos's world events that ClassicDB has no script for: the hourly bells (go_bells), the Darkmoon Faire music
/// (go_darkmoon_faire_music) and the elemental invasion rifts (elemental_invasions.cpp), with the invasion stages saved in characters table
/// world_elemental_invasion. The objects themselves are ClassicDB spawns; nothing is spawned here except the rifts' invaders.
/// </summary>
public sealed class HolidayObjectsFeature(IServiceScopeFactory scopes, GameEventFeature events,
    ILogger<HolidayObjectsFeature>? logger = null) : IWorldFeature
{
    private readonly HashSet<GameObjectMapSystem> _installed = [];
    private readonly HashSet<MapCombat> _combats = [];

    public ElementalInvasionController Invasion { get; private set; } = null!;

    internal BellAi Bells { get; private set; } = null!;

    internal DarkmoonFaireMusicAi Music { get; private set; } = null!;

    internal ElementalRiftAi Rifts { get; private set; } = null!;

    public void Attach(WorldRuntime world)
    {
        Bells = new BellAi(events.IsActiveEvent);
        Music = new DarkmoonFaireMusicAi(events.IsActiveEvent);
        Invasion = new ElementalInvasionController(events.IsActiveEvent);
        Rifts = new ElementalRiftAi(Invasion);
        Load();
        Invasion.Changed = Save;
        world.WorldTick += _ =>
        {
            Install(world);
            Invasion.Update();
        };
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<GameObjectMapSystem>() is { } objects) _installed.Remove(objects);
            if (map.FindUpdater<MapCombat>() is { } combat && _combats.Remove(combat)) combat.UnitKilled -= OnUnitKilled;
        };
    }

    private void Install(WorldRuntime world)
    {
        // npc_invaderAI::JustDied (elemental_invasions.cpp:198-204) is an AI death hook: only a creature that really died counts, so the
        // credit hangs on MapCombat.UnitKilled (raised after OnJustDied) and not on SpellSystem.UnitDied, which a ForcedDespawn also raises.
        foreach (Map map in world.Maps)
        {
            if (map.FindUpdater<MapCombat>() is { } combat && _combats.Add(combat)) combat.UnitKilled += OnUnitKilled;
        }

        foreach (Map map in world.Maps.Where(m => m.MapId is 0 or 1))
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects || !_installed.Add(objects)) continue;
            objects.RegisterAi(BellAi.GoHordeBell, Bells);
            objects.RegisterAi(BellAi.GoAllianceBell, Bells);
            objects.RegisterAi(DarkmoonFaireMusicAi.GoDarkmoonFaireMusic, Music);
            foreach (InvasionElement e in ElementalInvasionController.Elements) objects.RegisterAi(e.Rift, Rifts);
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is ArcaneCore.Game.Creatures.Creature creature) Invasion.OnCreatureDied(creature.Entry);
    }

    private void Load()
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IElementalInvasionStore>()?.LoadAsync().GetAwaiter().GetResult() is not { } saved) return;
            foreach (ElementalInvasionState s in saved.Where(s => s.Element is >= 0 and < 4)) Invasion.Restore(s.Element, s.Stage, s.Kills);
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("world-event.elemental-invasion", ex);
            AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "elemental-invasion", $"load error={ex.GetType().Name}: {ex.Message}");
            logger?.LogError(ex, "could not load the elemental invasion state");
        }
    }

    private StateSaveQueue<int, ElementalInvasionState>? _saves;

    /// <summary>The per-element writer: off the world thread (every invader kill saved on it).</summary>
    internal StateSaveQueue<int, ElementalInvasionState> Saves => _saves ??= new(async state =>
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IElementalInvasionStore>() is { } store) await store.SaveAsync(state).ConfigureAwait(false);
    }, logger, "elemental invasion");

    /// <summary>VAR_*_KILLS / VAR_* stage: the element's latest stage and kills, written off the world thread.</summary>
    private void Save(int index) => Saves.Save(index, new ElementalInvasionState(index, Invasion.Stage(index), Invasion.Kills(index)));

    /// <summary>Writes every queued state before shutdown completes.</summary>
    public Task StopAsync() => _saves?.FlushAsync() ?? Task.CompletedTask;
}
