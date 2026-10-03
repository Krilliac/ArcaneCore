using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Quests;

public static partial class QuestPackets
{
    /// <summary>
    /// SMSG_QUEST_QUERY_RESPONSE for build 5875. Verified against vmangos/core commit
    /// 4b3d241cffe245a1f68da11380bce96c23db48c0, src/game/Server/Packets/Quest.cpp
    /// QuestQueryResponse::AppendBodyTo, and gtker/wow_messages commit
    /// 70abb9deff0bb63440d8aeb4386b820653e8a176,
    /// wow_message_parser/wowm/world/quest/smsg_quest_query_response.wowm (versions = "1.12").
    /// Rewards have fixed four/six pairs, objectives four groups, and all strings are CStrings.
    /// </summary>
    public static PacketWriter QueryResponse(Quest quest, float moneyRate)
    {
        ArgumentNullException.ThrowIfNull(quest);
        var w = new PacketWriter(256);
        var t = quest.Template;
        bool hiddenRewards = quest.HasFlag(QuestFlags.HiddenRewards);

        w.WriteUInt32(quest.Id);
        w.WriteUInt32(t.Method);
        w.WriteInt32(quest.QuestLevel);
        w.WriteInt32(t.ZoneOrSort);
        w.WriteUInt32(t.Type);
        w.WriteUInt32(t.RepObjectiveFaction);
        w.WriteInt32(t.RepObjectiveValue);
        w.WriteUInt32(0); // vmangos explicitly sends no opposite reputation objective.
        w.WriteUInt32(0);
        w.WriteUInt32(quest.NextQuestInChain);
        w.WriteInt32(hiddenRewards ? 0 : quest.GetRewOrReqMoney(moneyRate));
        w.WriteUInt32(t.RewMoneyMaxLevel); // 1.12 includes the raw value used for client XP calculation.
        w.WriteUInt32(t.RewSpell);
        w.WriteUInt32(t.SrcItemId);
        w.WriteUInt32((uint)quest.Flags);

        for (int i = 0; i < QuestConstants.RewardsCount; i++)
        {
            w.WriteUInt32(hiddenRewards ? 0 : quest.RewItemId[i]);
            w.WriteUInt32(hiddenRewards ? 0 : quest.RewItemCount[i]);
        }

        for (int i = 0; i < QuestConstants.RewardChoicesCount; i++)
        {
            w.WriteUInt32(hiddenRewards ? 0 : quest.RewChoiceItemId[i]);
            w.WriteUInt32(hiddenRewards ? 0 : quest.RewChoiceItemCount[i]);
        }

        w.WriteUInt32(t.PointMapId);
        w.WriteSingle(t.PointX);
        w.WriteSingle(t.PointY);
        w.WriteUInt32(t.PointOpt);
        w.WriteCString(quest.Title);
        w.WriteCString(quest.Objectives);
        w.WriteCString(quest.Details);
        w.WriteCString(quest.EndText);

        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            int target = quest.ReqCreatureOrGOId[i];
            uint entry = target < 0 ? (uint)-(long)target | 0x80000000u : (uint)target;
            w.WriteUInt32(entry);
            w.WriteUInt32(quest.ReqCreatureOrGOCount[i]);
            w.WriteUInt32(quest.ReqItemId[i]);
            w.WriteUInt32(quest.ReqItemCount[i]);
        }

        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            w.WriteCString(quest.ObjectiveText[i]);
        }

        return w;
    }
}
