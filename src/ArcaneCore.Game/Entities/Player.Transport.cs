using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>
    /// The ship seat stored with the character (vmangos <c>characters.transport_guid</c>, <c>transport_x</c> .. <c>transport_o</c>),
    /// read at login and consumed by the transport system when the player enters its first map (vmangos Player::LoadFromDB,
    /// Player.cpp:14794-14838). Null on land, and after it was consumed. Never written back by itself: without a transport
    /// system the seat is dropped at the next save, as the player is not on a ship.
    /// </summary>
    public TransportSeat? LoginTransportSeat { get; internal set; }

    /// <summary>The seat a player had when it left the world aboard (taken off the ship before its final snapshot).</summary>
    internal TransportSeat? LogoutTransportSeat { get; set; }

    /// <summary>The seat a snapshot stores: the ship the player rides now, or the one it rode when it left the world.</summary>
    internal TransportSeat? CurrentTransportSeat => Transport is { } ship
        ? new TransportSeat(ship.Guid.Low, Movement.TransportX, Movement.TransportY, Movement.TransportZ, Movement.TransportOrientation)
        : LogoutTransportSeat;
}
