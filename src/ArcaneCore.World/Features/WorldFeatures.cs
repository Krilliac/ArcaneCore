using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Features;

/// <summary>Registration and attachment of the <see cref="IWorldFeature"/> implementations.</summary>
public static class WorldFeatures
{
    /// <summary>Seam interfaces a feature is additionally registered as, when it implements them.</summary>
    private static readonly Type[] SeamInterfaces = [typeof(IWorldFeature), typeof(IChatMessageHandler), typeof(Chat.IChatMuteSource), typeof(ICharacterHooks), typeof(ICharacterDeleteHook), typeof(ICharacterSettlementBarrier), typeof(ISpellSummonSink), typeof(Game.Creatures.ICreatureHostility)];

    /// <summary>Every feature type in this assembly, ordered by full name (deterministic).</summary>
    public static IReadOnlyList<Type> FeatureTypes { get; } = AssemblyDiscovery.FindTypes<IWorldFeature>();

    /// <summary>
    /// Every feature type of <paramref name="assemblies"/> (built-ins plus packs), ordered by assembly name, then full
    /// type name (deterministic). <see cref="FeatureTypes"/> is this over the world assembly alone.
    /// </summary>
    public static IReadOnlyList<Type> DiscoverFeatureTypes(CodeAssemblies assemblies) => AssemblyDiscovery.FindTypes<IWorldFeature>(assemblies);

    /// <summary>
    /// Register every feature as a singleton (as itself and as each seam interface it implements).
    /// An <see cref="IChatMessageHandler"/>, <see cref="ICharacterHooks"/> or <see cref="ICharacterDeleteHook"/> is only picked up when it is also an <see cref="IWorldFeature"/>.
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

        // The lifecycle seams (docs/design/hot-reload-tiers.md, section 4): the host that attaches and detaches features, the
        // startup/shutdown hooks and the teleport waiver registry (a discovered feature itself, registered above).
        services.TryAddSingleton(sp => new FeatureHost(
            audit: sp.GetService<HotCodeAudit>,
            reporter: sp.GetService<IHotSwapReporter>,
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger("ArcaneCore.Features")));
        services.TryAddSingleton(sp => new WorldLifecycleHooks(sp.GetService<ILogger<WorldLifecycleHooks>>()));
        services.TryAddSingleton<IWorldLifecycleHooks>(sp => sp.GetRequiredService<WorldLifecycleHooks>());
        services.TryAddSingleton<ITeleportWaiverRegistry>(sp => sp.GetRequiredService<TeleportWaiverRegistry>());
        return services;
    }

    /// <summary>
    /// Create every registered feature and attach it to <paramref name="world"/> (before <see cref="WorldRuntime.Start"/>),
    /// through the <see cref="FeatureHost"/>: a plain feature with <see cref="IWorldFeature.Attach"/>, a detachable one with
    /// <see cref="IDetachableWorldFeature.AttachFrom"/> and a subscription bag the host owns.
    /// </summary>
    public static void AttachWorldFeatures(this IServiceProvider services, WorldRuntime world)
    {
        FeatureHost host = services.GetService<FeatureHost>() ?? new FeatureHost();
        host.AttachAll(world, services.GetServices<IWorldFeature>());
    }

    /// <summary>Stop in reverse attachment order; every feature receives shutdown even if one fails.</summary>
    public static async Task StopWorldFeaturesAsync(this IServiceProvider services)
    {
        List<Exception> failures = [];
        FeatureHost? host = services.GetService<FeatureHost>();
        foreach (IWorldFeature feature in services.GetServices<IWorldFeature>().Reverse())
        {
            if (host?.IsDetached(feature) == true)
            {
                continue; // its quiesce already stopped and drained it
            }

            try
            {
                await feature.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more world features failed to stop", failures);
        }
    }
}

/// <summary>
/// Finds the implementations of the world daemon's extension interfaces in this assembly, or in a
/// <see cref="CodeAssemblies"/> set (built-ins plus pack assemblies).
/// </summary>
internal static class AssemblyDiscovery
{
    /// <summary>The default set: this assembly alone.</summary>
    public static CodeAssemblies BuiltIn { get; } = CodeAssemblies.Of(typeof(AssemblyDiscovery).Assembly);

    /// <summary>Non-abstract classes in this assembly assignable to <typeparamref name="T"/>, ordered by full name.</summary>
    public static IReadOnlyList<Type> FindTypes<T>() => FindTypes<T>(BuiltIn);

    /// <summary>
    /// Non-abstract classes of <paramref name="assemblies"/> assignable to <typeparamref name="T"/>, in set order
    /// (assembly name, then full type name; for one assembly that is full-name order). A type that cannot load fails
    /// closed (<see cref="CodeAssemblyLoadException"/>).
    /// </summary>
    public static IReadOnlyList<Type> FindTypes<T>(CodeAssemblies assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return [.. assemblies.GetLoadableTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(t))];
    }

    /// <summary>One instance (parameterless constructor) of every type <see cref="FindTypes{T}()"/> returns.</summary>
    public static IReadOnlyList<T> CreateAll<T>() => CreateAll<T>(BuiltIn);

    /// <summary>One instance (parameterless constructor) of every type <see cref="FindTypes{T}(CodeAssemblies)"/> returns.</summary>
    public static IReadOnlyList<T> CreateAll<T>(CodeAssemblies assemblies) => FindTypes<T>(assemblies)
        .Select(t => (T)(Activator.CreateInstance(t)
            ?? throw new InvalidOperationException($"could not create {t.FullName}")))
        .ToArray();
}
