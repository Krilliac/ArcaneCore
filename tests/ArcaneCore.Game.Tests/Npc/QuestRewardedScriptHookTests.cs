using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// A quest rewarded at a creature questgiver reaches the creature's AI (<see cref="CreatureAI.OnQuestRewarded"/>), through the real quest
/// service and its reward path (<see cref="CreatureQuestScripts"/> listens to <c>QuestRewarded</c>).
/// </summary>
public sealed class QuestRewardedScriptHookTests
{
    private const uint Id = 930101;

    private sealed class RewardRecorderAI(Creature creature) : CreatureAI(creature)
    {
        public List<(Player Player, uint Quest)> Rewarded { get; } = [];

        public override void OnQuestRewarded(Player player, uint questId) => Rewarded.Add((player, questId));
    }

    private static void Settle(QuestFlowKit kit, QuestRewardPlan plan)
    {
        Guid operation = Guid.NewGuid();
        Assert.True(kit.Player.BeginQuestSettlement(operation));
        using (kit.Player.BeginQuestSettlementPublication(operation))
        {
            kit.Services.ApplyReward(plan);
        }

        Assert.True(kit.Player.EndQuestSettlement(operation));
    }

    [Fact]
    public void RewardingAQuest_TellsTheGiversAi_WithThePlayerAndTheQuestId()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id, method: 0)], starters: [Id], enders: [Id]);
        var ai = new RewardRecorderAI(kit.Creature);
        kit.Creature.AI = ai;
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null);

        Assert.Empty(ai.Rewarded);
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out QuestRewardPlan? plan));
        Assert.Empty(ai.Rewarded); // preparing a reward is not rewarding it
        Settle(kit, plan);

        (Player player, uint quest) = Assert.Single(ai.Rewarded);
        Assert.Same(kit.Player, player);
        Assert.Equal(Id, quest);
    }

    [Fact]
    public void ACreatureWithoutAnAi_AndADetachedHook_AreHarmless()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id, method: 0)], starters: [Id], enders: [Id]);
        var ai = new RewardRecorderAI(kit.Creature);
        kit.Creature.AI = ai;
        IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null);
        hooks.Dispose();

        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out QuestRewardPlan? plan));
        Settle(kit, plan);
        Assert.Empty(ai.Rewarded); // detached: the quest service no longer reaches the creature
    }

    [Fact]
    public void AGiverWithoutAnAi_IsRewardedWithoutError()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id, method: 0)], starters: [Id], enders: [Id]);
        Assert.Null(kit.Creature.AI);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null);

        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out QuestRewardPlan? plan));
        Settle(kit, plan);
        Assert.Equal(QuestStatusOf(kit), (QuestStatus.Complete, true));
    }

    private static (QuestStatus, bool) QuestStatusOf(QuestFlowKit kit)
    {
        QuestStatusData data = kit.State.Quests.Get(Id)!;
        return (data.Status, data.Rewarded);
    }
}
