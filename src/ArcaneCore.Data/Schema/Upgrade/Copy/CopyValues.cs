using System.Globalization;
using System.Numerics;
using System.Text;

namespace ArcaneCore.Data.Schema.Upgrade.Copy;

/// <summary>How a target column stores values, as far as <c>arcane-db copy</c> must convert a source value to fit it.</summary>
internal enum CopyKind
{
    /// <summary>SQLite target (or a type the copy does not know): the value is bound as a primitive (<see cref="CopyValues.ToNative"/>), the driver stores it.</summary>
    Native = 0,
    Integer,
    Boolean,
    Decimal,
    Double,

    /// <summary>A timestamp that carries no zone (MariaDB/MySQL <c>datetime</c>, PostgreSQL <c>timestamp</c>): written with <see cref="DateTimeKind.Unspecified"/>, the UTC clock value.</summary>
    Timestamp,

    /// <summary>PostgreSQL <c>timestamp with time zone</c>: written as <see cref="DateTimeKind.Utc"/>.</summary>
    TimestampTz,
    Date,
    Time,
    Text,
    Binary,
    Guid,
}

/// <summary>
/// The conversions of <c>arcane-db copy</c>: a value read from the source (a SQLite value is a long, double, string or byte[]; a
/// server value is already typed) to the CLR type the target column's type takes, and a canonical text of a converted value for the
/// content check. Pure and stateless, so safe from any thread.
/// </summary>
internal static class CopyValues
{
    private const long TicksPerMicrosecond = 10;

    /// <summary>
    /// The kind of a server column from its catalog type: <paramref name="dataType"/> is information_schema's <c>data_type</c>,
    /// <paramref name="columnType"/> MariaDB's <c>column_type</c> (so <c>tinyint(1)</c>, the boolean Pomelo creates, is found).
    /// </summary>
    public static CopyKind KindOfServerType(string dataType, string? columnType)
    {
        string type = dataType.Trim().ToLowerInvariant();
        if (string.Equals(columnType?.Trim(), "tinyint(1)", StringComparison.OrdinalIgnoreCase))
        {
            return CopyKind.Boolean;
        }

        return type switch
        {
            "boolean" or "bool" => CopyKind.Boolean,
            "smallint" or "integer" or "int" or "bigint" or "tinyint" or "mediumint" or "year" => CopyKind.Integer,
            "numeric" or "decimal" => CopyKind.Decimal,
            "real" or "double precision" or "double" or "float" => CopyKind.Double,
            "timestamp with time zone" => CopyKind.TimestampTz,
            "timestamp without time zone" or "datetime" or "timestamp" => CopyKind.Timestamp,
            "date" => CopyKind.Date,
            "time without time zone" or "time" => CopyKind.Time,
            "uuid" => CopyKind.Guid,
            "bytea" or "blob" or "tinyblob" or "mediumblob" or "longblob" or "binary" or "varbinary" => CopyKind.Binary,
            "text" or "character varying" or "character" or "varchar" or "char" or "tinytext" or "mediumtext" or "longtext" or "json" or "jsonb" => CopyKind.Text,
            _ => CopyKind.Native,
        };
    }

    /// <summary>
    /// <paramref name="value"/> as the CLR type <paramref name="kind"/> takes. <see cref="DBNull"/> and null give <see cref="DBNull.Value"/>.
    /// Idempotent: converting a value read back from the target gives the same type, which the content check relies on.
    /// </summary>
    /// <exception cref="FormatException">The value cannot be read as the kind (for example text that is not a date).</exception>
    public static object Convert(object? value, CopyKind kind)
    {
        if (value is null or DBNull)
        {
            return DBNull.Value;
        }

        try
        {
            return kind switch
            {
                CopyKind.Native => ToNative(value),
                CopyKind.Integer => ToInteger(value),
                CopyKind.Boolean => ToBoolean(value),
                CopyKind.Decimal => ToDecimal(value),
                CopyKind.Double => ToDouble(value),
                CopyKind.Timestamp => ToTimestamp(value, utc: false),
                CopyKind.TimestampTz => ToTimestamp(value, utc: true),
                CopyKind.Date => ToDate(value),
                CopyKind.Time => ToTime(value),
                CopyKind.Text => ToText(value),
                CopyKind.Binary => value as byte[] ?? throw new FormatException($"a {value.GetType().Name} is not binary data"),
                CopyKind.Guid => ToGuid(value),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or ArgumentException)
        {
            throw new FormatException($"{value.GetType().Name} value cannot be converted to {kind}: {ex.Message}", ex);
        }
    }

    /// <summary>The primitive a SQLite column stores for a value another engine returns (the encodings Microsoft.Data.Sqlite and EF Core SQLite use).</summary>
    public static object ToNative(object value) => value switch
    {
        string or long or double or byte[] => value,
        bool b => b ? 1L : 0L,
        sbyte or byte or short or ushort or int or uint => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong u => u <= long.MaxValue ? (long)u : u.ToString(CultureInfo.InvariantCulture),
        float f => (double)f,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan s => s.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(),
        char c => c.ToString(),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static object ToInteger(object value) => value switch
    {
        long or ulong => value,
        bool b => b ? 1L : 0L,
        sbyte or byte or short or ushort or int or uint => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        decimal d when decimal.Truncate(d) == d => d >= long.MinValue && d <= long.MaxValue ? (long)d : (object)(ulong)d,
        double d when Math.Truncate(d) == d && Math.Abs(d) < 9.2e18 => (long)d,
        string s => ParseInteger(s),
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not an integer"),
    };

    private static object ParseInteger(string s)
    {
        string text = s.Trim();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed))
        {
            return signed;
        }

        return ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static object ToBoolean(object value) => value switch
    {
        bool => value,
        string s => s.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "t" => true,
            "0" or "false" or "f" => false,
            _ => throw new InvalidCastException($"'{s}' is not a boolean"),
        },
        sbyte or byte or short or ushort or int or uint or long => System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        ulong u => u != 0,
        decimal d => d != 0,
        double d => d != 0,
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not a boolean"),
    };

    private static object ToDecimal(object value) => value switch
    {
        decimal => value,
        string s => decimal.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        long or ulong or int or uint or short or ushort or byte or sbyte => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        double d => (decimal)d,
        float f => (decimal)f,
        BigInteger b => (decimal)b,
        bool b => b ? 1m : 0m,
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not a decimal"),
    };

    private static object ToDouble(object value) => value switch
    {
        double => value,
        float f => (double)f,
        string s => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        decimal d => (double)d,
        long or ulong or int or uint or short or ushort or byte or sbyte => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not a floating-point number"),
    };

    private static object ToTimestamp(object value, bool utc)
    {
        DateTime instant = value switch
        {
            DateTime t => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t,
            DateTimeOffset o => o.UtcDateTime,
            string s => DateTimeOffset.Parse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime,
            _ => throw new InvalidCastException($"a {value.GetType().Name} is not a timestamp"),
        };

        // PostgreSQL and MariaDB (datetime(6)) keep microseconds; truncating here, on both sides of the content check, keeps them comparable.
        long ticks = instant.Ticks - (instant.Ticks % TicksPerMicrosecond);
        return new DateTime(ticks, utc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
    }

    private static object ToDate(object value) => value switch
    {
        DateOnly => value,
        DateTime t => DateOnly.FromDateTime(t),
        DateTimeOffset o => DateOnly.FromDateTime(o.UtcDateTime),
        string s => DateOnly.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not a date"),
    };

    private static object ToTime(object value) => value switch
    {
        TimeSpan => value,
        TimeOnly t => t.ToTimeSpan(),
        string s => TimeSpan.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not a time"),
    };

    private static object ToText(object value) => value switch
    {
        string => value,
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] => throw new InvalidCastException("binary data is not text"),
        _ => ToNative(value) is string text ? text : System.Convert.ToString(ToNative(value), CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static object ToGuid(object value) => value switch
    {
        Guid => value,
        string s => Guid.Parse(s.Trim()),
        byte[] { Length: 16 } bytes => new Guid(bytes),
        _ => throw new InvalidCastException($"a {value.GetType().Name} is not a GUID"),
    };

    /// <summary>
    /// A text that is equal for equal values whatever engine they came from, for a value already passed through <see cref="Convert"/>:
    /// the content check hashes it. Each part carries a type tag, and a string its length, so no two different rows share a text.
    /// </summary>
    public static void AppendCanonical(StringBuilder text, object value)
    {
        switch (value)
        {
            case DBNull:
                text.Append('N');
                break;
            case string s:
                text.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
                break;
            case bool b:
                text.Append(b ? "b1" : "b0");
                break;
            case long l:
                text.Append('i').Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case ulong u:
                text.Append('i').Append(u.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal d:
                text.Append('d').Append((d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
                break;
            case double f:
                text.Append('f').Append(f.ToString("R", CultureInfo.InvariantCulture));
                break;
            case DateTime t:
                text.Append('t').Append(t.Ticks.ToString(CultureInfo.InvariantCulture));
                break;
            case DateOnly o:
                text.Append('D').Append(o.DayNumber.ToString(CultureInfo.InvariantCulture));
                break;
            case TimeSpan s:
                text.Append('T').Append(s.Ticks.ToString(CultureInfo.InvariantCulture));
                break;
            case Guid g:
                text.Append('g').Append(g.ToString("D", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                text.Append('x').Append(System.Convert.ToHexString(bytes));
                break;
            default:
                // Reached only when the target column type is not one the copy knows (CopyKind.Native on a server): any primitive is read back as itself.
                text.Append('o').Append(System.Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }

        text.Append('\u001f');
    }
}
