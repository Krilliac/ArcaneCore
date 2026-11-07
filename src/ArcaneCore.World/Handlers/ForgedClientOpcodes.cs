using System.Collections.Frozen;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Client opcodes a real 1.12.1 (build 5875) client never sends, registered as forged-client tripwires. A static check of the 1.12.1 Wow.exe found no
/// send path that writes any of them (320 send sites traced for CMSG_BOT_DETECTED2 alone: opcode 0x17 never reaches a packet write; the others have
/// no sender in the client's packet writers either), and vmangos drops each as <c>INVALID_PACKET(..., UnhandleReason::Unhandled)</c> while
/// mangos-classic routes them to <c>Handle_NULL</c> with <c>STATUS_NEVER</c>. So a packet that arrives is not a player: it is a modified client,
/// a packet tool or a bot. They are registered through <see cref="OpcodeTable.OnStripped"/> like <see cref="StrippedDevOpcodes"/>, which gives them the
/// same report-only path: the payload is never read (nothing here has a body format to parse), nothing is queued for the world thread and nothing is
/// answered; a player's packet is a Warning-level <c>packet/dev-opcode</c> finding and a log line naming the account, a staff member's is audited, and
/// none of it counts as an unhandled opcode. The drop never kicks, bans or scores: a tripwire, not a punishment, so a wrong classification cannot
/// hurt an honest player.
/// <para>
/// Kept apart from <see cref="StrippedDevOpcodes"/> (a pinned inventory of the stripped developer, GM and cheat opcodes) because the reason differs:
/// those have a name that says developer command, these are gameplay-named opcodes whose absence from the client is the evidence. The registry page
/// (docs/reference/opcodes.md) shows them as <c>tripwire</c>. Not run against a live client; "no send code found" does not prove no path reaches one,
/// and a real 1.12.1 client check that none ever arrives stays listed for the client acceptance run (docs/areas/protocol-decisions.md).
/// </para>
/// </summary>
public sealed class ForgedClientOpcodes : IOpcodeHandlerGroup
{
    private static readonly WorldOpcode[] Opcodes =
    [
        WorldOpcode.CmsgBotDetected2, // CMSG_BOT_DETECTED2 0x0017, vmangos Opcodes.cpp:97, mangos-classic Opcodes.cpp:51 (Handle_NULL)
        WorldOpcode.MsgUpdateGroupMembers, // MSG_UPDATE_GROUP_MEMBERS 0x0080, vmangos Opcodes.cpp:209, mangos-classic Opcodes.cpp:156 (UMSG_)
        WorldOpcode.MsgUpdateGuild, // MSG_UPDATE_GUILD 0x0094, vmangos Opcodes.cpp:229, mangos-classic Opcodes.cpp:176 (UMSG_)
        WorldOpcode.MsgMoveSetRawPositionAck, // MSG_MOVE_SET_RAW_POSITION_ACK 0x00E0, vmangos Opcodes.cpp:305, mangos-classic Opcodes.cpp:252
        WorldOpcode.CmsgAutoequipGroundItem, // CMSG_AUTOEQUIP_GROUND_ITEM 0x0106, vmangos Opcodes.cpp:343, mangos-classic Opcodes.cpp:290
        WorldOpcode.CmsgDropItem, // CMSG_DROP_ITEM 0x0110, vmangos Opcodes.cpp:353
        WorldOpcode.CmsgNewSpellSlot, // CMSG_NEW_SPELL_SLOT 0x012D, vmangos Opcodes.cpp:382, mangos-classic Opcodes.cpp:329
        WorldOpcode.MsgDeleteGuildCharter, // MSG_DELETE_GUILD_CHARTER 0x02C0, vmangos Opcodes.cpp:800, mangos-classic Opcodes.cpp:732 (UMSG_)
        WorldOpcode.CmsgLuaUsage, // CMSG_LUA_USAGE 0x0323, vmangos Opcodes.cpp:916, mangos-classic Opcodes.cpp:831
        WorldOpcode.CmsgGmUpdateTicketStatus, // CMSG_GM_UPDATE_TICKET_STATUS 0x0327, vmangos Opcodes.cpp:920, mangos-classic Opcodes.cpp:835
        WorldOpcode.MsgSetDungeonDifficulty, // MSG_SET_DUNGEON_DIFFICULTY 0x0329, vmangos Opcodes.cpp:922, mangos-classic Opcodes.cpp:837 (the client only receives it: handler at 0x4E7E3C, no send found)
        WorldOpcode.MsgDelayGhostTeleport, // MSG_DELAY_GHOST_TELEPORT 0x032E, vmangos Opcodes.cpp:927, mangos-classic Opcodes.cpp:842
    ];

    /// <summary>Every forged-client tripwire opcode.</summary>
    public static FrozenSet<WorldOpcode> All { get; } = Opcodes.ToFrozenSet();

    public void Register(OpcodeTable table)
    {
        foreach (WorldOpcode opcode in Opcodes)
        {
            table.OnStripped(opcode);
        }
    }
}
