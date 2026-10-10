using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// What a <see cref="SmartScript"/> runs on (AzerothCore SmartScript::GetBaseObject / me / go): a creature, a game object, or - for an area
/// trigger, whose script lives for one trigger - nothing but the invoking player's map. Built by the three factories; world thread only.
/// </summary>
internal sealed class SmartScriptOwner
{
    private readonly CreatureSmartAI? _ai;
    private readonly GameObjectMapSystem? _objects;
    private readonly Player? _player;

    private SmartScriptOwner(SmartScriptSource source, SmartScriptCatalog catalog, CreatureSmartAI? ai, GameObject? go, GameObjectMapSystem? objects, Player? player)
    {
        Source = source;
        Catalog = catalog;
        _ai = ai;
        Go = go;
        _objects = objects;
        _player = player;
    }

    public SmartScriptSource Source { get; }

    public SmartScriptCatalog Catalog { get; }

    /// <summary>The creature (null for a game object or an area trigger).</summary>
    public Creature? Me => _ai?.Me;

    /// <summary>The game object (null for a creature or an area trigger).</summary>
    public GameObject? Go { get; }

    /// <summary>The creature or game object the script belongs to; null for an area trigger (AzerothCore GetBaseObject).</summary>
    public WorldObject? Base => (WorldObject?)Me ?? Go;

    public Map? Map => Me?.Map ?? Go?.Map ?? _player?.Map;

    /// <summary>The creature system that serves this owner's map (the creature's own host, else the map's).</summary>
    public CreatureMapSystem? CreatureSystem => _ai is not null ? _ai.Host : Map?.FindUpdater<CreatureMapSystem>();

    public GameObjectMapSystem? Objects => _objects ?? Map?.FindUpdater<GameObjectMapSystem>();

    public static SmartScriptOwner ForCreature(CreatureSmartAI ai, SmartScriptCatalog catalog)
        => new(SmartScriptSource.Creature, catalog, ai, null, null, null);

    public static SmartScriptOwner ForGameObject(GameObjectMapSystem objects, GameObject go, SmartScriptCatalog catalog)
        => new(SmartScriptSource.GameObject, catalog, null, go, objects, null);

    public static SmartScriptOwner ForAreaTrigger(Player player, SmartScriptCatalog catalog)
        => new(SmartScriptSource.AreaTrigger, catalog, null, null, null, player);

    /// <summary>A number in [<paramref name="min"/>, <paramref name="max"/>] from the owner's map system, so a seeded test is deterministic; <paramref name="min"/> when there is none.</summary>
    public uint Rand(uint min, uint max)
    {
        if (max <= min)
        {
            return min;
        }

        if (Go is not null)
        {
            return _objects is null ? min : (uint)_objects.Random.NextInt64(min, (long)max + 1);
        }

        return (uint)(CreatureSystem?.RandomInt((int)min, (int)max) ?? (int)min);
    }
}
