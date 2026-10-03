using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Quests;

public static partial class QuestPackets
{
    /// <summary>
    /// SMSG_QUESTGIVER_QUEST_INVALID (gtker/wow_messages smsg_questgiver_quest_invalid.wowm,
    /// 1.12): u32 reason. vmangos Player::SendCanTakeQuestResponse (Player.cpp:14400-14405).
    /// </summary>
    public static PacketWriter QuestInvalid(QuestInvalidReason reason)
    {
        var w = new PacketWriter(4);
        w.WriteUInt32((uint)reason);
        return w;
    }

    /// <summary>
    /// SMSG_QUESTGIVER_QUEST_FAILED (smsg_questgiver_questfailed.wowm, 1.12): u32 quest, u32 reason.
    /// vmangos Player::SendQuestFailedAtTaker.
    /// </summary>
    public static PacketWriter QuestFailed(uint questId, QuestInvalidReason reason)
    {
        var w = new PacketWriter(8);
        w.WriteUInt32(questId);
        w.WriteUInt32((uint)reason);
        return w;
    }
}
