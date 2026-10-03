using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// Build-5875 quest menu bodies, reimplemented from vmangos src/game/Server/Packets/Quest.cpp
/// QuestGiverQuestList, QuestGiverQuestDetails, QuestGiverRequestItems and QuestGiverOfferReward.
/// https://github.com/vmangos/core/blob/development/src/game/Server/Packets/Quest.cpp
/// </summary>
public static partial class QuestPackets
{
    public static PacketWriter List(ObjectGuid npc, IReadOnlyList<(Quest Quest, DialogStatus Icon)> quests)
    {
        var w = new PacketWriter(128);
        w.WriteUInt64(npc.Value);
        w.WriteCString(string.Empty);
        w.WriteUInt32(0); // No quest greeting/broadcast-text content is modelled.
        w.WriteUInt32(0);
        w.WriteByte((byte)quests.Count);
        foreach ((Quest quest, DialogStatus icon) in quests)
        {
            w.WriteUInt32(quest.Id);
            w.WriteUInt32((uint)icon);
            w.WriteInt32(quest.QuestLevel);
            w.WriteCString(quest.Title);
        }

        return w;
    }

    public static PacketWriter Details(ObjectGuid npc, Quest quest, float moneyRate, Func<uint, uint> display)
    {
        var w = Header(npc, quest, quest.Details);
        w.WriteCString(quest.Objectives);
        w.WriteUInt32(1); // ActivateAccept/autoFinish in PlayerMenu::SendQuestGiverQuestDetails.
        if (quest.HasFlag(QuestFlags.HiddenRewards))
        {
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
        }
        else
        {
            Rewards(w, quest, display);
            w.WriteInt32(quest.GetRewOrReqMoney(moneyRate));
        }

        w.WriteUInt32(quest.Template.RewSpell);
        w.WriteUInt32(QuestConstants.EmoteCount);
        for (int i = 0; i < QuestConstants.EmoteCount; i++)
        {
            w.WriteUInt32(quest.DetailsEmote[i]);
            w.WriteUInt32(quest.DetailsEmoteDelay[i]);
        }

        return w;
    }

    public static PacketWriter OfferReward(ObjectGuid npc, Quest quest, float moneyRate, Func<uint, uint> display)
    {
        var w = Header(npc, quest, quest.OfferRewardText);
        w.WriteUInt32(1);
        int count = quest.OfferRewardEmote.TakeWhile(id => id != 0).Count();
        w.WriteUInt32((uint)count);
        for (int i = 0; i < count; i++)
        {
            w.WriteUInt32(quest.OfferRewardEmoteDelay[i]);
            w.WriteUInt32(quest.OfferRewardEmote[i]);
        }

        Rewards(w, quest, display);
        w.WriteInt32(quest.GetRewOrReqMoney(moneyRate));
        w.WriteUInt32((uint)quest.Flags);
        w.WriteUInt32(quest.Template.RewSpell);
        return w;
    }

    public static PacketWriter RequestItems(ObjectGuid npc, Quest quest, bool complete, Func<uint, uint> display, bool closeOnCancel = true)
    {
        var w = Header(npc, quest, quest.RequestItemsText);
        w.WriteUInt32(0);
        w.WriteUInt32(complete ? quest.Template.CompleteEmote : quest.Template.IncompleteEmote);
        w.WriteUInt32(closeOnCancel ? 1u : 0u);
        w.WriteUInt32(quest.Template.RewOrReqMoney < 0 ? (uint)-(long)quest.Template.RewOrReqMoney : 0);
        Items(w, quest.ReqItemId, quest.ReqItemCount, display);
        w.WriteUInt32(2);
        w.WriteUInt32(complete ? 3u : 0u);
        w.WriteUInt32(4);
        w.WriteUInt32(8);
        return w;
    }

    /// <summary>
    /// SMSG_QUESTGIVER_QUEST_COMPLETE (5875): quest, 3, XP (0 at the maximum level), configured
    /// money (plus RewMoneyMaxLevel at the maximum level), then fixed item/count pairs. Choice
    /// rewards are excluded.
    /// vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0 Quest.cpp 123–135,
    /// Player.cpp 14336–14363; gtker/wow_messages 70abb9deff0bb63440d8aeb4386b820653e8a176
    /// smsg_questgiver_quest_complete.wowm. Money retains its configured 32-bit pattern,
    /// including a negative required-money amount; it is not the clamped balance delta.
    /// </summary>
    public static PacketWriter Complete(Quest quest, uint money) => Complete(quest, 0, money);

    /// <inheritdoc cref="Complete(Quest, uint)"/>
    public static PacketWriter Complete(Quest quest, uint experience, uint money)
    {
        var w = new PacketWriter(20 + (quest.RewItemsCount * 8));
        w.WriteUInt32(quest.Id);
        w.WriteUInt32(3);
        w.WriteUInt32(experience);
        w.WriteUInt32(money);
        w.WriteUInt32((uint)quest.RewItemsCount);
        for (int i = 0; i < QuestConstants.RewardsCount; i++)
        {
            if (quest.RewItemId[i] != 0)
            {
                w.WriteUInt32(quest.RewItemId[i]);
                w.WriteUInt32(quest.RewItemCount[i]);
            }
        }

        return w;
    }

    private static PacketWriter Header(ObjectGuid npc, Quest quest, string text)
    {
        var w = new PacketWriter(256);
        w.WriteUInt64(npc.Value);
        w.WriteUInt32(quest.Id);
        w.WriteCString(quest.Title);
        w.WriteCString(text);
        return w;
    }

    private static void Rewards(PacketWriter w, Quest quest, Func<uint, uint> display)
    {
        Items(w, quest.RewChoiceItemId, quest.RewChoiceItemCount, display);
        Items(w, quest.RewItemId, quest.RewItemCount, display);
    }

    private static void Items(PacketWriter w, IReadOnlyList<uint> ids, IReadOnlyList<uint> counts, Func<uint, uint> display)
    {
        w.WriteUInt32((uint)ids.Count(id => id != 0));
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] != 0)
            {
                w.WriteUInt32(ids[i]);
                w.WriteUInt32(counts[i]);
                w.WriteUInt32(display(ids[i]));
            }
        }
    }
}
