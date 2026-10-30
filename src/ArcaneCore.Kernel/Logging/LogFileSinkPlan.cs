namespace ArcaneCore.Kernel.Logging;

/// <summary>The content format of the single log file (<c>Logging:ArcaneCore:Format</c>).</summary>
public enum LogFileFormat
{
    /// <summary>The plain text line format (same as the plain console), file extension chosen by the path.</summary>
    Text,

    /// <summary>One JSON object per log event (JSON lines), structured state properties kept.</summary>
    Json,
}

/// <summary>A startup warning about a deprecated or ambiguous log file configuration.</summary>
/// <param name="Key">The configuration key the warning is about.</param>
/// <param name="Message">What is wrong and what the daemon does instead.</param>
/// <param name="Fix">What to change.</param>
public sealed record LogFileSinkWarning(string Key, string Message, string Fix);

/// <summary>
/// The one log file a daemon writes, resolved from <see cref="ArcaneLoggingOptions"/>: whether it is on, its format, path and
/// tuning, plus the startup warnings the resolution produced. Pure and immutable; the provider, the config checks and every
/// consumer that needs the log path (crash dialog, dump directory) share this one resolution so they cannot disagree.
/// <para>
/// With <c>Format</c> set, the file is configured under <c>Logging:ArcaneCore:File:*</c>. The default format when nothing at all
/// is configured is <see cref="LogFileFormat.Json"/>: JSON keeps the structured state properties for ingestion, the
/// console already gives people a text view, and a Text file stays one setting away. With Format set the
/// path is <c>File:Path</c>, else the legacy <c>Json:Path</c>, else the format default.
/// With <c>Format</c> unset the legacy keys decide: <c>File</c> alone writes text, <c>Json</c> alone writes JSON with its own path
/// and tuning, and both enabled writes JSON only and warns (a daemon never writes two log files).
/// </para>
/// <para>
/// Warnings (<see cref="LogFileSinkWarning"/>, each with the config key, a message and a fix) never change the resolution and never
/// fail sink construction; <see cref="LoggingConfigChecks.Check"/> reports them as warning issues (so, like every config-check
/// warning, they fail startup only under <c>Startup:Strict=true</c>) and the provider logs them at startup.
/// Key <c>Logging:ArcaneCore:Format</c>: Format unset with File and Json both enabled. Key <c>Logging:ArcaneCore:Json:Enabled</c>:
/// Format unset with Json enabled alone (deprecated alias), or Format set with <c>Json:Enabled</c> set. Key
/// <c>Logging:ArcaneCore:Json:Path</c>: Format set with <c>Json:Path</c> set and different from <c>File:Path</c>, with the file sink enabled
/// (with logging off the deprecated path has no effect and warns nothing).
/// </para>
/// </summary>
/// <param name="Enabled">Whether the file sink runs.</param>
/// <param name="Format">The content format.</param>
/// <param name="Path">The active file path as configured, or the per-format default; never null or empty unless the configuration holds only white space (rejected by <see cref="LoggingConfigChecks"/>).</param>
/// <param name="RollSizeMb">Roll size in MiB; 0 never rolls by size.</param>
/// <param name="RollDaily">Roll at the first line of a new day.</param>
/// <param name="Retain">Rolled segments kept; 0 keeps all.</param>
/// <param name="QueueCapacity">Lines the writer thread may hold before new lines are dropped.</param>
/// <param name="Section">The configuration section the tuning keys were read from (<c>Logging:ArcaneCore:File</c> or <c>Logging:ArcaneCore:Json</c>).</param>
/// <param name="PathKey">The configuration key the path came from (or would come from), for error messages.</param>
/// <param name="Warnings">Startup warnings (deprecation, ambiguous legacy configuration); empty when none.</param>
public sealed record LogFileSinkPlan(
    bool Enabled,
    LogFileFormat Format,
    string Path,
    int RollSizeMb,
    bool RollDaily,
    int Retain,
    int QueueCapacity,
    string Section,
    string PathKey,
    IReadOnlyList<LogFileSinkWarning> Warnings)
{
    /// <summary>Path used for a Text file when none is configured.</summary>
    public const string DefaultTextPath = "logs/arcanecore.log";

    /// <summary>Path used for a Json file when none is configured.</summary>
    public const string DefaultJsonPath = "logs/arcanecore.jsonl";

    private const string Root = ArcaneLoggingOptions.SectionName;
    private const string FileSection = Root + ":File";
    private const string JsonSection = Root + ":Json";

    private static bool SamePath(string a, string? b)
    {
        if (b is null)
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    /// <summary>The default path for <paramref name="format"/>.</summary>
    public static string DefaultPath(LogFileFormat format) => format == LogFileFormat.Text ? DefaultTextPath : DefaultJsonPath;

    /// <summary>The plan for options that came from a raw configuration (invalid values fall back to defaults; <see cref="LoggingConfigChecks"/> reports them).</summary>
    public static LogFileSinkPlan Resolve(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Resolve(LoggingConfigChecks.Bind(configuration));
    }

    /// <summary>Applies the resolution rules described on the type.</summary>
    public static LogFileSinkPlan Resolve(ArcaneLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        FileSinkOptions file = options.File;
        JsonSinkOptions json = options.Json;

        if (options.Format is { } set)
        {
            LogFileFormat format = set == LogFileFormat.Text ? LogFileFormat.Text : LogFileFormat.Json;
            string path;
            string pathKey;
            if (!string.IsNullOrEmpty(file.Path))
            {
                path = file.Path;
                pathKey = FileSection + ":Path";
            }
            else if (!string.IsNullOrEmpty(json.Path))
            {
                path = json.Path;
                pathKey = JsonSection + ":Path";
            }
            else
            {
                path = DefaultPath(format);
                pathKey = FileSection + ":Path";
            }

            var warnings = new List<LogFileSinkWarning>(1);
            bool jsonPathSet = !string.IsNullOrEmpty(json.Path);
            bool enabled = file.Enabled || json.Enabled;

            // A deprecated Json:Path alone only matters when a file is written: with logging off it has no effect, so it warns nothing
            // (a warning here would fail startup under Startup:Strict for a configuration that does nothing).
            if (json.Enabled || (enabled && jsonPathSet && !SamePath(json.Path!, file.Path)))
            {
                string keys = json.Enabled && jsonPathSet ? "Json:Enabled and Json:Path" : json.Enabled ? "Json:Enabled" : "Json:Path";
                warnings.Add(new LogFileSinkWarning(
                    json.Enabled ? JsonSection + ":Enabled" : JsonSection + ":Path",
                    $"Logging:ArcaneCore:{keys} {(keys.Contains(" and ", StringComparison.Ordinal) ? "are" : "is")} deprecated: Logging:ArcaneCore:Format is set, so the single log file ({format}) is configured under Logging:ArcaneCore:File. "
                    + "Json:Enabled still switches the file on and Json:Path is used for the file (Text or Json) only when Logging:ArcaneCore:File:Path is not set; move them to Logging:ArcaneCore:File and remove the Json section.",
                    "move Enabled and Path to Logging:ArcaneCore:File and remove the Logging:ArcaneCore:Json section"));
            }

            return new LogFileSinkPlan(enabled, format, path, file.RollSizeMb, file.RollDaily, file.Retain, file.QueueCapacity, FileSection, pathKey, warnings);
        }

        string textPath = string.IsNullOrEmpty(file.Path) ? DefaultTextPath : file.Path;
        string jsonPath = string.IsNullOrEmpty(json.Path) ? DefaultJsonPath : json.Path;
        if (json.Enabled)
        {
            var warnings = new List<LogFileSinkWarning>(1);
            if (file.Enabled)
            {
                warnings.Add(new LogFileSinkWarning(
                    Root + ":Format",
                    $"Logging:ArcaneCore:Format is not set and both Logging:ArcaneCore:File ('{textPath}') and Logging:ArcaneCore:Json ('{jsonPath}') are enabled. "
                    + $"The daemon writes one log file, as JSON lines at '{jsonPath}'; the text sink at '{textPath}' is NOT being written.",
                    "set Logging:ArcaneCore:Format to Json or Text and configure the file under Logging:ArcaneCore:File"));
            }
            else
            {
                warnings.Add(new LogFileSinkWarning(
                    JsonSection + ":Enabled",
                    $"Logging:ArcaneCore:Json is a deprecated alias: Logging:ArcaneCore:Format is not set, so the single log file is written as JSON lines at '{jsonPath}' because Json:Enabled is true.",
                    "set Logging:ArcaneCore:Format=Json and move Enabled, Path and the tuning keys to Logging:ArcaneCore:File"));
            }

            return new LogFileSinkPlan(true, LogFileFormat.Json, jsonPath, json.RollSizeMb, json.RollDaily, json.Retain, json.QueueCapacity, JsonSection, JsonSection + ":Path", warnings);
        }

        if (file.Enabled)
        {
            return new LogFileSinkPlan(true, LogFileFormat.Text, textPath, file.RollSizeMb, file.RollDaily, file.Retain, file.QueueCapacity, FileSection, FileSection + ":Path", []);
        }

        string idlePath = string.IsNullOrEmpty(file.Path) ? DefaultJsonPath : file.Path;
        return new LogFileSinkPlan(false, LogFileFormat.Json, idlePath, file.RollSizeMb, file.RollDaily, file.Retain, file.QueueCapacity, FileSection, FileSection + ":Path", []);
    }
}
