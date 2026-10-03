using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Reload;

/// <summary>
/// The live-reload service of the world daemon (discovered <see cref="IWorldFeature"/>): owns the
/// <see cref="ReloadCoordinator"/> and registers every <see cref="IContentReloadable"/> in this
/// assembly (parameterless or <see cref="IServiceProvider"/> constructor, ordered by full type
/// name), so a feature adds a reloadable by adding a class. Nothing reloads by itself: the
/// <c>.reload</c> commands (<see cref="ReloadCommands"/>) are the only trigger
/// (docs/areas/hot-reload.md).
/// </summary>
public sealed class ReloadFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ReloadFeature> _logger;

    public ReloadFeature(IServiceProvider services, ILogger<ReloadFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = _services.GetService<IOptions<HotReloadOptions>>()?.Value
            ?? _services.GetService<IConfiguration>()?.GetSection(HotReloadOptions.SectionName).Get<HotReloadOptions>()
            ?? new HotReloadOptions();
        if (Options.Commands)
        {
            _coordinator = new ReloadCoordinator(logger, Options);
        }
    }

    private readonly ReloadCoordinator? _coordinator;

    /// <summary>The <c>HotReload</c> options (read when the world starts).</summary>
    public HotReloadOptions Options { get; }

    /// <summary>Whether live reload is on (<see cref="HotReloadOptions.Commands"/>, default off). When off there is no coordinator and no <c>.reload</c> root.</summary>
    public bool Enabled => _coordinator is not null;

    /// <summary>The coordinator; only exists when <see cref="Enabled"/>.</summary>
    public ReloadCoordinator Coordinator => _coordinator
        ?? throw new InvalidOperationException("Live reload is disabled (HotReload:Commands is false).");

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_coordinator is null)
        {
            _logger.LogInformation("Live reload is disabled (HotReload:Commands=false); the .reload commands do not exist");
            return;
        }

        Coordinator.Attach(world);
        foreach (Type type in AssemblyDiscovery.FindTypes<IContentReloadable>())
        {
            var reloadable = (IContentReloadable)ActivatorUtilities.CreateInstance(_services, type);
            if (reloadable is IOptionalReloadable { IsEnabled: false })
            {
                continue; // a reload behind its own switch (e.g. World:GameEvents:AllowReload) that is off: its name does not exist
            }

            Coordinator.Register(reloadable);
        }

        _logger.LogInformation("Live reload ready: {Names} (commands enabled)", string.Join(", ", Coordinator.Names));
    }
}
