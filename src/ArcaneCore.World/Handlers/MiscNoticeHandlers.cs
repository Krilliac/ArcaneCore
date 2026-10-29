using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Client requests vmangos answers trivially that had no handler here (docs/reference/opcodes.md "unhandled"): the Looking For Group
/// window's poll (MiscHandler.cpp:277-282), the bug and suggestion report (MiscHandler.cpp:566-571, logged), and three notices vmangos
/// accepts without effect: CMSG_PLAYER_LOGOUT (:342-344), CMSG_SET_TARGET_OBSOLETE (HandleSetMouseOverTargetOpcode, :433-435) and
/// CMSG_QUESTGIVER_QUEST_AUTOLAUNCH (QuestHandler.cpp:399-401). World thread.
/// </summary>
public sealed class MiscNoticeHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.MsgLookingForGroup, HandleLookingForGroup);
        table.OnWorld(WorldOpcode.CmsgBug, HandleBug);
        table.OnWorld(WorldOpcode.CmsgPlayerLogout, static (_, _, _) => { });
        table.OnWorld(WorldOpcode.CmsgSetTargetObsolete, static (_, _, _) => { });
        table.OnWorld(WorldOpcode.CmsgQuestgiverQuestAutolaunch, static (_, _, _) => { });
    }

    /// <summary>MSG_LOOKING_FOR_GROUP (empty) → MSG_LOOKING_FOR_GROUP u32 0: no LFG listings (the 1.12 server has no LFG system).</summary>
    internal static void HandleLookingForGroup(WorldSession session, Player player, byte[] payload)
        => session.Send(WorldOpcode.MsgLookingForGroup, BitConverter.GetBytes(0u));

    /// <summary>
    /// CMSG_BUG: u32 suggestion, u32 content length, content, u32 type length, type (vmangos WorldPackets::Misc::Bug). Logged only, as
    /// vmangos does; a short or malformed packet is ignored.
    /// </summary>
    internal static void HandleBug(WorldSession session, Player player, byte[] payload)
    {
        if (!TryParseBug(payload, out bool suggestion, out string content, out string type))
        {
            return;
        }

        session.Services.GetService<ILoggerFactory>()?.CreateLogger<MiscNoticeHandlers>().LogInformation("Player {Player} reported a {Kind}: [{Type}] {Content}", player.Name, suggestion ? "suggestion" : "bug",
            LogSafe.Escape(type, 64), LogSafe.Escape(content, 2000)); // client text: CR/LF and control characters escaped (log forging)
    }

    internal static bool TryParseBug(byte[] payload, out bool suggestion, out string content, out string type)
    {
        suggestion = false;
        content = type = string.Empty;
        try
        {
            var reader = new PacketReader(payload);
            suggestion = reader.ReadUInt32() != 0;
            reader.ReadUInt32(); // content length (the string is NUL-terminated as well)
            content = reader.ReadCString();
            reader.ReadUInt32();
            type = reader.ReadCString();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
