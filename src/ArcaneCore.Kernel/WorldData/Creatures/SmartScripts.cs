namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// One <c>smart_scripts</c> row (AzerothCore src/server/game/AI/SmartScripts/SmartScriptMgr.h <c>SmartScriptHolder</c>: event, action and
/// target with their raw parameters). <see cref="EntryOrGuid"/> is a creature or game object entry, or minus a spawn guid, or an area trigger
/// id, or a timed action list id, by <see cref="SourceType"/> (SMART_SCRIPT_TYPE_*: 0 creature, 1 game object, 2 area trigger, 9 timed action list).
/// </summary>
public sealed record SmartScriptRow
{
    public int EntryOrGuid { get; init; }
    public byte SourceType { get; init; }
    public ushort Id { get; init; }
    public ushort Link { get; init; }
    public byte EventType { get; init; }
    public uint EventPhaseMask { get; init; }
    public byte EventChance { get; init; } = 100;
    public uint EventFlags { get; init; }
    public uint EventParam1 { get; init; }
    public uint EventParam2 { get; init; }
    public uint EventParam3 { get; init; }
    public uint EventParam4 { get; init; }
    public uint EventParam5 { get; init; }
    public uint EventParam6 { get; init; }
    public byte ActionType { get; init; }
    public uint ActionParam1 { get; init; }
    public uint ActionParam2 { get; init; }
    public uint ActionParam3 { get; init; }
    public uint ActionParam4 { get; init; }
    public uint ActionParam5 { get; init; }
    public uint ActionParam6 { get; init; }
    public byte TargetType { get; init; }
    public uint TargetParam1 { get; init; }
    public uint TargetParam2 { get; init; }
    public uint TargetParam3 { get; init; }
    public uint TargetParam4 { get; init; }
    public float TargetX { get; init; }
    public float TargetY { get; init; }
    public float TargetZ { get; init; }
    public float TargetO { get; init; }

    /// <summary>
    /// The cmangos <c>conditions</c> row (condition_entry) that gates the event, 0 = none. ArcaneCore's conditions table is the cmangos layout, not
    /// AzerothCore's source type 22, so the row names its condition directly (docs/integration/smartai-slice2-20261010.md).
    /// </summary>
    public uint ConditionId { get; init; }

    public string Comment { get; init; } = string.Empty;
}

/// <summary>The SMART_SCRIPT_TYPE values the engine runs (SmartScriptMgr.h:1801-1814); every other source type is rejected at load.</summary>
public enum SmartScriptSource : byte
{
    Creature = 0,
    GameObject = 1,
    AreaTrigger = 2,
    TimedActionList = 9,
}

/// <summary>One <c>smart_scripts</c> row the catalog did not accept, and why (logged by the world feature; the row never runs).</summary>
public sealed record SmartScriptRejection(int EntryOrGuid, byte SourceType, ushort Id, string Reason);

/// <summary>
/// The ids the loader checks smart-script rows against (SmartScriptMgr.cpp:141-210: the creature or GO template and spawn, and the area trigger must
/// exist; here also the <c>conditions</c> row a ConditionId names). A catalog built without references skips these checks.
/// </summary>
public sealed record SmartScriptReferences(
    IReadOnlySet<uint> CreatureEntries,
    IReadOnlySet<uint> CreatureGuids,
    IReadOnlySet<uint> GameObjectEntries,
    IReadOnlySet<uint> GameObjectGuids,
    IReadOnlySet<uint> AreaTriggers,
    IReadOnlySet<uint> Conditions);

/// <summary>
/// The accepted <c>smart_scripts</c> rows by source (AzerothCore SmartAIMgr::LoadSmartAIFromDB / GetScript): creature and game object rows by
/// entry or by spawn (a spawn's own rows, keyed by a negative entryorguid, replace its entry's rows), area-trigger rows by trigger id and
/// timed action list rows by list id. Every row the engine cannot run is listed in <see cref="Rejected"/> with its reason
/// (<see cref="SmartScriptSupport"/> holds the rules); nothing is dropped silently except DEBUG_ONLY rows, which AzerothCore also skips.
/// </summary>
public sealed class SmartScriptCatalog
{
    /// <summary>SMART_EVENT_FLAG_DEBUG_ONLY (SmartScriptMgr.h:1961-1976).</summary>
    private const uint DebugOnly = 0x080;

    private readonly Dictionary<uint, SmartScriptRow[]> _byEntry;
    private readonly Dictionary<uint, SmartScriptRow[]> _byGuid;
    private readonly Dictionary<uint, SmartScriptRow[]> _goByEntry;
    private readonly Dictionary<uint, SmartScriptRow[]> _goByGuid;
    private readonly Dictionary<uint, SmartScriptRow[]> _areaTriggers;
    private readonly Dictionary<uint, SmartScriptRow[]> _lists;

    public SmartScriptCatalog(IEnumerable<SmartScriptRow> rows, SmartScriptReferences? references = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var rejected = new List<SmartScriptRejection>();
        List<SmartScriptRow> live = [];
        foreach (SmartScriptRow row in rows)
        {
            if (Admit(row, references) is { } reason)
            {
                rejected.Add(new SmartScriptRejection(row.EntryOrGuid, row.SourceType, row.Id, reason));
            }
            else if ((row.EventFlags & DebugOnly) == 0)
            {
                live.Add(row);
            }
        }

        // 80/87/88 may only call lists that keep usable rows, and a list row can itself be rejected, so repeat until the set is stable.
        while (true)
        {
            HashSet<uint> lists = [.. live.Where(r => r.SourceType == (byte)SmartScriptSource.TimedActionList).Select(r => (uint)r.EntryOrGuid)];
            List<SmartScriptRow> next = [];
            foreach (SmartScriptRow row in live)
            {
                if (SmartScriptSupport.Check(row, lists.Contains, (low, high) => lists.Any(id => id >= low && id <= high)) is { } reason)
                {
                    rejected.Add(new SmartScriptRejection(row.EntryOrGuid, row.SourceType, row.Id, reason));
                }
                else
                {
                    next.Add(row);
                }
            }

            bool stable = next.Count == live.Count;
            live = next;
            if (stable)
            {
                break;
            }
        }

        Rejected = rejected;
        SmartScriptRow[] creature = [.. live.Where(r => r.SourceType == (byte)SmartScriptSource.Creature)];
        SmartScriptRow[] objects = [.. live.Where(r => r.SourceType == (byte)SmartScriptSource.GameObject)];
        SmartScriptRow[] triggers = [.. live.Where(r => r.SourceType == (byte)SmartScriptSource.AreaTrigger)];
        SmartScriptRow[] timed = [.. live.Where(r => r.SourceType == (byte)SmartScriptSource.TimedActionList)];
        Count = creature.Length;
        GameObjectRowCount = objects.Length;
        AreaTriggerRowCount = triggers.Length;
        TimedActionListRowCount = timed.Length;
        _byEntry = Index(creature, positive: true);
        _byGuid = Index(creature, positive: false);
        _goByEntry = Index(objects, positive: true);
        _goByGuid = Index(objects, positive: false);
        _areaTriggers = Index(triggers, positive: true);
        _lists = Index(timed, positive: true);
    }

    public static SmartScriptCatalog Empty { get; } = new([]);

    /// <summary>The creature-source rows accepted.</summary>
    public int Count { get; }

    /// <summary>The game-object-source rows accepted.</summary>
    public int GameObjectRowCount { get; }

    /// <summary>The area-trigger-source rows accepted.</summary>
    public int AreaTriggerRowCount { get; }

    /// <summary>The timed-action-list rows accepted.</summary>
    public int TimedActionListRowCount { get; }

    /// <summary>Every row the loader refused, with the reason (the engine never runs these).</summary>
    public IReadOnlyList<SmartScriptRejection> Rejected { get; }

    /// <summary>Whether any game object has rows (the fallback object AI only walks a map's objects when this is true).</summary>
    public bool HasGameObjectRows => GameObjectRowCount > 0;

    /// <summary>The rows a creature runs: its spawn's rows when it has any, else its entry's.</summary>
    public IReadOnlyList<SmartScriptRow> For(uint entry, uint spawnGuid)
        => spawnGuid != 0 && _byGuid.TryGetValue(spawnGuid, out SmartScriptRow[]? own) ? own
            : _byEntry.GetValueOrDefault(entry) ?? [];

    public bool HasRows(uint entry, uint spawnGuid) => For(entry, spawnGuid).Count > 0;

    /// <summary>The rows a game object runs: its spawn's rows when it has any, else its entry's (SmartAIMgr::GetScript, entryorguid = -spawnId).</summary>
    public IReadOnlyList<SmartScriptRow> ForGameObject(uint entry, uint spawnGuid)
        => spawnGuid != 0 && _goByGuid.TryGetValue(spawnGuid, out SmartScriptRow[]? own) ? own
            : _goByEntry.GetValueOrDefault(entry) ?? [];

    /// <summary>The rows of an area trigger id (SMART_SCRIPT_TYPE_AREATRIGGER: entryorguid is the trigger id).</summary>
    public IReadOnlyList<SmartScriptRow> ForAreaTrigger(uint triggerId) => _areaTriggers.GetValueOrDefault(triggerId) ?? [];

    /// <summary>The rows of a timed action list in id order (SMART_SCRIPT_TYPE_TIMED_ACTIONLIST: entryorguid is the list id).</summary>
    public IReadOnlyList<SmartScriptRow> TimedActionList(uint listId) => _lists.GetValueOrDefault(listId) ?? [];

    public bool HasTimedActionList(uint listId) => _lists.ContainsKey(listId);

    /// <summary>Steps 1-4 of the load order, the identity of the row. The reason it is refused, or null when it goes on to the structural checks.</summary>
    private static string? Admit(SmartScriptRow row, SmartScriptReferences? refs)
    {
        if (row.EntryOrGuid == 0)
        {
            return "entryorguid 0 (SmartScriptMgr.cpp:141-210)";
        }

        if (row.SourceType >= 10)
        {
            return $"source type {row.SourceType} is invalid (the maximum is 9)";
        }

        if (row.SourceType is not (0 or 1 or 2 or 9))
        {
            return $"source type {row.SourceType} is not implemented (AzerothCore reports types 3-8 as not yet implemented)";
        }

        var source = (SmartScriptSource)row.SourceType;
        if (row.EntryOrGuid < 0 && source is SmartScriptSource.AreaTrigger or SmartScriptSource.TimedActionList)
        {
            return $"negative entryorguid {row.EntryOrGuid} is valid only for creature and game object scripts";
        }

        if (refs is null)
        {
            return null;
        }

        uint key = (uint)Math.Abs((long)row.EntryOrGuid);
        string? missing = source switch
        {
            SmartScriptSource.Creature when row.EntryOrGuid > 0 && !refs.CreatureEntries.Contains(key) => $"creature template {key} does not exist",
            SmartScriptSource.Creature when row.EntryOrGuid < 0 && !refs.CreatureGuids.Contains(key) => $"creature spawn {key} does not exist",
            SmartScriptSource.GameObject when row.EntryOrGuid > 0 && !refs.GameObjectEntries.Contains(key) => $"gameobject template {key} does not exist",
            SmartScriptSource.GameObject when row.EntryOrGuid < 0 && !refs.GameObjectGuids.Contains(key) => $"gameobject spawn {key} does not exist",
            SmartScriptSource.AreaTrigger when !refs.AreaTriggers.Contains(key) => $"area trigger {key} does not exist",
            _ => null,
        };
        if (missing is not null)
        {
            return missing;
        }

        return row.ConditionId != 0 && !refs.Conditions.Contains(row.ConditionId)
            ? $"condition {row.ConditionId} is not in the conditions table" : null;
    }

    private static Dictionary<uint, SmartScriptRow[]> Index(SmartScriptRow[] rows, bool positive)
        => rows.Where(r => positive ? r.EntryOrGuid > 0 : r.EntryOrGuid < 0)
            .GroupBy(r => (uint)Math.Abs((long)r.EntryOrGuid))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).ToArray());
}
