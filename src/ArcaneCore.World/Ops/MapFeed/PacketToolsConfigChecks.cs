using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Startup checks for the <c>World:Control:PacketTools</c> section (docs/areas/map-feed.md, "Packet tools"). A value that is not true or
/// false is an error (the options binder would throw on it). Tools switched on while the map feed they ride on is off is a warning: they
/// stay unreachable. AllowOutsideDevelopment is a warning, and so is Enabled on the live server (the launcher's ARCANECORE_LIVE_SERVER marker, which reaches the
/// configuration through the environment variables; the tools stay off there). Whether the environment admits them is decided at start (the check does not know the host environment).
/// </summary>
public sealed class PacketToolsConfigChecks : IConfigCheck
{
    private const string Root = WorldPacketToolsOptions.SectionName;

    /// <inheritdoc />
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        bool enabled = Flag(issues, configuration, $"{Root}:Enabled");
        bool allowOutside = Flag(issues, configuration, $"{Root}:AllowOutsideDevelopment");
        if (allowOutside)
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Warning,
                $"{Root}:AllowOutsideDevelopment",
                "the packet tools may then run outside the Development environment, where a local tool can act as any online character.",
                "turn it off unless this is a throwaway test server; never set it on a server players use"));
        }

        if (enabled && configuration[WorldPacketToolsOptions.LiveServerVariable] == "1")
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Warning,
                $"{Root}:Enabled",
                $"this is the live server ({WorldPacketToolsOptions.LiveServerVariable}=1), which never runs the packet tools; they stay off whatever the configuration says.",
                "turn the tools off in the live profile"));
        }

        string? feedText = configuration[$"{WorldMapFeedOptions.SectionName}:Enabled"];
        bool feed = bool.TryParse(feedText, out bool feedOn) && feedOn;
        if (enabled && !feed)
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Warning,
                $"{Root}:Enabled",
                "the packet tools ride on the map feed, which is off, so no tool can reach them.",
                $"set {WorldMapFeedOptions.SectionName}:Enabled=true as well, or turn the tools off"));
        }

        return issues;
    }

    private static bool Flag(List<ConfigIssue> issues, IConfiguration configuration, string key)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (bool.TryParse(text, out bool value))
        {
            return value;
        }

        issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not true or false.", "use true or false (default false)"));
        return false;
    }
}
