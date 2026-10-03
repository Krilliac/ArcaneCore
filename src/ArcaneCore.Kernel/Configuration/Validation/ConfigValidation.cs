using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Validation;

public enum ConfigSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One configuration problem: where it is, what is wrong, and how to fix it. Messages must never
/// contain secrets (connection strings, passwords): name the key, not the value of a secret.
/// </summary>
public sealed record ConfigIssue(ConfigSeverity Severity, string Key, string Problem, string Fix)
{
    /// <summary>The environment-variable spelling of <see cref="Key"/> (<c>World:Port</c> becomes <c>World__Port</c>).</summary>
    public string EnvironmentVariable => Key.Replace(":", "__", StringComparison.Ordinal);

    public override string ToString()
        => $"{Severity.ToString().ToUpperInvariant()} {Key}: {Problem} Fix: {Fix} (appsettings key {Key} or environment variable {EnvironmentVariable}).";
}

/// <summary>A pure check of a configuration (no I/O beyond the file system, no secrets in messages). Discovered by reflection per daemon.</summary>
public interface IConfigCheck
{
    IEnumerable<ConfigIssue> Check(IConfiguration configuration);
}

/// <summary>All issues of a run, with the strict-mode promotion of warnings.</summary>
public sealed class ConfigReport(IReadOnlyList<ConfigIssue> issues, bool strict)
{
    public IReadOnlyList<ConfigIssue> Issues { get; } = issues;

    public bool Strict { get; } = strict;

    public int ErrorCount => Issues.Count(i => i.Severity == ConfigSeverity.Error);

    public int WarningCount => Issues.Count(i => i.Severity == ConfigSeverity.Warning);

    /// <summary>Invalid: any error, or any warning when <see cref="Strict"/> ("Startup:Strict").</summary>
    public bool IsInvalid => ErrorCount > 0 || (Strict && WarningCount > 0);

    public static ConfigReport Run(IConfiguration configuration, IEnumerable<IConfigCheck> checks)
    {
        var issues = new List<ConfigIssue>();
        foreach (IConfigCheck check in checks)
        {
            issues.AddRange(check.Check(configuration));
        }

        return new ConfigReport(issues, bool.TryParse(configuration["Startup:Strict"], out bool strict) && strict);
    }

    public void Write(TextWriter output)
    {
        foreach (ConfigIssue issue in Issues.OrderByDescending(i => i.Severity))
        {
            output.WriteLine(issue);
        }

        output.WriteLine($"{ErrorCount} error(s), {WarningCount} warning(s){(Strict && WarningCount > 0 ? " (Startup:Strict makes warnings errors)" : string.Empty)}.");
    }
}
