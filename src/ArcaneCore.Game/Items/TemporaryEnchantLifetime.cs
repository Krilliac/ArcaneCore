namespace ArcaneCore.Game.Items;

/// <summary>
/// Tracks remaining online time for item enchantment slots. The world thread owns an instance;
/// callers register a non-zero-duration slot when an item enters the player's inventory and
/// unregister it before the item leaves. This deliberately uses the enchantment duration word,
/// not <see cref="Item.Duration"/> (the timed-item lifetime).
/// </summary>
public sealed class TemporaryEnchantLifetime
{
    private readonly record struct Entry(uint EnchantmentId, uint Remaining);

    private readonly Dictionary<(Item Item, int Slot), Entry> _remaining = [];
    private readonly Action<Item, int>? _removeAppliedEffect;
    private readonly Action<Item, int>? _afterClear;
    private readonly HashSet<Item> _currentItems = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Item Item, int Slot)> _scratchKeys = [];
    private readonly List<KeyValuePair<(Item Item, int Slot), Entry>> _scratchEntries = [];

    /// <param name="removeAppliedEffect">
    /// Optional paired removal hook. It runs before the three enchantment fields are cleared at
    /// expiry, matching vmangos <c>ApplyEnchantment(..., false)</c> then <c>ClearEnchantment</c>.
    /// </param>
    public TemporaryEnchantLifetime(Action<Item, int>? removeAppliedEffect = null, Action<Item, int>? afterClear = null)
    {
        _removeAppliedEffect = removeAppliedEffect;
        _afterClear = afterClear;
    }

    public int Count => _remaining.Count;

    /// <summary>
    /// Register or refresh one slot. Zero duration is intentionally untimed; the usual permanent
    /// slot 0 therefore remains untracked because its duration word is zero.
    /// </summary>
    public void Register(Item item, int slot)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(item);

        uint duration = item.EnchantmentDuration(slot);
        if (item.EnchantmentId(slot) == 0 || duration == 0)
        {
            _remaining.Remove((item, slot));
            return;
        }

        _remaining[(item, slot)] = new(item.EnchantmentId(slot), duration);
    }

    /// <summary>
    /// Stop tracking a slot before moving or destroying its item, preserving its current remaining
    /// duration in the item fields for the next inventory/save operation.
    /// </summary>
    public void Unregister(Item item, int slot)
    {
        ValidateSlot(slot);
        ArgumentNullException.ThrowIfNull(item);

        if (_remaining.Remove((item, slot), out Entry entry) && item.EnchantmentId(slot) == entry.EnchantmentId)
        {
            item.SetUInt32(UpdateFields.ItemFieldEnchantment + slot * 3 + 1, entry.Remaining);
        }
    }

    /// <summary>Drop entries for items that are no longer owned by the inventory.</summary>
    public void Reconcile(IEnumerable<Item> currentItems)
    {
        ArgumentNullException.ThrowIfNull(currentItems);
        _currentItems.Clear();
        foreach (Item item in currentItems)
        {
            _currentItems.Add(item);
            for (int slot = 0; slot < Item.EnchantmentValues / 3; slot++)
            {
                var key = (item, slot);
                if (!_remaining.ContainsKey(key) && item.EnchantmentId(slot) != 0 && item.EnchantmentDuration(slot) != 0)
                    _remaining[key] = new(item.EnchantmentId(slot), item.EnchantmentDuration(slot));
            }
        }

        _scratchKeys.Clear();
        foreach (var key in _remaining.Keys)
        {
            if (!_currentItems.Contains(key.Item))
                _scratchKeys.Add((key.Item, key.Slot));
        }

        foreach ((Item item, int slot) key in _scratchKeys)
        {
            _remaining.Remove(key);
        }
    }

    /// <summary>Advance all tracked slots by online world time in milliseconds.</summary>
    public void Update(uint diffMs)
    {
        if (diffMs == 0 || _remaining.Count == 0)
            return;

        _scratchEntries.Clear();
        foreach (KeyValuePair<(Item Item, int Slot), Entry> pair in _remaining)
            _scratchEntries.Add(pair);

        foreach (((Item item, int slot), Entry entry) in _scratchEntries)
        {
            uint currentId = item.EnchantmentId(slot);
            uint currentDuration = item.EnchantmentDuration(slot);
            if (currentId == 0 || currentDuration == 0)
            {
                _remaining.Remove((item, slot));
                continue;
            }

            // A producer may replace or refresh the slot between ticks. Adopt the current
            // fields; never apply the old slot's remaining time to the new enchantment.
            if (currentId != entry.EnchantmentId || currentDuration != entry.Remaining)
            {
                _remaining[(item, slot)] = new(currentId, currentDuration);
                continue;
            }

            if (entry.Remaining <= diffMs)
            {
                _removeAppliedEffect?.Invoke(item, slot);
                ClearFields(item, slot);
                _afterClear?.Invoke(item, slot);
                _remaining.Remove((item, slot));
            }
            else
            {
                uint remaining = entry.Remaining - diffMs;
                _remaining[(item, slot)] = entry with { Remaining = remaining };
                item.SetUInt32(UpdateFields.ItemFieldEnchantment + slot * 3 + 1, remaining);
            }
        }
    }

    /// <summary>Copy live countdown values into item fields before a character snapshot is made.</summary>
    public void FlushToItems()
    {
        _scratchEntries.Clear();
        foreach (KeyValuePair<(Item Item, int Slot), Entry> pair in _remaining)
            _scratchEntries.Add(pair);

        foreach (((Item item, int slot), Entry entry) in _scratchEntries)
        {
            uint currentId = item.EnchantmentId(slot);
            uint currentDuration = item.EnchantmentDuration(slot);
            if (currentId == 0 || currentId != entry.EnchantmentId || currentDuration == 0)
            {
                _remaining.Remove((item, slot));
                continue;
            }

            // A same-ID refresh is a new producer write. Adopt it before serializing; never
            // restore the old remaining value over a valid changed duration.
            if (currentDuration != entry.Remaining)
            {
                _remaining[(item, slot)] = new(currentId, currentDuration);
            }
        }
    }

    private static void ClearFields(Item item, int slot)
    {
        int offset = UpdateFields.ItemFieldEnchantment + slot * 3;
        item.SetUInt32(offset, 0);
        item.SetUInt32(offset + 1, 0);
        item.SetUInt32(offset + 2, 0);
    }

    private static void ValidateSlot(int slot)
    {
        if ((uint)slot >= Item.EnchantmentValues / 3u)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "enchantment slot must be 0..6");
    }
}
