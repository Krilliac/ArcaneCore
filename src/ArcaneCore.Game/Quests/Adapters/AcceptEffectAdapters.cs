using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>
/// Quests with a source spell (quest_template.SrcSpell): the spell is cast on accept through the
/// <see cref="IQuestSpellCaster"/> dependency, so the adapter exists only when the host has a spell system.
/// </summary>
public sealed class SrcSpellAdapter : IQuestAdapterModule
{
    public QuestAdapter Provides(QuestNpcServices services) => services.Deps.SpellCaster is null ? QuestAdapter.None : QuestAdapter.SrcSpell;
}

/// <summary>PvP quests (type 41): accepting one flags the player (QuestNpcServices.Interaction.cs, vmangos Player.cpp:12866).</summary>
public sealed class PvpQuestAdapter : IQuestAdapterModule
{
    public QuestAdapter Provides(QuestNpcServices services) => QuestAdapter.PvpType;
}
