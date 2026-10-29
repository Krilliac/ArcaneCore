using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Features;

/// <summary>Registration and attachment of the <see cref="IWorldFeature"/> implementations.</summary>
public static class WorldFeatures
{
    /// <summary>Seam interfaces a feature is additionally registered as, when it implements them.</summary>
    private static readonly Type[] SeamInterfaces = [typeof(IWorldFeature), typeof(IChatMessageHandler), typeof(Chat.IChatMuteSource), typeof(ICharacterHooks), typeof(ICharacterDeleteHook), typeof(ICharacterSettlementBarrier), typeof(ISpellSummonSink), typeof(Game.Creatures.ICreatureHostility), typeof(ArcaneCore.Kernel.Ops.ServerConsole.ISupervisedLifecycleRequestHandler)];

    /// <summary>Every feature type of the startup set (<see cref="WorldCodeAssemblies.Startup"/>: World plus the built-in packs), ordered by full name, then assembly name (deterministic, the order of before the pack moved out).</summary>
    public static IReadOnlyList<Type> FeatureTypes { get; } = AssemblyDiscovery.FindTypes<IWorldFeature>();

    /// <summary>
    /// Every feature type of <paramref name="assemblies"/> (built-ins plus packs), ordered by assembly name, then full
    /// type name (deterministic). <see cref="FeatureTypes"/> is this over <see cref="WorldCodeAssemblies.Startup"/>.
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

        services.TryAddSingleton<ShutdownDrain>(); // shared by the feature write queues; a host that only calls AddWorldFeatures still gets one lane set

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
                FaultLedger.Shared.Record("feature.lifecycle", ex);
                AnomalyLedger.Shared.Report("feature", "detach-fault", feature.GetType().Name, $"stop_error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
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
    /// <summary>The default set: the world's startup code set (this assembly plus the registered built-in packs, <see cref="WorldCodeAssemblies.Startup"/>).</summary>
    public static CodeAssemblies BuiltIn => WorldCodeAssemblies.Startup;

    /// <summary>
    /// Non-abstract classes of the startup set assignable to <typeparamref name="T"/>, ordered by full name (ordinal) across its assemblies.
    /// The built-in packs are part of the world (tier 2b moved the Playerbots pack out of this assembly with its namespaces unchanged),
    /// so their types interleave with World's by name: features, service modules, commands and hosted services keep the order they
    /// had when the pack was inside ArcaneCore.World. A pack set (<see cref="FindTypes{T}(CodeAssemblies)"/>) keeps the set order.
    /// </summary>
    public static IReadOnlyList<Type> FindTypes<T>() => [.. FindTypes<T>(BuiltIn)
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ThenBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal)];

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
    public static IReadOnlyList<T> CreateAll<T>() => Create<T>(FindTypes<T>());

    /// <summary>One instance (parameterless constructor) of every type <see cref="FindTypes{T}(CodeAssemblies)"/> returns.</summary>
    public static IReadOnlyList<T> CreateAll<T>(CodeAssemblies assemblies) => Create<T>(FindTypes<T>(assemblies));

    private static T[] Create<T>(IEnumerable<Type> types) => types
        .Select(t => (T)(Activator.CreateInstance(t)
            ?? throw new InvalidOperationException($"could not create {t.FullName}")))
        .ToArray();
}
