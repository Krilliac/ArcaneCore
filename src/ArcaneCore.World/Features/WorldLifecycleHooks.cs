using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Features;

/// <summary>
/// The core's <see cref="IWorldLifecycleHooks"/>: named startup and shutdown hooks a feature registers (through its
/// <see cref="FeatureSubscriptions"/>, so a detach removes them) instead of the world host calling the feature by type.
/// <see cref="RunAsync"/> runs every hook of a stage even when one fails, then throws the failures together.
/// Thread-safe; a registration made while a stage runs is picked up by the next run.
/// </summary>
public sealed class WorldLifecycleHooks(ILogger<WorldLifecycleHooks>? logger = null) : IWorldLifecycleHooks
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly List<Hook> _hooks = [];

    public IDisposable Register(WorldLifecycleStage stage, string name, Func<CancellationToken, Task> hook) => Add(stage, name, hook, last: false);

    /// <summary>
    /// Register a core hook that runs after every <see cref="Register"/>ed hook of <paramref name="stage"/>, whatever the order the
    /// features attached in (a pack from another assembly attaches in assembly-name order). The core's sweep of managed bots no
    /// feature logged out uses it, so it always follows the playerbot shutdown. Several run in registration order.
    /// </summary>
    internal IDisposable RegisterLast(WorldLifecycleStage stage, string name, Func<CancellationToken, Task> hook) => Add(stage, name, hook, last: true);

    private IDisposable Add(WorldLifecycleStage stage, string name, Func<CancellationToken, Task> hook, bool last)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(hook);
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown lifecycle stage");
        }

        var entry = new Hook(stage, name, hook, last);
        lock (_gate)
        {
            if (_hooks.Any(h => h.Stage == stage && string.Equals(h.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"a {stage} hook named '{name}' is already registered");
            }

            _hooks.Add(entry);
        }

        return new Registration(this, entry);
    }

    /// <summary>The names registered for <paramref name="stage"/>, in the order <see cref="RunAsync"/> runs them.</summary>
    public IReadOnlyList<string> Registered(WorldLifecycleStage stage) => [.. Ordered(stage).Select(h => h.Name)];

    /// <summary>
    /// Run the hooks of <paramref name="stage"/>: <see cref="WorldLifecycleStage.AfterWorldStart"/> in registration order,
    /// <see cref="WorldLifecycleStage.BeforeWorldStop"/> newest first; then the <see cref="RegisterLast"/> ones. Every hook runs;
    /// failures are thrown together.
    /// </summary>
    public async Task RunAsync(WorldLifecycleStage stage, CancellationToken cancellationToken = default)
    {
        List<Exception> failures = [];
        foreach (Hook hook in Ordered(stage))
        {
            try
            {
                await hook.Run(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "World {Stage} hook {Hook} failed", stage, hook.Name);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException($"{failures.Count} world {stage} hook(s) failed", failures);
        }
    }

    private Hook[] Ordered(WorldLifecycleStage stage)
    {
        lock (_gate)
        {
            IEnumerable<Hook> hooks = _hooks.Where(h => h.Stage == stage && !h.Last);
            IEnumerable<Hook> last = _hooks.Where(h => h.Stage == stage && h.Last);
            return stage == WorldLifecycleStage.BeforeWorldStop ? [.. hooks.Reverse(), .. last] : [.. hooks, .. last];
        }
    }

    private void Remove(Hook hook)
    {
        lock (_gate)
        {
            _hooks.RemoveAll(h => ReferenceEquals(h, hook));
        }
    }

    private sealed record Hook(WorldLifecycleStage Stage, string Name, Func<CancellationToken, Task> Run, bool Last);

    private sealed class Registration(WorldLifecycleHooks owner, Hook hook) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(hook);
            }
        }
    }
}
