using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Teleport;

/// <summary>
/// The world daemon's map and teleport feature (docs/areas/grid-terrain.md): at startup it
/// attaches the world's <see cref="WorldMaps"/> (terrain from <c>World:Maps:DataDirectory</c>),
/// fills it from the world database's map tables when an <see cref="IMapDataStore"/> is
/// registered, and creates the <see cref="TeleportService"/> that the teleport handlers and
/// commands use. A player that logs out mid-teleport is forgotten.
/// </summary>
public sealed class TeleportFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<TeleportFeature> logger) : IWorldFeature
{
    private TeleportService? _service;
    private WorldMaps? _maps;

    /// <summary>The teleport state machine (available after <see cref="Attach"/>).</summary>
    public TeleportService Teleports => _service ?? throw new InvalidOperationException("the teleport feature is not attached");

    /// <summary>The world's map services (available after <see cref="Attach"/>).</summary>
    public WorldMaps Maps => _maps ?? throw new InvalidOperationException("the teleport feature is not attached");

    public void Attach(WorldRuntime world)
    {
        _maps = WorldMaps.Attach(world, logger);
        using (IServiceScope scope = scopes.CreateScope())
        {
            IMapDataStore? store = scope.ServiceProvider.GetService<IMapDataStore>();
            if (store is not null)
            {
                MapContent content = store.LoadAsync().GetAwaiter().GetResult();
                foreach (string skipped in _maps.Load(content))
                {
                    logger.LogWarning("{Message}", skipped);
                }

                logger.LogInformation(
                    "loaded {Maps} maps, {Areas} areas, {Triggers} area triggers, {Teleports} area trigger teleports, {Teles} teleport locations",
                    _maps.Registry.Count, content.Areas.Count, _maps.AreaTriggers.Count, content.AreaTriggerTeleports.Count, _maps.GameTeles.Count);
            }
            else
            {
                logger.LogInformation("no map data store registered; using the default continents");
            }
        }

        _service = new TeleportService(world, SendBeforeAddToMap, SendAfterAddToMap, logger);
        world.PlayerLoggingOut += _service.Forget;
        _service.HonorlessTargetDue += CastHonorlessTarget;
    }

    /// <summary>Spell 2479, Honorless Target (vmangos Player::ProcessDelayedOperations, Player.cpp:2183-2184).</summary>
    public const uint SpellHonorlessTarget = 2479;

    /// <summary>
    /// vmangos DELAYED_CAST_HONORLESS_TARGET: cast after a same-map teleport that left combat, and after a far teleport only when the player
    /// arrived in a PvP-enforced area (MovementHandler.cpp:193-195: an enemy capital, anywhere on a PvP realm, or a battleground).
    /// </summary>
    private void CastHonorlessTarget(Player player, bool far)
    {
        if (far && !PvpAreaState.IsInEnforcedArea(player) && BattlegroundManager.TypeOfMap(player.MapId) == BattlegroundType.None)
        {
            return;
        }

        services.GetService<SpellFeature>()?.System.CastSpell(player, SpellHonorlessTarget, SpellCastTargets.ForSelf(), triggered: true);
    }

    private static void SendBeforeAddToMap(Player player)
    {
        if (player.Session is WorldSession session)
        {
            LoginSequence.SendInitialPacketsBeforeAddToMap(session, player, LoginPackets.BuildTutorialFlags(session.Settings.Tutorials));
        }
    }

    private static void SendAfterAddToMap(Player player)
    {
        if (player.Session is WorldSession session)
        {
            LoginSequence.SendInitialPacketsAfterAddToMap(session, player);
        }
    }
}
