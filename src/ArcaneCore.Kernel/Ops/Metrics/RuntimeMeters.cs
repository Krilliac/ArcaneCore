using System.Diagnostics.Metrics;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// GC and allocation instruments, observed at collection time only (no hot-path cost): collections per generation,
/// managed heap size, total allocated bytes, GC pause time, the thread-pool queue and the working set.
/// One meter per process (created on first use).
/// </summary>
public static class RuntimeMeters
{
    private static readonly Lazy<Meter> Instance = new(Create);

    /// <summary>The runtime meter (created and populated once).</summary>
    public static Meter Meter => Instance.Value;

    private static Meter Create()
    {
        var meter = new Meter("ArcaneCore.Runtime", "1.0");
        meter.CreateObservableCounter("arcanecore.gc.collections", ObserveCollections, "{collection}", "GC collections by generation.");
        meter.CreateObservableGauge("arcanecore.gc.heap_size", () => GC.GetGCMemoryInfo().HeapSizeBytes, "By", "Managed heap size after the last GC.");
        meter.CreateObservableCounter("arcanecore.gc.allocated", () => GC.GetTotalAllocatedBytes(), "By", "Bytes allocated by every thread since start.");
        meter.CreateObservableCounter("arcanecore.gc.pause_time", () => GC.GetTotalPauseDuration().TotalSeconds, "s", "Time the runtime has been paused for GC.");
        meter.CreateObservableGauge("arcanecore.threadpool.queue_length", () => ThreadPool.PendingWorkItemCount, "{item}", "Thread-pool work items queued.");
        meter.CreateObservableGauge("arcanecore.process.working_set", () => Environment.WorkingSet, "By", "Process working set.");
        return meter;
    }

    private static IEnumerable<Measurement<long>> ObserveCollections()
    {
        for (int generation = 0; generation <= GC.MaxGeneration; generation++)
        {
            yield return new Measurement<long>(GC.CollectionCount(generation), new KeyValuePair<string, object?>("generation", "gen" + generation));
        }
    }
}
