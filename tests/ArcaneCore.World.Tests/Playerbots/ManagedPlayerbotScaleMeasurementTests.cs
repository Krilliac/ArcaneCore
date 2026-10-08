using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>A fact that runs only when <see cref="Variable"/> lists the running-bot counts to measure ("25,50,100").</summary>
internal sealed class BotScaleFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_BOT_SCALE";

    public BotScaleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"{Variable} is not set: the managed-bot scale measurement did NOT run (docs/integration/perf-limits-20261008.md).";
        }
    }
}

/// <summary>
/// What running managed bots cost the world tick (the evidence for <see cref="PlayerbotOptions.MaxBotsCeiling"/>). Not a
/// correctness test: wall time is only reported. One world test host (in-memory stores, no terrain, 50 ms real-clock tick)
/// creates and starts autonomous bots up to each count of <see cref="BotScaleFactAttribute.Variable"/>, lets them run for
/// <c>ARCANECORE_BOT_SCALE_HOLD_SECONDS</c> (default 15) and reports, per step, the tick work from the world-level tick event
/// to the end of the world features (nearest-rank percentiles), the managed-bot feature's smoothed time, the bytes the world
/// thread allocated per tick, GC collections and the managed heap. <c>ARCANECORE_BOT_SCALE_CSV</c> appends the rows to a file.
/// </summary>
[Collection("Managed bot scale")]
public sealed class ManagedPlayerbotScaleMeasurementTests(ITestOutputHelper output)
{
    [BotScaleFact]
    public async Task RunningBots_TickCost_IsReported()
    {
        int[] steps = Environment.GetEnvironmentVariable(BotScaleFactAttribute.Variable)!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).Order().ToArray();
        int hold = int.TryParse(Environment.GetEnvironmentVariable("ARCANECORE_BOT_SCALE_HOLD_SECONDS"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 15;
        string? csv = Environment.GetEnvironmentVariable("ARCANECORE_BOT_SCALE_CSV");
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        int max = steps[^1];
        await using WorldTestHost host = WorldTestHost.Start(
            configure: o => o.TickIntervalMs = 50,
            configureServices: services =>
            {
                services.AddSingleton<IAccountStore>(accounts);
                services.AddSingleton<IAccountAdmin>(accounts);
                services.AddSingleton<ICharacterStore>(characters);
                services.AddSingleton<ICharacterLifeStore>(characters);
                services.AddSingleton<IManagedPlayerbotStore>(owners);
                services.AddSingleton<IManagedPlayerbotProvisionStore>(new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));
                services.AddSingleton<Microsoft.Extensions.Options.IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(new PlayerbotOptions
                {
                    Enabled = true, MaxBots = max, MaxRegisteredBots = Math.Max(max, 1), AllowedMaps = [0, 1],
                }));
            });
        ManagedPlayerbotFeature bots = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await bots.StartupAsync(default);

        var recorder = new TickRecorder();
        host.World.WorldTick += recorder.Begin;
        host.World.Updated += recorder.End;
        output.WriteLine("bots,ticks,work_mean_ms,work_p50_ms,work_p95_ms,work_p99_ms,work_max_ms,botfeature_ema_ms,alloc_per_tick_kib,gc0,gc1,gc2,heap_mib,create_start_s");
        int running = 0;
        try
        {
            foreach (int target in steps)
            {
                var ramp = Stopwatch.StartNew();
                for (; running < target; running++)
                {
                    PlayerbotOperationResult created = await bots.CreateAsync(Name(running), (byte)(running % 2 == 0 ? 1 : 2), 1);
                    Assert.True(created.Success, $"create {running}: {created.Code}");
                    PlayerbotOperationResult started = await bots.StartAsync(created.BotId!.Value.ToString());
                    Assert.True(started.Success, $"start {running}: {started.Code}");
                }

                ramp.Stop();
                await Task.Delay(TimeSpan.FromSeconds(2)); // settle the logins
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                using AllocationSampler? sampler = Environment.GetEnvironmentVariable("ARCANECORE_BOT_SCALE_ALLOC_TYPES") == "1" ? new AllocationSampler() : null;
                recorder.Reset();
                await Task.Delay(TimeSpan.FromSeconds(hold));
                (long[] work, long allocated) = recorder.Take();
                if (sampler is not null)
                {
                    foreach ((string type, long bytes) in sampler.Top(15))
                    {
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  alloc-sample {running} bots: {bytes / 1024} KiB {type}"));
                    }
                }

                TickStatsSnapshot snapshot = host.World.Stats.Snapshot();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  phases alloc/tick: commands {snapshot.CommandsMeanBytes / 1024:F1} KiB, maps {snapshot.MapsMeanBytes / 1024:F1} KiB, features {snapshot.FeaturesMeanBytes / 1024:F1} KiB; top features: {string.Join(", ", snapshot.FeatureAllocations.Take(3).Select(f => $"{f.Name} {f.MeanBytes / 1024:F1} KiB"))}"));
                Assert.True(work.Length > 10, "too few ticks recorded");
                long[] sorted = work.Order().ToArray();
                double feature = host.World.Stats.Snapshot().FeatureMeans.FirstOrDefault(f => f.Name == nameof(ManagedPlayerbotFeature)).MeanMicros;
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{running},{work.Length},{work.Average() / 1000:F3},{Ms(sorted, 50)},{Ms(sorted, 95)},{Ms(sorted, 99)},{sorted[^1] / 1000d:F3},{feature / 1000:F3},{allocated / (double)work.Length / 1024:F1},{GC.CollectionCount(0) - gc0},{GC.CollectionCount(1) - gc1},{GC.CollectionCount(2) - gc2},{GC.GetTotalMemory(false) / (1024d * 1024):F0},{ramp.Elapsed.TotalSeconds:F1}");
                output.WriteLine(line);
                if (!string.IsNullOrEmpty(csv))
                {
                    File.AppendAllText(csv, line + Environment.NewLine);
                }
            }

            Assert.Equal(running, bots.Snapshot().Count(b => b.State == ManagedPlayerbotState.Running));
        }
        finally
        {
            host.World.WorldTick -= recorder.Begin;
            host.World.Updated -= recorder.End;
            await bots.ShutdownBeforeWorldStopAsync();
        }
    }

    private static string Ms(long[] sorted, double percentile)
        => (TickStats.NearestRank(sorted, percentile) / 1000d).ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Distinct letters-only character names: "Scale" + base-26 index ("Scaleaaa", "Scaleaab", ...).</summary>
    private static string Name(int index)
    {
        char[] suffix = new char[3];
        for (int i = 2; i >= 0; i--)
        {
            suffix[i] = (char)('a' + (index % 26));
            index /= 26;
        }

        return "Scale" + new string(suffix);
    }

    /// <summary>World thread only: the time and the world thread's allocations from the world-level tick event to the end of the features.</summary>
    private sealed class TickRecorder
    {
        private readonly object _gate = new();
        private readonly List<long> _work = new(4096);
        private long _allocated;
        private long _start;
        private long _startBytes;

        public void Begin(uint diff)
        {
            _start = Stopwatch.GetTimestamp();
            _startBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void End(uint diff)
        {
            if (_start == 0)
            {
                return;
            }

            long micros = (Stopwatch.GetTimestamp() - _start) * 1_000_000 / Stopwatch.Frequency;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - _startBytes;
            lock (_gate)
            {
                _work.Add(micros);
                _allocated += bytes;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _work.Clear();
                _allocated = 0;
            }
        }

        public (long[] Work, long Allocated) Take()
        {
            lock (_gate)
            {
                return ([.. _work], _allocated);
            }
        }
    }
}

/// <summary>
/// The runtime's GCAllocationTick samples (one per ~100 KB allocated, with the type of the allocation that crossed it) summed by
/// type: an in-process allocation profile without dotnet-trace. Every thread of the process is sampled.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new(StringComparer.Ordinal);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword: AllocationTick is verbose
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || eventData.PayloadNames is null)
        {
            return;
        }

        int typeIndex = eventData.PayloadNames.IndexOf("TypeName");
        int amountIndex = eventData.PayloadNames.IndexOf("AllocationAmount64");
        if (typeIndex < 0 || amountIndex < 0)
        {
            return;
        }

        string type = eventData.Payload![typeIndex] as string ?? "?";
        long amount = Convert.ToInt64(eventData.Payload[amountIndex], CultureInfo.InvariantCulture);
        _bytes.AddOrUpdate(type, amount, (_, current) => current + amount);
    }

    public IEnumerable<(string Type, long Bytes)> Top(int count)
        => _bytes.Select(entry => (entry.Key, entry.Value)).OrderByDescending(entry => entry.Value).Take(count).ToArray();
}

[CollectionDefinition("Managed bot scale", DisableParallelization = true)]
public sealed class ManagedBotScaleCollection;
