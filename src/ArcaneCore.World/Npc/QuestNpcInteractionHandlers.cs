using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Creature quest interactions, dispatched on the authoritative map thread.
/// vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0 Server/Packets/Quest.cpp and
/// Handlers/QuestHandler.cpp; gtker/wow_messages 70abb9deff0bb63440d8aeb4386b820653e8a176
/// quest/cmsg_questgiver_accept_quest.wowm, queries/cmsg_questgiver_query_quest.wowm,
/// quest/cmsg_questlog_remove_quest.wowm. Vanilla GUIDs are full u64, no later-build suffixes.
/// Both greeting opcodes use vmangos PrepareGossipMenu/SendPreparedGossip for the creature.
/// </summary>
public sealed class QuestNpcInteractionHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGossipHello, GossipHello);
        table.OnWorld(WorldOpcode.CmsgQuestgiverHello, GossipHello);
        table.OnWorld(WorldOpcode.CmsgQuestgiverStatusQuery, Status);
        table.OnWorld(WorldOpcode.CmsgQuestgiverQueryQuest, Details);
        table.OnWorld(WorldOpcode.CmsgQuestgiverAcceptQuest, Accept);
        table.OnWorld(WorldOpcode.CmsgQuestlogRemoveQuest, Abandon);
        table.OnWorld(WorldOpcode.CmsgQuestgiverCompleteQuest, Complete);
        table.OnWorld(WorldOpcode.CmsgQuestgiverRequestReward, RequestReward);
        table.OnWorld(WorldOpcode.CmsgQuestgiverChooseReward, ChooseReward);
    }

    private static QuestNpcServices Services(WorldSession session) => session.Services.GetRequiredService<QuestNpcFeature>().Services;

    private static void GossipHello(WorldSession session, Player player, byte[] payload)
        => Services(session).GossipHello(player, ReadGuid(payload));

    private static void Status(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 8);
        var reader = new PacketReader(payload);
        Services(session).QuestgiverStatusQuery(player, new ObjectGuid(reader.ReadUInt64()));
    }

    private static void Details(WorldSession session, Player player, byte[] payload)
    {
        (ObjectGuid guid, uint quest) = ReadQuest(payload);
        Services(session).QuestgiverQueryQuest(player, guid, quest);
    }

    private static void Accept(WorldSession session, Player player, byte[] payload)
    {
        (ObjectGuid guid, uint quest) = ReadQuest(payload);
        Services(session).AcceptQuest(player, guid, quest);
    }

    private static void Abandon(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 1);
        Services(session).AbandonQuest(player, payload[0]);
    }

    private static void Complete(WorldSession session, Player player, byte[] payload)
    {
        (ObjectGuid guid, uint quest) = ReadQuest(payload);
        Services(session).CompleteQuest(player, guid, quest);
    }

    private static void RequestReward(WorldSession session, Player player, byte[] payload)
    {
        (ObjectGuid guid, uint quest) = ReadQuest(payload);
        Services(session).RequestReward(player, guid, quest);
    }

    private static void ChooseReward(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 16);
        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        uint quest = reader.ReadUInt32();
        uint choice = reader.ReadUInt32();
        session.Services.GetRequiredService<QuestNpcFeature>().ChooseReward(session, player, guid, quest, choice);
    }

    private static (ObjectGuid Guid, uint Quest) ReadQuest(byte[] payload)
    {
        RequireLength(payload, 12);
        var reader = new PacketReader(payload);
        return (new ObjectGuid(reader.ReadUInt64()), reader.ReadUInt32());
    }

    private static ObjectGuid ReadGuid(byte[] payload)
    {
        RequireLength(payload, 8);
        var reader = new PacketReader(payload);
        return new ObjectGuid(reader.ReadUInt64());
    }

    private static void RequireLength(byte[] payload, int expected)
    {
        if (payload.Length != expected)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"quest interaction requires exactly {expected} bytes");
        }
    }
}
