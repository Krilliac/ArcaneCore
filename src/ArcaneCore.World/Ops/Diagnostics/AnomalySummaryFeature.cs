using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// Periodic operator log summary for <see cref="AnomalyLedger"/>:
/// logs one <see cref="LogLevel.Information"/> line every 5 minutes (configurable) with the top 10 anomaly keys
/// by count delta since the previous summary (skipping when the total delta is 0), and logs a
/// <see cref="LogLevel.Warning"/> the first time any anomaly key is observed with <see cref="AnomalySeverity.Error"/>.
/// </summary>
public sealed class AnomalySummaryFeature : IWorldFeature
{
    /// <summary>Default summary period in milliseconds (5 minutes).</summary>
    public const uint DefaultSummaryIntervalMs = 5 * 60 * 1_000;

    /// <summary>Default number of top anomaly keys included in each summary line.</summary>
    public const int DefaultTopKeys = 10;

    private readonly ILogger<AnomalySummaryFeature> _logger;
    private readonly Dictionary<string, long> _lastCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _errorWarnedKeys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private ulong _elapsedMs;

    [ActivatorUtilitiesConstructor]
    public AnomalySummaryFeature(IServiceProvider services, ILogger<AnomalySummaryFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Ledger = services.GetService<AnomalyLedger>() ?? AnomalyLedger.Shared;
    }

    internal AnomalySummaryFeature(ILogger<AnomalySummaryFeature> logger, AnomalyLedger? ledger = null, TimeSpan? interval = null, int topKeys = DefaultTopKeys)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Ledger = ledger ?? AnomalyLedger.Shared;
        if (interval is { } span)
        {
            SummaryIntervalMs = (uint)Math.Clamp((long)span.TotalMilliseconds, 1L, uint.MaxValue);
        }

        TopKeys = Math.Max(1, topKeys);
    }

    /// <summary>The ledger summarized by this feature (defaults to <see cref="AnomalyLedger.Shared"/>).</summary>
    public AnomalyLedger Ledger { get; set; }

    /// <summary>Summary interval in milliseconds (defaults to <see cref="DefaultSummaryIntervalMs"/>).</summary>
    public uint SummaryIntervalMs { get; set; } = DefaultSummaryIntervalMs;

    /// <summary>Maximum keys listed in the periodic summary line (defaults to <see cref="DefaultTopKeys"/>).</summary>
    public int TopKeys { get; set; } = DefaultTopKeys;

    public static void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton(AnomalyLedger.Shared);
        _ = PlayerbotAnomalyHistory.Shared;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _ = PlayerbotAnomalyHistory.Shared;
        world.WorldTick += OnTick;
    }

    /// <summary>World-tick handler accumulating elapsed time and triggering a summary pass every <see cref="SummaryIntervalMs"/>.</summary>
    public void OnTick(uint diffMs)
    {
        try
        {
            uint interval = Math.Max(1u, SummaryIntervalMs);
            _elapsedMs += diffMs;
            if (_elapsedMs < interval)
            {
                return;
            }

            _elapsedMs %= interval;
            RunSummaryPass();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("anomaly-summary-tick", ex);
        }
    }

    /// <summary>
    /// Evaluates the current <see cref="Ledger"/> snapshot, logs a <see cref="LogLevel.Warning"/> for any key
    /// seen for the first time at <see cref="AnomalySeverity.Error"/>, and logs one <see cref="LogLevel.Information"/>
    /// summary line with the top <see cref="TopKeys"/> keys by count delta when the total delta is positive.
    /// Returns the total delta observed in this pass.
    /// </summary>
    public long RunSummaryPass()
    {
        IReadOnlyList<AnomalySnapshot> snapshots = Ledger.Snapshot();
        var newErrors = new List<AnomalySnapshot>();
        var deltas = new List<(AnomalySnapshot Snapshot, long Delta)>();
        long totalDelta = 0;

        lock (_gate)
        {
            if (snapshots.Count == 0 && _lastCounts.Count > 0)
            {
                _lastCounts.Clear();
                _errorWarnedKeys.Clear();
            }

            var currentKeys = new HashSet<string>(snapshots.Count, StringComparer.Ordinal);
            foreach (AnomalySnapshot snap in snapshots)
            {
                currentKeys.Add(snap.Key);

                if (snap.Severity == AnomalySeverity.Error && _errorWarnedKeys.Add(snap.Key))
                {
                    newErrors.Add(snap);
                }

                long prev = 0;
                if (_lastCounts.TryGetValue(snap.Key, out long recordedPrev))
                {
                    prev = snap.Count >= recordedPrev ? recordedPrev : 0;
                }

                long delta = snap.Count - prev;
                _lastCounts[snap.Key] = snap.Count;
                if (delta > 0)
                {
                    deltas.Add((snap, delta));
                    totalDelta += delta;
                }
            }

            if (_lastCounts.Count > currentKeys.Count)
            {
                string[] stale = [.. _lastCounts.Keys.Where(k => !currentKeys.Contains(k))];
                foreach (string key in stale)
                {
                    _lastCounts.Remove(key);
                    _errorWarnedKeys.Remove(key);
                }
            }
        }

        foreach (AnomalySnapshot err in newErrors)
        {
            string subject = err.LastSubject.Length == 0 ? "(none)" : err.LastSubject;
            _logger.LogWarning(
                "Anomaly error key {AnomalyKey} first observed (count={Count}, subjects={DistinctSubjects}, subject={LastSubject}): {LastDetail}",
                err.Key,
                err.Count,
                err.DistinctSubjects,
                subject,
                err.LastDetail);
        }

        if (totalDelta <= 0)
        {
            return 0;
        }

        int take = Math.Max(1, TopKeys);
        var topItems = deltas
            .OrderByDescending(d => d.Delta)
            .ThenBy(d => d.Snapshot.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(d => string.Create(
                CultureInfo.InvariantCulture,
                $"{d.Snapshot.Key}=+{d.Delta} (total={d.Snapshot.Count}, subjects={d.Snapshot.DistinctSubjects}, severity={d.Snapshot.Severity}, rate={d.Snapshot.Timing.RatePerHour15Min:F1}/h 15 min {d.Snapshot.Timing.RatePerHour1Hour:F1}/h 1 h)"))
            .ToArray();

        LedgerTiming ledgerTiming = Ledger.Timing();
        _logger.LogInformation(
            "Anomaly summary (+{TotalDelta} new across {ActiveKeys} keys; ledger total={LedgerTotal}, distinct={LedgerDistinct}): {TopKeys}; ledger rate {LedgerRate15m:F1}/h last 15 min, {LedgerRate1h:F1}/h last 1 h",
            totalDelta,
            deltas.Count,
            Ledger.Total,
            Ledger.Distinct,
            string.Join(", ", topItems),
            ledgerTiming.RatePerHour15Min,
            ledgerTiming.RatePerHour1Hour);

        return totalDelta;
    }
}
