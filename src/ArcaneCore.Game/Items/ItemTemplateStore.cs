using System.Collections.Frozen;
using ArcaneCore.Kernel.Characters;
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
/// <para>
/// When configured with a <see cref="DurableIdBlockAllocator"/> (or reserved <see cref="IdRangeBlock"/>s),
/// <see cref="Next"/> consumes pre-reserved durable blocks without blocking the world thread on I/O,
/// triggers background low-watermark prefetching, and fails closed if no reserved IDs remain.
/// </para>
/// </summary>
public sealed class ItemGuidAllocator
{
    private long _last;
    private volatile DurableIdBlockAllocator? _blockAllocator;

    public ItemGuidAllocator(uint lastUsed = 0) => _last = lastUsed;

    public ItemGuidAllocator(DurableIdBlockAllocator blockAllocator)
    {
        ArgumentNullException.ThrowIfNull(blockAllocator);
        _blockAllocator = blockAllocator;
    }

    /// <summary>
    /// Optional durable block allocator backing this item GUID allocator when cluster block reservation is enabled.
    /// </summary>
    public DurableIdBlockAllocator? BlockAllocator => _blockAllocator;

    /// <summary>
    /// Remaining pre-reserved IDs when operating in durable block-reservation mode.
    /// </summary>
    public ulong RemainingReservedIds => _blockAllocator?.RemainingCount ?? 0;

    /// <summary>
    /// Attaches or replaces the <see cref="DurableIdBlockAllocator"/> used by <see cref="Next"/>.
    /// </summary>
    public void UseBlockAllocator(DurableIdBlockAllocator? blockAllocator)
    {
        if (blockAllocator is not null && !string.Equals(blockAllocator.Scope, IdRangeScopes.Item, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Block allocator scope must be '{IdRangeScopes.Item}', but was '{blockAllocator.Scope}'.",
                nameof(blockAllocator));
        }

        _blockAllocator = blockAllocator;
    }

    /// <summary>
    /// Configures durable block reservation with a store and optional block/watermark sizes.
    /// </summary>
    public DurableIdBlockAllocator ConfigureBlockReservation(
        IIdRangeReservationStore store,
        uint blockSize = 1024,
        uint lowWatermark = 256)
    {
        ArgumentNullException.ThrowIfNull(store);
        var allocator = new DurableIdBlockAllocator(IdRangeScopes.Item, store, blockSize, lowWatermark);
        _blockAllocator = allocator;
        return allocator;
    }

    /// <summary>
    /// Configures durable block reservation with an async reservation callback and optional block/watermark sizes.
    /// </summary>
    public DurableIdBlockAllocator ConfigureBlockReservation(
        Func<string, uint, CancellationToken, Task<IdRangeBlock>>? reserveBlockAsync = null,
        uint blockSize = 1024,
        uint lowWatermark = 256)
    {
        var allocator = new DurableIdBlockAllocator(IdRangeScopes.Item, blockSize, lowWatermark, reserveBlockAsync);
        _blockAllocator = allocator;
        return allocator;
    }

    /// <summary>
    /// Enqueues a reserved <see cref="IdRangeBlock"/> for <c>"item"</c> GUID allocation, enabling durable block mode if not already active.
    /// </summary>
    public void EnqueueBlock(IdRangeBlock block)
    {
        DurableIdBlockAllocator? allocator = _blockAllocator;
        if (allocator is null)
        {
            var created = new DurableIdBlockAllocator(IdRangeScopes.Item);
            allocator = Interlocked.CompareExchange(ref _blockAllocator, created, null) ?? created;
        }

        allocator.EnqueueBlock(block);
    }

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
        if (_blockAllocator is { } blockAllocator)
        {
            uint allocated = blockAllocator.Next();
            Seed(allocated);
            return allocated;
        }

        long next = Interlocked.Increment(ref _last);
        if (next > uint.MaxValue)
        {
            // Fail closed rather than reuse GUIDs (vmangos stops the world on overflow).
            throw new IdRangeExhaustedException(IdRangeScopes.Item, "item GUID space exhausted");
        }

        return (uint)next;
    }
}
