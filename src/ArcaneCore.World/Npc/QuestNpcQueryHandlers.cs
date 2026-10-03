using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Immutable quest/NPC content queries. vmangos/core commit 4b3d241cffe245a1f68da11380bce96c23db48c0:
/// src/game/Server/Protocol/Opcodes.cpp registers both as STATUS_LOGGEDIN/PACKET_PROCESS_DB_QUERY;
/// src/game/Server/Packets/Quest.cpp QueryQuest and Npc.cpp NpcTextQuery specify request fields;
/// src/game/Handlers/QuestHandler.cpp HandleQuestQueryOpcode and QueryHandler.cpp HandleNpcTextQueryOpcode
/// specify replies. Cross-checked with gtker/wow_messages commit 70abb9deff0bb63440d8aeb4386b820653e8a176,
/// wow_message_parser/wowm/world/queries/cmsg_quest_query.wowm and cmsg_npc_text_query.wowm.
/// </summary>
public sealed class QuestNpcQueryHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgQuestQuery, SessionStates.LoggedIn, HandleQuestQuery);
        table.OnSession(WorldOpcode.CmsgNpcTextQuery, SessionStates.LoggedIn, HandleNpcTextQuery);
    }

    /// <summary>CMSG_QUEST_QUERY (0x005C): u32 quest id; unknown quests receive no response (vmangos).</summary>
    private static Task HandleQuestQuery(WorldSession session, byte[] payload)
    {
        if (payload.Length != sizeof(uint))
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "CMSG_QUEST_QUERY requires a four-byte quest id");
        }

        var reader = new PacketReader(payload);
        uint questId = reader.ReadUInt32();
        var services = session.Services.GetRequiredService<QuestNpcFeature>().Services;
        if (services.Quests.Get(questId) is { } quest)
        {
            session.Send(WorldOpcode.SmsgQuestQueryResponse,
                QuestPackets.QueryResponse(quest, services.Options.RateDropMoney).AsSpan());
        }

        return Task.CompletedTask;
    }

    /// <summary>CMSG_NPC_TEXT_QUERY (0x017F): u32 text id, u64 guid (unused by vmangos' reply).</summary>
    private static Task HandleNpcTextQuery(WorldSession session, byte[] payload)
    {
        if (payload.Length != sizeof(uint) + sizeof(ulong))
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "CMSG_NPC_TEXT_QUERY requires a text id and an eight-byte guid");
        }

        var reader = new PacketReader(payload);
        uint textId = reader.ReadUInt32();
        _ = reader.ReadUInt64();
        var services = session.Services.GetRequiredService<QuestNpcFeature>().Services;
        session.Send(WorldOpcode.SmsgNpcTextUpdate, services.NpcTextQueryResponse(textId).AsSpan());
        return Task.CompletedTask;
    }
}
