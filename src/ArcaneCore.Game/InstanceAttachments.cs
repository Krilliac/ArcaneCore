using System.Runtime.CompilerServices;

namespace ArcaneCore.Game;

/// <summary>
/// A value attached to a long-lived instance (a spell system, a world) without the instance knowing it: a thin wrapper over one
/// <see cref="ConditionalWeakTable{TKey, TValue}"/>, so the entry dies with its key. Each lane owns its own static table; what a missing
/// entry means (create it, throw, do nothing) stays with the caller. Thread affinity: as the underlying table (thread-safe).
/// </summary>
internal sealed class InstanceAttachments<TKey, TValue>
    where TKey : class
    where TValue : class
{
    private readonly ConditionalWeakTable<TKey, TValue> _table = [];

    /// <summary>The value of <paramref name="key"/>, created by <paramref name="create"/> on first use.</summary>
    public TValue GetOrCreate(TKey key, Func<TKey, TValue> create)
        => _table.TryGetValue(key, out TValue? value) ? value : _table.GetValue(key, new ConditionalWeakTable<TKey, TValue>.CreateValueCallback(create));

    /// <summary>Set the value of <paramref name="key"/>, replacing an earlier one.</summary>
    public void Attach(TKey key, TValue value) => _table.AddOrUpdate(key, value);

    /// <summary>The value of <paramref name="key"/>, or null when none is attached.</summary>
    public TValue? Find(TKey key) => _table.TryGetValue(key, out TValue? value) ? value : null;

    /// <summary>Drop the value of <paramref name="key"/>, if any.</summary>
    public void Remove(TKey key) => _table.Remove(key);
}
