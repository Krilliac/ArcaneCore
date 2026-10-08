using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// Boarding and leaving ships from client movement (vmangos <c>WorldSession::HandleMoverRelocation</c>,
/// MovementHandler.cpp:1075-1102, re-implemented):
/// <list type="bullet">
/// <item>Aboard already: the world position is recomputed from the offset the client sends and the ship's own position,
/// before the block is stored (the client's world coordinates are not trusted), and the "just boarded" mark is cleared.</item>
/// <item>Not aboard, ONTRANSPORT set: the ship named by the block is looked up on the player's map and boarded, keeping the
/// client's offset; the player is marked "just boarded", and a CMSG_MOVE_TIME_SKIPPED that comes before its next movement
/// aboard sends the ship again (a 1.12 client quirk). An unknown GUID boards nothing, as in the reference.</item>
/// <item>Aboard, ONTRANSPORT cleared: the player leaves the ship.</item>
/// </list>
/// Does nothing while no <see cref="TransportSystem"/> is registered for the world (transports disabled).
/// </summary>
[MovementObserver(Order = -100)]
internal sealed class TransportMovementObserver : IClientMovementObserver
{
    public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
        if (context.Player.Transport is not { } ship || !incoming.HasFlag(MovementFlags.OnTransport))
        {
            return;
        }

        float x = incoming.TransportX, y = incoming.TransportY, z = incoming.TransportZ, o = incoming.TransportOrientation;
        ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
        incoming.X = x;
        incoming.Y = y;
        incoming.Z = z;
        incoming.Orientation = o;
    }

    public void AfterApply(MovementObserverContext context, in MovementInfo previous)
    {
        Entities.Player player = context.Player;
        if (player.Movement.HasFlag(MovementFlags.OnTransport))
        {
            if (player.Transport is null)
            {
                if (player.Map is { } map
                    && TransportSystem.Of(context.World) is { } system
                    && system.Find(map, new ObjectGuid(player.Movement.TransportGuid)) is { } ship)
                {
                    system.BoardFromClient(ship, player);
                }
            }
            else
            {
                // vmangos HandleMoverRelocation (MovementHandler.cpp:1087-1089): moving aboard ends "just boarded".
                TransportSystem.Of(context.World)?.ClearJustBoarded(player);
            }
        }
        else if (player.Transport is { } ship)
        {
            ship.RemovePassenger(player);
        }
    }
}
