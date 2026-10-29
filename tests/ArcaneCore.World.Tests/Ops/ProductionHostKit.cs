using ArcaneCore.Data.ClientData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The world daemon's production host as the wiring tests build it: Program.cs's service set over throw-away SQLite files. Never started by this kit.</summary>
internal static class ProductionHostKit
{
    public static Dictionary<string, string?> Values(string directory, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["World:BindAddress"] = "127.0.0.1",
            ["World:Port"] = "0",
        };
        foreach (string component in new[] { "Auth", "Characters", "World" })
        {
            values[$"Database:{component}:Provider"] = "Sqlite";
            values[$"Database:{component}:ConnectionString"] = $"Data Source={Path.Combine(directory, component.ToLowerInvariant() + ".db")}";
        }

        if (extra is not null)
        {
            foreach ((string key, string? value) in extra)
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>The host built the way Program.cs builds it (<see cref="WorldProductionServices"/>), with the scope and build validation Development turns on.</summary>
    public static IHost Build(string directory, IReadOnlyDictionary<string, string?>? extra = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Production,
        });
        // An operator asking for concurrent stop must not win: the WAL checkpointer's final checkpoint has to run after WorldHost has drained the saves.
        builder.Configuration.AddInMemoryCollection(Values(directory, extra));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HostOptions:ServicesStopConcurrently"] = "true" });
        builder.Services.AddWorldProductionServices(builder.Configuration, ClientDataReport.Empty);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        return builder.Build();
    }
}
