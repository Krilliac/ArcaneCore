using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Progression;

/// <summary>
/// Post-settlement quest reward effects in the daemon: the reward spell through the world spell
/// system and every optional <see cref="IQuestReputationRewards"/> owner (none on this branch).
/// </summary>
public sealed class QuestRewardEffects(SpellSystem spells, Func<IEnumerable<IQuestReputationRewards>> reputation, ILogger logger)
    : IQuestRewardEffects
{
    public bool CanCastRewardSpell(uint spellId) => spells.Store.Get(spellId) is not null;

    public void QuestRewarded(Player player, Quest quest, ObjectGuid questGiver)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quest);
        foreach (IQuestReputationRewards owner in reputation())
        {
            owner.RewardQuestReputation(player, quest);
        }

        uint spellId = QuestNpcServices.RewardSpell(quest);
        if (spellId == 0)
        {
            return;
        }

        Unit? giver = questGiver.IsEmpty ? null : player.Map?.FindObject(questGiver) as Unit;
        SpellCastResult result = QuestRewardSpells.Cast(spells, player, giver, spellId);
        if (result != SpellCastResult.CastOk)
        {
            logger.LogWarning("quest {Quest} reward spell {Spell} failed: {Result}", quest.Id, spellId, result);
        }
    }
}
