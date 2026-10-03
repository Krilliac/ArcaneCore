using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.World.Features;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat.Duel;

/// <summary>
/// Duels in the world daemon (discovered <see cref="IWorldFeature"/>): binds the <c>World:Duel</c> options (restart-only), creates the world's
/// <see cref="DuelService"/> and wires it to the spell system (the duel effect, Grovel, aura removal), the combo points, the ignore list and the
/// area table, and ends a player's duel as interrupted when it logs out or is disconnected while still in its map (vmangos Player::RemoveFromWorld,
/// Player.cpp:2228-2230; the logout runs while the player is still in the map, so the flag object can be removed and the opponent told).
/// Every optional dependency is resolved with <c>GetService</c>: a daemon without spells, social or game objects degrades by refusing the
/// duel request, never by throwing. The timer is the world's death clock (whole Unix seconds, vmangos <c>time(nullptr)</c>) unless a
/// <see cref="TimeProvider"/> is registered (tests). State is runtime-only, so there is no schema and no store.
/// </summary>
public sealed class DuelFeature : IWorldFeature, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DuelFeature> _logger;
    private readonly TimeProvider? _time;
    private WorldRuntime? _world;
    private Action<Player>? _onLoggingOut;

    public DuelFeature(IServiceProvider services, ILogger<DuelFeature> logger, TimeProvider? timeProvider = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider;
    }

    /// <summary>The bound options (after <see cref="Attach"/>).</summary>
    public DuelOptions Options { get; private set; } = new();

    /// <summary>The world's duel service (after <see cref="Attach"/>).</summary>
    public DuelService? Service { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the duel feature is already attached");
        }

        _world = world;
        var options = new DuelOptions();
        _services.GetService<IConfiguration>()?.GetSection(DuelOptions.SectionName).Bind(options);
        Options = options;

        TimeProvider? time = _time;
        Func<long> clock = time is null
            ? () => DeathHooks.For(world).Clock.UnixSeconds
            : () => time.GetUtcNow().ToUnixTimeSeconds();
        var service = new DuelService(options, clock);
        if (_services.GetService<SpellFeature>() is { } spells)
        {
            service.Install(spells.System);
            spells.System.UnixSecondsClock = clock; // aura stamps and the duel start share one clock
        }
        else
        {
            _logger.LogWarning("No spell feature: the duel spell is not available and duels cannot start");
        }

        service.CombosProvider = () => _services.GetService<ComboFeature>()?.Service;
        service.IsIgnoring = (target, challenger) => _services.GetService<SocialFeature>() is { } social && social.Context.IsIgnoring(target, challenger);
        service.AreaOf = player => player.Map is { } map
            ? WorldMaps.Of(world).Areas.GetById(map.GetZoneAndAreaId(player.X, player.Y, player.Z).AreaId)
            : null;
        DuelService.Register(world, service);
        Service = service;

        _onLoggingOut = player => service.Complete(player, DuelCompleteType.Interrupted);
        world.PlayerLoggingOut += _onLoggingOut;
    }

    public void Dispose()
    {
        if (_world is { } world && _onLoggingOut is { } handler)
        {
            world.PlayerLoggingOut -= handler;
        }
    }
}
