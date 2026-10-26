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
    AnomalyLedger? serverAnomalySource = null,
    Func<long>? invariantEpoch = null)
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

    /// <summary>
    /// Anomaly keys whose subjects are independent defects (one spell, one waypoint path, one script): each of the top subjects of such a key is its
    /// own finding (fault id from key and subject), instead of one finding per key that shows only the last detail.
    /// </summary>
    internal static readonly HashSet<string> PerSubjectKeys = new(StringComparer.Ordinal)
    {
        "spell/effect-unhandled", "creature/waypoint-path-missing", "creature-ai/script-action-unsupported", "loot/guaranteed-drop-missing",
    };

    internal const int DefaultMinServerWarningCount = 5;
    internal const int DefaultMinServerWarningSubjects = 3;

    private readonly object _gate = new();
    private readonly DiagnosticDeltaTracker _faults = new();
    private readonly DiagnosticDeltaTracker _invariants = new();
    private readonly DiagnosticDeltaTracker _serverAnomalies = new();

    /// <summary>The signals for what the ledger, invariant tracker, and server error anomalies recorded since the previous call (none when nothing changed).</summary>
    internal IReadOnlyList<QaSignal> Pull()
    {
        var signals = new List<QaSignal>();
        lock (_gate)
        {
            // Under the gate: two overlapping pulls must not both send the same delta. Each epoch is read before its snapshot (DiagnosticDeltaTracker).
            ledger.ReplayCrashJournal();
            _faults.BeginPass(ledger.Epoch);
            foreach (FaultSnapshot fault in ledger.Snapshot())
            {
                if (fault.Id == FaultLedger.OverflowId || string.Equals(fault.Site, BotSite, StringComparison.Ordinal) || IsConnectionNoise(fault)
                    || EnvironmentSites.Contains(fault.Site))
                {
                    continue;
                }

                AddDelta(signals, _faults.Added(fault.Id, fault.Count, out bool fromStart), fromStart,
                    (at, n) => SignalOf(fault, at, n), fault.FirstSeen, fault.LastSeen);
            }

            _faults.EndPass();

            if (invariantSource is not null)
            {
                // invariantEpoch is Invariant.Epoch when the source is the process-wide tracker; a source without one has no resets to tell apart.
                _invariants.BeginPass(invariantEpoch?.Invoke() ?? 0);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (InvariantFailure inv in invariantSource())
                {
                    if (inv.Count <= 0 || string.IsNullOrWhiteSpace(inv.Member))
                    {
                        continue;
                    }

                    string siteKey = $"{inv.Member}@{inv.File}:{inv.Line}";
                    long added = _invariants.Added(siteKey, inv.Count);
                    if (added > 0)
                    {
                        signals.Add(InvariantSignalOf(inv, siteKey, (inv.LastSeen ?? now).UtcDateTime, added));
                    }
                }

                _invariants.EndPass();
            }

            if (serverAnomalySource is not null)
            {
                _serverAnomalies.BeginPass(serverAnomalySource.Epoch);
                foreach (AnomalySnapshot anomaly in serverAnomalySource.Snapshot())
                {
                    if (!QualifiesServerAnomaly(anomaly))
                    {
                        continue;
                    }

                    if (PerSubjectKeys.Contains(anomaly.Key) && anomaly.TopSubjects.Count > 0)
                    {
                        foreach (SubjectCount subject in anomaly.TopSubjects)
                        {
                            AddDelta(signals, _serverAnomalies.Added(anomaly.Key + "|" + subject.Subject, subject.Count, out bool subjectFromStart), subjectFromStart,
                                (at, n) => SubjectAnomalySignalOf(anomaly, subject.Subject, at, n), anomaly.FirstSeen, anomaly.LastSeen);
                        }

                        continue;
                    }

                    AddDelta(signals, _serverAnomalies.Added(anomaly.Key, anomaly.Count, out bool fromStart), fromStart,
                        (at, n) => ServerAnomalySignalOf(anomaly, at, n), anomaly.FirstSeen, anomaly.LastSeen);
                }

                _serverAnomalies.EndPass();
            }
        }

        return signals;
    }

    /// <summary>
    /// A key that starts from zero sends its first occurrence on its own, stamped with its first-seen time, so a finding's first sighting is right;
    /// the rest of the new occurrences go in one signal stamped with the last-seen time.
    /// </summary>
    private static void AddDelta(List<QaSignal> signals, long added, bool fromStart, Func<DateTimeOffset, long, QaSignal> make, DateTimeOffset first, DateTimeOffset last)
    {
        if (added <= 0)
        {
            return;
        }

        if (fromStart)
        {
            signals.Add(make(first, 1));
            added--;
        }

        if (added > 0)
        {
            signals.Add(make(last, added));
        }
    }

    internal static bool QualifiesServerAnomaly(AnomalySnapshot anomaly)
        => !string.Equals(anomaly.Key, AnomalyLedger.OverflowKey, StringComparison.Ordinal)
            && !anomaly.Subsystem.StartsWith(PlayerbotQaAnomalyFeed.BotSubsystemPrefix, StringComparison.Ordinal)
            && !SpatialAnomalyKeys.Contains(anomaly.Key)
            && (anomaly.Severity >= AnomalySeverity.Error
                || (anomaly.Severity >= AnomalySeverity.Warning
                    && (anomaly.Count >= DefaultMinServerWarningCount || anomaly.DistinctSubjects >= DefaultMinServerWarningSubjects)));

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

    private static QaSignal ServerAnomalySignalOf(AnomalySnapshot anomaly, DateTimeOffset at, long occurrences)
    {
        string head = anomaly.LastDetail.Length > 0 ? $"{anomaly.Key}: {anomaly.LastDetail}" : anomaly.Key;
        return new QaSignal
        {
            Kind = QaSignalKind.ServerFault,
            AtUtc = at.UtcDateTime,
            FaultId = FaultLedger.HashSignature("anomaly|" + anomaly.Key),
            Goal = anomaly.Subsystem,
            ExceptionType = "Anomaly:" + anomaly.Key,
            Reason = "Anomaly:" + anomaly.Key,
            Detail = anomaly.Key,
            Diag = head + SubjectsSuffix(anomaly),
            Message = string.Empty,
            Occurrences = occurrences,
        };
    }

    /// <summary>One finding for one subject of a <see cref="PerSubjectKeys"/> key; the last detail is shown only for the subject that reported it.</summary>
    private static QaSignal SubjectAnomalySignalOf(AnomalySnapshot anomaly, string subject, DateTimeOffset at, long occurrences)
    {
        bool hasDetail = anomaly.LastDetail.Length > 0 && string.Equals(anomaly.LastSubject, subject, StringComparison.Ordinal);
        return new QaSignal
        {
            Kind = QaSignalKind.ServerFault,
            AtUtc = at.UtcDateTime,
            FaultId = FaultLedger.HashSignature("anomaly|" + anomaly.Key + "|" + subject),
            Goal = anomaly.Subsystem,
            ExceptionType = "Anomaly:" + anomaly.Key,
            Reason = "Anomaly:" + anomaly.Key,
            Detail = anomaly.Key,
            Diag = $"{anomaly.Key} [{subject}]" + (hasDetail ? ": " + anomaly.LastDetail : string.Empty),
            Message = string.Empty,
            Occurrences = occurrences,
        };
    }

    /// <summary>
    /// <c> | subjects=N: a xC; b xC</c> (the top subjects and the distinct count, <c>&gt;=</c> when saturated). The subjects are ids and guid low,
    /// never names (the composer redacts the whole Diag as well).
    /// </summary>
    private static string SubjectsSuffix(AnomalySnapshot anomaly)
    {
        if (anomaly.TopSubjects.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(" | ").Append(anomaly.SubjectsText).Append(": ");
        for (int i = 0; i < anomaly.TopSubjects.Count; i++)
        {
            SubjectCount top = anomaly.TopSubjects[i];
            sb.Append(i == 0 ? string.Empty : "; ").Append(top.Subject).Append(" x").Append(top.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
