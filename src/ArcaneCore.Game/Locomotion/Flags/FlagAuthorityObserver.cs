using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The server's say over the flags of an incoming movement block (vmangos HandleMoverRelocation,
/// MovementHandler.cpp:1062-1071): <see cref="MovementInfo.CorrectData"/> removes contradictory flags, then a unit the
/// server has rooted keeps its Root flag whatever the client claims ("Prevent client from removing root flag").
/// It runs first (order 10) so later observers see the corrected block. Every other flag is the client's own; the
/// stricter flag tests of vmangos' anticheat are not part of retail behaviour and are not delivered.
/// </summary>
[MovementObserver(Order = 10)]
public sealed class FlagAuthorityObserver : IClientMovementObserver
{
    public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
        incoming.CorrectData();

        // The previously stored block carries the server's Root flag (set when the root was acknowledged or enforced).
        if (previous.HasFlag(MovementFlags.Root) && !incoming.HasFlag(MovementFlags.Root))
        {
            incoming.Flags |= MovementFlags.Root;
        }
    }
}
