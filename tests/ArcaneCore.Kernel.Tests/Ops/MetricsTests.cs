using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>Ops:Metrics (docs/ops/metrics.md): aggregation, the Prometheus and OTLP formats, validation and the scrape endpoint.</summary>
public sealed class MetricsTests
{
    private static string UniqueMeter() => "ArcaneCore.Test." + Guid.NewGuid().ToString("N");

    private static MetricSnapshot Find(IReadOnlyList<MetricSnapshot> metrics, string meter, string name) =>
        Assert.Single(metrics, m => m.MeterName == meter && m.Name == name);

    [Fact]
    public void Store_AggregatesCountersGaugesAndHistograms()
    {
        using var store = new MetricsStore();
        store.Start();
        string meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        Counter<long> counter = meter.CreateCounter<long>("t.count", "{x}");
        Histogram<double> histogram = meter.CreateHistogram<double>("t.duration", "ms");
        int gauge = 7;
        meter.CreateObservableGauge("t.gauge", () => gauge);

        counter.Add(2);
        counter.Add(3);
        counter.Add(1, new KeyValuePair<string, object?>("kind", "a"));
        histogram.Record(0.4);
        histogram.Record(3);
        histogram.Record(9999);

        IReadOnlyList<MetricSnapshot> metrics = store.Collect();
        MetricSnapshot c = Find(metrics, meterName, "t.count");
        Assert.Equal(MetricKind.Counter, c.Kind);
        Assert.Equal(5, Assert.Single(c.Series, s => s.Tags.Count == 0).Value);
        Assert.Equal(1, Assert.Single(c.Series, s => s.Tags.Count == 1 && s.Tags[0].Value == "a").Value);

        MetricSnapshot h = Find(metrics, meterName, "t.duration");
        SeriesSnapshot hs = Assert.Single(h.Series);
        Assert.Equal(3, hs.Count);
        Assert.Equal(1, hs.BucketCounts[0]); // <= 0.5
        Assert.Equal(1, hs.BucketCounts[Array.IndexOf(MetricsStore.DefaultBounds, 5.0)]); // 3 falls in (2, 5]
        Assert.Equal(1, hs.BucketCounts[^1]); // +Inf

        Assert.Equal(7, Assert.Single(Find(metrics, meterName, "t.gauge").Series).Value);
        gauge = 9;
        Assert.Equal(9, Assert.Single(Find(store.Collect(), meterName, "t.gauge").Series).Value);
    }

    [Fact]
    public void Store_DropsObservableSeriesThatAreNoLongerReported()
    {
        using var store = new MetricsStore();
        store.Start();
        string meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        var maps = new List<int> { 0, 1 };
        meter.CreateObservableGauge("t.map", () => maps.Select(m => new Measurement<int>(m, new KeyValuePair<string, object?>("map", m))));
        Assert.Equal(2, Find(store.Collect(), meterName, "t.map").Series.Count);
        maps.Remove(1);
        Assert.Equal("0", Assert.Single(Find(store.Collect(), meterName, "t.map").Series).Tags[0].Value);
    }

    [Fact]
    public void Store_IgnoresMetersOutsideThePrefix()
    {
        using var store = new MetricsStore();
        store.Start();
        using var meter = new Meter("Other." + Guid.NewGuid().ToString("N"));
        meter.CreateCounter<long>("t.count").Add(1);
        Assert.DoesNotContain(store.Collect(), m => m.MeterName == meter.Name);
    }

    [Fact]
    public void Prometheus_RendersTypesSuffixesLabelsAndBuckets()
    {
        var metrics = new List<MetricSnapshot>
        {
            new("ArcaneCore.X", "arcanecore.net.bytes_in", "By", "Bytes \"in\".", MetricKind.Counter, [], [new([], 42, 0, 0, [])]),
            new("ArcaneCore.X", "arcanecore.map.players", "{player}", null, MetricKind.Gauge, [], [new([new("map", "0"), new("instance", "1")], 3, 0, 0, [])]),
            new("ArcaneCore.X", "arcanecore.world.tick.duration", "ms", null, MetricKind.Histogram, [1, 10], [new([], 0, 3, 12.5, [1, 1, 1])]),
        };
        string text = PrometheusFormatter.Render(metrics, "Test \"Realm\"");
        Assert.Contains("# TYPE arcanecore_net_bytes_in_bytes counter\n", text);
        Assert.Contains("arcanecore_net_bytes_in_bytes_total{realm=\"Test \\\"Realm\\\"\"} 42\n", text);
        Assert.Contains("arcanecore_map_players{realm=\"Test \\\"Realm\\\"\",map=\"0\",instance=\"1\"} 3\n", text);
        Assert.Contains("# TYPE arcanecore_world_tick_duration_milliseconds histogram\n", text);
        Assert.Contains("arcanecore_world_tick_duration_milliseconds_bucket{realm=\"Test \\\"Realm\\\"\",le=\"10\"} 2\n", text);
        Assert.Contains("arcanecore_world_tick_duration_milliseconds_bucket{realm=\"Test \\\"Realm\\\"\",le=\"+Inf\"} 3\n", text);
        Assert.Contains("arcanecore_world_tick_duration_milliseconds_count{realm=\"Test \\\"Realm\\\"\"} 3\n", text);
    }

    [Fact]
    public void Otlp_RendersAValidExportRequest()
    {
        var metrics = new List<MetricSnapshot>
        {
            new("ArcaneCore.Net", "arcanecore.net.packets_in", "{packet}", null, MetricKind.Counter, [], [new([], 5, 0, 0, [])]),
            new("ArcaneCore.World", "arcanecore.world.tick.duration", "ms", null, MetricKind.Histogram, [1], [new([new("map", "0")], 0, 2, 3, [1, 1])]),
        };
        byte[] body = OtlpJsonFormatter.Render(metrics, "arcanecore-world", "r", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1));
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement scopes = doc.RootElement.GetProperty("resourceMetrics")[0].GetProperty("scopeMetrics");
        Assert.Equal(2, scopes.GetArrayLength());
        JsonElement sum = scopes[0].GetProperty("metrics")[0].GetProperty("sum");
        Assert.True(sum.GetProperty("isMonotonic").GetBoolean());
        Assert.Equal(5, sum.GetProperty("dataPoints")[0].GetProperty("asDouble").GetDouble());
        JsonElement hist = scopes[1].GetProperty("metrics")[0].GetProperty("histogram").GetProperty("dataPoints")[0];
        Assert.Equal("2", hist.GetProperty("count").GetString());
        Assert.Equal("1000000000", hist.GetProperty("timeUnixNano").GetString());
    }

    [Fact]
    public void Defaults_AreOffAndValid()
    {
        var o = new MetricsOptions();
        Assert.False(o.Enabled);
        Assert.Empty(MetricsOptionsValidation.Problems(o));
        o.Enabled = true;
        Assert.Empty(MetricsOptionsValidation.Problems(o));
    }

    [Theory]
    [InlineData("Exporter", "Influx")]
    [InlineData("OtlpIntervalSeconds", "0")]
    [InlineData("MapSampleIntervalSeconds", "abc")]
    [InlineData("PrometheusPrefix", "localhost:9464")]
    [InlineData("Enabled", "maybe")]
    public void CheckConfig_ListsBadValues(string key, string value)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = key == "Enabled" ? value : "true",
            ["Ops:Metrics:" + key] = value,
        }).Build();
        ConfigIssue issue = Assert.Single(new MetricsOptionsValidation().Check(config));
        Assert.Equal("Ops:Metrics:" + key, issue.Key);
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
    }

    [Fact]
    public void CheckConfig_DisabledSectionDoesNotCheckTheExporterTargets()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = "false",
            ["Ops:Metrics:OtlpEndpoint"] = "not a url",
            ["Ops:Metrics:Exporter"] = "Otlp",
        }).Build();
        Assert.Empty(new MetricsOptionsValidation().Check(config));
    }

    [Fact]
    public async Task Host_ServesThePrometheusEndpoint()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var options = new MetricsOptions { Enabled = true, PrometheusPrefix = $"http://127.0.0.1:{port}/metrics/" };
        using var store = new MetricsStore();
        using var host = new MetricsHost(options, store, NullLogger<MetricsHost>.Instance, "test");
        await host.StartAsync(CancellationToken.None);
        try
        {
            Assert.NotNull(host.PrometheusPrefix);
            ArcaneMeters.PacketOut(10);
            using var http = new HttpClient();
            using HttpResponseMessage response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/metrics/"));
            string text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
            Assert.Contains("arcanecore_net_packets_out_total", text, StringComparison.Ordinal);
            Assert.Contains("arcanecore_gc_collections_total{generation=\"gen0\"}", text, StringComparison.Ordinal);
            Assert.Contains("arcanecore_gc_heap_size_bytes", text, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Host_DisabledStartsNothing()
    {
        using var store = new MetricsStore();
        using var host = new MetricsHost(new MetricsOptions(), store, NullLogger<MetricsHost>.Instance, "test");
        await host.StartAsync(CancellationToken.None);
        Assert.Null(host.PrometheusPrefix);
        await host.StopAsync(CancellationToken.None);
    }
}
