using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game.Maps;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>A fact that runs only when <see cref="Variable"/> asks for a real-clock cadence measurement (seconds of wall time).</summary>
internal sealed class TickCadenceFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TICK_CADENCE_SECONDS";

    public TickCadenceFactAttribute()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(Variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
        {
            Skip = $"{Variable} is not set: the real-clock tick cadence measurement did NOT run (docs/integration/perf-limits-20261008.md).";
        }
    }
}

/// <summary>
/// The world loop's cadence on the real clock (not a correctness test: wall time is only reported). A real
/// <see cref="WorldRuntime"/> runs its own thread at 50 ms with a synthetic busy tick of
/// <c>ARCANECORE_TICK_CADENCE_WORK_MS</c> (default 3, about the live mean) for <see cref="TickCadenceFactAttribute.Variable"/>
/// seconds. The frame interval is the time between two tick starts, taken from <see cref="Stopwatch"/> at
/// <see cref="WorldRuntime.WorldTick"/>; it is reported as nearest-rank percentiles with the loop's own late and frame
/// overrun counts and the process CPU time per second of wall time (the price of a precise wait).
/// <c>ARCANECORE_TICK_CADENCE_CSV</c> appends one summary row to that file.
/// </summary>
[Collection("World tick load")]
public sealed class TickCadenceMeasurementTests(ITestOutputHelper output)
{
    [TickCadenceFact]
    public void RealClockCadence_IsReported()
    {
        int seconds = int.Parse(Environment.GetEnvironmentVariable(TickCadenceFactAttribute.Variable)!, CultureInfo.InvariantCulture);
        double workMs = double.TryParse(Environment.GetEnvironmentVariable("ARCANECORE_TICK_CADENCE_WORK_MS"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double parsed) ? parsed : 3;
        string label = Environment.GetEnvironmentVariable("ARCANECORE_TICK_CADENCE_LABEL") ?? "run";
        using WorldRuntime world = TestWorld.CreateRuntime();
        string? timer = Environment.GetEnvironmentVariable("ARCANECORE_TICK_CADENCE_TIMER");
        if (!string.IsNullOrEmpty(timer))
        {
            world.Options.TickTimer = Enum.Parse<WorldTickTimer>(timer, ignoreCase: true);
        }

        if (long.TryParse(Environment.GetEnvironmentVariable("ARCANECORE_TICK_CADENCE_SPIN_US"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long spin))
        {
            world.TickSpinMarginMicros = spin;
        }
        var starts = new List<long>(seconds * 25);
        long workTicks = (long)(workMs * Stopwatch.Frequency / 1000);
        world.WorldTick += _ =>
        {
            long now = Stopwatch.GetTimestamp();
            starts.Add(now);
            while (Stopwatch.GetTimestamp() - now < workTicks)
            {
                Thread.SpinWait(20);
            }
        };

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        world.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        world.Stop();
        wall.Stop();
        process.Refresh();
        double cpuPerSecond = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / wall.Elapsed.TotalSeconds;

        long[] frames = starts.Zip(starts.Skip(1), (a, b) => (b - a) * 1_000_000 / Stopwatch.Frequency).Skip(5).ToArray();
        Assert.True(frames.Length > 10, "too few ticks recorded");
        long[] sorted = frames.Order().ToArray();
        long interval = world.Options.TickIntervalMs * 1000L;
        int over2 = frames.Count(f => f > interval + 2000);
        int over5 = frames.Count(f => f > interval + 5000);
        TickStatsSnapshot stats = world.Stats.Snapshot();
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{label},{world.EffectiveTickTimer}/{world.TickSpinMarginMicros}us,{workMs},{frames.Length},{frames.Average() / 1000:F3},{Pct(sorted, 1)},{Pct(sorted, 10)},{Pct(sorted, 50)},{Pct(sorted, 90)},{Pct(sorted, 99)},{sorted[^1] / 1000d:F3},{over2},{over5},{world.Scheduler.LateTicks},{world.Scheduler.SkippedTicks},{stats.FrameOverruns},{cpuPerSecond:F1}");
        output.WriteLine("label,timer,work_ms,frames,mean_ms,p1_ms,p10_ms,p50_ms,p90_ms,p99_ms,max_ms,over_interval_plus_2ms,over_interval_plus_5ms,scheduler_late,scheduler_skipped,frame_overruns,process_cpu_ms_per_s");
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ARCANECORE_TICK_CADENCE_CSV") is { Length: > 0 } csv)
        {
            File.AppendAllText(csv, line + Environment.NewLine);
        }
    }

    private static string Pct(long[] sorted, double percentile)
        => (TickStats.NearestRank(sorted, percentile) / 1000d).ToString("F3", CultureInfo.InvariantCulture);
}
