using System.Globalization;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog.Counters;

/// <summary>
/// Writes every registered counter to the log every <c>Ops:Watchdog:Counters:DumpIntervalSeconds</c>
/// as one Information line (<c>counters: a=1 b=2(+2) ...</c>; the parenthesised number is the
/// change since the previous dump). Runs on the watchdog thread; the line is built there, so
/// the allocation is off every hot path.
/// </summary>
public sealed class CounterDump(WatchdogOptions options, CounterRegistry registry, ILogger<CounterDump> logger) : IWatchdogMonitor
{
    private readonly Dictionary<Counter, long> _previous = new(ReferenceEqualityComparer.Instance);
    private long _nextDumpMicros = long.MinValue;

    public string Name => "counters";

    /// <summary>Dumps written so far.</summary>
    public long Dumps { get; private set; }

    public void Check(long nowMicros)
    {
        long interval = Micros.FromSeconds(options.Counters.DumpIntervalSeconds);
        if (interval <= 0)
        {
            return;
        }

        if (_nextDumpMicros == long.MinValue)
        {
            _nextDumpMicros = nowMicros + interval;
            return;
        }

        if (nowMicros < _nextDumpMicros)
        {
            return;
        }

        _nextDumpMicros = nowMicros + interval;
        string line = Render(registry, options.Counters.ChangedOnly);
        if (line.Length > 0)
        {
            Dumps++;
            logger.LogInformation(WatchdogEvents.CounterDump, "counters: {Counters}", line);
        }
    }

    /// <summary>The dump text (empty when there is nothing to say); updates the per-counter previous values.</summary>
    public string Render(CounterRegistry source, bool changedOnly)
    {
        var text = new StringBuilder();
        foreach (Counter counter in source.Counters)
        {
            long value = counter.Value;
            bool known = _previous.TryGetValue(counter, out long previous);
            _previous[counter] = value;
            long delta = known ? value - previous : value;
            if (changedOnly && known && delta == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(counter.Name).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
            if (known && counter.Kind == CounterKind.Counter)
            {
                text.Append("(+").Append(delta.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
        }

        return text.ToString();
    }
}
