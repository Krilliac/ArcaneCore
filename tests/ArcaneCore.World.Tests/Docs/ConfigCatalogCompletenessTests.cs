using System.Collections;
using System.Reflection;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
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

    /// <summary>Every public class of the assemblies that is named like an options class (Options, Settings or Config).</summary>
    public static IReadOnlyList<Type> OptionsNamedTypes(IEnumerable<Assembly> assemblies)
        => [.. assemblies.SelectMany(a => a.GetExportedTypes()).Where(IsOptionsNamed).OrderBy(t => t.FullName, StringComparer.Ordinal)];

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
        // (the binder throws when the key is missing, it does not leave zero).
        ConfigEntry rows = result.Entries.Single(e => e.Path == "Fixture:Rows");
        Assert.Equal(new[] { "Id", "Percent" }, rows.ElementFields!.Select(f => f.Name));
        Assert.All(rows.ElementFields!, f => Assert.Equal("required", f.Default));

        // A list of scalars has no entry fields.
        Assert.Null(result.Entries.Single(e => e.Path == "Fixture:Plain").ElementFields);
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
    /// covers every Configure, PostConfigure and AddOptions call under src/ArcaneCore.Gateway and src/ArcaneCore.Realm.
    /// </summary>
    [Theory]
    [InlineData("src/ArcaneCore.Gateway/", ConfigDaemon.Gateway)]
    [InlineData("src/ArcaneCore.Realm/", ConfigDaemon.Realm)]
    public void Production_EveryOptionsTypeADaemonBinds_NamesThatDaemon_AndSoDoItsCatalogKeys(string sourceRoot, ConfigDaemon daemon)
    {
        var bound = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string path, string text) in ConfigProduction.SourceFiles().Where(f => f.Path.StartsWith(sourceRoot, StringComparison.Ordinal)))
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"(?:Configure|AddOptions)<(\w+)>\("))
            {
                bound.Add(match.Groups[1].Value);
            }
        }

        Assert.True(bound.Count >= 2, "expected " + sourceRoot + " to bind several options types; found " + bound.Count);
        var wrong = new List<string>();
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

            foreach (ConfigEntry entry in ConfigProduction.Catalog.Entries.Where(e => e.Path == section.Path || e.Path.StartsWith(section.Path + ":", StringComparison.Ordinal)))
            {
                if ((entry.Daemons & daemon) == 0)
                {
                    wrong.Add(entry.Path + ": read by " + daemon + " (" + typeName + ") but the catalog lists it for " + entry.Daemons);
                }
            }
        }

        Assert.True(wrong.Count == 0, "options a daemon binds are missing for it in the catalog:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void Production_WardenKeys_AreListedForTheWorldAndTheGateway()
    {
        ConfigEntry[] warden = [.. ConfigProduction.Catalog.Entries.Where(e => e.Path == "Warden" || e.Path.StartsWith("Warden:", StringComparison.Ordinal))];
        Assert.Contains(warden, e => e.Path == "Warden:Enabled");
        Assert.Contains(warden, e => e.Path == "Warden:Checks");
        Assert.All(warden, e => Assert.Equal(ConfigDaemon.World | ConfigDaemon.Gateway, e.Daemons));
    }
}
