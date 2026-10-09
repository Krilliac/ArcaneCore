using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Which objects make a DB script run unique (cmangos Map::ScriptExecutionParam, Maps/Map.cpp:2181-2193): both (the default
/// SCRIPT_EXEC_PARAM_UNIQUE_BY_SOURCE_TARGET), only the source, or only the target. StartEvents_Event uses the source for an event started
/// by a creature or game object and the target for one aimed at a creature or game object (DBScripts/ScriptMgr.cpp:3476-3481).
/// </summary>
internal enum DbScriptUniqueness
{
    SourceAndTarget,
    Source,
    Target,
}

/// <summary>A scheduled step of a running relay script (cmangos ScriptAction in Map::m_scriptSchedule).</summary>
internal readonly record struct RelayScriptPendingStep(long DueMs, long Sequence, int RunId, RelayScriptStep Step, ObjectGuid Source, ObjectGuid Target);

/// <summary>
/// The relay-script schedule of one map (cmangos Map::ScriptsStart and Map::ScriptsProcess, Maps/Map.cpp:2166-2272). Starting a script
/// that is already scheduled for the same source and target does nothing (the default SCRIPT_EXEC_PARAM_UNIQUE_BY_SOURCE_TARGET); the
/// steps without delay run at once, in order, and the others are scheduled at <c>now + delay</c>; each update runs the steps whose time
/// has come, in delay, priority and dump order. A step whose execution answers true terminates its script: the remaining steps of that
/// run are dropped (TERMINATE_SCRIPT, :2256-2268). World thread (the map's).
/// </summary>
internal sealed class RelayScriptRunner
{
    private readonly List<RelayScriptPendingStep> _pending = [];
    private long _sequence;
    private int _runs;

    /// <summary>Steps waiting to run.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Whether a step of relay <paramref name="scriptId"/> is still scheduled for this source and target, comparing only the objects
    /// <paramref name="unique"/> names (cmangos ScriptAction::IsSameScript, DBScripts/ScriptMgr.h:626-632, called with an empty guid for
    /// the object the execution parameter leaves out).
    /// </summary>
    public bool IsRunning(uint scriptId, ObjectGuid source, ObjectGuid target, DbScriptUniqueness unique = DbScriptUniqueness.SourceAndTarget)
        => _pending.Exists(p => p.Step.Id == scriptId
            && (unique == DbScriptUniqueness.Target || p.Source == source)
            && (unique == DbScriptUniqueness.Source || p.Target == target));

    /// <summary>
    /// cmangos Map::ScriptsStart: run the undelayed steps of <paramref name="steps"/> through <paramref name="execute"/> (stopping when one
    /// terminates the script) and schedule the rest from <paramref name="nowMs"/>. Returns false when the same script already runs for this
    /// source and target (compared as <paramref name="unique"/> says), true otherwise (also when it terminated itself).
    /// </summary>
    public bool Start(IReadOnlyList<RelayScriptStep> steps, ObjectGuid source, ObjectGuid target, long nowMs, Func<RelayScriptPendingStep, bool> execute,
        DbScriptUniqueness unique = DbScriptUniqueness.SourceAndTarget)
    {
        if (steps.Count == 0 || IsRunning(steps[0].Id, source, target, unique))
        {
            return false;
        }

        int run = ++_runs;
        int index = 0;
        for (; index < steps.Count && steps[index].DelayMs == 0; index++)
        {
            if (execute(new RelayScriptPendingStep(nowMs, ++_sequence, run, steps[index], source, target)))
            {
                return true;
            }
        }

        for (; index < steps.Count; index++)
        {
            _pending.Add(new RelayScriptPendingStep(nowMs + steps[index].DelayMs, ++_sequence, run, steps[index], source, target));
        }

        _pending.Sort(static (a, b) => a.DueMs != b.DueMs ? a.DueMs.CompareTo(b.DueMs) : a.Sequence.CompareTo(b.Sequence));
        return true;
    }

    /// <summary>cmangos Map::ScriptsProcess: run the steps due at <paramref name="nowMs"/>; a step answering true terminates its run.</summary>
    public void Update(long nowMs, Func<RelayScriptPendingStep, bool> execute)
    {
        while (_pending.Count > 0 && _pending[0].DueMs <= nowMs)
        {
            RelayScriptPendingStep step = _pending[0];
            _pending.RemoveAt(0);
            if (execute(step))
            {
                _pending.RemoveAll(p => p.RunId == step.RunId);
            }
        }
    }

    /// <summary>Drop everything (the map system is going away).</summary>
    public void Clear() => _pending.Clear();
}
