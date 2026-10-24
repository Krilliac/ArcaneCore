using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Startup checks for <c>World:Control:MapFeed:Actions</c> and <c>World:Control:MapFeed:Web</c> (docs/areas/atlas-actions.md). A value that
/// cannot be read at all is an error (the options binder would throw on it). A readable value outside its range is an error while the section
/// is enabled and a warning otherwise. Parsed from the raw strings so every problem is listed at once. Two settings are warnings by design:
/// <c>AllowOnLiveServer</c> and a <c>PublicMaxSecurity</c> of Administrator.
/// </summary>
public sealed class MapFeedActionsConfigChecks : IConfigCheck
{
    private const string Actions = MapFeedActionsOptions.SectionName;
    private const string Web = WebFeedPipeOptions.SectionName;

    /// <inheritdoc />
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        var defaults = new MapFeedActionsOptions();

        bool enabled = Bool(issues, configuration, $"{Actions}:Enabled", defaults.Enabled);
        bool allowLive = Bool(issues, configuration, $"{Actions}:AllowOnLiveServer", defaults.AllowOnLiveServer);
        Bool(issues, configuration, $"{Actions}:WebLogin", defaults.WebLogin);
        Bool(issues, configuration, $"{Actions}:AuditLocalDurable", defaults.AuditLocalDurable);
        Bool(issues, configuration, $"{Actions}:PublicAllowModeration", defaults.PublicAllowModeration);

        Level(issues, configuration, $"{Actions}:LocalSecurity", defaults.LocalSecurity);
        AccountSecurity? publicMax = Level(issues, configuration, $"{Actions}:PublicMaxSecurity", defaults.PublicMaxSecurity);
        Level(issues, configuration, $"{Actions}:WebLoginMinSecurity", defaults.WebLoginMinSecurity);

        Range(issues, configuration, $"{Actions}:SettleSeconds", defaults.SettleSeconds, 1, 600, enabled);
        Range(issues, configuration, $"{Actions}:BulkConfirmAbove", defaults.BulkConfirmAbove, 0, MapFeedActionsOptions.MaxTargets, enabled);
        Range(issues, configuration, $"{Actions}:WebAuditDays", defaults.WebAuditDays, 1, 3650, enabled);
        Range(issues, configuration, $"{Actions}:LocalPerSecond", defaults.LocalPerSecond, 1, 1000, enabled);
        Range(issues, configuration, $"{Actions}:LocalBurst", defaults.LocalBurst, 1, 10_000, enabled);
        Range(issues, configuration, $"{Actions}:WebPerSecond", defaults.WebPerSecond, 1, 1000, enabled);
        Range(issues, configuration, $"{Actions}:WebBurst", defaults.WebBurst, 1, 10_000, enabled);

        if (enabled && allowLive)
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Warning, $"{Actions}:AllowOnLiveServer",
                "GM actions from a tool on this machine are allowed on the live server.",
                "leave AllowOnLiveServer off unless you mean to run GM commands from Atlas on the live server"));
        }

        if (publicMax == AccountSecurity.Administrator)
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Warning, $"{Actions}:PublicMaxSecurity",
                "an Administrator account logged in over the Internet would act as an Administrator.",
                "keep PublicMaxSecurity at GameMaster (the default) unless the public listener is trusted as much as the console"));
        }

        bool webEnabled = Bool(issues, configuration, $"{Web}:Enabled", false);
        Range(issues, configuration, $"{Web}:MaxConnections", new WebFeedPipeOptions().MaxConnections, 1, WebFeedPipeOptions.MaxMaxConnections, webEnabled);
        if (webEnabled && string.IsNullOrWhiteSpace(configuration[$"{Web}:ClientAccount"]))
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Error, $"{Web}:ClientAccount",
                "the web pipe is enabled but no client account is named, so it is not created.",
                "set ClientAccount to the SID or MACHINE\\name of the Windows account the public Atlas process runs as"));
        }

        if ((enabled || webEnabled) && !OperatingSystem.IsWindows())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{Actions}:Enabled", "the map feed is a Windows named pipe and is not created on this operating system.", "leave it off here"));
        }

        return issues;
    }

    private static bool Bool(List<ConfigIssue> issues, IConfiguration configuration, string key, bool fallback)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (bool.TryParse(text, out bool parsed))
        {
            return parsed;
        }

        issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not true or false.", $"use true or false (default {fallback.ToString().ToLowerInvariant()})"));
        return fallback;
    }

    private static AccountSecurity? Level(List<ConfigIssue> issues, IConfiguration configuration, string key, AccountSecurity fallback)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (Enum.TryParse(text.Trim(), ignoreCase: true, out AccountSecurity parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        issues.Add(new ConfigIssue(
            ConfigSeverity.Error, key, $"'{text}' is not an account security level.",
            $"use Player, Moderator, GameMaster or Administrator (default {fallback}); there is no Console level"));
        return null;
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
                enabled ? ConfigSeverity.Error : ConfigSeverity.Warning, key, $"{value} is out of range.", $"use {min} to {max} (default {fallback})"));
        }
    }
}
