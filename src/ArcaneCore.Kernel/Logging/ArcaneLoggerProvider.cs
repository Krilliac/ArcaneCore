using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// The ArcaneCore <see cref="ILoggerProvider"/>: a console sink, an optional rolling text file and an optional JSON-lines file,
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
    private readonly bool _colorAllowed;
    private ArcaneLoggingOptions _current;
    private volatile Func<DateTime> _clock;
    private volatile bool _includeScopes;
    private volatile IExternalScopeProvider? _scopeProvider;
    private int _disposed;

    /// <summary>The host constructor: builds the sinks from the current options and follows reloads.</summary>
    public ArcaneLoggerProvider(IOptionsMonitor<ArcaneLoggingOptions> options)
        : this(options.CurrentValue, ConsoleColorSupport.Resolve(options.CurrentValue.Console.Mode), BuildSinks(options.CurrentValue), options)
    {
    }

    /// <summary>
    /// Builds the provider over explicit sinks. The first sink is the console (<see cref="TextLogSink"/>, mode applied live);
    /// <paramref name="colorAllowed"/> is the environment's verdict (terminal, NO_COLOR, VT); <paramref name="options"/> may be
    /// null when nothing reloads (tests).
    /// </summary>
    public ArcaneLoggerProvider(ArcaneLoggingOptions initial, bool colorAllowed, LogSink[] sinks, IOptionsMonitor<ArcaneLoggingOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(sinks);
        LoggingConfigChecks.ThrowIfInvalid(initial);
        if (sinks.Length == 0 || sinks[0] is not TextLogSink console)
        {
            throw new ArgumentException("the first sink must be the console TextLogSink", nameof(sinks));
        }

        Sinks = sinks;
        _console = console;
        _colorAllowed = colorAllowed;
        _current = Snapshot(initial);
        _clock = initial.Timestamps == TimestampKind.Local ? LocalClock : UtcClock;
        _includeScopes = initial.IncludeScopes;
        ApplyConsoleMode(initial.Console.Mode);
        _reload = options?.OnChange(Reload);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>Every sink, console first. Immutable after construction.</summary>
    public LogSink[] Sinks { get; }

    /// <summary>True when the active console mode renders colour (mode Color and the environment allows it).</summary>
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

    /// <summary>The sinks the host configuration asks for: console (always, possibly disabled), then file and JSON when enabled.</summary>
    public static LogSink[] BuildSinks(ArcaneLoggingOptions options)
    {
        LoggingConfigChecks.ThrowIfInvalid(options);
        Func<DateTime> clock = options.Timestamps == TimestampKind.Local ? LocalClock : UtcClock;
        var sinks = new List<LogSink>(3)
        {
            Queued("console", new TextWriterLineWriter(Console.Out), options.Console.QueueCapacity, static (name, writer) => new TextLogSink(name, writer, color: false)),
        };
        if (options.File.Enabled)
        {
            var policy = RollingFilePolicy.FromOptions(options.File.RollSizeMb, options.File.RollDaily, options.File.Retain);
            sinks.Add(Queued("file", new RollingFileWriter(options.File.Path, policy, clock), options.File.QueueCapacity, static (name, writer) => new TextLogSink(name, writer, color: false)));
        }

        if (options.Json.Enabled)
        {
            var policy = RollingFilePolicy.FromOptions(options.Json.RollSizeMb, options.Json.RollDaily, options.Json.Retain);
            sinks.Add(Queued("json", new RollingFileWriter(options.Json.Path, policy, clock), options.Json.QueueCapacity, static (name, writer) => new JsonLogSink(name, writer)));
        }

        return [.. sinks];
    }

    /// <summary>A sink over a bounded queue whose drop notice is rendered by the sink itself (its own format and colour).</summary>
    public static LogSink Queued(string name, ILineWriter destination, int capacity, Func<string, ILineWriter, LogSink> create)
    {
        LogSink? sink = null;
        var queue = new QueuedLineWriter(destination, capacity, name, dropped => sink!.DropNotice(dropped));
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
        ReportFixed("File:Enabled", previous.File.Enabled, next.File.Enabled);
        ReportFixed("File:Path", previous.File.Path, next.File.Path);
        ReportFixed("File:RollSizeMb", previous.File.RollSizeMb, next.File.RollSizeMb);
        ReportFixed("File:RollDaily", previous.File.RollDaily, next.File.RollDaily);
        ReportFixed("File:Retain", previous.File.Retain, next.File.Retain);
        ReportFixed("File:QueueCapacity", previous.File.QueueCapacity, next.File.QueueCapacity);
        ReportFixed("Json:Enabled", previous.Json.Enabled, next.Json.Enabled);
        ReportFixed("Json:Path", previous.Json.Path, next.Json.Path);
        ReportFixed("Json:RollSizeMb", previous.Json.RollSizeMb, next.Json.RollSizeMb);
        ReportFixed("Json:RollDaily", previous.Json.RollDaily, next.Json.RollDaily);
        ReportFixed("Json:Retain", previous.Json.Retain, next.Json.Retain);
        ReportFixed("Json:QueueCapacity", previous.Json.QueueCapacity, next.Json.QueueCapacity);
    }

    private void ReportFixed<T>(string key, T running, T wanted)
        where T : IEquatable<T>
    {
        if (!running.Equals(wanted))
        {
            Self(LogLevel.Warning, $"Logging:ArcaneCore:{key} option can't be changed at reload; still {Convert.ToString(running, CultureInfo.InvariantCulture)}, restart to apply {Convert.ToString(wanted, CultureInfo.InvariantCulture)}.");
        }
    }

    private void ApplyConsoleMode(ConsoleMode mode)
    {
        _console.Color = mode == ConsoleMode.Color && _colorAllowed;
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
