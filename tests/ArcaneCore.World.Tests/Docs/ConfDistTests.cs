using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// The shipped <c>world.conf.dist</c> / <c>realm.conf.dist</c> and the embedded catalog are GENERATED from the options classes
/// (<see cref="ConfigProduction.Catalog"/> through <see cref="ConfCatalogGenerator"/> and <see cref="ConfWriter"/>), never hand-listed, so a
/// key that a lane adds to an options class reaches the .conf files by running
/// <c>ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs</c>, and CI fails until that has been done.
/// Contract with the generator: <c>ConfCatalogGenerator.Build(IEnumerable&lt;ConfigEntry&gt;)</c> returns the <see cref="ConfCatalog"/>.
/// </summary>
public sealed class ConfDistTests : IDisposable
{
    private const string CatalogFile = "src/ArcaneCore.Kernel/Configuration/Conf/conf-catalog.json";
    private const string WorldDist = "src/ArcaneCore.World/world.conf.dist";
    private const string RealmDist = "src/ArcaneCore.Realm/realm.conf.dist";
    private const string Guide = "docs/guide/configuration.md";

    private static readonly ConfigDaemon[] Daemons = [ConfigDaemon.World, ConfigDaemon.Realm];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-confdist-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfDistTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late watcher handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private static ConfCatalog Generated => ConfCatalogGenerator.Build(ConfigProduction.Catalog.Entries, ConfigExceptionTable.AdHocKeys);

    private static string DistPath(ConfigDaemon daemon) => daemon == ConfigDaemon.Realm ? RealmDist : WorldDist;

    private static string Committed(string relativePath) => DocsGolden.Normalize(RepoRoot.ReadText(relativePath));

    private static ConfigDaemon DaemonsOf(Type type) => type.GetCustomAttributes(typeof(ConfigDaemonsAttribute), inherit: false)
        .Cast<ConfigDaemonsAttribute>().SingleOrDefault()?.Daemons ?? ConfigDaemon.World;

    private static IConfigurationRoot LoadConf(string path) => new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = path }).Build();

    [Fact]
    public void ConfCatalog_MatchesTheCommittedEmbeddedCatalog()
    {
        ConfCatalog generated = Generated;
        Assert.True(generated.Keys.Count > 300, "the generated catalog must see the options classes; it has " + generated.Keys.Count + " keys");
        DocsGolden.Verify(CatalogFile, generated.ToJson());
    }

    [Fact]
    public void WorldConfDist_MatchesTheCommittedFile()
        => DocsGolden.Verify(WorldDist, ConfWriter.WriteDefaults(Generated, ConfigDaemon.World));

    [Fact]
    public void RealmConfDist_MatchesTheCommittedFile()
        => DocsGolden.Verify(RealmDist, ConfWriter.WriteDefaults(Generated, ConfigDaemon.Realm));

    [Fact]
    public void EveryCatalogKey_AppearsInExactlyTheDistsOfItsDaemons()
    {
        var headers = new Dictionary<ConfigDaemon, HashSet<string>>();
        foreach (ConfigDaemon daemon in Daemons)
        {
            headers[daemon] = [.. Regex.Matches(Committed(DistPath(daemon)), @"^#    (\S+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
            Assert.True(headers[daemon].Count > 50, DistPath(daemon) + " lists only " + headers[daemon].Count + " keys");
        }

        var problems = new List<string>();
        foreach (ConfigEntry entry in ConfigProduction.Catalog.Entries)
        {
            string display = ConfCatalog.Display(entry.Path);
            foreach (ConfigDaemon daemon in Daemons)
            {
                bool expected = (entry.Daemons & daemon) != 0;
                if (headers[daemon].Contains(display) != expected)
                {
                    problems.Add($"{entry.Path}: {(expected ? "missing from" : "must not be in")} {DistPath(daemon)} (declared for {entry.Daemons})");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)) + "\nRegenerate: " + DocsGolden.RegenerateCommand);
    }

    [Fact]
    public void EveryAdHocKey_AppearsInExactlyTheDistsOfItsDaemons_AndIsInTheEmbeddedCatalog()
    {
        var problems = new List<string>();
        foreach (AdHocKey key in ConfigExceptionTable.AdHocKeys)
        {
            string display = ConfCatalog.Display(key.Path);
            foreach (ConfigDaemon daemon in Daemons)
            {
                bool expected = (key.Daemons & daemon) != 0;
                bool listed = Regex.IsMatch(Committed(DistPath(daemon)), @"^#    " + Regex.Escape(display) + "$", RegexOptions.Multiline);
                if (listed != expected)
                {
                    problems.Add($"{key.Path}: {(expected ? "missing from" : "must not be in")} {DistPath(daemon)} (declared for {key.Daemons})");
                }

                ConfKeyClass classification = ConfCatalog.Embedded.Classify(key.Path, daemon);
                ConfKeyStatus wanted = expected ? ConfKeyStatus.Known : ConfKeyStatus.OtherDaemon;
                if (classification.Status != wanted)
                {
                    problems.Add($"{key.Path}: the embedded catalog classifies it as {classification.Status} for the {daemon}, expected {wanted}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\nRegenerate: " + DocsGolden.RegenerateCommand);
    }

    [Fact]
    public void ProductionCatalog_AcceptsTheKeysReadByName_InAConf_WithoutIssues()
    {
        ConfCatalog catalog = ConfCatalog.Embedded;
        string path = Path.Combine(_dir, "world.conf");
        File.WriteAllText(path, "Startup.Strict = true\nSpells.RequireSpellFocus = false\nCrafting.Enabled = false\nForms.ResetFistAttackTimeOnFormLoss = true\n");
        Assert.Empty(new ConfKeyCheck(catalog, ConfigDaemon.World).Check(LoadConf(path)).Select(i => i.ToString()));

        string realm = Path.Combine(_dir, "realm.conf");
        File.WriteAllText(realm, "Startup.Strict = true\n");
        Assert.Empty(new ConfKeyCheck(catalog, ConfigDaemon.Realm).Check(LoadConf(realm)).Select(i => i.ToString()));
    }

    [Fact]
    public void EmbeddedCatalog_EqualsTheGeneratedCatalog()
    {
        // The compiled resource, not the file: a regenerated catalog.json that was not rebuilt into the assembly makes the daemons check
        // and generate from a stale key list.
        Assert.Equal(Generated.ToJson(), ConfCatalog.Embedded.ToJson());
    }

    [Fact]
    public void WriteConfigDefaultsVerb_EqualsTheCommittedDist_ForBothDaemons()
    {
        ConfCatalog catalog = ConfCatalog.Embedded;
        foreach (ConfigDaemon daemon in Daemons)
        {
            DaemonCommand toStdout = DaemonCommandLine.Parse(["write-config", "--defaults", "-"], daemon, catalog);
            Assert.Equal(DaemonCommandKind.Verb, toStdout.Kind);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            IConfigurationRoot empty = new ConfigurationBuilder().Build();
            Assert.Equal(0, WriteConfigVerb.Run(toStdout, daemon, catalog, empty, stdout, stderr));
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Equal(Committed(DistPath(daemon)), DocsGolden.Normalize(stdout.ToString()));

            string file = Path.Combine(_dir, ConfCatalog.FileName(daemon));
            DaemonCommand toFile = DaemonCommandLine.Parse(["write-config", "--defaults", file], daemon, catalog);
            Assert.Equal(0, WriteConfigVerb.Run(toFile, daemon, catalog, empty, new StringWriter(), new StringWriter()));
            Assert.Equal(Committed(DistPath(daemon)), DocsGolden.Normalize(File.ReadAllText(file)));
        }
    }

    [Fact]
    public void ShippedDists_BindEveryOptionsSection_WithoutAConversionError()
    {
        foreach (ConfigDaemon daemon in Daemons)
        {
            IConfigurationRoot root = LoadConf(Path.Combine(RepoRoot.Find(), DistPath(daemon).Replace('/', Path.DirectorySeparatorChar)));
            ConfigSection[] sections = [.. ConfigExceptionTable.Sections().Where(s => (DaemonsOf(s.Type) & daemon) != 0)];
            Assert.True(sections.Length > 5, "the " + daemon + " must bind several options sections; found " + sections.Length);
            foreach (ConfigSection section in sections)
            {
                Exception? failure = Record.Exception(() => root.GetSection(section.Path).Get(section.Type));
                Assert.True(failure is null, $"{DistPath(daemon)} does not bind section {section.Path} to {section.Type.Name}: {failure?.Message}");
            }
        }
    }

    [Fact]
    public void ShippedDists_ValuesEqualTheCatalogDefaults()
    {
        ConfCatalog catalog = Generated;
        foreach (ConfigDaemon daemon in Daemons)
        {
            IConfigurationRoot root = LoadConf(Path.Combine(RepoRoot.Find(), DistPath(daemon).Replace('/', Path.DirectorySeparatorChar)));
            foreach (ConfKey key in catalog.KeysFor(daemon).Where(k => !k.IsCollection && k.Default is not null))
            {
                string? value = root[key.Path];
                Assert.True(value is not null, $"{DistPath(daemon)} does not set {key.Path} to its default {key.Default}");
                Assert.True(ConfValue.Equivalent(value, key.Default!, key.Type, key.EnumValues), $"{key.Path}: the dist has '{value}', the default is '{key.Default}'");
                Assert.Null(ConfValue.Validate(value, key.Type, key.EnumValues));
            }
        }
    }

    [Fact]
    public void ShippedDists_RaiseNoConfKeyCheckIssues()
    {
        ConfCatalog catalog = ConfCatalog.Embedded;
        foreach (ConfigDaemon daemon in Daemons)
        {
            IConfigurationRoot root = LoadConf(Path.Combine(RepoRoot.Find(), DistPath(daemon).Replace('/', Path.DirectorySeparatorChar)));
            var issues = new ConfKeyCheck(catalog, daemon).Check(root).ToList();
            Assert.True(issues.Count == 0, DistPath(daemon) + " raises: " + string.Join("; ", issues.Select(i => i.ToString())));
        }

        // The control: the same check does fire on a key that is not in the catalog (it is not vacuous).
        string path = Path.Combine(_dir, "world.conf");
        File.WriteAllText(path, "World.Prot = 1\n");
        Assert.NotEmpty(new ConfKeyCheck(catalog, ConfigDaemon.World).Check(LoadConf(path)));
    }

    [Fact]
    public void WriteCurrent_ParseRoundTrip_ForEveryCatalogKey_BothDaemons()
    {
        ConfCatalog catalog = Generated;
        foreach (ConfigDaemon daemon in Daemons)
        {
            var values = new Dictionary<string, string?>();
            var expected = new List<(ConfKey Key, string Path, string Value)>();
            foreach (ConfKey key in catalog.KeysFor(daemon))
            {
                if (key.IsCollection)
                {
                    foreach (ConfDefaultEntry entry in key.DefaultEntries)
                    {
                        values[key.Path + ":" + entry.Key] = entry.Value;
                        expected.Add((key, key.Path + ":" + entry.Key, entry.Value));
                    }
                }
                else if (key.Default is not null)
                {
                    values[key.Path] = key.Default;
                    expected.Add((key, key.Path, key.Default));
                }
            }

            // The world daemon reads several hundred defaults; the realm binds only auth, logon, console, net protection, watchdog, database, logging and diagnostics.
            int minimum = daemon == ConfigDaemon.World ? 200 : 50;
            Assert.True(expected.Count > minimum, $"expected the defaults of every {daemon} key; found {expected.Count}");
            IConfigurationRoot current = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            string file = Path.Combine(_dir, "current-" + ConfCatalog.FileName(daemon));
            File.WriteAllText(file, ConfWriter.WriteCurrent(catalog, daemon, current));
            IConfigurationRoot reread = LoadConf(file);

            foreach ((ConfKey key, string path, string value) in expected)
            {
                string? actual = reread[path];
                string type = key.IsCollection ? key.ElementType : key.Type;
                Assert.True(actual is not null && ConfValue.Equivalent(value, actual, type, key.EnumValues), $"{daemon} {path}: wrote '{value}', read back '{actual}'");
            }
        }
    }

    [Fact]
    public void ShippedAppsettings_MigrateThroughWriteCurrent_Losslessly()
    {
        ConfCatalog catalog = Generated;
        (ConfigDaemon Daemon, string Json)[] profiles =
        [
            (ConfigDaemon.World, "src/ArcaneCore.World/appsettings.json"),
            (ConfigDaemon.Realm, "src/ArcaneCore.Realm/appsettings.json"),
        ];
        foreach ((ConfigDaemon daemon, string json) in profiles)
        {
            IConfigurationRoot original = new ConfigurationBuilder().AddJsonFile(Path.Combine(RepoRoot.Find(), json.Replace('/', Path.DirectorySeparatorChar))).Build();
            string file = Path.Combine(_dir, "migrated-" + ConfCatalog.FileName(daemon));

            // Secrets are masked by default, so a lossless migration is the explicit opt-in (--include-secrets, a file target only).
            File.WriteAllText(file, ConfWriter.WriteCurrent(catalog, daemon, original, includeSecrets: true));
            IConfigurationRoot migrated = LoadConf(file);

            int compared = 0;
            foreach (KeyValuePair<string, string?> pair in original.AsEnumerable().Where(p => p.Value is not null))
            {
                ConfKeyClass classification = catalog.Classify(pair.Key, daemon);
                Assert.True(classification.Status != ConfKeyStatus.Unknown, $"{json}: {pair.Key} is not a key of the catalog");
                if (classification.Status is ConfKeyStatus.OtherDaemon or ConfKeyStatus.Section)
                {
                    continue;
                }

                string type = classification.Key is null ? "string" : classification.Key.IsCollection ? classification.Key.ElementType : classification.Key.Type;
                string? actual = migrated[pair.Key];
                Assert.True(actual is not null && ConfValue.Equivalent(pair.Value!, actual, type, classification.Key?.EnumValues),
                    $"{json}: {pair.Key} was '{pair.Value}' in appsettings.json and '{actual}' after write-config --current");
                compared++;
            }

            Assert.True(compared >= 10, json + ": only " + compared + " values were compared");
        }
    }

    [Fact]
    public void WriteCurrent_MasksPasswordsByDefault_AndOnlyTheOptInWritesThem()
    {
        ConfCatalog catalog = Generated;
        string json = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(json, """
            { "Database": {
                "ConnectionString": "Server=db;Port=3306;User=arc;Password=hunter2;Database=main",
                "Auth":       { "ConnectionString": "Server=db;User=arc;Pwd=s3cret-auth;Database=auth" },
                "Characters": { "ConnectionString": "Server=db;User=arc;Password=\"quo;ted-chars\";Database=chars" },
                "World":      { "ConnectionString": "Server=db;User=arc;password = spaced-world ;Database=world" } },
              "Mistyped": { "Password": "unknown-secret-1", "Dsn": "Host=a;Password=unknown-secret-2;Port=1" } }
            """);
        string[] secrets = ["hunter2", "s3cret-auth", "quo;ted-chars", "ted-chars", "spaced-world", "unknown-secret-1", "unknown-secret-2"];
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile(json).Build();

        foreach (ConfigDaemon daemon in Daemons)
        {
            string masked = ConfWriter.WriteCurrent(catalog, daemon, configuration);
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
            }

            Assert.Contains("Password=" + ConfSecrets.Placeholder, masked, StringComparison.Ordinal);
            Assert.Contains("Server=db", masked, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"changed from default[^\n]*\n\s*Database\.[A-Za-z.]*ConnectionString", masked);

            // The control: the opt-in does write the same values, so the absence above is the masking and not a missing key.
            string unmasked = ConfWriter.WriteCurrent(catalog, daemon, configuration, includeSecrets: true);
            Assert.Contains("User=arc;Pwd=s3cret-auth", unmasked.Replace("\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("unknown-secret-1", unmasked, StringComparison.Ordinal);
            Assert.Contains("unknown-secret-2", unmasked, StringComparison.Ordinal);
        }

        Assert.Contains("hunter2", ConfWriter.WriteCurrent(catalog, ConfigDaemon.World, configuration, includeSecrets: true), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConfigVerb_IncludeSecrets_RefusesStandardOutputAndDefaults_AndMasksOtherwise()
    {
        ConfCatalog catalog = Generated;
        Assert.Equal(DaemonCommandKind.UsageError, DaemonCommandLine.Parse(["write-config", "--current", "-", "--include-secrets"], ConfigDaemon.World, catalog).Kind);
        Assert.Equal(DaemonCommandKind.UsageError, DaemonCommandLine.Parse(["write-config", "--defaults", "x.conf", "--include-secrets"], ConfigDaemon.World, catalog).Kind);
        DaemonCommand allowed = DaemonCommandLine.Parse(["write-config", "--current", "x.conf", "--include-secrets"], ConfigDaemon.World, catalog);
        Assert.Equal(DaemonCommandKind.Verb, allowed.Kind);
        Assert.True(allowed.IncludeSecrets);
        Assert.False(DaemonCommandLine.Parse(["write-config", "--current", "-"], ConfigDaemon.World, catalog).IncludeSecrets);

        // The verb itself also refuses a hand-built command that asks for secrets on standard output.
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = "Server=a;Password=pw-in-memory" }).Build();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = WriteConfigVerb.Run(allowed with { OutputPath = "-" }, ConfigDaemon.World, catalog, configuration, stdout, stderr);
        Assert.Equal(ArcaneCore.Kernel.Ops.ExitCodes.Usage, code);
        Assert.DoesNotContain("pw-in-memory", stdout.ToString() + stderr, StringComparison.Ordinal);

        var masked = new StringWriter();
        Assert.Equal(0, WriteConfigVerb.Run(DaemonCommandLine.Parse(["write-config", "--current", "-"], ConfigDaemon.World, catalog), ConfigDaemon.World, catalog, configuration, masked, new StringWriter()));
        Assert.DoesNotContain("pw-in-memory", masked.ToString(), StringComparison.Ordinal);
        Assert.Contains("Password=" + ConfSecrets.Placeholder, masked.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EverySectionTheRealmBinds_IsMarkedForTheRealm_AndNoWorldOnlySectionIs()
    {
        string root = RepoRoot.Find();
        HashSet<string> realmAssemblies = ReferencedAssemblies(root, "src/ArcaneCore.Realm/ArcaneCore.Realm.csproj");
        Assert.Contains("ArcaneCore.Kernel", realmAssemblies);
        Assert.DoesNotContain("ArcaneCore.World", realmAssemblies);

        // A section the Realm can not even load (its options type lives in an assembly the Realm does not reference) must not claim the Realm.
        foreach (ConfigSection section in ConfigExceptionTable.Sections())
        {
            if ((DaemonsOf(section.Type) & ConfigDaemon.Realm) != 0)
            {
                Assert.True(realmAssemblies.Contains(section.Type.Assembly.GetName().Name!), $"{section.Path} ({section.Type.Name}) is marked for the realm but its type is in {section.Type.Assembly.GetName().Name}, which the Realm does not reference");
            }
        }

        // The other direction: an options class the Realm's RealmProductionServices.cs (called by Program.cs) binds directly must be marked for the Realm, and so must each of its catalog keys.
        string program = File.ReadAllText(Path.Combine(root, "src", "ArcaneCore.Realm", "RealmProductionServices.cs"));
        string[] bound = [.. Regex.Matches(program, @"Configure<(\w+)>\(").Select(m => m.Groups[1].Value).Where(n => n != "HostOptions").Distinct()];
        Assert.True(bound.Length >= 3, "expected the Realm to bind several options classes; found " + bound.Length);
        foreach (string typeName in bound)
        {
            ConfigSection section = ConfigExceptionTable.Sections().Single(s => s.Type.Name == typeName);
            Assert.True((DaemonsOf(section.Type) & ConfigDaemon.Realm) != 0, $"{typeName} is bound by the Realm's RealmProductionServices.cs but is not marked [ConfigDaemons] for the Realm");
            foreach (ConfigEntry entry in ConfigProduction.Catalog.Entries.Where(e => e.Path == section.Path || e.Path.StartsWith(section.Path + ":", StringComparison.Ordinal)))
            {
                Assert.True((entry.Daemons & ConfigDaemon.Realm) != 0, $"{entry.Path} is read by the Realm ({typeName}) but the catalog does not list it for the Realm");
            }
        }
    }

    private static HashSet<string> ReferencedAssemblies(string root, string csproj)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.Combine(root, csproj.Replace('/', Path.DirectorySeparatorChar))]);
        while (pending.Count > 0)
        {
            string project = pending.Pop();
            if (!result.Add(Path.GetFileNameWithoutExtension(project)))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(File.ReadAllText(project), @"ProjectReference\s+Include=""([^""]+)"""))
            {
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        return result;
    }

    [Fact]
    public void ConfigurationGuide_NamesTheVerbsOptionsFilesAndPrecedenceTheCodeUses()
    {
        string guide = Committed(Guide);
        string[] required =
        [
            DaemonCommandLine.CheckConfig,
            DaemonCommandLine.WriteConfig,
            "--defaults",
            "--current",
            "--force",
            "--config",
            ConfFiles.DirectoryVariable,
            ConfCatalog.FileName(ConfigDaemon.World),
            ConfCatalog.FileName(ConfigDaemon.Realm),
            "world.conf.dist",
            "realm.conf.dist",
            "ARCANECORE_UPDATE_DOCS",
            "--help",
        ];
        foreach (string term in required)
        {
            Assert.Contains(term, guide, StringComparison.Ordinal);
        }

        // The documented exit codes are the ones the code returns.
        Assert.Contains(ArcaneCore.Kernel.Ops.ExitCodes.Usage.ToString(System.Globalization.CultureInfo.InvariantCulture), guide, StringComparison.Ordinal);
        Assert.Contains(ArcaneCore.Kernel.Ops.ExitCodes.InvalidConfiguration.ToString(System.Globalization.CultureInfo.InvariantCulture), guide, StringComparison.Ordinal);

        // The precedence line lists the sources in the order ConfFiles.Attach registers them.
        string[] order = ["appsettings.json", "world.conf", "environment variables", "command line"];
        bool listed = guide.Split('\n').Any(line =>
        {
            int at = -1;
            foreach (string source in order)
            {
                int next = line.IndexOf(source, at + 1, StringComparison.OrdinalIgnoreCase);
                if (next < 0)
                {
                    return false;
                }

                at = next;
            }

            return true;
        });
        Assert.True(listed, "the guide must have one line that lists " + string.Join(" < ", order) + " in that order");

        Assert.Empty(GuideFactChecker.UnknownKeys(guide, ConfigProduction.Catalog.Entries.Select(e => e.Path)));
        Assert.Empty(GuideFactChecker.MissingPaths(guide, RepoRoot.Find()));
    }
}
