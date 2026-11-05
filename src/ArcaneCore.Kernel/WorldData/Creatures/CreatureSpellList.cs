namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// Flags of one creature spell-list entry. Same bit values as the cmangos <c>creature_spell_list.Flags</c> column
/// (mangos-classic Entities/CreatureSpellList.h <c>SpellListFlags</c>, doc/SpellLists.txt).
/// </summary>
[Flags]
public enum CreatureSpellListFlags : uint
{
    None = 0,

    /// <summary>Gated by the list's support-action chance at each tick.</summary>
    SupportAction = 1,

    /// <summary>Gated by the list's ranged-attack chance (minus 50 while the creature fights in melee mode); the creature's main ranged spell.</summary>
    RangedAction = 2,

    /// <summary>
    /// The cast imposes the spell's category cooldown rather than its own. Metadata for the host: the scheduler never reads this flag (it runs
    /// one repeat timer per entry either way); the host reads it from the entry when it applies the spell system cooldown after a cast.
    /// </summary>
    CategoryCooldown = 4,

    /// <summary>Cast in addition to the one blocking spell of a tick, outside the roll for it.</summary>
    NonBlocking = 8,
}

/// <summary>
/// The hardcoded target ids a spell-list entry can name (cmangos <c>SpellListTargetingHardcoded</c>, ids below 100 are reserved for these).
/// The host that runs a list resolves them; the scheduler only passes the id through.
/// </summary>
public static class CreatureSpellListTargets
{
    public const uint None = 0;
    public const uint Current = 1;
    public const uint SelfTarget = 2;
    public const uint DispellableFriendly = 3;
    public const uint DispellableFriendlyNoSelf = 4;
    public const uint FriendlyMissingBuff = 5;
    public const uint FriendlyMissingBuffNoSelf = 6;
    public const uint CurrentNotAlone = 7;

    /// <summary>Target ids below this are the hardcoded ones above; ids from here on name a <see cref="CreatureSpellListTargeting"/> row.</summary>
    public const uint HardcodedMax = 100;
}

/// <summary>Kind of a targeting row (cmangos <c>SpellListTargetingType</c>).</summary>
public enum CreatureSpellListTargetingType : uint
{
    Hardcoded = 0,
    Attack = 1,
    Support = 2,
}

/// <summary>
/// One targeting row a spell-list entry can name by id (cmangos <c>CreatureSpellListTargeting</c>, the <c>creature_spell_targeting</c> table).
/// The scheduler never reads it; the host that resolves <see cref="CreatureSpellListEntry.TargetId"/> interprets the type, the parameters and the unit condition.
/// </summary>
public sealed record CreatureSpellListTargeting(uint Id, CreatureSpellListTargetingType Type, uint Param1, uint Param2, uint Param3, int UnitCondition);

/// <summary>
/// One spell of a creature spell list. Times are milliseconds. <see cref="Probability"/> is the weight when several entries are
/// eligible in one tick (all zero: the lowest position wins). <see cref="Availability"/> is the percentage chance that the entry is part
/// of the list for one assignment (spawn).
/// </summary>
public sealed record CreatureSpellListEntry(
    uint Position,
    uint SpellId,
    CreatureSpellListFlags Flags,
    uint TargetId,
    uint ScriptId,
    uint Availability,
    uint Probability,
    uint InitialMin,
    uint InitialMax,
    uint RepeatMin,
    uint RepeatMax)
{
    /// <summary>The entry is skipped by the AI scheduler (it stays on a charm bar).</summary>
    public bool DisabledForAI { get; init; }

    /// <summary>The combat condition id the entry is gated by (cmangos <c>CombatCondition</c>); 0 and -1 mean none.</summary>
    public int CombatCondition { get; init; }

    /// <summary>Whether <see cref="CombatCondition"/> names a condition (cmangos skips the check for 0 and -1).</summary>
    public bool HasCombatCondition => CombatCondition is not (0 or -1);
}

/// <summary>One spell list: the chances that gate its support and ranged entries, and its entries in position order.</summary>
public sealed class CreatureSpellList
{
    /// <summary>The scheduler tick, a 1200 ms loop (cmangos doc/SpellLists.txt: checked against retail).</summary>
    public const uint TickMs = 1200;

    /// <summary>Retail ranged-attack chance penalty while the creature fights in melee mode.</summary>
    public const uint MeleeModeRangedPenalty = 50;

    public CreatureSpellList(uint id, string name, uint chanceSupportAction, uint chanceRangedAttack, IEnumerable<CreatureSpellListEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entries);
        if (chanceSupportAction > 100) throw new ArgumentOutOfRangeException(nameof(chanceSupportAction), "A chance is a percentage.");
        if (chanceRangedAttack > 100) throw new ArgumentOutOfRangeException(nameof(chanceRangedAttack), "A chance is a percentage.");
        CreatureSpellListEntry[] ordered = [.. entries.OrderBy(e => e.Position)];
        for (int i = 0; i < ordered.Length; i++)
        {
            CreatureSpellListEntry e = ordered[i];
            if (i > 0 && ordered[i - 1].Position == e.Position)
                throw new ArgumentException($"Spell list {id} repeats position {e.Position}.", nameof(entries));
            if (e.SpellId == 0)
                throw new ArgumentException($"Spell list {id} position {e.Position} has no spell.", nameof(entries));
            if (e.InitialMin > e.InitialMax || e.RepeatMin > e.RepeatMax)
                throw new ArgumentException($"Spell list {id} position {e.Position} has a minimum above its maximum.", nameof(entries));
            if (e.Availability > 100)
                throw new ArgumentException($"Spell list {id} position {e.Position} has an availability above 100.", nameof(entries));
        }

        Id = id;
        Name = name;
        ChanceSupportAction = chanceSupportAction;
        ChanceRangedAttack = chanceRangedAttack;
        Entries = ordered;
    }

    public uint Id { get; }

    public string Name { get; }

    /// <summary>The whole list is switched off (cmangos <c>Disabled</c>, the compatibility flag for creature_template_spells): nothing of it is scheduled.</summary>
    public bool Disabled { get; init; }

    public uint ChanceSupportAction { get; }

    public uint ChanceRangedAttack { get; }

    /// <summary>The entries ordered by position (the priority order when every probability is zero).</summary>
    public IReadOnlyList<CreatureSpellListEntry> Entries { get; }
}

/// <summary>
/// Every creature spell list, immutable once built (the world thread reads it without locks; a reload builds a new catalog and swaps the reference).
/// </summary>
public sealed class CreatureSpellListCatalog
{
    public static readonly CreatureSpellListCatalog Empty = new([]);

    private readonly Dictionary<uint, CreatureSpellList> _lists;
    private readonly Dictionary<uint, CreatureSpellListTargeting> _targeting = [];

    /// <summary>Build the catalog. Throws when a list or targeting id repeats, a targeting row uses a hardcoded id, or an entry names a targeting id from 100 up that has no row.</summary>
    public CreatureSpellListCatalog(IEnumerable<CreatureSpellList> lists, IEnumerable<CreatureSpellListTargeting>? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(lists);
        foreach (CreatureSpellListTargeting row in targeting ?? [])
        {
            if (row.Id < CreatureSpellListTargets.HardcodedMax)
                throw new ArgumentException($"Targeting row {row.Id} uses an id reserved for the hardcoded targets.", nameof(targeting));
            if (!_targeting.TryAdd(row.Id, row))
                throw new ArgumentException($"Targeting row {row.Id} is defined twice.", nameof(targeting));
        }

        _lists = [];
        foreach (CreatureSpellList list in lists)
        {
            if (!_lists.TryAdd(list.Id, list))
                throw new ArgumentException($"Spell list {list.Id} is defined twice.", nameof(lists));
            foreach (CreatureSpellListEntry entry in list.Entries)
            {
                if (entry.TargetId >= CreatureSpellListTargets.HardcodedMax && !_targeting.ContainsKey(entry.TargetId))
                    throw new ArgumentException($"Spell list {list.Id} position {entry.Position} names missing targeting row {entry.TargetId}.", nameof(lists));
            }
        }
    }

    public int Count => _lists.Count;

    /// <summary>The targeting row with this id, or null (hardcoded ids below 100 have no row).</summary>
    public CreatureSpellListTargeting? FindTargeting(uint id) => _targeting.GetValueOrDefault(id);

    /// <summary>The list with this id, or null (an id of 0 is no list).</summary>
    public CreatureSpellList? Find(uint id) => id == 0 ? null : _lists.GetValueOrDefault(id);
}
