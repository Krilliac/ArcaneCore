using System.Collections.Concurrent;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// An always-on bounded history of the most recent <typeparamref name="TEntry"/> values (a session's recent opcodes, its ping samples).
/// The capacity is fixed at construction (<see cref="Resize"/> aside); when full the oldest entry is overwritten and counted in
/// <see cref="Overwritten"/>. Entries are copied in, so a struct <typeparamref name="TEntry"/> costs no allocation per write.
/// <para>
/// Thread safety: every member may be called from any thread (socket reader and gateway threads write while the world thread reads).
/// One <see cref="Lock"/> per ring guards the buffer; readers copy out under it, so a reader never sees a torn entry and rings
/// never contend with one another.
/// </para>
/// </summary>
public sealed class RingRecorder<TEntry>
{
    private readonly Lock _gate = new();
    private TEntry[] _items;
    private int _next;
    private int _count;
    private long _overwritten;

    /// <summary>A ring holding the newest <paramref name="capacity"/> entries.</summary>
    public RingRecorder(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _items = new TEntry[capacity];
    }

    /// <summary>The number of entries the ring holds.</summary>
    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _items.Length;
            }
        }
    }

    /// <summary>The number of entries currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The capacity and held count read under one lock, so the pair is consistent even while another thread resizes.</summary>
    public (int Capacity, int Count) Status()
    {
        lock (_gate)
        {
            return (_items.Length, _count);
        }
    }

    /// <summary>How many entries were overwritten because the ring was full.</summary>
    public long Overwritten
    {
        get
        {
            lock (_gate)
            {
                return _overwritten;
            }
        }
    }

    /// <summary>Append <paramref name="entry"/>, overwriting the oldest one when full. One short lock, no allocation.</summary>
    public void Add(in TEntry entry)
    {
        lock (_gate)
        {
            _items[_next] = entry;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length)
            {
                _count++;
            }
            else
            {
                _overwritten++;
            }
        }
    }

    /// <summary>The held entries, oldest first (a copy).</summary>
    public TEntry[] Snapshot()
    {
        lock (_gate)
        {
            var result = new TEntry[_count];
            int start = _count < _items.Length ? 0 : _next;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _items[(start + i) % _items.Length];
            }

            return result;
        }
    }

    /// <summary>Drop the held entries and reset <see cref="Overwritten"/>.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _next = 0;
            _count = 0;
            _overwritten = 0;
        }
    }

    /// <summary>Change the capacity, keeping the newest entries that fit; the ones that do not are counted as overwritten.</summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        lock (_gate)
        {
            if (_items.Length == capacity)
            {
                return;
            }

            int start = _count < _items.Length ? 0 : _next;
            int skip = Math.Max(0, _count - capacity);
            var resized = new TEntry[capacity];
            int kept = _count - skip;
            for (int i = 0; i < kept; i++)
            {
                resized[i] = _items[(start + skip + i) % _items.Length];
            }

            _items = resized;
            _overwritten += skip;
            _count = kept;
            _next = kept % capacity;
        }
    }
}

/// <summary>
/// A <see cref="RingRecorder{TEntry}"/> that exists only while a GM opted in: <see cref="Add"/> is one volatile read while off, so a
/// hot path may call it unconditionally. Enabling allocates the ring (the only allocation); disabling frees it.
/// <para>Thread safety: every member may be called from any thread. An <see cref="Add"/> racing a <see cref="Disable"/> may land in the ring that is being dropped, which is harmless.</para>
/// </summary>
public sealed class OptInRing<TEntry>(int maxCapacity = OptInRing<TEntry>.DefaultMaxCapacity)
{
    /// <summary>Default cap on the ring's capacity.</summary>
    public const int DefaultMaxCapacity = 4096;

    private RingRecorder<TEntry>? _ring;

    /// <summary>The cap on the ring's capacity.</summary>
    public int MaxCapacity { get; } = maxCapacity > 0 ? maxCapacity : throw new ArgumentOutOfRangeException(nameof(maxCapacity));

    /// <summary>True while recording: the cheap pre-check for a hot path.</summary>
    public bool IsEnabled => Volatile.Read(ref _ring) is not null;

    /// <summary>Start recording with room for <paramref name="capacity"/> entries (clamped to <see cref="MaxCapacity"/>); on an enabled ring this resizes it, keeping the newest entries.</summary>
    public void Enable(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        capacity = Math.Min(capacity, MaxCapacity);
        RingRecorder<TEntry>? current = Volatile.Read(ref _ring);
        if (current is not null)
        {
            current.Resize(capacity);
            return;
        }

        // Concurrent first Enable calls: the loser's ring is dropped and the winner is resized to the requested capacity.
        RingRecorder<TEntry> created = new(capacity);
        RingRecorder<TEntry>? winner = Interlocked.CompareExchange(ref _ring, created, null);
        winner?.Resize(capacity);
    }

    /// <summary>Stop recording and free the ring. False when it was off.</summary>
    public bool Disable() => Interlocked.Exchange(ref _ring, null) is not null;

    /// <summary>Append <paramref name="entry"/> when enabled; otherwise one volatile read and nothing else.</summary>
    public void Add(in TEntry entry) => Volatile.Read(ref _ring)?.Add(in entry);

    /// <summary>The held entries, oldest first (empty while off).</summary>
    public TEntry[] Snapshot() => Volatile.Read(ref _ring)?.Snapshot() ?? [];

    /// <summary>How many entries were overwritten because the ring was full (0 while off).</summary>
    public long Overwritten => Volatile.Read(ref _ring)?.Overwritten ?? 0;

    /// <summary>Drop the held entries but keep recording. False while off.</summary>
    public bool Clear()
    {
        RingRecorder<TEntry>? ring = Volatile.Read(ref _ring);
        ring?.Clear();
        return ring is not null;
    }

    /// <summary>The ring's capacity and held count, or null while off.</summary>
    public (int Capacity, int Count)? Status()
    {
        RingRecorder<TEntry>? ring = Volatile.Read(ref _ring);
        return ring?.Status();
    }
}

/// <summary>
/// Bounded in-memory history of recent events for the targets a GM opted in to record (one ring per
/// <typeparamref name="TKey"/>: a session, a player, a bot...), read back on demand by a command instead of flooding chat.
/// Off by default and zero cost while no target is enabled: <see cref="Record"/> is then one volatile read. Each ring has a
/// fixed capacity chosen at <see cref="Enable"/> time; when full, the oldest entry is overwritten and counted in
/// <see cref="Overwritten"/>. The number of rings and the capacity of each are capped (<see cref="MaxTargets"/>,
/// <see cref="MaxCapacity"/>), so a GM cannot make the recorder grow without bound.
/// <para>
/// Thread safety: every member may be called from any thread (net, map and world threads record). Looking a key up is lock-free
/// and each ring has its own lock, so recording a target that is not enabled takes no lock and two targets never contend. Only
/// <see cref="Enable"/> and <see cref="Disable"/> take the recorder-wide lock (they enforce <see cref="MaxTargets"/>). Callers on a
/// hot path check <see cref="IsActive"/> (or <see cref="IsRecording"/>) before building the entry.
/// </para>
/// </summary>
public sealed class RingRecorder<TKey, TEntry>(int maxTargets = RingRecorder<TKey, TEntry>.DefaultMaxTargets, int maxCapacity = RingRecorder<TKey, TEntry>.DefaultMaxCapacity)
    where TKey : notnull
{
    /// <summary>Default cap on simultaneously recorded targets.</summary>
    public const int DefaultMaxTargets = 64;

    /// <summary>Default cap on one ring's capacity.</summary>
    public const int DefaultMaxCapacity = 4096;

    private readonly Lock _gate = new(); // guards structural changes (Enable / Disable) so MaxTargets holds
    private readonly ConcurrentDictionary<TKey, RingRecorder<TEntry>> _rings = new();
    private int _active;

    /// <summary>The cap on simultaneously recorded targets.</summary>
    public int MaxTargets { get; } = maxTargets > 0 ? maxTargets : throw new ArgumentOutOfRangeException(nameof(maxTargets));

    /// <summary>The cap on one ring's capacity.</summary>
    public int MaxCapacity { get; } = maxCapacity > 0 ? maxCapacity : throw new ArgumentOutOfRangeException(nameof(maxCapacity));

    /// <summary>True while at least one target is enabled: the cheap pre-check for a hot path.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>The number of enabled targets.</summary>
    public int TargetCount => _rings.Count;

    /// <summary>
    /// Start recording <paramref name="key"/> with room for <paramref name="capacity"/> entries (clamped to <see cref="MaxCapacity"/>).
    /// Enabling an enabled target resizes it to the new capacity, keeping the newest entries that fit. False when
    /// <see cref="MaxTargets"/> other targets are already recorded.
    /// </summary>
    public bool Enable(TKey key, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        capacity = Math.Min(capacity, MaxCapacity);
        lock (_gate)
        {
            if (_rings.TryGetValue(key, out RingRecorder<TEntry>? old))
            {
                old.Resize(capacity);
                return true;
            }

            if (_rings.Count >= MaxTargets)
            {
                return false;
            }

            _rings[key] = new RingRecorder<TEntry>(capacity);
            Volatile.Write(ref _active, 1);
            return true;
        }
    }

    /// <summary>Stop recording <paramref name="key"/> and free its ring. False when it was not enabled.</summary>
    public bool Disable(TKey key)
    {
        lock (_gate)
        {
            bool removed = _rings.TryRemove(key, out _);
            if (_rings.IsEmpty)
            {
                Volatile.Write(ref _active, 0);
            }

            return removed;
        }
    }

    /// <summary>Stop recording every target.</summary>
    public void DisableAll()
    {
        lock (_gate)
        {
            _rings.Clear();
            Volatile.Write(ref _active, 0);
        }
    }

    /// <summary>Whether <paramref name="key"/> is enabled.</summary>
    public bool IsRecording(TKey key) => IsActive && _rings.ContainsKey(key);

    /// <summary>Add <paramref name="entry"/> to <paramref name="key"/>'s ring. A no-op (one volatile read) when nothing is enabled, and lock-free when the key is not.</summary>
    public void Record(TKey key, in TEntry entry)
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        if (_rings.TryGetValue(key, out RingRecorder<TEntry>? ring))
        {
            ring.Add(in entry);
        }
    }

    /// <summary>The recorded entries of <paramref name="key"/>, oldest first (empty when it is not enabled).</summary>
    public TEntry[] Snapshot(TKey key) => _rings.TryGetValue(key, out RingRecorder<TEntry>? ring) ? ring.Snapshot() : [];

    /// <summary>How many entries of <paramref name="key"/> were overwritten because its ring was full (0 when not enabled).</summary>
    public long Overwritten(TKey key) => _rings.TryGetValue(key, out RingRecorder<TEntry>? ring) ? ring.Overwritten : 0;

    /// <summary>Drop the recorded entries of <paramref name="key"/> but keep recording. False when it is not enabled.</summary>
    public bool Clear(TKey key)
    {
        if (!_rings.TryGetValue(key, out RingRecorder<TEntry>? ring))
        {
            return false;
        }

        ring.Clear();
        return true;
    }

    /// <summary>The enabled keys and the capacity and held count of each, for a status listing.</summary>
    public IReadOnlyList<(TKey Key, int Capacity, int Count)> Targets()
        => [.. _rings.Select(r => (r.Key, r.Value.Capacity, r.Value.Count))];
}
