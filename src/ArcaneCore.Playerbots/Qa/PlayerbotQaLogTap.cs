using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// An <see cref="ILoggerProvider"/> that listens for the bot and creature-script warnings QA aggregates, as well as <see cref="LogLevel.Error"/>
/// and <see cref="LogLevel.Critical"/> log calls in any category (<c>ArcaneCore.*</c>, <c>Microsoft.*</c>, <c>System.*</c>) and every <see cref="LogLevel.Warning"/> or above
/// that carries an exception, none of which already went through <see cref="FaultLedger"/> or <see cref="AnomalyLedger"/>. A log call parses the line and does one non-blocking queue write; it does no I/O, takes no lock beyond the channel's,
/// never throws and never logs (it cannot recurse). Thread-safe: log calls come from any thread.
/// </summary>
internal sealed class PlayerbotQaLogTap : ILoggerProvider
{
    /// <summary>The category of the managed bot feature (stall, death loop, fault, quarantine lines).</summary>
    internal static readonly string BotCategory = typeof(ManagedPlayerbotFeature).FullName!;

    /// <summary>The category of the creature feature, whose logger the map systems use (relay script lines).</summary>
    internal const string CreatureCategory = "ArcaneCore.World.Creatures.CreatureWorldFeature";

    private const string QaCategoryPrefix = "ArcaneCore.World.Playerbots.Qa";
    private const string DiagnosticsCategoryPrefix = "ArcaneCore.Diagnostics.";

    private readonly PlayerbotQaSignalBus _bus;
    private readonly bool _enabled;
    private readonly bool _logExceptions;
    private readonly Func<string, DateTime, Guid?, (bool Ok, QaSignal? Signal)> _parse;
    private readonly TimeProvider _time;
    private long _failures;
    private long _signals;

    public PlayerbotQaLogTap(PlayerbotQaSignalBus bus, IOptions<PlayerbotQaOptions> options)
        : this(bus, options.Value.Enabled, null, null, options.Value.Faults)
    {
    }

    internal PlayerbotQaLogTap(PlayerbotQaSignalBus bus, bool enabled, Func<string, DateTime, Guid?, (bool, QaSignal?)>? parse, TimeProvider? time,
        PlayerbotQaFaultOptions? faults = null)
    {
        _bus = bus;
        _enabled = enabled;
        _logExceptions = (faults ?? new PlayerbotQaFaultOptions()).LogExceptions;
        _parse = parse ?? ((message, at, key) => (PlayerbotQaSignalParser.TryParse(message, at, key, out QaSignal? signal), signal));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Log calls whose handling threw (swallowed).</summary>
    internal long Failures => Interlocked.Read(ref _failures);

    /// <summary>Signals handed to the bus.</summary>
    internal long Signals => Interlocked.Read(ref _signals);

    public ILogger CreateLogger(string categoryName)
    {
        if (!_enabled || string.IsNullOrEmpty(categoryName))
        {
            return NullLogger.Instance;
        }

        if (string.Equals(categoryName, BotCategory, StringComparison.Ordinal)
            || string.Equals(categoryName, CreatureCategory, StringComparison.Ordinal))
        {
            return new TapLogger(this, categoryName, warningEnabled: true);
        }

        if (IsServerErrorCategory(categoryName))
        {
            return new TapLogger(this, categoryName, warningEnabled: false, exceptionWarnings: _logExceptions);
        }

        // A framework category (Microsoft.EntityFrameworkCore.*, System.*): its Error entries and its Warning-or-above entries with an exception.
        if (_logExceptions && IsFrameworkCategory(categoryName))
        {
            return new TapLogger(this, categoryName, warningEnabled: false, exceptionWarnings: true);
        }

        return NullLogger.Instance;
    }

    /// <summary>
    /// Whether a category's entries can be server faults. Not the QA's own categories (a fault QA logs about itself must not feed itself) and not the diagnostics categories
    /// (the crash handler's reports and the anomaly ledger's lines reach QA through <see cref="FaultObservers"/> and the ledgers, not through their log text).
    /// </summary>
    internal static bool IsServerErrorCategory(string categoryName)
        => categoryName.StartsWith("ArcaneCore.", StringComparison.Ordinal)
            && !categoryName.StartsWith(QaCategoryPrefix, StringComparison.Ordinal)
            && !categoryName.StartsWith(DiagnosticsCategoryPrefix, StringComparison.Ordinal);

    /// <summary>A category of the framework or a library: not ours, and not a QA or diagnostics category either.</summary>
    internal static bool IsFrameworkCategory(string categoryName)
        => !categoryName.StartsWith("ArcaneCore.", StringComparison.Ordinal) && !string.Equals(categoryName, "ArcaneCore", StringComparison.Ordinal);

    public void Dispose()
    {
    }

    private void Handle<TState>(string categoryName, bool warningEnabled, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            Guid? bot = null;
            string? template = null;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i].Key == "BotId" && values[i].Value is Guid id)
                    {
                        bot = id;
                    }
                    else if (values[i].Key == "{OriginalFormat}" && values[i].Value is string fmt)
                    {
                        template = fmt;
                    }
                }
            }

            string message = formatter(state, exception);
            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
            if (warningEnabled)
            {
                (bool ok, QaSignal? signal) = _parse(message, nowUtc, bot);
                if (ok && signal is not null)
                {
                    _bus.TryWrite(signal);
                    Interlocked.Increment(ref _signals);
                    return;
                }
            }

            if ((logLevel >= LogLevel.Error || exception is not null) && TryCreateServerErrorSignal(categoryName, logLevel, eventId, exception, message, template, nowUtc, out QaSignal? faultSignal))
            {
                _bus.TryWrite(faultSignal!);
                Interlocked.Increment(ref _signals);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private static bool TryCreateServerErrorSignal(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        Exception? exception,
        string message,
        string? template,
        DateTime nowUtc,
        out QaSignal? signal)
    {
        signal = null;
        if (eventId.Id == AnomalyLedger.LogEventId.Id
            || message.Contains("[fault=", StringComparison.Ordinal)
            || exception is OperationCanceledException or TaskCanceledException)
        {
            return false;
        }

        if (exception is not null && FaultLedger.WasRecorded(exception))
        {
            return false;
        }

        string shortCategory = ShortCategory(categoryName);
        string site = "log:" + shortCategory;
        if (exception is not null)
        {
            // Type and first ArcaneCore frame only, not the category: the same fault logged by two loggers (or seen first-chance) is one finding.
            string id = PlayerbotQaFaultIds.Describe(exception, out string type, out _, out IReadOnlyList<string> frames);
            string detail = frames.Count > 0 ? string.Join(" > ", frames) : shortCategory;
            string condensed = FaultLedger.FormatCondensedStack(exception);
            signal = new QaSignal
            {
                Kind = QaSignalKind.ServerFault,
                AtUtc = nowUtc,
                FaultId = id,
                Goal = site,
                ExceptionType = type,
                Reason = type,
                Detail = detail,
                Diag = condensed.Length > 0 ? condensed : detail,
                Message = string.Empty,
                Occurrences = 1,
            };
            return true;
        }

        string basis = !string.IsNullOrWhiteSpace(template)
            ? template.Trim()
            : PlayerbotQaFingerprint.Normalise(message);
        basis = PlayerbotQaRedactor.StripSqlValues(basis); // the SQL's shape, never a value
        if (basis.Length == 0)
        {
            return false;
        }

        string kindName = logLevel == LogLevel.Critical ? "LogCritical" : "LogError";
        string logId = FaultLedger.HashSignature($"{kindName}|{categoryName}|{eventId.Id}|{basis}");
        signal = new QaSignal
        {
            Kind = QaSignalKind.ServerFault,
            AtUtc = nowUtc,
            FaultId = logId,
            Goal = site,
            ExceptionType = kindName,
            Reason = kindName,
            Detail = shortCategory,
            Diag = basis,
            Message = string.Empty,
            Occurrences = 1,
        };
        return true;
    }

    private static string ShortCategory(string categoryName)
    {
        int dot = categoryName.LastIndexOf('.');
        return dot >= 0 && dot + 1 < categoryName.Length ? categoryName[(dot + 1)..] : categoryName;
    }

    private sealed class TapLogger(PlayerbotQaLogTap owner, string categoryName, bool warningEnabled, bool exceptionWarnings = false) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // A Warning is enabled when the category carries bot warnings, or when its Warning entries may hold an exception (Log drops the ones that do not).
        public bool IsEnabled(LogLevel logLevel)
            => warningEnabled || exceptionWarnings
                ? logLevel is >= LogLevel.Warning and < LogLevel.None
                : logLevel is >= LogLevel.Error and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (!warningEnabled && logLevel < LogLevel.Error && exception is null) return; // a plain warning of a server category: nothing to look at, nothing allocated
            owner.Handle(categoryName, warningEnabled, logLevel, eventId, state, exception, formatter);
        }
    }
}
