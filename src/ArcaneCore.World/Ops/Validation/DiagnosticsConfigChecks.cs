using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.Validation;

/// <summary>
/// The <c>Diagnostics</c> section (docs/ops/invariants.md): every policy must be one of its enum's
/// names and the invariant log limit must not be negative. Checked by <c>check-config</c> and at every
/// start, so an unknown policy name is one listed line and exit 78 instead of a binder exception.
/// </summary>
public sealed class DiagnosticsConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        IConfigurationSection section = configuration.GetSection(DiagnosticsOptions.SectionName);

        Enum<InvariantPolicy>(issues, section, nameof(DiagnosticsOptions.OnInvariant));
        Enum<UnhandledExceptionPolicy>(issues, section, nameof(DiagnosticsOptions.OnUnhandled));
        Enum<UnobservedTaskPolicy>(issues, section, nameof(DiagnosticsOptions.OnUnobservedTask));
        Bool(issues, section, nameof(DiagnosticsOptions.BreakOnInvariant));
        Bool(issues, section, nameof(DiagnosticsOptions.FirstChanceExceptions));

        string key = DiagnosticsOptions.SectionName + ":" + nameof(DiagnosticsOptions.InvariantLogLimit);
        string? limit = section[nameof(DiagnosticsOptions.InvariantLogLimit)];
        if (limit is not null)
        {
            if (!int.TryParse(limit, out int value))
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{limit}' is not a whole number.", "use 0 (count only) or the number of failures per call site to log (default 10)"));
            }
            else if (value < 0)
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"{value} is negative.", "use 0 (count only) or a positive number of failures per call site to log (default 10)"));
            }
        }

        return issues;
    }

    private static void Enum<T>(List<ConfigIssue> issues, IConfigurationSection section, string name)
        where T : struct, Enum
    {
        string? text = section[name];
        if (text is null)
        {
            return;
        }

        // TryParse accepts a number too ("7"); IsDefined keeps the value to the named members.
        if (!System.Enum.TryParse(text, ignoreCase: true, out T parsed) || !System.Enum.IsDefined(parsed))
        {
            issues.Add(new ConfigIssue(
                ConfigSeverity.Error,
                DiagnosticsOptions.SectionName + ":" + name,
                $"'{text}' is not a {typeof(T).Name}.",
                "use one of " + string.Join(", ", System.Enum.GetNames<T>())));
        }
    }

    private static void Bool(List<ConfigIssue> issues, IConfigurationSection section, string name)
    {
        string? text = section[name];
        if (text is not null && !bool.TryParse(text, out _))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, DiagnosticsOptions.SectionName + ":" + name, $"'{text}' is not true or false.", "use true or false"));
        }
    }
}
