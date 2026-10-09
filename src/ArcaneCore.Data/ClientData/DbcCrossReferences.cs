using System.Data.Common;
using System.Globalization;
using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.ClientData;

/// <summary>
/// One world-database column whose non-zero values must be ids of a client DBC (field 0). <paramref name="Where"/> is an extra
/// SQL condition (column names unquoted, both sides of it constant) for columns that only sometimes hold such an id.
/// </summary>
public sealed record DbcReference(string Dbc, string Table, string Column, string? Where = null)
{
    public string Name => $"{Table}.{Column}";
}

/// <summary>What one reference check found.</summary>
public enum DbcReferenceStatus
{
    /// <summary>Every referenced id is in the DBC.</summary>
    Ok,

    /// <summary>Some referenced ids are not in the DBC.</summary>
    Dangling,

    /// <summary>Not checked: the DBC is unusable or the table/column does not exist in this database.</summary>
    Skipped,

    /// <summary>Every missing id is a documented gap in the build-5875 client DBC set.</summary>
    KnownClientGap,
}

/// <summary>The result of one reference check.</summary>
/// <param name="Reference">The column checked.</param>
/// <param name="Status">The outcome.</param>
/// <param name="ReferencedIds">Distinct non-zero ids the column holds.</param>
/// <param name="DanglingIds">Distinct ids the DBC lacks.</param>
/// <param name="DanglingRows">Rows holding such an id.</param>
/// <param name="Samples">Up to five of the dangling ids, ascending.</param>
/// <param name="Reason">Why it was skipped, or null.</param>
public sealed record DbcReferenceResult(
    DbcReference Reference, DbcReferenceStatus Status, int ReferencedIds, int DanglingIds, long DanglingRows, IReadOnlyList<long> Samples, string? Reason)
{
    public string Describe()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return Status switch
        {
            DbcReferenceStatus.Skipped => $"{Reference.Name} -> {Reference.Dbc}: skipped ({Reason})",
            DbcReferenceStatus.Ok => string.Create(c, $"{Reference.Name} -> {Reference.Dbc}: ok ({ReferencedIds} ids)"),
            DbcReferenceStatus.KnownClientGap => string.Create(c, $"{Reference.Name} -> {Reference.Dbc}: known client-data gap ({DanglingIds} of {ReferencedIds} ids in {DanglingRows} rows; e.g. {string.Join(", ", Samples)})"),
            _ => string.Create(c, $"{Reference.Name} -> {Reference.Dbc}: {DanglingIds} of {ReferencedIds} ids dangling in {DanglingRows} rows (e.g. {string.Join(", ", Samples)})"),
        };
    }
}

/// <summary>
/// Cross-references the world database against the client DBCs (docs/areas/client-data.md): spell, map, area, faction, display,
/// trigger, graveyard, taxi, lock, item set, skill and emote ids the content uses must exist in the client's
/// tables, or the client shows nothing (or crashes) for them. Read-only: every query is a SELECT with GROUP BY.
/// </summary>
public static partial class DbcCrossReferences
{
    private static IEnumerable<DbcReference> Many(string dbc, string table, params string[] columns) => columns.Select(c => new DbcReference(dbc, table, c));

    /// <summary>Every reference checked, grouped by DBC.</summary>
    public static IReadOnlyList<DbcReference> All { get; } =
    [
        new("Spell.dbc", "spell_template", "Id", "IsServerSide = 0"),
        new("Spell.dbc", "npc_trainer", "spell"),
        .. Many("Spell.dbc", "item_template", "spellid_1", "spellid_2", "spellid_3", "spellid_4", "spellid_5"),
        new("Spell.dbc", "playercreateinfo_spell", "Spell"),
        new("Spell.dbc", "playercreateinfo_action", "action", "type = 0"),
        new("Spell.dbc", "totem_spell", "SpellId"),
        .. Many("Spell.dbc", "quest_template", "SrcSpell", "RewSpell", "RewSpellCast", "ReqSpellCast1", "ReqSpellCast2", "ReqSpellCast3", "ReqSpellCast4"),
        new("Spell.dbc", "creature_template", "TrainerSpell"),
        new("Spell.dbc", "spell_proc_event", "entry"),
        new("Spell.dbc", "spell_target_position", "id"),

        new("Map.dbc", "map_template", "Entry"),
        new("Map.dbc", "creature_spawn", "MapId"),
        new("Map.dbc", "gameobject_spawn", "MapId"),
        new("Map.dbc", "area_template", "MapId"),
        new("Map.dbc", "areatrigger_template", "MapId"),
        new("Map.dbc", "areatrigger_teleport", "TargetMap"),
        new("Map.dbc", "world_safe_locs", "map"),
        new("Map.dbc", "player_create_info", "MapId"),
        new("Map.dbc", "taxi_nodes", "map_id"),
        new("Map.dbc", "game_tele", "Map"),
        new("Map.dbc", "spell_target_position", "target_map"),
        new("Map.dbc", "item_template", "map_bound"),

        new("AreaTable.dbc", "area_template", "Entry"),
        new("AreaTable.dbc", "player_create_info", "ZoneId"),
        new("AreaTable.dbc", "game_graveyard_zone", "ghost_zone"),
        new("AreaTable.dbc", "quest_template", "ZoneOrSort"),
        new("AreaTable.dbc", "game_weather", "Zone"),
        new("AreaTable.dbc", "item_template", "area_bound"),

        new("FactionTemplate.dbc", "creature_template", "Faction"),
        new("FactionTemplate.dbc", "gameobject_template", "Faction"),
        new("FactionTemplate.dbc", "race_info", "FactionTemplate"),

        .. Many("Faction.dbc", "creature_onkill_reputation", "RewOnKillRepFaction1", "RewOnKillRepFaction2"),
        .. Many("Faction.dbc", "quest_template", "RewRepFaction1", "RewRepFaction2", "RewRepFaction3", "RewRepFaction4", "RewRepFaction5",
            "RepObjectiveFaction", "RequiredMinRepFaction", "RequiredMaxRepFaction"),
        new("Faction.dbc", "item_template", "required_reputation_faction"),
        new("Faction.dbc", "reputation_spillover_template", "Faction"),
        new("Faction.dbc", "reputation_reward_rate", "Faction"),

        .. Many("CreatureDisplayInfo.dbc", "creature_template", "DisplayId1", "DisplayId2", "DisplayId3", "DisplayId4"),
        .. Many("CreatureDisplayInfo.dbc", "creature_model_info", "DisplayId", "DisplayIdOtherGender"),
        new("CreatureDisplayInfo.dbc", "race_info", "DisplayId"),
        new("CreatureDisplayInfo.dbc", "creature_addon", "MountDisplayId"),
        new("GameObjectDisplayInfo.dbc", "gameobject_template", "DisplayId"),
        new("ItemDisplayInfo.dbc", "item_template", "display_id"),

        new("AreaTrigger.dbc", "areatrigger_template", "Id"),
        new("AreaTrigger.dbc", "areatrigger_teleport", "Id"),
        new("AreaTrigger.dbc", "areatrigger_tavern", "id"),
        new("AreaTrigger.dbc", "areatrigger_involvedrelation", "id"),
        new("WorldSafeLocs.dbc", "world_safe_locs", "id"),
        new("WorldSafeLocs.dbc", "game_graveyard_zone", "id"),
        new("TaxiNodes.dbc", "taxi_nodes", "id"),
        .. Many("TaxiNodes.dbc", "taxi_path", "from_node", "to_node"),
        new("TaxiPath.dbc", "taxi_path", "id"),
        new("Lock.dbc", "lock_template", "Id"),
        new("Lock.dbc", "item_template", "lock_id"),
        new("ItemSet.dbc", "item_template", "set_id"),
        new("SkillLine.dbc", "item_template", "required_skill"),
        new("SkillLine.dbc", "quest_template", "RequiredSkill"),
        new("SkillLine.dbc", "npc_trainer", "reqskill"),
        new("SkillLine.dbc", "playercreateinfo_skills", "skill"),
        .. Many("Emotes.dbc", "broadcast_text", "EmoteId1", "EmoteId2", "EmoteId3"),
    ];

    /// <summary>The field-0 ids of a DBC.</summary>
    public static HashSet<long> ReadIds(string path)
    {
        DbcFile file = DbcFile.Load(path);
        var ids = new HashSet<long>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            ids.Add(file.GetUInt32(row, 0));
        }

        return ids;
    }

    /// <summary>
    /// Run every reference of <paramref name="references"/> (default <see cref="All"/>) against the open world database
    /// <paramref name="connection"/> and the DBCs in <paramref name="directory"/>. A DBC that is missing or has another layout skips its
    /// checks; so does a table or column this database does not have.
    /// </summary>
    public static async Task<IReadOnlyList<DbcReferenceResult>> RunAsync(
        DbConnection connection, string directory, IEnumerable<DbcReference>? references = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        (string open, string close) = connection.GetType().Name.Contains("MySql", StringComparison.OrdinalIgnoreCase) ? ("`", "`") : ("\"", "\"");
        var dbcs = new Dictionary<string, (HashSet<long>? Ids, string? Reason)>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DbcReferenceResult>();
        foreach (DbcReference reference in references ?? All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!dbcs.TryGetValue(reference.Dbc, out (HashSet<long>? Ids, string? Reason) dbc))
            {
                ClientDbcFileCheck check = ClientDbcInspector.Check(Path.Combine(directory, reference.Dbc));
                dbc = check.IsUsable ? (ReadIds(check.Path), null) : (null, $"{reference.Dbc} {check.Describe()}");
                dbcs[reference.Dbc] = dbc;
            }

            if (dbc.Ids is null)
            {
                results.Add(new DbcReferenceResult(reference, DbcReferenceStatus.Skipped, 0, 0, 0, [], dbc.Reason));
                continue;
            }

            string column = open + reference.Column + close;
            string where = $"{column} > 0" + (reference.Where is null ? string.Empty : " AND " + reference.Where);
            Dictionary<long, long> counts;
            try
            {
                counts = await CountAsync(connection, $"SELECT {column}, COUNT(*) FROM {open}{reference.Table}{close} WHERE {where} GROUP BY {column}", cancellationToken).ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                results.Add(new DbcReferenceResult(reference, DbcReferenceStatus.Skipped, 0, 0, 0, [], "not in this database: " + FirstLine(ex.Message)));
                continue;
            }

            long[] dangling = [.. counts.Keys.Where(id => !dbc.Ids.Contains(id)).Order()];
            results.Add(new DbcReferenceResult(
                reference, dangling.Length == 0 ? DbcReferenceStatus.Ok : DbcReferenceStatus.Dangling, counts.Count, dangling.Length,
                dangling.Sum(id => counts[id]), [.. dangling.Take(5)], null));
        }

        return results;
    }

    /// <summary>
    /// The report lines: a summary headed <paramref name="title"/>, then each dangling, known-gap or skipped reference (ok ones
    /// only with <paramref name="all"/>). Known client-data gaps are counted apart from dangling ids and named only when present.
    /// </summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<DbcReferenceResult> results, bool all = false, string title = "DBC cross-references")
    {
        ArgumentNullException.ThrowIfNull(results);
        CultureInfo c = CultureInfo.InvariantCulture;
        DbcReferenceResult[] dangling = [.. results.Where(r => r.Status == DbcReferenceStatus.Dangling)];
        DbcReferenceResult[] known = [.. results.Where(r => r.Status == DbcReferenceStatus.KnownClientGap)];
        string summary = string.Create(c, $"{title}: {results.Count} checked, {results.Count(r => r.Status == DbcReferenceStatus.Ok)} ok, "
            + $"{dangling.Length} with dangling ids ({dangling.Sum(r => r.DanglingIds)} ids, {dangling.Sum(r => r.DanglingRows)} rows), "
            + $"{results.Count(r => r.Status == DbcReferenceStatus.Skipped)} skipped");
        if (known.Length > 0)
        {
            summary += string.Create(c, $", {known.Length} known client-data gaps ({known.Sum(r => r.DanglingIds)} ids, {known.Sum(r => r.DanglingRows)} rows)");
        }

        var lines = new List<string> { summary };
        lines.AddRange(results.Where(r => all || r.Status != DbcReferenceStatus.Ok).Select(r => r.Describe()));
        return lines;
    }

    private static async Task<Dictionary<long, long>> CountAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var counts = new Dictionary<long, long>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
