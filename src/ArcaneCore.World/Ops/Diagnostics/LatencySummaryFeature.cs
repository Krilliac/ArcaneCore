using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// Periodic operator log summary of the latency histograms in <see cref="LatencyRegistry"/> (character saves, SQLite checkpoints, navigation path queries, login to in-world,
/// packet handlers by opcode): once per interval (5 minutes by default) one <see cref="LogLevel.Information"/> line per metric that recorded anything in the last 15 minutes
/// (the full percentile line of <see cref="LatencySnapshot.Format"/> plus the totals since start), and one line naming the slowest packet handlers by p99. Nothing is logged for an
/// idle metric and nothing per event: the instrumented call sites only record into the histograms.
/// <para>
/// Thread affinity: a world-tick handler like <see cref="AnomalySummaryFeature"/> (the tick only adds elapsed time; a pass reads the histograms lock-free and costs
/// well under a millisecond: one 15 minute window merge per histogram ranked or printed).
/// </para>
/// </summary>
public sealed class LatencySummaryFeature : IWorldFeature
{
    /// <summary>Default summary period in milliseconds (5 minutes).</summary>
    public const uint DefaultSummaryIntervalMs = 5 * 60 * 1_000;

    /// <summary>Packet handlers named in the summary line.</summary>
    public const int TopOpcodes = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ILogger<LatencySummaryFeature> _logger;
    private ulong _elapsedMs;

    [ActivatorUtilitiesConstructor]
    public LatencySummaryFeature(IServiceProvider services, ILogger<LatencySummaryFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Registry = services.GetService<LatencyRegistry>() ?? LatencyRegistry.Shared;
    }

    internal LatencySummaryFeature(ILogger<LatencySummaryFeature> logger, LatencyRegistry? registry = null, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Registry = registry ?? LatencyRegistry.Shared;
        if (interval is { } span)
        {
            SummaryIntervalMs = (uint)Math.Clamp((long)span.TotalMilliseconds, 1L, uint.MaxValue);
        }
    }

    /// <summary>The registry summarized by this feature (defaults to <see cref="LatencyRegistry.Shared"/>).</summary>
    public LatencyRegistry Registry { get; set; }

    /// <summary>Summary interval in milliseconds (defaults to <see cref="DefaultSummaryIntervalMs"/>).</summary>
    public uint SummaryIntervalMs { get; set; } = DefaultSummaryIntervalMs;

    public static void ConfigureServices(IServiceCollection services) => services.TryAddSingleton(LatencyRegistry.Shared);

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.WorldTick += OnTick;
    }

    /// <summary>World-tick handler accumulating elapsed time and running a pass every <see cref="SummaryIntervalMs"/>.</summary>
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
            FaultLedger.Shared.Record("latency.summary-tick", ex);
        }
    }

    /// <summary>Log the summary now; returns the number of lines written (0 when nothing recorded anything in the last 15 minutes).</summary>
    public int RunSummaryPass()
    {
        int lines = 0;
        foreach (KeyValuePair<string, LatencyHistogram> pair in Registry.Histograms())
        {
            if (pair.Key.StartsWith(LatencyNames.PacketHandlerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            LatencySnapshot recent = pair.Value.Snapshot(Window);
            if (recent.IsEmpty)
            {
                continue;
            }

            if (pair.Key == LatencyRegistry.OverflowName)
            {
                // Names beyond the registry caps share this compact histogram, which keeps no since-start data.
                _logger.LogInformation("Latency {Metric}, last 15 min: {Latency} [names beyond the registry caps share this histogram; no totals kept]", pair.Key, recent.Format());
                lines++;
                continue;
            }

            LatencySnapshot total = pair.Value.Snapshot();
            _logger.LogInformation("Latency {Metric}, last 15 min: {Latency} [since start: {TotalCount} samples, p99 {TotalP99Ms} ms, max {TotalMaxMs} ms]",
                pair.Key, recent.Format(), total.Count, LatencySnapshot.Millis(total.P99), LatencySnapshot.Millis(total.Max));
            lines++;
        }

        var top = new List<string>(TopOpcodes);
        foreach ((string name, _, LatencySnapshot recent) in Registry.TopByP99(LatencyNames.PacketHandlerPrefix, TopOpcodes, Window))
        {
            top.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name[LatencyNames.PacketHandlerPrefix.Length..]} p99 {LatencySnapshot.Millis(recent.P99)} ms (p50 {LatencySnapshot.Millis(recent.P50)}, max {LatencySnapshot.Millis(recent.Max)}, n={recent.Count})"));
        }

        if (top.Count > 0)
        {
            _logger.LogInformation("Packet handler latency, top {Count} opcodes by p99 (last 15 min): {Top}", top.Count, string.Join(", ", top));
            lines++;
        }

        return lines;
    }
}
