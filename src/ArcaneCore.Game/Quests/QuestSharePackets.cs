using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// vmangos QuestShareMessages (QuestDef.h:60-71) and gtker/wow_messages quest/msg_quest_push_result.wowm, enum
/// QuestPartyMessage for versions "1 2" (the 3.3.5 enum has different numbers and no TOO_FAR; the 1.12 one is used).
/// </summary>
public enum QuestShareMessage : byte
{
    SharingQuest = 0,
    CantTakeQuest = 1,
    AcceptQuest = 2,
    DeclineQuest = 3,
    TooFar = 4,
    Busy = 5,
    LogFull = 6,
    HaveQuest = 7,
    FinishQuest = 8,
}

/// <summary>Quest sharing packet bodies (vmangos Server/Packets/Quest.cpp:87-91 and :186-191).</summary>
public static class QuestSharePackets
{
    /// <summary>MSG_QUEST_PUSH_RESULT (0x0276): u64 guid of the player the message is about, u8 message.</summary>
    public static PacketWriter PushResult(ObjectGuid subject, QuestShareMessage message)
    {
        var w = new PacketWriter(9);
        w.WriteUInt64(subject.Value);
        w.WriteByte((byte)message);
        return w;
    }

    /// <summary>SMSG_QUEST_CONFIRM_ACCEPT (0x019C): u32 quest, CString title, u64 guid of the player who accepted.</summary>
    public static PacketWriter ConfirmAccept(uint questId, string title, ObjectGuid sender)
    {
        var w = new PacketWriter(13 + title.Length);
        w.WriteUInt32(questId);
        w.WriteCString(title);
        w.WriteUInt64(sender.Value);
        return w;
    }
}
