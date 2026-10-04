using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Quest sharing opcodes (vmangos Handlers/QuestHandler.cpp:332-381, 403-474): CMSG_PUSHQUESTTOPARTY (0x019D, u32 quest),
/// CMSG_QUEST_CONFIRM_ACCEPT (0x019B, u32 quest) and MSG_QUEST_PUSH_RESULT (0x0276, u64 guid, u8 message), with the layouts of
/// gtker/wow_messages quest/*.wowm. A payload of the wrong length is a protocol error like every quest handler.
/// </summary>
public sealed class QuestShareHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgPushquesttoparty, PushToParty);
        table.OnWorld(WorldOpcode.CmsgQuestConfirmAccept, ConfirmAccept);
        table.OnWorld(WorldOpcode.MsgQuestPushResult, PushResult);
    }

    private static QuestNpcServices Services(WorldSession session) => session.Services.GetRequiredService<QuestNpcFeature>().Services;

    private static void PushToParty(WorldSession session, Player player, byte[] payload)
        => Services(session).PushQuestToParty(player, ReadQuest(payload));

    private static void ConfirmAccept(WorldSession session, Player player, byte[] payload)
        => Services(session).ConfirmAcceptQuest(player, ReadQuest(payload));

    private static void PushResult(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length != 9)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "MSG_QUEST_PUSH_RESULT requires exactly 9 bytes");
        }

        // vmangos ignores the guid of the packet and answers for the player itself (QuestHandler.cpp:461-474).
        Services(session).QuestPushResult(player, payload[8]);
    }

    private static uint ReadQuest(byte[] payload)
    {
        if (payload.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "quest sharing requires exactly 4 bytes");
        }

        return new PacketReader(payload).ReadUInt32();
    }
}
