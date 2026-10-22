using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Validation;

/// <summary>
/// Shared building blocks of the <see cref="IConfigCheck"/> implementers. The text forms (<see cref="ReadBool"/>,
/// <see cref="ReadRange"/>) parse a raw configuration string; the typed forms (<see cref="Range"/>, <see cref="NonNegative"/>)
/// check a value that is already bound. Each check keeps its own wording: the fix text is a parameter, never merged.
/// </summary>
internal static class ConfigChecks
{
    public static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);

    public static ConfigIssue Warn(string key, string problem, string fix) => new(ConfigSeverity.Warning, key, problem, fix);

    /// <summary>Reports <c>'text' is not a boolean.</c> when the key is set and is not <c>true</c> or <c>false</c>.</summary>
    public static void ReadBool(List<ConfigIssue> issues, IConfiguration configuration, string key)
    {
        string? text = configuration[key];
        if (!string.IsNullOrWhiteSpace(text) && !bool.TryParse(text, out _))
        {
            issues.Add(Error(key, $"'{text}' is not a boolean.", "use true or false"));
        }
    }

    /// <summary>
    /// The parsed value when the key is set and valid, null when unset, null after reporting an error. <paramref name="fix"/>
    /// defaults to <c>use min..max</c>.
    /// </summary>
    public static int? ReadRange(List<ConfigIssue> issues, IConfiguration configuration, string key, int min, int max, string? fix = null)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            issues.Add(Error(key, $"'{text}' is not a whole number.", fix ?? $"use {min}..{max}"));
            return null;
        }

        if (value < min || value > max)
        {
            issues.Add(Error(key, $"{value} is outside {min}..{max}.", fix ?? $"use {min}..{max}"));
            return null;
        }

        return value;
    }

    /// <summary>
    /// The parsed boolean, or <paramref name="fallback"/> when <paramref name="text"/> is blank; a non-blank text that is not
    /// <c>true</c> or <c>false</c> is reported and also yields <paramref name="fallback"/>.
    /// </summary>
    public static bool BoolOr(List<ConfigIssue> issues, string key, string? text, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (bool.TryParse(text, out bool value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{text}' is not true or false.", "use true or false"));
        return fallback;
    }

    /// <summary>As <see cref="BoolOr"/> for an <see cref="int"/>; the fix text of a non-numeric value is the caller's.</summary>
    public static int IntOr(List<ConfigIssue> issues, string key, string? text, int fallback, string fix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{text}' is not a whole number.", fix));
        return fallback;
    }

    /// <summary>As <see cref="IntOr"/> for a <see cref="long"/>.</summary>
    public static long LongOr(List<ConfigIssue> issues, string key, string? text, long fallback, string fix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{text}' is not a whole number.", fix));
        return fallback;
    }

    /// <summary>As <see cref="BoolOr"/> for an enum name (case-insensitive, a defined member); the fix lists the member names.</summary>
    public static T EnumOr<T>(List<ConfigIssue> issues, string key, string? text, T fallback)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value))
        {
            return value;
        }

        issues.Add(Error(key, $"'{text}' is not one of the allowed values.", "use " + string.Join(", ", Enum.GetNames<T>())));
        return fallback;
    }

    /// <summary>Reports <c>value is out of range.</c> with the fix <c>use {expected}</c>.</summary>
    public static void Range(List<ConfigIssue> issues, string key, long value, long min, long max, string expected)
    {
        if (value < min || value > max)
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {expected}"));
        }
    }

    public static void NonNegative(List<ConfigIssue> issues, string key, long value)
    {
        if (value < 0)
        {
            issues.Add(Error(key, $"{value} is negative.", "use 0 to disable, or a positive value"));
        }
    }
}
