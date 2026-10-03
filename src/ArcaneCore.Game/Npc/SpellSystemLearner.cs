using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Trainer spell knowledge over the existing spell system and spellbook (vmangos
/// ObjectMgr::LoadTrainers learnedSpell, Player::IsSpellFitByClassAndRace, SpellMgr spell
/// chains, Spell::EffectLearnSpell). Rank prerequisites and race/class fit come from
/// SkillLineAbility.dbc when supplied.
/// </summary>
/// <remarks>
/// With the skill system active (<paramref name="skills"/> returns its catalog) the trainer reads the player's
/// real skills (<see cref="Player.Skills"/>), the primary-profession state is derived like vmangos
/// Player::GetTrainerSpellState, skills a SkillRaceClassInfo row marks not trainable are not offered, and
/// buying casts the teaching spell through the spell system so its effects (LEARN_SPELL, SKILL_STEP) run. Without
/// it every skill value reads 0 and no primary-profession points exist. spell_chain "req_spell" requirements
/// (talent-dependent ranks) are not modelled.
/// </remarks>
/// <param name="spells">The spell system (resolved lazily: features attach in name order).</param>
/// <param name="abilities">SkillLineAbility.dbc data (empty: no rank chain, every spell fits).</param>
/// <param name="skills">The skill catalog while the skill system is active (null: the stand-in behaviour).</param>
public sealed class SpellSystemLearner(Func<SpellSystem?> spells, SkillLineAbilityCatalog abilities, Func<SkillCatalog?>? skills = null) : ISpellLearner
{
    public TrainerSpellInfo? DescribeTrainerSpell(uint teachingSpell)
    {
        if (spells()?.Store is not { } store || store.Get(teachingSpell) is not { } teaching)
        {
            return null;
        }

        uint learned = LearnedSpells(teaching).FirstOrDefault();
        if (learned == 0 || store.Get(learned) is not { } learnedInfo)
        {
            return null;
        }

        if (skills?.Invoke() is not { } catalog)
        {
            return new TrainerSpellInfo(learned, learnedInfo.SpellLevel, abilities.PreviousRank(learned), 0, false, false, false);
        }

        // vmangos Player::GetTrainerSpellState (Player.cpp:4277-4293): a primary-profession teaching spell is one whose
        // second effect is SKILL (or that teaches a spell whose second effect is SKILL) for a primary profession skill.
        SpellEffectInfo second = teaching.Effects[1];
        bool professionLearn = second.Effect == SpellEffectName.Skill;
        bool firstRank = catalog.IsPrimaryProfessionFirstRankSpell(teaching.Id);
        foreach (SpellEffectInfo effect in teaching.Effects.Where(e => e.Effect == SpellEffectName.LearnSpell))
        {
            if (professionLearn || store.Get(effect.TriggerSpell) is not { } taught)
            {
                continue;
            }

            professionLearn |= taught.Effects[1].Effect == SpellEffectName.Skill;
            firstRank |= catalog.IsPrimaryProfessionFirstRankSpell(taught.Id);
        }

        bool primaryLearn = professionLearn && catalog.IsPrimaryProfessionSkill(unchecked((uint)second.MiscValue));
        return new TrainerSpellInfo(
            learned, learnedInfo.SpellLevel, catalog.Ranks.Previous(learned), 0,
            catalog.IsPrimaryProfessionFirstRankSpell(learned), primaryLearn, firstRank);
    }

    public bool HasSpell(Player player, uint spellId) => spells()?.Spellbook?.HasSpell(player, spellId) ?? false;

    public bool IsSpellFitByClassAndRace(Player player, uint spellId)
    {
        uint raceMask = Mask((byte)player.Race);
        uint classMask = Mask((byte)player.Class);
        if (skills?.Invoke() is not { } catalog)
        {
            return abilities.FitsClassAndRace(spellId, raceMask, classMask);
        }

        // vmangos Player::IsSpellFitByClassAndRace (Player.cpp:19522-19575), the NONTRAINABLE rule: the first ability
        // that fits the race and class decides, and a skill whose race/class row is not trainable is not taught.
        // The row's minimum level is not applied here (the reference applies it only when training, while the trainer
        // list asks the same question without a level; the spell level gate stays in the trainer service).
        IReadOnlyList<SkillLineAbilityRecord> list = catalog.AbilitiesOfSpell(spellId);
        if (list.Count == 0)
        {
            return true;
        }

        foreach (SkillLineAbilityRecord ability in list)
        {
            if ((ability.RaceMask != 0 && (ability.RaceMask & raceMask) == 0) || (ability.ClassMask != 0 && (ability.ClassMask & classMask) == 0))
            {
                continue;
            }

            foreach (SkillRaceClassInfoRecord row in catalog.RaceClassInfos(ability.SkillId))
            {
                if ((row.RaceMask & raceMask) != 0 && (row.ClassMask & classMask) != 0 && (row.Flags & SkillRaceClassFlags.NotTrainable) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    public uint GetSkillValueBase(Player player, uint skill) => player.Skills?.GetValueBase(skill) ?? 0;

    public uint GetSkillValue(Player player, uint skill) => player.Skills?.GetValue(skill) ?? 0;

    public uint GetFreePrimaryProfessionPoints(Player player) => player.Skills?.FreePrimaryProfessionPoints ?? 0;

    /// <summary>
    /// vmangos HandleTrainerBuySpellOpcode: a teaching spell with SPELL_EFFECT_LEARN_SPELL teaches
    /// its trigger spells (Spell::EffectLearnSpell), any other is learned itself.
    /// </summary>
    public bool CastTeachingSpell(Player player, ObjectGuid trainer, uint teachingSpell)
    {
        if (spells() is not { } system || system.Store.Get(teachingSpell) is not { } teaching)
        {
            return false;
        }

        // vmangos casts the teaching spell (HandleTrainerBuySpellOpcode, NPCHandler.cpp:321-333) so every effect runs
        // (LEARN_SPELL teaches, SKILL_STEP sets the profession up). The caster is the player here, not the trainer
        // unit (the cast visual only); the money is charged only when the cast succeeded.
        if (skills?.Invoke() is not null && player.Skills is not null)
        {
            return system.CastSpell(player, teachingSpell, SpellCastTargets.ForSelf(), triggered: true) == SpellCastResult.CastOk;
        }

        bool learnedAny = false;
        foreach (uint spell in LearnedSpells(teaching))
        {
            learnedAny |= system.LearnSpell(player, spell);
        }

        return learnedAny;
    }

    public void CastQuestRewardSpell(Player player, ObjectGuid questEnder, uint spellId)
        => spells()?.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);

    private static IEnumerable<uint> LearnedSpells(SpellInfo teaching)
    {
        uint[] learned = teaching.Effects
            .Where(e => e.Effect == SpellEffectName.LearnSpell && e.TriggerSpell != 0)
            .Select(e => e.TriggerSpell)
            .ToArray();
        return learned.Length > 0 ? learned : [teaching.Id];
    }

    private static uint Mask(byte id) => id is > 0 and <= 32 ? 1u << (id - 1) : 0;
}
