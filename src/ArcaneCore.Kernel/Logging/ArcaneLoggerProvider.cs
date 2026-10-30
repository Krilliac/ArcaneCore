using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// The ArcaneCore <see cref="ILoggerProvider"/>: a console sink and at most one rolling log file (text or JSON lines, <see cref="ArcaneLoggingOptions.Format"/>),
/// all behind bounded queues (docs/ops/logging.md). Built once from <see cref="ArcaneLoggingOptions"/>; a configuration reload
/// (the host re-reads <c>appsettings.json</c> on change) applies <c>Console:Mode</c>, <c>Timestamps</c> and <c>IncludeScopes</c>
/// live and logs, for every file key that changed, that it keeps its start value (vmangos "option can't be changed at reload",
/// World.cpp:3044-3055). Disposal (host shutdown) drains and flushes every sink.
/// <para>
/// Ownership: the sinks and their writers belong to the provider. State read on the hot path (<see cref="Sinks"/>,
/// <see cref="IncludeScopes"/>, the clock) is immutable or volatile; no lock is taken per event.
/// </para>
/// </summary>
[ProviderAlias("ArcaneCore")]
public sealed class ArcaneLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private static readonly Func<DateTime> UtcClock = static () => DateTime.UtcNow;
    private static readonly Func<DateTime> LocalClock = static () => DateTime.Now;

    private readonly ConcurrentDictionary<string, ArcaneLogger> _loggers = new(StringComparer.Ordinal);
    private readonly TextLogSink _console;
    private readonly IDisposable? _reload;
    private readonly Func<bool> _terminalAllowsColor;
    private bool? _colorAllowed;
    private ArcaneLoggingOptions _current;
    private volatile Func<DateTime> _clock;
    private volatile bool _includeScopes;
    private volatile IExternalScopeProvider? _scopeProvider;
    private int _disposed;

    /// <summary>The host constructor: builds the sinks from the current options and follows reloads.</summary>
    public ArcaneLoggerProvider(IOptionsMonitor<ArcaneLoggingOptions> options)
        : this(options, ConsoleColorSupport.TerminalAllowsColor, BuildSinks(options.CurrentValue))
    {
        EmitStartupWarnings(options.CurrentValue);
    }

    /// <summary>
    /// The host constructor over an explicit terminal probe and sinks (tests). <paramref name="terminalAllowsColor"/> is the
    /// environment's verdict alone (terminal, NO_COLOR, VT), independent of the configured mode; it is asked at most once, the first
    /// time a mode of <see cref="ConsoleMode.Color"/> is applied, so a daemon started Plain or Off renders colour after a reload to
    /// Color when the terminal allows it, and a daemon that never asks for colour never touches the console mode.
    /// </summary>
    public ArcaneLoggerProvider(IOptionsMonitor<ArcaneLoggingOptions> options, Func<bool> terminalAllowsColor, LogSink[] sinks)
        : this(options.CurrentValue, terminalAllowsColor, sinks, options)
    {
    }

    /// <summary>
    /// Builds the provider over explicit sinks. The first sink is the console (<see cref="TextLogSink"/>, mode applied live);
    /// <paramref name="colorAllowed"/> is the environment's verdict (terminal, NO_COLOR, VT); <paramref name="options"/> may be
    /// null when nothing reloads (tests).
    /// </summary>
    public ArcaneLoggerProvider(ArcaneLoggingOptions initial, bool colorAllowed, LogSink[] sinks, IOptionsMonitor<ArcaneLoggingOptions>? options = null)
        : this(initial, () => colorAllowed, sinks, options)
    {
    }

    private ArcaneLoggerProvider(ArcaneLoggingOptions initial, Func<bool> terminalAllowsColor, LogSink[] sinks, IOptionsMonitor<ArcaneLoggingOptions>? options)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(terminalAllowsColor);
        ArgumentNullException.ThrowIfNull(sinks);
        LoggingConfigChecks.ThrowIfInvalid(initial);
        if (sinks.Length == 0 || sinks[0] is not TextLogSink console)
        {
            throw new ArgumentException("the first sink must be the console TextLogSink", nameof(sinks));
        }

        Sinks = sinks;
        _console = console;
        _terminalAllowsColor = terminalAllowsColor;
        _current = Snapshot(initial);
        _clock = initial.Timestamps == TimestampKind.Local ? LocalClock : UtcClock;
        _includeScopes = initial.IncludeScopes;
        ApplyConsoleMode(initial.Console.Mode);
        _reload = options?.OnChange(Reload);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>Every sink, console first. Immutable after construction.</summary>
    public LogSink[] Sinks { get; }

    /// <summary>True when the active console mode renders colour (mode Color, applied at start or by a reload, and the terminal allows it).</summary>
    public bool ConsoleColor => _console.Color && _console.Enabled;

    public bool IncludeScopes => _includeScopes;

    public IExternalScopeProvider? ScopeProvider => _scopeProvider;

    /// <summary>The configured clock (UTC or local).</summary>
    public DateTime Now() => _clock();

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, static (name, provider) => new ArcaneLogger(name, provider), this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    /// <summary>Blocks until every queued line has been written and flushed, or <paramref name="timeout"/> passes; true when everything is out.</summary>
    public bool Flush(TimeSpan timeout)
    {
        bool complete = true;
        foreach (LogSink sink in Sinks)
        {
            if (sink.Writer is QueuedLineWriter queued)
            {
                complete &= queued.Flush(timeout);
            }
            else
            {
                sink.Flush();
            }
        }

        return complete;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _reload?.Dispose();
        foreach (LogSink sink in Sinks)
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// The sinks the host configuration asks for: the console (always, possibly disabled), then at most one file sink as resolved by
    /// <see cref="LogFileSinkPlan"/>, named <c>file</c> for Text and <c>json</c> for Json.
    /// </summary>
    public static LogSink[] BuildSinks(ArcaneLoggingOptions options)
    {
        LoggingConfigChecks.ThrowIfInvalid(options);
        Func<DateTime> clock = options.Timestamps == TimestampKind.Local ? LocalClock : UtcClock;
        var sinks = new List<LogSink>(2)
        {
            Queued("console", new TextWriterLineWriter(Console.Out), options.Console.QueueCapacity, static (name, writer) => new TextLogSink(name, writer, color: false)),
        };
        LogFileSinkPlan plan = LogFileSinkPlan.Resolve(options);
        if (plan.Enabled)
        {
            var policy = RollingFilePolicy.FromOptions(plan.RollSizeMb, plan.RollDaily, plan.Retain);
            var file = new RollingFileWriter(plan.Path, policy, clock);
            sinks.Add(plan.Format == LogFileFormat.Text
                ? Queued("file", file, plan.QueueCapacity, static (name, writer) => new TextLogSink(name, writer, color: false))
                : Queued("json", file, plan.QueueCapacity, static (name, writer) => new JsonLogSink(name, writer)));
        }

        return [.. sinks];
    }

    /// <summary>A sink over a bounded queue whose drop notice is rendered by the sink itself (its own format and colour).</summary>
    public static LogSink Queued(string name, ILineWriter destination, int capacity, Func<string, ILineWriter, LogSink> create)
    {
        LogSink? sink = null;
        var queue = new QueuedLineWriter(destination, capacity, name, dropped => sink!.DropNotice(dropped), priorityDropNotice: lost => sink!.PriorityDropNotice(lost));
        sink = create(name, queue);
        return sink;
    }

    private void Reload(ArcaneLoggingOptions next)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            LoggingConfigChecks.ThrowIfInvalid(next);
        }
        catch (OptionsValidationException ex)
        {
            Self(LogLevel.Error, "Logging configuration reload rejected; the running settings are kept: " + string.Join(" ", ex.Failures));
            return;
        }

        ArcaneLoggingOptions previous;
        lock (_loggers)
        {
            previous = _current;
            _current = Snapshot(next);
        }

        if (previous.Console.Mode != next.Console.Mode)
        {
            ApplyConsoleMode(next.Console.Mode);
            Self(LogLevel.Information, $"Logging:ArcaneCore:Console:Mode changed to {next.Console.Mode}.");
        }

        if (previous.Timestamps != next.Timestamps)
        {
            _clock = next.Timestamps == TimestampKind.Local ? LocalClock : UtcClock;
            Self(LogLevel.Information, $"Logging:ArcaneCore:Timestamps changed to {next.Timestamps}.");
        }

        if (previous.IncludeScopes != next.IncludeScopes)
        {
            _includeScopes = next.IncludeScopes;
            Self(LogLevel.Information, $"Logging:ArcaneCore:IncludeScopes changed to {next.IncludeScopes}.");
        }

        ReportFixed("Console:QueueCapacity", previous.Console.QueueCapacity, next.Console.QueueCapacity);
        ReportFixedFileSink(previous, next);
    }

    /// <summary>
    /// The file sink is restart-only. What the daemon would write is the resolved <see cref="LogFileSinkPlan"/>, not the raw keys, so the
    /// previous and next options are compared as plans: moving a path between <c>Json:Path</c> and <c>File:Path</c>, or changing a key the
    /// resolution ignores, resolves to the same plan and logs nothing. Each difference is reported under the key the plan reads it from.
    /// </summary>
    private void ReportFixedFileSink(ArcaneLoggingOptions previous, ArcaneLoggingOptions next)
    {
        LogFileSinkPlan was = LogFileSinkPlan.Resolve(previous);
        LogFileSinkPlan wanted = LogFileSinkPlan.Resolve(next);
        ReportFixed("File:Enabled", was.Enabled, wanted.Enabled);
        if (!was.Enabled && !wanted.Enabled)
        {
            return;
        }

        if (was.Enabled && wanted.Enabled)
        {
            ReportFixed("Format", was.Format.ToString(), wanted.Format.ToString());
        }

        const string Prefix = ArcaneLoggingOptions.SectionName + ":";
        ReportFixed(wanted.PathKey[Prefix.Length..], PathText(was.Path), PathText(wanted.Path));
        string section = wanted.Section[Prefix.Length..];
        ReportFixed(section + ":RollSizeMb", was.RollSizeMb, wanted.RollSizeMb);
        ReportFixed(section + ":RollDaily", was.RollDaily, wanted.RollDaily);
        ReportFixed(section + ":Retain", was.Retain, wanted.Retain);
        ReportFixed(section + ":QueueCapacity", was.QueueCapacity, wanted.QueueCapacity);
    }

    private static string PathText(string? path) => string.IsNullOrEmpty(path) ? "(unset)" : path;

    private void ReportFixed<T>(string key, T running, T wanted)
        where T : IEquatable<T>
    {
        if (running.Equals(wanted))
        {
            return;
        }

        Self(LogLevel.Warning, $"Logging:ArcaneCore:{key} option can't be changed at reload; still {Convert.ToString(running, CultureInfo.InvariantCulture)}, restart to apply {Convert.ToString(wanted, CultureInfo.InvariantCulture)}.");
    }

    /// <summary>
    /// Writes the plan's warnings (deprecated or ambiguous file configuration) through the sinks, so they land in the log file too and
    /// not only in the startup config report on stderr. <see cref="Self"/> already isolates each sink's failure.
    /// </summary>
    private void EmitStartupWarnings(ArcaneLoggingOptions options)
    {
        foreach (LogFileSinkWarning warning in LogFileSinkPlan.Resolve(options).Warnings)
        {
            Self(LogLevel.Warning, warning.Message + " Fix: " + warning.Fix + ".");
        }
    }

    /// <summary>Combines the mode (per reload) with the terminal's capability (probed once, when colour is first asked for).</summary>
    private void ApplyConsoleMode(ConsoleMode mode)
    {
        _console.Color = mode == ConsoleMode.Color && (_colorAllowed ??= _terminalAllowsColor());
        _console.Enabled = mode != ConsoleMode.Off;
    }

    /// <summary>Writes a line about the logging system itself through every sink (no factory filter applies).</summary>
    private void Self(LogLevel level, string message)
    {
        var evt = new LogEvent(Now(), level, "ArcaneCore.Logging", default, message, null);
        foreach (LogSink sink in Sinks)
        {
            if (sink.Enabled)
            {
                try
                {
                    sink.Emit<object?>(in evt, null, null);
                }
                catch (Exception)
                {
                    sink.CountFault();
                }
            }
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Flush(TimeSpan.FromSeconds(5));

    /// <summary>A copy, so a later in-place rebinding by the options monitor cannot change what was compared against.</summary>
    private static ArcaneLoggingOptions Snapshot(ArcaneLoggingOptions source)
    {
        var copy = new ArcaneLoggingOptions { Timestamps = source.Timestamps, IncludeScopes = source.IncludeScopes };
        copy.Console.Mode = source.Console.Mode;
        copy.Console.QueueCapacity = source.Console.QueueCapacity;
        copy.Format = source.Format;
        copy.File.Enabled = source.File.Enabled;
        copy.File.Path = source.File.Path;
        copy.File.RollSizeMb = source.File.RollSizeMb;
        copy.File.RollDaily = source.File.RollDaily;
        copy.File.Retain = source.File.Retain;
        copy.File.QueueCapacity = source.File.QueueCapacity;
        copy.Json.Enabled = source.Json.Enabled;
        copy.Json.Path = source.Json.Path;
        copy.Json.RollSizeMb = source.Json.RollSizeMb;
        copy.Json.RollDaily = source.Json.RollDaily;
        copy.Json.Retain = source.Json.Retain;
        copy.Json.QueueCapacity = source.Json.QueueCapacity;
        return copy;
    }
}
