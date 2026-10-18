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
internal sealed class PlayerbotQaFaultFeed(
    FaultLedger ledger,
    Func<IReadOnlyList<InvariantFailure>>? invariantSource = null,
    AnomalyLedger? serverAnomalySource = null)
{
    /// <summary>The ledger site of a bot's update exception (ManagedPlayerbotFeature.Fault).</summary>
    internal const string BotSite = "playerbot";

    /// <summary>
    /// Ledger sites that record the host's environment, not a server defect: a full or unavailable disk or folder under the policy telemetry,
    /// or under a packet capture or a dump (IO/access errors). Each site's catch is narrowed to those exception types.
    /// They stay in the ledger (<c>.server faults</c>) but are not filed as public QA issues, which would turn an operator's machine state into
    /// bug reports. A site whose catch can also see code defects (null dereference, missing key) must not be listed here.
    /// </summary>
    internal static readonly HashSet<string> EnvironmentSites = new(StringComparer.Ordinal)
    {
        "playerbot.policy-telemetry-io", "net.packet-capture", "dump.free-space",
    };

    /// <summary>Terrain/mesh collision observer keys that are spatial warnings rather than server code faults.</summary>
    internal static readonly HashSet<string> SpatialAnomalyKeys = new(StringComparer.Ordinal)
    {
        "creature/under-map", "player/under-map",
    };

    private readonly object _gate = new();
    private Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private Dictionary<string, long> _seenInvariants = new(StringComparer.Ordinal);
    private Dictionary<string, long> _seenServerAnomalies = new(StringComparer.Ordinal);

    /// <summary>The signals for what the ledger, invariant tracker, and server error anomalies recorded since the previous call (none when nothing changed).</summary>
    internal IReadOnlyList<QaSignal> Pull()
    {
        var signals = new List<QaSignal>();
        lock (_gate)
        {
            ledger.ReplayCrashJournal();
            IReadOnlyList<FaultSnapshot> snapshot = ledger.Snapshot(); // under the gate: two overlapping pulls must not both send the same delta
            var seen = new Dictionary<string, long>(snapshot.Count, StringComparer.Ordinal);
            foreach (FaultSnapshot fault in snapshot)
            {
                if (fault.Id == FaultLedger.OverflowId || string.Equals(fault.Site, BotSite, StringComparison.Ordinal) || IsConnectionNoise(fault)
                    || EnvironmentSites.Contains(fault.Site))
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

            if (invariantSource is not null)
            {
                IReadOnlyList<InvariantFailure> invariants = invariantSource();
                var seenInvariants = new Dictionary<string, long>(invariants.Count, StringComparer.Ordinal);
                DateTime nowUtc = DateTime.UtcNow;
                foreach (InvariantFailure inv in invariants)
                {
                    if (inv.Count <= 0 || string.IsNullOrWhiteSpace(inv.Member))
                    {
                        continue;
                    }

                    string siteKey = $"{inv.Member}@{inv.File}:{inv.Line}";
                    long before = _seenInvariants.TryGetValue(siteKey, out long previous) && previous <= inv.Count ? previous : 0;
                    seenInvariants[siteKey] = inv.Count;
                    long added = inv.Count - before;
                    if (added <= 0)
                    {
                        continue;
                    }

                    signals.Add(InvariantSignalOf(inv, siteKey, nowUtc, added));
                }

                _seenInvariants = seenInvariants;
            }

            if (serverAnomalySource is not null)
            {
                IReadOnlyList<AnomalySnapshot> anomalies = serverAnomalySource.Snapshot();
                var seenAnomalies = new Dictionary<string, long>(anomalies.Count, StringComparer.Ordinal);
                foreach (AnomalySnapshot anomaly in anomalies)
                {
                    if (!QualifiesServerAnomaly(anomaly))
                    {
                        continue;
                    }

                    long before = _seenServerAnomalies.TryGetValue(anomaly.Key, out long previous) && previous <= anomaly.Count ? previous : 0;
                    seenAnomalies[anomaly.Key] = anomaly.Count;
                    long added = anomaly.Count - before;
                    if (added <= 0)
                    {
                        continue;
                    }

                    if (before == 0)
                    {
                        signals.Add(ServerAnomalySignalOf(anomaly, anomaly.FirstSeen, 1));
                        added--;
                    }

                    if (added > 0)
                    {
                        signals.Add(ServerAnomalySignalOf(anomaly, anomaly.LastSeen, added));
                    }
                }

                _seenServerAnomalies = seenAnomalies;
            }
        }

        return signals;
    }

    internal static bool QualifiesServerAnomaly(AnomalySnapshot anomaly)
        => !string.Equals(anomaly.Key, AnomalyLedger.OverflowKey, StringComparison.Ordinal)
            && !anomaly.Subsystem.StartsWith(PlayerbotQaAnomalyFeed.BotSubsystemPrefix, StringComparison.Ordinal)
            && !SpatialAnomalyKeys.Contains(anomaly.Key)
            && anomaly.Severity >= AnomalySeverity.Error;

    /// <summary>
    /// Connection teardown at the session site (a peer that reset, a closed stream, a cancelled read) is the network, not a server bug: it is counted in
    /// the ledger and metrics but never becomes a public issue.
    /// </summary>
    internal static bool IsConnectionNoise(FaultSnapshot fault)
        => string.Equals(fault.Site, "session", StringComparison.Ordinal)
            && fault.ExceptionType is "System.Net.Sockets.SocketException" or "System.IO.IOException" or "System.IO.EndOfStreamException"
                or "System.ObjectDisposedException" or "System.OperationCanceledException" or "System.Threading.Tasks.TaskCanceledException";

    private static QaSignal SignalOf(FaultSnapshot fault, DateTimeOffset at, long occurrences) => new()
    {
        Kind = QaSignalKind.ServerFault,
        AtUtc = at.UtcDateTime,
        FaultId = fault.Id,
        Goal = fault.Site,
        ExceptionType = fault.ExceptionType,
        Reason = fault.ExceptionType,
        Detail = string.Join(" > ", fault.Frames),
        Diag = fault.SampleStack,
        Message = string.Empty, // the exception message stays in the ledger (.server faults show); it can name a player and never enters a finding
        Occurrences = occurrences,
    };

    private static QaSignal InvariantSignalOf(InvariantFailure inv, string siteKey, DateTime atUtc, long occurrences)
    {
        string file = Path.GetFileName(inv.File);
        string frame = string.IsNullOrEmpty(file) ? inv.Member : $"{inv.Member}({file}:{inv.Line})";
        return new QaSignal
        {
            Kind = QaSignalKind.ServerFault,
            AtUtc = atUtc,
            FaultId = FaultLedger.HashSignature("invariant|" + siteKey),
            Goal = "invariant",
            ExceptionType = typeof(InvariantViolationException).FullName!,
            Reason = typeof(InvariantViolationException).FullName!,
            Detail = inv.Member,
            Diag = frame,
            Message = string.Empty,
            Occurrences = occurrences,
        };
    }

    private static QaSignal ServerAnomalySignalOf(AnomalySnapshot anomaly, DateTimeOffset at, long occurrences) => new()
    {
        Kind = QaSignalKind.ServerFault,
        AtUtc = at.UtcDateTime,
        FaultId = FaultLedger.HashSignature("anomaly|" + anomaly.Key),
        Goal = anomaly.Subsystem,
        ExceptionType = "Anomaly:" + anomaly.Key,
        Reason = "Anomaly:" + anomaly.Key,
        Detail = anomaly.Key,
        Diag = anomaly.Key,
        Message = string.Empty,
        Occurrences = occurrences,
    };
}
