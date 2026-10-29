using ArcaneCore.Kernel.Diagnostics;
using System.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Features;

/// <summary>Where a feature is in its lifecycle.</summary>
public enum FeatureState
{
    /// <summary>In the world: its subscriptions fire.</summary>
    Attached,

    /// <summary>Its background work was stopped for a detach that did not reach the world thread; still attached. <c>.feature attach</c> re-attaches it.</summary>
    Quiesced,

    /// <summary>Out of the world: no subscription left, its handover (if any) kept for the next attach.</summary>
    Detached,

    /// <summary>A failed detach could not be rolled back: out of the world, handover kept; <c>.feature attach</c> retries.</summary>
    Faulted,
}

/// <summary>One feature as <c>.feature list</c> shows it.</summary>
/// <param name="Name">The name commands take: the type name, or the full type name when two features share a type name.</param>
/// <param name="TypeName">The full type name.</param>
/// <param name="Kind">How it may detach; every plain <see cref="IWorldFeature"/> is <see cref="DetachKind.Pinned"/>.</param>
/// <param name="State">Where it is now.</param>
/// <param name="Subscriptions">Subscriptions its bag holds (0 when detached or not detachable).</param>
/// <param name="HandoverValues">Values in the kept handover (0 when attached).</param>
/// <param name="ChangedUtc">The last lifecycle change after start, or null.</param>
/// <param name="LastError">Why the last operation failed, or null.</param>
public sealed record FeatureStatus(
    string Name,
    string TypeName,
    DetachKind Kind,
    FeatureState State,
    int Subscriptions,
    int HandoverValues,
    DateTimeOffset? ChangedUtc,
    string? LastError);

/// <summary>What a detach, attach or cycle did.</summary>
/// <param name="Ok">The requested change is in force.</param>
/// <param name="Detail">What happened, for the reply and the audit line.</param>
/// <param name="Result">The hot swap result it was reported as.</param>
public sealed record FeatureResult(bool Ok, string Detail, HotSwapResult Result);

/// <summary>
/// Owns the lifecycle of the world features (docs/design/hot-reload-tiers.md, section 4.2): attaches every feature at
/// start (an <see cref="IDetachableWorldFeature"/> through <see cref="IDetachableWorldFeature.AttachFrom"/> with a bag the
/// host owns), and detaches and re-attaches a detachable one in a running world.
/// <list type="bullet">
/// <item><b>Tick boundary.</b> Quiesce runs off the world thread; detach and attach are posted to the world thread
/// (<see cref="IHotCodeWorld.Post"/>, before any map update) and a commit the world thread does not reach in time is
/// cancelled for good.</item>
/// <item><b>All or nothing.</b> A detach whose <see cref="IDetachableWorldFeature.Detach"/> or unsubscribes throw is rolled
/// back in the same tick: the bag is disposed and the feature re-attached from the same handover. An attach that throws
/// disposes what it subscribed and leaves the feature detached with its handover. A quiesce that fails is undone by a
/// detach and re-attach in one tick. Only a rollback that itself throws leaves the feature <see cref="FeatureState.Faulted"/>.</item>
/// <item><b>Pinned refuses.</b> A plain <see cref="IWorldFeature"/>, or a detachable one whose kind is
/// <see cref="DetachKind.Pinned"/>, is never detached.</item>
/// <item>Every operation is serialized, logged, written to the hot code audit file and reported as a hot swap
/// (<see cref="HotSwapLane.Feature"/>), like a module swap.</item>
/// </list>
/// </summary>
public sealed class FeatureHost
{
    private readonly IHotCodeWorld? _poster;
    private readonly Func<HotCodeAudit?> _audit;
    private readonly Func<IHotSwapReporter?> _reporter;
    private readonly ILogger _logger;
    private readonly TimeSpan _commitTimeout;
    private readonly TimeSpan _quiesceTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private WorldRuntime? _world;
    private IHotCodeWorld? _worldPoster;

    /// <param name="poster">Where commits run; default the attached world's <see cref="WorldRuntime.Post"/>.</param>
    /// <param name="audit">The audit file, resolved when an operation runs (null: none).</param>
    /// <param name="reporter">The hot swap reporter, resolved when an operation runs (null: none).</param>
    /// <param name="logger">Lifecycle log.</param>
    /// <param name="commitTimeout">How long a commit may wait for the world thread (default 10 s).</param>
    /// <param name="quiesceTimeout">How long a feature may take to quiesce (default 30 s).</param>
    /// <param name="clock">UTC clock.</param>
    public FeatureHost(
        IHotCodeWorld? poster = null,
        Func<HotCodeAudit?>? audit = null,
        Func<IHotSwapReporter?>? reporter = null,
        ILogger? logger = null,
        TimeSpan? commitTimeout = null,
        TimeSpan? quiesceTimeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        _poster = poster;
        _audit = audit ?? (() => null);
        _reporter = reporter ?? (() => null);
        _logger = logger ?? NullLogger.Instance;
        _commitTimeout = commitTimeout ?? TimeSpan.FromSeconds(10);
        _quiesceTimeout = quiesceTimeout ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attach <paramref name="features"/> in order to <paramref name="world"/> (once, before the world thread starts). A
    /// plain feature gets <see cref="IWorldFeature.Attach"/>; a detachable one <see cref="IDetachableWorldFeature.AttachFrom"/>
    /// with a null handover and a bag the host owns. An exception propagates, as a failing <c>Attach</c> did before; the
    /// features attached before it stay tracked so shutdown still reaches them.
    /// </summary>
    public void AttachAll(WorldRuntime world, IEnumerable<IWorldFeature> features)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(features);
        IWorldFeature[] all = [.. features];
        lock (_gate)
        {
            if (_world is not null)
            {
                throw new InvalidOperationException("the features are already attached");
            }

            _world = world;
            _worldPoster = _poster ?? new WorldRuntimeHotCodeWorld(world);
            HashSet<string> shared = [.. all.GroupBy(f => f.GetType().Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)];
            foreach (IWorldFeature feature in all)
            {
                Type type = feature.GetType();
                _entries.Add(new Entry(
                    shared.Contains(type.Name) ? type.FullName ?? type.Name : type.Name,
                    feature,
                    feature as IDetachableWorldFeature));
            }
        }

        foreach (Entry entry in Snapshot())
        {
            if (entry.Detachable is not { } detachable)
            {
                entry.Feature.Attach(world);
                continue;
            }

            var bag = new FeatureSubscriptions(entry.Name);
            try
            {
                detachable.AttachFrom(world, null, bag);
            }
            catch
            {
                DisposeQuietly(bag, entry.Name);
                throw;
            }

            lock (_gate)
            {
                entry.Subscriptions = bag;
            }
        }
    }

    /// <summary>Every feature, pinned ones included, in attachment order.</summary>
    public IReadOnlyList<FeatureStatus> List()
    {
        lock (_gate)
        {
            return [.. _entries.Select(StatusOf)];
        }
    }

    /// <summary>The feature <paramref name="name"/> (type name or full type name, any case), or null.</summary>
    public FeatureStatus? Find(string name)
    {
        lock (_gate)
        {
            return Resolve(name) is { } entry ? StatusOf(entry) : null;
        }
    }

    /// <summary>
    /// Whether <paramref name="feature"/> is out of the world now, <see cref="FeatureState.Detached"/> or <see cref="FeatureState.Faulted"/>
    /// (shutdown skips its <see cref="IWorldFeature.StopAsync"/>: its quiesce already drained it, and a faulted one holds no subscription).
    /// </summary>
    public bool IsDetached(IWorldFeature feature)
    {
        lock (_gate)
        {
            return _entries.Any(e => ReferenceEquals(e.Feature, feature) && e.State is FeatureState.Detached or FeatureState.Faulted);
        }
    }

    /// <summary>
    /// Whether <paramref name="feature"/> is in the world now: tracked by <see cref="AttachAll"/> and neither <see cref="FeatureState.Detached"/>
    /// nor <see cref="FeatureState.Faulted"/>. False for a feature a role filter never admitted.
    /// </summary>
    public bool IsAttached(IWorldFeature feature)
    {
        lock (_gate)
        {
            return _entries.Any(e => ReferenceEquals(e.Feature, feature) && e.State is not (FeatureState.Detached or FeatureState.Faulted));
        }
    }

    /// <summary>Whether <see cref="AttachAll"/> has run (shutdown then skips the features it never admitted).</summary>
    public bool HasAttached
    {
        get
        {
            lock (_gate)
            {
                return _world is not null;
            }
        }
    }

    /// <summary>Quiesce <paramref name="name"/>, then detach it at a tick boundary. Off the world thread; never throws for a feature failure.</summary>
    public Task<FeatureResult> DetachAsync(string name, HotSwapOrigin? origin = null)
        => Task.Run(() => RunAsync("detach", name, origin, DetachCoreAsync));

    /// <summary>Attach a detached (or faulted) <paramref name="name"/> from its kept handover at a tick boundary; a quiesced one is detached and re-attached in one tick.</summary>
    public Task<FeatureResult> AttachAsync(string name, HotSwapOrigin? origin = null)
        => Task.Run(() => RunAsync("attach", name, origin, AttachCoreAsync));

    /// <summary>Quiesce <paramref name="name"/>, then detach it and attach it again from the handover in one tick (the shape of a pack swap).</summary>
    public Task<FeatureResult> CycleAsync(string name, HotSwapOrigin? origin = null)
        => Task.Run(() => RunAsync("cycle", name, origin, CycleCoreAsync));

    private async Task<FeatureResult> RunAsync(string action, string name, HotSwapOrigin? origin, Func<Entry, Task<Outcome>> run)
    {
        origin ??= HotSwapOrigin.Console;
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            long started = Stopwatch.GetTimestamp();
            Entry? entry;
            lock (_gate)
            {
                entry = _world is null ? null : Resolve(name);
            }

            Outcome outcome = entry is null
                ? Outcome.Reject(_world is null ? "the features are not attached yet" : $"no feature '{name}'")
                : await run(entry).ConfigureAwait(false);
            string area = entry?.Name ?? name;
            if (entry is not null && !outcome.Ok && outcome.Result != HotSwapResult.Rejected)
            {
                lock (_gate)
                {
                    entry.LastError = outcome.Detail;
                }
            }

            return Report(action, area, outcome, origin, Stopwatch.GetElapsedTime(started));
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<Outcome> DetachCoreAsync(Entry entry)
    {
        if (Refusal(entry) is { } refused)
        {
            return refused;
        }

        if (entry.State != FeatureState.Attached)
        {
            return Outcome.Reject($"{entry.Name} is {Describe(entry.State)}, not attached");
        }

        if (await QuiesceAsync(entry).ConfigureAwait(false) is { } quiesceError)
        {
            return await RestoreAfterQuiesceAsync(entry, quiesceError).ConfigureAwait(false);
        }

        return await CommitAsync(entry, () => DetachOnWorld(entry, reattach: false)).ConfigureAwait(false);
    }

    private async Task<Outcome> AttachCoreAsync(Entry entry)
    {
        if (Refusal(entry) is { } refused)
        {
            return refused;
        }

        return entry.State switch
        {
            FeatureState.Attached => Outcome.Reject($"{entry.Name} is already attached"),
            FeatureState.Quiesced => await CommitAsync(entry, () => DetachOnWorld(entry, reattach: true)).ConfigureAwait(false),
            _ => await CommitAsync(entry, () => AttachOnWorld(entry)).ConfigureAwait(false),
        };
    }

    private async Task<Outcome> CycleCoreAsync(Entry entry)
    {
        if (Refusal(entry) is { } refused)
        {
            return refused;
        }

        if (entry.State != FeatureState.Attached)
        {
            return Outcome.Reject($"{entry.Name} is {Describe(entry.State)}, not attached");
        }

        if (await QuiesceAsync(entry).ConfigureAwait(false) is { } quiesceError)
        {
            return await RestoreAfterQuiesceAsync(entry, quiesceError).ConfigureAwait(false);
        }

        return await CommitAsync(entry, () => DetachOnWorld(entry, reattach: true)).ConfigureAwait(false);
    }

    private static Outcome? Refusal(Entry entry) => entry.Detachable is null
        ? Outcome.Reject($"{entry.Name} is pinned (not an IDetachableWorldFeature)")
        : entry.Detachable.DetachKind == DetachKind.Pinned
            ? Outcome.Reject($"{entry.Name} is pinned (DetachKind.Pinned)")
            : null;

    /// <summary>Null when the feature quiesced, else why not.</summary>
    private async Task<string?> QuiesceAsync(Entry entry)
    {
        using var cancel = new CancellationTokenSource(_quiesceTimeout);
        try
        {
            await entry.Detachable!.QuiesceAsync(cancel.Token).WaitAsync(_quiesceTimeout).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException ex)
        {
            FaultLedger.Shared.Record("feature.host", ex);
            AnomalyLedger.Shared.Report("feature", "quiesce-timeout", entry.Name, $"timeout={_quiesceTimeout.TotalSeconds:0.#}s", AnomalySeverity.Warning);
            return $"it did not quiesce within {_quiesceTimeout.TotalSeconds:0.#}s";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("feature.host", ex);
            AnomalyLedger.Shared.Report("feature", "quiesce-timeout", entry.Name, $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            _logger.LogError(ex, "Feature {Feature} failed to quiesce", entry.Name);
            return $"quiesce threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// A failed quiesce may have stopped part of the feature's work: detach and re-attach it in one tick so it restarts whole. The
    /// feature's <see cref="IDetachableWorldFeature.Detach"/> may refuse (throw) while work the quiesce did not drain is still running,
    /// and its <see cref="IDetachableWorldFeature.AttachFrom"/> must keep, not duplicate, any service the quiesce did not stop.
    /// </summary>
    private async Task<Outcome> RestoreAfterQuiesceAsync(Entry entry, string quiesceError)
    {
        SetState(entry, FeatureState.Quiesced, entry.Handover);
        Outcome restored = await CommitAsync(entry, () => DetachOnWorld(entry, reattach: true)).ConfigureAwait(false);
        return restored.Ok || restored.RolledBack
            ? Outcome.Fail($"{quiesceError}; nothing was detached (re-attached to restart its work)", rolledBack: true)
            : Outcome.Fail($"{quiesceError}; restoring it failed too: {restored.Detail}");
    }

    /// <summary>Run <paramref name="apply"/> on the world thread; a commit that did not run leaves the feature quiesced (if it was attached).</summary>
    private async Task<Outcome> CommitAsync(Entry entry, Func<Outcome> apply)
    {
        Outcome? outcome = null;
        string? notRun = await WorldCommit.RunAsync(_worldPoster!, _commitTimeout, () =>
        {
            outcome = apply();
            return null;
        }).ConfigureAwait(false);
        if (outcome is { } done)
        {
            return done;
        }

        // Timed out (nothing ran) or the apply escaped its own handling (should not happen: every step catches).
        FeatureState state;
        lock (_gate)
        {
            state = entry.State;
        }

        if (state == FeatureState.Attached)
        {
            SetState(entry, FeatureState.Quiesced, entry.Handover);
            return Outcome.Fail($"{notRun}; {entry.Name} is quiesced but still attached (.feature attach {entry.Name} restarts it)");
        }

        return Outcome.Fail(notRun ?? "the commit did not run");
    }

    /// <summary>
    /// World thread. Detach, dispose the bag, then (when <paramref name="reattach"/>, or to roll a failure back) attach again
    /// from the same handover.
    /// </summary>
    private Outcome DetachOnWorld(Entry entry, bool reattach)
    {
        IDetachableWorldFeature feature = entry.Detachable!;
        var handover = new FeatureHandover();
        Exception? failure = null;
        try
        {
            feature.Detach(handover);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("feature.host", ex);
            AnomalyLedger.Shared.Report("feature", "detach-fault", entry.Name, $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            failure = ex;
        }

        FeatureSubscriptions? bag;
        lock (_gate)
        {
            bag = entry.Subscriptions;
            entry.Subscriptions = null;
        }

        int removed = bag?.Count ?? 0;
        try
        {
            bag?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("feature.host", ex);
            AnomalyLedger.Shared.Report("feature", "detach-fault", entry.Name, $"dispose_error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            failure = failure is null ? ex : new AggregateException(failure, ex);
        }

        FeatureHandover? carried = feature.DetachKind == DetachKind.Handover ? handover : null;
        if (failure is null && !reattach)
        {
            SetState(entry, FeatureState.Detached, carried);
            return Outcome.Apply($"{entry.Name} detached: {removed} subscriptions removed{HandoverText(carried)}");
        }

        if (failure is not null)
        {
            _logger.LogError(failure, "Feature {Feature} failed to detach; rolling back", entry.Name);
        }

        string? attachError = AttachFrom(entry, carried);
        if (failure is null)
        {
            return attachError is null
                ? Outcome.Apply($"{entry.Name} detached and re-attached in one tick: {removed} subscriptions removed, {entry.Subscriptions?.Count ?? 0} made{HandoverText(carried)}")
                : Fault(entry, carried, $"{entry.Name} detached, but attaching it again threw: {attachError}");
        }

        string why = failure is AggregateException { InnerExceptions.Count: > 0 } many ? many.InnerExceptions[0].Message : failure.Message;
        return attachError is null
            ? Outcome.Fail($"detaching {entry.Name} threw ({why}); rolled back: re-attached from the same handover", rolledBack: true)
            : Fault(entry, carried, $"detaching {entry.Name} threw ({why}) and the rollback attach threw too: {attachError}");
    }

    /// <summary>World thread. Attach a detached or faulted feature from its kept handover.</summary>
    private Outcome AttachOnWorld(Entry entry)
    {
        FeatureHandover? handover = entry.Handover;
        string? error = AttachFrom(entry, handover);
        return error is null
            ? Outcome.Apply($"{entry.Name} attached: {entry.Subscriptions?.Count ?? 0} subscriptions{HandoverText(handover)}")
            : Outcome.Fail($"attaching {entry.Name} threw: {error}; it stays {Describe(entry.State)} with its handover");
    }

    /// <summary>World thread. Null when attached; else the error, with every subscription the attempt made undone.</summary>
    private string? AttachFrom(Entry entry, FeatureHandover? handover)
    {
        var bag = new FeatureSubscriptions(entry.Name);
        try
        {
            entry.Detachable!.AttachFrom(_world!, handover, bag);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("feature.host", ex);
            AnomalyLedger.Shared.Report("feature", "attach-fault", entry.Name, $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            _logger.LogError(ex, "Feature {Feature} failed to attach", entry.Name);
            DisposeQuietly(bag, entry.Name);
            return $"{ex.GetType().Name}: {ex.Message}";
        }

        lock (_gate)
        {
            entry.Subscriptions = bag;
            entry.Handover = null;
            entry.State = FeatureState.Attached;
            entry.LastError = null;
            entry.ChangedUtc = _clock();
        }

        return null;
    }

    private Outcome Fault(Entry entry, FeatureHandover? handover, string detail)
    {
        SetState(entry, FeatureState.Faulted, handover);
        return Outcome.Fail($"{detail}; {entry.Name} is out of the world (.feature attach {entry.Name} retries)");
    }

    private void SetState(Entry entry, FeatureState state, FeatureHandover? handover)
    {
        lock (_gate)
        {
            entry.State = state;
            entry.Handover = handover;
            entry.ChangedUtc = _clock();
        }
    }

    private FeatureResult Report(string action, string area, Outcome outcome, HotSwapOrigin origin, TimeSpan duration)
    {
        HotSwapResult result = outcome.Result;
        if (result == HotSwapResult.Applied)
        {
            _logger.LogInformation("Feature {Action} {Feature} by {Actor}: {Detail}", action, area, origin.Actor, outcome.Detail);
        }
        else
        {
            _logger.LogWarning("Feature {Action} {Feature} by {Actor} {Result}: {Detail}", action, area, origin.Actor, result, outcome.Detail);
        }

        _audit()?.Record("feature", $"{origin.Actor} {action} {area}: {result.ToString().ToLowerInvariant()}; {outcome.Detail}");
        _reporter()?.Report(new HotSwapEvent(HotSwapLane.Feature, action, area, result, origin, duration, outcome.Detail));
        return new FeatureResult(outcome.Ok, outcome.Detail, result);
    }

    private Entry? Resolve(string name)
    {
        string wanted = (name ?? string.Empty).Trim();
        return _entries.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? _entries.FirstOrDefault(e => string.Equals(e.Feature.GetType().FullName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private Entry[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    private static FeatureStatus StatusOf(Entry e) => new(
        e.Name,
        e.Feature.GetType().FullName ?? e.Name,
        e.Detachable?.DetachKind ?? DetachKind.Pinned,
        e.State,
        e.Subscriptions?.Count ?? 0,
        e.Handover?.Count ?? 0,
        e.ChangedUtc,
        e.LastError);

    private void DisposeQuietly(FeatureSubscriptions bag, string name)
    {
        try
        {
            bag.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("feature.host", ex);
            AnomalyLedger.Shared.Report("feature", "detach-fault", name, $"undo_subscriptions_error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Warning);
            _logger.LogError(ex, "Undoing the subscriptions of {Feature} failed", name);
        }
    }

    private static string HandoverText(FeatureHandover? handover) => handover is null ? string.Empty : $", handover {handover.Count} values";

    private static string Describe(FeatureState state) => state.ToString().ToLowerInvariant();

    private sealed class Entry(string name, IWorldFeature feature, IDetachableWorldFeature? detachable)
    {
        public string Name { get; } = name;

        public IWorldFeature Feature { get; } = feature;

        public IDetachableWorldFeature? Detachable { get; } = detachable;

        public FeatureState State { get; set; } = FeatureState.Attached;

        public FeatureSubscriptions? Subscriptions { get; set; }

        public FeatureHandover? Handover { get; set; }

        public DateTimeOffset? ChangedUtc { get; set; }

        public string? LastError { get; set; }
    }

    private readonly record struct Outcome(bool Ok, string Detail, HotSwapResult Result, bool RolledBack)
    {
        public static Outcome Apply(string detail) => new(true, detail, HotSwapResult.Applied, false);

        public static Outcome Reject(string detail) => new(false, detail, HotSwapResult.Rejected, false);

        public static Outcome Fail(string detail, bool rolledBack = false) => new(false, detail, HotSwapResult.Failed, rolledBack);
    }
}
