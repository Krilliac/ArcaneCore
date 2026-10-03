using System.Globalization;
using System.Reflection;
using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// What the mapper had to adjust: values outside the target type's range are clamped, and each
/// clamp is counted and (for the first few) described, so an import report can say so instead of
/// silently writing a different number.
/// </summary>
public sealed class MapDiagnostics
{
    private readonly Dictionary<string, (int Count, string First, string Target)> _columns = new(StringComparer.Ordinal);

    /// <summary>Values clamped in total.</summary>
    public int Clamped { get; private set; }

    /// <summary>One line per clamped column: how many values were clamped and the first one.</summary>
    public IReadOnlyList<string> Samples => [.. _columns.Select(c =>
        $"{c.Key}: {c.Value.Count} value(s) do not fit {c.Value.Target} and were clamped (first: {c.Value.First})")];

    internal void Clamp(string table, string column, string raw, string target)
    {
        Clamped++;
        string key = table + "." + column;
        _columns[key] = _columns.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.First, target) : (1, raw, target);
    }
}

/// <summary>
/// Maps a dump row onto a row class by name. A source column is matched to a public settable
/// property of <typeparamref name="T"/> when their names are equal ignoring case and
/// underscores (<c>stat_type1</c> = <c>StatType1</c> = <c>stattype1</c>), or through an explicit
/// alias for the few names that really differ between a source and the row. Columns without a
/// property are ignored (<see cref="Maps"/> tells which); properties without a column keep their
/// default. Values convert by the property's type; integers outside its range clamp (counted in
/// <see cref="MapDiagnostics"/>), text that is not a number is an <see cref="ImportSchemaException"/>.
/// </summary>
public sealed class RowMapper<T>
    where T : class, new()
{
    private static readonly PropertyInfo[] s_properties = [.. typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && Converter.Supports(p.PropertyType))];

    private readonly Dictionary<string, PropertyInfo> _byName;
    private readonly HashSet<PropertyInfo> _wrap;
    // The column list and the plan built for it are published together as ONE immutable reference:
    // the importers share a static mapper across threads, and two fields would let a reader pair one
    // caller's plan with another caller's columns.
    private sealed record ColumnPlan(IReadOnlyList<string> Columns, (int Index, string Column, PropertyInfo Property)[] Entries);

    private volatile ColumnPlan? _current;

    /// <param name="aliases">Source column name (any case, underscores ignored) to property name.</param>
    /// <param name="signedAsUnsigned">Names of unsigned 32-bit properties whose source column is signed and holds negative values meaning two's-complement bits (a server that stores them in a uint32 reads -1 as 0xFFFFFFFF): they wrap instead of clamping.</param>
    public RowMapper(IReadOnlyDictionary<string, string>? aliases = null, IEnumerable<string>? signedAsUnsigned = null)
    {
        _byName = s_properties.ToDictionary(p => Normalize(p.Name), StringComparer.Ordinal);
        _wrap = new HashSet<PropertyInfo>(signedAsUnsigned?.Select(name => s_properties.FirstOrDefault(p => p.Name == name && p.PropertyType == typeof(uint))
            ?? throw new ArgumentException($"{typeof(T).Name} has no settable uint property '{name}'", nameof(signedAsUnsigned))) ?? []);
        if (aliases is null)
        {
            return;
        }

        foreach ((string column, string property) in aliases)
        {
            PropertyInfo target = s_properties.FirstOrDefault(p => p.Name == property)
                ?? throw new ArgumentException($"{typeof(T).Name} has no settable property '{property}' for the alias '{column}'", nameof(aliases));
            _byName[Normalize(column)] = target;
        }
    }

    /// <summary>The comparison form of a column or property name: lower case, no underscores.</summary>
    public static string Normalize(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>Whether <paramref name="column"/> is read into a property.</summary>
    public bool Maps(string column) => _byName.ContainsKey(Normalize(column));

    public T Map(DumpRow row, MapDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ColumnPlan? current = _current;
        if (current is null || !ReferenceEquals(current.Columns, row.Columns))
        {
            current = Plan(row.Columns);
        }

        var result = new T();
        foreach ((int index, string column, PropertyInfo property) in current.Entries)
        {
            if (index >= row.Values.Count)
            {
                continue;
            }

            string? raw = row.Values[index];
            if (raw is null)
            {
                continue;
            }

            property.SetValue(result, Converter.Convert(row.Table, column, raw, property.PropertyType, diagnostics, _wrap.Contains(property)));
        }

        return result;
    }

    private ColumnPlan Plan(IReadOnlyList<string> columns)
    {
        var plan = new List<(int, string, PropertyInfo)>();
        var taken = new HashSet<PropertyInfo>();
        for (int i = 0; i < columns.Count; i++)
        {
            if (_byName.TryGetValue(Normalize(columns[i]), out PropertyInfo? property) && taken.Add(property))
            {
                plan.Add((i, columns[i], property));
            }
        }

        var built = new ColumnPlan(columns, [.. plan]);
        _current = built;
        return built;
    }

    private static class Converter
    {
        private static readonly Dictionary<Type, (decimal Min, decimal Max)> s_integers = new()
        {
            [typeof(byte)] = (byte.MinValue, byte.MaxValue),
            [typeof(sbyte)] = (sbyte.MinValue, sbyte.MaxValue),
            [typeof(short)] = (short.MinValue, short.MaxValue),
            [typeof(ushort)] = (ushort.MinValue, ushort.MaxValue),
            [typeof(int)] = (int.MinValue, int.MaxValue),
            [typeof(uint)] = (uint.MinValue, uint.MaxValue),
            [typeof(long)] = (long.MinValue, long.MaxValue),
            [typeof(ulong)] = (ulong.MinValue, ulong.MaxValue),
        };

        public static bool Supports(Type type)
            => type == typeof(string) || type == typeof(bool) || type == typeof(float) || type == typeof(double) || s_integers.ContainsKey(type);

        public static object Convert(string table, string column, string raw, Type type, MapDiagnostics? diagnostics, bool wrapNegative)
        {
            if (type == typeof(string))
            {
                return raw;
            }

            if (type == typeof(bool))
            {
                return raw is not ("0" or "" or "false" or "False");
            }

            if (type == typeof(float) || type == typeof(double))
            {
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                {
                    throw Invalid(table, column, raw, type);
                }

                return type == typeof(float) ? (object)(float)number : number;
            }

            (decimal min, decimal max) = s_integers[type];
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            {
                throw Invalid(table, column, raw, type);
            }

            value = decimal.Truncate(value);
            if (wrapNegative && value is < 0 and >= int.MinValue)
            {
                return unchecked((uint)(int)value);
            }

            if (value < min || value > max)
            {
                diagnostics?.Clamp(table, column, raw, type.Name);
                value = value < min ? min : max;
            }

            return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        private static ImportSchemaException Invalid(string table, string column, string raw, Type type)
            => new(table, column, $"table `{table}`, column `{column}`: value '{raw}' is not a valid {type.Name}");
    }
}
