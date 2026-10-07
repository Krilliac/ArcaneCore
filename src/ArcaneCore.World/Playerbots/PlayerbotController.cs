using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Drives a managed bot in scripted mode instead of <see cref="PlayerbotBrain"/>. While a controller is attached the
/// brain is not updated at all (no goals, no packets drained, no actions); the controller owns the bot's session
/// actions and its outbound packet queue. Autonomous mode (no controller) is the default and is unchanged.
/// </summary>
public interface IPlayerbotController
{
    /// <summary>World thread, once per world tick while the bot is in the world and scripted.</summary>
    void Tick(PlayerbotControllerContext context, uint elapsedMs);

    /// <summary>World thread or the stopping thread: the controller was replaced or the bot stopped.</summary>
    void Detached(Guid botId);
}

/// <summary>What a controller may touch for one scripted bot during <see cref="IPlayerbotController.Tick"/>.</summary>
public sealed class PlayerbotControllerContext
{
    internal PlayerbotControllerContext(Guid botId, WorldSession session)
    {
        BotId = botId;
        Session = session;
    }

    public Guid BotId { get; }

    /// <summary>The bot's ordinary socketless world session.</summary>
    public WorldSession Session { get; }

    public Player? Player => Session.Player;

    public WorldRuntime World => Session.World;

    /// <summary>Run one client opcode through the real world handler (world thread; same gates as the brain's actions).</summary>
    public bool TryAction(WorldOpcode opcode, byte[] payload) => Session.TryManagedAction(opcode, payload);

    /// <summary>
    /// Acknowledge the server's own pending teleports and movement-flag/speed orders the way a client does
    /// (the brain's acknowledgement path). Returns true when something was acknowledged.
    /// </summary>
    public bool AcknowledgeServerOrders()
        => Session.Player is { } player && PlayerbotMovementControl.Update(Session, player);
}
