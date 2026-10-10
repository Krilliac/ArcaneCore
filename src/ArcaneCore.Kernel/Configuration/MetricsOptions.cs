namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Ops:Metrics</c> section: server metrics recorded with <c>System.Diagnostics.Metrics</c> (tick and map update
/// time, sessions, packets, the character save queue, GC and allocation, per-map players and creatures) and exported
/// for Prometheus (a scrape endpoint) and/or an OpenTelemetry collector (OTLP/HTTP JSON push). Off by default; read once
/// at start (restart-only). Modelled on TrinityCore <c>src/common/Metric/Metric.cpp</c> (<c>Metric.Enable</c>,
/// <c>Metric.Interval</c>, <c>Metric.ConnectionInfo</c>, <c>Metric.OverallStatusInterval</c>), which pushes to InfluxDB;
/// vmangos and cMaNGOS have no equivalent. See docs/ops/metrics.md.
/// </summary>
[ConfigDaemons(ConfigDaemon.Both)]
public sealed class MetricsOptions
{
    public const string SectionName = "Ops:Metrics";

    /// <summary>The World's default scrape prefix.</summary>
    public const string WorldDefaultPrometheusPrefix = "http://localhost:9464/metrics/";

    /// <summary>The Realm's default scrape prefix: a port of its own, so a shared profile serves both daemons out of the box.</summary>
    public const string RealmDefaultPrometheusPrefix = "http://localhost:9465/metrics/";

    /// <summary>The master switch. <c>false</c>: no listener is attached, so every instrument stays disabled and costs one branch. Default false (TrinityCore <c>Metric.Enable = 0</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>Where the metrics go. Default Prometheus. Values: <c>Prometheus</c> (scrape endpoint), <c>Otlp</c> (push to an OpenTelemetry collector), <c>Both</c>.</summary>
    public MetricsExporter Exporter { get; set; } = MetricsExporter.Prometheus;

    /// <summary>The World daemon's Prometheus scrape listener prefix (an <c>HttpListener</c> prefix ending in <c>/</c>). Use <c>http://+:9464/metrics/</c> to listen on every interface (a container). The Realm daemon does not read this key: it reads <c>RealmPrometheusPrefix</c>. Default <c>http://localhost:9464/metrics/</c>.</summary>
    public string PrometheusPrefix { get; set; } = WorldDefaultPrometheusPrefix;

    /// <summary>The Realm daemon's Prometheus scrape listener prefix, so a Realm and a World started from one shared appsettings.json do not both bind the same port. It never falls back to <c>PrometheusPrefix</c>; setting both to the same prefix makes the second daemon to start log an error and serve no metrics. Default <c>http://localhost:9465/metrics/</c>.</summary>
    public string RealmPrometheusPrefix { get; set; } = RealmDefaultPrometheusPrefix;

    /// <summary>The OTLP/HTTP metrics endpoint the push exporter posts JSON to. Default <c>http://localhost:4318/v1/metrics</c>.</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4318/v1/metrics";

    /// <summary>Seconds between OTLP pushes. 1-3600. Default 15 (TrinityCore <c>Metric.Interval = 1</c> pushes every second to InfluxDB; a collector batches, so a longer period suffices).</summary>
    public int OtlpIntervalSeconds { get; set; } = 15;

    /// <summary>Seconds between per-map samples (players, creatures, objects, update time) taken on the world thread. 1-3600. Default 10 (TrinityCore <c>Metric.OverallStatusInterval = 10</c>).</summary>
    public int MapSampleIntervalSeconds { get; set; } = 10;

    /// <summary><c>false</c>: no per-map series, only the world totals (bounds the label cardinality on a server with many instances). Default true.</summary>
    public bool PerMapMetrics { get; set; } = true;

    /// <summary>A <c>realm</c> label added to every series (TrinityCore tags its metrics with the realm name). Empty: no label. Default empty.</summary>
    public string Realm { get; set; } = "";

    /// <summary>The scrape prefix the daemon of <paramref name="role"/> binds.</summary>
    public string PrometheusPrefixFor(MetricsRole role) => role == MetricsRole.Realm ? RealmPrometheusPrefix : PrometheusPrefix;

    /// <summary>The configuration key that sets the scrape prefix of <paramref name="role"/>.</summary>
    public static string PrometheusPrefixKey(MetricsRole role) =>
        SectionName + ":" + (role == MetricsRole.Realm ? nameof(RealmPrometheusPrefix) : nameof(PrometheusPrefix));
}

/// <summary>The exporter(s) of <c>Ops:Metrics</c>.</summary>
public enum MetricsExporter
{
    /// <summary>Serve the Prometheus text format at <c>PrometheusPrefix</c>.</summary>
    Prometheus,

    /// <summary>Push OTLP/HTTP JSON to <c>OtlpEndpoint</c>.</summary>
    Otlp,

    /// <summary>Both.</summary>
    Both,
}

/// <summary>Which daemon reads <c>Ops:Metrics</c>. Both share the section; each has its own Prometheus endpoint key.</summary>
public enum MetricsRole
{
    /// <summary>ArcaneCore.World: <c>Ops:Metrics:PrometheusPrefix</c>.</summary>
    World,

    /// <summary>ArcaneCore.Realm: <c>Ops:Metrics:RealmPrometheusPrefix</c>.</summary>
    Realm,
}
