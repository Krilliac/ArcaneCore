namespace ArcaneCore.Game.Items;

/// <summary>
/// A stable <see cref="IItemTemplateStore"/> that forwards to whichever immutable store is current,
/// so everything that keeps a reference to it (an online player's inventory, the economy, the
/// vendors) sees a reload on its next lookup without being re-wired. Lookups are by entry, like
/// vmangos <c>ObjectMgr::GetItemPrototype</c>; an <see cref="Item"/> already created keeps the
/// record it was created with (docs/areas/hot-reload.md). Thread-safe: the swap is a single
/// volatile reference write.
/// </summary>
public sealed class LiveItemTemplateStore : IItemTemplateStore
{
    private volatile IItemTemplateStore _current = ItemTemplateStore.Empty;

    /// <summary>The store lookups currently go to.</summary>
    public IItemTemplateStore Current => _current;

    public int Count => _current.Count;

    public Kernel.Items.ItemTemplate? Find(uint entry) => _current.Find(entry);

    public IReadOnlyList<Kernel.Items.StartingItem> StartingItems(byte race, byte cls) => _current.StartingItems(race, cls);

    /// <summary>Make <paramref name="store"/> the one lookups go to.</summary>
    public void Replace(IItemTemplateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _current = store;
    }
}
