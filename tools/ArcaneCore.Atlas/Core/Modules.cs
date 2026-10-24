using System.Reflection;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Core;

/// <summary>
/// One feature area's contribution (server layers, MPQ reader, live feed, ...). The host discovers every public,
/// non-abstract <see cref="IAtlasModule"/> in the Atlas assembly that has a public parameterless constructor, orders
/// them by <see cref="Order"/> then <see cref="Name"/>, and calls <see cref="Register"/> once. No shared file lists
/// the modules, so each lane adds its own.
/// </summary>
public interface IAtlasModule
{
    string Name { get; }

    /// <summary>
    /// Lower registers first. Modules that provide services others read during registration use
    /// <see cref="ModuleOrder.Foundation"/> (client files, map directory, server engine).
    /// </summary>
    int Order { get; }

    /// <summary>Adds layer sources, commands and services. Must not block on I/O longer than a directory listing.</summary>
    void Register(AtlasContext context);
}

/// <summary>Conventional <see cref="IAtlasModule.Order"/> values.</summary>
public static class ModuleOrder
{
    public const int Foundation = 0;
    public const int Layers = 100;
    public const int Diff = 200;
    public const int Host = 300;
}

/// <summary>What a module registers into: the process's commands, layer sources and services.</summary>
public sealed class AtlasContext : IDisposable
{
    private readonly List<string> _problems = [];
    private readonly List<Action> _composed = [];

    public AtlasContext(AtlasPaths paths, ILoggerFactory loggers, bool headless)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
        Headless = headless;
        Cache = new LayerCache(Layers);
        Services.Add(paths);
        Services.Add(Commands);
        Services.Add(Layers);
        Services.Add(Cache);
        Services.Add(Events);
        Services.Add(View);
        Services.Add(BinaryEndpoints);
        Services.Add(CacheParticipants);
        Services.Add(this);
    }

    /// <summary>Every consumer of layer geometry builds through this cache.</summary>
    public LayerCache Cache { get; }

    /// <summary>Pushes to connected UIs (view changes, command log, live frames).</summary>
    public EventHub Events { get; } = new();

    /// <summary>The shared current view (headless runs use it as the capture/export view).</summary>
    public ViewStore View { get; } = new(ViewState.Default);

    public AtlasPaths Paths { get; }

    public ILoggerFactory Loggers { get; }

    /// <summary>True for <c>run</c>, <c>script</c> and headless MCP: no UI, no HTTP listener.</summary>
    public bool Headless { get; }

    public CommandRegistry Commands { get; } = new();

    public LayerRegistry Layers { get; } = new();

    /// <summary>The <c>GET /api/bin/{name}</c> routes (textures, model packages, ...).</summary>
    public BinaryEndpoints BinaryEndpoints { get; } = new();

    /// <summary>Caches besides the tile cache, reported by <c>status</c> and emptied by <c>cache.clear</c>.</summary>
    public CacheParticipants CacheParticipants { get; } = new();

    public AtlasServices Services { get; } = new();

    /// <summary>Modules that failed to register, with the reason (shown by <c>status</c>).</summary>
    public IReadOnlyList<string> Problems => _problems;

    public void ReportProblem(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        _problems.Add(problem);
    }

    /// <summary>
    /// Runs <paramref name="action"/> once, after every module has registered and the command, layer, endpoint and cache registries
    /// are frozen. A module uses it to freeze a registry of its own (for example the generated texture schemes) at the same moment.
    /// </summary>
    public void OnComposed(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _composed.Add(action);
    }

    internal void RunComposedHooks()
    {
        foreach (Action action in _composed)
        {
            try
            {
                action();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                ReportProblem($"A post-composition hook failed: {e.GetType().Name}: {e.Message}");
            }
        }

        _composed.Clear();
    }

    public void Dispose() => Services.Dispose();
}

/// <summary>Layer id to the one source that builds it.</summary>
public sealed class LayerRegistry
{
    private readonly Dictionary<string, ILayerSource> _sources = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _frozen;

    public void Add(ILayerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_frozen)
            {
                throw new InvalidOperationException("Layer sources cannot be added after startup.");
            }

            foreach (string id in source.Layers)
            {
                if (!LayerCatalog.TryGet(id, out _))
                {
                    throw new ArgumentException($"Layer '{id}' is not in the catalog.");
                }

                if (_sources.ContainsKey(id))
                {
                    throw new ArgumentException($"Layer '{id}' already has a source.");
                }
            }

            foreach (string id in source.Layers)
            {
                _sources[id] = source;
            }
        }
    }

    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }

    public bool TryGet(string layerId, out ILayerSource source)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(layerId, out source!);
        }
    }

    /// <summary>Layer ids that have a source, in catalog order.</summary>
    public IReadOnlyList<string> Available
    {
        get
        {
            lock (_gate)
            {
                return [.. LayerCatalog.All.Select(l => l.Id).Where(_sources.ContainsKey)];
            }
        }
    }
}

/// <summary>A minimal service map (one instance per type), disposed in reverse registration order.</summary>
public sealed class AtlasServices : IServiceProvider, IDisposable
{
    private readonly Dictionary<Type, object> _services = [];
    private readonly List<object> _order = [];
    private readonly Lock _gate = new();

    /// <summary>Registers <paramref name="instance"/> as <typeparamref name="T"/>; one instance per type.</summary>
    public void Add<T>(T instance)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
        {
            if (!_services.TryAdd(typeof(T), instance))
            {
                throw new ArgumentException($"A {typeof(T).Name} is already registered.");
            }

            if (!_order.Contains(instance))
            {
                _order.Add(instance);
            }
        }
    }

    public T? Get<T>()
        where T : class
        => GetService(typeof(T)) as T;

    public T Require<T>()
        where T : class
        => Get<T>() ?? throw new InvalidOperationException($"No {typeof(T).Name} is registered.");

    public object? GetService(Type serviceType)
    {
        lock (_gate)
        {
            return _services.TryGetValue(serviceType, out object? value) ? value : null;
        }
    }

    public void Dispose()
    {
        object[] order;
        lock (_gate)
        {
            order = [.. _order];
            _order.Clear();
            _services.Clear();
        }

        for (int i = order.Length - 1; i >= 0; i--)
        {
            if (order[i] is IDisposable disposable && order[i] is not AtlasContext)
            {
                disposable.Dispose();
            }
        }
    }
}

/// <summary>Discovers and runs the modules.</summary>
public static class AtlasModules
{
    public static IReadOnlyList<IAtlasModule> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return
        [
            .. assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAtlasModule).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(t => (IAtlasModule)Activator.CreateInstance(t)!)
                .OrderBy(m => m.Order)
                .ThenBy(m => m.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Builds a context and registers every module of <paramref name="assembly"/> (default: Atlas). A module that
    /// throws is skipped and reported in <see cref="AtlasContext.Problems"/>; the rest still register. Registries are
    /// frozen afterwards.
    /// </summary>
    public static AtlasContext Compose(AtlasPaths paths, ILoggerFactory loggers, bool headless, Assembly? assembly = null)
        => Compose(paths, loggers, headless, Discover(assembly ?? typeof(AtlasModules).Assembly));

    public static AtlasContext Compose(AtlasPaths paths, ILoggerFactory loggers, bool headless, IEnumerable<IAtlasModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var context = new AtlasContext(paths, loggers, headless);
        ILogger log = loggers.CreateLogger("ArcaneCore.Atlas.Modules");
        foreach (string problem in paths.Problems())
        {
            context.ReportProblem(problem);
            log.LogWarning("{Problem}", problem);
        }

        foreach (IAtlasModule module in modules.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            try
            {
                module.Register(context);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                string problem = $"Module {module.Name} did not register: {e.GetType().Name}: {e.Message}";
                context.ReportProblem(problem);
                log.LogWarning("{Problem}", problem);
            }
        }

        context.Commands.Freeze();
        context.Layers.Freeze();
        context.BinaryEndpoints.Freeze();
        context.CacheParticipants.Freeze();
        context.RunComposedHooks();
        return context;
    }
}
