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
