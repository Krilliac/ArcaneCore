using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Reload;

/// <summary>
/// Runs reloads one at a time in three phases: the <see cref="IContentReloadable"/> builds a
/// candidate on a worker thread and validates it there; the swap is then posted to the world
/// thread (<see cref="WorldRuntime.Post"/>, drained by <c>RunCommands</c> before any map updates,
/// so no map sees half of a swap) and runs inside a <see cref="ReloadTransaction"/> that rolls
/// back if it fails. Whatever goes wrong before the swap leaves live state untouched.
/// <para>
/// vmangos reloads on the world update thread and blocks it for the whole database read
/// (<c>ChatHandler::HandleReloadSpellTemplateCommand</c> → <c>SpellMgr::LoadSpells</c>,
/// ServerCommands.cpp:1409; SpellMgr.cpp:3702); building off-thread keeps the tick running and is
/// not observable to clients.
/// </para>
/// </summary>
public sealed class ReloadCoordinator
{
    private readonly ILogger _logger;
    private readonly HotReloadOptions _options;
    private readonly SortedDictionary<string, IContentReloadable> _reloadables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReloadResult> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _registrationLock = new();
    private WorldRuntime? _world;

    public ReloadCoordinator(ILogger logger, HotReloadOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new HotReloadOptions();
    }

    /// <summary>The names of the registered reloadables, sorted.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_registrationLock)
            {
                return [.. _reloadables.Keys];
            }
        }
    }

    /// <summary>The most recent result per reloadable (only names that have been reloaded since start).</summary>
    public IReadOnlyDictionary<string, ReloadResult> LastResults => _last;

    /// <summary>Whether a reload is running right now.</summary>
    public bool IsBusy => _running.CurrentCount == 0;

    /// <summary>The world whose thread performs the swaps.</summary>
    public void Attach(WorldRuntime world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    /// <summary>Add a reloadable; two with the same name (case-insensitive) are a programming error.</summary>
    public void Register(IContentReloadable reloadable)
    {
        ArgumentNullException.ThrowIfNull(reloadable);
        lock (_registrationLock)
        {
            if (!_reloadables.TryAdd(reloadable.Name, reloadable))
            {
                throw new InvalidOperationException($"reloadable '{reloadable.Name}' is registered twice");
            }
        }
    }

    /// <summary>Reload one reloadable by exact (case-insensitive) name. Never throws: every outcome is a <see cref="ReloadResult"/>.</summary>
    public async Task<ReloadResult> ReloadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        IContentReloadable? reloadable;
        lock (_registrationLock)
        {
            _reloadables.TryGetValue(name, out reloadable);
        }

        if (reloadable is null)
        {
            return Finish(name, ReloadStatus.Rejected, $"there is no reloadable '{name}' (known: {string.Join(", ", Names)})", [], Stopwatch.GetTimestamp(), remember: false);
        }

        if (!await _running.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return Finish(reloadable.Name, ReloadStatus.Busy, "another reload is still running", [], Stopwatch.GetTimestamp(), remember: false);
        }

        try
        {
            return await RunAsync(reloadable, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Reload every reloadable that <see cref="IContentReloadable.IncludedInAll"/>, one after the
    /// other in dependency order, with names breaking ties (vmangos <c>reload all</c>,
    /// ServerCommands.cpp:885-905, which leaves the config out).
    /// </summary>
    public async Task<IReadOnlyList<ReloadResult>> ReloadAllAsync(CancellationToken cancellationToken = default)
    {
        IContentReloadable[] included;
        lock (_registrationLock)
        {
            included = [.. _reloadables.Values.Where(r => r.IncludedInAll)];
        }

        var results = new List<ReloadResult>(included.Length);
        var remaining = included.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IContentReloadable>(included.Length);
        while (remaining.Count > 0)
        {
            IContentReloadable? next = remaining.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(r => !r.CommitAfter.Any(remaining.ContainsKey));
            if (next is null)
            {
                long started = Stopwatch.GetTimestamp();
                return included.Select(r => Finish(r.Name, ReloadStatus.Rejected,
                    "reload dependency cycle; nothing was changed", [], started)).ToArray();
            }

            ordered.Add(next);
            remaining.Remove(next.Name);
        }

        foreach (IContentReloadable reloadable in ordered)
        {
            results.Add(await ReloadAsync(reloadable.Name, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<ReloadResult> RunAsync(IContentReloadable reloadable, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string name = reloadable.Name;
        WorldRuntime? world = _world;
        if (world is null)
        {
            return Finish(name, ReloadStatus.Failed, "the reload coordinator is not attached to a world", [], started);
        }

        _logger.LogInformation("Reloading {Name}", name);

        // Phase 1 and 2, off the world thread: build and validate. Nothing here touches live state.
        ContentCandidate candidate;
        try
        {
            candidate = await BuildAsync(reloadable, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Finish(name, ReloadStatus.Failed, $"building timed out after {_options.BuildTimeoutMs} ms; nothing was changed", [], started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(name, ReloadStatus.Failed, "cancelled; nothing was changed", [], started);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Building the {Name} reload failed", name);
            return Finish(name, ReloadStatus.Failed, $"building failed ({ex.Message}); nothing was changed", [], started);
        }

        IReadOnlyList<string> problems;
        try
        {
            problems = candidate.Validate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validating the {Name} reload failed", name);
            return Finish(name, ReloadStatus.Failed, $"validation failed ({ex.Message}); nothing was changed", [], started);
        }

        if (problems.Count > 0)
        {
            return Finish(name, ReloadStatus.Rejected, $"{problems.Count} problem(s) found; nothing was changed", problems, started);
        }

        // Phase 3, on the world thread between ticks.
        return await CommitAsync(world, name, candidate, started).ConfigureAwait(false);
    }

    private async Task<ContentCandidate> BuildAsync(IContentReloadable reloadable, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        if (_options.BuildTimeoutMs > 0)
        {
            timeout.CancelAfter(_options.BuildTimeoutMs);
        }

        Task<ContentCandidate> build = Task.Run(() => reloadable.BuildAsync(linked.Token), CancellationToken.None);
        try
        {
            if (_options.BuildTimeoutMs > 0)
            {
                // A build that ignores its token must still not hold the reload forever.
                return await build.WaitAsync(TimeSpan.FromMilliseconds(_options.BuildTimeoutMs), cancellationToken).ConfigureAwait(false);
            }

            return await build.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
        catch (TimeoutException)
        {
            // The abandoned build may still finish; observe its outcome so it is never unobserved.
            _ = build.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            timeout.Cancel();
            throw;
        }
    }

    private async Task<ReloadResult> CommitAsync(WorldRuntime world, string name, ContentCandidate candidate, long started)
    {
        // 0 = queued, 1 = running or finished on the world thread, 2 = abandoned by the timeout (never runs).
        int state = 0;
        var done = new TaskCompletionSource<ReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Post(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                return;
            }

            done.SetResult(Swap(world, name, candidate, started));
        });

        if (_options.CommitTimeoutMs <= 0)
        {
            return await done.Task.ConfigureAwait(false);
        }

        try
        {
            return await done.Task.WaitAsync(TimeSpan.FromMilliseconds(_options.CommitTimeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                return Finish(name, ReloadStatus.Failed, $"the world thread did not run the swap within {_options.CommitTimeoutMs} ms; it was cancelled and nothing was changed", [], started);
            }

            // The swap had already started: it is short and all-or-nothing, so wait for its real outcome.
            return await done.Task.ConfigureAwait(false);
        }
    }

    /// <summary>The swap itself (world thread).</summary>
    private ReloadResult Swap(WorldRuntime world, string name, ContentCandidate candidate, long started)
    {
        var transaction = new ReloadTransaction();
        try
        {
            if (candidate.TryKeepCurrent(world, out string reason))
            {
                return Finish(name, ReloadStatus.KeptCurrent, $"{reason}; the loaded content was kept", [], started);
            }

            candidate.Commit(world, transaction);
        }
        catch (Exception ex)
        {
            IReadOnlyList<Exception> undoFailures = transaction.Rollback();
            _logger.LogError(ex, "Swapping in {Name} failed; rolled back", name);
            foreach (Exception undo in undoFailures)
            {
                _logger.LogError(undo, "Rolling back a {Name} reload step failed", name);
            }

            string suffix = undoFailures.Count == 0 ? "the change was rolled back" : $"the change was rolled back, but {undoFailures.Count} undo step(s) failed";
            return Finish(name, ReloadStatus.Failed, $"swap failed ({ex.Message}); {suffix}", [], started);
        }

        foreach (string note in transaction.Notes)
        {
            _logger.LogWarning("{Name} reload: {Note}", name, note);
        }

        return Finish(name, ReloadStatus.Applied, $"reloaded: {candidate.Summary}", transaction.Notes, started);
    }

    private ReloadResult Finish(string name, ReloadStatus status, string message, IReadOnlyList<string> notes, long started, bool remember = true)
    {
        var result = new ReloadResult(name, status, message, [.. notes], Stopwatch.GetElapsedTime(started), DateTimeOffset.UtcNow);
        if (remember)
        {
            _last[name] = result;
            _logger.LogInformation("Reload {Name}: {Status} - {Message}", name, status, message);
        }

        return result;
    }
}
