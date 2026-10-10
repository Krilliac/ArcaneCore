using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Ops.Cli;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The World daemon's command line and .conf handling, end to end. On 2026-10-10 <c>ArcaneCore.World.dll --help</c> started a live server
/// against the live profile (the .NET command-line provider drops <c>--help</c> silently and the host was built before anything looked at
/// the arguments). The process tests run the real daemon. A "sentinel" is a file-logger path: the logger creates its file when the host
/// is built, so its absence proves the host never was (the Realm test project holds the control that proves the sentinel can fire).
/// </summary>
public sealed class ConfWorldCliTests : IDisposable
{
    private const string Valid = "Server=127.0.0.1;Port=3306;Database=x;User=arcane;Password=Sup3rSecret!;";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-confworld-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfWorldCliTests()
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
            // A late handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private string Sentinel => Path.Combine(_dir, "sentinel.log");

    private string[] SentinelArgs => ["--Logging:ArcaneCore:File:Enabled=true", "--Logging:ArcaneCore:File:Path=" + Sentinel];

    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var all = new Dictionary<string, string?>
        {
            ["Database:Auth:Provider"] = "MariaDb",
            ["Database:Auth:ConnectionString"] = Valid,
            ["Database:Characters:Provider"] = "MariaDb",
            ["Database:Characters:ConnectionString"] = Valid,
            ["Database:World:Provider"] = "MariaDb",
            ["Database:World:ConnectionString"] = Valid,
        };
        foreach ((string key, string? value) in values)
        {
            all[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    private void AssertNoHostWasBuilt() => Assert.False(File.Exists(Sentinel), "the host was built: " + Sentinel + " exists");

    [Fact]
    public void OpsCli_WriteConfigIsAVerb_AndCheckConfigStillReturns0And78()
    {
        Assert.Contains("check-config", OpsCli.Verbs);
        Assert.Contains("write-config", OpsCli.Verbs);

        Assert.True(OpsCli.TryRun(["check-config"], Config(), new StringWriter(), out int okCode));
        Assert.Equal(ExitCodes.Success, okCode);

        var bad = new StringWriter();
        Assert.True(OpsCli.TryRun(["check-config"], Config(("World:Port", "0"), ("World:CharactersPerRealm", "99")), bad, out int badCode));
        Assert.Equal(ExitCodes.InvalidConfiguration, badCode);
        Assert.Contains("World:Port", bad.ToString(), StringComparison.Ordinal);
        Assert.Contains("World:CharactersPerRealm", bad.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task World_Help_PrintsUsageAndExits64_WithoutBuildingTheHost()
    {
        ConfProcess.Result result = await ConfProcess.RunWorldAsync([.. SentinelArgs, "--help"]);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Usage:", result.Output, StringComparison.Ordinal);
        Assert.Contains("write-config", result.Output, StringComparison.Ordinal);
        Assert.Contains("--config", result.Output, StringComparison.Ordinal);
        AssertNoHostWasBuilt();
    }

    [Fact]
    public async Task World_UnknownOption_Exits64_BeforeTheHost()
    {
        ConfProcess.Result result = await ConfProcess.RunWorldAsync([.. SentinelArgs, "--Wrld:Port=1"]);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Wrld:Port", result.Output, StringComparison.Ordinal);
        Assert.Contains("World:Port", result.Output, StringComparison.Ordinal); // the closest key
        AssertNoHostWasBuilt();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task World_MalformedWorldConf_Exits78_NamingFileAndLine(bool byConfigOption)
    {
        // check-config is the safe verb: should the conf be wrongly accepted, no live server starts.
        string conf = Path.Combine(_dir, "world.conf");
        await File.WriteAllTextAsync(conf, "# first line is fine\nWorld.Motd = \"fine\"\nWorld.Port 8085\n");
        string[] where = byConfigOption ? ["--config", conf] : ["--contentRoot", _dir];

        ConfProcess.Result result = await ConfProcess.RunWorldAsync(["check-config", .. where, .. SentinelArgs]);

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        Assert.Contains("world.conf:3", result.Output, StringComparison.Ordinal);
        AssertNoHostWasBuilt();
    }

    [Fact]
    public async Task World_WriteConfigCurrent_RefusesToOverwriteWithoutForce()
    {
        string target = Path.Combine(_dir, "existing.conf");
        const string original = "# an existing operator file\n";
        await File.WriteAllTextAsync(target, original);

        ConfProcess.Result refused = await ConfProcess.RunWorldAsync("write-config", "--current", target);
        Assert.Equal(ExitCodes.Usage, refused.ExitCode);
        Assert.Contains("--force", refused.Output, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(target));

        ConfProcess.Result forced = await ConfProcess.RunWorldAsync("write-config", "--current", target, "--force");
        Assert.Equal(ExitCodes.Success, forced.ExitCode);
        string written = await File.ReadAllTextAsync(target);
        Assert.NotEqual(original, written);
        Assert.Contains("World.Port", written, StringComparison.Ordinal);

        string fresh = Path.Combine(_dir, "fresh.conf");
        ConfProcess.Result created = await ConfProcess.RunWorldAsync("write-config", "--defaults", fresh);
        Assert.Equal(ExitCodes.Success, created.ExitCode);
        Assert.Contains("World.Port", await File.ReadAllTextAsync(fresh), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--Startup:Strict=true")]
    [InlineData("--Spells:RequireSpellFocus=false")]
    [InlineData("--Crafting:Enabled=false")]
    [InlineData("--Forms:ResetFistAttackTimeOnFormLoss=true")]
    public async Task World_KeysTheDaemonReadsByName_AreAcceptedOnTheCommandLine(string option)
    {
        // The keys are read through IConfiguration[...], not an options class; the embedded catalog must still list them or the CLI refuses them (exit 64).
        string[] database =
        [
            "--Database:Auth:Provider=MariaDb", "--Database:Auth:ConnectionString=" + Valid,
            "--Database:Characters:Provider=MariaDb", "--Database:Characters:ConnectionString=" + Valid,
            "--Database:World:Provider=MariaDb", "--Database:World:ConnectionString=" + Valid,
        ];

        ConfProcess.Result result = await ConfProcess.RunWorldAsync(["check-config", .. database, .. SentinelArgs, option]);

        Assert.NotEqual(ExitCodes.Usage, result.ExitCode);
        Assert.DoesNotContain("unknown option", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        AssertNoHostWasBuilt();
    }

    [Fact]
    public async Task World_StartupStrictInAConf_IsAKnownKey_AndAMistypedOneStillFailsStrictStartup()
    {
        string[] database =
        [
            "--Database:Auth:Provider=MariaDb", "--Database:Auth:ConnectionString=" + Valid,
            "--Database:Characters:Provider=MariaDb", "--Database:Characters:ConnectionString=" + Valid,
            "--Database:World:Provider=MariaDb", "--Database:World:ConnectionString=" + Valid,
        ];
        await File.WriteAllTextAsync(Path.Combine(_dir, "world.conf"), "Startup.Strict = true\nCrafting.Enabled = false\nForms.ResetFistAttackTimeOnFormLoss = true\n");
        ConfProcess.Result known = await ConfProcess.RunWorldAsync(["check-config", "--contentRoot", _dir, .. database, .. SentinelArgs]);
        Assert.Equal(ExitCodes.Success, known.ExitCode);
        Assert.DoesNotContain("world.conf:", known.Output, StringComparison.Ordinal);

        // The control: the same strict file with one mistyped key is refused, so the pass above is the catalog and not a disabled check.
        await File.WriteAllTextAsync(Path.Combine(_dir, "world.conf"), "Startup.Strict = true\nCrafting.Enabld = false\n");
        ConfProcess.Result unknown = await ConfProcess.RunWorldAsync(["check-config", "--contentRoot", _dir, .. database, .. SentinelArgs]);
        Assert.Equal(ExitCodes.InvalidConfiguration, unknown.ExitCode);
        Assert.Contains("world.conf:2", unknown.Output, StringComparison.Ordinal);
    }
}
