using ArcaneCore.Kernel.Ops.ServerConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArcaneCore.World.Ops.ServerConsole;

/// <summary>Registers the world daemon's console (docs/areas/server-console.md).</summary>
public static class WorldConsoleServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>World:Console</c> and add the console hosted service. Call after <c>AddWorldDaemon</c>: hosted services stop in
    /// reverse order, so the console stops before the listener and the world. Only the daemon's Program calls this, so no test
    /// or diagnostics host ever starts a stdin reader.
    /// </summary>
    public static IServiceCollection AddWorldConsole(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorldConsoleOptions>(configuration.GetSection(WorldConsoleOptions.SectionName));
        services.TryAddSingleton(_ => ConsoleInputSource.Default());
        services.AddHostedService<SupervisedChildDrain>(); // a child of the cluster host supervisor stops gracefully on its stdin "drain" line or EOF (the console itself stays off on redirected input)
        services.AddHostedService<WorldConsole>();
        return services;
    }
}
