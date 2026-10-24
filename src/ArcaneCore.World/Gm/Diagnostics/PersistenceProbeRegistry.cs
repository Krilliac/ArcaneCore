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

    /// <summary>Keys (or character ids, as <c>char:ID</c>) whose last write failed and is still retained; empty when everything is durable.</summary>
    IReadOnlyList<string> RetainedKeys { get; }

    /// <summary>The failure that halted the queue for good (such as a rejected character ownership), or null while it is running.</summary>
    Exception? TerminalFailure { get; }

    /// <summary>The most recent failure the queue still holds (a retained write's, else the terminal one), or null.</summary>
    Exception? LastFailure { get; }
}

/// <summary>An <see cref="IPersistenceQueueProbe"/> over delegates read on demand.</summary>
public sealed class DelegatePersistenceQueueProbe(
    string name,
    Func<int> pending,
    Func<IReadOnlyList<string>>? retainedKeys = null,
    Func<Exception?>? terminalFailure = null,
    Func<Exception?>? lastFailure = null) : IPersistenceQueueProbe
{
    public string Name { get; } = name;

    public int Pending => pending();

    public IReadOnlyList<string> RetainedKeys => retainedKeys?.Invoke() ?? [];

    public Exception? TerminalFailure => terminalFailure?.Invoke();

    public Exception? LastFailure => lastFailure?.Invoke() ?? TerminalFailure;
}

/// <summary>A point-in-time copy of one probe, safe to format after the queue moves on.</summary>
public sealed record PersistenceQueueSnapshot(
    string Name,
    int Pending,
    IReadOnlyList<string> RetainedKeys,
    Exception? TerminalFailure,
    Exception? LastFailure)
{
    /// <summary>True when the queue holds nothing that is not durable and has not halted.</summary>
    public bool IsHealthy => RetainedKeys.Count == 0 && TerminalFailure is null;

    /// <summary>The one-line report: <c>name: N pending</c>, then the retained keys and the terminal failure.</summary>
    public string Format()
    {
        string line = $"{Name}: {Pending} pending";
        if (RetainedKeys.Count > 0)
        {
            line += $", RETAINED after failed writes: {string.Join(", ", RetainedKeys)}";
        }

        if (TerminalFailure is not null)
        {
            line += $", HALTED: {Describe(TerminalFailure)}";
        }

        return line;
    }

    /// <summary>A second line naming the failure behind a retained write, or null when the queue holds none (a halted queue already names its failure in <see cref="Format"/>).</summary>
    public string? FormatFailure()
        => LastFailure is not null && TerminalFailure is null ? $"  {Name} last failure: {Describe(LastFailure)}" : null;

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
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
                    result.Add(new PersistenceQueueSnapshot(probe.Name, probe.Pending, probe.RetainedKeys, probe.TerminalFailure, probe.LastFailure));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Add(new PersistenceQueueSnapshot(name, 0, [], ex, ex));
            }
        }

        return result;
    }
}
