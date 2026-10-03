using System.Collections.Frozen;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>Read-only item content (thread-safe: immutable once built).</summary>
public interface IItemTemplateStore
{
    ItemTemplate? Find(uint entry);

    /// <summary>playercreateinfo_item rows of one race/class, in table (key) order.</summary>
    IReadOnlyList<StartingItem> StartingItems(byte race, byte cls);

    int Count { get; }

    /// <summary>
    /// vmangos ObjectMgr::GetQuestStartingItemID (ObjectMgr.cpp:4224-4225, 6248-6256): the first item (lowest
    /// entry) whose <c>startquest</c> is <paramref name="questId"/>, or 0.
    /// </summary>
    uint QuestStartingItem(uint questId) => 0;
}

/// <summary>An immutable in-memory <see cref="IItemTemplateStore"/> (ROADMAP: static content lives in memory).</summary>
public sealed class ItemTemplateStore : IItemTemplateStore
{
    private readonly FrozenDictionary<uint, ItemTemplate> _templates;
    private readonly FrozenDictionary<(byte, byte), StartingItem[]> _startingItems;
    private readonly FrozenDictionary<uint, uint> _questStartingItems;

    public ItemTemplateStore(IEnumerable<ItemTemplate> templates, IEnumerable<StartingItem>? startingItems = null)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = templates.ToFrozenDictionary(t => t.Entry, t => t.Normalized());
        _questStartingItems = _templates.Values
            .Where(t => t.StartQuest != 0)
            .OrderBy(t => t.Entry)
            .GroupBy(t => t.StartQuest)
            .ToFrozenDictionary(g => g.Key, g => g.First().Entry);
        _startingItems = (startingItems ?? [])
            .GroupBy(s => (s.Race, s.Class))
            .ToFrozenDictionary(g => g.Key, g => g.ToArray());
    }

    public static ItemTemplateStore Empty { get; } = new([]);

    public int Count => _templates.Count;

    /// <summary>Every template (GM lookups and name searches; unordered, sort by entry where order matters).</summary>
    public IEnumerable<ItemTemplate> All => _templates.Values;

    public ItemTemplate? Find(uint entry) => _templates.GetValueOrDefault(entry);

    public uint QuestStartingItem(uint questId) => _questStartingItems.GetValueOrDefault(questId);

    public IReadOnlyList<StartingItem> StartingItems(byte race, byte cls)
        => _startingItems.TryGetValue((race, cls), out StartingItem[]? items) ? items : [];
}

/// <summary>
/// Hands out realm-unique item GUID counters (vmangos ObjectMgr::GenerateItemLowGuid, seeded
/// from MAX(item_instance.guid)). Thread-safe: character creation runs on session tasks and
/// splits run on the world thread.
/// </summary>
public sealed class ItemGuidAllocator
{
    private long _last;

    public ItemGuidAllocator(uint lastUsed = 0) => _last = lastUsed;

    /// <summary>Raise the counter to at least <paramref name="lastUsed"/> (never lowers it).</summary>
    public void Seed(uint lastUsed)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _last);
            if (lastUsed <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _last, lastUsed, current) != current);
    }

    public uint Next()
    {
        long next = Interlocked.Increment(ref _last);
        if (next > uint.MaxValue)
        {
            // Fail closed rather than reuse GUIDs (vmangos stops the world on overflow).
            throw new InvalidOperationException("item GUID space exhausted");
        }

        return (uint)next;
    }
}
