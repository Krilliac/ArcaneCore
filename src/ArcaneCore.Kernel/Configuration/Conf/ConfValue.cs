using System.Globalization;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// Value rules of the .conf files: quoting and per-type validation with the invariant culture, the way the
/// configuration binder reads a value (<c>bool</c> is <c>true</c> or <c>false</c>, numbers use '.' as the decimal point, enums are names).
/// Stateless; safe from any thread.
/// </summary>
public static class ConfValue
{
    /// <summary>Writes <paramref name="value"/> in double quotes; only <c>"</c> and <c>\</c> are escaped. A line break cannot be written and throws <see cref="FormatException"/>.</summary>
    public static string Quote(string value)
    {
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new FormatException("a value with a line break cannot be written to a .conf file");
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>True for types whose value is free text, which the writer always quotes.</summary>
    public static bool IsTextType(string typeText, IReadOnlyList<string>? enumValues)
        => enumValues is null && StripNullable(typeText) is not (
            "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
            or "float" or "double" or "decimal" or "TimeSpan" or "DateTime" or "DateTimeOffset");

    /// <summary>Returns null when <paramref name="value"/> is a legal value of the type, otherwise what is wrong with it. An empty value is "not set" and is legal.</summary>
    public static string? Validate(string value, string typeText, IReadOnlyList<string>? enumValues)
    {
        if (value.Length == 0)
        {
            return null;
        }

        string type = StripNullable(typeText);
        if (enumValues is not null)
        {
            foreach (string part in value.Split(','))
            {
                string name = part.Trim();
                bool known = enumValues.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                if (!known)
                {
                    return $"'{name}' is not one of {string.Join(" | ", enumValues)}.";
                }
            }

            return null;
        }

        switch (type)
        {
            case "bool":
                return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"'{value}' is not a boolean; write true or false (1 and 0 are not accepted).";
            case "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong":
                return TryInteger(value, type) ? null : $"'{value}' is not a valid {type} (a whole number{RangeText(type)}).";
            case "float" or "double" or "decimal":
                return TryDecimal(value, type) ? null : $"'{value}' is not a valid {type} (a number with '.' as the decimal point).";
            case "TimeSpan":
                return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _) ? null : $"'{value}' is not a TimeSpan (use [d.]hh:mm:ss).";
            case "DateTime":
                return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : $"'{value}' is not a date and time.";
            case "DateTimeOffset":
                return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : $"'{value}' is not a date, time and offset.";
            case "Guid":
                return Guid.TryParse(value, out _) ? null : $"'{value}' is not a GUID.";
            case "char":
                return value.Length == 1 ? null : $"'{value}' is not a single character.";
            default:
                return null;
        }
    }

    /// <summary>True when the two strings are the same value of the type (case of booleans and enum names, spelling of numbers and time spans).</summary>
    public static bool Equivalent(string a, string b, string typeText, IReadOnlyList<string>? enumValues = null)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        string type = StripNullable(typeText);
        if (enumValues is not null || type == "bool")
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        switch (type)
        {
            case "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal":
                if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal x)
                    && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal y))
                {
                    return x == y;
                }

                return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double dx)
                    && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double dy)
                    && dx == dy;
            case "TimeSpan":
                return TimeSpan.TryParse(a, CultureInfo.InvariantCulture, out TimeSpan s)
                    && TimeSpan.TryParse(b, CultureInfo.InvariantCulture, out TimeSpan t)
                    && s == t;
            default:
                return false;
        }
    }

    /// <summary>The scalar type text without a trailing nullable marker.</summary>
    public static string StripNullable(string typeText) => typeText.EndsWith('?') ? typeText[..^1] : typeText;

    /// <summary>The "Allowed" text of a scalar type for the generated comment blocks.</summary>
    public static string AllowedText(string typeText, IReadOnlyList<string>? enumValues)
    {
        if (enumValues is not null)
        {
            return string.Join(" | ", enumValues);
        }

        string type = StripNullable(typeText);
        return type switch
        {
            "bool" => "true | false",
            "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" => "a whole number" + RangeText(type),
            "float" or "double" or "decimal" => "a number, '.' is the decimal point",
            "TimeSpan" => "a time span, [d.]hh:mm:ss",
            "DateTime" or "DateTimeOffset" => "a date and time, ISO 8601",
            _ => "text",
        };
    }

    private static bool TryInteger(string value, string type)
    {
        string text = value;
        NumberStyles style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.AllowHexSpecifier;
        }

        return type switch
        {
            "byte" => byte.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            "sbyte" => sbyte.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            "short" => short.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            "ushort" => ushort.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            "int" => int.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            "uint" => uint.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            "long" => long.TryParse(text, style, CultureInfo.InvariantCulture, out _),
            _ => ulong.TryParse(text, style, CultureInfo.InvariantCulture, out _),
        };
    }

    private static bool TryDecimal(string value, string type) => type switch
    {
        "float" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        "double" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
    };

    private static string RangeText(string type) => type switch
    {
        "byte" => ", 0 to 255",
        "sbyte" => ", -128 to 127",
        "short" => ", -32768 to 32767",
        "ushort" => ", 0 to 65535",
        "int" => ", -2147483648 to 2147483647",
        "uint" => ", 0 to 4294967295",
        "long" => ", -9223372036854775808 to 9223372036854775807",
        _ => ", 0 to 18446744073709551615",
    };
}
