using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Control;

/// <summary>Registers the world daemon's local control pipe (docs/areas/server-console.md, "Control pipe").</summary>
public static class WorldControlPipeServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>World:Control:Pipe</c> and add the control pipe hosted service, which does nothing unless <c>Enabled</c> is set. Call
    /// after <c>AddWorldDaemon</c> (hosted services stop in reverse order, so the pipe stops before the listener and the world). Only the
    /// daemon's production registration calls this.
    /// </summary>
    public static IServiceCollection AddWorldControlPipe(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorldControlPipeOptions>(configuration.GetSection(WorldControlPipeOptions.SectionName));
        services.AddHostedService<WorldControlPipe>();
        return services;
    }
}
