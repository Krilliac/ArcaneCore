using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Entities;

public abstract partial class Unit
{
    /// <summary>
    /// Assign the whole movement flag word the server decided (vmangos <c>m_movementInfo.moveFlags = ...</c>, Player::SetFly,
    /// Player.cpp:4543/4547). Unlike <see cref="AddMovementFlags"/> and <see cref="RemoveMovementFlags"/> it also drops every other
    /// flag. World thread.
    /// </summary>
    public void SetMovementFlags(MovementFlags flags) => _movement.Flags = flags;

    /// <summary>
    /// vmangos <c>MovementInfo::SetAsServerSide</c> (MovementInfo.h:205-216): the stored block's time becomes the server time and is
    /// never allowed to repeat or go back ("preserve order of server side packets"), so a server-sent block is ordered after the
    /// client block it follows. World thread.
    /// </summary>
    public void StampServerMovementTime(uint serverTimeMs)
    {
        uint old = _movement.Time;
        _movement.Time = unchecked((int)(serverTimeMs - old)) <= 0 ? unchecked(old + 1) : serverTimeMs;
    }
}
