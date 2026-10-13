using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Bot QA's instruments (docs/ops/metrics.md): <c>arcanecore.botqa.occurrences{kind}</c> (a counter, recorded as signals are folded into findings) and
/// <c>arcanecore.botqa.findings{kind,state}</c> (a gauge over the aggregator, observed at collection time only). Both meter names start with
/// <see cref="ArcaneMeters.Prefix"/>, so the metrics store collects them when <c>Ops:Metrics:Enabled</c> is on; the gauge is created only then
/// (<see cref="RegisterFindings"/>), and with no listener a count is a single enabled check. Label values are bounded: <c>kind</c> is a row of
/// <see cref="QaKinds"/> (its slug) and <c>state</c> a <see cref="QaPublicationState"/>, so at most kinds x states series exist.
/// </summary>
internal static class PlayerbotQaMeters
{
    /// <summary>The meter of the occurrence counter.</summary>
    internal const string MeterName = "ArcaneCore.Playerbots.Qa";

    /// <summary>The meter of the findings gauge (one per QA feature, disposed when it stops).</summary>
    internal const string FindingsMeterName = "ArcaneCore.Playerbots.Qa.Findings";

    internal static Meter Meter { get; } = new(MeterName, "1.0");

    /// <summary>Occurrences folded into findings, by <c>kind</c> (the slug of the kind's row; a static coverage fact is no occurrence).</summary>
    internal static Counter<long> Occurrences { get; } = Meter.CreateCounter<long>(
        "arcanecore.botqa.occurrences", "{occurrence}", "Occurrences folded into bot QA findings, by finding kind.");

    internal static void CountOccurrences(QaSignalKind kind, long occurrences)
    {
        if (Occurrences.Enabled && occurrences > 0) Occurrences.Add(occurrences, new KeyValuePair<string, object?>("kind", QaKinds.Of(kind).Slug));
    }

    /// <summary>
    /// Create the findings gauge over <paramref name="findings"/> (the aggregator's cached snapshot): one measurement per kind and publication state, zeros
    /// included so a series does not appear and vanish. The caller disposes the returned meter when QA stops. <paramref name="scope"/> is the owner (the meter's <see cref="Meter.Scope"/>), so a listener can tell one QA feature's gauge from another's.
    /// </summary>
    internal static Meter RegisterFindings(Func<IReadOnlyList<QaFindingView>> findings, object? scope = null)
    {
        var meter = new Meter(FindingsMeterName, "1.0", null, scope);
        meter.CreateObservableGauge("arcanecore.botqa.findings", () => Observe(findings()), "{finding}", "Bot QA findings held, by finding kind and publication state.");
        return meter;
    }

    internal static IEnumerable<Measurement<long>> Observe(IReadOnlyList<QaFindingView> findings)
    {
        QaPublicationState[] states = Enum.GetValues<QaPublicationState>();
        var counts = new long[QaKinds.All.Count, states.Length];
        foreach (QaFindingView view in findings)
        {
            int state = Array.IndexOf(states, view.Publication.State);
            if ((uint)view.Kind < (uint)QaKinds.All.Count && state >= 0) counts[(int)view.Kind, state]++;
        }

        foreach (QaKindInfo row in QaKinds.All)
        {
            for (int s = 0; s < states.Length; s++)
            {
                yield return new Measurement<long>(counts[(int)row.Kind, s], new KeyValuePair<string, object?>("kind", row.Slug),
                    new KeyValuePair<string, object?>("state", states[s].ToString().ToLowerInvariant()));
            }
        }
    }
}
