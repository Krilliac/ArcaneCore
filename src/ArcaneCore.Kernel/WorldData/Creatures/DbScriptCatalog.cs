namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>The independent DB script namespaces in cmangos ScriptMgrDefines.h (ScriptMapType).</summary>
public enum DbScriptKind : byte
{
    QuestEnd = 0,
    QuestStart = 1,

    /// <summary>cmangos <c>dbscripts_on_spell</c> (SCRIPT_TYPE_SPELL), also fed by vmangos <c>spell_scripts</c>: keyed by spell id.</summary>
    Spell = 2,

    /// <summary>cmangos <c>dbscripts_on_go_use</c> (SCRIPT_TYPE_GAMEOBJECT): keyed by the <c>gameobject.guid</c> of the object that is used.</summary>
    GameObject = 3,

    /// <summary>cmangos <c>dbscripts_on_go_template_use</c> (SCRIPT_TYPE_GAMEOBJECT_TEMPLATE): keyed by the <c>gameobject_template.entry</c> of the object that is used.</summary>
    GameObjectTemplate = 4,
    Event = 5,
    Gossip = 6,
    CreatureMovement = 8,
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

    /// <summary>The cmangos table a script kind is stored in (the table a skipped step's warning names).</summary>
    public static string TableOf(DbScriptKind kind) => kind switch
    {
        DbScriptKind.QuestStart => "dbscripts_on_quest_start",
        DbScriptKind.QuestEnd => "dbscripts_on_quest_end",
        DbScriptKind.Spell => "dbscripts_on_spell",
        DbScriptKind.GameObject => "dbscripts_on_go_use",
        DbScriptKind.GameObjectTemplate => "dbscripts_on_go_template_use",
        DbScriptKind.Event => "dbscripts_on_event",
        DbScriptKind.Gossip => "dbscripts_on_gossip",
        DbScriptKind.CreatureMovement => "dbscripts_on_creature_movement",
        DbScriptKind.Relay => "dbscripts_on_relay",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a DB script table"),
    };

    public IReadOnlyList<RelayScriptStep> Get(DbScriptKind kind, uint id)
        => _tables.TryGetValue(kind, out RelayScriptCatalog? table) ? table.Get(id) : [];

    /// <summary>Every step of every namespace.</summary>
    public IEnumerable<(DbScriptKind Kind, RelayScriptStep Step)> AllSteps
        => _tables.SelectMany(table => table.Value.AllSteps.Select(step => (table.Key, step)));

    public int StepCount(DbScriptKind kind)
        => _tables.TryGetValue(kind, out RelayScriptCatalog? table) ? table.StepCount : 0;
}
