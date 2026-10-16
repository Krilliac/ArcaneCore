using System.Globalization;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// Point-in-time snapshot of process, runtime, <see cref="FaultLedger"/>, and <see cref="Invariant"/> health
/// captured by <see cref="SelfDiagnosisReport.Capture"/>.
/// </summary>
public sealed record SelfDiagnosisValues(
    int ProcessorCount,
    int ThreadPoolThreads,
    long ThreadPoolPendingWorkItems,
    bool ThreadPoolStarvationWarning,
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    bool HighMemoryPressureWarning,
    int Gen2Collections,
    double TotalGcPauseMs,
    double LastFullBlockingPauseMs,
    double LastBackgroundPauseMs,
    long TotalFaults,
    int DistinctFaults,
    long OverflowFaults,
    IReadOnlyList<FaultSnapshot> TopFaults,
    long InvariantFailures,
    IReadOnlyList<InvariantFailure> TopInvariants,
    IReadOnlyList<string>? DiagnosisNotes = null,
    IReadOnlyDictionary<string, (int Signatures, long TotalCount)>? FaultsBySubsystem = null);

/// <summary>
/// Thread-safe, non-throwing self-diagnosis builder that inspects process and runtime health
/// (ThreadPool starvation, GC memory load and Gen2 pauses), <see cref="FaultLedger.Shared"/> health,
/// and <see cref="Invariant"/> failures.
/// </summary>
public static class SelfDiagnosisReport
{
    /// <summary>Capture a self-diagnosis snapshot from the runtime, <paramref name="ledger"/> (default <see cref="FaultLedger.Shared"/>), and <see cref="Invariant"/>.</summary>
    public static SelfDiagnosisValues Capture(FaultLedger? ledger = null)
    {
        FaultLedger targetLedger = ledger ?? FaultLedger.Shared;
        var notes = new List<string>();
        int processors = 1;
        int poolThreads = 0;
        long pendingItems = 0;
        try
        {
            processors = Math.Max(1, Environment.ProcessorCount);
            poolThreads = ThreadPool.ThreadCount;
            pendingItems = ThreadPool.PendingWorkItemCount;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"ThreadPool probe failed ({ex.GetType().Name}: {ex.Message})");
        }

        bool starvationWarning = pendingItems > 2L * processors;

        long memoryLoad = 0;
        long highThreshold = 0;
        int gen2 = 0;
        double totalPauseMs = 0;
        double fullBlockingPauseMs = 0;
        double backgroundPauseMs = 0;
        try
        {
            GCMemoryInfo any = GC.GetGCMemoryInfo(GCKind.Any);
            memoryLoad = any.MemoryLoadBytes;
            highThreshold = any.HighMemoryLoadThresholdBytes;
            gen2 = GC.CollectionCount(2);
            totalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
            fullBlockingPauseMs = SumPausesMs(GC.GetGCMemoryInfo(GCKind.FullBlocking));
            backgroundPauseMs = SumPausesMs(GC.GetGCMemoryInfo(GCKind.Background));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"GC probe failed ({ex.GetType().Name}: {ex.Message})");
        }

        bool highMemoryWarning = highThreshold > 0 && memoryLoad >= highThreshold;

        long totalFaults = 0;
        int distinctFaults = 0;
        long overflowFaults = 0;
        IReadOnlyList<FaultSnapshot> topFaults = [];
        IReadOnlyDictionary<string, (int Signatures, long TotalCount)> faultsBySubsystem =
            new SortedDictionary<string, (int Signatures, long TotalCount)>(StringComparer.Ordinal);
        try
        {
            totalFaults = targetLedger.Total;
            distinctFaults = targetLedger.Distinct;
            overflowFaults = targetLedger.Find(FaultLedger.OverflowId)?.Count ?? 0;
            topFaults = [.. targetLedger.Snapshot().Where(f => f.Id != FaultLedger.OverflowId).Take(3)];
            if (totalFaults > 0)
            {
                faultsBySubsystem = targetLedger.BySubsystem();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"FaultLedger probe failed ({ex.GetType().Name}: {ex.Message})");
        }

        long invariantFailures = 0;
        IReadOnlyList<InvariantFailure> topInvariants = [];
        try
        {
            invariantFailures = Invariant.FailureCount;
            topInvariants = [.. Invariant.Failures().Take(3)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"Invariant probe failed ({ex.GetType().Name}: {ex.Message})");
        }

        return new SelfDiagnosisValues(
            processors,
            poolThreads,
            pendingItems,
            starvationWarning,
            memoryLoad,
            highThreshold,
            highMemoryWarning,
            gen2,
            totalPauseMs,
            fullBlockingPauseMs,
            backgroundPauseMs,
            totalFaults,
            distinctFaults,
            overflowFaults,
            topFaults,
            invariantFailures,
            topInvariants,
            notes,
            faultsBySubsystem);
    }

    /// <summary>Capture and format a self-diagnosis report as console/chat lines.</summary>
    public static IReadOnlyList<string> Format(FaultLedger? ledger = null)
        => Format(Capture(ledger));

    /// <summary>Format a captured <see cref="SelfDiagnosisValues"/> snapshot as console/chat lines.</summary>
    public static IReadOnlyList<string> Format(SelfDiagnosisValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var lines = new List<string>(14);
        try
        {
            bool warning = values.ThreadPoolStarvationWarning
                || values.HighMemoryPressureWarning
                || values.TotalFaults > 0
                || values.InvariantFailures > 0
                || values.DiagnosisNotes is { Count: > 0 };
            string poolWarn = values.ThreadPoolStarvationWarning ? " [WARNING: pending > 2x processors]" : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Self-diagnosis: {(warning ? "WARN" : "OK")} | ThreadPool: threads={values.ThreadPoolThreads}, pending={values.ThreadPoolPendingWorkItems}, processors={values.ProcessorCount}{poolWarn}"));

            string memWarn = values.HighMemoryPressureWarning ? " [WARNING: high memory load]" : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"GC health: load={Bytes(values.MemoryLoadBytes)} / threshold={Bytes(values.HighMemoryLoadThresholdBytes)}{memWarn}, gen2={values.Gen2Collections}, totalPause={values.TotalGcPauseMs:F1} ms (last fullBlocking={values.LastFullBlockingPauseMs:F1} ms, background={values.LastBackgroundPauseMs:F1} ms)"));

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Faults: total={values.TotalFaults}, distinct={values.DistinctFaults}, overflow={values.OverflowFaults}"));

            IReadOnlyDictionary<string, (int Signatures, long TotalCount)>? bySubsystem = values.FaultsBySubsystem;
            if ((bySubsystem is null || bySubsystem.Count == 0) && FaultLedger.Shared.Total > 0)
            {
                bySubsystem = FaultLedger.Shared.BySubsystem();
            }

            if ((values.TotalFaults > 0 || FaultLedger.Shared.Total > 0) && bySubsystem is { Count: > 0 })
            {
                string summary = string.Join(", ", bySubsystem.Select(
                    static kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value.TotalCount} ({kv.Value.Signatures} sig)")));
                lines.Add("  subsystems: " + summary);
            }

            foreach (FaultSnapshot fault in values.TopFaults)
            {
                string stack = !string.IsNullOrEmpty(fault.SampleStack)
                    ? fault.SampleStack
                    : fault.Frames.Count > 0 ? string.Join(" <- ", fault.Frames) : "(none)";
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  fault {fault.Id} x{fault.Count} {fault.Site} {fault.ExceptionType} [stack: {stack}]: {fault.SampleMessage}"));
                lines.Add("    " + fault.Timing.Describe());
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Invariants: failures={values.InvariantFailures}"));
            foreach (InvariantFailure inv in values.TopInvariants)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  invariant x{inv.Count} {inv.Member} ({inv.File}:{inv.Line}): {inv.LastMessage}"));
            }

            if (values.DiagnosisNotes is { Count: > 0 })
            {
                foreach (string note in values.DiagnosisNotes)
                {
                    lines.Add("  note: " + note);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"Self-diagnosis formatting error ({ex.GetType().Name}: {ex.Message})");
        }

        return lines;
    }

    private static double SumPausesMs(GCMemoryInfo info)
    {
        double sum = 0;
        foreach (TimeSpan pause in info.PauseDurations)
        {
            sum += pause.TotalMilliseconds;
        }

        return sum;
    }

    private static string Bytes(long bytes)
    {
        double value = bytes;
        return Math.Abs(value) switch
        {
            < 1024d => bytes.ToString(CultureInfo.InvariantCulture) + " B",
            < 1024d * 1024 => (value / 1024d).ToString("F2", CultureInfo.InvariantCulture) + " KiB",
            < 1024d * 1024 * 1024 => (value / (1024d * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " MiB",
            _ => (value / (1024d * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GiB",
        };
    }
}
