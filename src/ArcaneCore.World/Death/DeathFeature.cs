using ArcaneCore.Game.Death;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Death;

/// <summary>
/// Binds the <c>World:Death</c> options and registers the world's <see cref="DeathHooks"/>
/// (auto-discovered like every <see cref="IWorldFeature"/>). Later death slices extend this
/// feature (login handling, ghost form, graveyards).
/// </summary>
public sealed class DeathFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DeathFeature> _logger;
    private WorldRuntime? _world;

    public DeathFeature(IServiceProvider services, ILogger<DeathFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The bound options (after <see cref="Attach"/>).</summary>
    public DeathOptions Options { get; private set; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the death feature is already attached");
        }

        _world = world;
        var options = new DeathOptions();
        _services.GetService<IConfiguration>()?.GetSection(DeathOptions.SectionName).Bind(options);
        Options = options;
        if (!DeathHooks.TryRegister(world, new DeathHooks(options, DeathClock.System)))
        {
            _logger.LogWarning("Death hooks were already registered for this world; the {Section} options are not applied", DeathOptions.SectionName);
        }
    }
}
