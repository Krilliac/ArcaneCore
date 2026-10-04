using System.Globalization;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Startup checks for <c>Logging:ArcaneCore</c>, in the style of the world daemon's <c>check-config</c>: every bad value is
/// reported with key, reason and fix. One rule set, <see cref="Rules"/>, serves both entry points: <see cref="Check"/> parses the
/// raw strings into typed values first (so one unparsable key does not hide the next, and is itself reported) and then runs the
/// rules on the result, defaults filled in; <see cref="ThrowIfInvalid"/> runs the same rules on the bound options when the provider
/// is built or reloaded, so a daemon without the <c>check-config</c> verb (the realm) still fails closed instead of starting with a
/// sink it cannot honour, and a configuration that passes <c>check-config</c> can never fail at host build. Messages never contain
/// anything but the key and its value (log paths are not secrets).
/// </summary>
public sealed class LoggingConfigChecks : IConfigCheck
{
    private const string Root = ArcaneLoggingOptions.SectionName;

    /// <summary>Largest queue accepted; a line is a pooled array, so this bounds the memory a wedged destination can pin.</summary>
    public const int MaxQueueCapacity = 1_000_000;

    private const string QueueRange = "1 to 1000000 lines";

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        ArcaneLoggingOptions options = Parse(configuration, issues);
        Rules(options, issues);
        return issues;
    }

    /// <summary>The bound-options form of the same rules; throws <see cref="OptionsValidationException"/> listing every failure as <c>key problem</c>.</summary>
    public static void ThrowIfInvalid(ArcaneLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<ConfigIssue>();
        Rules(options, issues);
        if (issues.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(ArcaneLoggingOptions), issues.Select(static i => i.Key + " " + i.Problem).ToArray());
        }
    }

    /// <summary>
    /// The single rule set over typed values: enum members defined, queue capacities within 1..<see cref="MaxQueueCapacity"/>,
    /// roll size and retention not negative, an enabled sink's path a usable file path, and the text and JSON sinks on different
    /// files when both are enabled (both paths taken from the configuration or the sink's default).
    /// </summary>
    private static void Rules(ArcaneLoggingOptions options, List<ConfigIssue> issues)
    {
        if (!Enum.IsDefined(options.Console.Mode))
        {
            issues.Add(Error(Root + ":Console:Mode", $"must be Color, Plain or Off; {(int)options.Console.Mode} is not one of them.", "use Color, Plain or Off"));
        }

        QueueCapacity(issues, Root + ":Console:QueueCapacity", options.Console.QueueCapacity);
        if (!Enum.IsDefined(options.Timestamps))
        {
            issues.Add(Error(Root + ":Timestamps", $"must be Utc or Local; {(int)options.Timestamps} is not one of them.", "use Utc or Local"));
        }

        FileSink(issues, Root + ":File", options.File.Enabled, options.File.Path, options.File.RollSizeMb, options.File.Retain, options.File.QueueCapacity);
        FileSink(issues, Root + ":Json", options.Json.Enabled, options.Json.Path, options.Json.RollSizeMb, options.Json.Retain, options.Json.QueueCapacity);
        if (options.File.Enabled && options.Json.Enabled && SamePath(options.File.Path, options.Json.Path))
        {
            issues.Add(Error(Root + ":Json:Path", $"must differ from {Root}:File:Path when both sinks are enabled; both are '{options.Json.Path}'.", "give the JSON-lines sink its own file, such as logs/arcanecore.jsonl"));
        }
    }

    private static void FileSink(List<ConfigIssue> issues, string section, bool enabled, string path, int rollSizeMb, int retain, int queueCapacity)
    {
        if (enabled && PathProblem(path) is { } problem)
        {
            issues.Add(Error(section + ":Path", $"'{path}' {problem}.", "use a file path such as logs/arcanecore.log (relative paths resolve against the working directory)"));
        }

        if (rollSizeMb < 0)
        {
            issues.Add(Error(section + ":RollSizeMb", $"must be 0 (never roll by size) or a positive number of MiB; {rollSizeMb} is out of range.", "use 0 or a positive number of MiB"));
        }

        if (retain < 0)
        {
            issues.Add(Error(section + ":Retain", $"must be 0 (keep all) or the number of rolled segments to keep; {retain} is out of range.", "use 0 or the number of segments to keep"));
        }

        QueueCapacity(issues, section + ":QueueCapacity", queueCapacity);
    }

    private static void QueueCapacity(List<ConfigIssue> issues, string key, int value)
    {
        if (value is < 1 or > MaxQueueCapacity)
        {
            issues.Add(Error(key, $"must be {QueueRange}; {value} is out of range.", "use " + QueueRange));
        }
    }

    private static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);

    private static string? PathProblem(string? path)
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

    /// <summary>
    /// The raw section as typed values: a key that is absent keeps the option's default, one whose text is not a value of its type
    /// is reported here and keeps the default too (so the rules do not report it a second time). The options binder is not used
    /// because it stops at the first bad value, and <c>check-config</c> lists them all.
    /// </summary>
    private static ArcaneLoggingOptions Parse(IConfiguration configuration, List<ConfigIssue> issues)
    {
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = EnumValue(issues, configuration, Root + ":Console:Mode", "Color, Plain or Off", options.Console.Mode);
        options.Console.QueueCapacity = Int(issues, configuration, Root + ":Console:QueueCapacity", QueueRange, options.Console.QueueCapacity);
        options.Timestamps = EnumValue(issues, configuration, Root + ":Timestamps", "Utc or Local", options.Timestamps);
        options.IncludeScopes = Bool(issues, configuration, Root + ":IncludeScopes", options.IncludeScopes);

        string file = Root + ":File";
        options.File.Enabled = Bool(issues, configuration, file + ":Enabled", options.File.Enabled);
        options.File.Path = configuration[file + ":Path"] ?? options.File.Path;
        options.File.RollSizeMb = Int(issues, configuration, file + ":RollSizeMb", "0 (never roll by size) or a positive number of MiB", options.File.RollSizeMb);
        options.File.RollDaily = Bool(issues, configuration, file + ":RollDaily", options.File.RollDaily);
        options.File.Retain = Int(issues, configuration, file + ":Retain", "0 (keep all) or the number of rolled segments to keep", options.File.Retain);
        options.File.QueueCapacity = Int(issues, configuration, file + ":QueueCapacity", QueueRange, options.File.QueueCapacity);

        string json = Root + ":Json";
        options.Json.Enabled = Bool(issues, configuration, json + ":Enabled", options.Json.Enabled);
        options.Json.Path = configuration[json + ":Path"] ?? options.Json.Path;
        options.Json.RollSizeMb = Int(issues, configuration, json + ":RollSizeMb", "0 (never roll by size) or a positive number of MiB", options.Json.RollSizeMb);
        options.Json.RollDaily = Bool(issues, configuration, json + ":RollDaily", options.Json.RollDaily);
        options.Json.Retain = Int(issues, configuration, json + ":Retain", "0 (keep all) or the number of rolled segments to keep", options.Json.Retain);
        options.Json.QueueCapacity = Int(issues, configuration, json + ":QueueCapacity", QueueRange, options.Json.QueueCapacity);
        return options;
    }

    private static TEnum EnumValue<TEnum>(List<ConfigIssue> issues, IConfiguration configuration, string key, string allowed, TEnum fallback)
        where TEnum : struct, Enum
    {
        string? raw = configuration[key];
        if (raw is null)
        {
            return fallback;
        }

        if (System.Enum.TryParse(raw, ignoreCase: true, out TEnum value) && System.Enum.IsDefined(value) && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        issues.Add(Error(key, $"'{raw}' is not one of {allowed}.", "use " + allowed));
        return fallback;
    }

    private static bool Bool(List<ConfigIssue> issues, IConfiguration configuration, string key, bool fallback)
    {
        string? raw = configuration[key];
        if (raw is null)
        {
            return fallback;
        }

        if (bool.TryParse(raw, out bool value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{raw}' is not true or false.", "use true or false"));
        return fallback;
    }

    private static int Int(List<ConfigIssue> issues, IConfiguration configuration, string key, string allowed, int fallback)
    {
        string? raw = configuration[key];
        if (raw is null)
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{raw}' is not a whole number.", "use " + allowed));
        return fallback;
    }
}
