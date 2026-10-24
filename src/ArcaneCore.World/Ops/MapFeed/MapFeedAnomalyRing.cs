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
internal sealed record AnomalyPage(AnomalySlot[] Slots, long Latest, long Lost);

/// <summary>
/// A bounded ring of the anomalies this world reported (docs/areas/atlas-insight.md). Not yet implemented.
/// </summary>
internal sealed class MapFeedAnomalyRing : IDisposable
{
    /// <summary>Slots in the ring.</summary>
    public const int Capacity = 4096;

    public MapFeedAnomalyRing(AnomalyLedger ledger, object owner, bool keepUnowned, TimeProvider? time = null)
        => throw new NotImplementedException();

    /// <summary>Whether the ring is attached to the ledger's <c>Reported</c> event.</summary>
    public bool IsSubscribed => throw new NotImplementedException();

    /// <summary>Notices kept so far (every notice that passed the owner check; the ring holds the last <see cref="Capacity"/>).</summary>
    public long Accepted => throw new NotImplementedException();

    /// <summary>Notices the owner check refused.</summary>
    public long Refused => throw new NotImplementedException();

    /// <summary>Offer one notice (any thread; the ledger calls this on the reporting thread).</summary>
    public void Offer(AnomalyNotice notice) => throw new NotImplementedException();

    /// <summary>The notices above <paramref name="since"/>, oldest first, at most <paramref name="max"/> (any thread).</summary>
    public AnomalyPage Query(long since, int max) => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
