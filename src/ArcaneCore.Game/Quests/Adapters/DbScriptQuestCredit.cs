using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>
/// The event-credit source of the exploration/event quests that creature scripts complete (<see cref="QuestNpcServices.ScriptCreditedQuests"/>):
/// a DB script's SCRIPT_COMMAND_QUEST_EXPLORED (mangos-classic ScriptMgr.cpp:1934-1966, e.g. classic-db quest 2843's start script) or a
/// scripted escort (ScriptDev2 RewardPlayerAndGroupAtEventExplored, e.g. Ruul Snowhoof's 6482). Such a quest is no longer withheld for a
/// missing <see cref="QuestAdapter.EventCredit"/>.
/// </summary>
public sealed class DbScriptQuestCredit : IQuestAdapterModule
{
    public QuestAdapter Provides(QuestNpcServices services) => QuestAdapter.None;

    public IEnumerable<uint> EventQuestsCovered(QuestNpcServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.ScriptCreditedQuests?.Invoke() ?? [];
    }
}
