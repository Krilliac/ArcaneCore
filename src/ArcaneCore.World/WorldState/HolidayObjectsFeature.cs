using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The object scripts of vmangos's world events that ClassicDB has no script for: the hourly bells (go_bells), the Darkmoon Faire music
/// (go_darkmoon_faire_music) and the elemental invasion rifts (elemental_invasions.cpp), with the invasion stages saved in characters table
/// world_elemental_invasion. The objects themselves are ClassicDB spawns; nothing is spawned here except the rifts' invaders.
/// </summary>
public sealed class HolidayObjectsFeature(IServiceProvider services, IServiceScopeFactory scopes, GameEventFeature events,
    ILogger<HolidayObjectsFeature>? logger = null) : IWorldFeature
{
    private readonly HashSet<GameObjectMapSystem> _installed = [];
    private bool _deathHooked;

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
        };
    }

    private void Install(WorldRuntime world)
    {
        if (!_deathHooked && services.GetService<SpellFeature>()?.System is { } spells)
        {
            spells.UnitDied += unit =>
            {
                if (unit is ArcaneCore.Game.Creatures.Creature creature) Invasion.OnCreatureDied(creature.Entry);
            };
            _deathHooked = true;
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
            logger?.LogError(ex, "could not load the elemental invasion state");
        }
    }

    private void Save(int index)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            scope.ServiceProvider.GetService<IElementalInvasionStore>()?
                .SaveAsync(new ElementalInvasionState(index, Invasion.Stage(index), Invasion.Kills(index))).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "could not save the elemental invasion state");
        }
    }
}
