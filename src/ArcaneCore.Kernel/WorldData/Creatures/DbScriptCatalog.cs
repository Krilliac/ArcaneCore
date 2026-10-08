namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>The independent DB script namespaces in cmangos ScriptMgrDefines.h (ScriptMapType).</summary>
public enum DbScriptKind : byte
{
    QuestEnd = 0,
    QuestStart = 1,
    Event = 5,
    Gossip = 6,
    Relay = 9,
}

/// <summary>
/// The non-relay DB script tables loaded once at startup. Each namespace has its own ids and uses the same row layout,
/// delay/priority ordering and command executor as dbscripts_on_relay (cmangos ScriptMgr::LoadScripts and Map::ScriptsStart).
/// </summary>
public sealed class DbScriptCatalog
{
    public static readonly DbScriptCatalog Empty = new([]);

    private readonly Dictionary<DbScriptKind, RelayScriptCatalog> _tables;

    public DbScriptCatalog(IEnumerable<(DbScriptKind Kind, RelayScriptStep Step)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _tables = steps.GroupBy(row => row.Kind)
            .ToDictionary(group => group.Key, group => new RelayScriptCatalog(group.Select(row => row.Step), []));
    }

    public IReadOnlyList<RelayScriptStep> Get(DbScriptKind kind, uint id)
        => _tables.TryGetValue(kind, out RelayScriptCatalog? table) ? table.Get(id) : [];

    public int StepCount(DbScriptKind kind)
        => _tables.TryGetValue(kind, out RelayScriptCatalog? table) ? table.StepCount : 0;
}
