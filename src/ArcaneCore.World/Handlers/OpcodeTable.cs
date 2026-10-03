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

/// <summary>Sets of session states an opcode is accepted in.</summary>
[Flags]
public enum SessionStates
{
    None = 0,
    CharacterSelect = 1 << SessionState.CharacterSelect,
    LoggingIn = 1 << SessionState.LoggingIn,
    InWorld = 1 << SessionState.InWorld,

    /// <summary>A character is loading or in the world (vmangos STATUS_LOGGEDIN: _player is set from the login on).</summary>
    LoggedIn = LoggingIn | InWorld,

    /// <summary>Any state after CMSG_AUTH_SESSION (vmangos STATUS_AUTHED handlers that also run in world).</summary>
    Authenticated = CharacterSelect | LoggingIn | InWorld,
}

/// <summary>A handler that runs on the session's own task; may await I/O (DB access).</summary>
public delegate Task SessionHandler(WorldSession session, byte[] payload);

/// <summary>A handler that runs on the world thread against the session's in-world player.</summary>
public delegate void WorldHandler(WorldSession session, Player player, byte[] payload);

/// <summary>How one opcode is accepted and dispatched.</summary>
public sealed record OpcodeHandler(WorldOpcode Opcode, SessionStates AllowedStates, SessionHandler? Session, WorldHandler? World)
{
    public bool AllowsState(SessionState state) => (AllowedStates & (SessionStates)(1 << (int)state)) != 0;
}

/// <summary>
/// Opcode → handler registry. Session handlers serve the character screen; world handlers
/// are queued per session and run inside the map update (vmangos PACKET_PROCESS_MAP).
/// </summary>
public sealed class OpcodeTable
{
    // Filled on one thread while the daemon starts, then only read (by every session task and the
    // world thread). Nothing mutates a dictionary that readers can reach: a code hot reload builds
    // a new table (WithGroups) and swaps the whole reference (Replace), so a reader sees the old
    // handlers or the new ones, never a half-filled table.
    private volatile Dictionary<WorldOpcode, OpcodeHandler> _handlers = [];
    private volatile bool _sealed;

    /// <summary>Register a handler that runs on the session task in the given states.</summary>
    public void OnSession(WorldOpcode opcode, SessionStates allowedStates, SessionHandler handler)
        => Add(new OpcodeHandler(opcode, allowedStates, handler, null));

    /// <summary>Register an in-world handler (runs on the world thread).</summary>
    public void OnWorld(WorldOpcode opcode, WorldHandler handler)
        => Add(new OpcodeHandler(opcode, SessionStates.InWorld, null, handler));

    public bool TryGet(WorldOpcode opcode, out OpcodeHandler handler)
        => _handlers.TryGetValue(opcode, out handler!);

    public int Count => _handlers.Count;

    /// <summary>
    /// A new, unpublished table holding this table's handlers plus the handlers of
    /// <paramref name="fresh"/> for opcodes this table does not have (code hot reload: a group
    /// that only exists after an edit, or an opcode an edited group now registers). Handlers this
    /// table already has are kept as they are. This table is not modified.
    /// </summary>
    public OpcodeTable WithNewHandlersFrom(OpcodeTable fresh, out IReadOnlyList<WorldOpcode> added)
    {
        var merged = new Dictionary<WorldOpcode, OpcodeHandler>(_handlers);
        var addedOpcodes = new List<WorldOpcode>();
        foreach ((WorldOpcode opcode, OpcodeHandler handler) in fresh._handlers)
        {
            if (merged.TryAdd(opcode, handler))
            {
                addedOpcodes.Add(opcode);
            }
        }

        added = addedOpcodes;
        return new OpcodeTable { _handlers = merged };
    }

    /// <summary>An unpublished copy of this table (the state to restore if a swap has to be undone).</summary>
    public OpcodeTable Copy() => new() { _handlers = new Dictionary<WorldOpcode, OpcodeHandler>(_handlers) };

    /// <summary>
    /// Make this table serve <paramref name="candidate"/>'s handlers from now on (an atomic
    /// reference swap; safe against concurrent <see cref="TryGet"/>). The candidate is sealed:
    /// it, and this table, must not be registered into afterwards, because readers can now reach
    /// the dictionary.
    /// </summary>
    public void Replace(OpcodeTable candidate)
    {
        candidate._sealed = true;
        _sealed = true;
        _handlers = candidate._handlers;
    }

    private void Add(OpcodeHandler handler)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("the opcode table was swapped in and is read-only");
        }

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
