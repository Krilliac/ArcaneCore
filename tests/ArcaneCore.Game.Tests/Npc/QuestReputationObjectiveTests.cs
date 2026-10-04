using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// vmangos Player::ReputationChanged (Player.cpp:14239-14264): a reputation objective quest completes when the standing
/// reaches its value and reverts when the standing falls below it.
/// </summary>
public sealed class QuestReputationObjectiveTests
{
    private const uint Id = 940001;
    private const uint Faction = 72;
    private const int Value = 3000;

    private static QuestTemplate RepQuest(uint id = Id, uint faction = Faction) => new()
    {
        Entry = id, Method = 2, QuestLevel = 1, Title = "Friends", RequestItemsText = "Befriend them.",
        RepObjectiveFaction = faction, RepObjectiveValue = Value,
    };

    private static QuestFlowKit Kit(FakeReputation reputation, QuestTemplate? quest = null)
    {
        quest ??= RepQuest();
        return new QuestFlowKit([quest], starters: [quest.Entry], enders: [quest.Entry], reputation: reputation);
    }

    [Fact]
    public void StandingRisingToTheObjective_CompletesTheQuest_ButOnlyOnceNotified()
    {
        var reputation = new FakeReputation();
        using QuestFlowKit kit = Kit(reputation);
        Assert.True(kit.Accept(Id));
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Id));

        reputation.Standing[Faction] = Value;
        // Nothing observes a bare standing change: the reputation owner must raise the event.
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Id));
        kit.Services.ReputationChanged(kit.Player, Faction);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Id));
        Assert.Contains(kit.Sink.Rows, row => row.Quest == Id && row.Status == (byte)QuestStatus.Complete);
    }

    [Fact]
    public void StandingFallingBelowTheObjective_RevertsCompleteToIncomplete()
    {
        var reputation = new FakeReputation { Standing = { [Faction] = Value } };
        using QuestFlowKit kit = Kit(reputation);
        Assert.True(kit.Accept(Id));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Id));

        reputation.Standing[Faction] = Value - 1;
        kit.Services.ReputationChanged(kit.Player, Faction);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Id));
    }

    [Fact]
    public void AnotherFaction_DoesNothing()
    {
        var reputation = new FakeReputation();
        using QuestFlowKit kit = Kit(reputation);
        Assert.True(kit.Accept(Id));
        reputation.Standing[Faction] = Value;
        kit.Services.ReputationChanged(kit.Player, Faction + 1);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Id));
    }

    [Fact]
    public void QuestNotInTheLog_IsIgnored()
    {
        var reputation = new FakeReputation { Standing = { [Faction] = Value } };
        using QuestFlowKit kit = Kit(reputation);
        kit.Services.ReputationChanged(kit.Player, Faction);
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(Id));
        Assert.Empty(kit.Sink.Rows);
    }

    [Fact]
    public void SubscriptionForwardsTheSourcesEvent_AndStopsAfterDispose()
    {
        var reputation = new FakeReputation();
        using QuestFlowKit kit = Kit(reputation);
        Assert.True(kit.Accept(Id));
        var source = new FakeSource();
        var subscription = new ReputationObjectiveSubscription(source, () => kit.Services);

        reputation.Standing[Faction] = Value;
        source.Raise(kit.Player, Faction);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Id));

        subscription.Dispose();
        Assert.Equal(0, source.Subscribers);
        reputation.Standing[Faction] = 0;
        source.Raise(kit.Player, Faction);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Id));
    }

    private sealed class FakeReputation : IPlayerReputation
    {
        public Dictionary<uint, int> Standing { get; } = [];

        public int GetReputation(Player player, uint factionId) => Standing.GetValueOrDefault(factionId);

        public byte GetReputationRank(Player player, uint factionId) => 4;

        public float GetPriceDiscount(Player player, NpcInfo npc) => 1.0f;
    }

    private sealed class FakeSource : IReputationChangeSource
    {
        private Action<Player, uint>? _changed;

        public event Action<Player, uint>? ReputationChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public void Raise(Player player, uint faction) => _changed?.Invoke(player, faction);
    }
}
