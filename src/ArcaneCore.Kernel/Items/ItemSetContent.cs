using System.Collections.Frozen;

namespace ArcaneCore.Kernel.Items;

/// <summary>
/// One ItemSet.dbc row (mangos ItemSetEntry, DBCStructure.h): the bonus spells of a set and how many worn pieces each needs.
/// Slot i of <see cref="SpellIds"/> pairs with slot i of <see cref="Thresholds"/>; an empty slot (spell 0) is unused.
/// Immutable; built once by the DBC reader.
/// </summary>
public sealed record ItemSetRecord
{
    /// <summary>ItemSet.dbc holds 8 bonus slots per set (m_setSpellID / m_setThreshold).</summary>
    public const int BonusSlots = 8;

    public ItemSetRecord(uint id, string name, IReadOnlyList<uint> spellIds, IReadOnlyList<uint> thresholds, uint requiredSkill, uint requiredSkillRank)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spellIds);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (spellIds.Count != BonusSlots || thresholds.Count != BonusSlots)
        {
            throw new ArgumentException($"an item set has exactly {BonusSlots} bonus slots");
        }

        Id = id;
        Name = name;
        SpellIds = [.. spellIds];
        Thresholds = [.. thresholds];
        RequiredSkill = requiredSkill;
        RequiredSkillRank = requiredSkillRank;
    }

    public uint Id { get; }

    /// <summary>The enUS name (informational; the client has its own copy).</summary>
    public string Name { get; }

    public IReadOnlyList<uint> SpellIds { get; }

    public IReadOnlyList<uint> Thresholds { get; }

    /// <summary>A skill the wearer needs for the set to count (0 = none).</summary>
    public uint RequiredSkill { get; }

    public uint RequiredSkillRank { get; }
}

/// <summary>The item sets of the client's ItemSet.dbc, by id. Immutable once built; empty when no file is configured.</summary>
public sealed class ItemSetCatalog
{
    private readonly FrozenDictionary<uint, ItemSetRecord> _sets;

    public ItemSetCatalog(IEnumerable<ItemSetRecord> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        _sets = sets.ToFrozenDictionary(s => s.Id);
    }

    public static ItemSetCatalog Empty { get; } = new([]);

    public int Count => _sets.Count;

    public ItemSetRecord? Find(uint id) => _sets.GetValueOrDefault(id);
}
