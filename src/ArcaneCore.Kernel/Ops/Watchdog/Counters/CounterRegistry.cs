namespace ArcaneCore.Kernel.Ops.Watchdog.Counters;

/// <summary>
/// The process-wide set of counters. Registration (<see cref="GetOrAdd"/>) is the cold path: it
/// takes a lock and replaces an immutable array (copy on write). Reading is the hot path:
/// <see cref="Counters"/> is one volatile load of that array, so enumerating it allocates
/// nothing and never blocks a writer. <see cref="Default"/> is the instance both daemons
/// register in DI and dump periodically; a feature that cannot take a dependency uses it
/// directly. Names are <c>[a-z0-9_.]</c>, at most 64 characters, unique per registry.
/// </summary>
public sealed class CounterRegistry
{
    public const int MaxNameLength = 64;

    private readonly object _gate = new();
    private Counter[] _counters = [];

    /// <summary>The shared registry.</summary>
    public static CounterRegistry Default { get; } = new();

    /// <summary>Every registered counter in registration order (a stable array: new registrations do not change it).</summary>
    public ReadOnlySpan<Counter> Counters => Volatile.Read(ref _counters);

    public int Count => Volatile.Read(ref _counters).Length;

    /// <summary>
    /// The counter with this name, created on first use. Asking for an existing name with a
    /// different <paramref name="kind"/> throws: two owners disagree on what the counter means.
    /// </summary>
    public Counter GetOrAdd(string name, CounterKind kind = CounterKind.Counter)
    {
        ValidateName(name);
        Counter? existing = Find(Volatile.Read(ref _counters), name);
        if (existing is null)
        {
            lock (_gate)
            {
                existing = Find(_counters, name);
                if (existing is null)
                {
                    existing = new Counter(name, kind);
                    Counter[] next = new Counter[_counters.Length + 1];
                    Array.Copy(_counters, next, _counters.Length);
                    next[^1] = existing;
                    Volatile.Write(ref _counters, next);
                    return existing;
                }
            }
        }

        if (existing.Kind != kind)
        {
            throw new InvalidOperationException($"counter '{name}' is registered as a {existing.Kind}, not a {kind}");
        }

        return existing;
    }

    /// <summary>The registered counter with this name, or null.</summary>
    public Counter? Find(string name) => Find(Volatile.Read(ref _counters), name);

    /// <summary>Read every counter once (allocates the result; the readers of a dump call this on demand).</summary>
    public CounterSnapshot Snapshot(long timestampMicros)
    {
        Counter[] counters = Volatile.Read(ref _counters);
        var samples = new CounterSample[counters.Length];
        for (int i = 0; i < counters.Length; i++)
        {
            samples[i] = new CounterSample(counters[i].Name, counters[i].Kind, counters[i].Value);
        }

        return new CounterSnapshot(timestampMicros, samples);
    }

    private static Counter? Find(Counter[] counters, string name)
    {
        foreach (Counter counter in counters)
        {
            if (string.Equals(counter.Name, name, StringComparison.Ordinal))
            {
                return counter;
            }
        }

        return null;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"counter name '{name}' is longer than {MaxNameLength} characters", nameof(name));
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.'))
            {
                throw new ArgumentException($"counter name '{name}' must be [a-z0-9_.]", nameof(name));
            }
        }
    }
}
