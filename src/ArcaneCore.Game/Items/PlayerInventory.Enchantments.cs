namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    private readonly TemporaryEnchantLifetime _enchantLifetime;

    private void OnEnchantExpired(Item item, int slot)
    {
        if (Player is { } player)
            EnchantmentSink?.ApplyEnchantment(player, item, slot, apply: false);
    }

    private void OnEnchantCleared(Item item, int slot)
        => RefreshVisibleEnchantment(item, slot);

    /// <summary>Advance owned enchantment lifetimes by online world milliseconds.</summary>
    public void UpdateEnchantDurations(uint diffMs)
    {
        if (!_loaded || diffMs == 0)
            return;

        _enchantLifetime.Reconcile(AllItems);
        _enchantLifetime.Update(diffMs);
    }

    /// <summary>Register restored or newly changed enchantment fields without resetting unrelated slots.</summary>
    public void RegisterEnchantDurations(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        for (int slot = 0; slot < Item.EnchantmentValues / 3; slot++)
            _enchantLifetime.Register(item, slot);
    }

    /// <summary>Flush live countdown values before inventory serialization.</summary>
    public void FlushEnchantDurations() => _enchantLifetime.FlushToItems();

    /// <summary>Expose the tracked-slot count for focused maintenance tests and diagnostics.</summary>
    public int TrackedEnchantDurationCount => _enchantLifetime.Count;
}
