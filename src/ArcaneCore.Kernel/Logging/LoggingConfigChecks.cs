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
        if (!issues.Any(static i => i.Severity == ConfigSeverity.Error && i.Key == Root + ":Format"))
        {
            LogFileSinkPlan plan = LogFileSinkPlan.Resolve(options);
            bool fileInForce = plan.Section == Root + ":File";
            FileSinkOptions unusedFile = options.File;
            JsonSinkOptions unusedJson = options.Json;
            if (fileInForce)
            {
                Tuning(issues, Root + ":Json", unusedJson.RollSizeMb, unusedJson.Retain, unusedJson.QueueCapacity, inForce: false);
            }
            else
            {
                Tuning(issues, Root + ":File", unusedFile.RollSizeMb, unusedFile.Retain, unusedFile.QueueCapacity, inForce: false);
            }

            foreach (LogFileSinkWarning w in plan.Warnings)
            {
                issues.Add(ConfigChecks.Warn(w.Key, w.Message, w.Fix));
            }
        }

        return issues;
    }

    /// <summary>The bound-options form of the same rules; throws <see cref="OptionsValidationException"/> listing every failure as <c>key problem</c>.</summary>
    public static void ThrowIfInvalid(ArcaneLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<ConfigIssue>();
        Rules(options, issues);
        if (issues.Any(static i => i.Severity == ConfigSeverity.Error))
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(ArcaneLoggingOptions), issues.Where(static i => i.Severity == ConfigSeverity.Error).Select(static i => i.Key + " " + i.Problem).ToArray());
        }
    }

    /// <summary>
    /// The single rule set over typed values: enum members defined (including <c>Format</c> when set), and for the one log file the
    /// options resolve to (<see cref="LogFileSinkPlan"/>) the keys actually in force: queue capacity within 1..<see cref="MaxQueueCapacity"/>,
    /// roll size and retention not negative, and, when the file is enabled, a usable file path. Only one file exists, so there is no
    /// rule about two sinks sharing a path.
    /// </summary>
    private static void Rules(ArcaneLoggingOptions options, List<ConfigIssue> issues)
    {
        if (!Enum.IsDefined(options.Console.Mode))
        {
            issues.Add(ConfigChecks.Error(Root + ":Console:Mode", $"must be Color, Plain or Off; {(int)options.Console.Mode} is not one of them.", "use Color, Plain or Off"));
        }

        QueueCapacity(issues, Root + ":Console:QueueCapacity", options.Console.QueueCapacity);
        if (!Enum.IsDefined(options.Timestamps))
        {
            issues.Add(ConfigChecks.Error(Root + ":Timestamps", $"must be Utc or Local; {(int)options.Timestamps} is not one of them.", "use Utc or Local"));
        }

        if (options.Format is { } format && !Enum.IsDefined(format))
        {
            issues.Add(ConfigChecks.Error(Root + ":Format", $"must be Json or Text; {(int)format} is not one of them.", "use Json or Text"));
            return;
        }

        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(options);
        if (plan.Enabled && PathProblem(plan.Path) is { } problem)
        {
            issues.Add(ConfigChecks.Error(plan.PathKey, $"'{plan.Path}' {problem}.", "use a file path such as logs/arcanecore.jsonl (relative paths resolve against the working directory)"));
        }

        Tuning(issues, plan.Section, plan.RollSizeMb, plan.Retain, plan.QueueCapacity, inForce: true);
    }

    /// <summary>Roll size, retention and queue capacity of one file section; an error for the section in force, a warning for the one that is not.</summary>
    private static void Tuning(List<ConfigIssue> issues, string section, int rollSizeMb, int retain, int queueCapacity, bool inForce)
    {
        const string NotInForce = " This section is not in force, so the value is not used.";
        if (rollSizeMb < 0)
        {
            string problem = $"must be 0 (never roll by size) or a positive number of MiB; {rollSizeMb} is out of range.";
            AddIssue(issues, inForce, section + ":RollSizeMb", problem, NotInForce, "use 0 or a positive number of MiB");
        }

        if (retain < 0)
        {
            string problem = $"must be 0 (keep all) or the number of rolled segments to keep; {retain} is out of range.";
            AddIssue(issues, inForce, section + ":Retain", problem, NotInForce, "use 0 or the number of segments to keep");
        }

        if (queueCapacity is < 1 or > MaxQueueCapacity)
        {
            string problem = $"must be {QueueRange}; {queueCapacity} is out of range.";
            AddIssue(issues, inForce, section + ":QueueCapacity", problem, NotInForce, "use " + QueueRange);
        }
    }

    private static void AddIssue(List<ConfigIssue> issues, bool inForce, string key, string problem, string notInForce, string fix)
        => issues.Add(inForce ? ConfigChecks.Error(key, problem, fix) : ConfigChecks.Warn(key, problem + notInForce, fix));

    private static void QueueCapacity(List<ConfigIssue> issues, string key, int value)
    {
        if (value is < 1 or > MaxQueueCapacity)
        {
            issues.Add(ConfigChecks.Error(key, $"must be {QueueRange}; {value} is out of range.", "use " + QueueRange));
        }
    }

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

    /// <summary>
    /// The raw section as typed values: a key that is absent keeps the option's default, one whose text is not a value of its type
    /// is reported here and keeps the default too (so the rules do not report it a second time). The options binder is not used
    /// because it stops at the first bad value, and <c>check-config</c> lists them all.
    /// </summary>
    internal static ArcaneLoggingOptions Bind(IConfiguration configuration) => Parse(configuration, []);

    private static ArcaneLoggingOptions Parse(IConfiguration configuration, List<ConfigIssue> issues)
    {
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = EnumValue(issues, configuration, Root + ":Console:Mode", "Color, Plain or Off", options.Console.Mode);
        options.Console.QueueCapacity = Int(issues, configuration, Root + ":Console:QueueCapacity", QueueRange, options.Console.QueueCapacity);
        options.Timestamps = EnumValue(issues, configuration, Root + ":Timestamps", "Utc or Local", options.Timestamps);
        options.IncludeScopes = Bool(issues, configuration, Root + ":IncludeScopes", options.IncludeScopes);
        options.Format = configuration[Root + ":Format"] is null ? null : EnumValue(issues, configuration, Root + ":Format", "Json or Text", LogFileFormat.Json);

        string file = Root + ":File";
        options.File.Enabled = Bool(issues, configuration, file + ":Enabled", options.File.Enabled);
        options.File.Path = configuration[file + ":Path"];
        options.File.RollSizeMb = Int(issues, configuration, file + ":RollSizeMb", "0 (never roll by size) or a positive number of MiB", options.File.RollSizeMb);
        options.File.RollDaily = Bool(issues, configuration, file + ":RollDaily", options.File.RollDaily);
        options.File.Retain = Int(issues, configuration, file + ":Retain", "0 (keep all) or the number of rolled segments to keep", options.File.Retain);
        options.File.QueueCapacity = Int(issues, configuration, file + ":QueueCapacity", QueueRange, options.File.QueueCapacity);

        string json = Root + ":Json";
        options.Json.Enabled = Bool(issues, configuration, json + ":Enabled", options.Json.Enabled);
        options.Json.Path = configuration[json + ":Path"];
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

        issues.Add(ConfigChecks.Error(key, $"'{raw}' is not one of {allowed}.", "use " + allowed));
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

        issues.Add(ConfigChecks.Error(key, $"'{raw}' is not true or false.", "use true or false"));
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

        issues.Add(ConfigChecks.Error(key, $"'{raw}' is not a whole number.", "use " + allowed));
        return fallback;
    }
}
