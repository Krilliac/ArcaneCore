namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>One canonical subsystem's faults and anomalies (<see cref="DiagnosticOverview.BySubsystem"/>).</summary>
/// <param name="Subsystem">The canonical subsystem (<see cref="DiagnosticSubsystem"/>).</param>
/// <param name="Faults">Fault occurrences.</param>
/// <param name="FaultSignatures">Distinct fault signatures.</param>
/// <param name="Anomalies">Anomaly reports.</param>
/// <param name="AnomalyKeys">Distinct anomaly keys.</param>
/// <param name="WorstAnomaly">The worst anomaly severity, or null without anomalies.</param>
/// <param name="LastSeen">The latest fault or anomaly.</param>
public sealed record DiagnosticSubsystemSummary(
    string Subsystem,
    long Faults,
    int FaultSignatures,
    long Anomalies,
    int AnomalyKeys,
    AnomalySeverity? WorstAnomaly,
    DateTimeOffset LastSeen)
{
    /// <summary>Faults and anomalies together.</summary>
    public long Total => Faults + Anomalies;
}

/// <summary>
/// The one view across the diagnostics ledgers: faults (<see cref="FaultLedger"/>) and anomalies (<see cref="AnomalyLedger"/>) grouped under the
/// canonical subsystem both resolve to (<see cref="DiagnosticSubsystem"/>), so an operator sees "map: 12 faults, 40 anomalies" in one line instead of
/// reading two listings that spell the area differently. The overflow buckets have no subsystem and are left out. Pure function over snapshots.
/// </summary>
public static class DiagnosticOverview
{
    /// <summary>Faults and anomalies per canonical subsystem, the busiest first (then by name).</summary>
    public static IReadOnlyList<DiagnosticSubsystemSummary> BySubsystem(IEnumerable<FaultSnapshot> faults, IEnumerable<AnomalySnapshot> anomalies)
    {
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(anomalies);
        var rows = new Dictionary<string, DiagnosticSubsystemSummary>(StringComparer.Ordinal);
        foreach (FaultSnapshot fault in faults)
        {
            if (fault.Count <= 0 || string.Equals(fault.Id, FaultLedger.OverflowId, StringComparison.Ordinal))
            {
                continue;
            }

            string subsystem = DiagnosticSubsystem.OfSite(fault.Site);
            DiagnosticSubsystemSummary row = Row(rows, subsystem, fault.LastSeen);
            rows[subsystem] = row with
            {
                Faults = row.Faults + fault.Count,
                FaultSignatures = row.FaultSignatures + 1,
                LastSeen = Max(row.LastSeen, fault.LastSeen),
            };
        }

        foreach (AnomalySnapshot anomaly in anomalies)
        {
            if (anomaly.Count <= 0 || string.Equals(anomaly.Key, AnomalyLedger.OverflowKey, StringComparison.Ordinal))
            {
                continue;
            }

            string subsystem = DiagnosticSubsystem.Canonical(anomaly.Subsystem);
            DiagnosticSubsystemSummary row = Row(rows, subsystem, anomaly.LastSeen);
            rows[subsystem] = row with
            {
                Anomalies = row.Anomalies + anomaly.Count,
                AnomalyKeys = row.AnomalyKeys + 1,
                WorstAnomaly = row.WorstAnomaly is { } worst && worst >= anomaly.Severity ? worst : anomaly.Severity,
                LastSeen = Max(row.LastSeen, anomaly.LastSeen),
            };
        }

        var list = rows.Values.ToList();
        list.Sort(static (a, b) =>
        {
            int byTotal = b.Total.CompareTo(a.Total);
            return byTotal != 0 ? byTotal : string.CompareOrdinal(a.Subsystem, b.Subsystem);
        });
        return list;
    }

    private static DiagnosticSubsystemSummary Row(Dictionary<string, DiagnosticSubsystemSummary> rows, string subsystem, DateTimeOffset seen)
        => rows.TryGetValue(subsystem, out DiagnosticSubsystemSummary? row) ? row : new DiagnosticSubsystemSummary(subsystem, 0, 0, 0, 0, null, seen);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
