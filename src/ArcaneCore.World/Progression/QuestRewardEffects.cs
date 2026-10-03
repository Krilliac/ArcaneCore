using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Progression;

/// <summary>
/// Quest reward spells in the daemon, through the world spell system. A reward spell is accepted in
/// exactly one of two shapes:
/// <list type="bullet">
/// <item><b>Pure grant</b>: every effect is LearnSpell or CreateItem. The spell is never cast; its
/// grants are resolved and frozen at preparation, persisted atomically with the quest journal and then
/// announced (docs/integration/quest-settlement-async.md).</item>
/// <item><b>Transient</b>: nothing durable. Cast once after the commit, for the original live player only;
/// teleport and summon effects pass only through a player-aware preflight that runs again at publication.</item>
/// </list>
/// Mixed spells are refused (the transient half could not be cast without repeating the grant), as are
/// the other permanent effects, the teleport/summon variants preflight cannot model, and any teleport or
/// summon whose built-in handler was replaced.
/// </summary>
public sealed class QuestRewardEffects(SpellSystem spells, ILogger logger) : IQuestRewardEffects
{
    /// <summary>The static allowlist gate; player-aware checks happen in <see cref="TryPrepareRewardSpell"/>.</summary>
    public bool CanCastRewardSpell(uint spellId) => spells.Store.Get(spellId) is { } spell && Classify(spell) != Shape.Refused;

    public bool TryPrepareRewardSpell(Player player, ObjectGuid questGiver, uint spellId, out QuestRewardSpellGrant grant)
    {
        ArgumentNullException.ThrowIfNull(player);
        grant = QuestRewardSpellGrant.None;
        if (spells.Store.Get(spellId) is not { } spell)
        {
            return false;
        }

        Shape shape = Classify(spell);
        if (shape == Shape.Refused)
        {
            return false;
        }

        Unit caster = QuestRewardSpells.ResolveCaster(player, GiverOf(player, questGiver), spell);
        if (shape == Shape.Transient)
        {
            if (!PreflightTransient(player, caster, spell))
            {
                return false;
            }

            grant = new QuestRewardSpellGrant(true, [], []);
            return true;
        }

        var learned = new List<uint>();
        var created = new List<QuestRewardCreatedItem>();
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.IsEmpty)
            {
                continue;
            }

            if (effect.Effect == SpellEffectName.LearnSpell)
            {
                if (spells.Spellbook is not { } book)
                {
                    return false;
                }

                if (!book.HasSpell(player, effect.TriggerSpell) && !learned.Contains(effect.TriggerSpell))
                {
                    learned.Add(effect.TriggerSpell);
                }
            }
            else
            {
                // vmangos Spell::DoCreateItem: an unknown template is refused; the count is the effect
                // value clamped to [1, Stackable]. It is rolled here, once, so the commit and the
                // published packets agree.
                if (player.Inventory.Templates.Find(effect.ItemType) is not { } template)
                {
                    return false;
                }

                int value = spell.CalculateEffectValue(i, caster.Level, spells.Random);
                created.Add(new QuestRewardCreatedItem(effect.ItemType, (uint)Math.Clamp(value, 1, (int)Math.Max(template.Stackable, 1u))));
            }
        }

        grant = new QuestRewardSpellGrant(false, learned, created);
        return true;
    }

    public void AnnounceLearnedSpells(Player player, QuestRewardSpellGrant grant)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(grant);
        foreach (uint spellId in grant.LearnedSpells)
        {
            spells.AnnounceLearnedSpell(player, spellId);
        }
    }

    public void PublishRewardSpell(Player player, Quest quest, ObjectGuid questGiver, QuestRewardSpellGrant grant)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(grant);
        foreach (uint spellId in grant.LearnedSpells)
        {
            spells.CastLearnedPassive(player, spellId);
        }

        if (!grant.Transient)
        {
            return;
        }

        uint rewardSpell = QuestNpcServices.RewardSpell(quest);
        if (spells.Store.Get(rewardSpell) is not { } spell)
        {
            logger.LogWarning("quest {Quest} reward spell {Spell} vanished before publication", quest.Id, rewardSpell);
            return;
        }

        Unit? giver = GiverOf(player, questGiver);
        Unit caster = QuestRewardSpells.ResolveCaster(player, giver, spell);
        // The commit already happened; whether a teleport destination or summon owner is still valid
        // is asked again for the original player, and a refusal is a lost (never durable) effect.
        if (!PreflightTransient(player, caster, spell))
        {
            logger.LogWarning("quest {Quest} transient reward spell {Spell} no longer passes its preflight; the effect is lost", quest.Id, rewardSpell);
            return;
        }

        SpellCastResult result = QuestRewardSpells.Cast(spells, player, giver, rewardSpell);
        if (result != SpellCastResult.CastOk)
        {
            logger.LogWarning("quest {Quest} reward spell {Spell} failed: {Result}", quest.Id, rewardSpell, result);
        }
    }

    private static Unit? GiverOf(Player player, ObjectGuid questGiver)
        => questGiver.IsEmpty ? null : player.Map?.FindObject(questGiver) as Unit;

    private enum Shape { Refused, PureGrant, Transient }

    private Shape Classify(SpellInfo spell)
    {
        bool anyEffect = false;
        bool anyGrant = false;
        bool allGrants = true;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty)
            {
                continue;
            }

            anyEffect = true;
            if (!AllowedTarget(effect))
            {
                return Shape.Refused;
            }

            if (effect.Effect is SpellEffectName.LearnSpell or SpellEffectName.CreateItem)
            {
                anyGrant = true;
                // A grant that can never be resolved keeps the quest unsupported instead of failing at turn-in.
                if (effect.Effect == SpellEffectName.LearnSpell
                    ? effect.TriggerSpell == 0 || spells.Store.Get(effect.TriggerSpell) is null
                    : effect.ItemType == 0)
                {
                    return Shape.Refused;
                }
            }
            else
            {
                allGrants = false;
            }
        }

        if (!anyEffect)
        {
            return Shape.Refused;
        }

        if (anyGrant)
        {
            return allGrants ? Shape.PureGrant : Shape.Refused;
        }

        return IsTransient(spell, [], nested: false) ? Shape.Transient : Shape.Refused;
    }

    private static bool AllowedTarget(SpellEffectInfo effect) => effect.TargetA is SpellImplicitTarget.None or SpellImplicitTarget.UnitCaster
        or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitFriend or SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitParty;

    /// <summary>
    /// Only effects that leave nothing durable behind, whose handlers exist, and whose delivery is either
    /// unconditional or preflightable. Nested (triggered) spells must be transient too and may not teleport or summon.
    /// </summary>
    private bool IsTransient(SpellInfo spell, HashSet<uint> visiting, bool nested)
    {
        if (!visiting.Add(spell.Id))
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
                if (!spells.HasEffectHandler(effect.Effect) || RequiresDurableGrant(effect.Effect) || !AllowedTarget(effect))
                {
                    return false;
                }

                // Teleport and summon need the player-aware preflight, which models only the built-in
                // handlers of the two base effects; a replaced handler is unknown code.
                if (IsTeleportOrSummon(effect.Effect)
                    && (nested || effect.Effect is not (SpellEffectName.TeleportUnits or SpellEffectName.Summon)
                        || !spells.HasBuiltInEffectHandler(effect.Effect)))
                {
                    return false;
                }

                if (IsAuraApplying(effect.Effect)
                    && (!spells.HasAuraHandler(effect.AuraType) || spell.IsPassive || spell.GetDuration() <= 0))
                {
                    return false;
                }

                if ((effect.Effect is (SpellEffectName.TriggerSpell or SpellEffectName.TriggerMissile)
                        || effect.AuraType == AuraType.PeriodicTriggerSpell)
                    && (spells.Store.Get(effect.TriggerSpell) is not { } triggered || !IsTransient(triggered, visiting, nested: true)))
                {
                    return false;
                }
            }

            return hasEffect;
        }
        finally
        {
            visiting.Remove(spell.Id);
        }
    }

    /// <summary>Player-aware delivery checks for a transient reward, for the caster that would cast it.</summary>
    private bool PreflightTransient(Player player, Unit caster, SpellInfo spell)
    {
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.IsEmpty)
            {
                continue;
            }

            if (effect.Effect == SpellEffectName.TeleportUnits)
            {
                // The unit a quest reward moves is the player; a reward that would move the quest giver is refused.
                Unit target = effect.TargetA is SpellImplicitTarget.None or SpellImplicitTarget.UnitCaster ? caster : player;
                SpellCastTargets targets = ReferenceEquals(caster, player) ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(player.Guid);
                if (!ReferenceEquals(target, player)
                    || !spells.TryResolveTeleportDestination(spell, i, target, targets, out SpellTargetPosition destination)
                    || !spells.Teleports.CanTeleport(target, destination.MapId, destination.X, destination.Y, destination.Z, destination.Orientation))
                {
                    return false;
                }
            }
            else if (effect.Effect == SpellEffectName.Summon
                && (spells.Summons is not { } sink || caster.Map is null || effect.MiscValue <= 0
                    || !sink.CanSummon(caster, (uint)effect.MiscValue)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAuraApplying(SpellEffectName effect) => effect is
        SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty or SpellEffectName.ApplyAreaAuraPet
        or SpellEffectName.ApplyAreaAuraFriend or SpellEffectName.ApplyAreaAuraEnemy or SpellEffectName.ApplyAreaAuraRaid
        or SpellEffectName.ApplyAreaAuraOwner or SpellEffectName.PersistentAreaAura;

    private static bool IsTeleportOrSummon(SpellEffectName effect) => effect is
        SpellEffectName.TeleportUnits or SpellEffectName.TeleportUnitsFaceCaster or SpellEffectName.Summon
        or SpellEffectName.SummonWild or SpellEffectName.SummonGuardian or SpellEffectName.SummonPet
        or SpellEffectName.SummonPossessed or SpellEffectName.SummonTotem or SpellEffectName.SummonTotemSlot1
        or SpellEffectName.SummonTotemSlot2 or SpellEffectName.SummonTotemSlot3 or SpellEffectName.SummonTotemSlot4
        or SpellEffectName.SummonPhantasm or SpellEffectName.SummonCritter or SpellEffectName.SummonDeadPet
        or SpellEffectName.SummonDemon or SpellEffectName.SummonObjectWild or SpellEffectName.SummonObjectSlot1
        or SpellEffectName.SummonObjectSlot2 or SpellEffectName.SummonObjectSlot3 or SpellEffectName.SummonObjectSlot4
        or SpellEffectName.SummonPlayer;

    private static bool RequiresDurableGrant(SpellEffectName effect) => effect is
        SpellEffectName.LearnSpell or SpellEffectName.LearnPetSpell or SpellEffectName.CreateItem
        or SpellEffectName.SummonChangeItem
        or SpellEffectName.QuestComplete or SpellEffectName.Skill or SpellEffectName.SkillStep
        or SpellEffectName.TradeSkill or SpellEffectName.Proficiency or SpellEffectName.Language
        or SpellEffectName.DualWield or SpellEffectName.Reputation or SpellEffectName.Bind
        or SpellEffectName.EnchantItem or SpellEffectName.EnchantHeldItem
        or SpellEffectName.DurabilityDamage or SpellEffectName.DurabilityDamagePct
        or SpellEffectName.Disenchant or SpellEffectName.Pickpocket
        // Opaque scripted effects cannot prove that all their grants are transient.
        or SpellEffectName.Dummy or SpellEffectName.ScriptEffect or SpellEffectName.SendEvent;
}
