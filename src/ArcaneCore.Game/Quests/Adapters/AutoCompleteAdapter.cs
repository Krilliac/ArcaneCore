using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>
/// Autocomplete quests (quest_template.Method 0, vmangos QUEST_METHOD_AUTOCOMPLETE): turned in from the ender's list
/// without objectives and, when they were never accepted, without a journal entry
/// (QuestNpcServices.TurnIn.cs, the reward store's InsertIfMissing). 696 of the 4245 classic-db quests.
/// </summary>
public sealed class AutoCompleteAdapter : IQuestAdapterModule
{
    public QuestAdapter Provides(QuestNpcServices services) => QuestAdapter.Autocomplete;
}
