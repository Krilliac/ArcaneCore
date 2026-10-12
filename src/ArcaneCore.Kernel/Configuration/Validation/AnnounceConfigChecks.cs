using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Validation;

/// <summary>
/// Start-up checks for the <c>World:Announce</c> section (docs/areas/hot-swap-announcements.md). Raw strings are parsed so every
/// bad key is reported at once with its fix; defaults are never flagged.
/// </summary>
public sealed class AnnounceConfigChecks : IConfigCheck
{
    /// <summary>The category sections under <c>World:Announce</c>. A new category adds its name here.</summary>
    public static readonly string[] Categories = ["HotSwap"];

    /// <summary>The lane names <c>World:Announce:HotSwap:Lanes</c> accepts.</summary>
    public static readonly string[] HotSwapLanes = ["Data", "Module", "Code"];

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();
        Bool(issues, configuration, AnnounceOptions.SectionName + ":Enabled");
        foreach (string category in Categories)
        {
            string prefix = $"{AnnounceOptions.SectionName}:{category}:";
            Bool(issues, configuration, prefix + "GmNotify");
            Bool(issues, configuration, prefix + "WorldMessage");
            Bool(issues, configuration, prefix + "IncludeDetails");
            Bool(issues, configuration, prefix + "AllowNamesInWorldMessage");
            Range(issues, configuration, prefix + "RateBurst", 0, 10_000);
            Range(issues, configuration, prefix + "RatePerMinute", 0, 100_000);
            Range(issues, configuration, prefix + "DedupSeconds", 0, 86_400);

            string? severity = configuration[prefix + "MinimumSeverity"];
            if (!string.IsNullOrWhiteSpace(severity) && !Enum.TryParse(severity, ignoreCase: true, out AnnounceSeverity parsed))
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Error, prefix + "MinimumSeverity", $"'{severity}' is not a severity.", "use Info, Warning, Error or Critical"));
            }
            else if (!string.IsNullOrWhiteSpace(severity) && int.TryParse(severity, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Error, prefix + "MinimumSeverity", $"'{severity}' is a number, not a severity name.", "use Info, Warning, Error or Critical"));
            }
        }

        string hotSwap = $"{AnnounceOptions.SectionName}:HotSwap:";
        Bool(issues, configuration, hotSwap + "WorldMessageOnFailure");
        Bool(issues, configuration, hotSwap + "WorldMessageIncludeReason");
        foreach (IConfigurationSection lane in configuration.GetSection("World:Announce:HotSwap:Lanes").GetChildren())
        {
            if (!HotSwapLanes.Contains(lane.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Error, lane.Path, $"'{lane.Value}' is not a hot swap lane.", $"use {string.Join(", ", HotSwapLanes)}"));
            }
        }

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

    private static void Range(List<ConfigIssue> issues, IConfiguration configuration, string key, int min, int max)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a whole number.", $"use {min}..{max}"));
        }
        else if (value < min || value > max)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"{value} is outside {min}..{max}.", $"use {min}..{max}"));
        }
    }
}
