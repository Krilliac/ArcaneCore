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
    /// <summary>
    /// Only transient, executable effects may use post-commit publication. Permanent rewards
    /// need an atomic grant or a durable recovery intent before their quests can be consumed.
    /// Handler registration alone does not provide that settlement contract.
    /// </summary>
    public bool CanCastRewardSpell(uint spellId) => CanPublish(spellId, []);

    private bool CanPublish(uint spellId, HashSet<uint> visiting)
    {
        if (spells.Store.Get(spellId) is not { } spell || !visiting.Add(spellId))
        {
            return false;
        }

        try
        {
            bool hasEffect = false;
            foreach (SpellEffectInfo effect in spell.Effects)
            {
                if (effect.IsEmpty)
                {
                    continue;
                }

                hasEffect = true;
                // These handlers also need destination/spawn-owner preflight. Handler
                // registration cannot prove that a post-commit reward will be delivered.
                if (!spells.HasEffectHandler(effect.Effect) || RequiresDurableGrant(effect.Effect)
                    || effect.Effect is (SpellEffectName.TeleportUnits or SpellEffectName.Summon)
                    || effect.TargetA is not (SpellImplicitTarget.None or SpellImplicitTarget.UnitCaster
                        or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitFriend
                        or SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitParty))
                {
                    return false;
                }

                if (effect.Effect == SpellEffectName.ApplyAura
                    && (!spells.HasAuraHandler(effect.AuraType) || spell.IsPassive || spell.GetDuration() <= 0))
                {
                    return false;
                }

                if ((effect.Effect is (SpellEffectName.TriggerSpell or SpellEffectName.TriggerMissile)
                        || effect.AuraType == AuraType.PeriodicTriggerSpell)
                    && !CanPublish(effect.TriggerSpell, visiting))
                {
                    return false;
                }
            }

            return hasEffect;
        }
        finally
        {
            visiting.Remove(spellId);
        }
    }

    private static bool RequiresDurableGrant(SpellEffectName effect) => effect is
        SpellEffectName.LearnSpell or SpellEffectName.LearnPetSpell or SpellEffectName.CreateItem
        or SpellEffectName.QuestComplete or SpellEffectName.Skill or SpellEffectName.SkillStep
        or SpellEffectName.TradeSkill or SpellEffectName.Proficiency or SpellEffectName.Language
        or SpellEffectName.DualWield or SpellEffectName.Reputation or SpellEffectName.Bind
        or SpellEffectName.EnchantItem or SpellEffectName.EnchantHeldItem
        or SpellEffectName.DurabilityDamage or SpellEffectName.DurabilityDamagePct
        or SpellEffectName.Disenchant or SpellEffectName.Pickpocket
        // Opaque scripted effects cannot prove that all their grants are transient.
        or SpellEffectName.Dummy or SpellEffectName.ScriptEffect or SpellEffectName.SendEvent;

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
