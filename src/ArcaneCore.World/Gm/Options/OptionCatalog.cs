using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>The state of one option as <c>.server config</c> prints it.</summary>
public readonly record struct OptionStatus(OptionKey Key, bool Available, object? Current, object? Baseline, bool Overridden, string? Actor, string? Reason);

/// <summary>Why <see cref="OptionCatalog.TrySet"/> refused or what it did.</summary>
public enum OptionSetResult
{
    Ok,
    ReadOnly,
    NotAvailable,
    Invalid,
}

/// <summary>
/// The one registry of server options: the reload keys (<see cref="Reload.WorldConfigKeys"/>), the live rates
/// (<see cref="Rates.RateCatalog"/>), the anticheat and packet-heuristics settings, and whatever packs register through
/// <see cref="IOptionSource"/>. Built per use from the running services (<see cref="Create"/>), so it only lists what is running.
/// A key is Live, NextLoad or Restart; a Restart key is read-only. A change is validated (type, bounds, the key's own cross-check), applied
/// through the owner's own path, and remembered with its baseline in <see cref="OptionOverrides"/> so <c>diff</c> and <c>reset</c> work.
/// Thread affinity: world thread only.
/// </summary>
public sealed class OptionCatalog
{
    private readonly Dictionary<string, OptionKey> _byName;

    private OptionCatalog(IReadOnlyList<OptionKey> keys, int skipped, List<string> conflicts)
    {
        Keys = keys;
        Skipped = skipped;
        Conflicts = conflicts;
        _byName = new Dictionary<string, OptionKey>(StringComparer.OrdinalIgnoreCase);
        foreach (OptionKey key in keys)
        {
            _byName[key.Path] = key;
        }

        foreach (OptionKey key in keys)
        {
            foreach (string alias in key.Aliases)
            {
                if (!_byName.TryAdd(alias, key) && !ReferenceEquals(_byName[alias], key))
                {
                    conflicts.Add(alias);
                }
            }
        }
    }

    /// <summary>Every key, in display order (reload keys, rates, anticheat, packet heuristics, pack keys).</summary>
    public IReadOnlyList<OptionKey> Keys { get; }

    /// <summary>How many reload keys hold a structured value (a list, a table) that cannot be typed in one line; they are left out.</summary>
    public int Skipped { get; }

    /// <summary>Paths and aliases that two keys claim (the first key kept; a path wins over an alias), for the tests and the startup log.</summary>
    public IReadOnlyList<string> Conflicts { get; }

    /// <summary>Builds the catalog for what is running now.</summary>
    public static OptionCatalog Create(OptionScope scope)
    {
        IReadOnlyList<OptionKey> raw = OptionSources.Collect(scope, out int skipped);
        var keys = new List<OptionKey>(raw.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        foreach (OptionKey key in raw)
        {
            if (seen.Add(key.Path))
            {
                keys.Add(key);
            }
            else
            {
                conflicts.Add(key.Path);
            }
        }

        return new OptionCatalog(keys, skipped, conflicts);
    }

    /// <summary>Finds a key by exact path or alias (case-insensitive); null when none.</summary>
    public OptionKey? Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _byName.GetValueOrDefault(text.Trim());
    }

    /// <summary>The keys whose path, alias or description contains <paramref name="filter"/> (all when empty).</summary>
    public IEnumerable<OptionKey> Match(string filter)
    {
        string f = filter.Trim();
        return f.Length == 0
            ? Keys
            : Keys.Where(k => k.Path.Contains(f, StringComparison.OrdinalIgnoreCase)
                || k.Aliases.Any(a => a.Contains(f, StringComparison.OrdinalIgnoreCase))
                || k.Description.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The state of <paramref name="key"/>.</summary>
    public OptionStatus Describe(OptionScope scope, OptionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        object? current = key.Get(scope);
        if (current is null)
        {
            return new OptionStatus(key, false, null, null, false, null, null);
        }

        if (key.Tracking is { } tracking)
        {
            (bool overridden, object? baseline) = tracking.State(scope);
            return new OptionStatus(key, true, current, overridden ? baseline : current, overridden, null, null);
        }

        // An entry whose value is no longer the key's (a reload or another path put another one in) is stale: it is ignored here, because a
        // read must not change the store, and dropped by the next write (TrySet).
        if (OptionOverrides.For(scope.World).Find(key.Path) is { } entry && Equals(entry.Value, current))
        {
            return new OptionStatus(key, true, current, entry.Baseline, true, entry.Actor, entry.Reason);
        }

        return new OptionStatus(key, true, current, current, false, null, null);
    }

    /// <summary>The keys overridden now, in display order.</summary>
    public IReadOnlyList<OptionStatus> Diff(OptionScope scope)
        => [.. Keys.Select(k => Describe(scope, k)).Where(s => s.Available && s.Overridden)];

    /// <summary>
    /// Parses <paramref name="text"/> for <paramref name="key"/>, validates it, applies it through the owner and remembers the baseline.
    /// <paramref name="before"/> and <paramref name="after"/> are the shown values on success.
    /// </summary>
    public OptionSetResult TrySet(OptionScope scope, OptionKey key, string text, OptionActor actor, out string error, out string before, out string after)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);
        before = after = string.Empty;
        if (key.Apply == OptionApplyClass.Restart)
        {
            error = $"{key.Path} is read from the configuration at start; edit the configuration file and restart";
            return OptionSetResult.ReadOnly;
        }

        if (key.Secret)
        {
            // The command line is in the GM audit and the log as typed, so a secret value must never travel through it.
            error = $"{key.Path} is a secret and is not written through a command; edit the configuration file";
            return OptionSetResult.ReadOnly;
        }

        if (key.Set is null)
        {
            error = $"{key.Path} cannot be changed here; edit the configuration file and .reload config";
            return OptionSetResult.ReadOnly;
        }

        if (!key.TryParse(text, out object value, out string parseError))
        {
            error = parseError;
            return OptionSetResult.Invalid;
        }

        object? current = key.Get(scope);
        if (current is null)
        {
            error = $"{key.Path} is not available: the feature that owns it is not running";
            return OptionSetResult.NotAvailable;
        }

        string? refused = key.Set(scope, value, actor);
        if (refused is not null)
        {
            error = refused;
            return OptionSetResult.Invalid;
        }

        object? now = key.Get(scope) ?? value;
        if (key.Tracking is null)
        {
            OptionOverrides store = OptionOverrides.For(scope.World);
            if (store.Find(key.Path) is { } entry && !Equals(entry.Value, current))
            {
                store.Forget(key.Path); // stale: the baseline it holds is not the one this change started from
            }

            store.Remember(key.Path, current, now, actor.Name, actor.Reason);

            // A key that tracks its own override (the rates) logs its changes itself.
            scope.Logger.LogInformation(
                "Option {Option} {Old} -> {New} by {Actor}{Reason} ({Apply})",
                key.Path, key.Format(current), key.Format(now), actor.Name, string.IsNullOrWhiteSpace(actor.Reason) ? string.Empty : $" (reason: {actor.Reason})",
                OptionKey.Show(key.Apply));
        }

        error = string.Empty;
        before = key.Format(current);
        after = key.Format(now);
        return OptionSetResult.Ok;
    }

    /// <summary>Puts <paramref name="key"/> back to its baseline. False when it was not overridden (or its owner is gone).</summary>
    public bool Reset(OptionScope scope, OptionKey key, string actor)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Tracking is { } tracking)
        {
            return tracking.Reset(scope, actor);
        }

        OptionStatus status = Describe(scope, key);
        if (!status.Available || !status.Overridden || status.Baseline is null || key.Set is null)
        {
            return false;
        }

        if (key.Set(scope, status.Baseline, new OptionActor(actor, "reset")) is not null)
        {
            return false;
        }

        OptionOverrides.For(scope.World).Forget(key.Path);
        scope.Logger.LogInformation("Option {Option} reset {Old} -> {New} by {Actor}", key.Path, key.Format(status.Current), key.Format(status.Baseline), actor);
        return true;
    }

    /// <summary>Resets every overridden key; returns how many went back.</summary>
    public int ResetAll(OptionScope scope, string actor)
    {
        int count = 0;
        foreach (OptionStatus status in Diff(scope))
        {
            if (Reset(scope, status.Key, actor))
            {
                count++;
            }
        }

        return count;
    }
}
