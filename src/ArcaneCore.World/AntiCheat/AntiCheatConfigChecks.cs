using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.AntiCheat;

/// <summary>
/// Startup check of the <c>AntiCheat</c> section (docs/areas/anticheat.md): a value that does not bind (a word where a number
/// belongs, an unknown action) or that <see cref="AntiCheatOptions.Validate"/> refuses is an error (exit 78), so the daemon
/// never starts with checks that silently fell back to something else. A ceiling of Kick with the autoban off is a warning.
/// </summary>
public sealed class AntiCheatConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var options = new AntiCheatOptions();
        try
        {
            configuration.GetSection(AntiCheatOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, AntiCheatOptions.SectionName, $"a value does not fit its key ({ex.Message}).",
                "use true/false, whole numbers and the action names None, Log, GmAlert, Rubberband or Kick")];
        }

        var issues = new List<ConfigIssue>();
        foreach (string problem in options.Validate())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, AntiCheatOptions.SectionName, problem, "see docs/areas/anticheat.md for the ranges"));
        }

        if (options.Enabled && options.Action == AntiCheatAction.Kick && !options.Autoban.Enabled)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{AntiCheatOptions.SectionName}:Autoban:Enabled",
                "the anticheat kicks but never bans, so a kicked cheater can reconnect at once.", "set AntiCheat:Autoban:Enabled true, or accept reconnects"));
        }

        return issues;
    }
}
