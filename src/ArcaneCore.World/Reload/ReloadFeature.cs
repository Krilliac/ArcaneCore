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
        Coordinator = new ReloadCoordinator(logger, Options);
    }

    /// <summary>The <c>HotReload</c> options (read when the world starts; the commands consult <see cref="HotReloadOptions.Commands"/> on every use).</summary>
    public HotReloadOptions Options { get; }

    public ReloadCoordinator Coordinator { get; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Coordinator.Attach(world);
        foreach (Type type in AssemblyDiscovery.FindTypes<IContentReloadable>())
        {
            Coordinator.Register((IContentReloadable)ActivatorUtilities.CreateInstance(_services, type));
        }

        _logger.LogInformation("Live reload ready: {Names} (commands {State})", string.Join(", ", Coordinator.Names), Options.Commands ? "enabled" : "disabled");
    }
}
