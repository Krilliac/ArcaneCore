namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The bookkeeping every diagnostics ledger keeps for one key (a fault signature, an anomaly key): how often and when it fired, its rate over the
/// last hour, the gaps between occurrences, a bounded tally of the subjects (or messages) seen with it, the newest distinct details, and the log
/// throttle (the first <c>logLimit</c> occurrences log, then at most one line a minute carrying how many were held back).
/// <para>
/// <see cref="FaultLedger"/> and <see cref="AnomalyLedger"/> used to carry their own copy of each of these; they now hold one of these per entry and
/// add only what is their own (a fault's sample stack, an anomaly's worst severity).
/// </para>
/// <para>Not thread safe: the owning entry calls every member under its own lock.</para>
/// </summary>
internal sealed class LedgerEntryCore(int tallyCapacity, int recentCapacity)
{
    /// <summary>The least time between two throttled log lines of one key.</summary>
    internal static readonly TimeSpan ThrottleInterval = TimeSpan.FromMinutes(1);

    private readonly RateWindow _rate = new();
    private GapRing? _gaps;
    private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
    private long _sinceLog;

    /// <summary>Occurrences since creation or the last <see cref="Clear"/>.</summary>
    public long Count { get; private set; }

    /// <summary>The first occurrence (default while <see cref="Count"/> is 0).</summary>
    public DateTimeOffset First { get; private set; }

    /// <summary>The latest occurrence (default while <see cref="Count"/> is 0).</summary>
    public DateTimeOffset Last { get; private set; }

    /// <summary>The bounded tally of subjects or messages (Space-Saving).</summary>
    public SubjectTally Tally { get; } = new(tallyCapacity);

    /// <summary>The newest distinct details or messages.</summary>
    public RecentDistinct Recent { get; } = new(recentCapacity);

    /// <summary>Count one occurrence at <paramref name="now"/> (first and last seen, the rate window, the gap to the previous one); the new count.</summary>
    public long Occur(DateTimeOffset now)
    {
        Count++;
        if (Count == 1)
        {
            First = now;
        }
        else
        {
            (_gaps ??= new GapRing()).Add(now - Last);
        }

        Last = now;
        _rate.Add(now);
        return Count;
    }

    /// <summary>
    /// Whether the occurrence just counted should be logged: always when <paramref name="force"/> or within the first <paramref name="logLimit"/>
    /// occurrences, then at most once per <see cref="ThrottleInterval"/>, with the number of occurrences held back since the previous line.
    /// </summary>
    public (bool Log, long Suppressed) Throttle(DateTimeOffset now, int logLimit, bool force = false)
    {
        if (force || Count <= logLimit)
        {
            _lastLog = now;
            return (true, 0);
        }

        if (_lastLog == DateTimeOffset.MinValue || now - _lastLog >= ThrottleInterval)
        {
            long suppressed = _sinceLog;
            _sinceLog = 0;
            _lastLog = now;
            return (true, suppressed);
        }

        _sinceLog++;
        return (false, 0);
    }

    /// <summary>First and last seen, the occurrences of the last 15 minutes and hour, and the p50/p99 gap between occurrences.</summary>
    public LedgerTiming TimingAt(DateTimeOffset now)
    {
        (TimeSpan? p50, TimeSpan? p99) = _gaps?.Percentiles() ?? (null, null);
        return new LedgerTiming(Count == 0 ? null : First, Count == 0 ? null : Last, _rate.CountSince(now, 15), _rate.CountSince(now, 60), p50, p99);
    }

    /// <summary>Forget everything (a ledger reset of a bucket that is kept, such as the overflow bucket).</summary>
    public void Clear()
    {
        Count = 0;
        First = default;
        Last = default;
        _sinceLog = 0;
        _lastLog = DateTimeOffset.MinValue;
        Tally.Clear();
        Recent.Clear();
        _rate.Clear();
        _gaps?.Clear();
    }
}
