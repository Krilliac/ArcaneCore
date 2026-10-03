using System.Collections;
using System.Globalization;
using System.Reflection;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>A configuration section and the options class bound from it.</summary>
internal sealed record ConfigSection(string Path, Type Type);

/// <summary>One documented configuration key.</summary>
internal sealed record ConfigEntry(string Section, string Path, string TypeText, string Default, string Meaning, string Reload);

/// <summary>The catalog and every reason it could not be built cleanly (an empty list means complete).</summary>
internal sealed record ConfigCatalogResult(IReadOnlyList<ConfigEntry> Entries, IReadOnlyList<string> Problems);

/// <summary>
/// Builds the configuration catalog by walking the options classes: public properties the binder can set (get-only
/// collections and complex properties too, because the binder fills them), recursing into complex properties. The
/// default of every key is read from a freshly constructed instance, so a default in the page cannot drift from the code.
/// </summary>
internal static class ConfigCatalog
{
    private const int MaxDepth = 6;

    public static ConfigCatalogResult Build(
        IEnumerable<ConfigSection> sections,
        Func<Type, string, string?> sourceSummary,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> skippedProperties,
        Func<string, string?> reload)
    {
        var entries = new List<ConfigEntry>();
        var problems = new List<string>();
        foreach (ConfigSection section in sections)
        {
            object? instance = TryCreate(section.Type, section.Path, problems);
            if (instance is not null)
            {
                Walk(section.Type, instance, section.Path, section.Path, 0, sourceSummary, overrides, skippedProperties, reload, entries, problems);
            }
        }

        foreach (IGrouping<string, ConfigEntry> duplicate in entries.GroupBy(e => e.Path, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{duplicate.Key}: more than one options type maps to this key path");
        }

        return new ConfigCatalogResult(
            [.. entries.OrderBy(e => e.Section, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal)],
            problems);
    }

    private static object? TryCreate(Type type, string path, List<string> problems)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            problems.Add($"{path}: {type.Name} has no public parameterless constructor, so its defaults cannot be read; document it in the exception table");
            return null;
        }

        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException)
        {
            problems.Add($"{path}: constructing {type.Name} threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
            return null;
        }
    }

    private static void Walk(
        Type type,
        object instance,
        string prefix,
        string section,
        int depth,
        Func<Type, string, string?> sourceSummary,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> skipped,
        Func<string, string?> reload,
        List<ConfigEntry> entries,
        List<string> problems)
    {
        if (depth > MaxDepth)
        {
            problems.Add($"{prefix}: nesting deeper than {MaxDepth} levels (a cycle?)");
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true)
            {
                continue;
            }

            string path = prefix + ":" + property.Name;
            if (skipped.ContainsKey(type.Name + "." + property.Name))
            {
                continue;
            }

            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            bool settable = property.SetMethod?.IsPublic == true;
            if (IsLeaf(propertyType))
            {
                if (!settable && !IsCollection(propertyType))
                {
                    continue;
                }

                object? value = property.GetValue(instance);
                string meaning = MeaningOf(property, path, sourceSummary, overrides, problems);
                if (propertyType.IsEnum)
                {
                    meaning += (meaning.Length > 0 ? " " : string.Empty) + "Values: " + string.Join(", ", Enum.GetNames(propertyType).Select(n => "`" + n + "`")) + ".";
                }

                entries.Add(new ConfigEntry(section, path, TypeText(property.PropertyType), FormatValue(value), meaning, reload(path) ?? "-"));
                continue;
            }

            object? child = property.GetValue(instance) ?? TryCreate(propertyType, path, problems);
            if (child is not null)
            {
                Walk(propertyType, child, path, section, depth + 1, sourceSummary, overrides, skipped, reload, entries, problems);
            }
        }
    }

    private static string MeaningOf(PropertyInfo property, string path, Func<Type, string, string?> sourceSummary, IReadOnlyDictionary<string, string> overrides, List<string> problems)
    {
        Type declaring = property.DeclaringType ?? property.ReflectedType!;
        string? text = sourceSummary(declaring, property.Name);
        if (string.IsNullOrWhiteSpace(text) && overrides.TryGetValue(declaring.Name + "." + property.Name, out string? over))
        {
            text = over;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add($"{path}: no /// summary on {declaring.Name}.{property.Name} and no ConfigDocOverrides entry (add one under \"{declaring.Name}.{property.Name}\")");
            return string.Empty;
        }

        return text.Trim();
    }

    private static bool IsCollection(Type type) => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || IsCollection(type)
        || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan) || type == typeof(DateTime)
        || type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(Uri);

    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short", [typeof(ushort)] = "ushort",
        [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float",
        [typeof(double)] = "double", [typeof(decimal)] = "decimal", [typeof(string)] = "string", [typeof(char)] = "char",
    };

    public static string TypeText(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return TypeText(underlying) + "?";
        }

        if (Aliases.TryGetValue(type, out string? alias))
        {
            return alias;
        }

        if (type.IsArray)
        {
            return TypeText(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeText)) + ">";
        }

        return type.Name;
    }

    public static string FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return "\"" + text + "\"";
            case bool flag:
                return flag ? "true" : "false";
            case Enum e:
                return e.ToString();
            case TimeSpan span:
                return span.ToString("c", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case IDictionary dictionary:
                {
                    var pairs = new List<string>();
                    foreach (DictionaryEntry pair in dictionary)
                    {
                        pairs.Add(FormatValue(pair.Key) + ": " + FormatValue(pair.Value));
                    }

                    pairs.Sort(StringComparer.Ordinal);
                    return "{" + string.Join(", ", pairs) + "}";
                }

            case IEnumerable sequence:
                {
                    var items = new List<string>();
                    foreach (object? item in sequence)
                    {
                        items.Add(FormatValue(item));
                    }

                    return "[" + string.Join(", ", items) + "]";
                }

            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? "null";
        }
    }
}
