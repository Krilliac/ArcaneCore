using ArcaneCore.Game.Maps;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Features;

/// <summary>
/// A world-daemon feature service (channels, groups, social lists, teleports, …). Every
/// non-abstract implementation in this assembly is registered as a singleton and attached to
/// the world before the world thread starts; <see cref="Attach"/> is where it subscribes to
/// <see cref="WorldRuntime.PlayerLoggedIn"/> / <see cref="WorldRuntime.PlayerLoggingOut"/>.
/// <para>
/// Discovery (instead of a hand-maintained list) lets features built in parallel land without
/// editing a shared registration file (docs/integration/seams.md). Handlers reach a
/// feature through <c>session.Services.GetRequiredService&lt;TFeature&gt;()</c>.
/// </para>
/// </summary>
public interface IWorldFeature
{
    /// <summary>Called once, before the world thread starts (no world-thread work may run here).</summary>
    void Attach(WorldRuntime world);
}

/// <summary>Registration and attachment of the <see cref="IWorldFeature"/> implementations.</summary>
public static class WorldFeatures
{
    /// <summary>Seam interfaces a feature is additionally registered as, when it implements them.</summary>
    private static readonly Type[] SeamInterfaces = [typeof(IWorldFeature), typeof(IChatMessageHandler)];

    /// <summary>Every feature type in this assembly, ordered by full name (deterministic).</summary>
    public static IReadOnlyList<Type> FeatureTypes { get; } = AssemblyDiscovery.FindTypes<IWorldFeature>();

    /// <summary>
    /// Register every feature as a singleton (as itself and as each seam interface it implements).
    /// An <see cref="IChatMessageHandler"/> is only picked up when it is also an <see cref="IWorldFeature"/>.
    /// </summary>
    public static IServiceCollection AddWorldFeatures(this IServiceCollection services)
    {
        foreach (Type type in FeatureTypes)
        {
            services.AddSingleton(type);
            foreach (Type seam in SeamInterfaces.Where(i => i.IsAssignableFrom(type)))
            {
                services.AddSingleton(seam, provider => provider.GetRequiredService(type));
            }
        }

        return services;
    }

    /// <summary>Create every registered feature and attach it to <paramref name="world"/> (before <see cref="WorldRuntime.Start"/>).</summary>
    public static void AttachWorldFeatures(this IServiceProvider services, WorldRuntime world)
    {
        foreach (IWorldFeature feature in services.GetServices<IWorldFeature>())
        {
            feature.Attach(world);
        }
    }
}

/// <summary>Finds the implementations of the world daemon's extension interfaces in this assembly.</summary>
internal static class AssemblyDiscovery
{
    /// <summary>Non-abstract classes in this assembly assignable to <typeparamref name="T"/>, ordered by full name.</summary>
    public static IReadOnlyList<Type> FindTypes<T>() => typeof(AssemblyDiscovery).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(t))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>One instance (parameterless constructor) of every type <see cref="FindTypes{T}"/> returns.</summary>
    public static IReadOnlyList<T> CreateAll<T>() => FindTypes<T>()
        .Select(t => (T)(Activator.CreateInstance(t)
            ?? throw new InvalidOperationException($"could not create {t.FullName}")))
        .ToArray();
}
