using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Conditions;

/// <summary>
/// Mutable world-thread facts for conditions 31, 40 and 42. Instance scripts and world-event
/// owners set their own facts; an unset world-script fact is unknown and fails closed.
/// </summary>
public sealed class ConditionRuntimeState
{
    private static readonly ConditionalWeakTable<WorldRuntime, ConditionRuntimeState> s_worlds = new();
    private readonly ConditionalWeakTable<Map, MapFacts> _maps = new();
    private readonly Dictionary<(uint Id, uint State), bool> _worldScript = [];

    private sealed class MapFacts
    {
        public Dictionary<uint, int> Variables { get; } = [];
        public HashSet<uint> CompletedEncounters { get; } = [];
    }

    public static ConditionRuntimeState For(WorldRuntime world) => s_worlds.GetValue(world, _ => new ConditionRuntimeState());

    public void SetMapVariable(Map map, uint id, int value) => _maps.GetOrCreateValue(map).Variables[id] = value;

    public int GetMapVariable(Map map, uint id) => _maps.GetOrCreateValue(map).Variables.GetValueOrDefault(id);

    public void SetCompletedEncounter(Map map, uint dbcEncounterId, bool complete)
    {
        HashSet<uint> completed = _maps.GetOrCreateValue(map).CompletedEncounters;
        if (complete) completed.Add(dbcEncounterId);
        else completed.Remove(dbcEncounterId);
    }

    public bool HasCompletedEncounter(Map map, uint first, uint second)
    {
        HashSet<uint> completed = _maps.GetOrCreateValue(map).CompletedEncounters;
        return completed.Contains(first) || (second != 0 && completed.Contains(second));
    }

    public void SetWorldScriptCondition(uint id, uint state, bool fulfilled) => _worldScript[(id, state)] = fulfilled;

    public bool? WorldScriptCondition(uint id, uint state)
        => _worldScript.TryGetValue((id, state), out bool fulfilled) ? fulfilled : null;
}
