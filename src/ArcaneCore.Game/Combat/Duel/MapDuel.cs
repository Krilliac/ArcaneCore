using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The per-map duel tick (vmangos Player::Update, Player.cpp:1126-1140): a finished duel object is dropped, then the start timer is
/// checked (<see cref="DuelService.UpdateDuelFlag"/>), then the distance (<see cref="DuelService.CheckDistance"/>). It runs after combat
/// (order 10) like Player::Update runs after the unit update. A player leaving the map for another one (a far teleport) ends the duel as fled
/// (vmangos Player::TeleportTo, Player.cpp:1878-1883). A world without a <see cref="DuelService"/> does nothing. World thread.
/// </summary>
[DefaultMapUpdater(Order = 10)]
public sealed class MapDuel : IMapUpdater
{
    private readonly WorldRuntime _world;

    internal MapDuel(Map map, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(map);
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <inheritdoc/>
    public void Update(Map map, uint diffMs)
    {
        DuelService? service = DuelService.Find(_world);
        if (service is null)
        {
            return;
        }

        long now = service.Clock();
        foreach (Player player in map.Players.ToArray())
        {
            if (player.Duel is null)
            {
                continue;
            }

            if (player.Duel.Finished)
            {
                // "Delay delete duel" (Player.cpp:1132-1137): the finished object lives until the owner's next update.
                player.Duel = null;
                continue;
            }

            service.UpdateDuelFlag(player, now);
            service.CheckDistance(player, now);
        }
    }

    /// <inheritdoc/>
    public void OnPlayerRemoved(Map map, Player player)
        => DuelService.Find(_world)?.Complete(player, DuelCompleteType.Fled);
}
