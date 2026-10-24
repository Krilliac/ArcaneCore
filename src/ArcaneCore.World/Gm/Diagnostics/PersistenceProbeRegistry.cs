namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// Read-only view of one write-behind persistence queue, as the GM diagnostics show it (<c>.arcane queues</c>, the cluster lease
/// summary). Reads may come from any thread and must not block: each member reads a volatile counter or takes the queue's own short lock.
/// </summary>
public interface IPersistenceQueueProbe
{
    /// <summary>The queue's display name ("character saves", "social", ...).</summary>
    string Name { get; }

    /// <summary>Writes queued or in progress.</summary>
    int Pending { get; }

    /// <summary>Keys (or character ids, as <c>char:ID</c>) whose last write failed and is still retained; empty when everything is durable, null when the queue cannot report it (unknown is not the same as empty).</summary>
    IReadOnlyList<string>? RetainedKeys { get; }

    /// <summary>True when <see cref="TerminalFailure"/> is meaningful; false when the queue has no halt state to report (a null <see cref="TerminalFailure"/> then means unknown, not running).</summary>
    bool ReportsTerminalFailure { get; }

    /// <summary>The failure that halted the queue for good (such as a rejected character ownership), or null while it is running (only meaningful when <see cref="ReportsTerminalFailure"/>).</summary>
    Exception? TerminalFailure { get; }

    /// <summary>The failure behind a retained write (which retained write is up to the queue; the keyed queues give the first key in ordinal order), else the terminal one, or null. Not necessarily the newest failure.</summary>
    Exception? RetainedFailure { get; }
}

/// <summary>An <see cref="IPersistenceQueueProbe"/> over delegates read on demand.</summary>
public sealed class DelegatePersistenceQueueProbe(
    string name,
    Func<int> pending,
    Func<IReadOnlyList<string>>? retainedKeys = null,
    Func<Exception?>? terminalFailure = null,
    Func<Exception?>? retainedFailure = null) : IPersistenceQueueProbe
{
    public string Name { get; } = name;

    public int Pending => pending();

    public IReadOnlyList<string>? RetainedKeys => retainedKeys?.Invoke();

    public bool ReportsTerminalFailure => terminalFailure is not null;

    public Exception? TerminalFailure => terminalFailure?.Invoke();

    public Exception? RetainedFailure => retainedFailure?.Invoke() ?? TerminalFailure;
}

/// <summary>
/// A point-in-time copy of one probe, safe to format after the queue moves on. A null <paramref name="RetainedKeys"/> or a false
/// <paramref name="TerminalKnown"/> means the queue cannot report that field: it prints <c>n/a</c> and never counts as healthy.
/// </summary>
public sealed record PersistenceQueueSnapshot(
    string Name,
    int Pending,
    IReadOnlyList<string>? RetainedKeys,
    Exception? TerminalFailure,
    Exception? RetainedFailure,
    bool TerminalKnown = true)
{
    /// <summary>The longest exception message the report shows; database messages can carry host or file detail.</summary>
    private const int MaxMessageLength = 120;

    /// <summary>True only when the queue reported both fields and holds nothing that is not durable and has not halted.</summary>
    public bool IsHealthy => RetainedKeys is { Count: 0 } && TerminalKnown && TerminalFailure is null;

    /// <summary>The one-line report: <c>name: N pending</c>, then the retained keys and the terminal failure; a field the queue cannot report prints <c>n/a</c>.</summary>
    public string Format()
    {
        string line = $"{Name}: {Pending} pending";
        if (RetainedKeys is null)
        {
            line += ", retained n/a";
        }
        else if (RetainedKeys.Count > 0)
        {
            line += $", RETAINED after failed writes: {string.Join(", ", RetainedKeys)}";
        }

        if (!TerminalKnown && TerminalFailure is null)
        {
            line += ", halted n/a";
        }
        else if (TerminalFailure is not null)
        {
            line += $", HALTED: {Describe(TerminalFailure)}";
        }

        return line;
    }

    /// <summary>A second line naming the failure behind a retained write, or null when the queue holds none (a halted queue already names its failure in <see cref="Format"/>).</summary>
    public string? FormatFailure()
        => RetainedFailure is not null && TerminalFailure is null ? $"  {Name} retained failure: {Describe(RetainedFailure)}" : null;

    private static string Describe(Exception ex)
    {
        string message = ex.Message;
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + "...";
        }

        return ex.GetType().Name + ": " + message;
    }
}

/// <summary>
/// The list of every write-behind queue the GM diagnostics report. A queue is registered by name with a resolver, which is called
/// on every <see cref="Snapshot"/> and may return null while the queue does not exist yet (a feature that is disabled or not
/// loaded): the entry is then skipped, so registering never depends on start-up order. Registering a name again replaces the
/// earlier entry. <see cref="WorldPersistenceProbes"/> registers the built-in queues, one line each.
/// <para>Thread safety: all members may be called from any thread; resolvers and probe reads run outside the registry's lock.</para>
/// </summary>
public sealed class PersistenceProbeRegistry
{
    private readonly Lock _gate = new();
    private readonly List<(string Name, Func<IPersistenceQueueProbe?> Resolve)> _entries = [];

    /// <summary>The number of registered names (whether or not their queue exists right now).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Register <paramref name="name"/>; <paramref name="resolve"/> yields its probe, or null while the queue is absent.</summary>
    public void Register(string name, Func<IPersistenceQueueProbe?> resolve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resolve);
        lock (_gate)
        {
            int index = _entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _entries[index] = (name, resolve);
            }
            else
            {
                _entries.Add((name, resolve));
            }
        }
    }

    /// <summary>Register an already built probe under its own name.</summary>
    public void Register(IPersistenceQueueProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Register(probe.Name, () => probe);
    }

    /// <summary>One snapshot per queue that exists now, in registration order. A probe that throws is reported as halted rather than hiding the others.</summary>
    public IReadOnlyList<PersistenceQueueSnapshot> Snapshot()
    {
        (string Name, Func<IPersistenceQueueProbe?> Resolve)[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
        }

        var result = new List<PersistenceQueueSnapshot>(entries.Length);
        foreach ((string name, Func<IPersistenceQueueProbe?> resolve) in entries)
        {
            try
            {
                if (resolve() is { } probe)
                {
                    result.Add(new PersistenceQueueSnapshot(probe.Name, probe.Pending, probe.RetainedKeys, probe.TerminalFailure, probe.RetainedFailure, probe.ReportsTerminalFailure));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Add(new PersistenceQueueSnapshot(name, 0, null, ex, ex));
            }
        }

        return result;
    }
}
