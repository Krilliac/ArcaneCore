using System.Text.Json;
using ArcaneCore.World.Reload;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// The configuration reference (docs/reference/configuration.md) is generated from the options classes. The first group of tests runs
/// the generator on synthetic fixture types (so a defect in the generator itself cannot hide behind the production
/// options); the second group runs it on the real options and guards the committed page.
/// </summary>
public sealed class ConfigReferenceTests
{
    public enum FixtureMode
    {
        Off,
        Fast,
    }

    public sealed class FixtureChild
    {
        /// <summary>Child depth.</summary>
        public int Depth { get; set; } = 3;
    }

    public sealed class FixtureOptions
    {
        /// <summary>A flag.</summary>
        public bool Flag { get; set; } = true;

        /// <summary>A wait.</summary>
        public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>A mode.</summary>
        public FixtureMode Mode { get; set; } = FixtureMode.Fast;

        /// <summary>A ratio.</summary>
        public float Ratio { get; set; } = 0.1f;

        /// <summary>A name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The map.</summary>
        public Dictionary<string, int> Map { get; } = new() { ["b"] = 2, ["a"] = 1 };

        /// <summary>The child.</summary>
        public FixtureChild Child { get; } = new();

        /// <summary>An optional child, null until configured.</summary>
        public FixtureChild? Maybe { get; set; }

        public int Undocumented { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

    private static ConfigCatalogResult BuildFixture(IEnumerable<ConfigSection> sections, IReadOnlyDictionary<string, string>? overrides = null)
        => ConfigCatalog.Build(sections, (_, _) => "doc.", overrides ?? NoOverrides, NoOverrides, _ => null);

    [Fact]
    public void Catalog_PropertyWithoutSummaryOrOverride_IsAProblemNamingItsKey()
    {
        ConfigCatalogResult result = ConfigCatalog.Build(
            [new ConfigSection("Fixture", typeof(FixtureOptions))],
            (type, name) => name == "Undocumented" ? null : "doc.",
            NoOverrides,
            NoOverrides,
            _ => null);
        string problem = Assert.Single(result.Problems);
        Assert.Contains("Fixture:Undocumented", problem, StringComparison.Ordinal);
        Assert.Contains("FixtureOptions.Undocumented", problem, StringComparison.Ordinal);

        ConfigCatalogResult fixedUp = ConfigCatalog.Build(
            [new ConfigSection("Fixture", typeof(FixtureOptions))],
            (type, name) => name == "Undocumented" ? null : "doc.",
            new Dictionary<string, string> { ["FixtureOptions.Undocumented"] = "From the override." },
            NoOverrides,
            _ => null);
        Assert.Empty(fixedUp.Problems);
        Assert.Equal("From the override.", fixedUp.Entries.Single(e => e.Path == "Fixture:Undocumented").Meaning);
    }

    [Fact]
    public void Catalog_TwoTypesOnOneKeyPath_AreReported()
    {
        ConfigCatalogResult result = BuildFixture([new ConfigSection("Fixture", typeof(FixtureChild)), new ConfigSection("Fixture", typeof(FixtureChild))]);
        Assert.Contains(result.Problems, p => p.StartsWith("Fixture:Depth:", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_TypeWithoutParameterlessConstructor_IsAProblem()
    {
        ConfigCatalogResult result = BuildFixture([new ConfigSection("NoCtor", typeof(string))]);
        Assert.Contains(result.Problems, p => p.Contains("parameterless constructor", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_Defaults_AreReadFromAConstructedInstance()
    {
        ConfigCatalogResult result = BuildFixture([new ConfigSection("Fixture", typeof(FixtureOptions))], new Dictionary<string, string> { ["FixtureOptions.Undocumented"] = "x" });
        Assert.Empty(result.Problems);
        string DefaultOf(string key) => result.Entries.Single(e => e.Path == key).Default;
        Assert.Equal("true", DefaultOf("Fixture:Flag"));
        Assert.Equal("00:00:10", DefaultOf("Fixture:Wait"));
        Assert.Equal("Fast", DefaultOf("Fixture:Mode"));
        Assert.Equal("0.1", DefaultOf("Fixture:Ratio"));
        Assert.Equal("\"\"", DefaultOf("Fixture:Name"));
        Assert.Equal("{\"a\": 1, \"b\": 2}", DefaultOf("Fixture:Map"));
        Assert.Equal("3", DefaultOf("Fixture:Child:Depth"));
        Assert.Equal("3", DefaultOf("Fixture:Maybe:Depth"));
        Assert.Equal("FixtureMode", result.Entries.Single(e => e.Path == "Fixture:Mode").TypeText);
        Assert.Contains("Values: `Off`, `Fast`.", result.Entries.Single(e => e.Path == "Fixture:Mode").Meaning, StringComparison.Ordinal);
    }

    [Fact]
    public void Summaries_RenderTags_AndOnlyReadTheTypesOwnBody()
    {
        const string first = """
            namespace N;

            /// <summary>The outer.</summary>
            public sealed partial class Outer
            {
                public sealed class Inner
                {
                    /// <summary>Inner size.</summary>
                    public int Size { get; set; }
                }

                /// <summary>
                /// Outer size, see <see cref="Inner"/> and <c>Foo:Bar</c>;
                /// not <paramref name="x"/> but <see langword="null"/> | pipe &lt;tag&gt;.
                /// </summary>
                [Obsolete("x")]
                public int Size { get; set; }
            }
            """;
        const string second = """
            public sealed partial class Outer
            {
                /// <summary>From the partial.</summary>
                public int Other { get; set; }
            }
            """;
        var summaries = new ConfigSourceSummaries([first, second]);
        Assert.Equal("Inner size.", summaries.Find(typeof(Inner), "Size"));
        Assert.Equal("Outer size, see `Inner` and `Foo:Bar`; not `x` but `null` \\| pipe &lt;tag&gt;.", summaries.Find(typeof(Outer), "Size"));
        Assert.Equal("From the partial.", summaries.Find(typeof(Outer), "Other"));
        Assert.Null(summaries.Find(typeof(Outer), "Missing"));
    }

    // Names only: the summaries reader matches by type name.
    private sealed class Outer;

    private sealed class Inner;

    [Fact]
    public void Renderer_IsByteStable()
    {
        ConfigCatalogResult result = BuildFixture([new ConfigSection("Fixture", typeof(FixtureOptions))], new Dictionary<string, string> { ["FixtureOptions.Undocumented"] = "x" });
        var adHoc = new[] { new AdHocKey("A:B", "bool", "true", "Meaning.") };
        string a = ConfigReferenceRenderer.Render(result.Entries, adHoc, NoOverrides);
        string b = ConfigReferenceRenderer.Render(result.Entries, adHoc, NoOverrides);
        Assert.Equal(a, b);
        Assert.Contains("| `Fixture:Wait` | `TimeSpan` | `00:00:10` | - | doc. |", a, StringComparison.Ordinal);
        Assert.Contains("[`Fixture`](#fixture)", a, StringComparison.Ordinal);
    }

    private static ConfigCoverage Coverage(params string[] paths) => new(
        new Dictionary<string, string> { ["FixtureOptions"] = "Fixture" },
        new HashSet<string>(paths),
        new HashSet<string> { "FixtureOptions" },
        ["Logging"],
        new HashSet<string> { "HostOptions" });

    [Fact]
    public void Scan_FlagsEveryUncoveredBindingStyle_AndAcceptsTheCoveredOnes()
    {
        string[] bad =
        [
            "x.GetSection(\"Unknown\").Bind(o);",
            "x.GetSection(Missing.SectionName).Bind(o);",
            "x.GetSection(FixtureOptions.SectionName + \":Sub\").Bind(o);",
            "services.Configure<UnknownOptions>(c);",
            "var v = configuration[\"Nope:Key\"];",
            "public const string ConfigKey = \"Nope:Other\";",
            "x.GetSection(SectionName).Bind(o);",
            "x.GetSection(Compute(1)).Bind(o);",
        ];
        foreach (string line in bad)
        {
            IReadOnlyList<string> problems = ConfigSourceScan.FindUncovered([("f.cs", "class Plain {" + line + "}")], Coverage("Fixture:Flag"));
            Assert.True(problems.Count > 0, "should have been flagged: " + line);
        }

        string[] good =
        [
            "x.GetSection(\"Fixture\").Bind(o);",
            "x.GetSection(FixtureOptions.SectionName).Bind(o);",
            "x.GetSection(FixtureOptions.SectionName + \":Sub\").Bind(o);",
            "services.Configure<FixtureOptions>(c);",
            "services.Configure<HostOptions>(c);",
            "var v = configuration[\"Fixture:Flag\"];",
            "var v = configuration[\"Logging:Anything\"];",
            "x.GetSection(section).Exists();",
        ];
        foreach (string line in good)
        {
            IReadOnlyList<string> problems = ConfigSourceScan.FindUncovered([("f.cs", "class Plain {" + line + "}")], Coverage("Fixture:Flag", "Fixture:Sub:Deep"));
            Assert.True(problems.Count == 0, "should have been accepted: " + line + " -> " + string.Join("; ", problems));
        }

        Assert.Empty(ConfigSourceScan.FindUncovered([("f.cs", "class FixtureOptions { const string SectionName = \"Fixture\"; void A() { x.GetSection(SectionName); } }")], Coverage("Fixture:Flag")));
    }

    // ------------------------------------------------------------------ production

    private static ConfigCatalogResult Production => ConfigProduction.Catalog;

    [Fact]
    public void Production_CatalogIsCompleteAndWellFormed()
    {
        Assert.True(Production.Problems.Count == 0, string.Join("\n", Production.Problems));
        Assert.True(Production.Entries.Count > 300, "the catalog must see the options classes; it found " + Production.Entries.Count);
        Assert.Contains(Production.Entries, e => e.Path == "World:GmCommands:SecurityMap" && e.Default.Contains("Administrator", StringComparison.Ordinal));
        Assert.Contains(Production.Entries, e => e.Path == "Database:Auth:ConnectionString");
        Assert.Contains(Production.Entries, e => e.Path == "Creatures:EventAi" || e.Path.StartsWith("Creatures:EventAi:", StringComparison.Ordinal));
        Assert.Contains(Production.Entries, e => e.Path == "World:HotCode:Modules:Enabled");
    }

    [Fact]
    public void Production_DefaultsDoNotDependOnTheMachine()
    {
        foreach (ConfigEntry entry in Production.Entries)
        {
            Assert.DoesNotContain(":\\", entry.Default, StringComparison.Ordinal);
            Assert.DoesNotContain("/home/", entry.Default, StringComparison.Ordinal);
            Assert.DoesNotContain("/tmp", entry.Default, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Production_PerfAliasIsExcludedWithAReason_AndPerformanceLogIsDocumented()
    {
        Assert.True(ConfigExceptionTable.SkippedProperties.TryGetValue("WorldRuntimeOptions.Perf", out string? reason));
        Assert.Contains("alias", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Production.Entries, e => e.Path.StartsWith("World:Perf", StringComparison.Ordinal));
        Assert.Contains(Production.Entries, e => e.Path == "PerformanceLog:SlowWorldUpdate");
    }

    [Fact]
    public void Production_EveryReloadClassifiedKey_IsInTheCatalog()
    {
        HashSet<string> paths = [.. Production.Entries.Select(e => e.Path)];
        foreach (WorldConfigKey key in WorldConfigKeys.All)
        {
            Assert.Contains(key.Path, paths);
            Assert.NotEqual("-", Production.Entries.Single(e => e.Path == key.Path).Reload);
        }
    }

    [Fact]
    public void Production_EveryKeyOfTheShippedAppsettingsFiles_ResolvesToACatalogEntry()
    {
        string root = RepoRoot.Find();
        string[] files =
        [
            .. Directory.GetDirectories(Path.Combine(root, "src")).Concat(Directory.GetDirectories(Path.Combine(root, "tools")))
                .Select(d => Path.Combine(d, "appsettings.json")).Where(File.Exists),
        ];
        Assert.True(files.Length >= 5, "expected the five shipped appsettings files, found " + files.Length);

        var unresolved = new List<string>();
        foreach (string file in files)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            foreach (string key in Leaves(document.RootElement, string.Empty))
            {
                if (!Resolves(key))
                {
                    unresolved.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + ": " + key);
                }
            }
        }

        Assert.True(unresolved.Count == 0, "shipped appsettings keys with no catalog entry:\n" + string.Join("\n", unresolved));
    }

    private static IEnumerable<string> Leaves(JsonElement element, string prefix)
    {
        if (element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any())
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                foreach (string leaf in Leaves(property.Value, prefix.Length == 0 ? property.Name : prefix + ":" + property.Name))
                {
                    yield return leaf;
                }
            }
        }
        else
        {
            yield return prefix;
        }
    }

    private static bool Resolves(string key)
    {
        if (ConfigExceptionTable.FrameworkSections.Contains(key.Split(':')[0], StringComparer.Ordinal))
        {
            return true;
        }

        if (ConfigExceptionTable.AdHocKeys.Any(a => a.Path == key))
        {
            return true;
        }

        return Production.Entries.Any(e => e.Path == key
            || (key.StartsWith(e.Path + ":", StringComparison.Ordinal)
                && (e.TypeText.StartsWith("Dictionary<", StringComparison.Ordinal) || e.TypeText.StartsWith("List<", StringComparison.Ordinal) || e.TypeText.EndsWith("[]", StringComparison.Ordinal))));
    }

    [Fact]
    public void Production_EveryBindingInTheSource_IsCoveredByTheCatalogOrTheExceptionTable()
    {
        HashSet<string> paths = [.. Production.Entries.Select(e => e.Path), .. ConfigExceptionTable.AdHocKeys.Select(a => a.Path)];
        HashSet<string> types = [.. ConfigExceptionTable.Sections().Select(s => s.Type.Name)];
        var coverage = new ConfigCoverage(ConfigExceptionTable.DiscoverHolders(), paths, types, ConfigExceptionTable.FrameworkSections, ConfigExceptionTable.FrameworkTypes);
        var sources = ConfigProduction.SourceFiles().Where(f => !f.Path.StartsWith("tools/ArcaneCore.MockClient/", StringComparison.Ordinal)).ToList();
        Assert.True(sources.Count > 500, "the scan must see the source tree; it found " + sources.Count + " files");
        IReadOnlyList<string> problems = ConfigSourceScan.FindUncovered(sources, coverage);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Production_ConfigurationReference_MatchesTheCommittedPage()
    {
        string page = ConfigReferenceRenderer.Render(Production.Entries, ConfigExceptionTable.AdHocKeys, ConfigExceptionTable.SkippedProperties);
        DocsGolden.Verify("docs/reference/configuration.md", page);
    }
}
