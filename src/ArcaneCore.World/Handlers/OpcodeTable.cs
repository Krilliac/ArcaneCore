using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Handlers;

/// <summary>Where a session is in its lifecycle (vmangos SessionStatus, simplified).</summary>
public enum SessionState
{
    /// <summary>Connected, CMSG_AUTH_SESSION not yet accepted.</summary>
    Connected,

    /// <summary>Authenticated, at the character screen (vmangos STATUS_AUTHED).</summary>
    CharacterSelect,

    /// <summary>CMSG_PLAYER_LOGIN accepted, character loading / entering the map.</summary>
    LoggingIn,

    /// <summary>The player is in the world (vmangos STATUS_LOGGEDIN).</summary>
    InWorld,

    /// <summary>The connection is closing or closed.</summary>
    Closed,
}

/// <summary>A handler that runs on the session's own task; may await I/O (DB access).</summary>
public delegate Task SessionHandler(WorldSession session, byte[] payload);

/// <summary>A handler that runs on the world thread against the session's in-world player.</summary>
public delegate void WorldHandler(WorldSession session, Player player, byte[] payload);

/// <summary>How one opcode is accepted and dispatched.</summary>
public sealed record OpcodeHandler(WorldOpcode Opcode, SessionState RequiredState, SessionHandler? Session, WorldHandler? World);

/// <summary>
/// Opcode → handler registry. Session handlers serve the character screen; world handlers
/// are queued per session and run inside the map update (vmangos PACKET_PROCESS_MAP).
/// </summary>
public sealed class OpcodeTable
{
    private readonly Dictionary<WorldOpcode, OpcodeHandler> _handlers = [];

    /// <summary>Register a character-screen handler (runs on the session task, state must match).</summary>
    public void OnSession(WorldOpcode opcode, SessionState requiredState, SessionHandler handler)
        => Add(new OpcodeHandler(opcode, requiredState, handler, null));

    /// <summary>Register an in-world handler (runs on the world thread).</summary>
    public void OnWorld(WorldOpcode opcode, WorldHandler handler)
        => Add(new OpcodeHandler(opcode, SessionState.InWorld, null, handler));

    public bool TryGet(WorldOpcode opcode, out OpcodeHandler handler)
        => _handlers.TryGetValue(opcode, out handler!);

    public int Count => _handlers.Count;

    private void Add(OpcodeHandler handler)
    {
        if (!_handlers.TryAdd(handler.Opcode, handler))
        {
            throw new InvalidOperationException($"{WorldOpcodeNames.GetName(handler.Opcode)} registered twice");
        }
    }
}

/// <summary>A feature's set of opcode handlers.</summary>
public interface IOpcodeHandlerGroup
{
    void Register(OpcodeTable table);
}
