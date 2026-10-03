using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
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
public sealed class DeathFeature : IWorldFeature, IDisposable
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

        world.PlayerLoggedIn += OnPlayerLoggedIn;
    }

    public void Dispose()
    {
        if (_world is { } world)
        {
            world.PlayerLoggedIn -= OnPlayerLoggedIn;
        }
    }

    /// <summary>
    /// A stored ghost has entered its map: put its body back and finish the ghost state
    /// (vmangos Player::LoadCorpse from HandlePlayerLogin, CharacterHandler.cpp:636). A ghost whose
    /// body is missing was already resurrected at half health when its vitals were applied.
    /// The stored life stays on the player for <c>CharacterLifeFeature</c>, which clears it after
    /// the login auras have been restored.
    /// </summary>
    private void OnPlayerLoggedIn(Player player)
    {
        if (player.LoadedLife is not { Stored: { } life } || !PlayerLife.IsGhostWithBody(life) || player.Map is not { } map)
        {
            return;
        }

        map.Combat.RestoreGhost(player, life.Corpse!);
    }
}
