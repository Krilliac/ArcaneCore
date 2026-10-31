using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration.Conf;
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
/// <summary>
/// An override the owner tracks itself. <paramref name="Reset"/>(scope, actor, force) says whether the key went back and, when the owner refused
/// (a restore that would cross a paired value), why; <c>force</c> skips that refusal for the last pass of a reset-all.
/// </summary>
internal sealed record OptionTracking(Func<OptionScope, (bool Overridden, object? Baseline)> State, Func<OptionScope, string, bool, (bool Done, string? Refusal)> Reset);

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
        IReadOnlyList<string>? choices = null, IReadOnlyList<string>? aliases = null, OptionTracking? tracking = null, string? configPath = null, bool informationalBounds = false)
    {
        ConfigPath = configPath ?? path;
        BoundsEnforced = !informationalBounds;
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

    /// <summary>
    /// False when <see cref="Min"/> and <see cref="Max"/> are only shown (the reload keys, whose own Check enforces them and refuses with
    /// its own message); true when <see cref="TryParse"/> refuses a value outside them.
    /// </summary>
    public bool BoundsEnforced { get; }

    /// <summary>True when the value is never printed (a password, token or connection string).</summary>
    public bool Secret { get; }

    /// <summary>The accepted names of a <see cref="OptionType.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>Other names that select the key (matched case-insensitively).</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Whether <c>.server config set</c> may change it: not a restart-only key, not a secret (the typed command line is audited), and it has a writer.</summary>
    public bool Writable => Apply != OptionApplyClass.Restart && Set is not null && !Secret;

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
        OptionType.Integer or OptionType.Number when !double.IsNaN(Min) => $">= {Number(Min)}",
        OptionType.Integer or OptionType.Number when !double.IsNaN(Max) => $"<= {Number(Max)}",
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
        if (BoundsEnforced && ((!double.IsNaN(Min) && asDouble < Min) || (!double.IsNaN(Max) && asDouble > Max)))
        {
            error = $"{Number(asDouble)} is out of range; {Path} must be between {Number(Min)} and {Number(Max)}";
            return false;
        }

        return true;
    }

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
