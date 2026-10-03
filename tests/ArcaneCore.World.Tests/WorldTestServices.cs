using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A feature's test doubles for <see cref="WorldTestHost"/> (in-memory stores and the like).
/// Every non-abstract implementation in this test assembly is discovered and registered after
/// the host's own services, so a feature's tests never edit the shared host
/// (docs/integration/seams.md). Keep implementations in the feature's own folder.
/// </summary>
internal interface IWorldTestServices
{
    void Register(IServiceCollection services);
}

internal static class WorldTestServices
{
    public static void RegisterAll(IServiceCollection services)
    {
        IEnumerable<Type> types = typeof(WorldTestServices).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IWorldTestServices).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (Type type in types)
        {
            ((IWorldTestServices)Activator.CreateInstance(type)!).Register(services);
        }
    }
}
