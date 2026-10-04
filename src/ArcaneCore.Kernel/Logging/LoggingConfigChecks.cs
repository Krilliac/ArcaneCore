using System.Globalization;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Startup checks for <c>Logging:ArcaneCore</c>, in the style of the world daemon's <c>check-config</c>: every bad value is
/// reported with key, reason and fix (parsed from the raw strings, so one bad key does not hide the next). The same rules run as
/// <see cref="ThrowIfInvalid"/> on the bound options when the provider is built or reloaded, so a daemon without the
/// <c>check-config</c> verb (the realm) still fails closed instead of starting with a sink it cannot honour. Messages never
/// contain anything but the key and its value (log paths are not secrets).
/// </summary>
public sealed class LoggingConfigChecks : IConfigCheck
{
    private const string Root = ArcaneLoggingOptions.SectionName;

    /// <summary>Largest queue accepted; a line is a pooled array, so this bounds the memory a wedged destination can pin.</summary>
    public const int MaxQueueCapacity = 1_000_000;

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        EnumValue<ConsoleMode>(issues, configuration, Root + ":Console:Mode", "Color, Plain or Off");
        Range(issues, configuration, Root + ":Console:QueueCapacity", 1, MaxQueueCapacity, "1 to 1000000 lines");
        EnumValue<TimestampKind>(issues, configuration, Root + ":Timestamps", "Utc or Local");
        Bool(issues, configuration, Root + ":IncludeScopes");
        FileSink(issues, configuration, Root + ":File");
        FileSink(issues, configuration, Root + ":Json");
        return issues;
    }

    /// <summary>The bound-options form of the same rules; throws <see cref="OptionsValidationException"/> listing every failure.</summary>
    public static void ThrowIfInvalid(ArcaneLoggingOptions options)
    {
        var failures = new List<string>();
        if (options.Console.QueueCapacity is < 1 or > MaxQueueCapacity)
        {
            failures.Add($"{Root}:Console:QueueCapacity must be 1 to {MaxQueueCapacity}.");
        }

        if (!Enum.IsDefined(options.Console.Mode))
        {
            failures.Add($"{Root}:Console:Mode must be Color, Plain or Off.");
        }

        if (!Enum.IsDefined(options.Timestamps))
        {
            failures.Add($"{Root}:Timestamps must be Utc or Local.");
        }

        FileSink(failures, Root + ":File", options.File.Enabled, options.File.Path, options.File.RollSizeMb, options.File.Retain, options.File.QueueCapacity);
        FileSink(failures, Root + ":Json", options.Json.Enabled, options.Json.Path, options.Json.RollSizeMb, options.Json.Retain, options.Json.QueueCapacity);
        if (options.File.Enabled && options.Json.Enabled && SamePath(options.File.Path, options.Json.Path))
        {
            failures.Add($"{Root}:File:Path and {Root}:Json:Path must differ when both sinks are enabled.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(ArcaneLoggingOptions), failures);
        }
    }

    private static void FileSink(List<string> failures, string section, bool enabled, string path, int rollSizeMb, int retain, int queueCapacity)
    {
        if (enabled && PathProblem(path) is { } problem)
        {
            failures.Add($"{section}:Path {problem}.");
        }

        if (rollSizeMb < 0)
        {
            failures.Add($"{section}:RollSizeMb must be 0 (never roll by size) or a positive number of MiB.");
        }

        if (retain < 0)
        {
            failures.Add($"{section}:Retain must be 0 (keep all) or the number of rolled segments to keep.");
        }

        if (queueCapacity is < 1 or > MaxQueueCapacity)
        {
            failures.Add($"{section}:QueueCapacity must be 1 to {MaxQueueCapacity}.");
        }
    }

    private static void FileSink(List<ConfigIssue> issues, IConfiguration configuration, string section)
    {
        bool? enabled = Bool(issues, configuration, section + ":Enabled");
        string? path = configuration[section + ":Path"];
        if (enabled == true && path is not null && PathProblem(path) is { } problem)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, section + ":Path", $"'{path}' {problem}.", "use a file path such as logs/arcanecore.log (relative paths resolve against the working directory)"));
        }

        Range(issues, configuration, section + ":RollSizeMb", 0, int.MaxValue, "0 (never roll by size) or a positive number of MiB");
        Bool(issues, configuration, section + ":RollDaily");
        Range(issues, configuration, section + ":Retain", 0, int.MaxValue, "0 (keep all) or the number of rolled segments to keep");
        Range(issues, configuration, section + ":QueueCapacity", 1, MaxQueueCapacity, "1 to 1000000 lines");
    }

    private static string? PathProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "is empty";
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return "contains characters that are not allowed in a path";
        }

        if (string.IsNullOrEmpty(Path.GetFileName(path)))
        {
            return "names a directory, not a file";
        }

        return null;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    private static void EnumValue<TEnum>(List<ConfigIssue> issues, IConfiguration configuration, string key, string allowed)
        where TEnum : struct, Enum
    {
        string? raw = configuration[key];
        if (raw is not null && (!System.Enum.TryParse(raw, ignoreCase: true, out TEnum value) || !System.Enum.IsDefined(value) || int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{raw}' is not one of {allowed}.", $"use {allowed}"));
        }
    }

    private static bool? Bool(List<ConfigIssue> issues, IConfiguration configuration, string key)
    {
        string? raw = configuration[key];
        if (raw is null)
        {
            return null;
        }

        if (bool.TryParse(raw, out bool value))
        {
            return value;
        }

        issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{raw}' is not true or false.", "use true or false"));
        return null;
    }

    private static void Range(List<ConfigIssue> issues, IConfiguration configuration, string key, int min, int max, string allowed)
    {
        string? raw = configuration[key];
        if (raw is null)
        {
            return;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{raw}' is not a whole number.", $"use {allowed}"));
        }
        else if (value < min || value > max)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"{value} is out of range.", $"use {allowed}"));
        }
    }
}
