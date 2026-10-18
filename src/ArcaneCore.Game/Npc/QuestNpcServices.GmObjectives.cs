using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Outcome of <see cref="QuestNpcServices.GmSetObjectiveCount"/>.</summary>
public enum GmObjectiveResult
{
    Set,
    NotInLog,
    NoSuchObjective,
    ItemObjective,
    NotLoaded,
}

public sealed partial class QuestNpcServices
{
    /// <summary>
    /// <c>.quest objective</c> (docs/reference/gm-commands.md): set the kill / use-object progress of objective <paramref name="index"/>
    /// (0-3) of a logged quest to <paramref name="count"/> (clamped to the required count), update the log slot, tell the client
    /// (SMSG_QUESTUPDATE_ADD_KILL) and re-check completion both ways, then persist. Item objectives follow the inventory and are refused.
    /// </summary>
    public GmObjectiveResult GmSetObjectiveCount(Player player, Quest quest, int index, uint count, out uint applied)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quest);
        applied = 0;
        if (Ready(player) is not { } state)
        {
            return GmObjectiveResult.NotLoaded;
        }

        if (index is < 0 or >= QuestConstants.ObjectivesCount)
        {
            return GmObjectiveResult.NoSuchObjective;
        }

        int slot = state.Quests.FindSlot(quest.Id);
        if (slot < 0 || state.Quests.Get(quest.Id) is not { } data || data.Status is not (QuestStatus.Incomplete or QuestStatus.Complete))
        {
            return GmObjectiveResult.NotInLog;
        }

        if (quest.ReqCreatureOrGOId[index] == 0)
        {
            return quest.ReqItemId[index] != 0 ? GmObjectiveResult.ItemObjective : GmObjectiveResult.NoSuchObjective;
        }

        applied = Math.Min(count, quest.ReqCreatureOrGOCount[index]);
        data.CreatureOrGOCount[index] = applied;
        state.Quests.SetSlotCounter(slot, index, applied);
        state.Quests.MarkChanged(quest.Id);
        int target = quest.ReqCreatureOrGOId[index];
        uint entry = (uint)Math.Abs((long)target);
        var body = new PacketWriter(24);
        body.WriteUInt32(quest.Id);
        body.WriteUInt32(target > 0 ? entry : entry | 0x80000000u);
        body.WriteUInt32(applied);
        body.WriteUInt32(quest.ReqCreatureOrGOCount[index]);
        body.WriteUInt64(0);
        Send(player, WorldOpcode.SmsgQuestupdateAddKill, body);
        RefreshCompletion(state, quest, data, slot);
        Flush(state);
        return GmObjectiveResult.Set;
    }
}
