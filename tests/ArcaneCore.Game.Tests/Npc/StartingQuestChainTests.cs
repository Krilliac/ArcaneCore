using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Classic Northshire chain contract: quest 7 is gated by rewarded quest 783, not merely by
/// being level 1 and human. Numeric fields come from the pinned classic-db world row.
/// </summary>
public sealed class StartingQuestChainTests
{
    [Fact]
    public void QuestSevenRequiresRewardedQuest783_ThenCreatesAnIncompleteObjectiveRow()
    {
        QuestTemplate threatWithin = new()
        {
            Entry = 783,
            Method = 2,
            MinLevel = 1,
            MaxLevel = 255,
            QuestLevel = 1,
            RequiredRaces = 77,
            // The pinned row carries 40 XP; this game-only kit has no experience owner, so the
            // zero-XP fixture below keeps the chain test focused on acceptance and journaling.
            RewXP = 0,
            // The pinned row also carries faction 72 / +75 reputation. This game-only kit has
            // no reputation settlement owner; the live chain probe must separately verify
            // the actual reputation reward rather than infer it from this narrow fixture.
        };
        QuestTemplate koboldCamp = new()
        {
            Entry = 7,
            Method = 2,
            MinLevel = 1,
            MaxLevel = 255,
            QuestLevel = 2,
            RequiredRaces = 77,
            PrevQuestId = 783,
            ReqCreatureOrGOId1 = 6,
            ReqCreatureOrGOCount1 = 10,
        };

        using var kit = new QuestFlowKit([threatWithin, koboldCamp], starters: [783, 7], enders: [783, 7], rewardable: [783]);

        Assert.False(kit.Accept(7));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(7));
        Assert.Null(kit.State.Quests.Get(7));

        Assert.True(kit.Accept(783));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(783));

        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, 783, 0, out QuestRewardPlan? plan));
        Assert.Equal(0u, plan!.Experience);
        kit.Services.ApplyReward(plan);
        Assert.True(kit.State.Quests.Get(783)!.Rewarded);

        Assert.True(kit.Accept(7));
        QuestStatusData accepted = Assert.IsType<QuestStatusData>(kit.State.Quests.Get(7));
        Assert.Equal(QuestStatus.Incomplete, accepted.Status);
        Assert.Equal(6, koboldCamp.ReqCreatureOrGOId1);
        Assert.Equal(10u, koboldCamp.ReqCreatureOrGOCount1);
    }
}
