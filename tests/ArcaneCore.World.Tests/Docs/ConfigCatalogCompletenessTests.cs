using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Reflection checks that every option the server binds is in the configuration catalog, independent of the catalog generator: they walk the
/// options types themselves and compare with the catalog that ships embedded in the Kernel assembly (the one the daemons check .conf files with
/// and write the .conf.dist files from). A new options type, a new property, or a new list of groups that nobody catalogued fails here with its name.
/// </summary>
internal static class ConfigCompleteness
{
    private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
    private const int MaxDepth = 6;

    /// <summary>
    /// The key path of every property the binder can set (and every collection and nested group) below each section that <paramref name="inCatalog"/>
    /// does not know. A "Type.Property" entry of <paramref name="skipped"/> is a property documented elsewhere or not a key.
    /// </summary>
    public static IReadOnlyList<string> MissingPaths(IEnumerable<ConfigSection> sections, Func<string, bool> inCatalog, IReadOnlyDictionary<string, string> skipped)
    {
        var missing = new List<string>();
        foreach (ConfigSection section in sections)
        {
            CollectMissing(section.Type, section.Path, inCatalog, skipped, missing, 0);
        }

        return [.. missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static void CollectMissing(Type type, string prefix, Func<string, bool> inCatalog, IReadOnlyDictionary<string, string> skipped, List<string> missing, int depth)
    {
        if (depth > MaxDepth)
        {
            missing.Add(prefix + ": nested deeper than " + MaxDepth + " levels (a cycle?)");
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(Public))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true || skipped.ContainsKey(type.Name + "." + property.Name))
            {
                continue;
            }

            string path = prefix + ":" + property.Name;
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (ConfigCatalog.IsLeaf(propertyType))
            {
                bool settable = property.SetMethod?.IsPublic == true;
                if ((settable || ConfigCatalog.IsCollection(propertyType)) && !inCatalog(path))
                {
                    missing.Add(path + " (" + type.Name + "." + property.Name + ")");
                }

                continue;
            }

            CollectMissing(propertyType, path, inCatalog, skipped, missing, depth + 1);
        }
    }

    /// <summary>The list and map options whose entries are groups of keys, by key path, with the entry type.</summary>
    public static IReadOnlyDictionary<string, Type> GroupCollections(IEnumerable<ConfigSection> sections, IReadOnlyDictionary<string, string> skipped)
    {
        var found = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (ConfigSection section in sections)
        {
            CollectGroups(section.Type, section.Path, skipped, found, 0);
        }

        return found;
    }

    private static void CollectGroups(Type type, string prefix, IReadOnlyDictionary<string, string> skipped, Dictionary<string, Type> found, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(Public))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true || skipped.ContainsKey(type.Name + "." + property.Name))
            {
                continue;
            }

            string path = prefix + ":" + property.Name;
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!ConfigCatalog.IsLeaf(propertyType))
            {
                CollectGroups(propertyType, path, skipped, found, depth + 1);
                continue;
            }

            if (ConfigCatalog.IsCollection(propertyType) && ElementOf(propertyType) is { } element)
            {
                Type scalar = Nullable.GetUnderlyingType(element) ?? element;
                if (!ConfigCatalog.IsLeaf(scalar))
                {
                    found[path] = scalar;
                }
            }
        }
    }

    /// <summary>The names of the public properties of an entry type that are keys of an entry (settable, or a collection).</summary>
    public static IReadOnlyList<string> EntryKeyNames(Type entry)
    {
        var names = new List<string>();
        foreach (PropertyInfo property in entry.GetProperties(Public))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true)
            {
                continue;
            }

            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (ConfigCatalog.IsLeaf(propertyType) && (property.SetMethod?.IsPublic == true || ConfigCatalog.IsCollection(propertyType)))
            {
                names.Add(property.Name);
            }
        }

        return names;
    }

    /// <summary>Every class reachable from <paramref name="roots"/> through properties and collection elements, with the base classes of each.</summary>
    public static IReadOnlySet<Type> ReachableTypes(IEnumerable<Type> roots, IReadOnlyDictionary<string, string> skipped)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(roots);
        while (queue.Count > 0)
        {
            Type type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            for (Type? parent = type.BaseType; parent is not null && parent != typeof(object); parent = parent.BaseType)
            {
                queue.Enqueue(parent);
            }

            foreach (PropertyInfo property in type.GetProperties(Public))
            {
                if (property.GetIndexParameters().Length > 0 || skipped.ContainsKey(type.Name + "." + property.Name))
                {
                    continue;
                }

                Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                Type? target = ConfigCatalog.IsCollection(propertyType) ? ElementOf(propertyType) : propertyType;
                target = target is null ? null : Nullable.GetUnderlyingType(target) ?? target;
                if (target is { IsClass: true } && !ConfigCatalog.IsLeaf(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return seen;
    }

    /// <summary>The element type of a list or array, or the value type of a map; null when it cannot be told.</summary>
    private static Type? ElementOf(Type collection)
    {
        if (collection.IsArray)
        {
            return collection.GetElementType();
        }

        Type[] all = [.. collection.GetInterfaces(), collection];
        foreach (Type candidate in all.Where(t => t.IsGenericType))
        {
            Type definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return candidate.GetGenericArguments()[1];
            }
        }

        foreach (Type candidate in all.Where(t => t.IsGenericType))
        {
            if (candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>Every class of the assemblies, internal and nested ones too, that is named like an options class (Options, Settings or Config).</summary>
    public static IReadOnlyList<Type> OptionsNamedTypes(IEnumerable<Assembly> assemblies)
        => [.. assemblies.SelectMany(a => a.GetTypes()).Where(IsOptionsNamed).OrderBy(t => t.FullName, StringComparer.Ordinal)];

    private static bool IsOptionsNamed(Type type)
        => type.IsClass && !(type.IsAbstract && type.IsSealed) && !type.IsGenericTypeDefinition
           && (type.Name.EndsWith("Options", StringComparison.Ordinal) || type.Name.EndsWith("Settings", StringComparison.Ordinal) || type.Name.EndsWith("Config", StringComparison.Ordinal));

    /// <summary>
    /// The options-named types that are neither reachable from a catalogued section nor listed in <paramref name="notConfiguration"/> (type name to
    /// reason), and the list rows that no longer fit: a type that is gone, or one that is reachable and so does not need the exemption.
    /// </summary>
    public static IReadOnlyList<string> UncataloguedTypes(IReadOnlyList<Type> candidates, IReadOnlySet<Type> reachable, IReadOnlyDictionary<string, string> notConfiguration)
    {
        var problems = new List<string>();
        foreach (Type type in candidates.Where(t => !reachable.Contains(t) && !notConfiguration.ContainsKey(t.Name)))
        {
            problems.Add(type.FullName + " is named like an options class but no catalogued section reaches it: bind it into the catalog (a SectionName constant, or a property of a section), or add it to the not-configuration list with the reason");
        }

        foreach (string name in notConfiguration.Keys.Order(StringComparer.Ordinal))
        {
            Type[] named = [.. candidates.Where(t => t.Name == name)];
            if (named.Length == 0)
            {
                problems.Add(name + " is on the not-configuration list but no such options-named type exists any more: remove the row");
            }
            else if (named.Any(reachable.Contains))
            {
                problems.Add(name + " is on the not-configuration list but a catalogued section reaches it: remove the row");
            }
        }

        return problems;
    }

    /// <summary>
    /// The shared helpers a daemon reaches in other projects (method name to its file). <see cref="DaemonScanText"/> follows a call to one of these into
    /// its body and the same-file methods the body calls.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SharedHelpers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AddArcaneCoreLogging"] = "src/ArcaneCore.Kernel/Logging/ArcaneLoggingServiceCollectionExtensions.cs",
        ["AddAuthDatabase"] = "src/ArcaneCore.Data/DataServiceCollectionExtensions.cs",
        ["AddCharacterDatabase"] = "src/ArcaneCore.Data/DataServiceCollectionExtensions.cs",
        ["AddDatabaseResilience"] = "src/ArcaneCore.Data/Resilience/DatabaseResilienceServiceCollectionExtensions.cs",
        ["UseArcaneDiagnostics"] = "src/ArcaneCore.Kernel/Diagnostics/DiagnosticsHostingExtensions.cs",
    };

    /// <summary>The options types bound in <paramref name="text"/>, by type (or section holder) name: Configure, PostConfigure, AddOptions, GetSection(X.SectionName).Bind/.Get/.GetValue, and section.Get&lt;T&gt;.</summary>
    public static IReadOnlySet<string> OptionsBoundIn(string text)
    {
        var bound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string pattern in new[]
        {
            // [\w.]+ so a fully qualified bind (services.Configure<ArcaneCore.World.Warden.WardenOptions>) is seen; the last segment is the type name.
            @"(?:Configure|PostConfigure|AddOptions)<([\w.]+)>\(",
            @"GetSection\(\s*([\w.]+?)\.(?:SectionName|Section)\b[^()]*\)\s*\.(?:Bind|Get|GetValue)\b",
            @"\.Get<([\w.]*(?:Options|Settings|Config))>\(",
        })
        {
            foreach (Match match in Regex.Matches(text, pattern))
            {
                bound.Add(match.Groups[1].Value.Split('.')[^1]);
            }
        }

        return bound;
    }

    /// <summary>
    /// The keys read straight from IConfiguration in <paramref name="text"/>: a literal, or a constant declared in <paramref name="file"/> as a literal or as
    /// Holder.SectionName + a literal. A key in a variable or a constant of another file is not seen.
    /// </summary>
    public static IReadOnlySet<string> KeysReadIn(string text, string file, IReadOnlyDictionary<string, string> holders)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"(?:onfiguration|config)\??\[\s*(?:""([^""$]+)""|([A-Z]\w*))\s*\]"))
        {
            if (match.Groups[1].Success)
            {
                keys.Add(match.Groups[1].Value);
                continue;
            }

            Match constant = Regex.Match(file, @"\bconst\s+string\s+" + match.Groups[2].Value + @"\s*=\s*(?:(\w+)\.SectionName\s*\+\s*)?""([^""]+)""");
            if (constant.Success && (!constant.Groups[1].Success || holders.ContainsKey(constant.Groups[1].Value)))
            {
                keys.Add((constant.Groups[1].Success ? holders[constant.Groups[1].Value] : string.Empty) + constant.Groups[2].Value);
            }
        }

        return keys;
    }

    /// <summary>The body of a static method of <paramref name="file"/> and of every static method of that file it reaches, or null when the file does not declare it.</summary>
    public static string? MethodTextWithCallees(string file, string method)
    {
        if (MethodBody(file, method) is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([method]);
        var text = new System.Text.StringBuilder();
        while (pending.Count > 0)
        {
            string name = pending.Pop();
            if (!seen.Add(name) || MethodBody(file, name) is not { } body)
            {
                continue;
            }

            text.Append(body).Append('\n');
            foreach (Match call in Regex.Matches(body, @"\b([A-Z]\w*)\s*\("))
            {
                pending.Push(call.Groups[1].Value);
            }
        }

        return text.ToString();
    }

    private static string? MethodBody(string file, string method)
    {
        Match declaration = Regex.Match(file, @"\bstatic\s[\w<>\[\],.? ]*?\s" + Regex.Escape(method) + @"\s*\(");
        if (!declaration.Success)
        {
            return null;
        }

        int index = declaration.Index + declaration.Length;
        for (int depth = 1; depth > 0 && index < file.Length; index++)
        {
            depth += file[index] == '(' ? 1 : file[index] == ')' ? -1 : 0;
        }

        int brace = file.IndexOf('{', index);
        int arrow = file.IndexOf("=>", index, StringComparison.Ordinal);
        if (arrow >= 0 && (brace < 0 || arrow < brace))
        {
            int end = file.IndexOf(';', arrow);
            return file[arrow..(end < 0 ? file.Length : end)];
        }

        if (brace < 0)
        {
            return null;
        }

        int close = brace + 1;
        for (int depth = 1; depth > 0 && close < file.Length; close++)
        {
            depth += file[close] == '{' ? 1 : file[close] == '}' ? -1 : 0;
        }

        return file[brace..close];
    }

    /// <summary>
    /// The text a daemon runs: <paramref name="daemon"/>, followed by the body of every helper of <paramref name="helpers"/> that it calls. A helper that
    /// is called but no longer found in its file is added to <paramref name="problems"/>, so the guard cannot quietly stop following it.
    /// </summary>
    public static string DaemonScanText(string daemon, IReadOnlyDictionary<string, string> helpers, Func<string, string?> fileText, List<string> problems)
    {
        var text = new System.Text.StringBuilder(daemon);
        foreach ((string helper, string path) in helpers)
        {
            if (!Regex.IsMatch(daemon, @"\b" + helper + @"\("))
            {
                continue;
            }

            string? body = fileText(path) is { } file ? MethodTextWithCallees(file, helper) : null;
            if (body is null)
            {
                problems.Add(helper + ": called by the daemon but not found in " + path + ": fix the SharedHelpers row");
                continue;
            }

            text.Append('\n').Append(body);
        }

        return text.ToString();
    }
}

/// <summary>
/// The completeness guard for the configuration catalog, plus the fixture tests that prove the guard (and the entry-field documentation of list
/// and map options) detect what they are for.
/// </summary>
public sealed class ConfigCatalogCompletenessTests
{
    // ------------------------------------------------------------------ fixtures

    public enum FixtureLevel
    {
        Plain,
        Loud,
    }

    public sealed class FixtureLimit
    {
        public int Max { get; set; } = 5;
    }

    public sealed class FixtureEntry
    {
        public string Label { get; set; } = "x";

        public FixtureLevel Level { get; set; } = FixtureLevel.Loud;

        public int[] Maps { get; set; } = [1];

        public FixtureLimit Limit { get; } = new();

        public string NotAKey => "computed";

        internal int Hidden { get; set; }
    }

    public sealed record FixtureRecordEntry(uint Id, uint Percent);

    /// <summary>A positional record with a parameter default and a property outside the constructor.</summary>
    public sealed record FixtureRecordWithExtra(uint Id, uint Scale = 3)
    {
        public int Extra { get; set; } = 7;
    }

    public sealed class FixtureRefusingEntry
    {
        public FixtureRefusingEntry(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            Name = name;
        }

        public string Name { get; set; }
    }

    public sealed class FixtureTwoConstructorsEntry
    {
        public FixtureTwoConstructorsEntry(int size) => Size = size;

        public FixtureTwoConstructorsEntry(int size, int more) => Size = size + more;

        public int Size { get; set; }
    }

    public sealed class FixtureRecordListOptions
    {
        public List<FixtureRecordWithExtra> Rows { get; set; } = [];
    }

    public sealed class FixtureRefusingListOptions
    {
        public List<FixtureRefusingEntry> Rows { get; set; } = [];
    }

    public sealed class FixtureTwoConstructorsListOptions
    {
        public List<FixtureTwoConstructorsEntry> Rows { get; set; } = [];
    }

    internal sealed class FixtureInternalOptions
    {
        public int Hidden { get; set; }
    }

    public sealed class FixtureListOptions
    {
        public List<FixtureEntry> Entries { get; set; } = [];

        public Dictionary<string, FixtureEntry> ByName { get; } = new();

        public List<FixtureRecordEntry> Rows { get; set; } = [];

        public List<string> Plain { get; set; } = [];
    }

    public sealed class FixtureBoundOptions
    {
        public int Size { get; set; } = 1;

        public FixtureLimit Limit { get; } = new();

        public int ReadOnlyComputed => 2;

        public List<FixtureEntry> Entries { get; set; } = [];
    }

    public sealed class FixtureStrayOptions
    {
        public int Orphan { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, string> NoRows = new Dictionary<string, string>();

    private static ConfigCatalogResult BuildFixture(Func<Type, string, string?> summary, IReadOnlyDictionary<string, string>? overrides = null)
        => ConfigCatalog.Build([new ConfigSection("Fixture", typeof(FixtureListOptions))], summary, overrides ?? NoRows, NoRows, _ => null);

    // ------------------------------------------------------------------ the guard proves itself

    [Fact]
    public void Guard_MissingProperty_IsReportedByItsKeyPath()
    {
        ConfigSection[] sections = [new("Fix", typeof(FixtureBoundOptions))];
        HashSet<string> complete = ["Fix:Size", "Fix:Limit:Max", "Fix:Entries"];
        Assert.Empty(ConfigCompleteness.MissingPaths(sections, complete.Contains, NoRows));

        // RED shape: each of these catalogs lacks one key the options type binds.
        string only = Assert.Single(ConfigCompleteness.MissingPaths(sections, new HashSet<string> { "Fix:Size", "Fix:Entries" }.Contains, NoRows));
        Assert.StartsWith("Fix:Limit:Max", only, StringComparison.Ordinal);
        Assert.Contains("FixtureLimit.Max", only, StringComparison.Ordinal);
        string list = Assert.Single(ConfigCompleteness.MissingPaths(sections, new HashSet<string> { "Fix:Size", "Fix:Limit:Max" }.Contains, NoRows));
        Assert.StartsWith("Fix:Entries", list, StringComparison.Ordinal);
        Assert.Equal(3, ConfigCompleteness.MissingPaths(sections, _ => false, NoRows).Count);

        // A get-only scalar is computed, not a key; a skipped property is documented elsewhere.
        Assert.DoesNotContain(ConfigCompleteness.MissingPaths(sections, _ => false, NoRows), p => p.StartsWith("Fix:ReadOnlyComputed", StringComparison.Ordinal));
        IReadOnlyDictionary<string, string> skipped = new Dictionary<string, string> { ["FixtureBoundOptions.Size"] = "documented elsewhere" };
        Assert.DoesNotContain(ConfigCompleteness.MissingPaths(sections, _ => false, skipped), p => p.StartsWith("Fix:Size", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_UnreachableOptionsType_IsReported_AndAStaleExemptionToo()
    {
        Type[] candidates = [typeof(FixtureBoundOptions), typeof(FixtureStrayOptions), typeof(FixtureLimit)];
        IReadOnlySet<Type> reachable = ConfigCompleteness.ReachableTypes([typeof(FixtureBoundOptions)], NoRows);
        Assert.Contains(typeof(FixtureEntry), reachable);
        Assert.Contains(typeof(FixtureLimit), reachable);
        Assert.DoesNotContain(typeof(FixtureStrayOptions), reachable);

        string problem = Assert.Single(ConfigCompleteness.UncataloguedTypes(candidates, reachable, NoRows));
        Assert.Contains(nameof(FixtureStrayOptions), problem, StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> exempt = new Dictionary<string, string> { [nameof(FixtureStrayOptions)] = "built in code" };
        Assert.Empty(ConfigCompleteness.UncataloguedTypes(candidates, reachable, exempt));

        IReadOnlyDictionary<string, string> stale = new Dictionary<string, string> { [nameof(FixtureStrayOptions)] = "built in code", [nameof(FixtureBoundOptions)] = "wrong", ["GoneOptions"] = "gone" };
        IReadOnlyList<string> staleProblems = ConfigCompleteness.UncataloguedTypes(candidates, reachable, stale);
        Assert.Equal(2, staleProblems.Count);
        Assert.Contains(staleProblems, p => p.StartsWith(nameof(FixtureBoundOptions), StringComparison.Ordinal) && p.Contains("reaches it", StringComparison.Ordinal));
        Assert.Contains(staleProblems, p => p.StartsWith("GoneOptions", StringComparison.Ordinal) && p.Contains("no such", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_FindsListsOfGroups_AndTheirEntryKeys()
    {
        IReadOnlyDictionary<string, Type> groups = ConfigCompleteness.GroupCollections([new ConfigSection("Fixture", typeof(FixtureListOptions))], NoRows);
        Assert.Equal(typeof(FixtureEntry), groups["Fixture:Entries"]);
        Assert.Equal(typeof(FixtureEntry), groups["Fixture:ByName"]);
        Assert.Equal(typeof(FixtureRecordEntry), groups["Fixture:Rows"]);
        Assert.DoesNotContain("Fixture:Plain", groups.Keys);

        Assert.Equal(new[] { "Label", "Level", "Maps" }, ConfigCompleteness.EntryKeyNames(typeof(FixtureEntry)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Guard_OptionsNamedTypes_SeesInternalAndNestedTypes()
    {
        IReadOnlyList<Type> types = ConfigCompleteness.OptionsNamedTypes([typeof(ConfigCatalogCompletenessTests).Assembly]);
        Assert.Contains(typeof(FixtureInternalOptions), types);
        Assert.Contains(typeof(FixtureStrayOptions), types);
    }

    [Fact]
    public void Guard_DaemonScan_SeesEveryBindStyle()
    {
        const string text = """
            services.Configure<OneOptions>(x);
            services.PostConfigure<Some.Where.TwoOptions>(x);
            services.AddOptions<ThreeOptions>().Bind(y);
            configuration.GetSection(FourOptions.SectionName).Bind(four);
            configuration.GetSection(Some.FiveOptions.SectionName)
                .Get<FiveOptions>();
            bool on = configuration.GetSection(SixOptions.SectionName).GetValue<bool>("Enabled");
            section.Get<SevenOptions>();
            configuration.GetSection(EightHolder.Section + ":Sub").Bind(eight);
            configuration.GetSection(NotReadOptions.SectionName);
            map.Get<int>(1);
            """;
        Assert.Equal(
            new[] { "EightHolder", "FiveOptions", "FourOptions", "OneOptions", "SevenOptions", "SixOptions", "ThreeOptions", "TwoOptions" },
            ConfigCompleteness.OptionsBoundIn(text));
    }

    [Fact]
    public void Guard_DaemonScan_ReadsKeysFromLiteralsAndConstantsOfTheFile()
    {
        const string file = """
            public const string PlainKey = "A:B";
            private const string HeldKey = Holder.SectionName + ":C";
            bool one = configuration[PlainKey];
            bool two = configuration[HeldKey];
            bool three = _configuration["D:E"];
            bool four = configuration[variable];
            bool five = configuration[Elsewhere];
            """;
        IReadOnlyDictionary<string, string> holders = new Dictionary<string, string> { ["Holder"] = "Held" };
        Assert.Equal(new[] { "A:B", "D:E", "Held:C" }, ConfigCompleteness.KeysReadIn(file, file, holders));
    }

    [Fact]
    public void Guard_DaemonScan_FollowsAHelperThroughItsFile_AndReportsALostOne()
    {
        const string helpers = """
            public static class Extensions
            {
                public static IServiceCollection AddThing(this IServiceCollection services, IConfiguration configuration)
                {
                    AddCommon(services, configuration);
                    return services;
                }

                private static void AddCommon(IServiceCollection services, IConfiguration configuration)
                {
                    services.Configure<CommonOptions>(configuration.GetSection(CommonOptions.SectionName));
                    Required(configuration);
                }

                private static bool Required(IConfiguration configuration) => configuration["X:Y"] == "1";

                public static void NotCalled(IServiceCollection services) => services.Configure<OtherOptions>(_ => { });
            }
            """;
        IReadOnlyDictionary<string, string> table = new Dictionary<string, string> { ["AddThing"] = "extensions.cs", ["AddAbsent"] = "extensions.cs", ["AddUnused"] = "extensions.cs" };
        var problems = new List<string>();
        string text = ConfigCompleteness.DaemonScanText("services.AddThing(configuration); services.AddAbsent(configuration);", table, _ => helpers, problems);
        Assert.Equal(new[] { "CommonOptions" }, ConfigCompleteness.OptionsBoundIn(text));
        Assert.Equal(new[] { "X:Y" }, ConfigCompleteness.KeysReadIn(text, helpers, new Dictionary<string, string>()));
        Assert.StartsWith("AddAbsent:", Assert.Single(problems), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ entry fields of lists and maps

    [Fact]
    public void ElementFields_ListMapAndRecordEntries_AreDocumentedWithDefaults()
    {
        ConfigCatalogResult result = BuildFixture(
            (_, _) => "doc.",
            new Dictionary<string, string>());
        Assert.Empty(result.Problems);

        foreach (string key in new[] { "Fixture:Entries", "Fixture:ByName" })
        {
            ConfigEntry entry = result.Entries.Single(e => e.Path == key);
            Assert.NotNull(entry.ElementFields);
            Assert.Equal(new[] { "Label", "Level", "Limit:Max", "Maps" }, entry.ElementFields.Select(f => f.Name));
            ConfigElementField label = entry.ElementFields.Single(f => f.Name == "Label");
            Assert.Equal("string", label.TypeText);
            Assert.Equal("\"x\"", label.Default);
            Assert.Equal("doc.", label.Meaning);
            Assert.Equal("5", entry.ElementFields.Single(f => f.Name == "Limit:Max").Default);
            Assert.Equal("[1]", entry.ElementFields.Single(f => f.Name == "Maps").Default);
            ConfigElementField level = entry.ElementFields.Single(f => f.Name == "Level");
            Assert.Equal("Loud", level.Default);
            Assert.Contains("Values: `Plain`, `Loud`.", level.Meaning, StringComparison.Ordinal);
        }

        // A positional record has no parameterless constructor: its fields still appear, and a parameter without a default value is required
        // (the binder drops an entry without the key, it does not leave zero; AuctionHouseConfigTests proves it, and a config check has to refuse the entry).
        ConfigEntry rows = result.Entries.Single(e => e.Path == "Fixture:Rows");
        Assert.Equal(new[] { "Id", "Percent" }, rows.ElementFields!.Select(f => f.Name));
        Assert.All(rows.ElementFields!, f => Assert.Equal("required", f.Default));

        // A list of scalars has no entry fields.
        Assert.Null(result.Entries.Single(e => e.Path == "Fixture:Plain").ElementFields);
    }

    [Fact]
    public void ElementFields_PropertyOutsideTheConstructor_ShowsItsRealDefault()
    {
        ConfigCatalogResult result = ConfigCatalog.Build([new ConfigSection("Fixture", typeof(FixtureRecordListOptions))], (_, _) => "doc.", NoRows, NoRows, _ => null);
        Assert.Empty(result.Problems);
        IReadOnlyList<ConfigElementField> fields = result.Entries.Single(e => e.Path == "Fixture:Rows").ElementFields!;
        Assert.Equal("required", fields.Single(f => f.Name == "Id").Default);
        Assert.Equal("3", fields.Single(f => f.Name == "Scale").Default);
        Assert.Equal("7", fields.Single(f => f.Name == "Extra").Default);
    }

    [Fact]
    public void ElementFields_EntryThatCannotBeBuilt_IsAProblem_NotARowOfZeros()
    {
        ConfigCatalogResult refusing = ConfigCatalog.Build([new ConfigSection("Fixture", typeof(FixtureRefusingListOptions))], (_, _) => "doc.", NoRows, NoRows, _ => null);
        Assert.Contains(refusing.Problems, p => p.StartsWith("Fixture:Rows:", StringComparison.Ordinal) && p.Contains(nameof(FixtureRefusingEntry), StringComparison.Ordinal) && p.Contains("threw", StringComparison.Ordinal));

        ConfigCatalogResult two = ConfigCatalog.Build([new ConfigSection("Fixture", typeof(FixtureTwoConstructorsListOptions))], (_, _) => "doc.", NoRows, NoRows, _ => null);
        Assert.Contains(two.Problems, p => p.StartsWith("Fixture:Rows:", StringComparison.Ordinal) && p.Contains(nameof(FixtureTwoConstructorsEntry), StringComparison.Ordinal) && p.Contains("2 public constructors", StringComparison.Ordinal));
    }

    [Fact]
    public void ElementFields_FieldWithoutSummaryOrOverride_IsAProblemNamingTheEntryKey()
    {
        ConfigCatalogResult result = BuildFixture((type, name) => type == typeof(FixtureEntry) && name == "Label" ? null : "doc.");
        Assert.Contains(result.Problems, p => p.StartsWith("Fixture:Entries:Label:", StringComparison.Ordinal) && p.Contains("FixtureEntry.Label", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.StartsWith("Fixture:ByName:Label:", StringComparison.Ordinal));

        ConfigCatalogResult fixedUp = BuildFixture(
            (type, name) => type == typeof(FixtureEntry) && name == "Label" ? null : "doc.",
            new Dictionary<string, string> { ["FixtureEntry.Label"] = "From the override." });
        Assert.Empty(fixedUp.Problems);
        Assert.Equal("From the override.", fixedUp.Entries.Single(e => e.Path == "Fixture:Entries").ElementFields!.Single(f => f.Name == "Label").Meaning);
    }

    [Fact]
    public void Renderer_ListsTheEntryFieldsOfEveryListAndMapOfGroups()
    {
        ConfigCatalogResult result = BuildFixture((_, _) => "doc.");
        string page = ConfigReferenceRenderer.Render(result.Entries, [], NoRows);
        Assert.Contains("- [List and map element fields](#list-and-map-element-fields)", page, StringComparison.Ordinal);
        Assert.Contains("## List and map element fields", page, StringComparison.Ordinal);
        Assert.Contains("### `Fixture:Entries`", page, StringComparison.Ordinal);
        Assert.Contains("each entry sets `Fixture:Entries:<index>:<Field>`", page, StringComparison.Ordinal);
        Assert.Contains("each entry sets `Fixture:ByName:<name>:<Field>`", page, StringComparison.Ordinal);
        Assert.Contains("| `Label` | `string` | `\"x\"` | doc. |", page, StringComparison.Ordinal);
        Assert.Contains("| `Limit:Max` | `int` | `5` | doc. |", page, StringComparison.Ordinal);
        Assert.DoesNotContain("### `Fixture:Plain`", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ production

    private static readonly IReadOnlyDictionary<string, string> NotConfiguration = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ConditionTableOptions"] = "A parameter of ConditionTable.Build, constructed in code; nothing binds it from configuration (conditions are configured through the Conditions section).",
        ["DatabaseSettings"] = "A record of what the schema drift checker read from the live database (character set, encoding, storage engines); not an option.",
        ["PlayerDumpOptions"] = "An optional service the .pdump command reads (its directory); nothing registers or binds it from configuration, so the directory stays the default.",
        ["RuleBuildOptions"] = "A parameter of RuleCatalog.Build, constructed in code; nothing binds it from configuration.",
        ["SchemaUpgradeOptions"] = "The parameter object of the schema bootstrapper, built in code by the daemons and the db tools from the catalogued Database:Upgrade keys (policy, lock timeout) and command-line switches.",
        ["GroupRewardOptions"] = "A parameter of the kill-reward range, built in code at its call sites; nothing binds it from configuration.",
        ["HonorOptions"] = "The runtime form of the catalogued World:Honor keys (HonorSettings); built from them, never bound itself.",
        ["HonorMaintenanceOptions"] = "A parameter of the weekly honor maintenance, derived from HonorOptions.Maintenance.",
        ["PathOptions"] = "A per-query parameter of the path finder, built in code at each call; not configuration.",
        ["SkillOptions"] = "The runtime form of the catalogued Skills keys (SkillsFeatureOptions.ToSkillOptions); built from them, never bound itself.",
        ["AccountSettings"] = "The per-account client data row (the account_data store); player data, not configuration.",
        ["BulkheadOptions"] = "The runtime form of a catalogued Resilience bulkhead (BulkheadSettings.ToOptions); built from it, never bound itself.",
        ["CircuitBreakerOptions"] = "The runtime form of a catalogued Resilience circuit breaker (CircuitBreakerSettings.ToOptions); built from it, never bound itself.",
        ["RetryOptions"] = "A parameter of a retry policy, built in code at its call site (the schema lock poll); not configuration.",
        ["GmStreamOptions"] = "The settings of the GM diagnostics stream, constructed with its defaults in code; nothing binds them from configuration.",
        ["KvCacheConfig"] = "The sizing of the in-process model KV cache, built in code by the prefix store; nothing binds it from configuration.",
        ["SchedulerConfig"] = "The inference scheduler budgets, built in code from the catalogued World:Playerbots:Chat serving keys (InProcessChatEngine.SchedulerConfigFor).",
        ["ScenarioRunOptions"] = "A per-run parameter of the scenario runner (the scenario command), built in code; not configuration.",
        ["ChatGenerationSettings"] = "The sampling settings of one reply, built in code from the catalogued World:Playerbots:Chat in-process keys (InProcessBotChatClient.Settings).",
        ["ChatModelConfig"] = "The shape of the loaded model, read from the model file's own metadata (ChatModel); not configuration.",
        ["ChatServingOptions"] = "The engine's scheduling settings, built in code from the catalogued World:Playerbots:Chat in-process keys (InProcessBotChatClient.Serving).",
        ["PacketHeuristicsSettings"] = "The immutable, validated runtime view of the catalogued PacketHeuristicsOptions; built from them, never bound itself.",
        ["SamplerConfig"] = "The sampler chain of one reply, built in code from ChatGenerationSettings (ChatReplyDecoder.Config); not configuration.",
        ["SharedOptions"] = "A private cache of the path finder (NavMeshPathfinder) holding path options per cancellation generation; not configuration.",
    };

    private static ConfCatalog Embedded => ConfCatalog.Embedded;

    [Fact]
    public void Production_EveryBoundOptionsProperty_IsInTheEmbeddedCatalog()
    {
        Assert.True(Embedded.Keys.Count > 1000, "the embedded catalog must hold the keys; it has " + Embedded.Keys.Count);
        IReadOnlyList<ConfigSection> sections = ConfigExceptionTable.Sections();
        Assert.True(sections.Count > 50, "the walk must see the options sections; it found " + sections.Count);
        IReadOnlyList<string> missing = ConfigCompleteness.MissingPaths(sections, path => Embedded.Find(path) is not null, ConfigExceptionTable.SkippedProperties);
        Assert.True(missing.Count == 0, "options properties the binder reads that the embedded catalog (conf-catalog.json) lacks; regenerate it with ARCANECORE_UPDATE_DOCS=1 or catalogue the type:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Production_EveryOptionsNamedType_IsInTheCatalogOrListedAsNotConfiguration()
    {
        IReadOnlyList<ConfigSection> sections = ConfigExceptionTable.Sections();
        IReadOnlySet<Type> reachable = ConfigCompleteness.ReachableTypes(sections.Select(s => s.Type), ConfigExceptionTable.SkippedProperties);
        // A type skipped as a whole (ChatRestrictionOptions) is documented by the type that shares its keys.
        HashSet<Type> withSkipped = [.. reachable];
        foreach (Type type in ConfigExceptionTable.Assemblies.SelectMany(a => a.GetExportedTypes()).Where(t => ConfigExceptionTable.SkippedProperties.ContainsKey(t.Name)))
        {
            withSkipped.Add(type);
        }

        IReadOnlyList<Type> candidates = ConfigCompleteness.OptionsNamedTypes(ConfigExceptionTable.Assemblies);
        Assert.True(candidates.Count > 150, "the scan must see the options classes; it found " + candidates.Count);
        IReadOnlyList<string> problems = ConfigCompleteness.UncataloguedTypes(candidates, withSkipped, NotConfiguration);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Production_EveryListOrMapOfGroups_DocumentsAllItsEntryFields()
    {
        IReadOnlyDictionary<string, Type> groups = ConfigCompleteness.GroupCollections(ConfigExceptionTable.Sections(), ConfigExceptionTable.SkippedProperties);
        Assert.True(groups.Count >= 12, "expected the twelve known lists of groups; the walk found " + groups.Count + ": " + string.Join(", ", groups.Keys));
        string page = ConfigReferenceRenderer.Render(ConfigProduction.Catalog.Entries, ConfigExceptionTable.AdHocKeys, ConfigExceptionTable.SkippedProperties);
        var problems = new List<string>();
        foreach ((string path, Type entryType) in groups)
        {
            ConfigEntry? entry = ConfigProduction.Catalog.Entries.SingleOrDefault(e => e.Path == path);
            if (entry?.ElementFields is not { Count: > 0 } fields)
            {
                problems.Add(path + ": the catalog has no entry fields for " + entryType.Name);
                continue;
            }

            foreach (string name in ConfigCompleteness.EntryKeyNames(entryType).Where(n => fields.All(f => f.Name != n)))
            {
                problems.Add(path + ": entry field " + name + " of " + entryType.Name + " is not documented");
            }

            if (fields.Any(f => string.IsNullOrWhiteSpace(f.Meaning)))
            {
                problems.Add(path + ": an entry field has no meaning");
            }

            if (!page.Contains("### `" + path + "`", StringComparison.Ordinal))
            {
                problems.Add(path + ": the configuration reference page has no element-field table for it");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Production_TheKnownEntryFieldsOfTheListsOfGroups_AreDocumented()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Auth:IntegrityHashes"] = ["Build", "Os", "Platform", "Hash"],
            ["Auth:AutoPatch:Patches"] = ["Build", "Locale", "File"],
            ["Cluster:Peer:Endpoints"] = ["NodeId", "Address"],
            ["Cluster:Supervisor:Processes"] = ["Name", "Role", "ExecutablePath", "Arguments", "DrainTimeoutMs", "Maps", "StartOrder"],
            ["Economy:AuctionHouses"] = ["Id", "DepositPercent", "CutPercent"],
            ["Gateway:Workers"] = ["NodeId", "Address", "Port", "Maps", "StandbyNodeId", "StandbyAddress", "StandbyPort"],
            ["NpcServices:NpcTemplates"] = ["Entry", "GossipMenuId", "TrainerType", "TrainerClass", "TrainerRace", "TrainerSpell"],
            ["Quests:AreaTriggerQuests"] = ["TriggerId", "QuestId"],
            ["Realms:Seed"] = ["Name", "Address", "Type", "Flags", "Population", "Category"],
            ["Warden:Checks"] = ["Id", "Kind", "Module", "Address", "Expected", "Path", "Pattern", "DriverName", "DriverPath", "Wanted", "Function", "Length", "InitialLogin", "Action", "Comment"],
        };
        foreach ((string path, string[] names) in expected)
        {
            ConfigEntry entry = ConfigProduction.Catalog.Entries.Single(e => e.Path == path);
            Assert.NotNull(entry.ElementFields);
            foreach (string name in names)
            {
                Assert.Contains(entry.ElementFields, f => f.Name == name);
            }
        }
    }

    [Fact]
    public void Production_ListAndMapKeysOfGroups_AreCollectionsInTheEmbeddedCatalog()
    {
        foreach (string path in ConfigCompleteness.GroupCollections(ConfigExceptionTable.Sections(), ConfigExceptionTable.SkippedProperties).Keys)
        {
            ConfKey? key = Embedded.Find(path);
            Assert.True(key is not null && key.IsCollection && key.ElementType == "object", path + " must be a list or map of groups in the embedded catalog");
        }
    }

    [Fact]
    public void Production_HostFrameworkKeys_AreAcceptedByTheChecks_AndNamedOnThePage()
    {
        foreach (string key in new[] { "Logging:LogLevel:Default", "Logging:LogLevel:Microsoft.EntityFrameworkCore", "HostOptions:ShutdownTimeout", "hostBuilder:reloadConfigOnChange" })
        {
            Assert.True(ConfCatalog.IsFrameworkKey(key), key + " must be a framework key");
        }

        Assert.False(ConfCatalog.IsFrameworkKey("Logging:ArcaneCore:Console:Enabled"));
        string page = ConfigReferenceRenderer.Render(ConfigProduction.Catalog.Entries, ConfigExceptionTable.AdHocKeys, ConfigExceptionTable.SkippedProperties);
        Assert.Contains("`Logging:LogLevel:<category>`", page, StringComparison.Ordinal);
        Assert.Contains("`HostOptions:ShutdownTimeout`", page, StringComparison.Ordinal);
        Assert.Contains("`hostBuilder:reloadConfigOnChange`", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A daemon that binds an options type must find its keys in its own .conf.dist: the type has to name that daemon in [ConfigDaemons], and so does
    /// every catalog key below its section (the catalog decides which dist files carry a key and which daemon warns about a foreign key). The scan
    /// reads the text under src/ArcaneCore.Gateway and src/ArcaneCore.Realm plus the bodies of the shared helpers it calls (<see cref="ConfigCompleteness.SharedHelpers"/>)
    /// and of the same-file methods those call. It sees Configure, PostConfigure and AddOptions (qualified type names too), GetSection(X.SectionName).Bind/.Get/.GetValue,
    /// section.Get&lt;T&gt;, and keys read straight from IConfiguration by a literal or a constant of the same file. Limits: a helper not in the table is not followed (a
    /// World type a daemon uses, or a helper in a file not listed); a section chosen at run time, a key held in a variable, and a local section variable bound with .Bind are not seen;
    /// a section whose type has a property-level [ConfigDaemons] is checked at type level only, plus the keys read by name. It is a guard against the known styles, not a proof.
    /// </summary>
    [Theory]
    [InlineData("src/ArcaneCore.Gateway/", ConfigDaemon.Gateway)]
    [InlineData("src/ArcaneCore.Realm/", ConfigDaemon.Realm)]
    public void Production_EveryOptionsTypeADaemonBinds_NamesThatDaemon_AndSoDoItsCatalogKeys(string sourceRoot, ConfigDaemon daemon)
    {
        IReadOnlyList<(string Path, string Text)> sources = ConfigProduction.SourceFiles();
        IReadOnlyDictionary<string, string> holders = ConfigExceptionTable.DiscoverHolders();
        var wrong = new List<string>();
        var bound = new SortedSet<string>(StringComparer.Ordinal);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string path, string text) in sources.Where(f => f.Path.StartsWith(sourceRoot, StringComparison.Ordinal)))
        {
            string scanned = ConfigCompleteness.DaemonScanText(text, ConfigCompleteness.SharedHelpers, file => sources.FirstOrDefault(f => f.Path == file).Text, wrong);
            bound.UnionWith(ConfigCompleteness.OptionsBoundIn(scanned).Select(name => ConfigExceptionTable.HolderToOptions.TryGetValue(name, out Type? options) ? options.Name : name));
            keys.UnionWith(ConfigCompleteness.KeysReadIn(text, text, holders));
            foreach ((string helper, string file) in ConfigCompleteness.SharedHelpers.Where(h => Regex.IsMatch(text, @"\b" + h.Key + @"\(")))
            {
                string helperFile = sources.FirstOrDefault(f => f.Path == file).Text ?? string.Empty;
                keys.UnionWith(ConfigCompleteness.KeysReadIn(ConfigCompleteness.MethodTextWithCallees(helperFile, helper) ?? string.Empty, helperFile, holders));
            }
        }

        // Each of these is reached a different way, so an empty scan or a lost helper fails here and not by finding nothing wrong.
        string[] expectedBound = daemon == ConfigDaemon.Gateway
            ? ["WardenOptions", "GatewayOptions", "ArcaneLoggingOptions", "DatabaseOptions", "DiagnosticsOptions"]
            : ["AuthOptions", "ArcaneLoggingOptions", "DatabaseOptions", "DiagnosticsOptions", "ResilienceOptions"];
        Assert.All(expectedBound, name => Assert.Contains(name, bound));
        if (daemon == ConfigDaemon.Gateway)
        {
            Assert.Contains("Cluster:Fencing:Enabled", keys);
            Assert.Contains("Cluster:Worker:Enabled", keys);
        }

        IReadOnlyList<ConfigSection> sections = ConfigExceptionTable.Sections();
        foreach (string typeName in bound)
        {
            ConfigSection? section = sections.SingleOrDefault(s => s.Type.Name == typeName);
            if (section is null)
            {
                wrong.Add(typeName + ": bound by " + sourceRoot + " but not a catalogued section");
                continue;
            }

            ConfigDaemon declared = section.Type.GetCustomAttributes(typeof(ArcaneCore.Kernel.Configuration.ConfigDaemonsAttribute), inherit: false)
                .Cast<ArcaneCore.Kernel.Configuration.ConfigDaemonsAttribute>().SingleOrDefault()?.Daemons ?? ConfigDaemon.World;
            if ((declared & daemon) == 0)
            {
                wrong.Add(typeName + " (" + section.Path + "): bound by " + sourceRoot + " but [ConfigDaemons] is " + declared);
            }

            // A key narrowed on purpose by a property attribute (DatabaseOptions) is the one exception: only keys that no attribute narrowed are checked, which is every key of a section whose type has no property attribute.
            bool narrowed = section.Type.GetProperties().Any(p => p.GetCustomAttributes(typeof(ArcaneCore.Kernel.Configuration.ConfigDaemonsAttribute), inherit: false).Length > 0);
            if (narrowed)
            {
                continue;
            }

            // By section, not by path prefix: Diagnostics:PacketCapture sits below Diagnostics but is another options type, which the daemon does not bind.
            foreach (ConfigEntry entry in ConfigProduction.Catalog.Entries.Where(e => e.Section == section.Path))
            {
                if ((entry.Daemons & daemon) == 0)
                {
                    wrong.Add(entry.Path + ": read by " + daemon + " (" + typeName + ") but the catalog lists it for " + entry.Daemons);
                }
            }
        }

        foreach (string key in keys)
        {
            ConfigDaemon? readers = ConfigProduction.Catalog.Entries.FirstOrDefault(e => e.Path == key)?.Daemons ?? ConfigExceptionTable.AdHocKeys.FirstOrDefault(k => k.Path == key)?.Daemons;
            if (readers is null)
            {
                wrong.Add(key + ": read by name by " + daemon + " but not a catalog key");
            }
            else if ((readers.Value & daemon) == 0)
            {
                wrong.Add(key + ": read by name by " + daemon + " but the catalog lists it for " + readers);
            }
        }

        Assert.True(wrong.Count == 0, "options a daemon binds are missing for it in the catalog:\n" + string.Join("\n", wrong.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>The config check that refuses an entry of each list with a required entry field: the binder would drop that entry silently (AuctionHouseConfigTests).</summary>
    private static readonly IReadOnlyDictionary<string, IConfigCheck> RequiredFieldChecks = new Dictionary<string, IConfigCheck>(StringComparer.Ordinal)
    {
        ["Economy:AuctionHouses"] = new EconomyConfigChecks(),
    };

    [Fact]
    public void Production_EveryListWithARequiredEntryField_HasACheckThatRefusesTheEntry()
    {
        ConfigEntry[] lists = [.. ConfigProduction.Catalog.Entries.Where(e => e.ElementFields?.Any(f => f.Default == "required") == true)];
        Assert.Contains(lists, e => e.Path == "Economy:AuctionHouses");
        foreach (ConfigEntry list in lists)
        {
            Assert.True(RequiredFieldChecks.ContainsKey(list.Path), list.Path + " has required entry fields, and the binder drops an entry without one silently: add a config check that refuses it, and a row here");
            IConfigCheck check = RequiredFieldChecks[list.Path];
            foreach (ConfigElementField missing in list.ElementFields!.Where(f => f.Default == "required"))
            {
                string prefix = list.Path + (list.Kind == ConfKey.Map ? ":x:" : ":0:");
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(list.ElementFields!.Where(f => f != missing).Select(f => new KeyValuePair<string, string?>(prefix + f.Name, "1")))
                    .Build();
                Assert.Contains(check.Check(configuration), issue => issue.Severity == ConfigSeverity.Error && issue.Key == prefix + missing.Name);
            }
        }
    }

    [Theory]
    [InlineData("Cluster:Fencing:Enabled")]
    [InlineData("Cluster:Worker:Enabled")]
    public void Production_TheCharacterFenceSwitches_AreListedForTheWorldAndTheGateway(string key)
        => Assert.Equal(ConfigDaemon.World | ConfigDaemon.Gateway, ConfigProduction.Catalog.Entries.Single(e => e.Path == key).Daemons);

    [Fact]
    public void Production_WardenKeys_AreListedForTheWorldAndTheGateway()
    {
        ConfigEntry[] warden = [.. ConfigProduction.Catalog.Entries.Where(e => e.Path == "Warden" || e.Path.StartsWith("Warden:", StringComparison.Ordinal))];
        Assert.Contains(warden, e => e.Path == "Warden:Enabled");
        Assert.Contains(warden, e => e.Path == "Warden:Checks");
        Assert.All(warden, e => Assert.Equal(ConfigDaemon.World | ConfigDaemon.Gateway, e.Daemons));
    }
}
