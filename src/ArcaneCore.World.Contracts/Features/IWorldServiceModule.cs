using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Features;

/// <summary>
/// A subsystem's service registrations, discovered by the world daemon instead of being listed in its registration code
/// (docs/design/hot-reload-tiers.md, section 4.1: the core names no pack type). Every non-abstract implementation in the world
/// assembly is created once (parameterless constructor) and asked to register, in full type name order, where
/// <c>AddWorldDaemon</c> used to list the subsystem's services.
/// </summary>
public interface IWorldServiceModule
{
    void Register(IServiceCollection services, IConfiguration configuration);
}
