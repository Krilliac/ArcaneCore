using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// The world's <see cref="IScriptQuestEvents"/>: the quest log of DB scripts and player-linked escorts through the quest service of
/// <see cref="QuestNpcFeature"/>, resolved at every call because the creature feature attaches first (as <see cref="EventAiQuestEvents"/>).
/// Without the quest feature every call does nothing.
/// </summary>
public sealed class ScriptQuestEvents(IServiceProvider services) : IScriptQuestEvents
{
    private QuestNpcServices? Quests => services.GetService<QuestNpcFeature>()?.Services;

    public void AreaExploredOrEventHappens(Player player, uint questId) => Quests?.AreaExploredOrEventHappens(player, questId);

    public void FailQuest(Player player, uint questId) => Quests?.FailQuest(player, questId);

    public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => Quests?.KilledMonsterCredit(player, creatureEntry, source);

    public void GroupEventFailHappens(Player player, uint questId) => Quests?.GroupEventFailHappens(player, questId);

    public IReadOnlyList<Player> GroupMembersOf(Player player) => Quests?.Deps.Party?.MembersOf(player) ?? [];
}
