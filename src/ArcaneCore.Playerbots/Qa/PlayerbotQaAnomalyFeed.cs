using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Polls an <see cref="AnomalyLedger"/> for recurring <c>bot-*</c> outcome anomalies
/// (<see cref="AnomalySnapshot.Severity"/> &gt;= <see cref="AnomalySeverity.Warning"/> and either
/// <see cref="AnomalySnapshot.Count"/> &gt;= <see cref="DefaultMinCount"/> or
/// <see cref="AnomalySnapshot.DistinctSubjects"/> &gt;= <see cref="DefaultMinDistinctSubjects"/>)
/// and converts count deltas into <see cref="QaSignalKind.Anomaly"/> signals for the bot-QA aggregator.
/// </summary>
internal sealed class PlayerbotQaAnomalyFeed(
    AnomalyLedger ledger,
    int minCount = PlayerbotQaAnomalyFeed.DefaultMinCount,
    int minDistinctSubjects = PlayerbotQaAnomalyFeed.DefaultMinDistinctSubjects,
    AnomalySeverity minSeverity = AnomalySeverity.Warning)
{
    internal const string BotSubsystemPrefix = "bot-";
    internal const int DefaultMinCount = 5;
    internal const int DefaultMinDistinctSubjects = 3;

    private readonly object _gate = new();
    private Dictionary<string, long> _seen = new(StringComparer.Ordinal);

    /// <summary>Pulls new <see cref="QaSignalKind.Anomaly"/> signals since the previous poll.</summary>
    internal IReadOnlyList<QaSignal> Pull()
    {
        var signals = new List<QaSignal>();
        lock (_gate)
        {
            IReadOnlyList<AnomalySnapshot> snapshot = ledger.Snapshot();
            var seen = new Dictionary<string, long>(snapshot.Count, StringComparer.Ordinal);
            foreach (AnomalySnapshot anomaly in snapshot)
            {
                if (!Qualifies(anomaly))
                {
                    continue;
                }

                long before = _seen.TryGetValue(anomaly.Key, out long previous) && previous <= anomaly.Count ? previous : 0;
                seen[anomaly.Key] = anomaly.Count;
                long added = anomaly.Count - before;
                if (added <= 0)
                {
                    continue;
                }

                signals.Add(SignalOf(anomaly, added));
            }

            _seen = seen;
        }

        return signals;
    }

    internal bool Qualifies(AnomalySnapshot anomaly)
        => !string.Equals(anomaly.Key, AnomalyLedger.OverflowKey, StringComparison.Ordinal)
            && anomaly.Subsystem.StartsWith(BotSubsystemPrefix, StringComparison.Ordinal)
            && anomaly.Severity >= minSeverity
            && (anomaly.Count >= minCount || anomaly.DistinctSubjects >= minDistinctSubjects);

    private static QaSignal SignalOf(AnomalySnapshot anomaly, long occurrences) => new()
    {
        Kind = QaSignalKind.Anomaly,
        AtUtc = anomaly.LastSeen.UtcDateTime,
        BotCount = anomaly.DistinctSubjects,
        Goal = anomaly.Subsystem,
        Reason = anomaly.Code,
        FaultCode = anomaly.Key,
        Detail = anomaly.LastDetail,
        Message = anomaly.LastDetail.Length != 0 ? $"{anomaly.Key}: {anomaly.LastDetail}" : anomaly.Key,
        Occurrences = occurrences,
    };
}
