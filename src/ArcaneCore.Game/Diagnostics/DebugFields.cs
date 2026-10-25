using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ArcaneCore.Game.Diagnostics;

/// <summary>
/// A game object that can describe its own state for a GM debug command (<c>.inspect</c>, <c>.debug</c>). Implemented in a
/// <c>&lt;Type&gt;.Gm.&lt;Topic&gt;.cs</c> partial file next to the type, so the description reads the type's own private state and
/// there is no reflection over fields. <see cref="Describe"/> runs on the thread that owns the object (the map thread for
/// map objects) and only reads: it must not mutate, hold a lock for long or allocate beyond the writer.
/// </summary>
public interface IDebugDescribable
{
    /// <summary>Write this object's fields to <paramref name="writer"/>, in the order they should be shown.</summary>
    void Describe(DebugWriter writer);
}

/// <summary>One described value: its section ("" for the top level), name and invariant-culture text.</summary>
public readonly record struct DebugField(string Section, string Name, string Value);

/// <summary>
/// Collects the fields a <see cref="IDebugDescribable"/> reports, grouped by section, and renders them as chat lines or JSON.
/// Values are formatted with the invariant culture so output is stable across hosts. The field count is capped
/// (<see cref="MaxFields"/>): a describe that runs away (a big collection written element by element) stops growing and
/// <see cref="Truncated"/> says so. Not thread-safe; one writer per describe call.
/// </summary>
public sealed class DebugWriter
{
    /// <summary>Default cap on the number of fields one writer keeps.</summary>
    public const int DefaultMaxFields = 256;

    /// <summary>How deep <see cref="AddObject"/> may nest; deeper (or cyclic) objects are skipped and <see cref="Truncated"/> is set.</summary>
    public const int MaxDepth = 8;

    private readonly List<DebugField> _fields = [];
    private string _section = string.Empty;
    private int _depth;

    public DebugWriter(int maxFields = DefaultMaxFields)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFields, 0);
        MaxFields = maxFields;
    }

    /// <summary>The cap on kept fields.</summary>
    public int MaxFields { get; }

    /// <summary>True when at least one field was dropped because <see cref="MaxFields"/> was reached.</summary>
    public bool Truncated { get; private set; }

    /// <summary>The kept fields, in the order they were written.</summary>
    public IReadOnlyList<DebugField> Fields => _fields;

    /// <summary>Fields written after this call belong to <paramref name="name"/> until the next section ("" returns to the top level).</summary>
    public DebugWriter Section(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _section = name;
        return this;
    }

    public DebugWriter Add(string name, string? value) => Append(name, value ?? "-");

    public DebugWriter Add(string name, bool value) => Append(name, value ? "true" : "false");

    public DebugWriter Add(string name, int value) => Append(name, value.ToString(CultureInfo.InvariantCulture));

    public DebugWriter Add(string name, uint value) => Append(name, value.ToString(CultureInfo.InvariantCulture));

    public DebugWriter Add(string name, long value) => Append(name, value.ToString(CultureInfo.InvariantCulture));

    public DebugWriter Add(string name, ulong value) => Append(name, value.ToString(CultureInfo.InvariantCulture));

    public DebugWriter Add(string name, float value) => Append(name, value.ToString("0.###", CultureInfo.InvariantCulture));

    public DebugWriter Add(string name, double value) => Append(name, value.ToString("0.###", CultureInfo.InvariantCulture));

    public DebugWriter Add(string name, ObjectGuid value) => Append(name, value.ToString());

    public DebugWriter Add(string name, TimeSpan value) => Append(name, value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms");

    /// <summary>An enum value, by its name.</summary>
    public DebugWriter AddEnum<T>(string name, T value)
        where T : struct, Enum
        => Append(name, value.ToString());

    /// <summary>A position, as <c>x y z</c>.</summary>
    public DebugWriter AddPosition(string name, float x, float y, float z)
        => Append(name, string.Create(CultureInfo.InvariantCulture, $"{x:0.##} {y:0.##} {z:0.##}"));

    /// <summary>A collection, as its count plus at most <paramref name="maxItems"/> comma-separated items.</summary>
    public DebugWriter AddList<T>(string name, IReadOnlyCollection<T> items, int maxItems = 8)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        sb.Append(items.Count.ToString(CultureInfo.InvariantCulture));
        if (items.Count > 0 && maxItems > 0)
        {
            sb.Append(" [");
            int shown = 0;
            foreach (T item in items)
            {
                if (shown == maxItems)
                {
                    sb.Append(", ...");
                    break;
                }

                if (shown > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Convert.ToString(item, CultureInfo.InvariantCulture));
                shown++;
            }

            sb.Append(']');
        }

        return Append(name, sb.ToString());
    }

    /// <summary>Describe a nested object under its own section named <paramref name="section"/>, then return to the current one.</summary>
    public DebugWriter AddObject(string section, IDebugDescribable? value)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (_depth >= MaxDepth)
        {
            Truncated = true;
            return this;
        }

        _depth++;
        string previous = _section;
        _section = section;
        try
        {
            if (value is null)
            {
                Append("value", "-");
            }
            else
            {
                value.Describe(this);
            }
        }
        finally
        {
            _depth--;
            _section = previous;
        }

        return this;
    }

    /// <summary>The fields as chat lines: <c>name: value</c>, with a <c>[section]</c> header line whenever the section changes, plus a truncation note.</summary>
    public IReadOnlyList<string> ToLines()
    {
        var lines = new List<string>(_fields.Count + 2);
        string current = string.Empty;
        foreach (DebugField field in _fields)
        {
            if (!string.Equals(field.Section, current, StringComparison.Ordinal))
            {
                current = field.Section;
                // A top-level field after a section gets an empty header, so it does not read as part of that section.
                lines.Add(current.Length > 0 ? "[" + current + "]" : "[]");
            }

            lines.Add(field.Name + ": " + field.Value);
        }

        if (Truncated)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"(output capped at {MaxFields} fields)"));
        }

        return lines;
    }

    /// <summary>The fields as one JSON object: top-level fields as properties, each section as a nested object, and <c>truncated</c> when capped.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            var used = new HashSet<string>(StringComparer.Ordinal) { "truncated" }; // reserved, so a field of that name is renamed
            foreach (DebugField field in _fields.Where(f => f.Section.Length == 0))
            {
                json.WriteString(UniqueName(used, field.Name), field.Value);
            }

            foreach (IGrouping<string, DebugField> section in _fields.Where(f => f.Section.Length > 0).GroupBy(f => f.Section, StringComparer.Ordinal))
            {
                json.WriteStartObject(UniqueName(used, section.Key));
                var usedInSection = new HashSet<string>(StringComparer.Ordinal);
                foreach (DebugField field in section)
                {
                    json.WriteString(UniqueName(usedInSection, field.Name), field.Value);
                }

                json.WriteEndObject();
            }

            if (Truncated)
            {
                json.WriteBoolean("truncated", true);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // JSON objects must not repeat a property name: the second use of a name becomes name_2, then name_3.
    private static string UniqueName(HashSet<string> used, string name)
    {
        string candidate = name;
        for (int n = 2; !used.Add(candidate); n++)
        {
            candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private DebugWriter Append(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_fields.Count >= MaxFields)
        {
            Truncated = true;
            return this;
        }

        _fields.Add(new DebugField(_section, name, value));
        return this;
    }
}

/// <summary>Describes any <see cref="IDebugDescribable"/> into a fresh writer.</summary>
public static class DebugFields
{
    /// <summary>The fields <paramref name="value"/> reports.</summary>
    public static DebugWriter Snapshot(IDebugDescribable value, int maxFields = DebugWriter.DefaultMaxFields)
    {
        ArgumentNullException.ThrowIfNull(value);
        var writer = new DebugWriter(maxFields);
        value.Describe(writer);
        return writer;
    }
}
