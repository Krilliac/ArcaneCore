using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Validation;

/// <summary>
/// Start-up checks for the <c>Resilience</c> section (docs/ops/resilience.md). Values are parsed from the raw strings
/// like <c>WorldConfigChecks</c>, so every bad key is reported at once with its fix. A value the binder would reject
/// (not a number) is an error; a number outside its range is an error; the defaults are never flagged.
/// </summary>
public sealed class ResilienceConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();

        Bool(issues, configuration, "Resilience:Database:Enabled");
        Range(issues, configuration, "Resilience:Database:QueryTimeoutMs", 0, 600_000, "0 to disable, or 1..600000 milliseconds (default 5000)");

        int? threshold = Range(issues, configuration, "Resilience:Database:Breaker:FailureThreshold", 0, int.MaxValue, "0 to disable, or a count of consecutive failures (default 5)");
        double? rate = Fraction(issues, configuration, "Resilience:Database:Breaker:FailureRateThreshold");
        if (threshold == 0 && rate == 0)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, "Resilience:Database:Breaker:FailureThreshold",
                "FailureThreshold and FailureRateThreshold are both 0, so the breaker could never open.", "enable at least one trip condition"));
        }

        Range(issues, configuration, "Resilience:Database:Breaker:MinimumThroughput", 1, int.MaxValue, "at least 1 call (default 10)");
        Range(issues, configuration, "Resilience:Database:Breaker:SamplingWindowMs", 1, 86_400_000, "1..86400000 milliseconds (default 10000)");
        Range(issues, configuration, "Resilience:Database:Breaker:OpenDurationMs", 1, 86_400_000, "1..86400000 milliseconds (default 10000)");
        Range(issues, configuration, "Resilience:Database:Breaker:HalfOpenMaxProbes", 1, int.MaxValue, "at least 1 probe (default 1)");

        Range(issues, configuration, "Resilience:Database:Bulkhead:MaxConcurrency", 0, int.MaxValue, "0 to disable, or a positive concurrency cap");
        Range(issues, configuration, "Resilience:Database:Bulkhead:MaxQueue", 0, int.MaxValue, "0 or a positive queue length (default 64)");

        Range(issues, configuration, "Resilience:Database:Bootstrap:MaxAttempts", 1, int.MaxValue, "at least 1 attempt (default 5)");
        int? baseDelay = Range(issues, configuration, "Resilience:Database:Bootstrap:BaseDelayMs", 0, 3_600_000, "0..3600000 milliseconds (default 500)");
        int? maxDelay = Range(issues, configuration, "Resilience:Database:Bootstrap:MaxDelayMs", 0, 3_600_000, "0..3600000 milliseconds (default 5000)");
        if (baseDelay is { } b && maxDelay is { } m && m < b)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, "Resilience:Database:Bootstrap:MaxDelayMs",
                $"{m} is below BaseDelayMs ({b}).", "use a cap at least as large as the base delay"));
        }

        Range(issues, configuration, "Resilience:Database:Bootstrap:MaxTotalDurationMs", 0, 86_400_000, "0 for no budget, or up to 86400000 milliseconds");
        return issues;
    }

    private static void Bool(List<ConfigIssue> issues, IConfiguration configuration, string key)
    {
        string? text = configuration[key];
        if (!string.IsNullOrWhiteSpace(text) && !bool.TryParse(text, out _))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a boolean.", "use true or false"));
        }
    }

    /// <summary>The parsed value when the key is set and valid, the default (null) when unset, null after reporting an error.</summary>
    private static int? Range(List<ConfigIssue> issues, IConfiguration configuration, string key, int min, int max, string fix)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a whole number.", fix));
            return null;
        }

        if (value < min || value > max)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"{value} is outside {min}..{max}.", fix));
            return null;
        }

        return value;
    }

    private static double? Fraction(List<ConfigIssue> issues, IConfiguration configuration, string key)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a number.", "use a fraction between 0 (disabled) and 1, e.g. 0.5"));
            return null;
        }

        if (value is < 0 or > 1)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"{value.ToString(CultureInfo.InvariantCulture)} is outside 0..1.", "use a fraction between 0 (disabled) and 1, e.g. 0.5"));
            return null;
        }

        return value;
    }
}
