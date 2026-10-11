using System.Collections;
using System.Globalization;
using System.Reflection;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>A configuration section and the options class bound from it.</summary>
internal sealed record ConfigSection(string Path, Type Type);

/// <summary>
/// One documented configuration key. <see cref="Default"/> is the display text of the reference page; the remaining members feed the
/// .conf catalog: which daemon(s) read it, whether it is a scalar, a sequence or a map, the scalar type of its value(s), the enum
/// names, the default in .conf form (<see cref="RawDefault"/>, null = unset) and the default entries of a sequence or map.
/// </summary>
internal sealed record ConfigEntry(
    string Section,
    string Path,
    string TypeText,
    string Default,
    string Meaning,
    string Reload,
    ConfigDaemon Daemons = ConfigDaemon.World,
    string Kind = ConfKey.Scalar,
    string ElementType = "string",
    IReadOnlyList<string>? EnumValues = null,
    string? RawDefault = null,
    IReadOnlyList<ConfDefaultEntry>? DefaultEntries = null,
    string? AllowedOverride = null);

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
                Walk(section.Type, instance, section.Path, section.Path, 0, section.Type.GetCustomAttribute<ConfigDaemonsAttribute>()?.Daemons ?? ConfigDaemon.World, sourceSummary, overrides, skippedProperties, reload, entries, problems);
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
        ConfigDaemon daemons,
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
            ConfigDaemon propertyDaemons = property.GetCustomAttribute<ConfigDaemonsAttribute>(inherit: false)?.Daemons ?? daemons;
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

                ConfigEntry leaf = Leaf(section, path, property.PropertyType, propertyType, value, meaning, reload(path) ?? "-", propertyDaemons, problems);
                // An option's real range (ConfigAllowedAttribute) replaces the type's own in the "Allowed" line.
                if (property.GetCustomAttribute<ArcaneCore.Kernel.Configuration.ConfigAllowedAttribute>(inherit: false) is { } allowed)
                    leaf = leaf with { AllowedOverride = allowed.Allowed };
                entries.Add(leaf);
                continue;
            }

            object? child = property.GetValue(instance) ?? TryCreate(propertyType, path, problems);
            if (child is not null)
            {
                Walk(propertyType, child, path, section, depth + 1, propertyDaemons, sourceSummary, overrides, skipped, reload, entries, problems);
            }
        }
    }

    private static ConfigEntry Leaf(string section, string path, Type declaredType, Type propertyType, object? value, string meaning, string reload, ConfigDaemon daemons, List<string> problems)
    {
        string display = FormatValue(value);
        string typeText = TypeText(declaredType);
        if (!IsCollection(propertyType))
        {
            string[]? names = propertyType.IsEnum ? Enum.GetNames(propertyType) : null;
            string? raw = value is null ? null : RawText(value);
            return new ConfigEntry(section, path, typeText, display, meaning, reload, daemons, ConfKey.Scalar, typeText, names, raw, []);
        }

        bool isMap = typeof(IDictionary).IsAssignableFrom(propertyType)
            || propertyType.GetInterfaces().Append(propertyType).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        Type? element = ElementTypeOf(propertyType, isMap);
        Type? scalarElement = element is null ? null : Nullable.GetUnderlyingType(element) ?? element;
        bool scalar = scalarElement is not null && IsLeaf(scalarElement) && !IsCollection(scalarElement);
        string elementType = scalar ? TypeText(element!) : "object";
        IReadOnlyList<string>? elementNames = scalar && scalarElement!.IsEnum ? Enum.GetNames(scalarElement) : null;
        var defaults = new List<ConfDefaultEntry>();
        if (value is not null)
        {
            Flatten(value, string.Empty, 0, defaults, path, problems);
        }

        return new ConfigEntry(section, path, typeText, display, meaning, reload, daemons, isMap ? ConfKey.Map : ConfKey.Sequence, elementType, elementNames, null, defaults);
    }

    private static Type? ElementTypeOf(Type type, bool isMap)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        foreach (Type candidate in type.GetInterfaces().Append(type).Where(t => t.IsGenericType))
        {
            Type definition = candidate.GetGenericTypeDefinition();
            if (isMap && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)))
            {
                return candidate.GetGenericArguments()[1];
            }

            if (!isMap && definition == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>Flattens a default sequence or map into .conf entries: a sequence element is its index, a map element its key, a nested member is joined with ':'.</summary>
    private static void Flatten(object value, string prefix, int depth, List<ConfDefaultEntry> entries, string path, List<string> problems)
    {
        if (depth > MaxDepth)
        {
            problems.Add($"{path}: default value nested deeper than {MaxDepth} levels");
            return;
        }

        string Join(string name) => prefix.Length == 0 ? name : prefix + ":" + name;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry pair in dictionary)
            {
                FlattenMember(pair.Value, Join(Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? string.Empty), depth, entries, path, problems);
            }

            return;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            int index = 0;
            foreach (object? item in sequence)
            {
                FlattenMember(item, Join(index.ToString(CultureInfo.InvariantCulture)), depth, entries, path, problems);
                index++;
            }

            return;
        }

        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true)
            {
                FlattenMember(property.GetValue(value), Join(property.Name), depth, entries, path, problems);
            }
        }
    }

    private static void FlattenMember(object? member, string key, int depth, List<ConfDefaultEntry> entries, string path, List<string> problems)
    {
        if (member is null)
        {
            return;
        }

        Type type = member.GetType();
        if (IsLeaf(type) && !IsCollection(type))
        {
            entries.Add(new ConfDefaultEntry(key, RawText(member), ConfValue.IsTextType(TypeText(type), type.IsEnum ? Enum.GetNames(type) : null)));
            return;
        }

        Flatten(member, key, depth + 1, entries, path, problems);
    }

    /// <summary>A scalar in the form a .conf file (and the configuration binder) reads: invariant culture, booleans in lower case, round-trip floats.</summary>
    private static string RawText(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

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
