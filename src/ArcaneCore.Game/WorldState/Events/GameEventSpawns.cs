using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// Makes game-event spawns real: the <see cref="ISpawnGate"/> the creature and gameobject map systems ask when a grid loads, and
/// the <see cref="IGameEventEffects"/> phase that adds or removes the spawns of an event when it starts or stops
/// (<c>GameEventSpawn</c> / <c>GameEventUnspawn</c>, vmangos GameEventMgr.cpp:807-960).
/// <para>
/// The gate is a pure function of which events are running: a spawn listed under positive events exists while at least one of
/// them runs, and not at all while any event it is listed under negatively runs (a spawn listed under both signs of different
/// events follows both). That is the end state of vmangos' sequence of spawn and unspawn calls, so a grid that loads in the middle of
/// an event, or after it stopped, gets the right objects with no per-grid bookkeeping. The effect phase touches only the guids
/// listed under the event that changed, on every map that is running (instances included), so starting an event costs
/// the size of its own list and never a scan of all spawns.
/// </para>
/// </summary>
public sealed class GameEventSpawns : ISpawnGate, IGameEventEffects
{
    private readonly IGameEventState _state;
    private readonly GameEventRows _rows;
    private readonly Func<IEnumerable<Map>> _maps;
    private readonly Dictionary<uint, ushort[]> _creaturePositive;
    private readonly Dictionary<uint, ushort[]> _creatureNegative;
    private readonly Dictionary<uint, ushort[]> _gameObjectPositive;
    private readonly Dictionary<uint, ushort[]> _gameObjectNegative;

    public GameEventSpawns(IGameEventState state, GameEventRows rows, Func<IEnumerable<Map>> maps)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        (_creaturePositive, _creatureNegative) = Index(rows.Creatures);
        (_gameObjectPositive, _gameObjectNegative) = Index(rows.GameObjects);
    }

    public bool AllowsCreature(uint spawnGuid) => Allows(_creaturePositive, _creatureNegative, spawnGuid);

    public bool AllowsGameObject(uint spawnGuid) => Allows(_gameObjectPositive, _gameObjectNegative, spawnGuid);

    public IEnumerable<uint> GatedCreatures => _creaturePositive.Keys.Concat(_creatureNegative.Keys).Distinct();

    public IEnumerable<uint> GatedGameObjects => _gameObjectPositive.Keys.Concat(_gameObjectNegative.Keys).Distinct();

    /// <summary>The event started (positive number) or stopped (negative): the objects listed under that number come or go.</summary>
    public void SpawnEvent(int signedEventId) => Refresh(signedEventId);

    /// <summary>The mirror of <see cref="SpawnEvent"/>: the event stopped (positive number) or started (negative).</summary>
    public void UnspawnEvent(int signedEventId) => Refresh(signedEventId);

    private bool Allows(Dictionary<uint, ushort[]> positive, Dictionary<uint, ushort[]> negative, uint guid)
    {
        if (positive.TryGetValue(guid, out ushort[]? spawnWith) && !spawnWith.Any(_state.IsActiveEvent))
        {
            return false;
        }

        return !negative.TryGetValue(guid, out ushort[]? removeWith) || !removeWith.Any(_state.IsActiveEvent);
    }

    private void Refresh(int signedEventId)
    {
        _rows.Creatures.TryGetValue(signedEventId, out IReadOnlyList<uint>? creatures);
        _rows.GameObjects.TryGetValue(signedEventId, out IReadOnlyList<uint>? gameObjects);
        if (creatures is null && gameObjects is null)
        {
            return;
        }

        foreach (Map map in _maps().ToArray())
        {
            if (creatures is not null)
            {
                map.FindUpdater<CreatureMapSystem>()?.RefreshSpawns(creatures);
            }

            if (gameObjects is not null)
            {
                map.FindUpdater<GameObjectMapSystem>()?.RefreshSpawns(gameObjects);
            }
        }
    }

    private static (Dictionary<uint, ushort[]> Positive, Dictionary<uint, ushort[]> Negative) Index(IReadOnlyDictionary<int, IReadOnlyList<uint>> bySignedEvent)
    {
        var positive = new Dictionary<uint, List<ushort>>();
        var negative = new Dictionary<uint, List<ushort>>();
        foreach ((int signed, IReadOnlyList<uint> guids) in bySignedEvent)
        {
            Dictionary<uint, List<ushort>> target = signed > 0 ? positive : negative;
            var id = (ushort)Math.Abs(signed);
            foreach (uint guid in guids)
            {
                if (!target.TryGetValue(guid, out List<ushort>? events))
                {
                    target[guid] = events = [];
                }

                events.Add(id);
            }
        }

        return (positive.ToDictionary(p => p.Key, p => p.Value.ToArray()), negative.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    }
}
