namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// One <c>dbscripts_on_relay</c> row (cmangos-classic DB script table, mangos.sql; field meanings per command in
/// src/game/DBScripts/ScriptMgr.h:36-160). <paramref name="Ordinal"/> keeps the dump order of rows that share an id, delay and priority.
/// </summary>
public sealed record RelayScriptStep(
    uint Id,
    uint DelayMs,
    uint Priority,
    uint Command,
    uint DataLong,
    uint DataLong2,
    uint DataLong3,
    uint BuddyEntry,
    uint SearchRadius,
    uint DataFlags,
    int DataInt,
    int DataInt2,
    int DataInt3,
    int DataInt4,
    float DataFloat,
    float X,
    float Y,
    float Z,
    float Orientation,
    float Speed,
    uint ConditionId,
    uint Ordinal = 0)
{
    /// <summary>The four <c>dataint</c> columns (texts, emotes or spells chosen at random by several commands).</summary>
    public IReadOnlyList<int> DataInts => [DataInt, DataInt2, DataInt3, DataInt4];
}

/// <summary>One relay choice of a <c>dbscript_random_templates</c> row of type 1 (relay): template id, relay id, chance.</summary>
public sealed record RelayScriptTemplateChoice(uint TemplateId, uint RelayId, uint Chance);

/// <summary>
/// The relay DB scripts (cmangos <c>dbscripts_on_relay</c>) and their random templates, loaded once at startup and read-only afterwards.
/// A script's steps are ordered by delay, then priority, then dump order (cmangos keeps them in a multimap by delay and runs equal
/// delays by priority, ScriptMgr.cpp LoadScripts / ScriptsStart).
/// </summary>
public sealed class RelayScriptCatalog
{
    public static readonly RelayScriptCatalog Empty = new([], []);

    private readonly Dictionary<uint, RelayScriptStep[]> _scripts;
    private readonly Dictionary<uint, RelayScriptTemplateChoice[]> _templates;

    public RelayScriptCatalog(IEnumerable<RelayScriptStep> steps, IEnumerable<RelayScriptTemplateChoice> templates)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(templates);
        _scripts = steps.GroupBy(s => s.Id)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.DelayMs).ThenBy(s => s.Priority).ThenBy(s => s.Ordinal).ToArray());
        _templates = templates.GroupBy(t => t.TemplateId).ToDictionary(g => g.Key, g => g.OrderBy(t => t.RelayId).ToArray());
    }

    public int ScriptCount => _scripts.Count;

    public int StepCount => _scripts.Values.Sum(s => s.Length);

    public int TemplateCount => _templates.Count;

    /// <summary>The steps of relay script <paramref name="id"/> in run order (empty when there is none).</summary>
    public IReadOnlyList<RelayScriptStep> Get(uint id) => _scripts.GetValueOrDefault(id) ?? [];

    public bool Contains(uint id) => _scripts.ContainsKey(id);

    /// <summary>
    /// cmangos ScriptMgr::GetRandomRelayDbscriptFromTemplate: explicit chances first (cumulative against <paramref name="percentRoll"/>,
    /// 0..100), the remaining probability uniformly over the chance-zero rows (<paramref name="equalIndex"/> picks among them). 0 when the
    /// template does not exist or nothing was chosen.
    /// </summary>
    public uint SelectFromTemplate(uint templateId, float percentRoll, Func<int, int> equalIndex)
    {
        ArgumentNullException.ThrowIfNull(equalIndex);
        if (!_templates.TryGetValue(templateId, out RelayScriptTemplateChoice[]? rows))
        {
            return 0;
        }

        ulong cumulative = 0;
        foreach (RelayScriptTemplateChoice row in rows.Where(r => r.Chance > 0))
        {
            cumulative += row.Chance;
            if (cumulative >= percentRoll)
            {
                return row.RelayId;
            }
        }

        RelayScriptTemplateChoice[] equal = [.. rows.Where(r => r.Chance == 0)];
        if (equal.Length == 0)
        {
            return 0;
        }

        int index = equalIndex(equal.Length);
        return (uint)index < equal.Length ? equal[index].RelayId : 0;
    }
}
