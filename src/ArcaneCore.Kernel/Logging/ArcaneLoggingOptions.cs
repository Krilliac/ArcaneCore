namespace ArcaneCore.Kernel.Logging;

/// <summary>How the console sink renders, or whether it runs at all.</summary>
public enum ConsoleMode
{
    /// <summary>ANSI colour per level, dimmed category and scopes. Falls back to <see cref="Plain"/> when stdout is redirected, <c>NO_COLOR</c> is set, or Windows refuses VT processing.</summary>
    Color,

    /// <summary>No escape codes, fixed-width level column; for systemd/journald, CI and files captured from stdout.</summary>
    Plain,

    /// <summary>Nothing is written to the console.</summary>
    Off,
}

/// <summary>The clock a log line's timestamp is taken from.</summary>
public enum TimestampKind
{
    /// <summary>Coordinated universal time (<c>Z</c> suffix in JSON).</summary>
    Utc,

    /// <summary>The machine's local time (offset suffix in JSON).</summary>
    Local,
}

/// <summary>
/// The ArcaneCore logging provider (<c>Logging:ArcaneCore</c>, docs/ops/logging.md): one console sink and at most one rolling
/// log file, text or JSON lines (<see cref="Format"/>). Per-category minimum levels stay in the standard <c>Logging:LogLevel</c> section.
/// <c>Console:Mode</c>, <c>Timestamps</c> and <c>IncludeScopes</c> are applied live when the configuration file changes; every
/// file key is read once at start.
/// </summary>
[ArcaneCore.Kernel.Configuration.ConfigDaemons(ArcaneCore.Kernel.Configuration.ConfigDaemon.Both)]
public sealed class ArcaneLoggingOptions
{
    public const string SectionName = "Logging:ArcaneCore";

    /// <summary>The console sink (stdout).</summary>
    public ConsoleSinkOptions Console { get; } = new();

    /// <summary>
    /// The one rolling log file. Its content is <see cref="Format"/>: the plain console line format (<c>Text</c>) or one JSON object
    /// per event (<c>Json</c>).
    /// </summary>
    public FileSinkOptions File { get; } = new();

    /// <summary>
    /// Deprecated alias of the JSON-lines file, kept so older configurations still bind. Only honoured while <see cref="Format"/>
    /// is unset (see <see cref="LogFileSinkPlan"/>); new configurations use <see cref="File"/> with <c>Format=Json</c>.
    /// </summary>
    public JsonSinkOptions Json { get; } = new();

    /// <summary>
    /// The content format of the one log file: <c>Json</c> (one JSON object per line, structured properties kept) or <c>Text</c>
    /// (the plain console line format). Not set means the legacy <c>File</c>/<c>Json</c> switches decide; with nothing configured the
    /// default is <c>Json</c>: JSON keeps the structured state properties for ingestion, and the console already gives a text view.
    /// The file is configured under <c>File</c>. Restart-only.
    /// </summary>
    public LogFileFormat? Format { get; set; }

    /// <summary>The clock timestamps are taken from: <c>Utc</c> (default) or <c>Local</c>. Live at reload.</summary>
    public TimestampKind Timestamps { get; set; } = TimestampKind.Utc;

    /// <summary><c>true</c> appends the active logger scopes to each line (<c>=&gt; scope</c>). Live at reload.</summary>
    public bool IncludeScopes { get; set; } = true;
}

/// <summary>Console sink settings.</summary>
public sealed class ConsoleSinkOptions
{
    /// <summary><c>Color</c> (default), <c>Plain</c> or <c>Off</c>. Live at reload; colour is still disabled when stdout is not a terminal or <c>NO_COLOR</c> is set.</summary>
    public ConsoleMode Mode { get; set; } = ConsoleMode.Color;

    /// <summary>Lines the console writer thread may hold before new lines are dropped (and the drop counted and reported). Restart-only.</summary>
    public int QueueCapacity { get; set; } = 4096;
}

/// <summary>The log file's settings, applying to whichever <see cref="ArcaneLoggingOptions.Format"/> is chosen. Every key is restart-only.</summary>
public sealed class FileSinkOptions
{
    /// <summary><c>true</c> writes the one log file, in the chosen <c>Format</c>, to <c>Path</c>. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The active log file; relative paths resolve against the working directory. Rolled segments sit next to it as
    /// <c>name-yyyyMMdd-NNN.ext</c>. Not set means <c>logs/arcanecore.jsonl</c> for a Json file and <c>logs/arcanecore.log</c> for a Text file.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Roll when the active file would exceed this many MiB; 0 never rolls by size.</summary>
    public int RollSizeMb { get; set; } = 64;

    /// <summary><c>true</c> rolls at the first line of a new day (in the <c>Timestamps</c> clock).</summary>
    public bool RollDaily { get; set; } = true;

    /// <summary>Rolled segments kept next to the active file; the oldest beyond this count are deleted after each roll. 0 keeps all.</summary>
    public int Retain { get; set; } = 14;

    /// <summary>Lines the file writer thread may hold before new lines are dropped (counted and reported in the file itself). The world thread never waits on the disk.</summary>
    public int QueueCapacity { get; set; } = 8192;
}

/// <summary>Deprecated alias of the JSON-lines file settings (use <see cref="FileSinkOptions"/> with <c>Format=Json</c>). Every key is restart-only.</summary>
public sealed class JsonSinkOptions
{
    /// <summary>Deprecated: use <c>File:Enabled</c> with <c>Format=Json</c>. <c>true</c> writes one JSON object per log event to <c>Path</c>. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Deprecated: use <c>File:Path</c>. The active JSON-lines file; relative paths resolve against the working directory. Not set means <c>logs/arcanecore.jsonl</c>.</summary>
    public string? Path { get; set; }

    /// <summary>Deprecated: use <c>File:RollSizeMb</c>. Roll when the active file would exceed this many MiB; 0 never rolls by size.</summary>
    public int RollSizeMb { get; set; } = 64;

    /// <summary>Deprecated: use <c>File:RollDaily</c>. <c>true</c> rolls at the first line of a new day (in the <c>Timestamps</c> clock).</summary>
    public bool RollDaily { get; set; } = true;

    /// <summary>Deprecated: use <c>File:Retain</c>. Rolled segments kept; the oldest beyond this count are deleted after each roll. 0 keeps all.</summary>
    public int Retain { get; set; } = 14;

    /// <summary>Deprecated: use <c>File:QueueCapacity</c>. Lines the JSON writer thread may hold before new lines are dropped (counted and reported as a JSON line).</summary>
    public int QueueCapacity { get; set; } = 8192;
}
