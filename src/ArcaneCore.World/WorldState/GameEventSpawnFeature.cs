using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Wires game-event spawns into the world (docs/areas/game-events-weather.md): registers <see cref="GameEventSpawns"/> as the
/// spawn effect of every <see cref="GameEventService"/> the <see cref="GameEventFeature"/> builds, and installs it as the
/// <see cref="ISpawnGate"/> of every creature and gameobject map system, instances included. The systems attach themselves at
/// different times (the creature feature at start, the gameobject feature on map creation), so the gate is installed from the
/// world tick as soon as a system exists; installing it also removes the spawns it refuses that a system created before it.
/// With no running event the objects listed under positive events are absent and those listed under negative events are present, so
/// a world with game events switched off keeps event objects out, as vmangos does.
/// </summary>
public sealed class GameEventSpawnFeature(IServiceProvider services, ILogger<GameEventSpawnFeature> logger) : IWorldFeature
{
    private WorldRuntime? _world;
    private GameEventSpawns? _spawns;
    private bool _audited;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        GameEventFeature events = services.GetRequiredService<GameEventFeature>();
        events.ServiceCreated += Wire;
        if (events.Service is { } existing)
        {
            Wire(existing);
        }

        world.WorldTick += _ => InstallGate();
    }

    private void Wire(GameEventService service)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the game event spawn feature is not attached");
        _spawns = new GameEventSpawns(service, service.Rows, () => world.Maps);
        service.AddEffects(_spawns);
        _audited = false;
        InstallGate();
    }

    /// <summary>Install the gate on every map system that does not have the current one (world thread, once a system exists).</summary>
    private void InstallGate()
    {
        GameEventSpawns? spawns = _spawns;
        WorldRuntime? world = _world;
        if (spawns is null || world is null)
        {
            return;
        }

        foreach (Map map in world.Maps)
        {
            // A wrapping gate (a battleground map's event gate) keeps its place and asks the game-event gate after its own rule.
            if (map.FindUpdater<CreatureMapSystem>() is { } creatures && !ReferenceEquals(creatures.SpawnGate, spawns))
            {
                if (creatures.SpawnGate is IWrappingSpawnGate wrapping)
                {
                    if (!ReferenceEquals(wrapping.Inner, spawns))
                    {
                        wrapping.Inner = spawns;
                        creatures.SpawnGate = wrapping;
                    }
                }
                else
                {
                    creatures.SpawnGate = spawns;
                }
            }

            if (map.FindUpdater<GameObjectMapSystem>() is { } objects && !ReferenceEquals(objects.SpawnGate, spawns))
            {
                if (objects.SpawnGate is IWrappingSpawnGate wrapping)
                {
                    if (!ReferenceEquals(wrapping.Inner, spawns))
                    {
                        wrapping.Inner = spawns;
                        objects.SpawnGate = wrapping;
                    }
                }
                else
                {
                    objects.SpawnGate = spawns;
                }
            }
        }

        AuditOrphans(spawns);
    }

    /// <summary>
    /// Event rows whose spawn is not in the creature or gameobject content (classic-db z2815 has 33 and 1126 of them: the guids are in
    /// no spawn table of the dump) cannot do anything: say so once, with the counts, instead of per row (vmangos logs each one at load).
    /// <para>
    /// The audit waits until both contents are installed. The gameobject feature has its content at attach, but the creature feature
    /// installs its content from the world thread, so the first gate install (at attach) used to see the gameobjects and an empty creature
    /// content and counted every creature event guid as missing (the wave-9 "3148 creature guid(s)", which were all in the world).
    /// </para>
    /// </summary>
    private void AuditOrphans(GameEventSpawns spawns)
    {
        CreatureWorldFeature? creatureFeature = services.GetService<CreatureWorldFeature>();
        GameObjectLootFeature? objectFeature = services.GetService<GameObjectLootFeature>();
        if (_audited || creatureFeature is null || objectFeature is null)
        {
            return;
        }

        if (!creatureFeature.ContentInstalled || !objectFeature.ContentInstalled)
        {
            return; // a content is not installed yet; the next world tick asks again
        }

        var creatureContent = creatureFeature.Content;
        var objectContent = objectFeature.Content;
        if (creatureContent.SpawnCount == 0 && objectContent.SpawnCount == 0)
        {
            return; // no world content at all (a bare test world): nothing to compare the rows with
        }

        _audited = true;
        (int missingCreatures, int missingObjects) = CountOrphans(spawns, creatureContent, objectContent);
        if (missingCreatures > 0 || missingObjects > 0)
        {
            logger.LogWarning(
                "game event rows without a spawn are ignored: {Creatures} creature guid(s) not in the creature spawns, {GameObjects} gameobject guid(s) not in the gameobject spawns",
                missingCreatures, missingObjects);
        }
    }

    /// <summary>The gated creature and gameobject guids that are not spawns of the given contents.</summary>
    internal static (int Creatures, int GameObjects) CountOrphans(ISpawnGate gate, CreatureContent creatures, GameObjectContent objects)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(creatures);
        ArgumentNullException.ThrowIfNull(objects);
        HashSet<uint> creatureGuids = [.. creatures.MapsWithSpawns.SelectMany(creatures.GetSpawns).Select(s => s.Guid)];
        HashSet<uint> objectGuids = [.. objects.MapsWithSpawns.SelectMany(objects.GetSpawns).Select(s => s.Guid)];
        return (gate.GatedCreatures.Count(g => !creatureGuids.Contains(g)), gate.GatedGameObjects.Count(g => !objectGuids.Contains(g)));
    }
}
