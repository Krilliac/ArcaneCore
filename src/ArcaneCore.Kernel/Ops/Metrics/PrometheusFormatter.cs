using System.Globalization;
using System.Text;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>Renders a collection in the Prometheus text exposition format 0.0.4.</summary>
public static class PrometheusFormatter
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The Prometheus name of an instrument: dots and other invalid characters become underscores, plus the unit suffix.</summary>
    public static string NameOf(MetricSnapshot metric)
    {
        var sb = new StringBuilder(metric.Name.Length + 16);
        foreach (char c in metric.Name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == ':' ? c : '_');
        }

        string suffix = metric.Unit switch
        {
            "By" => "_bytes",
            "s" => "_seconds",
            "ms" => "_milliseconds",
            "us" => "_microseconds",
            _ => "",
        };
        if (suffix.Length > 0 && !sb.ToString().EndsWith(suffix, StringComparison.Ordinal))
        {
            sb.Append(suffix);
        }

        return sb.ToString();
    }

    public static string Render(IReadOnlyList<MetricSnapshot> metrics, string realm = "")
    {
        var sb = new StringBuilder(4096);
        foreach (MetricSnapshot metric in metrics)
        {
            string name = NameOf(metric);
            string type = metric.Kind switch
            {
                MetricKind.Counter => "counter",
                MetricKind.Histogram => "histogram",
                _ => "gauge",
            };
            if (!string.IsNullOrEmpty(metric.Description))
            {
                sb.Append("# HELP ").Append(name).Append(' ').Append(metric.Description.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)).Append('\n');
            }

            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            foreach (SeriesSnapshot series in metric.Series)
            {
                switch (metric.Kind)
                {
                    case MetricKind.Counter:
                        Line(sb, name + "_total", series.Tags, realm, null, series.Value);
                        break;
                    case MetricKind.Histogram:
                        long cumulative = 0;
                        for (int i = 0; i < series.BucketCounts.Length; i++)
                        {
                            cumulative += series.BucketCounts[i];
                            string le = i < metric.Bounds.Count ? Number(metric.Bounds[i]) : "+Inf";
                            Line(sb, name + "_bucket", series.Tags, realm, le, cumulative);
                        }

                        Line(sb, name + "_sum", series.Tags, realm, null, series.Sum);
                        Line(sb, name + "_count", series.Tags, realm, null, series.Count);
                        break;
                    default:
                        Line(sb, name, series.Tags, realm, null, series.Value);
                        break;
                }
            }
        }

        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string name, IReadOnlyList<KeyValuePair<string, string>> tags, string realm, string? le, double value)
    {
        sb.Append(name);
        bool any = false;
        void Label(string key, string text)
        {
            sb.Append(any ? ',' : '{').Append(key).Append("=\"")
                .Append(text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal))
                .Append('"');
            any = true;
        }

        if (realm.Length > 0)
        {
            Label("realm", realm);
        }

        foreach (KeyValuePair<string, string> tag in tags)
        {
            Label(tag.Key, tag.Value);
        }

        if (le is not null)
        {
            Label("le", le);
        }

        if (any)
        {
            sb.Append('}');
        }

        sb.Append(' ').Append(Number(value)).Append('\n');
    }

    private static string Number(double value) => double.IsPositiveInfinity(value) ? "+Inf" : value.ToString("R", CultureInfo.InvariantCulture);
}
