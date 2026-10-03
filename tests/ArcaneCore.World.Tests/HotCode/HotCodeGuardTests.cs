using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The fail-closed launch gate for code hot reload: everything is off by default, and a process
/// that is set up for metadata updates (dotnet watch, a debugger) is refused unless the operator
/// opted in, in a Development / Staging environment. Probes are fakes: no test touches the real
/// process environment.
/// </summary>
public sealed class HotCodeGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-hotcode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeProbe(string environment = "Production", bool supported = false, (string Name, string Value)[]? variables = null) : IRuntimeProbe
    {
        public string EnvironmentName { get; } = environment;

        public bool MetadataUpdatesSupported { get; } = supported;

        public string? GetEnvironmentVariable(string name)
            => (variables ?? []).Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();
    }

    [Fact]
    public void Defaults_AreOff()
    {
        var options = new HotCodeOptions();
        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.AuditLogPath);
    }

    [Fact]
    public void ShippedAppSettings_KeepHotCodeOff()
    {
        string path = FindUp("src/ArcaneCore.World/appsettings.json");
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        IConfigurationSection section = configuration.GetSection(HotCodeOptions.SectionName);
        Assert.True(section.Exists(), "appsettings.json must document World:HotCode");
        var bound = section.Get<HotCodeOptions>();
        Assert.NotNull(bound);
        Assert.False(bound.Enabled);
    }

    [Fact]
    public void NormalLaunch_IsAllowed_AndAuditsNothing()
    {
        string log = Path.Combine(_dir, "audit.log");
        HotCodeVerdict verdict = HotCodeGuard.Enforce(new HotCodeOptions(), new FakeProbe(), new HotCodeAudit(log));
        Assert.True(verdict.Allowed);
        Assert.False(verdict.HotReloadActive);
        Assert.False(File.Exists(log));
    }

    [Fact]
    public void MetadataUpdatesSupported_WhileDisabled_Throws()
    {
        var ex = Assert.Throws<HotCodeRefusedException>(
            () => HotCodeGuard.Enforce(new HotCodeOptions(), new FakeProbe(supported: true), new HotCodeAudit(null)));
        Assert.Contains("World:HotCode:Enabled", ex.Message);
        Assert.Contains("hot-runner", ex.Message);
    }

    [Theory]
    [InlineData("DOTNET_WATCH", "1")]
    [InlineData("DOTNET_MODIFIABLE_ASSEMBLIES", "debug")]
    [InlineData("DOTNET_STARTUP_HOOKS", @"C:\sdk\tools\Microsoft.Extensions.DotNetDeltaApplier.dll")]
    public void HotReloadVariable_WhileDisabled_Throws(string name, string value)
    {
        Assert.Throws<HotCodeRefusedException>(
            () => HotCodeGuard.Enforce(new HotCodeOptions(), new FakeProbe(variables: [(name, value)]), new HotCodeAudit(null)));
    }

    [Theory]
    [InlineData("DOTNET_WATCH", "0")]
    [InlineData("DOTNET_MODIFIABLE_ASSEMBLIES", "")]
    [InlineData("DOTNET_STARTUP_HOOKS", @"C:\tools\SomeOtherHook.dll")]
    public void UnrelatedVariable_WhileDisabled_IsAllowed(string name, string value)
    {
        Assert.True(HotCodeGuard.Enforce(new HotCodeOptions(), new FakeProbe(variables: [(name, value)]), new HotCodeAudit(null)).Allowed);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("")]
    [InlineData("Testing")]
    public void Enabled_OutsideDevelopmentOrStaging_Throws(string environment)
    {
        var ex = Assert.Throws<HotCodeRefusedException>(
            () => HotCodeGuard.Enforce(new HotCodeOptions { Enabled = true }, new FakeProbe(environment), new HotCodeAudit(null)));
        Assert.Contains("Development or Staging", ex.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("development")]
    public void Enabled_InDevelopmentOrStaging_IsAllowed_AndAudited(string environment)
    {
        string log = Path.Combine(_dir, "audit.log");
        HotCodeVerdict verdict = HotCodeGuard.Enforce(
            new HotCodeOptions { Enabled = true },
            new FakeProbe(environment, supported: true, variables: [("DOTNET_WATCH", "1")]),
            new HotCodeAudit(log));
        Assert.True(verdict.Allowed);
        Assert.True(verdict.HotReloadActive);
        string[] lines = File.ReadAllLines(log);
        Assert.Single(lines);
        Assert.Contains("start-allowed", lines[0]);
    }

    [Fact]
    public void EnabledWithoutAnAgent_IsAllowed_ButNotActive()
    {
        HotCodeVerdict verdict = HotCodeGuard.Evaluate(new HotCodeOptions { Enabled = true }, new FakeProbe("Development"));
        Assert.True(verdict.Allowed);
        Assert.False(verdict.HotReloadActive);
    }

    [Fact]
    public void EveryRefusal_IsAuditedOnce()
    {
        string log = Path.Combine(_dir, "nested", "audit.log");
        var audit = new HotCodeAudit(log, () => new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        Assert.Throws<HotCodeRefusedException>(
            () => HotCodeGuard.Enforce(new HotCodeOptions(), new FakeProbe(supported: true), audit));
        Assert.Throws<HotCodeRefusedException>(
            () => HotCodeGuard.Enforce(new HotCodeOptions { Enabled = true }, new FakeProbe("Production"), audit));

        string[] lines = File.ReadAllLines(log);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("2026-10-03T00:00:00.0000000+00:00\tstart-refused\t", l));
    }

    [Fact]
    public void UnwritableAuditFile_DoesNotMaskTheRefusal()
    {
        // The audit path is an existing directory, so appending fails with an I/O error.
        Directory.CreateDirectory(_dir);
        Assert.Throws<HotCodeRefusedException>(
            () => HotCodeGuard.Enforce(new HotCodeOptions(), new FakeProbe(supported: true), new HotCodeAudit(_dir)));
    }

    [Fact]
    public void ProgramRunsTheGuard_BeforeTheDatabaseInitializers()
    {
        string program = File.ReadAllText(FindUp("src/ArcaneCore.World/Program.cs"));
        int guard = program.IndexOf("HotCodeGuard.Enforce(", StringComparison.Ordinal);
        int build = program.IndexOf("builder.Build()", StringComparison.Ordinal);
        int firstInitializer = program.IndexOf("InitializeAsync()", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Program.cs must call HotCodeGuard.Enforce");
        Assert.True(guard < build, "the guard must run before the host is built");
        Assert.True(guard < firstInitializer, "the guard must run before any schema initializer");
    }

    private static string FindUp(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relative} not found above {AppContext.BaseDirectory}");
    }
}
