using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>The module lane is off by default and fails closed: no module object exists, and a bad configuration refuses to start.</summary>
public sealed class ModuleLaneConfigTests
{
    private sealed class Probe(string environment) : IRuntimeProbe
    {
        public string EnvironmentName { get; } = environment;

        public bool MetadataUpdatesSupported => false;

        public string? GetEnvironmentVariable(string name) => null;
    }

    // Only what is registered is inspected: building the real objects needs the whole daemon.
    private static IServiceCollection Build(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ServiceCollection().AddWorldDaemon(configuration);
    }

    private static bool Has(IServiceCollection services, Type serviceType) => services.Any(d => d.ServiceType == serviceType);

    private static bool HasHotModuleHostedService(IServiceCollection services)
        => services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType?.Name == "HotModuleHost");

    [Fact]
    public void ByDefault_NoModuleObjectExists_AndNoHotmodulePathIsRegistered()
    {
        IServiceCollection services = Build([]);

        Assert.False(Has(services, typeof(ModuleHost)));
        Assert.False(Has(services, typeof(HotCodeState)));
        Assert.False(HasHotModuleHostedService(services));
        Assert.False(new HotCodeOptions().Modules.Enabled);
        Assert.Empty(new HotCodeOptions().Modules.LoadOnStart);
    }

    [Fact]
    public void WithModulesEnabled_TheHostExists_WithoutTheDotnetWatchLane()
    {
        IServiceCollection services = Build(new()
        {
            ["World:HotCode:Modules:Enabled"] = "true",
            ["World:HotCode:Modules:Directory"] = "mods",
        });

        Assert.True(Has(services, typeof(ModuleHost)));
        Assert.False(Has(services, typeof(HotCodeRefresh)));
        Assert.True(HasHotModuleHostedService(services));
    }

    [Fact]
    public void TheModulesOptions_BindFromConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:HotCode:Modules:Enabled"] = "true",
            ["World:HotCode:Modules:Directory"] = "mods",
            ["World:HotCode:Modules:AllowAnyEnvironment"] = "true",
            ["World:HotCode:Modules:Allowlist"] = "allow.txt",
            ["World:HotCode:Modules:LoadOnStart:0"] = "A",
            ["World:HotCode:Modules:LoadOnStart:1"] = "B",
        }).Build();

        HotModuleOptions modules = configuration.GetSection(HotCodeOptions.SectionName).Get<HotCodeOptions>()!.Modules;

        Assert.True(modules.Enabled);
        Assert.Equal("mods", modules.Directory);
        Assert.True(modules.AllowAnyEnvironment);
        Assert.Equal("allow.txt", modules.Allowlist);
        Assert.Equal(["A", "B"], modules.LoadOnStart);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void ModulesWithADirectory_AreAcceptedInDevelopmentAndStaging(string environment)
    {
        var options = new HotCodeOptions { Modules = { Enabled = true, Directory = "mods" } };

        Assert.True(HotCodeGuard.Evaluate(options, new Probe(environment)).Allowed);
    }

    [Fact]
    public void Modules_AreRefusedInProduction_UnlessTheOperatorOptsIn()
    {
        var options = new HotCodeOptions { Modules = { Enabled = true, Directory = "mods" } };
        options.Modules.Allowlist = "allow.txt";

        HotCodeVerdict refused = HotCodeGuard.Evaluate(options, new Probe("Production"));
        options.Modules.AllowAnyEnvironment = true;
        HotCodeVerdict allowed = HotCodeGuard.Evaluate(options, new Probe("Production"));

        Assert.False(refused.Allowed);
        Assert.Contains("AllowAnyEnvironment", Assert.Single(refused.Refusals));
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void Modules_InProduction_NeedAnAllowlist_EvenWithTheOperatorOptIn()
    {
        var options = new HotCodeOptions { Modules = { Enabled = true, Directory = "mods", AllowAnyEnvironment = true } };

        HotCodeVerdict noList = HotCodeGuard.Evaluate(options, new Probe("Production"));
        options.Modules.Allowlist = "allow.txt";
        HotCodeVerdict withList = HotCodeGuard.Evaluate(options, new Probe("Production"));
        options.Modules.Allowlist = string.Empty;
        HotCodeVerdict development = HotCodeGuard.Evaluate(options, new Probe("Development"));

        Assert.False(noList.Allowed);
        Assert.Contains("Modules:Allowlist", Assert.Single(noList.Refusals));
        Assert.True(withList.Allowed);
        Assert.True(development.Allowed); // Development / Staging keep working without a list
    }

    [Fact]
    public void Modules_WithoutADirectory_RefuseToStart()
    {
        var options = new HotCodeOptions { Modules = { Enabled = true, AllowAnyEnvironment = true, Allowlist = "allow.txt" } };

        HotCodeVerdict verdict = HotCodeGuard.Evaluate(options, new Probe("Production"));

        Assert.False(verdict.Allowed);
        Assert.Contains("Modules:Directory", Assert.Single(verdict.Refusals));
    }

    [Fact]
    public void ModulesOff_ChangeNothingAboutTheGuard()
    {
        Assert.True(HotCodeGuard.Evaluate(new HotCodeOptions { Modules = { Directory = "" } }, new Probe("Production")).Allowed);
    }

    [Fact]
    public void TheModuleLane_DoesNotNeedTheWatchLaneToBeAllowedInProduction()
    {
        // The watch lane stays Development/Staging only, whatever the modules say.
        var options = new HotCodeOptions { Enabled = true, Modules = { Enabled = true, Directory = "mods", AllowAnyEnvironment = true, Allowlist = "allow.txt" } };

        HotCodeVerdict verdict = HotCodeGuard.Evaluate(options, new Probe("Production"));

        Assert.False(verdict.Allowed);
        Assert.Contains("only accepted when the host environment is Development or Staging", Assert.Single(verdict.Refusals));
    }
}
