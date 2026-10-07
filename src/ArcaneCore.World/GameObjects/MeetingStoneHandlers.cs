using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The meeting stone opcodes (vmangos LFG/LFGHandler.cpp; layouts from Server/Packets/Misc.cpp and gtker wow_messages meetingstone/*.wowm):
/// CMSG_MEETINGSTONE_JOIN (u64 meeting stone guid), CMSG_MEETINGSTONE_LEAVE and CMSG_MEETINGSTONE_INFO (both empty). The queue is
/// <see cref="MeetingStoneFeature.Queue"/>.
/// </summary>
public sealed class MeetingStoneHandlers : IOpcodeHandlerGroup
{
    /// <summary>meetingstone.areaID (data2, GameObjectDefines.h:437-442): the dungeon area the stone queues for.</summary>
    public const int MeetingStoneAreaData = 2;

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgMeetingstoneJoin, Join);
        table.OnWorld(WorldOpcode.CmsgMeetingstoneLeave, Leave);
        table.OnWorld(WorldOpcode.CmsgMeetingstoneInfo, Info);
    }

    /// <summary>
    /// HandleMeetingStoneJoinOpcode (LFGHandler.cpp:31-71): the guid must name a meeting stone the player can interact with; a party member who
    /// does not lead gets SMSG_MEETINGSTONE_JOINFAILED PARTYLEADER, a raid RAID_GROUP, a full party FULL_GROUP; then the player (or his party)
    /// queues for the stone's area. A short body is ignored.
    /// </summary>
    private static void Join(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8 || player.Map is not { } map || session.Services.GetService<MeetingStoneFeature>() is not { Queue: { } queue } feature)
        {
            return;
        }

        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        if (session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.FindInteractable(player, guid, GameObjectType.MeetingStone) is not { } stone)
        {
            return;
        }

        if (feature.Groups?.GetGroup(player.Guid) is { } group)
        {
            MeetingStoneFailure? failure = !group.IsLeader(player.Guid) ? MeetingStoneFailure.PartyLeader
                : group.IsRaid ? MeetingStoneFailure.RaidGroup
                : group.IsFull ? MeetingStoneFailure.FullGroup
                : null;
            if (failure is { } reason)
            {
                session.Send(WorldOpcode.SmsgMeetingstoneJoinfailed, MeetingStonePackets.JoinFailed(reason));
                return;
            }
        }

        queue.Join(player, stone.Template.GetData(MeetingStoneAreaData));
    }

    /// <summary>HandleMeetingStoneLeaveOpcode (LFGHandler.cpp:73-98).</summary>
    private static void Leave(WorldSession session, Player player, byte[] payload)
        => session.Services.GetService<MeetingStoneFeature>()?.Queue?.Leave(player);

    /// <summary>
    /// HandleMeetingStoneInfoOpcode (LFGHandler.cpp:100-128): the queue status. Without a queue the answer is the idle one (area 0, NONE). The body
    /// must be empty (ArcaneCore policy: surplus bytes disconnect, as for the other status polls).
    /// </summary>
    private static void Info(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "meeting stone status request requires an empty body");
        }

        if (session.Services.GetService<MeetingStoneFeature>()?.Queue is { } queue)
        {
            queue.Info(player);
            return;
        }

        session.Send(WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None));
    }
}
