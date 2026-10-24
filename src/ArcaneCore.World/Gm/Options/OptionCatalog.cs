using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.World.OptionKeys;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>The value kind of an <see cref="OptionKey"/>; it decides how a typed text is parsed and shown.</summary>
public enum OptionType
{
    /// <summary><c>true</c>/<c>false</c> (also on/off, yes/no, 1/0 when typing).</summary>
    Bool,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A decimal number (finite).</summary>
    Number,

    /// <summary>Free text.</summary>
    Text,

    /// <summary>One of a fixed list of names (an enum).</summary>
    Choice,
}

/// <summary>What one option operation resolves its owners from (world thread).</summary>
public readonly record struct OptionScope(IServiceProvider Services, WorldRuntime World, ILogger Logger);

/// <summary>Who changes an option and why; the reason is free text after the value of <c>.server config set</c>.</summary>
public readonly record struct OptionActor(string Name, string? Reason);

/// <summary>How a key that tracks its own override (the rates, whose baseline <see cref="Rates.ServerRates"/> keeps) answers the catalog.</summary>
internal sealed record OptionTracking(Func<OptionScope, (bool Overridden, object? Baseline)> State, Func<OptionScope, string, bool> Reset);

/// <summary>
/// One option the server can show and (unless it needs a restart) change: its path, a typed value with bounds, the apply class and the
/// delegates that read and write it. Keys are built bound to a scope by <see cref="OptionCatalog.Create"/> (the value types of the reload
/// keys are known only from their live value); a key is cheap and short-lived. Values travel as canonical objects: bool, long, double or
/// string (a choice is its name). Thread affinity: every delegate runs on the world thread.
/// </summary>
public sealed class OptionKey
{
    internal OptionKey(
        string path, string description, OptionType type, OptionApplyClass apply, string group, Func<OptionScope, object?> get,
        Func<OptionScope, object, OptionActor, string?>? set, double min = double.NaN, double max = double.NaN, bool secret = false,
        IReadOnlyList<string>? choices = null, IReadOnlyList<string>? aliases = null, OptionTracking? tracking = null, string? configPath = null)
    {
        ConfigPath = configPath ?? path;
        Path = path;
        Description = description;
        Type = type;
        Apply = apply;
        Group = group;
        Get = get;
        Set = set;
        Min = min;
        Max = max;
        Secret = secret || ConfSecrets.IsSecretPath(path);
        Choices = choices ?? [];
        Aliases = aliases ?? [];
        Tracking = tracking;
    }

    /// <summary>The configuration path (<c>World:Maps:GridUnload</c>) or the vmangos name (<c>Rate.XP.Kill</c>).</summary>
    public string Path { get; }

    /// <summary>
    /// The path the configuration providers know the option by (<c>Progression:RateXpKill</c> for <c>Rate.XP.Kill</c>); the same as
    /// <see cref="Path"/> except for a rate. Empty when no configuration key is known for the option.
    /// </summary>
    public string ConfigPath { get; }

    /// <summary>What the option does.</summary>
    public string Description { get; }

    /// <summary>The value kind.</summary>
    public OptionType Type { get; }

    /// <summary>When a change takes effect.</summary>
    public OptionApplyClass Apply { get; }

    /// <summary>The owner shown in listings (<c>World</c>, <c>Rates</c>, <c>AntiCheat</c>, <c>Pack</c>).</summary>
    public string Group { get; }

    /// <summary>Lowest accepted value of a number, NaN when only the type bounds it.</summary>
    public double Min { get; }

    /// <summary>Highest accepted value of a number, NaN when only the type bounds it.</summary>
    public double Max { get; }

    /// <summary>True when the value is never printed (a password, token or connection string).</summary>
    public bool Secret { get; }

    /// <summary>The accepted names of a <see cref="OptionType.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>Other names that select the key (matched case-insensitively).</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Whether <c>.server config set</c> may change it: not a restart-only key, and it has a writer.</summary>
    public bool Writable => Apply != OptionApplyClass.Restart && Set is not null;

    internal Func<OptionScope, object?> Get { get; }

    internal Func<OptionScope, object, OptionActor, string?>? Set { get; }

    internal OptionTracking? Tracking { get; }

    /// <summary>The apply class as typed in replies.</summary>
    public static string Show(OptionApplyClass apply) => apply switch
    {
        OptionApplyClass.Live => "live",
        OptionApplyClass.NextLoad => "next-load",
        _ => "restart",
    };

    /// <summary>The bounds as shown, empty when the key has none.</summary>
    public string BoundsText => Type switch
    {
        OptionType.Integer or OptionType.Number when !double.IsNaN(Min) && !double.IsNaN(Max) => $"{Number(Min)}..{Number(Max)}",
        OptionType.Choice => string.Join('|', Choices),
        OptionType.Bool => "true|false",
        _ => string.Empty,
    };

    /// <summary>A value as shown: invariant, shortest round-trip number, secrets masked.</summary>
    public string Format(object? value)
    {
        if (value is null)
        {
            return "(not available)";
        }

        if (Secret && value is not bool)
        {
            return value is string { Length: 0 } ? "(empty)" : ConfSecrets.Placeholder;
        }

        string text = value switch
        {
            bool b => b ? "true" : "false",
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => Number(d),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        string flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length > 120 ? flat[..117] + "..." : flat;
    }

    /// <summary>Parses typed <paramref name="text"/> into the canonical value; <paramref name="error"/> says what is wrong. Range checks included.</summary>
    public bool TryParse(string text, out object value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        string t = text.Trim();
        switch (Type)
        {
            case OptionType.Bool:
                switch (t.ToLowerInvariant())
                {
                    case "1" or "on" or "true" or "yes":
                        value = true;
                        return true;
                    case "0" or "off" or "false" or "no":
                        value = false;
                        return true;
                    default:
                        error = $"'{t}' is not a boolean (true|false|on|off)";
                        return false;
                }

            case OptionType.Integer:
                if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
                {
                    error = $"'{t}' is not a whole number";
                    return false;
                }

                value = whole;
                break;
            case OptionType.Number:
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                {
                    error = $"'{t}' is not a finite number";
                    return false;
                }

                value = number;
                break;
            case OptionType.Choice:
                string? match = Choices.FirstOrDefault(c => c.Equals(t, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    error = $"'{t}' is not one of {string.Join('|', Choices)}";
                    return false;
                }

                value = match;
                return true;
            default:
                value = text.Trim();
                return true;
        }

        double asDouble = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if ((!double.IsNaN(Min) && asDouble < Min) || (!double.IsNaN(Max) && asDouble > Max))
        {
            error = $"{Number(asDouble)} is out of range; {Path} must be between {Number(Min)} and {Number(Max)}";
            return false;
        }

        return true;
    }

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

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
/// The overrides made through <c>.server config</c> (and the alias commands that route into it) for one world: for each changed key the
/// value it had before the first change (the baseline <c>reset</c> puts back), the value set, who set it and why. A key that tracks its
/// own override (the rates) is not stored here. <c>.reload config</c> drops the entries of the keys it puts back (<see cref="EndsAtReload"/>); an
/// entry whose key no longer holds the value that was set (another path changed it) is dropped when next looked at. Thread affinity: world thread only; no locking.
/// </summary>
public sealed class OptionOverrides
{
    private static readonly ConditionalWeakTable<WorldRuntime, OptionOverrides> s_perWorld = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record Entry(object Baseline, object Value, string Actor, string? Reason);

    /// <summary>The overrides of <paramref name="world"/>, created on first use.</summary>
    public static OptionOverrides For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_perWorld.GetValue(world, static _ => new OptionOverrides());
    }

    /// <summary>How many keys are overridden (stale entries may still be counted until looked at).</summary>
    public int Count => _entries.Count;

    internal Entry? Find(string path) => _entries.GetValueOrDefault(path);

    internal IEnumerable<string> Paths => _entries.Keys.ToArray();

    internal void Remember(string path, object before, object after, string actor, string? reason)
    {
        object baseline = _entries.TryGetValue(path, out Entry? existing) ? existing.Baseline : before;
        if (Equals(baseline, after))
        {
            _entries.Remove(path);
            return;
        }

        _entries[path] = new Entry(baseline, after, actor, reason);
    }

    internal bool Forget(string path) => _entries.Remove(path);

    private static readonly Lazy<HashSet<string>> s_reloadKeys = new(() => new HashSet<string>(Reload.WorldConfigKeys.All.Select(k => k.Path), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// True when <c>.reload config</c> puts the file's value back into the key at <paramref name="path"/> (the reload keys and the AntiCheat
    /// section), so an override of it ends at the reload. The PacketHeuristics keys and the pack keys are not part of the reload and keep
    /// theirs until restart.
    /// </summary>
    public static bool EndsAtReload(string path)
        => s_reloadKeys.Value.Contains(path) || path.StartsWith(Game.AntiCheat.AntiCheatOptions.SectionName + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops the entries of the keys a reload puts back (<see cref="EndsAtReload"/>); returns them so a rolled back reload can restore them.</summary>
    internal IReadOnlyList<KeyValuePair<string, Entry>> ForgetReloaded()
    {
        KeyValuePair<string, Entry>[] reloaded = [.. _entries.Where(e => EndsAtReload(e.Key))];
        foreach (KeyValuePair<string, Entry> entry in reloaded)
        {
            _entries.Remove(entry.Key);
        }

        return reloaded;
    }

    internal void Restore(IEnumerable<KeyValuePair<string, Entry>> entries)
    {
        foreach (KeyValuePair<string, Entry> entry in entries)
        {
            _entries[entry.Key] = entry.Value;
        }
    }
}

/// <summary>
/// The one registry of server options: the reload keys (<see cref="Reload.WorldConfigKeys"/>), the live rates
/// (<see cref="Rates.RateCatalog"/>), the anticheat and packet-heuristics settings, and whatever packs register through
/// <see cref="IOptionKeyProvider"/>. Built per use from the running services (<see cref="Create"/>), so it only lists what is running.
/// A key is Live, NextLoad or Restart; a Restart key is read-only. A change is validated (type, bounds, the key's own cross-check), applied
/// through the owner's own path, and remembered with its baseline in <see cref="OptionOverrides"/> so <c>diff</c> and <c>reset</c> work.
/// Thread affinity: world thread only.
/// </summary>
public sealed class OptionCatalog
{
    private readonly Dictionary<string, OptionKey> _byName;

    private OptionCatalog(IReadOnlyList<OptionKey> keys, int skipped, IReadOnlyList<string> conflicts)
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
                _byName.TryAdd(alias, key);
            }
        }
    }

    /// <summary>Every key, in display order (reload keys, rates, anticheat, packet heuristics, pack keys).</summary>
    public IReadOnlyList<OptionKey> Keys { get; }

    /// <summary>How many reload keys hold a structured value (a list, a table) that cannot be typed in one line; they are left out.</summary>
    public int Skipped { get; }

    /// <summary>Path or alias collisions between sources (the first key kept), for the tests and the startup log.</summary>
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

        OptionOverrides store = OptionOverrides.For(scope.World);
        if (store.Find(key.Path) is { } entry)
        {
            if (Equals(entry.Value, current))
            {
                return new OptionStatus(key, true, current, entry.Baseline, true, entry.Actor, entry.Reason);
            }

            store.Forget(key.Path); // a reload (or another path) put another value in
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
            OptionOverrides.For(scope.World).Remember(key.Path, current, now, actor.Name, actor.Reason);
        }

        scope.Logger.LogInformation(
            "Option {Option} {Old} -> {New} by {Actor}{Reason} ({Apply})",
            key.Path, key.Format(current), key.Format(now), actor.Name, string.IsNullOrWhiteSpace(actor.Reason) ? string.Empty : $" (reason: {actor.Reason})",
            OptionKey.Show(key.Apply));
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

    /// <summary>
    /// Runs <paramref name="mutate"/> (an alias command's own change, such as <c>.anticheat set</c>) and remembers it as an override of the key
    /// at <paramref name="path"/>, so <c>diff</c> and <c>reset</c> see it. When the key is unknown or unavailable the change still runs.
    /// </summary>
    public static void TrackExternal(OptionScope scope, string path, string actor, Action mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        OptionKey? key = Create(scope).Find(path);
        object? before = key?.Get(scope);
        mutate();
        if (key is null || before is null || key.Tracking is not null)
        {
            return;
        }

        if (key.Get(scope) is { } after)
        {
            OptionOverrides.For(scope.World).Remember(key.Path, before, after, actor, null);
        }
    }
}
