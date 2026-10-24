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

            if (!TryCanonical(native, out _, out OptionType type, out IReadOnlyList<string> choices))
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
            keys.Add(new OptionKey(
                key.Path, DescribeReloadKey(key.Path), type, key.Live ? OptionApplyClass.Live : OptionApplyClass.Restart, group, get, set,
                choices: choices, aliases: aliases));
        }
    }

    internal const string NoDescription = "(no description)";

    /// <summary>The first sentence of the key's entry in the configuration catalog (the text the generated reference prints), without markup.</summary>
    internal static string DescribeReloadKey(string path)
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

    /// <summary>The <c>.movement set</c> field of a player speed rate key (<c>movement.run</c>), null for any other key.</summary>
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
}
