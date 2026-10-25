using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

public sealed class ConfWriterTests : IDisposable
{
    private readonly ConfCatalog _catalog = ConfFixtureCatalog.Create();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-confw-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfWriterTests()
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

    private static IConfigurationRoot Memory(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void WriteDefaults_SectionIndexListsNamesOnly_WithoutPerSectionKeyCounts()
    {
        string text = ConfWriter.WriteDefaults(_catalog, ConfigDaemon.World);
        int start = text.IndexOf("# SECTIONS\n#\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "the index header is missing");
        int end = text.IndexOf("\n#\n", start + "# SECTIONS\n#\n".Length, StringComparison.Ordinal);
        string index = text[start..end];
        Assert.Contains("\n#   FIXTURE", index, StringComparison.Ordinal);
        Assert.DoesNotContain(" keys", index, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d", index);
    }

    [Fact]
    public void WriteDefaults_HasSectionBannersAndACommentBlockPerKey()
    {
        string text = ConfWriter.WriteDefaults(_catalog, ConfigDaemon.World);
        Assert.Contains("# FIXTURE", text, StringComparison.Ordinal);
        Assert.Contains("# STARTUP", text, StringComparison.Ordinal);
        Assert.Contains("#----", text, StringComparison.Ordinal);
        Assert.Contains("#    Fixture.Port\n", text, StringComparison.Ordinal);
        Assert.Contains("#        Description: Meaning of Fixture:Port.\n", text, StringComparison.Ordinal);
        Assert.Contains("#        Type: int\n", text, StringComparison.Ordinal);
        Assert.Contains("#        Default: 8085\n", text, StringComparison.Ordinal);
        Assert.Contains("#        Allowed: a whole number, -2147483648 to 2147483647\n", text, StringComparison.Ordinal);
        Assert.Contains("#        Reload: Live", text, StringComparison.Ordinal);
        Assert.Contains("#        Reload: Restart", text, StringComparison.Ordinal);
        Assert.Contains("#        Reload: Start-up", text, StringComparison.Ordinal);
        Assert.Contains("#        Docs: docs/reference/configuration.md#fixture\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Port = 8085\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Name = \"ArcaneCore\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Enabled = true\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Mode = Fast\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Wait = 00:00:10\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Map.a = 1\nFixture.Map.b = 2\n", text, StringComparison.Ordinal);
        Assert.Contains("\nDatabase.ConnectionString = \"\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\nLogging.LogLevel.Default = \"Information\"\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth.Port", text, StringComparison.Ordinal);

        string realm = ConfWriter.WriteDefaults(_catalog, ConfigDaemon.Realm);
        Assert.Contains("\nAuth.Port = 3724\n", realm, StringComparison.Ordinal);
        Assert.Contains("\nStartup.Strict = false\n", realm, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture.Port", realm, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDefaults_SequenceWithDefaultEntries_IsCommentedWithTheAppendNote()
    {
        string text = ConfWriter.WriteDefaults(_catalog, ConfigDaemon.World);
        Assert.Contains("\n# Fixture.List.0 = \"x\"\n# Fixture.List.1 = \"y\"\n", text, StringComparison.Ordinal);
        Assert.Contains("appended to the default list", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFixture.List.0", text, StringComparison.Ordinal);
        Assert.Contains("Each entry is a group of keys: Fixture.Items.0.<Field>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDefaults_NullDefault_IsCommentedOut()
    {
        string text = ConfWriter.WriteDefaults(_catalog, ConfigDaemon.World);
        Assert.Contains("\n# Fixture.Path =\n", text, StringComparison.Ordinal);
        Assert.Contains("#        Default: (not set)\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFixture.Path", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDefaults_IsByteStable_AndLf()
    {
        string a = ConfWriter.WriteDefaults(_catalog, ConfigDaemon.World);
        string b = ConfWriter.WriteDefaults(ConfCatalog.FromJson(_catalog.ToJson()), ConfigDaemon.World);
        Assert.Equal(a, b);
        Assert.DoesNotContain('\r', a);
        Assert.EndsWith("\n", a, StringComparison.Ordinal);
        Assert.All(a.Split('\n'), line => Assert.True(line.Length <= 200, "line too long: " + line));
    }

    [Fact]
    public void WriteDefaults_ParsesCleanly_AndRoundTripsThroughTheCatalogJson()
    {
        ConfDocument document = ConfDocument.ParseText(ConfWriter.WriteDefaults(_catalog, ConfigDaemon.World), "world.conf.dist");
        Assert.Contains(document.Entries, e => e.Path == "Fixture:Port" && e.Value == "8085");
        Assert.Equal(_catalog.Keys.Count, ConfCatalog.FromJson(_catalog.ToJson()).Keys.Count);
    }

    [Fact]
    public void WriteCurrent_MarksOnlyChangedKeys_WithTheDefault()
    {
        IConfigurationRoot root = Memory(("Fixture:Port", "9000"), ("Fixture:Enabled", "TRUE"), ("Fixture:Name", "Other"), ("Fixture:Ratio", "0.10"));
        string text = ConfWriter.WriteCurrent(_catalog, ConfigDaemon.World, root);
        Assert.Contains("# changed from default: 8085\nFixture.Port = 9000\n", text, StringComparison.Ordinal);
        Assert.Contains("# changed from default: \"ArcaneCore\"\nFixture.Name = \"Other\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Enabled = TRUE\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Ratio = 0.10\n", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("# changed from default:").Length - 1);
        Assert.Contains("\nFixture.Mode = Fast\n", text, StringComparison.Ordinal); // unconfigured keys are explicit too
    }

    [Fact]
    public void WriteCurrent_ParseOfWrite_EqualsTheConfiguration()
    {
        IConfigurationRoot root = Memory(
            ("Fixture:Port", "-5"),
            ("Fixture:Name", "a \"b\" # c ; C:\\dir\\"),
            ("Fixture:Mode", "off"),
            ("Fixture:Path", "x y"),
            ("Fixture:Map:c", "7"),
            ("Fixture:Map:a", "5"),
            ("Fixture:List:0", "first"),
            ("Fixture:List:1", "second"),
            ("Fixture:List:10", "tenth"),
            ("Fixture:Items:0:Name", "N0"),
            ("Fixture:Items:0:Population", "0.5"),
            ("Fixture:Items:1:Name", "N1"));
        string text = ConfWriter.WriteCurrent(_catalog, ConfigDaemon.World, root);
        string path = Path.Combine(_dir, "world.conf");
        File.WriteAllText(path, text);
        IConfigurationRoot reread = new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = path }).Build();

        foreach (ConfKey key in _catalog.KeysFor(ConfigDaemon.World))
        {
            if (key.IsCollection)
            {
                Assert.Equal(Flatten(root, key.Path, key), Flatten(reread, key.Path, key));
                continue;
            }

            string? expected = root[key.Path] ?? key.Default;
            Assert.Equal(expected, reread[key.Path]);
        }

        Assert.True(text.IndexOf("Fixture.List.2", StringComparison.Ordinal) < 0);
        Assert.True(text.IndexOf("Fixture.List.1 =", StringComparison.Ordinal) < text.IndexOf("Fixture.List.10 =", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Flatten(IConfigurationRoot root, string path, ConfKey key)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IConfigurationSection section)
        {
            foreach (IConfigurationSection child in section.GetChildren())
            {
                if (child.Value is not null)
                {
                    result[child.Path] = child.Value;
                }

                Walk(child);
            }
        }

        if (key.Kind == ConfKey.Map)
        {
            foreach (ConfDefaultEntry entry in key.DefaultEntries)
            {
                result[path + ":" + entry.Key] = entry.Value;
            }
        }

        Walk(root.GetSection(path));
        return result;
    }

    [Fact]
    public void WriteCurrent_OtherDaemonKeysSkippedWithACount_UnknownFileKeysListedCommented_EnvironmentNoiseIgnored()
    {
        string json = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(json, """
            { "Auth": { "Port": 1234 }, "Mistyped": { "Key": "v" }, "Fixture": { "Port": 77 },
              "Logging": { "LogLevel": { "Default": "Warning", "Microsoft.EntityFrameworkCore": "Warning" } } }
            """);
        IConfigurationRoot root = new ConfigurationBuilder()
            .AddJsonFile(json)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Noise:FromMemory"] = "x", ["Fixture:Name"] = "env" })
            .Build();
        string text = ConfWriter.WriteCurrent(_catalog, ConfigDaemon.World, root);

        Assert.Contains("1 key(s) found in the profile's files belong to realm.conf", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth.Port = 1234", text, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN KEYS", text, StringComparison.Ordinal);
        Assert.Contains("\n# Mistyped.Key = \"v\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\nLogging:LogLevel:Microsoft.EntityFrameworkCore = \"Warning\"\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Noise", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Port = 77\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFixture.Name = \"env\"\n", text, StringComparison.Ordinal); // an override of a catalog key is part of the effective configuration

        ConfDocument document = ConfDocument.ParseText(text, "world.conf");
        Assert.Contains(document.Entries, e => e.Path == "Logging:LogLevel:Microsoft.EntityFrameworkCore" && e.Value == "Warning");
    }
}
