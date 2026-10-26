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

    /// <summary>
    /// Ledger keys of world (non-bot) anomalies a bot can trigger by using the world: a chest that opens empty, a game object use refused as a
    /// defect, an open-lock cast that did nothing, and a game object effect dropped before it ran. They are admitted like <c>bot-*</c> keys.
    /// </summary>
    internal static readonly HashSet<string> WorldKeys = new(StringComparer.Ordinal)
    {
        "loot/gameobject-loot-empty",
        "gameobject/use-failed",
        "gameobject/open-no-effect",
        "spell/gameobject-effect-skipped",
    };
    internal const int DefaultMinCount = 5;
    internal const int DefaultMinDistinctSubjects = 3;

    private readonly object _gate = new();
    private readonly DiagnosticDeltaTracker _tracker = new();

    /// <summary>Pulls new <see cref="QaSignalKind.Anomaly"/> signals since the previous poll.</summary>
    internal IReadOnlyList<QaSignal> Pull()
    {
        var signals = new List<QaSignal>();
        lock (_gate)
        {
            _tracker.BeginPass(ledger.Epoch);
            foreach (AnomalySnapshot anomaly in ledger.Snapshot())
            {
                if (!Qualifies(anomaly))
                {
                    continue;
                }

                long added = _tracker.Added(anomaly.Key, anomaly.Count);
                if (added > 0)
                {
                    signals.Add(SignalOf(anomaly, added));
                }
            }

            _tracker.EndPass();
        }

        return signals;
    }

    internal bool Qualifies(AnomalySnapshot anomaly)
        => !string.Equals(anomaly.Key, AnomalyLedger.OverflowKey, StringComparison.Ordinal)
            && (anomaly.Subsystem.StartsWith(BotSubsystemPrefix, StringComparison.Ordinal) || WorldKeys.Contains(anomaly.Key))
            && anomaly.Severity >= minSeverity
            && (anomaly.Severity >= AnomalySeverity.Error || anomaly.Count >= minCount || anomaly.DistinctSubjects >= minDistinctSubjects);

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
