using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>One notice as the ring keeps it: the notice's own immutable values plus a sequence number and the time it arrived.</summary>
internal readonly record struct AnomalySlot(
    long Seq,
    DateTimeOffset At,
    string Key,
    string Subsystem,
    string Code,
    AnomalySeverity Severity,
    string Subject,
    string Detail,
    long Count);

/// <summary>What a ring answers to <see cref="MapFeedAnomalyRing.Query"/>.</summary>
/// <param name="Slots">The notices above the requested sequence number, oldest first.</param>
/// <param name="Latest">The newest sequence number the ring has issued (0: nothing yet).</param>
/// <param name="Lost">Notices after the requested sequence number that the ring no longer holds (overwritten).</param>
internal sealed record AnomalyPage(AnomalySlot[] Slots, long Latest, long Lost, bool Reset = false);

/// <summary>
/// A bounded ring of the anomalies this world reported (docs/areas/atlas-insight.md, "Anomalies"). The ledger is process-global and
/// raises <c>Reported</c> on the reporting thread, so <see cref="Offer"/> copies only the notice's own immutable values into a
/// preallocated slot under a short lock; it never touches a world object (positions are resolved later, by a query on the world thread).
/// A notice is kept only when it belongs to this ring's owner (the world's identity token), or has no owner and unowned notices are wanted.
/// </summary>
internal sealed class MapFeedAnomalyRing : IDisposable
{
    /// <summary>Slots in the ring.</summary>
    public const int Capacity = 4096;

    private readonly AnomalyLedger _ledger;
    private readonly object _owner;
    private readonly bool _keepUnowned;
    private readonly TimeProvider _time;
    private readonly AnomalySlot[] _slots = new AnomalySlot[Capacity];
    private readonly object _gate = new();
    private readonly Action<AnomalyNotice> _handler;
    private long _seq;
    private long _accepted;
    private long _refused;
    private bool _subscribed;

    public MapFeedAnomalyRing(AnomalyLedger ledger, object owner, bool keepUnowned, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(owner);
        _ledger = ledger;
        _owner = owner;
        _keepUnowned = keepUnowned;
        _time = time ?? TimeProvider.System;
        _handler = Offer;
        _ledger.Reported += _handler;
        _subscribed = true;
    }

    /// <summary>Whether the ring is attached to the ledger's <c>Reported</c> event.</summary>
    public bool IsSubscribed
    {
        get
        {
            lock (_gate)
            {
                return _subscribed;
            }
        }
    }

    /// <summary>Notices kept so far (every notice that passed the owner check; the ring holds the last <see cref="Capacity"/>).</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>Notices the owner check refused.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Offer one notice (any thread; the ledger calls this on the reporting thread).</summary>
    public void Offer(AnomalyNotice notice)
    {
        object? origin = notice.Origin;
        if (!(ReferenceEquals(origin, _owner) || (origin is null && _keepUnowned)))
        {
            Interlocked.Increment(ref _refused);
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            long seq = ++_seq;
            _slots[(int)((seq - 1) % Capacity)] = new AnomalySlot(
                seq, now, notice.Key, notice.Subsystem, notice.Code, notice.Severity, notice.Subject, notice.Detail, notice.Count);
        }

        Interlocked.Increment(ref _accepted);
    }

    /// <summary>The notices above <paramref name="since"/>, oldest first, at most <paramref name="max"/> (any thread).</summary>
    public AnomalyPage Query(long since, int max)
    {
        lock (_gate)
        {
            long latest = _seq;
            long oldest = Math.Max(1L, latest - Capacity + 1);
            long start = Math.Max(Math.Max(since, 0L) + 1, oldest);
            long lost = latest > since ? Math.Max(0L, oldest - (Math.Max(since, 0L) + 1)) : 0L;
            bool reset = since > latest; // the caller holds a sequence number this ring never issued: the daemon restarted, or the ring was recreated
            int count = max < 1 || start > latest ? 0 : (int)Math.Min((long)max, latest - start + 1);
            if (count == 0)
            {
                return new AnomalyPage([], latest, lost, reset);
            }

            var slots = new AnomalySlot[count];
            for (int i = 0; i < count; i++)
            {
                slots[i] = _slots[(int)((start + i - 1) % Capacity)];
            }

            return new AnomalyPage(slots, latest, lost);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                return;
            }

            _subscribed = false;
        }

        _ledger.Reported -= _handler;
    }
}
