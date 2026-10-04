using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// Casts a quest's source spell on the player who accepted it (vmangos HandleQuestgiverAcceptQuestOpcode,
/// QuestHandler.cpp:202-203: <c>CastSpell(player, SrcSpell, triggered)</c>). The quest service has no spell system of
/// its own; without a caster the quests that carry a source spell stay withheld.
/// </summary>
public interface IQuestSpellCaster
{
    /// <summary>Cast <paramref name="spellId"/> triggered, with the player as caster and target. False when it did not cast.</summary>
    bool CastOnSelf(Player player, uint spellId);

    /// <summary>
    /// Quests some spell credits through SPELL_EFFECT_QUEST_COMPLETE (EffectMiscValue is the quest id). Exploration and event
    /// quests have no other credit source in a world without scripts, so these are the ones that need not be withheld.
    /// </summary>
    IReadOnlyCollection<uint> QuestsCompletedBySpells => [];
}
