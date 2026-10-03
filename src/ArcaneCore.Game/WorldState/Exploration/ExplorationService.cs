using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.WorldState.Exploration;

/// <summary>
/// vmangos <c>Player::CheckAreaExploreAndOutdoor</c>' exploration half (Player.cpp:6089-6204): when a
/// living player stands on a terrain cell whose area flag it has not discovered, set the bit,
/// grant exploration XP, and send SMSG_EXPLORATION_EXPERIENCE. The outdoor / indoor aura handling
/// and inn-leave rest belong to other systems. World thread.
/// <para>
/// Order as vmangos: the bit is set; an unknown area is logged and nothing else happens; otherwise
/// the XP is given (SMSG_LOG_XPGAIN) and then SMSG_EXPLORATION_EXPERIENCE is sent, always, even
/// with 0 XP. Not delivered: waiting for the first-login cinematic and the taxi-flight skip (there
/// is neither a cinematic nor a taxi system yet).
/// </para>
/// </summary>
public sealed class ExplorationService(WorldStateHooks hooks, Func<IPlayerExperience?> experience, Func<uint> maxPlayerLevel, ILogger logger) : IExplorationChecker
{
    public void CheckAreaExplore(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsAlive || player.Map is not { } map)
        {
            return;
        }

        WorldStateHooks state = hooks;
        IZoneLocator locator = state.Locator;
        // Without area data and terrain there are no flags to read (a development world): nothing to discover.
        if (!locator.CanDeriveZones)
        {
            return;
        }

        uint areaFlag = locator.GetAreaFlag(map, player);
        switch (ExploredZonesFields.Mark(player, areaFlag))
        {
            case ExploreOutcome.OutOfRange:
                logger.LogError(
                    "Wrong area flag {Flag} in map data for (X: {X} Y: {Y}) point to field PLAYER_EXPLORED_ZONES_1 + {Offset} ({Offset} must be < {Size}).",
                    areaFlag, player.X, player.Y, areaFlag / 32, areaFlag / 32, ExploredZones.WordCount);
                return;
            case ExploreOutcome.Discovered:
                break;
            default:
                return;
        }

        AreaTemplate? area = locator.FindByAreaFlag(areaFlag, map.MapId);
        if (area is null)
        {
            logger.LogError("PLAYER: Player {Guid} discovered unknown area (x: {X} y: {Y} map: {Map}", player.Guid.Low, player.X, player.Y, map.MapId);
        }
        else
        {
            uint xp = 0;
            if (area.AreaLevel > 0 && player.Level < maxPlayerLevel())
            {
                xp = ExplorationXp.Compute(player.Level, area.AreaLevel, state.ExplorationBaseXp, state.ExplorationSettings.RateXp, maxPlayerLevel());
                experience()?.GiveXp(player, xp);
            }

            // Exploration packet should be sent even if no XP is gained.
            player.Session.Send(WorldOpcode.SmsgExplorationExperience, ExplorationPackets.Build(area.Entry, xp));
        }

        state.ExploredZonesSink?.Changed(player);
    }
}

