using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Startup checks for the <c>World:Control:MapFeed</c> section (docs/areas/map-feed.md). A value that cannot be read at all is an error
/// (the options binder would throw on it). A readable value outside its range is an error while the feed is enabled, because the feed then
/// stays off and the operator who asked for it must hear why; while the feed is disabled it is only a warning. Parsed from the raw strings
/// so every problem is listed at once.
/// </summary>
public sealed class MapFeedConfigChecks : IConfigCheck
{
    private const string Root = WorldMapFeedOptions.SectionName;

    /// <inheritdoc />
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        var defaults = new WorldMapFeedOptions();

        bool enabled = defaults.Enabled;
        string? enabledText = configuration[$"{Root}:Enabled"];
        if (!string.IsNullOrWhiteSpace(enabledText))
        {
            if (bool.TryParse(enabledText, out bool parsed))
            {
                enabled = parsed;
            }
            else
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Error, $"{Root}:Enabled", $"'{enabledText}' is not true or false.", "use true or false (default false)"));
            }
        }

        Range(issues, configuration, $"{Root}:MaxHz", defaults.MaxHz, WorldMapFeedOptions.MinMaxHz, WorldMapFeedOptions.MaxMaxHz, enabled);
        Range(issues, configuration, $"{Root}:MaxEntitiesPerFrame", defaults.MaxEntitiesPerFrame, WorldMapFeedOptions.MinEntitiesPerFrame, WorldMapFeedOptions.MaxEntitiesPerFrameLimit, enabled);
        Range(issues, configuration, $"{Root}:MaxConnections", defaults.MaxConnections, 1, WorldMapFeedOptions.MaxMaxConnections, enabled);

        if (enabled && !OperatingSystem.IsWindows())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{Root}:Enabled", "the map feed is a Windows named pipe and is not created on this operating system.", "leave it off here"));
        }

        return issues;
    }

    private static void Range(List<ConfigIssue> issues, IConfiguration configuration, string key, int fallback, int min, int max, bool enabled)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a whole number.", $"use a whole number from {min} to {max} (default {fallback})"));
            return;
        }

        if (value < min || value > max)
        {
            issues.Add(new ConfigIssue(
                enabled ? ConfigSeverity.Error : ConfigSeverity.Warning,
                key,
                $"{value} is out of range.",
                $"use {min} to {max} (default {fallback})"));
        }
    }
}
