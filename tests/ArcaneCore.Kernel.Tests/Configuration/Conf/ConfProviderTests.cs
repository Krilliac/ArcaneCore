using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

/// <summary>
/// Every test that reaches <see cref="ConfFiles.Attach"/> lives in this one class: xunit runs the methods of a class one after
/// another, and <c>ARCANECORE_CONFIG</c> is process-wide.
/// </summary>
public sealed class ConfProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-conf-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _previousVariable = Environment.GetEnvironmentVariable(ConfFiles.DirectoryVariable);

    public ConfProviderTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(ConfFiles.DirectoryVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfFiles.DirectoryVariable, _previousVariable);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late watcher handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private HostApplicationBuilder CreateBuilder(string[]? args = null, string? environment = null)
        => Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = _dir, EnvironmentName = environment });

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public void Provider_LoadsKeysIntoConfigurationPaths()
    {
        string path = Write("world.conf", "Fixture.Port = 9\nFixture.List.0 = a\nFixture.List.1 = b\n");
        IConfigurationRoot root = new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = path }).Build();
        Assert.Equal("9", root["Fixture:Port"]);
        Assert.Equal("9", root["fixture:port"]);
        Assert.Equal(["a", "b"], root.GetSection("Fixture:List").GetChildren().Select(c => c.Value));
        Assert.Equal(3, ((ConfConfigurationProvider)root.Providers.Single()).Document!.Entries.Count);
    }

    [Fact]
    public void Precedence_JsonBelowConfBelowEnvironmentBelowCommandLine()
    {
        Write("appsettings.json", """{ "ConfPrec": { "A": "json", "B": "json", "C": "json", "D": "json" } }""");
        Write("world.conf", "ConfPrec.B = conf\nConfPrec.C = conf\nConfPrec.D = conf\n");
        Environment.SetEnvironmentVariable("ConfPrec__C", "env");
        Environment.SetEnvironmentVariable("ConfPrec__D", "env");
        try
        {
            HostApplicationBuilder builder = CreateBuilder(["--ConfPrec:D=cmd"]);
            ConfAttachment attachment = ConfFiles.Attach(builder, ConfigDaemon.World, null);
            Assert.Equal(Path.Combine(_dir, "world.conf"), attachment.Path);
            Assert.Equal("json", builder.Configuration["ConfPrec:A"]);
            Assert.Equal("conf", builder.Configuration["ConfPrec:B"]);
            Assert.Equal("env", builder.Configuration["ConfPrec:C"]);
            Assert.Equal("cmd", builder.Configuration["ConfPrec:D"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConfPrec__C", null);
            Environment.SetEnvironmentVariable("ConfPrec__D", null);
        }
    }

    [Fact]
    public void Attach_InsertsAfterTheLastAppsettingsJsonSource()
    {
        Write("world.conf", "Fixture.Port = 9\n");
        HostApplicationBuilder builder = CreateBuilder(environment: "Staging");
        int lastJson = builder.Configuration.Sources.Select((s, i) => (s, i)).Last(p => p.s is JsonConfigurationSource { Path: { } json } && json.StartsWith("appsettings", StringComparison.Ordinal)).i;
        Assert.Equal("appsettings.Staging.json", ((JsonConfigurationSource)builder.Configuration.Sources[lastJson]).Path);

        ConfFiles.Attach(builder, ConfigDaemon.World, null);

        Assert.IsType<ConfConfigurationSource>(builder.Configuration.Sources[lastJson + 1]);
        Assert.Single(builder.Configuration.Sources.OfType<ConfConfigurationSource>());
        Assert.Equal("9", builder.Configuration["Fixture:Port"]); // the providers were rebuilt: the inserted source is live
    }

    [Fact]
    public void Attach_ExplicitPathMissing_FailsClosed_DefaultPathMissing_IsSkipped()
    {
        HostApplicationBuilder builder = CreateBuilder();
        Assert.Null(ConfFiles.Attach(builder, ConfigDaemon.World, null).Path);
        Assert.Empty(builder.Configuration.Sources.OfType<ConfConfigurationSource>());

        Assert.Throws<ConfLocationException>(() => ConfFiles.Attach(CreateBuilder(), ConfigDaemon.World, "nope.conf"));

        Write("custom.conf", "Fixture.Port = 5\n");
        HostApplicationBuilder explicitBuilder = CreateBuilder();
        Assert.Equal(Path.Combine(_dir, "custom.conf"), ConfFiles.Attach(explicitBuilder, ConfigDaemon.World, "custom.conf").Path);
        Assert.Equal("5", explicitBuilder.Configuration["Fixture:Port"]);

        Write("bad.conf", "Fixture.Port 5\n");
        Assert.Throws<ConfParseException>(() => ConfFiles.Attach(CreateBuilder(), ConfigDaemon.World, "bad.conf"));
    }

    [Fact]
    public void Attach_EnvironmentDirectory_SelectsTheDaemonsFile_AndAFilePathThereIsRejected()
    {
        string profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "world.conf"), "Fixture.Port = 1\n");
        File.WriteAllText(Path.Combine(profile, "realm.conf"), "Fixture.Port = 2\n");
        Environment.SetEnvironmentVariable(ConfFiles.DirectoryVariable, profile);

        Assert.Equal(Path.Combine(profile, "world.conf"), ConfFiles.Attach(CreateBuilder(), ConfigDaemon.World, null).Path);
        HostApplicationBuilder realm = CreateBuilder();
        Assert.Equal(Path.Combine(profile, "realm.conf"), ConfFiles.Attach(realm, ConfigDaemon.Realm, null).Path);
        Assert.Equal("2", realm.Configuration["Fixture:Port"]);

        Environment.SetEnvironmentVariable(ConfFiles.DirectoryVariable, Path.Combine(profile, "world.conf"));
        ConfLocationException ex = Assert.Throws<ConfLocationException>(() => ConfFiles.Attach(CreateBuilder(), ConfigDaemon.World, null));
        Assert.Contains("--config", ex.Message, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable(ConfFiles.DirectoryVariable, Path.Combine(_dir, "missing-dir"));
        Assert.Throws<ConfLocationException>(() => ConfFiles.Attach(CreateBuilder(), ConfigDaemon.World, null));
    }

    [Fact]
    public async Task ReloadOnChange_GoodEdit_UpdatesTheLiveRoot()
    {
        string path = Write("world.conf", "Fixture.Port = 1\n");
        IConfigurationRoot root = new ConfigurationBuilder()
            .Add(new ConfConfigurationSource { Path = path, ReloadOnChange = true, ReloadDelay = TimeSpan.FromMilliseconds(20) })
            .Build();
        try
        {
            Assert.Equal("1", root["Fixture:Port"]);
            File.WriteAllText(path, "Fixture.Port = 2\nFixture.Name = changed\n");
            Assert.True(await WaitFor(() => root["Fixture:Port"] == "2"), "the edit was not picked up");
            Assert.Equal("changed", root["Fixture:Name"]);
        }
        finally
        {
            (root as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task ReloadOnChange_MalformedEdit_KeepsTheLastGoodValues_AndReportsFileAndLine()
    {
        string path = Write("world.conf", "Fixture.Port = 1\n");
        var errors = new List<string>();
        IConfigurationRoot root = new ConfigurationBuilder()
            .Add(new ConfConfigurationSource { Path = path, ReloadOnChange = true, ReloadDelay = TimeSpan.FromMilliseconds(20), ReloadError = m => { lock (errors) { errors.Add(m); } } })
            .Build();
        try
        {
            File.WriteAllText(path, "Fixture.Port = 7\nthis line is broken\n");
            Assert.True(await WaitFor(() => { lock (errors) { return errors.Count > 0; } }), "the bad edit was not reported");
            Assert.Contains("world.conf:2:", errors[0], StringComparison.Ordinal);
            Assert.Equal("1", root["Fixture:Port"]);

            File.WriteAllText(path, "Fixture.Port = 8\n");
            Assert.True(await WaitFor(() => root["Fixture:Port"] == "8"), "a good edit after a bad one was not applied");
        }
        finally
        {
            (root as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task ReloadOnChange_DeletedFile_KeepsTheLastGoodValues()
    {
        string path = Write("world.conf", "Fixture.Port = 1\n");
        var errors = new List<string>();
        IConfigurationRoot root = new ConfigurationBuilder()
            .Add(new ConfConfigurationSource { Path = path, ReloadOnChange = true, ReloadDelay = TimeSpan.FromMilliseconds(20), ReloadError = m => { lock (errors) { errors.Add(m); } } })
            .Build();
        try
        {
            File.Delete(path);
            Assert.True(await WaitFor(() => { lock (errors) { return errors.Count > 0; } }), "the deletion was not reported");
            Assert.Contains("world.conf", errors[0], StringComparison.Ordinal);
            Assert.Equal("1", root["Fixture:Port"]);
        }
        finally
        {
            (root as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void FreshBuildOfTheSameSource_RereadsTheFile()
    {
        string path = Write("world.conf", "Fixture.Port = 1\n");
        var source = new ConfConfigurationSource { Path = path };
        IConfigurationRoot first = new ConfigurationBuilder().Add(source).Build();
        File.WriteAllText(path, "Fixture.Port = 2\n");
        IConfigurationRoot second = new ConfigurationBuilder().Add(source).Build();
        Assert.Equal("1", first["Fixture:Port"]); // no watcher: the live root is untouched
        Assert.Equal("2", second["Fixture:Port"]);
    }

    [Fact]
    public void FirstLoad_OfAMalformedFile_Throws_AndAMissingRequiredFile_Throws()
    {
        string bad = Write("bad.conf", "x\n");
        Assert.Throws<ConfParseException>(() => new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = bad }).Build());
        Assert.Throws<FileNotFoundException>(() => new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = Path.Combine(_dir, "none.conf") }).Build());
        Assert.NotNull(new ConfigurationBuilder().Add(new ConfConfigurationSource { Path = Path.Combine(_dir, "none.conf"), Optional = true }).Build());
    }
}
