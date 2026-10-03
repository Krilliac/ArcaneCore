namespace ArcaneCore.Kernel.WorldData.Loot;

/// <summary>The loot tables of the world database (vmangos LootMgr.cpp LootTemplates_*).</summary>
public enum LootTableKind : byte
{
    /// <summary><c>creature_loot_template</c> (keyed by creature_template lootid).</summary>
    Creature = 0,

    /// <summary><c>gameobject_loot_template</c> (keyed by chest data1 / fishing hole data1).</summary>
    GameObject = 1,

    /// <summary><c>item_loot_template</c> (keyed by item entry; lootable containers).</summary>
    Item = 2,

    /// <summary><c>skinning_loot_template</c> (keyed by creature_template skinloot).</summary>
    Skinning = 3,

    /// <summary><c>reference_loot_template</c> (shared sub-tables reached through negative mincountOrRef).</summary>
    Reference = 4,

    /// <summary><c>fishing_loot_template</c> (keyed by area id: a sub-zone, else its zone; entry 0 holds the failed-cast junk).</summary>
    Fishing = 5,

    /// <summary><c>pickpocketing_loot_template</c> (keyed by creature_template pickpocket loot id).</summary>
    Pickpocketing = 6,

    /// <summary><c>disenchant_loot_template</c> (keyed by item_template DisenchantID).</summary>
    Disenchant = 7,
}

/// <summary>
/// One loot table row (cmangos/vmangos <c>*_loot_template</c>: entry, item,
/// ChanceOrQuestChance, groupid, mincountOrRef, maxcount, condition_id).
/// </summary>
/// <param name="Entry">The table key (loot id).</param>
/// <param name="Item">Item entry; for a reference row, informational only.</param>
/// <param name="ChanceOrQuestChance">Percent chance; negative marks a quest item (its absolute value is the chance).</param>
/// <param name="GroupId">0 = ungrouped; rows of one group yield at most one item.</param>
/// <param name="MinCountOrRef">Minimum count, or a negative reference_loot_template entry.</param>
/// <param name="MaxCount">Maximum count, or the number of times a reference is processed.</param>
/// <param name="ConditionId">conditions.condition_entry (unsupported here: a non-zero condition skips the row).</param>
public sealed record LootStoreRow(uint Entry, uint Item, float ChanceOrQuestChance, byte GroupId, int MinCountOrRef, uint MaxCount, uint ConditionId = 0);

/// <summary>
/// The creature loot columns of cmangos <c>creature_template</c> (LootId, SkinningLootId,
/// MinLootGold, MaxLootGold; vmangos loot_id, skinning_loot_id, gold_min, gold_max), kept in the
/// loot module's <c>creature_loot_info</c> table so the creatures schema is not edited.
/// </summary>
public sealed record CreatureLootInfo(uint Entry, uint LootId, uint SkinningLootId, uint MinGold, uint MaxGold);

/// <summary>All loot rows, grouped by table and entry; read-only after load.</summary>
public sealed class LootContent
{
    public static readonly LootContent Empty = new([], []);

    private readonly Dictionary<uint, int> _fishingBase;
    private readonly Dictionary<uint, uint> _pickpocket;

    private readonly Dictionary<(LootTableKind, uint), IReadOnlyList<LootStoreRow>> _rows;
    private readonly Dictionary<uint, CreatureLootInfo> _creatures;

    public LootContent(IEnumerable<(LootTableKind Kind, LootStoreRow Row)> rows, IEnumerable<CreatureLootInfo> creatures)
        : this(rows, creatures, [], [])
    {
    }

    /// <param name="fishingBaseSkills"><c>skill_fishing_base_level</c>: area id to the base skill the area needs (signed: -70 and -20 exist).</param>
    /// <param name="pickpocketLootIds"><c>creature_template</c> pickpocket loot id per creature entry.</param>
    public LootContent(
        IEnumerable<(LootTableKind Kind, LootStoreRow Row)> rows,
        IEnumerable<CreatureLootInfo> creatures,
        IEnumerable<KeyValuePair<uint, int>> fishingBaseSkills,
        IEnumerable<KeyValuePair<uint, uint>> pickpocketLootIds)
    {
        (LootTableKind Kind, LootStoreRow Row)[] all = [.. rows];
        RowCount = all.Length;
        _rows = all.GroupBy(r => (r.Kind, r.Row.Entry))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LootStoreRow>)[.. g.Select(r => r.Row).OrderBy(r => r.GroupId).ThenBy(r => r.Item)]);
        _creatures = creatures.ToDictionary(c => c.Entry);
        _fishingBase = fishingBaseSkills.ToDictionary(p => p.Key, p => p.Value);
        _pickpocket = pickpocketLootIds.ToDictionary(p => p.Key, p => p.Value);
    }

    public int RowCount { get; }

    public int CreatureInfoCount => _creatures.Count;

    /// <summary>Rows of one table entry, ordered by group then item (deterministic processing order).</summary>
    public IReadOnlyList<LootStoreRow> GetRows(LootTableKind kind, uint entry) => _rows.GetValueOrDefault((kind, entry)) ?? [];

    public bool HasEntry(LootTableKind kind, uint entry) => _rows.ContainsKey((kind, entry));

    public CreatureLootInfo? FindCreature(uint entry) => _creatures.GetValueOrDefault(entry);

    public int FishingBaseSkillCount => _fishingBase.Count;

    public int PickpocketCreatureCount => _pickpocket.Count;

    /// <summary>
    /// The base fishing skill of an area (<c>skill_fishing_base_level</c>); 0 when the area has no row,
    /// which vmangos treats as "missing" (a zero row is indistinguishable from none: GameObject.cpp:1657,
    /// ObjectMgr::GetFishingBaseSkillLevel). Signed.
    /// </summary>
    public int FishingBaseSkill(uint areaId) => _fishingBase.GetValueOrDefault(areaId);

    /// <summary>The creature's pickpocket loot id (<c>creature_template.pickpocket_loot_id</c>), 0 when it has none.</summary>
    public uint FindPickpocketLootId(uint creatureEntry) => _pickpocket.GetValueOrDefault(creatureEntry);
}

/// <summary>Loads the loot content from the world database.</summary>
public interface ILootDataStore
{
    Task<LootContent> LoadAsync(CancellationToken cancellationToken = default);
}
