using System.Collections.Frozen;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The opcode rules of the packet tools (docs/areas/map-feed.md, "Packet tools"): which values are opcodes, which way each goes, and the
/// authentication and session-lifecycle opcodes the tools never inject or send. Names and values are the build-5875 table the server
/// itself uses (<see cref="WorldOpcodeNames"/>, generated from vmangos Opcodes_1_12_1.h and checked against gtker/wow_messages; see the
/// header of src/ArcaneCore.Protocol/WorldOpcode.g.cs). Pure; any thread.
/// </summary>
internal static class PacketToolOpcodes
{
    /// <summary>
    /// The largest payload an injection may carry: the largest packet a client can put on the wire. The size field counts the 4-byte
    /// opcode, and the server refuses a size above 0x2800 (<see cref="WorldSession.MaxClientPacketSize"/>).
    /// </summary>
    public const int MaxInjectPayload = WorldSession.MaxClientPacketSize - 4;

    /// <summary>The largest payload a tool may send to a client: a tools limit, well under the SMSG size field (<see cref="WorldSession.MaxServerPayload"/>).</summary>
    public const int MaxSendPayload = 16 * 1024;

    /// <summary>
    /// Client opcodes the tools never inject: authentication (<c>CMSG_AUTH_SESSION</c>, <c>CMSG_WARDEN_DATA</c>), the connection's own
    /// keep-alive (<c>CMSG_PING</c>, answered below the dispatcher), the character screen and login (<c>CMSG_CHAR_ENUM</c>,
    /// <c>CMSG_CHAR_CREATE</c>, <c>CMSG_CHAR_DELETE</c>, <c>CMSG_CHAR_RENAME</c>, <c>CMSG_PLAYER_LOGIN</c>), logout
    /// (<c>CMSG_LOGOUT_REQUEST</c>, <c>CMSG_LOGOUT_CANCEL</c>, <c>CMSG_PLAYER_LOGOUT</c>) and the teleport and world-port acknowledgements
    /// (<c>MSG_MOVE_TELEPORT_ACK</c>, <c>MSG_MOVE_WORLDPORT_ACK</c>): each moves the session through its lifecycle, which a tool must not
    /// drive behind the client's back.
    /// </summary>
    public static readonly FrozenSet<WorldOpcode> RefusedInject = new[]
    {
        WorldOpcode.CmsgAuthSession,
        WorldOpcode.CmsgWardenData,
        WorldOpcode.CmsgPing,
        WorldOpcode.CmsgCharEnum,
        WorldOpcode.CmsgCharCreate,
        WorldOpcode.CmsgCharDelete,
        WorldOpcode.CmsgCharRename,
        WorldOpcode.CmsgPlayerLogin,
        WorldOpcode.CmsgLogoutRequest,
        WorldOpcode.CmsgLogoutCancel,
        WorldOpcode.CmsgPlayerLogout,
        WorldOpcode.MsgMoveTeleportAck,
        WorldOpcode.MsgMoveWorldportAck,
    }.ToFrozenSet();

    /// <summary>
    /// Server opcodes the tools never send: authentication (<c>SMSG_AUTH_CHALLENGE</c>, <c>SMSG_AUTH_RESPONSE</c>, <c>SMSG_WARDEN_DATA</c>,
    /// <c>SMSG_PONG</c>), the character screen and login (<c>SMSG_CHAR_ENUM</c>, <c>SMSG_CHAR_CREATE</c>, <c>SMSG_CHAR_DELETE</c>,
    /// <c>SMSG_CHAR_RENAME</c>, <c>SMSG_CHARACTER_LOGIN_FAILED</c>, <c>SMSG_LOGIN_VERIFY_WORLD</c>), logout (<c>SMSG_LOGOUT_RESPONSE</c>,
    /// <c>SMSG_LOGOUT_COMPLETE</c>, <c>SMSG_LOGOUT_CANCEL_ACK</c>), far teleports (<c>SMSG_TRANSFER_PENDING</c>, <c>SMSG_NEW_WORLD</c>,
    /// <c>SMSG_TRANSFER_ABORTED</c>) and the teleport and world-port acknowledgements (<c>MSG_MOVE_TELEPORT_ACK</c>,
    /// <c>MSG_MOVE_WORLDPORT_ACK</c>): each would move the client through a lifecycle step the server did not take.
    /// </summary>
    public static readonly FrozenSet<WorldOpcode> RefusedSend = new[]
    {
        WorldOpcode.SmsgAuthChallenge,
        WorldOpcode.SmsgAuthResponse,
        WorldOpcode.SmsgWardenData,
        WorldOpcode.SmsgPong,
        WorldOpcode.SmsgCharEnum,
        WorldOpcode.SmsgCharCreate,
        WorldOpcode.SmsgCharDelete,
        WorldOpcode.SmsgCharRename,
        WorldOpcode.SmsgCharacterLoginFailed,
        WorldOpcode.SmsgLoginVerifyWorld,
        WorldOpcode.SmsgLogoutResponse,
        WorldOpcode.SmsgLogoutComplete,
        WorldOpcode.SmsgLogoutCancelAck,
        WorldOpcode.SmsgTransferPending,
        WorldOpcode.SmsgNewWorld,
        WorldOpcode.SmsgTransferAborted,
        WorldOpcode.MsgMoveTeleportAck,
        WorldOpcode.MsgMoveWorldportAck,
    }.ToFrozenSet();

    private static readonly FrozenDictionary<string, ushort> ByName = BuildNames();

    /// <summary>Whether <paramref name="value"/> has a name in the build-5875 table.</summary>
    public static bool IsKnown(uint value) => value <= ushort.MaxValue && !Name(value).StartsWith("0x", StringComparison.Ordinal);

    /// <summary>The reference name of <paramref name="value"/>, or its hex value when it has none.</summary>
    public static string Name(uint value) => WorldOpcodeNames.GetName(value);

    /// <summary>A client-to-server opcode: <c>CMSG_</c>, or <c>MSG_</c> (both ways).</summary>
    public static bool IsClientToServer(uint value)
        => Name(value) is { } name && (name.StartsWith("CMSG_", StringComparison.Ordinal) || name.StartsWith("MSG_", StringComparison.Ordinal));

    /// <summary>A server-to-client opcode: <c>SMSG_</c>, or <c>MSG_</c> (both ways).</summary>
    public static bool IsServerToClient(uint value)
        => Name(value) is { } name && (name.StartsWith("SMSG_", StringComparison.Ordinal) || name.StartsWith("MSG_", StringComparison.Ordinal));

    /// <summary>A reference name (any case) or a number (decimal, or hex after <c>0x</c>) to its value.</summary>
    public static bool TryParse(string text, out ushort value)
    {
        string trimmed = text.Trim();
        if (ByName.TryGetValue(trimmed.ToUpperInvariant(), out value))
        {
            return true;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(trimmed.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        return ushort.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static FrozenDictionary<string, ushort> BuildNames()
    {
        var names = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (WorldOpcode opcode in Enum.GetValues<WorldOpcode>())
        {
            string name = WorldOpcodeNames.GetName(opcode);
            if (!name.StartsWith("0x", StringComparison.Ordinal))
            {
                names.TryAdd(name, (ushort)opcode);
            }
        }

        return names.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
