using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Honor;

/// <summary>
/// A world feature that reacts when a real player inspects an autonomous playerbot: after <see cref="InspectHandlers"/> answered a
/// CMSG_INSPECT or MSG_INSPECT_HONOR_STATS for a target whose session is a playerbot (the inspector's own session is not). The
/// original cores have no such hook; it lets the bot pack keep the target within the client's 10-yard range for reading another
/// player's equipment while the inspection window is open. Picked up when the feature is an <see cref="Features.IWorldFeature"/>;
/// called on the world thread after the reply was sent.
/// </summary>
public interface IPlayerInspectReceiver
{
    void Inspected(Player inspector, Player target);
}
