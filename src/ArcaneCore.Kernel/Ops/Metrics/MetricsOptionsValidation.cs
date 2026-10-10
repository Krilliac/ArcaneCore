using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// The rules of <c>Ops:Metrics</c>, as an options validator at host start and as an <see cref="IConfigCheck"/> for
/// <c>check-config</c> (exit 78 with key, problem and fix). The exporter targets are only checked when the section is enabled.
/// </summary>
public sealed class MetricsOptionsValidation : IValidateOptions<MetricsOptions>, IConfigCheck
{
    private const string Root = MetricsOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, MetricsOptions options)
    {
        List<ConfigIssue> issues = Problems(options);
        List<ConfigIssue> errors = [.. issues.Where(i => i.Severity == ConfigSeverity.Error)];
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors.Select(i => i.ToString()));
    }

    /// <summary>The problems of a bound options object.</summary>
    public static List<ConfigIssue> Problems(MetricsOptions o)
    {
        var issues = new List<ConfigIssue>();
        if (!Enum.IsDefined(o.Exporter))
        {
            issues.Add(Error($"{Root}:Exporter", $"'{o.Exporter}' is not an exporter.", "use " + string.Join(", ", Enum.GetNames<MetricsExporter>())));
        }

        Range(issues, $"{Root}:OtlpIntervalSeconds", o.OtlpIntervalSeconds, 1, 3600);
        Range(issues, $"{Root}:MapSampleIntervalSeconds", o.MapSampleIntervalSeconds, 1, 3600);
        if (!o.Enabled)
        {
            return issues;
        }

        if (o.Exporter is MetricsExporter.Prometheus or MetricsExporter.Both
            && (string.IsNullOrWhiteSpace(o.PrometheusPrefix) || !o.PrometheusPrefix.EndsWith('/')
                || !(o.PrometheusPrefix.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || o.PrometheusPrefix.StartsWith("https://", StringComparison.OrdinalIgnoreCase))))
        {
            issues.Add(Error($"{Root}:PrometheusPrefix", $"'{o.PrometheusPrefix}' is not a listener prefix.", "use an http:// prefix ending in /, for example http://+:9464/metrics/"));
        }

        if (o.Exporter is MetricsExporter.Otlp or MetricsExporter.Both
            && (!Uri.TryCreate(o.OtlpEndpoint, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            issues.Add(Error($"{Root}:OtlpEndpoint", $"'{o.OtlpEndpoint}' is not an http(s) URL.", "use the collector's OTLP/HTTP metrics URL, for example http://otel-collector:4318/v1/metrics"));
        }

        return issues;
    }

    /// <summary>The <c>check-config</c> view: each raw value is parsed so every bad one is listed.</summary>
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        IConfigurationSection section = configuration.GetSection(MetricsOptions.SectionName);
        if (!section.Exists())
        {
            return issues;
        }

        var o = new MetricsOptions();
        o.Enabled = Bool(issues, section, "Enabled", o.Enabled);
        o.PerMapMetrics = Bool(issues, section, "PerMapMetrics", o.PerMapMetrics);
        o.OtlpIntervalSeconds = Int(issues, section, "OtlpIntervalSeconds", o.OtlpIntervalSeconds);
        o.MapSampleIntervalSeconds = Int(issues, section, "MapSampleIntervalSeconds", o.MapSampleIntervalSeconds);
        o.PrometheusPrefix = section["PrometheusPrefix"] ?? o.PrometheusPrefix;
        o.OtlpEndpoint = section["OtlpEndpoint"] ?? o.OtlpEndpoint;
        o.Realm = section["Realm"] ?? o.Realm;
        string? exporter = section["Exporter"];
        if (!string.IsNullOrWhiteSpace(exporter))
        {
            if (Enum.TryParse(exporter, ignoreCase: true, out MetricsExporter value) && Enum.IsDefined(value))
            {
                o.Exporter = value;
            }
            else
            {
                issues.Add(Error($"{Root}:Exporter", $"'{exporter}' is not one of the allowed values.", "use " + string.Join(", ", Enum.GetNames<MetricsExporter>())));
            }
        }

        if (issues.Count == 0)
        {
            issues.AddRange(Problems(o));
        }

        return issues;
    }

    private static int Int(List<ConfigIssue> issues, IConfigurationSection section, string key, int fallback)
    {
        string? text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        issues.Add(Error($"{Root}:{key}", $"'{text}' is not a whole number.", "use a whole number"));
        return fallback;
    }

    private static bool Bool(List<ConfigIssue> issues, IConfigurationSection section, string key, bool fallback)
    {
        string? text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (bool.TryParse(text, out bool value))
        {
            return value;
        }

        issues.Add(Error($"{Root}:{key}", $"'{text}' is not true or false.", "use true or false"));
        return fallback;
    }

    private static void Range(List<ConfigIssue> issues, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {min}-{max} seconds"));
        }
    }

    private static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);
}
