using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.World;

/// <summary>
/// The world daemon's complete service registration, in the order its <c>Program.cs</c> needs. It lives here, not in the top-level
/// statements, so the CI guard (<c>WiringAuditTests</c>) builds exactly what production builds with scope and build validation on:
/// a service registered as a dependency but never added fails there instead of at the first player. The host builder, the
/// crash hooks (<c>UseWorldDiagnostics</c>) and the early command-line handling stay in <c>Program.cs</c>.
/// </summary>
public static class WorldProductionServices
{
    /// <summary>Register everything the world daemon runs with. Call once, after the configuration is final.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The final configuration (appsettings, .conf, environment, command line).</param>
    /// <param name="clientData">The client-data report computed before anything binds; registered so the start-up log can describe it.</param>
    public static IServiceCollection AddWorldProductionServices(this IServiceCollection services, IConfiguration configuration, ArcaneCore.Data.ClientData.ClientDataReport clientData)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clientData);

        // The HostOptions section (e.g. ShutdownTimeout) is not bound by the default builder; a full save
        // drain for many players must not be cut short by the host default (docs/areas/ops-perf.md).
        services.Configure<HostOptions>(configuration.GetSection("HostOptions"));
        // Hosted services must stop in reverse registration order: the WAL checkpointer (registered with the databases, before WorldHost) has to run its final checkpoint after WorldHost has drained the save queue.
        services.PostConfigure<HostOptions>(options => options.ServicesStopConcurrently = false);
        services.AddArcaneCoreLogging(configuration);
        services.AddAuthDatabase(configuration);
        services.AddCharacterDatabase(configuration);
        services.AddWorldDatabase(configuration);
        services.AddDatabaseResilience(configuration); // breakers + bootstrap retry for the three databases (docs/ops/resilience.md)
        services.AddWorldDaemon(configuration);
        services.AddWorldConsole(configuration); // typed GM commands at the console window, off when stdin is redirected (docs/areas/server-console.md); after the listener so it stops first
        services.AddWorldControlPipe(configuration); // the console commands over a local, ACL'd named pipe; off by default (docs/areas/server-console.md, "Control pipe")
        services.AddNetProtection(configuration);
        services.AddWorldWatchdog(configuration);
        services.AddWorldMetrics(configuration); // Ops:Metrics, off by default (docs/ops/metrics.md)
        services.AddWorldOpsDiagnostics(configuration); // .debug alloc / .debug dump, dumps off by default (docs/ops/diagnostics.md)
        services.AddWorldObservability(configuration); // client-rejection log + duplicate-packet detector (docs/ops/observability.md)
        services.AddSingleton(clientData);
        services.AddHostedService<WiringAuditReporter>(); // last: one summary line once the host has started (docs/ops/wiring-audit.md)
        return services;
    }
}
