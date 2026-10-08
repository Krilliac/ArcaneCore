using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using ArcaneCore.Game.Maps;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>A fact that runs only when <see cref="Variable"/> asks for the GC-mode measurement (seconds of wall time).</summary>
internal sealed class GcModeFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_GC_MODE_SECONDS";

    public GcModeFactAttribute()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(Variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
        {
            Skip = $"{Variable} is not set: the GC-mode measurement did NOT run (docs/integration/perf-limits-20261008.md).";
        }
    }
}

/// <summary>
/// The world loop under a live-sized managed heap, for comparing garbage collector settings (run the same test with
/// DOTNET_gcServer / DOTNET_gcConcurrent / DOTNET_GCHeapCount / DOTNET_GCDynamicAdaptationMode in the environment). Not a
/// correctness test, and synthetic: the live World's heap (1.2-1.4 GB, gen2 ~1.0 GB, LOH 355-420 MB, ~0.95 MiB allocated per
/// tick, live stress test 2026-10-08) is imitated by <c>ARCANECORE_GC_MODE_HEAP_MB</c> (default 1200) of retained objects, 35 %
/// of it in large-object arrays, while every 50 ms tick on the real world thread allocates <c>ARCANECORE_GC_MODE_ALLOC_KB</c>
/// (default 960) of short-lived objects, replaces <c>ARCANECORE_GC_MODE_CHURN</c> (default 4000) retained small objects (so
/// garbage reaches gen2 and background collections run) and every 20 ticks one large array, and spins 3 ms. Reported: tick
/// work and frame percentiles, ticks over the 50 ms budget, GC counts and pause time (process-wide), the longest GC pause that
/// fell inside one tick, peak working set and CPU. <c>ARCANECORE_GC_MODE_CSV</c> appends one row to that file.
/// </summary>
[Collection("World tick load")]
public sealed class GcModeMeasurementTests(ITestOutputHelper output)
{
    private sealed class Node
    {
        public long A;
        public Node? Next;
        public object? Payload;
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    [GcModeFact]
    public void LiveSizedHeap_TickPauses_AreReported()
    {
        int seconds = EnvInt(GcModeFactAttribute.Variable, 120);
        int heapMb = EnvInt("ARCANECORE_GC_MODE_HEAP_MB", 1200);
        int allocKb = EnvInt("ARCANECORE_GC_MODE_ALLOC_KB", 960);
        int churn = EnvInt("ARCANECORE_GC_MODE_CHURN", 4000);
        string label = Environment.GetEnvironmentVariable("ARCANECORE_GC_MODE_LABEL") ?? "run";
        var random = new Random(5875);

        // Retained heap: 65 % small objects (a Node of ~48 bytes plus a 32..96 byte payload), 35 % 100 KB large-object arrays.
        long smallBytes = heapMb * 1024L * 1024 * 65 / 100;
        int nodes = (int)(smallBytes / 160);
        var retained = new Node[nodes];
        for (int i = 0; i < nodes; i++)
        {
            retained[i] = new Node { A = i, Next = i > 0 ? retained[i - 1] : null, Payload = new byte[32 + random.Next(64)] };
        }

        int largeCount = (int)(heapMb * 1024L * 1024 * 35 / 100 / (100 * 1024));
        var large = new byte[largeCount][];
        for (int i = 0; i < largeCount; i++)
        {
            large[i] = new byte[100 * 1024];
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long heapAfterBuild = GC.GetTotalMemory(false);

        using WorldRuntime world = TestWorld.CreateRuntime();
        var work = new List<long>(seconds * 25);
        var frames = new List<long>(seconds * 25);
        var pauses = new List<long>(seconds * 25);
        long lastStart = 0;
        long tick = 0;
        long spinTicks = 3 * Stopwatch.Frequency / 1000;
        object?[] scratch = new object?[64];
        world.WorldTick += _ =>
        {
            long start = Stopwatch.GetTimestamp();
            TimeSpan pauseBefore = GC.GetTotalPauseDuration();
            if (lastStart != 0)
            {
                frames.Add((start - lastStart) * 1_000_000 / Stopwatch.Frequency);
            }

            lastStart = start;
            // Short-lived allocation: small arrays and strings, the shapes a tick's packets, lists and closures make.
            long budget = allocKb * 1024L;
            for (long made = 0; made < budget;)
            {
                int size = 16 + random.Next(240);
                scratch[random.Next(scratch.Length)] = (made & 1) == 0 ? new byte[size] : new string('x', size / 2);
                made += size + 24;
            }

            for (int i = 0; i < churn; i++)
            {
                int at = random.Next(nodes);
                retained[at] = new Node { A = tick, Next = retained[random.Next(nodes)], Payload = new byte[32 + random.Next(64)] };
            }

            if (++tick % 20 == 0)
            {
                large[random.Next(largeCount)] = new byte[100 * 1024];
            }

            while (Stopwatch.GetTimestamp() - start < spinTicks)
            {
                Thread.SpinWait(20);
            }

            work.Add((Stopwatch.GetTimestamp() - start) * 1_000_000 / Stopwatch.Frequency);
            pauses.Add((long)((GC.GetTotalPauseDuration() - pauseBefore).TotalMicroseconds));
        };

        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        TimeSpan pauseStart = GC.GetTotalPauseDuration();
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long peak = 0;
        var wall = Stopwatch.StartNew();
        world.Start();
        while (wall.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.Sleep(500);
            process.Refresh();
            peak = Math.Max(peak, process.WorkingSet64);
        }

        world.Stop();
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / wall.Elapsed.TotalSeconds;
        GC.KeepAlive(retained);
        GC.KeepAlive(large);

        long[] w = [.. work.Skip(20).Order()];
        long[] f = [.. frames.Skip(20).Order()];
        long[] p = [.. pauses.Skip(20).Order()];
        Assert.True(w.Length > 100, "too few ticks recorded");
        string mode = $"{(GCSettings.IsServerGC ? "server" : "workstation")}/{(GCSettings.LatencyMode == GCLatencyMode.Batch ? "nonconcurrent" : "concurrent")}"
            + $"/heaps={Environment.GetEnvironmentVariable("DOTNET_GCHeapCount") ?? "default"}/datas={Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode") ?? "default"}";
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{label},{mode},{heapAfterBuild / (1024 * 1024)},{w.Length},{Ms(w, 50)},{Ms(w, 99)},{Ms(w, 99.9)},{w[^1] / 1000d:F1},{w.Count(x => x > 50_000)},{Ms(f, 50)},{Ms(f, 99)},{f[^1] / 1000d:F1},{GC.CollectionCount(0) - gc0},{GC.CollectionCount(1) - gc1},{GC.CollectionCount(2) - gc2},{(GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds:F0},{p[^1] / 1000d:F1},{peak / (1024 * 1024)},{cpu:F0}");
        output.WriteLine("label,gc,heap_mib,ticks,work_p50_ms,work_p99_ms,work_p999_ms,work_max_ms,work_over_50ms,frame_p50_ms,frame_p99_ms,frame_max_ms,gc0,gc1,gc2,gc_pause_total_ms,gc_pause_max_in_tick_ms,peak_ws_mib,cpu_ms_per_s");
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ARCANECORE_GC_MODE_CSV") is { Length: > 0 } csv)
        {
            File.AppendAllText(csv, line + Environment.NewLine);
        }
    }

    private static string Ms(long[] sorted, double percentile)
        => (TickStats.NearestRank(sorted, percentile) / 1000d).ToString("F2", CultureInfo.InvariantCulture);
}
