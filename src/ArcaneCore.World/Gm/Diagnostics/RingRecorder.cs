namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// Bounded in-memory history of recent events for the targets a GM opted in to record (one ring per
/// <typeparamref name="TKey"/>: a session, a player, a bot...), read back on demand by a command instead of flooding chat.
/// Off by default and zero cost while no target is enabled: <see cref="Record"/> is then one volatile read. Each ring has a
/// fixed capacity chosen at <see cref="Enable"/> time; when full, the oldest entry is overwritten and counted in
/// <see cref="Overwritten"/>. The number of rings and the capacity of each are capped (<see cref="MaxTargets"/>,
/// <see cref="MaxCapacity"/>), so a GM cannot make the recorder grow without bound.
/// <para>
/// Thread safety: every member may be called from any thread (net, map and world threads record). Active recording takes one
/// short lock per entry; callers on a hot path check <see cref="IsActive"/> (or <see cref="IsRecording"/>) before building the
/// entry. Entries are copied in, so a struct <typeparamref name="TEntry"/> costs no allocation after <see cref="Enable"/>.
/// </para>
/// </summary>
public sealed class RingRecorder<TKey, TEntry>(int maxTargets = RingRecorder<TKey, TEntry>.DefaultMaxTargets, int maxCapacity = RingRecorder<TKey, TEntry>.DefaultMaxCapacity)
    where TKey : notnull
{
    /// <summary>Default cap on simultaneously recorded targets.</summary>
    public const int DefaultMaxTargets = 64;

    /// <summary>Default cap on one ring's capacity.</summary>
    public const int DefaultMaxCapacity = 4096;

    private sealed class Ring(int capacity)
    {
        public readonly TEntry[] Items = new TEntry[capacity];
        public int Next;
        public int Count;
        public long Overwritten;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, Ring> _rings = [];
    private int _active;

    /// <summary>The cap on simultaneously recorded targets.</summary>
    public int MaxTargets { get; } = maxTargets > 0 ? maxTargets : throw new ArgumentOutOfRangeException(nameof(maxTargets));

    /// <summary>The cap on one ring's capacity.</summary>
    public int MaxCapacity { get; } = maxCapacity > 0 ? maxCapacity : throw new ArgumentOutOfRangeException(nameof(maxCapacity));

    /// <summary>True while at least one target is enabled: the cheap pre-check for a hot path.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>The number of enabled targets.</summary>
    public int TargetCount
    {
        get
        {
            lock (_gate)
            {
                return _rings.Count;
            }
        }
    }

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
            if (_rings.TryGetValue(key, out Ring? old))
            {
                if (old.Items.Length == capacity)
                {
                    return true;
                }

                var resized = new Ring(capacity);
                TEntry[] kept = Copy(old);
                foreach (TEntry item in kept.Skip(Math.Max(0, kept.Length - capacity)))
                {
                    Push(resized, item);
                }

                resized.Overwritten = old.Overwritten + Math.Max(0, kept.Length - capacity);
                _rings[key] = resized;
                return true;
            }

            if (_rings.Count >= MaxTargets)
            {
                return false;
            }

            _rings[key] = new Ring(capacity);
            Volatile.Write(ref _active, 1);
            return true;
        }
    }

    /// <summary>Stop recording <paramref name="key"/> and free its ring. False when it was not enabled.</summary>
    public bool Disable(TKey key)
    {
        lock (_gate)
        {
            bool removed = _rings.Remove(key);
            if (_rings.Count == 0)
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
    public bool IsRecording(TKey key)
    {
        if (!IsActive)
        {
            return false;
        }

        lock (_gate)
        {
            return _rings.ContainsKey(key);
        }
    }

    /// <summary>Add <paramref name="entry"/> to <paramref name="key"/>'s ring. A no-op (one volatile read) when nothing is enabled or the key is not.</summary>
    public void Record(TKey key, in TEntry entry)
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_rings.TryGetValue(key, out Ring? ring))
            {
                Push(ring, entry);
            }
        }
    }

    /// <summary>The recorded entries of <paramref name="key"/>, oldest first (empty when it is not enabled).</summary>
    public TEntry[] Snapshot(TKey key)
    {
        lock (_gate)
        {
            return _rings.TryGetValue(key, out Ring? ring) ? Copy(ring) : [];
        }
    }

    /// <summary>How many entries of <paramref name="key"/> were overwritten because its ring was full (0 when not enabled).</summary>
    public long Overwritten(TKey key)
    {
        lock (_gate)
        {
            return _rings.TryGetValue(key, out Ring? ring) ? ring.Overwritten : 0;
        }
    }

    /// <summary>Drop the recorded entries of <paramref name="key"/> but keep recording. False when it is not enabled.</summary>
    public bool Clear(TKey key)
    {
        lock (_gate)
        {
            if (!_rings.TryGetValue(key, out Ring? ring))
            {
                return false;
            }

            Array.Clear(ring.Items);
            ring.Next = 0;
            ring.Count = 0;
            ring.Overwritten = 0;
            return true;
        }
    }

    /// <summary>The enabled keys and the capacity of each, for a status listing.</summary>
    public IReadOnlyList<(TKey Key, int Capacity, int Count)> Targets()
    {
        lock (_gate)
        {
            return [.. _rings.Select(r => (r.Key, r.Value.Items.Length, r.Value.Count))];
        }
    }

    private static void Push(Ring ring, TEntry entry)
    {
        ring.Items[ring.Next] = entry;
        ring.Next = (ring.Next + 1) % ring.Items.Length;
        if (ring.Count < ring.Items.Length)
        {
            ring.Count++;
        }
        else
        {
            ring.Overwritten++;
        }
    }

    private static TEntry[] Copy(Ring ring)
    {
        var result = new TEntry[ring.Count];
        int start = ring.Count < ring.Items.Length ? 0 : ring.Next;
        for (int i = 0; i < ring.Count; i++)
        {
            result[i] = ring.Items[(start + i) % ring.Items.Length];
        }

        return result;
    }
}
