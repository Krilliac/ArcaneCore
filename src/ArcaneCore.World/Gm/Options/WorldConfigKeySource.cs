using System.Collections.Concurrent;
using System.Globalization;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>
/// The reload keys as option keys: every <see cref="WorldConfigKeys"/> entry (the World, Social, Locomotion and World:Playerbots keys),
/// keeping its Read/Apply/Check delegates, so no option is declared twice. A key whose value is a list or a table is left out. All run on
/// the world thread.
/// </summary>
internal static class WorldConfigKeySource
{
    private static WorldConfigView ViewOf(OptionScope scope)
        => new(
            scope.World.Options,
            scope.Services.GetService<IOptions<WorldOptions>>()?.Value,
            scope.Services.GetService<SocialFeature>()?.Options,
            scope.Services.GetService<IOptions<PlayerbotOptions>>()?.Value,
            LocomotionEnvironment.RegisteredOptions(scope.World));

    internal static void Add(OptionScope scope, List<OptionKey> keys, ref int skipped)
    {
        WorldConfigView probe = ViewOf(scope);
        foreach (WorldConfigKey wck in WorldConfigKeys.All)
        {
            object? native = wck.Read(probe);
            if (native is null)
            {
                continue; // the owning side is not running
            }

            if (!TryCanonical(native, out object seed, out OptionType type, out IReadOnlyList<string> choices))
            {
                skipped++;
                continue;
            }

            Type clr = native.GetType();
            WorldConfigKey key = wck;
            string[] segments = key.Path.Split(':');
            string group = segments.Length >= 3 ? segments[1] : segments[0];
            var aliases = new List<string>();
            if (key.Path.StartsWith(WorldOptions.SectionName + ":", StringComparison.Ordinal))
            {
                aliases.Add(key.Path[(WorldOptions.SectionName.Length + 1)..]);
            }

            if (LegacyOptionSources.MovementAlias(key.Path) is { } movement)
            {
                aliases.Add(movement);
            }

            Func<OptionScope, object?> get = s => key.Read(ViewOf(s)) is { } v && TryCanonical(v, out object canonical, out _, out _) ? canonical : null;
            Func<OptionScope, object, OptionActor, string?>? set = key.Live ? (s, value, _) => ApplyReloadKey(s, key, clr, value) : null;
            // The bounds are what the key's own Check accepts, found by probing it once (they are shown, not enforced: the Check refuses
            // with its own message).
            (double min, double max) = type is OptionType.Integer or OptionType.Number
                ? s_bounds.GetOrAdd(key.Path, _ => ProbeBounds(key.Check, clr, type, Convert.ToDouble(seed, CultureInfo.InvariantCulture)))
                : (double.NaN, double.NaN);
            keys.Add(new OptionKey(
                key.Path, DescribeReloadKey(key.Path), type, key.Live ? OptionApplyClass.Live : OptionApplyClass.Restart, group, get, set,
                min, max, choices: choices, aliases: aliases, informationalBounds: true));
        }
    }

    internal const string NoDescription = "(no description)";

    private static readonly ConcurrentDictionary<string, string> s_descriptions = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, (double Min, double Max)> s_bounds = new(StringComparer.Ordinal);

    /// <summary>The first sentence of the key's entry in the configuration catalog (the text the generated reference prints), without markup.</summary>
    internal static string DescribeReloadKey(string path) => s_descriptions.GetOrAdd(path, Describe);

    private static string Describe(string path)
    {
        string meaning = ConfCatalog.Embedded.Find(path)?.Meaning.Replace("`", string.Empty, StringComparison.Ordinal).Trim() ?? string.Empty;
        if (meaning.Length == 0)
        {
            return NoDescription;
        }

        int end = meaning.IndexOf(". ", StringComparison.Ordinal);
        string sentence = end > 0 ? meaning[..(end + 1)] : meaning;
        return sentence.Length > 160 ? sentence[..157] + "..." : sentence;
    }

    private static string? ApplyReloadKey(OptionScope scope, WorldConfigKey key, Type clr, object canonical)
    {
        if (!TryNative(clr, canonical, out object native, out string error))
        {
            return error;
        }

        if (key.Check?.Invoke(native) is { } problem)
        {
            return $"{key.Path} {problem}";
        }

        WorldConfigView view = ViewOf(scope);
        object? previous = key.Read(view);
        key.Apply!(view, native);
        if (view.Playerbots is { } playerbots && key.Path.StartsWith(PlayerbotOptions.SectionName + ":", StringComparison.Ordinal))
        {
            // The rules that tie two values together (UnfriendAffinity not above FriendAffinity...) are checked on the whole section, as
            // .reload config does (ConfigCandidate.Validate); the change is taken back when the section no longer holds together.
            try
            {
                playerbots.Social.Lists.Validate();
            }
            catch (InvalidOperationException ex)
            {
                key.Apply(view, previous);
                return ex.Message;
            }
        }

        if (WorldConfigKeys.IsSpeedRate(key) && view.Locomotion is { } locomotion)
        {
            SpeedRates.ApplyToAll(scope.World, locomotion); // the players' copies are re-sent, as .movement set does
        }

        return null;
    }

    /// <summary>The canonical form of a reload key's native value; false for a structured value (a list, a table) or one that does not fit.</summary>
    private static bool TryCanonical(object native, out object canonical, out OptionType type, out IReadOnlyList<string> choices)
    {
        choices = [];
        canonical = native;
        type = OptionType.Text;
        switch (native)
        {
            case bool:
                type = OptionType.Bool;
                return true;
            case sbyte or byte or short or ushort or int or uint or long:
                canonical = Convert.ToInt64(native, CultureInfo.InvariantCulture);
                type = OptionType.Integer;
                return true;
            case ulong big when big <= long.MaxValue:
                canonical = (long)big;
                type = OptionType.Integer;
                return true;
            case float f when float.IsFinite(f):
                canonical = double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                type = OptionType.Number;
                return true;
            case double d when double.IsFinite(d):
                type = OptionType.Number;
                return true;
            case string:
                return true;
            case Enum e when !e.GetType().IsDefined(typeof(FlagsAttribute), inherit: false):
                canonical = e.ToString();
                type = OptionType.Choice;
                choices = Enum.GetNames(e.GetType());
                return true;
            default:
                return false;
        }
    }

    /// <summary>The native value of type <paramref name="clr"/> for a canonical one; false, with the reason, when it does not fit the type.</summary>
    private static bool TryNative(Type clr, object canonical, out object native, out string error)
    {
        native = canonical;
        error = string.Empty;
        try
        {
            if (clr == typeof(bool) || clr == typeof(string) || clr == typeof(double))
            {
                return true;
            }

            if (clr.IsEnum)
            {
                native = Enum.Parse(clr, (string)canonical, ignoreCase: true);
                return true;
            }

            if (clr == typeof(float))
            {
                float narrowed = (float)(double)canonical;
                if (!float.IsFinite(narrowed))
                {
                    error = $"{OptionKey.Number((double)canonical)} does not fit a single-precision number";
                    return false;
                }

                native = narrowed;
                return true;
            }

            native = Convert.ChangeType(canonical, clr, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            error = $"{canonical} is out of range for a {clr.Name}";
            return false;
        }
        catch (ArgumentException)
        {
            error = $"{canonical} is not valid for this option";
            return false;
        }
    }

    // --- the bounds --------------------------------------------------------------------------------------------------

    /// <summary>The widest value the probe looks at; an edge not found before it (or before the type's own limit) counts as no bound.</summary>
    private const double SearchLimit = 1e12;

    /// <summary>
    /// The lowest and highest number the <paramref name="check"/> of a reload key accepts (NaN for a side with no bound), found by bisecting
    /// from <paramref name="seed"/>, the key's current value, which the check accepted. The result is an interval only when the check accepts
    /// the values sampled inside it; a check that is not a range (even numbers only, say) gives no bounds. A check is pure, so this runs once per key.
    /// </summary>
    internal static (double Min, double Max) ProbeBounds(Func<object?, string?>? check, Type clr, OptionType type, double seed)
    {
        (double Min, double Max) none = (double.NaN, double.NaN);
        if (check is null || !TypeLimits(clr, out double typeLow, out double typeHigh))
        {
            return none;
        }

        bool whole = type == OptionType.Integer;
        double low = Math.Max(typeLow, -SearchLimit);
        double high = Math.Min(typeHigh, SearchLimit);

        bool Accepts(double x)
        {
            if (!(x >= typeLow && x <= typeHigh))
            {
                return false;
            }

            object canonical = whole ? (long)x : x;
            if (!TryNative(clr, canonical, out object native, out _))
            {
                return false;
            }

            try
            {
                return check(native) is null;
            }
            catch (Exception)
            {
                return false; // a check that cannot judge this value does not accept it
            }
        }

        double Snap(double edge)
        {
            for (int digits = 0; digits <= 9; digits++)
            {
                double candidate = Math.Round(edge, digits);
                if (Math.Abs(candidate - edge) <= 1e-6 * Math.Max(1, Math.Abs(edge)) && Accepts(candidate))
                {
                    return candidate;
                }
            }

            return edge;
        }

        double Edge(double limit)
        {
            if (Accepts(limit))
            {
                return double.NaN;
            }

            double ok = seed;
            double bad = limit;
            for (int i = 0; i < 200; i++)
            {
                double gap = Math.Abs(bad - ok);
                if (whole ? gap <= 1 : gap <= 1e-9 * Math.Max(1, Math.Abs(ok)))
                {
                    break;
                }

                double mid = whole ? Math.Floor((ok + bad) / 2) : (ok + bad) / 2;
                if (Accepts(mid))
                {
                    ok = mid;
                }
                else
                {
                    bad = mid;
                }
            }

            return whole ? ok : Snap(ok);
        }

        if (!Accepts(seed))
        {
            return none;
        }

        double min = Edge(low);
        double max = Edge(high);
        double from = double.IsNaN(min) ? Math.Max(low, seed - 1000) : min;
        double to = double.IsNaN(max) ? Math.Min(high, seed + 1000) : max;
        for (int step = 1; step <= 9; step++)
        {
            double sample = from + ((to - from) * step / 10);
            if (!Accepts(whole ? Math.Round(sample) : sample))
            {
                return none;
            }
        }

        return (min, max);
    }

    /// <summary>The range of the numeric type <paramref name="clr"/>, kept inside what a long holds; false for any other type.</summary>
    private static bool TypeLimits(Type clr, out double low, out double high)
    {
        (low, high) = Type.GetTypeCode(clr) switch
        {
            TypeCode.SByte => ((double)sbyte.MinValue, (double)sbyte.MaxValue),
            TypeCode.Byte => (0d, (double)byte.MaxValue),
            TypeCode.Int16 => ((double)short.MinValue, (double)short.MaxValue),
            TypeCode.UInt16 => (0d, (double)ushort.MaxValue),
            TypeCode.Int32 => ((double)int.MinValue, (double)int.MaxValue),
            TypeCode.UInt32 => (0d, (double)uint.MaxValue),
            TypeCode.Int64 => (-9e18, 9e18),
            TypeCode.UInt64 => (0d, 9e18),
            TypeCode.Single => (-(double)float.MaxValue, (double)float.MaxValue),
            TypeCode.Double => (-double.MaxValue, double.MaxValue),
            _ => (double.NaN, double.NaN),
        };
        return !double.IsNaN(low);
    }
}
