namespace ArcaneCore.Game.Locomotion;

public sealed partial class LocomotionState
{
    private uint _rejectMovementUntilMs;
    private bool _rejectingMovement;

    /// <summary>
    /// vmangos <c>WorldSession::RejectMovementPacketsFor</c> (MovementHandler.cpp:1035-1040): client movement packets received up to
    /// <paramref name="ms"/> after <paramref name="nowMs"/> are dropped, so a packet the client sent before it saw the server's flag
    /// change cannot overwrite the new flags. A later deadline replaces an earlier one, never the other way. <paramref name="nowMs"/>
    /// is on the receive clock (<c>Clock.Milliseconds</c> truncated to 32 bits, the clock of a packet's received time). World thread.
    /// </summary>
    public void RejectMovementPacketsFor(uint nowMs, uint ms)
    {
        uint deadline = unchecked(nowMs + ms);
        if (!_rejectingMovement || unchecked((int)(deadline - _rejectMovementUntilMs)) > 0)
        {
            _rejectMovementUntilMs = deadline;
            _rejectingMovement = true;
        }
    }

    /// <summary>Whether a movement packet received at <paramref name="receivedMs"/> falls inside the window (vmangos <c>stime &lt;= m_moveRejectTime</c>, MovementHandler.cpp:295). World thread.</summary>
    public bool ShouldRejectMovement(uint receivedMs)
    {
        if (!_rejectingMovement)
        {
            return false;
        }

        if (unchecked((int)(_rejectMovementUntilMs - receivedMs)) >= 0)
        {
            return true;
        }

        _rejectingMovement = false; // expired: forget it, so the 32-bit clock wrapping never revives it
        return false;
    }
}
