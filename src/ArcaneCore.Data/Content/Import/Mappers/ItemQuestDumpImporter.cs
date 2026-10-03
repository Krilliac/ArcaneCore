using System.Globalization;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>How a quest's <c>RewXP</c> is filled when the source has no such column.</summary>
public enum QuestXpSource
{
    /// <summary>
    /// cmangos classic-db z2815 has no <c>RewXP</c>: the server derives the XP from
    /// <c>RewMoneyMaxLevel</c> and the quest level (<c>Quest::XPValue</c>). The importer stores that
    /// full XP (the value at the quest's own level range) in <c>RewXP</c>.
    /// </summary>
    Derived = 0,

    /// <summary>Leave <c>RewXP</c> at 0: quests from a source without <c>RewXP</c> give no experience.</summary>
    None = 1,
}

/// <summary>What an item/quest import read and wrote.</summary>
public sealed record ItemQuestImportReport(
    int Items,
    int Quests,
    int QuestStarters,
    int QuestEnders,
    int StartingItems,
    int SkippedRows,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Quests whose <c>RewXP</c> was derived from <c>RewMoneyMaxLevel</c> (source without <c>RewXP</c>).</summary>
    public int DerivedQuestXp { get; init; }
}

/// <summary>
/// Maps the item and quest tables of a cmangos classic-db or vmangos world dump into the item
/// and quest schema by column <b>name</b>: <c>item_template</c>, <c>quest_template</c>,
/// <c>creature_questrelation</c>, <c>creature_involvedrelation</c> and
/// <c>playercreateinfo_item</c> (the starting outfit). The rows are the existing
/// <see cref="ItemTemplateRow"/>, <see cref="QuestTemplate"/> and relation rows; the dumps are
/// GPL data and are never committed.
/// <para>
/// vmangos rows are patch-versioned: for <c>item_template</c> and <c>quest_template</c> the row with
/// the highest <c>patch</c> not above <see cref="MaxPatch"/> is used (vmangos
/// <c>ObjectMgr::LoadItemPrototypes</c> :3815-3821 and <c>LoadQuests</c> :5523, both
/// <c>WHERE patch = max(patch) … patch &lt;= WowPatch</c>); the relation tables apply
/// <c>patch_min..patch_max</c> (<c>LoadQuestRelationsHelper</c> :9172-9178). A relation to a quest that
/// does not exist is skipped with a warning, as vmangos does (:9199-9203); a relation to a missing
/// creature is kept, as vmangos only logs it (:9249-9261) — <c>verify</c> counts those.
/// </para>
/// <para>
/// Quest XP. cmangos has no <c>RewXP</c> column; <c>Quest::XPValue</c> (mangos-classic
/// src/game/Quests/QuestDef.cpp:171-205) derives the XP from <c>RewMoneyMaxLevel</c>: divided by
/// 0.6 for quest levels 1-60, 1.2 (61), 2.4 (62), 3.6 (63), 4.8 (64) and 6.0 for 65 and above, where the
/// quest level is cast to <c>uint32</c> (so -1 counts as above 65); level 0 gives none. With
/// <see cref="QuestXp"/> = <see cref="QuestXpSource.Derived"/> the importer stores
/// <c>ceil(full XP)</c> in <c>RewXP</c>; the game then applies the grey-level reduction to that
/// integer, so a reduced reward can differ from cmangos' float-then-ceil by 1 XP. Sources with a
/// <c>RewXP</c> column (vmangos) are never derived.
/// </para>
/// </summary>
public sealed class ItemQuestDumpImporter
{
    /// <summary>vmangos WowPatch for 1.12.1 (shared with the other dump importers).</summary>
    public const int MaxPatch = CreatureDumpImporter.MaxPatch;

    // cmangos column names that differ from the row's (the rest match by normalized name).
    private static readonly RowMapper<ItemTemplateRow> s_itemMapper = new(new Dictionary<string, string>
    {
        ["RangedModRange"] = nameof(ItemTemplateRow.RangeMod),
        ["itemset"] = nameof(ItemTemplateRow.SetId),
        ["LanguageID"] = nameof(ItemTemplateRow.PageLanguage),
        ["area"] = nameof(ItemTemplateRow.AreaBound),
        ["Map"] = nameof(ItemTemplateRow.MapBound),
    },
        // Material is signed in the source (-1 = consumable); cmangos stores it in a uint32 and sends it as such.
        signedAsUnsigned: [nameof(ItemTemplateRow.Material)]);

    private static readonly RowMapper<QuestTemplate> s_questMapper = new();
    private static readonly RowMapper<PlayerCreateInfoItemRow> s_startItemMapper = new();

    private readonly Dictionary<uint, (int Patch, ItemTemplateRow Row)> _items = [];
    private readonly Dictionary<uint, (int Patch, QuestTemplate Row, bool Derived)> _quests = [];
    private readonly HashSet<(uint, uint)> _starters = [];
    private readonly HashSet<(uint, uint)> _enders = [];
    private readonly Dictionary<(byte, byte, uint), PlayerCreateInfoItemRow> _startItems = [];
    private readonly MapDiagnostics _diagnostics = new();
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, (IReadOnlyList<string> Columns, int[] Key)> _keyCache = new(StringComparer.OrdinalIgnoreCase);
    private int _skipped;

    /// <summary>Whether an <c>item_template</c> column is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsItemColumn(string column) => s_itemMapper.Maps(column) || column.Equals("patch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a <c>quest_template</c> column is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsQuestColumn(string column) => s_questMapper.Maps(column) || column.Equals("patch", StringComparison.OrdinalIgnoreCase);

    /// <summary>How <c>RewXP</c> is filled for sources without that column.</summary>
    public QuestXpSource QuestXp { get; set; } = QuestXpSource.Derived;

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var reader = new MySqlDumpReader(dump);
        foreach (object item in reader.Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case "item_template":
                    ReadItem(row);
                    break;
                case "quest_template":
                    ReadQuest(row);
                    break;
                case "creature_questrelation":
                    ReadRelation(row, _starters);
                    break;
                case "creature_involvedrelation":
                    ReadRelation(row, _enders);
                    break;
                case "playercreateinfo_item":
                    ReadStartItem(row);
                    break;
            }
        }
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<ItemTemplateRow> Items, IReadOnlyCollection<QuestTemplate> Quests,
        IReadOnlyCollection<CreatureQuestStarterRow> Starters, IReadOnlyCollection<CreatureQuestEnderRow> Enders,
        IReadOnlyCollection<PlayerCreateInfoItemRow> StartingItems) Snapshot()
    {
        Relations relations = ResolveRelations();
        return ([.. _items.Values.Select(i => i.Row)], [.. _quests.Values.Select(q => q.Row)],
            [.. relations.Starters.Select(r => new CreatureQuestStarterRow { Id = r.Item1, Quest = r.Item2 })],
            [.. relations.Enders.Select(r => new CreatureQuestEnderRow { Id = r.Item1, Quest = r.Item2 })],
            [.. _startItems.Values]);
    }

    public ItemQuestImportReport BuildReport()
    {
        Relations relations = ResolveRelations();
        var warnings = new List<string>(_warnings);
        warnings.AddRange(_diagnostics.Samples);
        warnings.AddRange(relations.Warnings);
        return new ItemQuestImportReport(
            _items.Count, _quests.Count, relations.Starters.Count, relations.Enders.Count, _startItems.Count,
            _skipped + relations.Dropped, warnings)
        {
            DerivedQuestXp = _quests.Values.Count(q => q.Derived),
        };
    }

    /// <summary>
    /// Write everything read so far atomically, with the same transaction contract as
    /// <see cref="CreatureDumpImporter.WriteAsync"/> (see <see cref="ImportTransaction"/>). With
    /// <paramref name="replace"/> the five tables are emptied first; without it a key that already
    /// exists fails the whole write and nothing changes.
    /// </summary>
    public async Task<ItemQuestImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var snapshot = Snapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<PlayerCreateInfoItemRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<CreatureQuestEnderRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<CreatureQuestStarterRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<QuestTemplate>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<ItemTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.Items, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Quests, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Starters, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.Enders, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.StartingItems, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    // --- rows ------------------------------------------------------------------------------------

    private void ReadItem(DumpRow row)
    {
        uint[] key = KeyOf(row, "item_template");
        int patch = PatchOf(row);
        if (patch > MaxPatch)
        {
            _skipped++;
            return;
        }

        if (_items.TryGetValue(key[0], out var existing) && existing.Patch > patch)
        {
            _skipped++;
            return;
        }

        ItemTemplateRow item = s_itemMapper.Map(row, _diagnostics);
        _items[item.Entry] = (patch, item);
    }

    private void ReadQuest(DumpRow row)
    {
        uint[] key = KeyOf(row, "quest_template");
        int patch = PatchOf(row);
        if (patch > MaxPatch)
        {
            _skipped++;
            return;
        }

        if (_quests.TryGetValue(key[0], out var existing) && existing.Patch > patch)
        {
            _skipped++;
            return;
        }

        QuestTemplate quest = s_questMapper.Map(row, _diagnostics);
        bool derived = false;
        if (QuestXp == QuestXpSource.Derived && !row.Has("RewXP"))
        {
            uint xp = DerivedRewXp(quest.QuestLevel, quest.RewMoneyMaxLevel);
            if (xp != 0)
            {
                quest = ReplaceRewXp(quest, xp);
                derived = true;
            }
        }

        _quests[quest.Entry] = (patch, quest, derived);
    }

    private void ReadRelation(DumpRow row, HashSet<(uint, uint)> into)
    {
        string table = row.Table.ToLowerInvariant();
        uint[] key = KeyOf(row, table);
        if (row.Has("patch_min"))
        {
            // vmangos LoadQuestRelationsHelper: WHERE WowPatch BETWEEN patch_min AND patch_max.
            int min = Int(row, "patch_min");
            int max = row.Has("patch_max") ? Int(row, "patch_max") : MaxPatch;
            if (MaxPatch < min || MaxPatch > max)
            {
                _skipped++;
                return;
            }
        }

        into.Add((key[0], key[1]));
    }

    private void ReadStartItem(DumpRow row)
    {
        _ = KeyOf(row, "playercreateinfo_item");
        PlayerCreateInfoItemRow item = s_startItemMapper.Map(row, _diagnostics);
        _startItems[(item.Race, item.Class, item.ItemId)] = item;
    }

    // --- helpers -----------------------------------------------------------------------------------

    private readonly record struct Relations(
        IReadOnlyList<(uint, uint)> Starters, IReadOnlyList<(uint, uint)> Enders, int Dropped, IReadOnlyList<string> Warnings);

    /// <summary>The relations whose quest exists (vmangos skips the others), with a warning for each skipped one.</summary>
    private Relations ResolveRelations()
    {
        var warnings = new List<string>();
        int dropped = 0;

        List<(uint, uint)> Filter(HashSet<(uint, uint)> source, string table)
        {
            var kept = new List<(uint, uint)>();
            foreach ((uint id, uint quest) in source.Order())
            {
                if (_quests.ContainsKey(quest))
                {
                    kept.Add((id, quest));
                    continue;
                }

                dropped++;
                if (warnings.Count < 1000)
                {
                    warnings.Add($"{table}: quest {quest} listed for entry {id} does not exist; skipped");
                }
            }

            return kept;
        }

        List<(uint, uint)> starters = Filter(_starters, "creature_questrelation");
        List<(uint, uint)> enders = Filter(_enders, "creature_involvedrelation");
        return new Relations(starters, enders, dropped, warnings);
    }

    /// <summary>The table's key values of a row; the key columns must exist (a missing one is a schema error, never 0).</summary>
    private uint[] KeyOf(DumpRow row, string table)
    {
        if (!_keyCache.TryGetValue(table, out var cached) || !ReferenceEquals(cached.Columns, row.Columns))
        {
            cached = (row.Columns, ContentTableSpecs.Find(table)!.ResolveKey(row.Columns));
            _keyCache[table] = cached;
        }

        var key = new uint[cached.Key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            string? raw = cached.Key[i] < row.Values.Count ? row.Values[cached.Key[i]] : null;
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out key[i]))
            {
                throw new ImportSchemaException(
                    table, ContentTableSpecs.Find(table)!.Keys[i].Canonical,
                    $"table `{table}`: key value '{raw ?? "NULL"}' of `{ContentTableSpecs.Find(table)!.Keys[i].Canonical}` is not an unsigned number");
            }
        }

        return key;
    }

    private static int PatchOf(DumpRow row) => row.Has("patch") ? Int(row, "patch") : 0;

    private static int Int(DumpRow row, string column)
        => row.TryGet(out string? raw, column) && !string.IsNullOrEmpty(raw)
            ? (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>mangos-classic Quest::XPValue (QuestDef.cpp:171-205), the full-XP term: RewMoneyMaxLevel / divisor, ceil.</summary>
    internal static uint DerivedRewXp(int questLevel, uint rewMoneyMaxLevel)
    {
        if (rewMoneyMaxLevel == 0)
        {
            return 0;
        }

        // uint32 qLevel = QuestLevel: a negative level wraps to above 65.
        uint qLevel = unchecked((uint)questLevel);
        float money = rewMoneyMaxLevel;
        float fullXp;
        if (qLevel >= 65)
        {
            fullXp = money / 6.0f;
        }
        else if (qLevel == 64)
        {
            fullXp = money / 4.8f;
        }
        else if (qLevel == 63)
        {
            fullXp = money / 3.6f;
        }
        else if (qLevel == 62)
        {
            fullXp = money / 2.4f;
        }
        else if (qLevel == 61)
        {
            fullXp = money / 1.2f;
        }
        else if (qLevel is > 0 and <= 60)
        {
            fullXp = money / 0.6f;
        }
        else
        {
            fullXp = 0;
        }

        return (uint)MathF.Ceiling(fullXp);
    }

    // QuestTemplate's properties are init-only; copy it with the derived value.
    private static QuestTemplate ReplaceRewXp(QuestTemplate quest, uint xp)
    {
        System.Reflection.PropertyInfo property = typeof(QuestTemplate).GetProperty(nameof(QuestTemplate.RewXP))!;
        property.SetValue(quest, xp);
        return quest;
    }
}
