using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Realm.Ops.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.Realm.Tests.Ops;

/// <summary>CI guards: every concrete config check of the Kernel and Realm assemblies is run by the realm, and its production services build with validation on.</summary>
public sealed class RealmWiringTests
{
    private static ConfigReport ValidateWith(params (string Key, string Value)[] pairs)
        => RealmOpsCli.Validate(new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build());

    [Fact]
    public void AuthBindAddressThatIsNotAnIpAddress_IsAStartupError_NotACrashLaterInTheListener()
    {
        ConfigReport report = ValidateWith(("Auth:BindAddress", "localhost"));
        Assert.True(report.IsInvalid, "Auth:BindAddress=localhost passed realm validation; IPAddress.Parse would throw in LogonServer.StartAsync");
        Assert.Contains(report.Issues, i => i.Key == "Auth:BindAddress" && i.Severity == ConfigSeverity.Error && i.Fix.Contains("0.0.0.0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AuthBindAddressPresentButEmpty_IsAStartupError(string value)
    {
        ConfigReport report = ValidateWith(("Auth:BindAddress", value));
        Assert.Contains(report.Issues, i => i.Key == "Auth:BindAddress" && i.Severity == ConfigSeverity.Error);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::1")]
    public void AuthBindAddressIpLiterals_Pass(string value)
        => Assert.DoesNotContain(ValidateWith(("Auth:BindAddress", value)).Issues, i => i.Key == "Auth:BindAddress");

    [Fact]
    public void AuthBindAddressMissing_FallsBackToTheDefault()
        => Assert.DoesNotContain(ValidateWith().Issues, i => i.Key == "Auth:BindAddress");

    [Theory]
    [InlineData("0", true)]
    [InlineData("65535", true)]
    [InlineData("65536", false)]
    [InlineData("-1", false)]
    public void AuthPortBounds(string value, bool valid)
        => Assert.Equal(!valid, ValidateWith(("Auth:Port", value)).Issues.Any(i => i.Key == "Auth:Port" && i.Severity == ConfigSeverity.Error));

    [Fact]
    public void AuthPortOutOfRange_IsAStartupError()
    {
        Assert.Contains(ValidateWith(("Auth:Port", "70000")).Issues, i => i.Key == "Auth:Port" && i.Severity == ConfigSeverity.Error);
        Assert.DoesNotContain(ValidateWith(("Auth:Port", "3724"), ("Auth:BindAddress", "127.0.0.1")).Issues, i => i.Key.StartsWith("Auth:", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAuthFailureBudget_IsAStartupError_NotSilentlyClampedToOff()
    {
        ConfigReport report = ValidateWith(("Net:Protection:AuthFailureBurstPerIp", "-1"));
        Assert.True(report.IsInvalid, "a negative AuthFailureBurstPerIp passed realm validation; NetGuard clamps it to 0, which disables the guard");
        Assert.Contains(report.Issues, i => i.Key == "Net:Protection:AuthFailureBurstPerIp" && i.Severity == ConfigSeverity.Error);
    }

    [Fact]
    public void EveryConcreteConfigCheck_IsRunByTheRealmsValidation()
    {
        Type[] run = [.. RealmOpsCli.CreateChecks().Select(c => c.GetType())];
        Type[] concrete = [.. ConfigCheckDiscovery.ConcreteTypes(RealmOpsCli.CheckAssemblies)];
        Assert.True(concrete.Length >= 5);
        string[] unrun = [.. concrete.Where(t => !run.Contains(t)).Select(t => t.FullName ?? t.Name)];
        Assert.True(unrun.Length == 0, "config checks never run: " + string.Join(", ", unrun));
        Assert.Empty(ConfigCheckDiscovery.NotRun(RealmOpsCli.CheckAssemblies, RealmOpsCli.CreateChecks()));
    }

    [Fact]
    public void ProductionServiceCollection_BuildsWithScopeAndBuildValidation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcanecore-realmwiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = dir, EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Auth:Provider"] = "Sqlite",
                ["Database:Auth:ConnectionString"] = $"Data Source={Path.Combine(dir, "auth.db")}",
            });
            builder.Services.AddRealmProductionServices(builder.Configuration);
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
            using IHost host = builder.Build();
            Assert.Contains(host.Services.GetServices<IHostedService>(), s => s.GetType().Name == "LogonServer");
            Assert.Contains(host.Services.GetServices<IHostedService>(), s => s.GetType().Name == "SupervisedChildDrain");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
