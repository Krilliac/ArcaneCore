using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Entities;

public abstract partial class Unit
{
    /// <summary>
    /// The ship or zeppelin this unit rides (vmangos <c>WorldObject::m_transport</c>), or null. Set and cleared only by
    /// <see cref="ShipTransport.AddPassenger"/> and <see cref="ShipTransport.RemovePassenger"/> (world thread).
    /// </summary>
    public ShipTransport? Transport { get; internal set; }

    /// <summary>
    /// vmangos <c>MovementInfo::SetTransportData</c> plus <c>AddMovementFlag(MOVEFLAG_ONTRANSPORT)</c>: the transport GUID and
    /// this unit's offset on it, in the transport's frame.
    /// </summary>
    internal void SetTransportData(ObjectGuid transport, float x, float y, float z, float orientation)
    {
        _movement.Flags |= MovementFlags.OnTransport;
        _movement.TransportGuid = transport.Value;
        _movement.TransportX = x;
        _movement.TransportY = y;
        _movement.TransportZ = z;
        _movement.TransportOrientation = orientation;
    }

    /// <summary>
    /// vmangos <c>MovementInfo::ClearTransportData</c>: forget the GUID and offset. The flag is left alone, as in the
    /// reference (the caller removes it where vmangos does).
    /// </summary>
    internal void ClearTransportData()
    {
        _movement.TransportGuid = 0;
        _movement.TransportX = 0;
        _movement.TransportY = 0;
        _movement.TransportZ = 0;
        _movement.TransportOrientation = 0;
    }

    /// <summary>
    /// Move a passenger with its ship (vmangos <c>Map::PlayerRelocation</c> / <c>CreatureRelocation</c> from
    /// <c>GenericTransport::UpdatePassengerPosition</c>): the stored movement block and the world position, without the
    /// side effects of a client move. The client time stamp is cleared (<c>m_movementInfo.ctime = 0</c>).
    /// </summary>
    internal void RelocateOnTransport(float x, float y, float z, float orientation)
    {
        _movement.X = x;
        _movement.Y = y;
        _movement.Z = z;
        _movement.Orientation = orientation;
        SetPosition(x, y, z, orientation);
    }
}
