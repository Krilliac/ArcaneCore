using System.Runtime.InteropServices;

namespace ArcaneCore.Kernel.Ops.Watchdog.Counters;

/// <summary>What a <see cref="Counter"/> measures.</summary>
public enum CounterKind
{
    /// <summary>Monotonic: only <see cref="Counter.Increment"/> / <see cref="Counter.Add"/> with a non-negative amount.</summary>
    Counter = 0,

    /// <summary>A level that goes up and down (<see cref="Counter.Set"/>, <see cref="Counter.Decrement"/>).</summary>
    Gauge = 1,
}

/// <summary>
/// One named 64-bit value. <see cref="Increment"/>, <see cref="Add"/>, <see cref="Decrement"/> and
/// <see cref="Set"/> are a single interlocked instruction, allocate nothing and may be called from
/// any thread; <see cref="Value"/> is a volatile read. The value sits in its own cache line
/// (64 bytes of padding on each side) so two hot counters never false-share. Obtain one from
/// <see cref="CounterRegistry.GetOrAdd"/>, once, at construction time; hold the reference, not the name.
/// </summary>
public sealed class Counter
{
    private PaddedLong _cell;

    internal Counter(string name, CounterKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>Dotted lower-case name, e.g. <c>net.world.connections_refused</c>.</summary>
    public string Name { get; }

    public CounterKind Kind { get; }

    public long Value => Volatile.Read(ref _cell.Value);

    public void Increment() => Interlocked.Increment(ref _cell.Value);

    public void Add(long amount) => Interlocked.Add(ref _cell.Value, amount);

    public void Decrement() => Interlocked.Decrement(ref _cell.Value);

    public void Set(long value) => Volatile.Write(ref _cell.Value, value);

    public override string ToString() => $"{Name}={Value}";

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct PaddedLong
    {
        [FieldOffset(64)]
        public long Value;
    }
}

/// <summary>A counter's value at the moment of a snapshot.</summary>
public readonly record struct CounterSample(string Name, CounterKind Kind, long Value);

/// <summary>A consistent-by-counter snapshot: every value was read after the snapshot started, in registration order.</summary>
public sealed class CounterSnapshot
{
    private readonly CounterSample[] _samples;

    internal CounterSnapshot(long timestampMicros, CounterSample[] samples)
    {
        TimestampMicros = timestampMicros;
        _samples = samples;
    }

    public long TimestampMicros { get; }

    public int Count => _samples.Length;

    public ReadOnlySpan<CounterSample> Samples => _samples;

    /// <summary>The value of the named counter, or null when it was not registered at snapshot time.</summary>
    public long? this[string name]
    {
        get
        {
            foreach (CounterSample sample in _samples)
            {
                if (string.Equals(sample.Name, name, StringComparison.Ordinal))
                {
                    return sample.Value;
                }
            }

            return null;
        }
    }
}
