using ArcaneCore.Game;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_MEETINGSTONE_SETQUEUE (<see cref="MeetingStonePackets.SetQueue"/>): u32 area, u8 status.</summary>
public sealed record MeetingStoneQueueView(uint AreaId, MeetingStoneStatus Status);

/// <summary>
/// Meeting stone client actions and reply decoders for scenarios (game object types lane). The layouts follow the server's own handler parsing
/// (<c>GameObjects.MeetingStoneHandlers</c>) and writers (<see cref="MeetingStonePackets"/>); they live apart from <see cref="ScenarioBot"/> and
/// <see cref="ScenarioDecoders"/> so other lanes' harness extensions do not collide.
/// </summary>
public static class ScenarioMeetingStones
{
    /// <summary>CMSG_MEETINGSTONE_JOIN: u64 meeting stone guid.</summary>
    public static Task<bool> JoinMeetingStoneAsync(this ScenarioBot bot, ObjectGuid stone)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.SendAsync(WorldOpcode.CmsgMeetingstoneJoin, ScenarioPackets.Guid(stone.Value));
    }

    /// <summary>CMSG_MEETINGSTONE_LEAVE: empty.</summary>
    public static Task<bool> LeaveMeetingStoneAsync(this ScenarioBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.SendAsync(WorldOpcode.CmsgMeetingstoneLeave, []);
    }

    /// <summary>CMSG_MEETINGSTONE_INFO: empty.</summary>
    public static Task<bool> MeetingStoneInfoAsync(this ScenarioBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);
    }

    /// <summary>Decode SMSG_MEETINGSTONE_SETQUEUE (exactly five bytes).</summary>
    public static MeetingStoneQueueView SetQueue(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != 5)
        {
            throw new FormatException($"SMSG_MEETINGSTONE_SETQUEUE: expected 5 bytes, got {payload.Length}");
        }

        return new MeetingStoneQueueView(BitConverter.ToUInt32(payload, 0), (MeetingStoneStatus)payload[4]);
    }

    /// <summary>Decode SMSG_MEETINGSTONE_MEMBER_ADDED (u64 player guid).</summary>
    public static ulong MemberAdded(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != 8)
        {
            throw new FormatException($"SMSG_MEETINGSTONE_MEMBER_ADDED: expected 8 bytes, got {payload.Length}");
        }

        return BitConverter.ToUInt64(payload, 0);
    }

    /// <summary>Decode SMSG_MEETINGSTONE_JOINFAILED (u8 reason).</summary>
    public static MeetingStoneFailure JoinFailed(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != 1)
        {
            throw new FormatException($"SMSG_MEETINGSTONE_JOINFAILED: expected 1 byte, got {payload.Length}");
        }

        return (MeetingStoneFailure)payload[0];
    }
}
