using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Rates;

/// <summary>Where a rate change came from.</summary>
public enum RateChangeKind
{
    /// <summary><c>.rates set</c>.</summary>
    Set,

    /// <summary><c>.rates reset</c>.</summary>
    Reset,

    /// <summary><c>.reload config</c> (or its rollback) put the configured values back.</summary>
    Reload,
}

/// <summary>
/// One live rate change, the payload of <see cref="ServerRates.RateChanged"/>. <paramref name="Actor"/> is the GM's name (or "Console");
/// <paramref name="Reason"/> is the free text after the value of <c>.rates set</c>, null when none was given.
/// </summary>
public readonly record struct RateChange(RateDefinition Rate, float Old, float New, RateChangeKind Kind, string Actor, string? Reason);

/// <summary>One entry of <see cref="ServerRates.History"/>: when (UTC) a rate changed, and the change.</summary>
public readonly record struct RateHistoryEntry(DateTimeOffset At, RateChange Change);

/// <summary>The services one rate operation resolves its option objects from (world thread).</summary>
public readonly record struct RateScope(IServiceProvider Services, WorldRuntime World, ILogger Logger);

/// <summary>One option object (or one property of it) a rate reads and writes. Both delegates run on the world thread.</summary>
/// <remarks>
/// <paramref name="Snapshot"/> captures the value in its native type and returns the action that puts it back exactly (null: the float read through
/// <paramref name="Get"/> is restored), so a target stored as a double does not lose precision on reset.
/// </remarks>
public readonly record struct RateTarget(Func<float> Get, Action<float> Set, Func<Action>? Snapshot = null)
{
    internal Action Capture()
    {
        if (Snapshot is { } snapshot)
        {
            return snapshot();
        }

        float value = Get();
        Action<float> set = Set;
        return () => set(value);
    }
}

/// <summary>
/// One live-tunable rate: its vmangos configuration name (<c>Rate.XP.Kill</c>), short aliases, the allowed range and how to reach the
/// shared option objects the consumers read. <see cref="Resolve"/> is called for every operation, so the targets always belong to the
/// features running now; it returns no target when the owning feature is not running.
/// </summary>
public sealed class RateDefinition(
    string name, string description, float min, float max, Func<RateScope, IReadOnlyList<RateTarget>> resolve, string appliesTo, string[] aliases,
    Action<RateScope>? applied = null, Func<RateScope, float, string?>? crossCheck = null)
{
    /// <summary>The vmangos name, e.g. <c>Rate.XP.Kill</c>.</summary>
    public string Name { get; } = name;

    /// <summary>What the rate scales.</summary>
    public string Description { get; } = description;

    /// <summary>Lowest accepted value.</summary>
    public float Min { get; } = min;

    /// <summary>Highest accepted value.</summary>
    public float Max { get; } = max;

    /// <summary>When a change takes effect, for the help text.</summary>
    public string AppliesTo { get; } = appliesTo;

    /// <summary>Short names that also select the rate (matched case-insensitively), besides the name and the name without <c>Rate.</c>.</summary>
    public IReadOnlyList<string> Aliases { get; } = aliases;

    internal Func<RateScope, IReadOnlyList<RateTarget>> Resolve { get; } = resolve;

    /// <summary>Runs after every change of the rate (set, reset, reload, rollback) once the targets hold the new value: pushes it into state the option object does not own.</summary>
    internal Action<RateScope>? Applied { get; } = applied;

    /// <summary>A check against other rates (null: fine), run by <see cref="ServerRates.TrySet"/> after the range check.</summary>
    internal Func<RateScope, float, string?>? CrossCheck { get; } = crossCheck;

    /// <summary>Null when <paramref name="value"/> is acceptable, else the reason it is not.</summary>
    public string? Validate(float value)
    {
        if (!float.IsFinite(value))
        {
            return "the value must be a finite number";
        }

        if (value < Min || value > Max)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{ServerRates.Show(value)} is out of range; {Name} must be between {ServerRates.Show(Min)} and {ServerRates.Show(Max)}");
        }

        return null;
    }
}

/// <summary>The state of one rate as <c>.rates show</c> prints it.</summary>
public readonly record struct RateStatus(RateDefinition Rate, bool Available, float Current, float Configured, bool Overridden);

/// <summary>Why <see cref="ServerRates.TrySet"/> refused.</summary>
public enum RateSetResult
{
    Ok,
    NotRunning,
    Invalid,
}

/// <summary>
/// The server rates a GM can change live (<c>.rates</c>), one instance per world. A rate is a number on an option object the consumers read at
/// each use (the same objects <c>.reload config</c> and the feature binders fill), so a change applies at once; nothing is cached here except
/// the overrides: for each overridden rate the value it had before the first <c>.rates set</c> (the configured value), which
/// <c>.rates reset</c> and <c>.reload config</c> put back. A rate with several targets (<c>Rate.Drop.Money</c> scales creature loot and quest
/// money, as one vmangos key does) is set on all of them and shows the first one.
/// <para>
/// Thread affinity: world thread only (commands, the reload commit); no locking. Every change is logged at Information with old and new value,
/// actor and reason, and published through <see cref="RateChanged"/> in one place, so an announcer can subscribe without the commands knowing it.
/// </para>
/// </summary>
public sealed class ServerRates
{
    private static readonly ConditionalWeakTable<WorldRuntime, ServerRates> s_perWorld = new();

    /// <summary>How many changes <see cref="History"/> keeps; the oldest are dropped.</summary>
    public const int HistoryCapacity = 100;

    private readonly Dictionary<RateDefinition, Override> _baselines = [];

    private readonly List<RateHistoryEntry> _history = [];

    private sealed record Override(float Display, Action[] Restores);

    /// <summary>
    /// A copy of the newest changes (set, reset, reload) of this world, oldest first, at most <see cref="HistoryCapacity"/>. The list is
    /// guarded by a lock, so a change from a map's thread and a read cannot meet.
    /// </summary>
    public IReadOnlyList<RateHistoryEntry> History
    {
        get
        {
            lock (_history)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>Raised after every change (set, reset, reload) with the old and new value. A handler that throws is logged and skipped.</summary>
    public event Action<RateChange>? RateChanged;

    /// <summary>Every rate, in display order.</summary>
    public static IReadOnlyList<RateDefinition> All => RateCatalog.All;

    /// <summary>The rates of <paramref name="world"/>, created on first use.</summary>
    public static ServerRates For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_perWorld.GetValue(world, static _ => new ServerRates());
    }

    /// <summary>A value as shown in replies and logs (invariant, at most four decimals).</summary>
    public static string Show(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Finds a rate by name, alias or name without the <c>Rate.</c> prefix; case-insensitive.</summary>
    public static RateDefinition? Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string wanted = text.Trim();
        foreach (RateDefinition rate in All)
        {
            if (rate.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || (rate.Name.StartsWith("Rate.", StringComparison.OrdinalIgnoreCase)
                    && rate.Name.AsSpan(5).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                || rate.Aliases.Any(a => a.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return rate;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="rate"/> is overridden now.</summary>
    public bool IsOverridden(RateDefinition rate) => _baselines.ContainsKey(rate);

    /// <summary>How many rates are overridden.</summary>
    public int OverrideCount => _baselines.Count;

    /// <summary>The state of <paramref name="rate"/>; <see cref="RateStatus.Available"/> is false when its feature is not running.</summary>
    public RateStatus Describe(RateScope scope, RateDefinition rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        IReadOnlyList<RateTarget> targets = rate.Resolve(scope);
        if (targets.Count == 0)
        {
            return new RateStatus(rate, false, 0f, 0f, false);
        }

        float current = targets[0].Get();
        return _baselines.TryGetValue(rate, out Override? saved)
            ? new RateStatus(rate, true, current, saved.Display, true)
            : new RateStatus(rate, true, current, current, false);
    }

    /// <summary>
    /// Sets <paramref name="rate"/> on every target. The first set of a rate remembers the value it had (the configured one); later sets keep it.
    /// <paramref name="error"/> says why a refusal happened.
    /// </summary>
    public RateSetResult TrySet(RateScope scope, RateDefinition rate, float value, string actor, string? reason, out string? error)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(actor);
        error = rate.Validate(value);
        if (error is not null)
        {
            return RateSetResult.Invalid;
        }

        IReadOnlyList<RateTarget> targets = rate.Resolve(scope);
        if (targets.Count == 0)
        {
            error = $"{rate.Name} is not available: the feature that owns it is not running";
            return RateSetResult.NotRunning;
        }

        error = rate.CrossCheck?.Invoke(scope, value);
        if (error is not null)
        {
            return RateSetResult.Invalid;
        }

        float old = targets[0].Get();
        if (!_baselines.ContainsKey(rate))
        {
            _baselines[rate] = new Override(old, [.. targets.Select(t => t.Capture())]);
        }

        foreach (RateTarget target in targets)
        {
            target.Set(value);
        }

        rate.Applied?.Invoke(scope);
        Publish(scope, new RateChange(rate, old, value, RateChangeKind.Set, actor, reason));
        return RateSetResult.Ok;
    }

    /// <summary>
    /// Puts <paramref name="rate"/> back to its configured value. False when it was not overridden, its feature is gone, or the restore would
    /// cross a paired rate (Rate.XP.Personal.Min above Max): reset the other side first (<see cref="TryReset"/> says which).
    /// </summary>
    public bool Reset(RateScope scope, RateDefinition rate, string actor, RateChangeKind kind = RateChangeKind.Reset)
        => TryReset(scope, rate, actor, kind, out _);

    /// <summary>As <see cref="Reset"/>; <paramref name="error"/> is set when the restore was refused because it would cross a paired rate. <paramref name="force"/> skips that check (the last pass of a reset-all).</summary>
    public bool TryReset(RateScope scope, RateDefinition rate, string actor, RateChangeKind kind, out string? error, bool force = false)
        => ResetCore(scope, rate, actor, kind, enforceCrossCheck: !force, out error);

    private bool ResetCore(RateScope scope, RateDefinition rate, string actor, RateChangeKind kind, bool enforceCrossCheck, out string? error)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(actor);
        error = null;
        if (!_baselines.TryGetValue(rate, out Override? saved))
        {
            return false;
        }

        // Resolve first: if the feature is gone the saved value is kept rather than dropped unapplied.
        IReadOnlyList<RateTarget> targets = rate.Resolve(scope);
        if (targets.Count == 0)
        {
            return false;
        }

        // The configured value was valid against the configured partner, not necessarily against the partner's current override.
        if (enforceCrossCheck && rate.CrossCheck?.Invoke(scope, saved.Display) is { } crossed)
        {
            error = $"{crossed}; reset the paired rate first";
            return false;
        }

        _baselines.Remove(rate);
        float old = targets[0].Get();
        foreach (Action restore in saved.Restores)
        {
            restore();
        }

        rate.Applied?.Invoke(scope);
        Publish(scope, new RateChange(rate, old, saved.Display, kind, actor, null));
        return true;
    }

    /// <summary>
    /// Resets every overridden rate; returns how many were reset. A restore that would cross a paired rate waits until the pair has been
    /// reset; a rate still blocked when no other reset can unblock it (an invalid configured pair) is restored regardless.
    /// </summary>
    public int ResetAll(RateScope scope, string actor, RateChangeKind kind = RateChangeKind.Reset)
    {
        int count = 0;
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (RateDefinition rate in _baselines.Keys.ToArray())
            {
                if (ResetCore(scope, rate, actor, kind, enforceCrossCheck: true, out _))
                {
                    count++;
                    progressed = true;
                }
            }
        }

        foreach (RateDefinition rate in _baselines.Keys.ToArray())
        {
            if (ResetCore(scope, rate, actor, kind, enforceCrossCheck: false, out _))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The overridden rates and the values they hold now, to put back after a rolled-back reload.</summary>
    public IReadOnlyList<(RateDefinition Rate, float Value)> SnapshotOverrides(RateScope scope)
    {
        var result = new List<(RateDefinition, float)>();
        foreach (RateDefinition rate in _baselines.Keys)
        {
            IReadOnlyList<RateTarget> targets = rate.Resolve(scope);
            if (targets.Count > 0)
            {
                result.Add((rate, targets[0].Get()));
            }
        }

        return result;
    }

    private void Publish(RateScope scope, RateChange change)
    {
        lock (_history)
        {
            _history.Add(new RateHistoryEntry(DateTimeOffset.UtcNow, change));
            if (_history.Count > HistoryCapacity)
            {
                _history.RemoveAt(0);
            }
        }

        scope.Logger.LogInformation(
            "Rate {Rate} {Kind}: {Old} -> {New} by {Actor}{Reason}",
            change.Rate.Name, change.Kind, Show(change.Old), Show(change.New), change.Actor,
            string.IsNullOrWhiteSpace(change.Reason) ? string.Empty : $" (reason: {change.Reason})");
        Action<RateChange>? handlers = RateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<RateChange> handler in handlers.GetInvocationList().Cast<Action<RateChange>>())
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                scope.Logger.LogWarning(ex, "A RateChanged handler failed for {Rate}", change.Rate.Name);
            }
        }
    }
}
