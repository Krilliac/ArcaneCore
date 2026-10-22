namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// Turns repeated snapshots of a diagnostics ledger (<see cref="FaultLedger"/>, <see cref="AnomalyLedger"/>, <see cref="Invariant.Failures()"/>) into
/// "how many new occurrences since the previous pass" per key, the way every consumer that reports on a schedule needs it (bot QA's feeds).
/// <para>
/// A pass starts with <see cref="BeginPass"/> and the ledger's epoch (<see cref="FaultLedger.Epoch"/>, <see cref="AnomalyLedger.Epoch"/>,
/// <see cref="Invariant.Epoch"/>), calls <see cref="Added"/> for each key it still cares about, and ends with <see cref="EndPass"/>. A key not passed to
/// <see cref="Added"/> in a pass is forgotten, so a key that starts qualifying later reports its whole count. A changed epoch means the ledger was reset
/// in between: every key starts over, so occurrences after a reset are never lost, even when a key has grown past its old count by the next pass.
/// A count below the remembered one (a reset the epoch did not show, such as a ledger replaced by a test) also starts that key over.
/// </para>
/// <para>Not thread safe: the owner serialises its passes.</para>
/// </summary>
public sealed class DiagnosticDeltaTracker
{
    private Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private Dictionary<string, long> _next = new(StringComparer.Ordinal);
    private long? _epoch;

    /// <summary>Start a pass over a snapshot taken at <paramref name="epoch"/>.</summary>
    public void BeginPass(long epoch)
    {
        if (_epoch != epoch)
        {
            _seen.Clear();
            _epoch = epoch;
        }

        _next.Clear();
    }

    /// <summary>The occurrences of <paramref name="key"/> since the previous pass, given its <paramref name="count"/> now (0 when none are new).</summary>
    public long Added(string key, long count) => Added(key, count, out _);

    /// <summary>
    /// <see cref="Added(string, long)"/>, also saying whether the count starts from zero (<paramref name="fromStart"/>: a key not seen before in this
    /// epoch, or one that started over), so a consumer can report the first occurrence on its own.
    /// </summary>
    public long Added(string key, long count, out bool fromStart)
    {
        long before = _seen.TryGetValue(key, out long previous) && previous <= count ? previous : 0;
        _next[key] = count;
        fromStart = before == 0;
        return Math.Max(0, count - before);
    }

    /// <summary>End the pass: the keys passed to <see cref="Added"/> are what the next pass compares against.</summary>
    public void EndPass() => (_seen, _next) = (_next, _seen);
}
