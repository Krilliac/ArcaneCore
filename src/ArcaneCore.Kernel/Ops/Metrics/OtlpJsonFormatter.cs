using System.Globalization;
using System.Text.Json;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>Renders a collection as an OTLP/HTTP JSON <c>ExportMetricsServiceRequest</c> (cumulative temporality).</summary>
public static class OtlpJsonFormatter
{
    public static byte[] Render(IReadOnlyList<MetricSnapshot> metrics, string serviceName, string realm, DateTimeOffset start, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            string startNanos = Nanos(start);
            string nowNanos = Nanos(now);
            w.WriteStartObject();
            w.WriteStartArray("resourceMetrics");
            w.WriteStartObject();
            w.WriteStartObject("resource");
            w.WriteStartArray("attributes");
            Attribute(w, "service.name", serviceName);
            if (realm.Length > 0)
            {
                Attribute(w, "realm", realm);
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("scopeMetrics");
            foreach (IGrouping<string, MetricSnapshot> scope in metrics.GroupBy(m => m.MeterName, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteStartObject("scope");
                w.WriteString("name", scope.Key);
                w.WriteEndObject();
                w.WriteStartArray("metrics");
                foreach (MetricSnapshot metric in scope)
                {
                    WriteMetric(w, metric, startNanos, nowNanos);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteMetric(Utf8JsonWriter w, MetricSnapshot metric, string startNanos, string nowNanos)
    {
        w.WriteStartObject();
        w.WriteString("name", metric.Name);
        if (metric.Unit is { Length: > 0 } unit)
        {
            w.WriteString("unit", unit);
        }

        if (metric.Description is { Length: > 0 } description)
        {
            w.WriteString("description", description);
        }

        switch (metric.Kind)
        {
            case MetricKind.Histogram:
                w.WriteStartObject("histogram");
                w.WriteNumber("aggregationTemporality", 2);
                w.WriteStartArray("dataPoints");
                foreach (SeriesSnapshot s in metric.Series)
                {
                    w.WriteStartObject();
                    Points(w, s, startNanos, nowNanos);
                    w.WriteString("count", s.Count.ToString(CultureInfo.InvariantCulture));
                    w.WriteNumber("sum", s.Sum);
                    w.WriteStartArray("bucketCounts");
                    foreach (long c in s.BucketCounts)
                    {
                        w.WriteStringValue(c.ToString(CultureInfo.InvariantCulture));
                    }

                    w.WriteEndArray();
                    w.WriteStartArray("explicitBounds");
                    foreach (double b in metric.Bounds)
                    {
                        w.WriteNumberValue(b);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case MetricKind.Gauge:
                w.WriteStartObject("gauge");
                Values(w, metric, startNanos, nowNanos);
                w.WriteEndObject();
                break;
            default:
                w.WriteStartObject("sum");
                w.WriteNumber("aggregationTemporality", 2);
                w.WriteBoolean("isMonotonic", metric.Kind == MetricKind.Counter);
                Values(w, metric, startNanos, nowNanos);
                w.WriteEndObject();
                break;
        }

        w.WriteEndObject();
    }

    private static void Values(Utf8JsonWriter w, MetricSnapshot metric, string startNanos, string nowNanos)
    {
        w.WriteStartArray("dataPoints");
        foreach (SeriesSnapshot s in metric.Series)
        {
            w.WriteStartObject();
            Points(w, s, startNanos, nowNanos);
            w.WriteNumber("asDouble", s.Value);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Points(Utf8JsonWriter w, SeriesSnapshot s, string startNanos, string nowNanos)
    {
        w.WriteStartArray("attributes");
        foreach (KeyValuePair<string, string> tag in s.Tags)
        {
            Attribute(w, tag.Key, tag.Value);
        }

        w.WriteEndArray();
        w.WriteString("startTimeUnixNano", startNanos);
        w.WriteString("timeUnixNano", nowNanos);
    }

    private static void Attribute(Utf8JsonWriter w, string key, string value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteStartObject("value");
        w.WriteString("stringValue", value);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static string Nanos(DateTimeOffset time) => ((time.ToUnixTimeMilliseconds() * 1_000_000L)).ToString(CultureInfo.InvariantCulture);
}
