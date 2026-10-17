using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// An <see cref="ILoggerProvider"/> that listens for the bot and creature-script warnings QA aggregates, so the playerbot subsystems need no
/// change: only the categories <see cref="ManagedPlayerbotFeature"/> and <c>CreatureWorldFeature</c> (the logger the map systems use) get a live
/// logger, only Warning and above are enabled, every other category gets a shared null logger. A log call parses the line and does one
/// non-blocking queue write; it does no I/O, takes no lock beyond the channel's, never throws and never logs (it cannot recurse).
/// Thread-safe: log calls come from any thread.
/// </summary>
internal sealed class PlayerbotQaLogTap : ILoggerProvider
{
    /// <summary>The category of the managed bot feature (stall, death loop, fault, quarantine lines).</summary>
    internal static readonly string BotCategory = typeof(ManagedPlayerbotFeature).FullName!;

    /// <summary>The category of the creature feature, whose logger the map systems use (relay script lines).</summary>
    internal const string CreatureCategory = "ArcaneCore.World.Creatures.CreatureWorldFeature";

    private readonly PlayerbotQaSignalBus _bus;
    private readonly bool _enabled;
    private readonly Func<string, DateTime, Guid?, (bool Ok, QaSignal? Signal)> _parse;
    private readonly TimeProvider _time;
    private long _failures;
    private long _signals;

    public PlayerbotQaLogTap(PlayerbotQaSignalBus bus, IOptions<PlayerbotQaOptions> options)
        : this(bus, options.Value.Enabled, null, null)
    {
    }

    internal PlayerbotQaLogTap(PlayerbotQaSignalBus bus, bool enabled, Func<string, DateTime, Guid?, (bool, QaSignal?)>? parse, TimeProvider? time)
    {
        _bus = bus;
        _enabled = enabled;
        _parse = parse ?? ((message, at, key) => (PlayerbotQaSignalParser.TryParse(message, at, key, out QaSignal? signal), signal));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Log calls whose handling threw (swallowed).</summary>
    internal long Failures => Interlocked.Read(ref _failures);

    /// <summary>Signals handed to the bus.</summary>
    internal long Signals => Interlocked.Read(ref _signals);

    public ILogger CreateLogger(string categoryName)
    {
        if (_enabled && (string.Equals(categoryName, BotCategory, StringComparison.Ordinal)
            || string.Equals(categoryName, CreatureCategory, StringComparison.Ordinal)))
        {
            return new TapLogger(this);
        }

        return NullLogger.Instance;
    }

    public void Dispose()
    {
    }

    private void Handle<TState>(LogLevel logLevel, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            Guid? bot = null;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i].Key == "BotId" && values[i].Value is Guid id)
                    {
                        bot = id;
                        break;
                    }
                }
            }

            string message = formatter(state, exception);
            (bool ok, QaSignal? signal) = _parse(message, _time.GetUtcNow().UtcDateTime, bot);
            if (ok && signal is not null)
            {
                _bus.TryWrite(signal);
                Interlocked.Increment(ref _signals);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private sealed class TapLogger(PlayerbotQaLogTap owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Handle(logLevel, state, exception, formatter);
        }
    }
}
