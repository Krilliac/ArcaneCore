using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The Lunar Festival and New Year's Eve scripts of vmangos on the two continents: the launched fireworks (npc_pats_firework_guy), Omen's
/// return from the Moonglade lake (boss_omen), the firecrackers (go_lunar_festival_firecracker), the show rockets (go_firework_rocket) and the
/// city fireworks show (go_cheer_speaker). All the spawns stay ClassicDB's game_event rows; nothing is spawned here except what the scripts
/// summon (Omen, the show fireworks).
/// </summary>
public sealed class LunarFestivalFeature(IServiceProvider services, GameEventFeature events) : IWorldFeature
{
    private readonly HashSet<CreatureMapSystem> _creatureSystems = [];
    private readonly HashSet<GameObjectMapSystem> _objectSystems = [];
    private readonly FirecrackerAi _firecrackers = new();
    private readonly FireworkRocketAi _rockets = new();
    private bool _spellHooked;

    public OmenController Omen { get; } = new();

    internal CheerSpeakerAi CheerSpeakers { get; private set; } = null!;

    public void Attach(WorldRuntime world)
    {
        CheerSpeakers = new CheerSpeakerAi(events.IsActiveEvent);
        world.WorldTick += diffMs =>
        {
            Install(world);
            Omen.Update(diffMs);
        };
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } creatures) _creatureSystems.Remove(creatures);
            if (map.FindUpdater<GameObjectMapSystem>() is { } objects) _objectSystems.Remove(objects);
        };
    }

    private void Install(WorldRuntime world)
    {
        if (!_spellHooked && services.GetService<SpellFeature>()?.System is { } spells)
        {
            spells.SpellHitTarget += (_, target, spellId) => Omen.OnSpellHit(target, spellId);
            _spellHooked = true;
        }

        foreach (Map map in world.Maps.Where(m => m.MapId is 0 or 1))
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } creatures && _creatureSystems.Add(creatures))
            {
                foreach (FireworkKind kind in FireworkCatalog.Fireworks)
                    creatures.RegisterEntryAi(kind.NpcEntry, creature => new FireworkGuyAi(creature, Omen));
            }

            if (map.FindUpdater<GameObjectMapSystem>() is { } objects && _objectSystems.Add(objects))
            {
                foreach (uint entry in FirecrackerAi.Entries) objects.RegisterAi(entry, _firecrackers);
                foreach (uint entry in FireworkRocketAi.Entries) objects.RegisterAi(entry, _rockets);
                objects.RegisterAi(CheerSpeakerAi.GoCheerSpeaker, CheerSpeakers);
            }
        }
    }
}
