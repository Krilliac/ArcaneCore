using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Turns the growth of the server's <see cref="FaultLedger"/> into <see cref="QaSignalKind.ServerFault"/> signals: each QA pass asks for what is new
/// since the previous pass, and one signal per changed ledger signature carries the new occurrences (<see cref="QaSignal.Occurrences"/>), so a fault
/// that fired 40 000 times is one signal, not 40 000. The fingerprint of the finding is derived from the ledger id, so a signature is one finding
/// however often and from however many places it is reported.
/// <para>
/// The bot fault site (<see cref="BotSite"/>) is not fed: a bot's exception already reaches QA as an action fault with the bot's context, and a
/// second issue for the same exception would only duplicate it. The ledger's overflow bucket is not fed either (it has no signature to file).
/// Ownership: one instance per QA feature; <see cref="Pull"/> is serialised by a lock, so it is safe from any thread. Nothing here reads
/// or redacts the message: the composer redacts at output, as for every other signal.
/// </para>
/// </summary>
internal sealed class PlayerbotQaFaultFeed(FaultLedger ledger)
{
    /// <summary>The ledger site of a bot's update exception (ManagedPlayerbotFeature.Fault).</summary>
    internal const string BotSite = "playerbot";

    private readonly object _gate = new();
    private Dictionary<string, long> _seen = new(StringComparer.Ordinal);

    /// <summary>The signals for what the ledger recorded since the previous call (none when nothing changed).</summary>
    internal IReadOnlyList<QaSignal> Pull()
    {
        IReadOnlyList<FaultSnapshot> snapshot = ledger.Snapshot();
        var signals = new List<QaSignal>();
        lock (_gate)
        {
            var seen = new Dictionary<string, long>(snapshot.Count, StringComparer.Ordinal);
            foreach (FaultSnapshot fault in snapshot)
            {
                if (fault.Id == FaultLedger.OverflowId || string.Equals(fault.Site, BotSite, StringComparison.Ordinal))
                {
                    continue;
                }

                // A ledger reset (count below what was seen) starts the signature over.
                long before = _seen.TryGetValue(fault.Id, out long previous) && previous <= fault.Count ? previous : 0;
                seen[fault.Id] = fault.Count;
                long added = fault.Count - before;
                if (added <= 0)
                {
                    continue;
                }

                if (before == 0)
                {
                    signals.Add(SignalOf(fault, fault.FirstSeen, 1));
                    added--;
                }

                if (added > 0)
                {
                    signals.Add(SignalOf(fault, fault.LastSeen, added));
                }
            }

            _seen = seen;
        }

        return signals;
    }

    private static QaSignal SignalOf(FaultSnapshot fault, DateTimeOffset at, long occurrences) => new()
    {
        Kind = QaSignalKind.ServerFault,
        AtUtc = at.UtcDateTime,
        FaultId = fault.Id,
        Goal = fault.Site,
        ExceptionType = fault.ExceptionType,
        Reason = fault.ExceptionType,
        Detail = string.Join(" > ", fault.Frames),
        Message = fault.SampleMessage,
        Occurrences = occurrences,
    };
}
