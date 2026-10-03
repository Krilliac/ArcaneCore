using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>
/// PARTY_ACCEPT quests (QUEST_FLAGS_PARTY_ACCEPT, 170 in classic-db): after one member accepts, every eligible party member on
/// the map is offered a confirmation box (QuestNpcServices.Share.cs, QuestHandler.cpp:166-191). It needs the host's group lookup
/// (<see cref="IQuestParty"/>); a host without one keeps those quests withheld instead of silently skipping the box.
/// </summary>
public sealed class PartyAcceptAdapter : IQuestAdapterModule
{
    public QuestAdapter Provides(QuestNpcServices services) => services.Deps.Party is null ? QuestAdapter.None : QuestAdapter.PartyAccept;
}
