namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// One <c>smart_scripts</c> row (AzerothCore src/server/game/AI/SmartScripts/SmartScriptMgr.h <c>SmartScriptHolder</c>: event, action and
/// target with their raw parameters). <see cref="EntryOrGuid"/> is a creature entry, or minus a spawn guid. Only
/// <see cref="SourceType"/> 0 (SMART_SCRIPT_TYPE_CREATURE) is run by slice 1.
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
    public string Comment { get; init; } = string.Empty;
}

/// <summary>
/// The creature <c>smart_scripts</c> rows by entry and by spawn guid (AzerothCore SmartAIMgr::LoadSmartAIFromDB / GetScript: a spawn's own
/// rows, keyed by a negative entryorguid, replace its entry's rows).
/// </summary>
public sealed class SmartScriptCatalog
{
    public static SmartScriptCatalog Empty { get; } = new([]);

    private readonly Dictionary<uint, SmartScriptRow[]> _byEntry;
    private readonly Dictionary<uint, SmartScriptRow[]> _byGuid;

    public SmartScriptCatalog(IEnumerable<SmartScriptRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SmartScriptRow[] creature = [.. rows.Where(r => r.SourceType == 0 && r.EntryOrGuid != 0)];
        Count = creature.Length;
        _byEntry = creature.Where(r => r.EntryOrGuid > 0).GroupBy(r => (uint)r.EntryOrGuid)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).ToArray());
        _byGuid = creature.Where(r => r.EntryOrGuid < 0).GroupBy(r => (uint)-(long)r.EntryOrGuid)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).ToArray());
    }

    /// <summary>The creature-source rows loaded.</summary>
    public int Count { get; }

    /// <summary>The rows a creature runs: its spawn's rows when it has any, else its entry's.</summary>
    public IReadOnlyList<SmartScriptRow> For(uint entry, uint spawnGuid)
        => spawnGuid != 0 && _byGuid.TryGetValue(spawnGuid, out SmartScriptRow[]? own) ? own
            : _byEntry.GetValueOrDefault(entry) ?? [];

    public bool HasRows(uint entry, uint spawnGuid) => For(entry, spawnGuid).Count > 0;
}
