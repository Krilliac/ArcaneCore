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

    /// <summary>The cooldown the cast imposes is the spell's category cooldown rather than its own.</summary>
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
}

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

    public CreatureSpellListCatalog(IEnumerable<CreatureSpellList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        _lists = [];
        foreach (CreatureSpellList list in lists)
        {
            if (!_lists.TryAdd(list.Id, list))
                throw new ArgumentException($"Spell list {list.Id} is defined twice.", nameof(lists));
        }
    }

    public int Count => _lists.Count;

    /// <summary>The list with this id, or null (an id of 0 is no list).</summary>
    public CreatureSpellList? Find(uint id) => id == 0 ? null : _lists.GetValueOrDefault(id);
}
